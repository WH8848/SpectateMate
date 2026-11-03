using System;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpectateMate.Ui;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using System.Reflection;

namespace SpectateMate.Patches;

/// <summary>
/// 诊断：出牌飞到播放区时，容器的状态到底是什么。
///
/// 原版 AppendPileLerpTween 里：
///     Vector2 targetPosition = typePile.GetTargetPosition(cardNode);
///     tween.TweenProperty(cardNode, "position", targetPosition, num);
///
/// 而 PileType.Play 的公式是：
///     PlayContainer.Size * 0.5f - node.Size * 0.5f + Vector2.Up * 100f
///
/// 这个值被当作「相对 PlayContainer 的局部坐标」。如果布局还没完成、
/// PlayContainer.Size 仍是 (0,0)，算出来就贴近 (0,-100)，也就是左上角。
///
/// 观战时 NCombatUi 是现构建的，布局完成时间可能比正常对局晚，
/// 这里把两张卡（加入播放区时、以及补间目标算出时）的状态都记下来。
/// </summary>
internal static class PlayContainerProbe
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.playprobe");

            var add = AccessTools.Method(typeof(NCombatUi), "AddToPlayContainer");
            if (add != null)
            {
                _harmony.Patch(add, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(AddPrefix)));
            }

            var lerp = AccessTools.Method(
                typeof(CardPileCmd),
                "AppendPileLerpTween",
                new[] { typeof(Tween), typeof(NCard), typeof(PileType), typeof(PileType?) });

            if (lerp != null)
            {
                _harmony.Patch(lerp, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(LerpPrefix)));
            }

            // NCardFlyVfx 的两个 Create 重载：起点都来自 card.GlobalPosition，
            // 这里把它、局部位置和父节点一起记下来。
            var flyPile = AccessTools.Method(
                typeof(NCardFlyVfx),
                "Create",
                new[] { typeof(NCard), typeof(PileType), typeof(bool), typeof(string) });

            if (flyPile != null)
            {
                _harmony.Patch(flyPile, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(FlyPrefix)));
            }

            var flyCreature = AccessTools.Method(
                typeof(NCardFlyVfx),
                "Create",
                new[] { typeof(NCard), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), typeof(string) });

            if (flyCreature != null)
            {
                _harmony.Patch(flyCreature, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(FlyPrefix)));
            }

            // 其余卡牌特效：它们的 Create 也都以 NCard 为首参，
            // 统统一遍，看是谁在播放区那类位置把卡片拿走的。
            var vfxTypes = new[]
            {
                typeof(NCardTransformVfx),
                typeof(NCardFlyPowerVfx),
                typeof(NCardExhaustVfx),
                typeof(NCardExhaustQuickVfx),
                typeof(NCardRemoveVfx),
                typeof(NCardUpgradeVfx),
            };

            int vfxHooked = 0;

            foreach (Type t in vfxTypes)
            {
                foreach (System.Reflection.MethodInfo m in t.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "Create")
                    {
                        continue;
                    }

                    ParameterInfo[] ps = m.GetParameters();

                    if (ps.Length == 0 || ps[0].ParameterType != typeof(NCard))
                    {
                        continue;
                    }

                    try
                    {
                        _harmony.Patch(m, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(VfxPrefix)));
                        vfxHooked++;
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            ModEntry.Log.Info($"[{ModEntry.ModId}] vfx probe: hooked {vfxHooked}");

            // 远程玩家卡片的起点由 CreateCardNodeAndUpdateVisuals 决定：
            //     nCard.Position = GetCreatureNode(card.Owner.Creature).IntentContainer.GlobalPosition;
            // 意图栏尚未布局时该值可能是 (0,0)，卡片就从左上角出发。
            var createNode = AccessTools.Method(
                typeof(CardPileCmd),
                "CreateCardNodeAndUpdateVisuals",
                new[] { typeof(CardModel), typeof(PileType?), typeof(PileType), typeof(bool) });

            if (createNode != null)
            {
                _harmony.Patch(
                    createNode,
                    prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(CreateNodePrefix)),
                    postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(CreateNodePostfix)));

                ModEntry.Log.Info($"[{ModEntry.ModId}] create-node probe: hooked");
            }

            // 所有出牌路径（手动 / 自动 / FindOnTable 命中）都会经过这里，
            // 是唯一能兜住「卡片从哪来」的收口点。
            var moveNode = AccessTools.Method(
                typeof(CardPileCmd),
                "MoveCardNodeToNewPileBeforeTween",
                new[] { typeof(NCard), typeof(PileType) });

            if (moveNode != null)
            {
                _harmony.Patch(
                    moveNode,
                    prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(MoveNodePrefix)),
                    postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(MoveNodePostfix)));

                ModEntry.Log.Info($"[{ModEntry.ModId}] move-node probe: hooked");
            }

            var addPost = AccessTools.Method(typeof(NCombatUi), "AddToPlayContainer");

            if (addPost != null)
            {
                _harmony.Patch(addPost, postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(AddPostfix)));
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play probe: add={add != null} lerp={lerp != null}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] play probe failed: {e.Message}");
        }
    }

    private static ulong _lastScanMs;

    /// <summary>
    /// 每 3 秒把屏幕上的每一张 NCard 的位置扫一遍。
    ///
    /// 手牌有 STUCK 检测、出牌有 play settle 探针，两边都显示位置正常，
    /// 但画面上仍有一张牌停在左上角——说明它走的是这两条之外的路。
    /// 直接全量扫描，找出所有贴在 (0,0) 附近的卡，以及它们的父节点。
    /// </summary>
    public static void ScanCards()
    {
        try
        {
            // 不检查 IsOpen：这个现象在未观战时也会出现，
            // 只在观战期间扫就会永远看不到它。
            ulong now = Godot.Time.GetTicksMsec();

            if (now - _lastScanMs < 3000)
            {
                return;
            }

            _lastScanMs = now;

            Node? ui = NCombatRoom.Instance?.Ui;

            if (ui == null)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            int count = 0;

            foreach (Node node in ui.FindChildren("*", string.Empty, true, false))
            {
                if (node is not NCard card)
                {
                    continue;
                }

                count++;

                Vector2 g = card.GlobalPosition;

                sb.Append(card.Model?.Id.Entry ?? "?")
                    .Append("@(").Append((int)g.X).Append(',').Append((int)g.Y).Append(')')
                    .Append('/').Append(card.GetParent()?.Name ?? "?")
                    .Append(card.Visible ? "" : "(hidden)")
                    .Append(' ');
            }

            ModEntry.Log.Info($"[{ModEntry.ModId}] card scan({count}): {sb}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 飞行特效的起点等于 card.GlobalPosition。
    /// 若此刻卡片已经在屏幕中上部，光带就从那里窜出来，看着像「从左上角飞」。
    /// </summary>
    /// <summary>
    /// 通用：任何以 NCard 为首参的特效 Create，记录卡片此刻的位置。
    /// </summary>
    private static void MoveNodePrefix(NCard cardNode, PileType newPileType, ref Vector2 __state)
    {
        __state = cardNode?.GlobalPosition ?? Vector2.Zero;

        try
        {
            if (newPileType != PileType.Play || !SpectateOverlay.IsOpen)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] move-node: {cardNode?.Model?.Id.Entry} " +
                $"from={__state} local={cardNode?.Position} " +
                $"parent={cardNode?.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 换堆把卡片移进 PlayContainer 之后，如果它仍贴在原点，
    /// 说明起点本来就没算出来。此时直接换到屏幕中部偏下，
    /// 后面 AppendPlayPileLerpTween 就会从正确的位置起步。
    /// </summary>
    private static void MoveNodePostfix(NCard cardNode, PileType newPileType, Vector2 __state)
    {
        try
        {
            if (cardNode == null || !GodotObject.IsInstanceValid(cardNode) || newPileType != PileType.Play)
            {
                return;
            }

            Vector2 g = cardNode.GlobalPosition;

            if (g.Length() > 48f)
            {
                return;
            }

            Vector2 size = NGame.Instance?.GetViewportRect().Size ?? new Vector2(1920f, 1080f);
            Vector2 fallback = new(size.X * 0.5f, size.Y * 0.62f);
            cardNode.GlobalPosition = fallback;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] move-node FIXED: {cardNode.Model?.Id.Entry} " +
                $"saved={__state} now={g} -> {fallback}");
        }
        catch
        {
            // ignored
        }
    }

    private static void CreateNodePrefix(CardModel card, bool owningPlayerIsLocal, ref Vector2 __state)
    {
        __state = Vector2.Zero;

        try
        {
            if (owningPlayerIsLocal)
            {
                return;
            }

            Node? creature = NCombatRoom.Instance?.GetCreatureNode(card.Owner?.Creature!);
            Control? intent = creature is NCreature nc ? nc.IntentContainer : null;
            __state = intent?.GlobalPosition ?? Vector2.Zero;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] create-node: {card.Id.Entry} local={owningPlayerIsLocal} " +
                $"creature={(creature != null)} intent={(__state)} intentSize={intent?.Size}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] create-node prefix failed: {e.Message}");
        }
    }

    /// <summary>
    /// 兜底：起点落在原点附近说明意图栏还没布局，卡片会从左上角起飞。
    /// 这种情况下改用屏幕中部偏下作为起点——它至少是玩家能看懂的「从角色那边来」的方向。
    /// </summary>
    private static void CreateNodePostfix(NCard __result, bool owningPlayerIsLocal, Vector2 __state)
    {
        try
        {
            if (__result == null || owningPlayerIsLocal)
            {
                return;
            }

            Vector2 g = __result.GlobalPosition;

            if (g.Length() > 32f)
            {
                return;
            }

            Vector2 size = NGame.Instance?.GetViewportRect().Size ?? new Vector2(1920f, 1080f);
            Vector2 fallback = new(size.X * 0.5f, size.Y * 0.62f);
            __result.GlobalPosition = fallback;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] create-node FIXED: {__result.Model?.Id.Entry} " +
                $"{g} -> {fallback} (intent={__state})");
        }
        catch
        {
            // ignored
        }
    }

    private static void VfxPrefix(object[] __args, System.Reflection.MethodBase __originalMethod)
    {
        try
        {
            if (!SpectateOverlay.IsOpen || __args.Length == 0 || __args[0] is not NCard card)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] vfx {__originalMethod.DeclaringType?.Name}.Create: " +
                $"{card.Model?.Id.Entry} global={card.GlobalPosition} local={card.Position} " +
                $"size={card.Size} scale={card.Scale} " +
                $"parent={card.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    private static void FlyPrefix(NCard card)
    {
        try
        {
            if (!SpectateOverlay.IsOpen)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] fly create: {card.Model?.Id.Entry} " +
                $"global={card.GlobalPosition} local={card.Position} size={card.Size} " +
                $"parent={card.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    private static void AddPrefix(NCombatUi __instance, NCard card)
    {
        try
        {
            Control? pc = __instance.PlayContainer;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] add-to-play: {card.Model?.Id.Entry} " +
                $"pc(size={pc?.Size} pos={pc?.GlobalPosition} scale={pc?.Scale}) " +
                $"card(local={card.Position} global={card.GlobalPosition} size={card.Size}) " +
                $"parent={card.GetParent()?.Name}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// reparent 之后立刻记一次，再过 0.35 秒（补间时长 0.25）记一次，
    /// 用来判断补间到底有没有把卡片带到位。
    /// </summary>
    /// <summary>
    /// 强制校正：卡片进入播放区后，若最终仍停在原点附近，就把它拉到播放区中央。
    ///
    /// 原版补间从「意图栏位置」补到「播放区中心」。当意图栏尚未布局、
    /// 起点被算成 (0,0) 时，整条弧线都从原点出发；一旦补间被打断
    /// （reparent、场景切换、快速切换观战目标），卡片就永久留在原点。
    ///
    /// 这里在补间时长（0.25s）之后复核一次位置：只要还在原点附近，
    /// 就直接按播放区的全局变换算出正确落点。
    /// </summary>
    private static void CorrectPlayPosition(NCard card)
    {
        try
        {
            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                return;
            }

            Vector2 g = card.GlobalPosition;

            if (g.Length() > 48f)
            {
                return;
            }

            NCombatUi? ui = NCombatRoom.Instance?.Ui;

            if (ui?.PlayContainer is not { } pc)
            {
                return;
            }

            Vector2 local = PileType.Play.GetTargetPosition(card);
            Vector2 target = pc.GetGlobalTransform() * local;
            card.GlobalPosition = target;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play position CORRECTED: {card.Model?.Id.Entry} " +
                $"{g} -> {target}");
        }
        catch
        {
            // ignored
        }
    }

    private static void AddPostfix(NCombatUi __instance, NCard card)
    {
        try
        {
            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] add-to-play after: {card.Model?.Id.Entry} " +
                $"pos={card.Position} global={card.GlobalPosition}");

            // 补间 0.25 秒，这里在 0.4 秒后复核一次最终落点。
            SceneTree? correctTree = card.GetTree();

            if (correctTree != null)
            {
                SceneTreeTimer correctTimer = correctTree.CreateTimer(0.4);
                correctTimer.Timeout += () => CorrectPlayPosition(card);
            }

            SceneTree? tree = card.GetTree();

            if (tree == null)
            {
                return;
            }

            SceneTreeTimer timer = tree.CreateTimer(0.35);
            string name = card.Model?.Id.Entry ?? "?";
            timer.Timeout += () =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(card))
                    {
                        ModEntry.Log.Info(
                            $"[{ModEntry.ModId}] play settle: {name} card gone");
                        return;
                    }

                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] play settle: {name} pos={card.Position} " +
                        $"global={card.GlobalPosition} scale={card.Scale} " +
                        $"parent={card.GetParent()?.Name}");
                }
                catch
                {
                    // ignored
                }
            };
        }
        catch
        {
            // ignored
        }
    }

    private static void LerpPrefix(NCard cardNode, PileType typePile)
    {
        try
        {
            if (typePile != PileType.Play)
            {
                return;
            }

            Control? pc = NCombatRoom.Instance?.Ui?.PlayContainer;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play lerp: card={cardNode.Model?.Id.Entry} " +
                $"target={typePile.GetTargetPosition(cardNode)} " +
                $"pc(size={pc?.Size} pos={pc?.GlobalPosition}) " +
                $"cardSize={cardNode.Size}");
        }
        catch
        {
            // ignored
        }
    }
}
