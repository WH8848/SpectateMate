using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Patching.Models;

namespace SpectateMate.Patches;

/// <summary>
/// The team-mate avatar list is ordered by LocalContext.GetMe(): whoever that
/// returns gets the first (top) slot.
///
/// Our spectate machinery temporarily redirects LocalContext.NetId to the
/// team-mate. If that redirection is still in effect when the container is
/// rebuilt (rejoining a run mid-combat, returning from the menu), the TEAM-MATE
/// takes the top slot and our own avatar sinks below him.
///
/// This patch pins the id to the real local player for the duration of the
/// initialization, so the ordering can never be affected by spectating.
/// </summary>
internal sealed class AvatarOrderPatch : IPatchMethod
{
    public static string PatchId => "spectatemate_avatar_order";

    public static string Description => "Keep the local player first in the avatar list";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
    [
        PatchTarget.Method<NMultiplayerPlayerStateContainer>(
            nameof(NMultiplayerPlayerStateContainer.Initialize),
            typeof(RunState)),
    ];

    public static void Prefix(out ulong? __state)
    {
        __state = LocalContext.NetId;

        try
        {
            LocalContext.NetId = RunManager.Instance.NetService.NetId;
        }
        catch
        {
            // Not in a multiplayer session; leave it alone.
        }
    }

    public static void Finalizer(ulong? __state)
    {
        if (__state.HasValue)
        {
            LocalContext.NetId = __state;
        }
    }
}
