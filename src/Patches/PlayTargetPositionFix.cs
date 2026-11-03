using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 修复：播放区落点算错，卡片偏右下一百多像素。
///
/// 原版：

///     public static Vector2 GetTargetPosition(this PileType pileType, NCard? node)
///     {
///         return pileType switch
///         {
///             PileType.Play => NCombatRoom.Instance.Ui.PlayContainer.Size * 0.5f
///                            - node.Size * 0.5f
///                            + Vector2.Up * 100f,
///             ...
///         };
///     }
///
/// 其中 - node.Size * 0.5f 把「容器中心」换算成「卡片左上角」。
///
/// 问题在于 node.Size 并不可靠。原版新建卡片的顺序是：

///     nCard = NCard.Create(card);            // 从池里取出，尚未入树
///     Ui.AddChildSafely(nCard);              // 入树，_Ready 要下一帧才跑
///     nCard.UpdateVisuals(pileType, ...);    // IsNodeReady() 为 false，直接 return
///
/// UpdateVisuals 开头就有

///     if (!IsNodeReady()) return;

/// 于是卡片布局从未完成，Size 停在 (0,0)，- node.Size*0.5 这一项等于没减，
/// 落点从 (810,229) 变成 (960,440)——偏右下约 (150,211)。
///
/// 这解释了观战时「同一张牌有时停在正中、有时偏右下」：取决于那一刻
/// Size 有没有被别的路径补上。
///
/// 修复：卡片尺寸是固定的（NCard.defaultSize = 300x422），
/// 用它代替 node.Size。只在观战时介入，正常游玩保持原样。
/// </summary>
internal static class PlayTargetPositionFix
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.playtarget");

            MethodInfo? target = AccessTools.Method(
                typeof(PileTypeExtensions),
                "GetTargetPosition",
                new[] { typeof(PileType), typeof(NCard) });

            if (target == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] play target fix: hook target missing");
                return;
            }

            _harmony.Patch(target, postfix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(Postfix)));
            ModEntry.Log.Info($"[{ModEntry.ModId}] play target fix: hooked");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] play target fix failed: {e.Message}");
        }
    }

    private static void Postfix(PileType pileType, ref Vector2 __result)
    {
        try
        {
            if (pileType != PileType.Play || !SpectateOverlay.IsOpen)
            {
                return;
            }

            Control? play = NCombatRoom.Instance?.Ui?.PlayContainer;

            if (play == null || play.Size.X < 1f || play.Size.Y < 1f)
            {
                return;
            }

            __result = play.Size * 0.5f
                     - NCard.defaultSize * 0.5f
                     + new Vector2(0f, -100f);
        }
        catch
        {
            // ignored
        }
    }
}
