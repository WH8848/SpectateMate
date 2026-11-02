using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 观战期间接管手牌的「启用/禁用」状态。
///
/// NPlayerHand.UpdateHandDisabledState 在战斗状态每次变化时都会跑，判断依据是
/// LocalContext.GetMe()——也就是你自己。观战时屏幕上显示的是队友的手牌，
/// 但这个方法仍然拿你的状态去决定要不要 AnimDisable：
///
///     bool flag3 = state.CurrentSide == CombatSide.Enemy;
///     if (flag3 || flag2) { AnimDisable(); }
///
/// 结果就是他一打牌（战斗状态变化），你这边整排手牌就下移变暗。
///
/// 所以观战期间直接跳过原版，改由 SpectateUi.SyncHandEnabled 按被观战者的
/// 回合阶段驱动，并带连续确认以避免过渡态抖动。
/// </summary>
internal static class HandStatePatch
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.handstate");

            var target = AccessTools.Method(typeof(NPlayerHand), "UpdateHandDisabledState");
            if (target == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] hand state patch: UpdateHandDisabledState not found");
                return;
            }

            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(HandStatePatch), nameof(Prefix)));
            ModEntry.Log.Info($"[{ModEntry.ModId}] hand state patch: UpdateHandDisabledState suppressed while spectating");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] hand state patch failed: {e.Message}");
        }
    }

    private static bool Prefix()
    {
        // 观战时跳过原版；退出观战后立刻恢复正常。
        return !SpectateOverlay.IsOpen;
    }
}
