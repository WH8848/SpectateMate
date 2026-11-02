using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using SpectateMate.Core;
using SpectateMate.Patches;
using SpectateMate.Ui;
using STS2RitsuLib;
using STS2RitsuLib.Patching.Core;

namespace SpectateMate;

/// <summary>
/// RitsuLib-backed entry point. RitsuLib owns the patcher lifecycle, logging and
/// the framework events we use to tear the overlay down at the right time.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "SpectateMate";

    public static Logger Log { get; private set; } = null!;

    public static void Initialize()
    {
        Log = RitsuLibFramework.CreateLogger(ModId);
        Log.Info($"[{ModId}] init v{typeof(ModEntry).Assembly.GetName().Version}");

        SpectateSettings.Initialize();

        try
        {
            var patcher = RitsuLibFramework.CreatePatcher(ModId, "avatar", "teammate avatar right-click");
            patcher.RegisterPatch<AvatarRightClickPatch>();
            patcher.RegisterPatch<GlobalUiInitializePatch>();
            patcher.RegisterPatch<CombatUiActivatePatch>();
            patcher.RegisterPatch<HandPlayBlockPatch>();
            patcher.RegisterPatch<AvatarOrderPatch>();
            patcher.RegisterPatch<ActiveHoldersPatch>();

            // CardSelectCmd 是 static class，无法作为 RitsuLib 的泛型补丁目标，改用 Harmony。
            ChoiceProbe.Install();
            HandStatePatch.Install();
            ShuffleProbe.Install();
            RitsuLibFramework.ApplyRequiredPatcher(patcher, OnRequiredPatchFailed);

            RitsuLibFramework.SubscribeLifecycle<RunEndedEvent>(_ => SpectateOverlay.CloseAll());
            RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(_ => SpectateOverlay.OnCombatEnded());

            Log.Info($"[{ModId}] ready (patches={patcher.AppliedPatchCount})");
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] init failed: {e}");
        }
    }

    private static void OnRequiredPatchFailed()
    {
        Log.Error($"[{ModId}] required avatar patch failed to apply; mod disabled");
    }
}
