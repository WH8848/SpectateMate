using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using SpectateMate.Ui;
using STS2RitsuLib.Patching.Models;

namespace SpectateMate.Patches;

/// <summary>
/// Captures NGlobalUi/RunState. Deliberately does NOT redirect the local player:
/// NGlobalUi.Initialize chains into TopBar.Initialize -> Portrait.Initialize,
/// which moves player.Character.Icon into the portrait frame. Redirecting there
/// made a rejoining player's own top-bar portrait render the teammate.
/// </summary>
internal sealed class GlobalUiInitializePatch : IPatchMethod
{
    public static string PatchId => "spectatemate_global_ui_init";

    public static string Description => "Capture NGlobalUi/RunState";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
    [
        PatchTarget.Method<NGlobalUi>(nameof(NGlobalUi.Initialize), typeof(RunState)),
    ];

    public static void Postfix(NGlobalUi __instance, RunState runState)
    {
        // A new run invalidates whatever we were spectating before.
        DisplayedPlayer.SetTarget(null);
        SpectateUi.CaptureGlobalUi(__instance, runState);
    }
}

/// <summary>
/// Combat widgets (energy orb / star counter / piles) are built for a specific
/// player during Activate, so this one DOES redirect — it is what makes A1 work.
/// </summary>
internal sealed class CombatUiActivatePatch : IPatchMethod
{
    public static string PatchId => "spectatemate_combat_ui_activate";

    public static string Description => "Energy orb / star counter / combat piles render the teammate";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
    [
        PatchTarget.Method<NCombatUi>(nameof(NCombatUi.Activate), typeof(CombatState)),
    ];

    public static void Prefix(out bool __state)
    {
        __state = DisplayedPlayer.Push();
    }

    public static void Postfix(NCombatUi __instance)
    {
        SpectateUi.CaptureCombatUi(__instance);

        // 战斗 UI 刚建好，手牌容器可能带着上一局残留的透明状态。
        // 这里无条件重置，不依赖 Hide() 是否成功执行过。
        // 延后一帧：Activate 阶段 NPlayerHand 可能还没挂到 NCombatUi 上，
        // 立即执行会找不到容器而直接返回。
        Callable.From(VanillaHandVisibility.Reset).CallDeferred();

        // 观战可能在战斗外就打开了，这里补一次输入屏蔽。
        if (DisplayedPlayer.IsActive)
        {
            SpectateUi.SetInteraction(false);
        }

        // 新战斗的牌堆按钮和能量球都是新节点，必须重新绑一次。
        // 观战结束在战斗外时，Apply(null) 会因为旧 NCombatUi 已销毁而跳过重绑，
        // 不在这里补一刀，按钮就会一直指着上一场的牌堆。
        CombatWidgetRebind.ResetExhaustVisibilityCache();
        SpectateUi.ForceReapply();
        Callable.From(() => SpectateUi.Apply(SpectateOverlay.Target)).CallDeferred();
    }

    public static void Finalizer(bool __state)
    {
        DisplayedPlayer.Pop(__state);
    }
}
