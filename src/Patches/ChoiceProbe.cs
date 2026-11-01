using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 抓取被观战者正在进行的选牌候选。
///
/// 选牌逻辑在所有客户端都会执行（确定性锁步），候选列表 cards 是
/// FromChooseACardScreen 的参数，因此本地手里就有；只是非 owner 分支
/// 不会弹界面，而是直接等远端结果。
///
/// CardSelectCmd 是 static class，不能作为 RitsuLib PatchTarget.Method&lt;T&gt;
/// 的类型实参，所以这里直接用 Harmony。
/// </summary>
internal static class ChoiceProbe
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            MethodInfo? target = AccessTools.Method(
                typeof(CardSelectCmd),
                "FromChooseACardScreen",
                new[]
                {
                    typeof(PlayerChoiceContext),
                    typeof(IReadOnlyList<CardModel>),
                    typeof(Player),
                    typeof(bool),
                });

            if (target == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] FromChooseACardScreen not found; choice peek disabled");
                return;
            }

            _harmony = new Harmony($"{ModEntry.ModId}.choice");
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ChoiceProbe), nameof(Prefix)));
            ModEntry.Log.Info($"[{ModEntry.ModId}] choice probe installed");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice probe install failed: {e.Message}");
        }
    }

    private static void Prefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards, Player player, bool canSkip)
    {
        SpectateChoice.Remember(cards, canSkip, player);
    }
}
