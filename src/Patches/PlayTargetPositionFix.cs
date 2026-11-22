using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 观战时打出牌的落点修正。整个文件只做两件事，都直接写位置，不依赖时间、不依赖补间存活。
///
/// 背景：原版算落点用
///     PlayContainer.Size * 0.5f - node.Size * 0.5f + Vector2.Up * 100f
/// 其中 - node.Size * 0.5f 把「容器中心」换算成「卡片左上角」。但 NCard.UpdateVisuals
/// 开头有 if (!IsNodeReady()) return;，而原版创建卡片是「先 AddChild 再 UpdateVisuals」，
/// 所以 Size 经常还是 (0,0)，这一项等于没减，落点在 (960,440) 与 (810,229) 之间跳变。
/// 观战画面里正确的是 (960,440)。
///
/// 这里只保留两处介入：
///   1. GetTargetPosition 的 postfix —— 让所有调用方（入场补间、飞行特效、消耗特效）
///      拿到的都是同一个正确值；
///   2. AppendPlayPileLerpTween 的 prefix —— 补间开始前先把卡片摆到落点，
///      于是补间起点等于终点，不会从队列位置滑过来，也就不会出现「停在半路」。
///
/// 曾经有过、现已删除的做法（都不可靠）：
///   - 给 PlayPileTween 挂 TweenMethod 每帧写位置：补间被 Kill 时它一起死，
///     卡片就停在当时算出的坐标上——这正是「落点不稳定」的来源；
///   - transpiler 改写 BeforeActionPausedForPlayerChoice 的 IL：游戏一更新方法体
///     就会在运行时抛异常，影响的是游戏本身的选牌暂停逻辑；
///   - 按 0.25 / 0.2 秒的硬编码时长做延迟校正：FastMode 下原版补间只有 0.01~0.1 秒；
///   - 进入观战时批量扫描播放区、把「看起来停在原版落点」的牌搬到中心：
///     判断依据是位置约等于某个值，而 Size 为 0 时这个判断会退化成恒真。
/// </summary>
internal static class PlayTargetPositionFix
{
    public static void Install()
    {
        try
        {
            var harmony = new Harmony($"{ModEntry.ModId}.playtarget");

            // 逐个独立判断：任一处目标缺失都不能影响另一处。
            var targetPosition = AccessTools.DeclaredMethod(
                typeof(PileTypeExtensions),
                nameof(PileTypeExtensions.GetTargetPosition));

            if (targetPosition != null)
            {
                harmony.Patch(
                    targetPosition,
                    postfix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(AdjustTargetPosition)));

                ModEntry.Log.Info($"[{ModEntry.ModId}] play target: GetTargetPosition hooked");
            }
            else
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] play target: GetTargetPosition not found");
            }

            var playTween = AccessTools.DeclaredMethod(typeof(CardPileCmd), "AppendPlayPileLerpTween");

            if (playTween != null)
            {
                harmony.Patch(
                    playTween,
                    prefix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(PlaceBeforePlayTween)));

                ModEntry.Log.Info($"[{ModEntry.ModId}] play target: AppendPlayPileLerpTween hooked");
            }
            else
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] play target: AppendPlayPileLerpTween not found");
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] play target install failed: {e.Message}");
        }
    }

    /// <summary>
    /// 是否正在观战某个玩家打出的牌。
    ///
    /// 判据只用 SpectateOverlay —— 绝不碰 LocalContext。后者会被我们自己
    /// 在 NCombatUi.Activate 的作用域里重定向，任何依赖它的判断都会随时机变化。
    /// </summary>
    private static bool IsSpectatedCard(CardModel? model) =>
        SpectateOverlay.IsOpen &&
        SpectateOverlay.Target is { } target &&
        model is { Owner: { } owner } &&
        owner.NetId == target.NetId;

    /// <summary>
    /// 落点。不依赖 card.Size，只用播放区尺寸。
    ///
    /// 原版公式减去半张卡是为了把中心换算成左上角。观战时卡片节点往往刚出池、
    /// Size 还是 (0,0)，减去的那一项实际为零，落点落在 (960,440)。
    /// 实测这个位置才是画面上正确的那个，所以固定按 Size 为零计算。
    /// </summary>
    private static Vector2 LandingPoint(Control play) => play.Size * 0.5f + Vector2.Up * 100f;

    private static void AdjustTargetPosition(PileType pileType, NCard? node, ref Vector2 __result)
    {
        if (pileType != PileType.Play || !SpectateOverlay.IsOpen)
        {
            return;
        }

        if (node?.Model is not { } model || !IsSpectatedCard(model))
        {
            return;
        }

        if (NCombatRoom.Instance?.Ui?.PlayContainer is not { } play)
        {
            return;
        }

        __result = LandingPoint(play);
    }

    /// <summary>
    /// 入场补间开始前，先把卡片摆到落点。
    ///
    /// 补间的目标由 GetTargetPosition 给出（上面的 postfix 已经改成正确值），
    /// 起点取的是卡片当前 position。把起点也设成落点，两者相等，
    /// 卡片就直接出现在中央，而不是从播放队列的位置滑过来。
    /// </summary>
    private static void PlaceBeforePlayTween(NCard cardNode)
    {
        try
        {
            if (cardNode?.Model is not { } model || !IsSpectatedCard(model))
            {
                return;
            }

            if (NCombatRoom.Instance?.Ui?.PlayContainer is not { } play)
            {
                return;
            }

            // 只处理已经位于播放区的卡片：此时 position 是相对 PlayContainer 的局部坐标。
            if (cardNode.GetParent() != play)
            {
                return;
            }

            cardNode.Position = LandingPoint(play);
        }
        catch
        {
            // ignored
        }
    }
}
