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

        // 自检：观战窗口已经关闭却还在劫持 ActiveHolders。
        //
        // 注意不能用 DisplayedPlayer.IsActive —— 那个标志只在补丁的
        // Prefix → Finalizer 期间为 true（即重定向生效的那几毫秒）。
        // 用它判断会导致：正在观战时每拍都被判成「泄漏」并 ForceHide，
        // 下一拍 Rebuild 又把 _active 设回 true，形成死循环，
        // 表现为 hand mirror leak detected 刷屏、手牌反复隐藏与恢复。
        if (!SpectateOverlay.IsOpen)
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
