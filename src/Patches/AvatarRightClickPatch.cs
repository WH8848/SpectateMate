using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using SpectateMate.Core;
using SpectateMate.Ui;
using STS2RitsuLib.Patching.Models;

namespace SpectateMate.Patches;

/// <summary>
/// Right-clicking a teammate avatar opens the spectate overlay. Left-click stays
/// untouched (vanilla shows the expanded player state).
/// </summary>
internal sealed class AvatarRightClickPatch : IPatchMethod
{
    public static string PatchId => "spectatemate_avatar_right_click";

    public static string Description => "Bind right-click on teammate avatars to the spectate overlay";

    public static bool IsCritical => true;

    public static ModPatchTarget[] GetTargets() =>
    [
        PatchTarget.Method<NMultiplayerPlayerState>(nameof(NMultiplayerPlayerState._Ready)),
    ];

    public static void Postfix(NMultiplayerPlayerState __instance)
    {
        try
        {
            Player? player = __instance.Player;
            if (player == null || __instance.Hitbox is not { } hitbox)
            {
                return;
            }

            if (IsLocal(player))
            {
                return;
            }

            const string BoundFlag = "SpectateMate_bound";
            if (hitbox.HasMeta(BoundFlag))
            {
                return;
            }

            hitbox.SetMeta(BoundFlag, true);
            SpectateRegistry.Register(player);

            hitbox.Connect(NClickableControl.SignalName.MouseReleased, Callable.From<InputEvent>(evt =>
            {
                try
                {
                    if (evt is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Right })
                    {
                        SpectateOverlay.Toggle(player);
                    }
                }
                catch (Exception e)
                {
                    ModEntry.Log.Error($"[{ModEntry.ModId}] right-click handler: {e}");
                }
            }));

            ModEntry.Log.Info($"[{ModEntry.ModId}] right-click bound on netId={player.NetId}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Error($"[{ModEntry.ModId}] avatar patch: {e}");
        }
    }

    private static bool IsLocal(Player player)
    {
        try
        {
            return RunManager.Instance.NetService.NetId == player.NetId;
        }
        catch
        {
            return true;
        }
    }
}
