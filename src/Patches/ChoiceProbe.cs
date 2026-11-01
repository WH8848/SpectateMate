using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 抓取被观战者正在进行的选牌候选。
///
/// 选牌逻辑在所有客户端都会执行（确定性锁步），候选来自方法参数，因此本地
/// 手里就有；只是非 owner 分支不弹界面，而是直接等远端结果。
///
/// 目前覆盖两条路径：
/// * FromChooseACardScreen —— 从 N 张里挑一张（发现、飞溅）
/// * FromCombatPile(5 参) —— 从抽牌堆/弃牌堆选（冲锋、涅奥之怒）
///
/// CardSelectCmd 是 static class，不能作为 RitsuLib PatchTarget.Method&lt;T&gt;
/// 的类型实参，所以直接用 Harmony。
/// </summary>
internal static class ChoiceProbe
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.choice");

            MethodInfo? choose = AccessTools.Method(
                typeof(CardSelectCmd),
                "FromChooseACardScreen",
                new[]
                {
                    typeof(PlayerChoiceContext),
                    typeof(IReadOnlyList<CardModel>),
                    typeof(Player),
                    typeof(bool),
                });

            if (choose != null)
            {
                _harmony.Patch(choose, prefix: new HarmonyMethod(typeof(ChoiceProbe), nameof(ChoosePrefix)));
                ModEntry.Log.Info($"[{ModEntry.ModId}] choice probe: FromChooseACardScreen hooked");
            }
            else
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] choice probe: FromChooseACardScreen not found");
            }

            MethodInfo? pile = AccessTools.Method(
                typeof(CardSelectCmd),
                "FromCombatPile",
                new[]
                {
                    typeof(PlayerChoiceContext),
                    typeof(CardPile),
                    typeof(Player),
                    typeof(CardSelectorPrefs),
                    typeof(Func<CardModel, bool>),
                });

            if (pile != null)
            {
                _harmony.Patch(pile, prefix: new HarmonyMethod(typeof(ChoiceProbe), nameof(PilePrefix)));
                ModEntry.Log.Info($"[{ModEntry.ModId}] choice probe: FromCombatPile hooked");
            }
            else
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] choice probe: FromCombatPile not found");
            }

            // 弃牌 / 烧牌 / 手牌升级都汇聚到这里（FromHandForDiscard 只是转调）。
            // 它不弹覆盖层界面，而是让手牌区进入选择模式，所以只能镜像手牌的可见性。
            MethodInfo? hand = AccessTools.Method(
                typeof(CardSelectCmd),
                "FromHand",
                new[]
                {
                    typeof(PlayerChoiceContext),
                    typeof(Player),
                    typeof(CardSelectorPrefs),
                    typeof(Func<CardModel, bool>),
                    typeof(AbstractModel),
                });

            if (hand != null)
            {
                _harmony.Patch(hand, prefix: new HarmonyMethod(typeof(ChoiceProbe), nameof(HandPrefix)));
                ModEntry.Log.Info($"[{ModEntry.ModId}] choice probe: FromHand hooked");
            }
            else
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] choice probe: FromHand not found");
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice probe install failed: {e.Message}");
        }
    }

    private static void HandPrefix(
        PlayerChoiceContext context,
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel? source)
    {
        SpectateChoice.BeginHandSelection(player, prefs, filter);
    }

    private static void ChoosePrefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards, Player player, bool canSkip)
    {
        SpectateChoice.RememberChooseACard(cards, canSkip, player);
    }

    private static void PilePrefix(
        PlayerChoiceContext context,
        CardPile pile,
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter)
    {
        SpectateChoice.RememberCombatPile(pile, prefs, filter, player);
    }
}
