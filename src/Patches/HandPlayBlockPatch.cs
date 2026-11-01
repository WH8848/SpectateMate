using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpectateMate.Ui;
using STS2RitsuLib.Patching.Models;

namespace SpectateMate.Patches;

/// <summary>
/// Hard block on playing cards while spectating.
///
/// Every play path funnels through NPlayerHand.StartCardPlay: the mouse drag /
/// click goes OnHolderPressed -> StartCardPlay, and the numeric hotkeys call it
/// directly from _UnhandledKeyInput. Returning false from the prefix means the
/// vanilla method never runs, so no card play action is ever queued — regardless
/// of whether the hand area happened to receive the click.
/// </summary>
internal sealed class HandPlayBlockPatch : IPatchMethod
{
    public static string PatchId => "spectatemate_hand_play_block";

    public static string Description => "Block card plays while spectating";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
    [
        PatchTarget.Method<NPlayerHand>("StartCardPlay", typeof(NHandCardHolder), typeof(bool)),
    ];

    public static bool Prefix()
    {
        try
        {
            return !DisplayedPlayer.IsActive;
        }
        catch
        {
            return true;
        }
    }
}
