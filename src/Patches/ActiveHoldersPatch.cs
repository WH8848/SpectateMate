using System.Collections.Generic;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SpectateMate.Ui;
using STS2RitsuLib.Patching.Models;

namespace SpectateMate.Patches;

/// <summary>
/// Makes the vanilla hand layout treat the mirrored holders as THE hand.
///
/// NPlayerHand.RefreshLayout, focus neighbours, hover lift and neighbour push-aside
/// are all driven by ActiveHolders. Overriding that getter means the game positions
/// our holders with its own code — bit-for-bit identical to the real hand — instead
/// of us re-implementing the rules.
///
/// Card plays are blocked separately (HandPlayBlockPatch), so the paths that would
/// try to remove/move these holders never run.
/// </summary>
internal sealed class ActiveHoldersPatch : IPatchMethod
{
    public static string PatchId => "spectatemate_active_holders";

    public static string Description => "Vanilla hand layout drives the mirrored holders";

    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets() =>
    [
        PatchTarget.Getter<NPlayerHand>(nameof(NPlayerHand.ActiveHolders)),
    ];

    public static bool Prefix(ref IReadOnlyList<NHandCardHolder> __result)
    {
        if (!HandMirror.IsShowing)
        {
            return true;
        }

        // 自检：观战已经结束（没有目标）却还在劫持 ActiveHolders。
        // 这会让游戏拿我们的镜像 holder 去排自己的手牌，表现为手牌位置全乱。
        // 这里立刻恢复，然后照常走原版 getter。
        if (!DisplayedPlayer.IsActive)
        {
            HandMirror.ForceHide();
            return true;
        }

        IReadOnlyList<NHandCardHolder> mirrors = HandMirror.MirrorHolders;
        if (mirrors.Count == 0)
        {
            return true;
        }

        __result = mirrors;
        return false;
    }
}
