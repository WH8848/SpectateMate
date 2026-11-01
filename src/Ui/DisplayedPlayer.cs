using System;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMate.Ui;

/// <summary>
/// Temporarily redirects <see cref="LocalContext.NetId"/> to a teammate.
///
/// This is the whole trick behind the native-UI takeover: every vanilla widget
/// resolves "the player I should show" through <c>LocalContext.GetMe(...)</c>,
/// so swapping the id inside a Prefix and restoring it in a Finalizer makes
/// those widgets render the teammate instead — with zero layout work on our side.
/// </summary>
internal static class DisplayedPlayer
{
    private static Player? _target;
    private static int _depth;
    private static ulong? _savedNetId;

    public static Player? Target => _target;

    public static bool IsActive => _target != null;

    public static void SetTarget(Player? player)
    {
        _target = player;
    }

    /// <summary>
    /// Redirects the local-player id. Returns false when nothing was redirected,
    /// so the matching <see cref="Pop"/> is a no-op.
    /// </summary>
    public static bool Push()
    {
        Player? target = _target;
        if (target == null)
        {
            return false;
        }

        if (_depth == 0)
        {
            _savedNetId = LocalContext.NetId;
            LocalContext.NetId = target.NetId;
        }

        _depth++;
        return true;
    }

    public static void Pop(bool pushed)
    {
        if (!pushed)
        {
            return;
        }

        if (_depth > 0)
        {
            _depth--;
        }

        if (_depth != 0)
        {
            return;
        }

        // Prefer the authoritative id from the net service: if a Push/Pop ever got
        // unbalanced, restoring _savedNetId would leave us pointing at the
        // team-mate forever (which reorders the avatar list and corrupts GetMe).
        ulong? authoritative = null;

        try
        {
            authoritative = RunManager.Instance.NetService.NetId;
        }
        catch
        {
            // Not connected; fall back to whatever we saved.
        }

        LocalContext.NetId = authoritative ?? _savedNetId;
    }

    /// <summary>Runs <paramref name="action"/> with the teammate as "local player".</summary>
    public static void With(Player player, Action action)
    {
        Player? previous = _target;
        _target = player;
        bool pushed = Push();
        try
        {
            action();
        }
        finally
        {
            Pop(pushed);
            _target = previous;
        }
    }
}
