using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SpectateMate.Patches;

/// <summary>
/// 修复：战斗结束时打出的牌，卡片节点永远留在屏幕上。
///
/// 原版 AddDuringManualCardPlay 开头就有：

///     if (CombatManager.Instance.IsOverOrEnding) return;
///
/// 提前返回意味着：
///   - 模型没有进入 PileType.Play 堆
///   - 节点没有经过 MoveCardNodeToNewPileBeforeTween，也就没有补间
///
/// 而 OnPlayWrapper 结尾的去向处理带着守卫：
///
///     CardPile? pile = Pile;
///     if (pile != null && pile.Type == PileType.Play)
///     {
///         switch (resultLocation.pileType) { ... Add / Exhaust / RemoveFromCombat ... }
///     }
///
/// 模型不在 Play 堆，这一整段就被跳过。OnPlay 的伤害与效果照常结算，
/// 但没有人负责把卡片节点移走——它就停在屏幕左边不动了。
///
/// 这里补上：Prefix 捕获这种「注定不会被移走」的出牌，
/// 下一帧把对应节点回收。
/// </summary>
internal static class OrphanPlayFix
{
    private static readonly List<CardModel> Pending = new();
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.orphanplay");

            var add = AccessTools.Method(
                typeof(CardPileCmd),
                "AddDuringManualCardPlay",
                new[] { typeof(CardModel) });

            if (add == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] orphan play: hook target missing");
                return;
            }

            _harmony.Patch(add, prefix: new HarmonyMethod(typeof(OrphanPlayFix), nameof(Prefix)));
            ModEntry.Log.Info($"[{ModEntry.ModId}] orphan play: hooked");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] orphan play install failed: {e.Message}");
        }
    }

    private static void Prefix(CardModel card)
    {
        try
        {
            if (!CombatManager.Instance.IsOverOrEnding)
            {
                return;
            }

            Pending.Add(card);

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] orphan play: {card.Id.Entry} " +
                $"(combat over/ending, node will not be moved)");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>每拍调用，把上一步记下的卡片节点回收掉。</summary>
    public static void Tick()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        foreach (CardModel model in Pending)
        {
            try
            {
                NCard? node = NCard.FindOnTable(model);

                if (node == null || !GodotObject.IsInstanceValid(node))
                {
                    continue;
                }

                ModEntry.Log.Info(
                    $"[{ModEntry.ModId}] orphan play: freeing {model.Id.Entry} " +
                    $"at {node.GlobalPosition}");

                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }
            catch (Exception e)
            {
                ModEntry.Log.Warn(
                    $"[{ModEntry.ModId}] orphan play: free failed for {model.Id.Entry}: {e.Message}");
            }
        }

        Pending.Clear();
    }
}
