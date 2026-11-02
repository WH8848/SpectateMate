using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SpectateMate.Ui;

/// <summary>
/// 观战目标头像的高亮指示。
///
/// NMultiplayerPlayerState 的黄色选择框由 _isMouseOver 驱动：
///
///   private void UpdateHighlightedState() {
///       bool flag = _isMouseOver || _isCreatureHovered;
///       ...
///       if (_isHighlighted) { _selectionReticle.OnSelect(); ... }
///   }
///
/// 而 _isMouseOver 只在 OnFocus / OnUnfocus 里被改写，也就是跟键盘焦点绑定。
/// 这里直接设它并手动触发一次刷新，绕开焦点系统：
/// 观战期间持续点亮，退出观战时统一熄灭。
/// </summary>
internal static class AvatarHighlight
{
    private static readonly FieldInfo? MouseOverField =
        typeof(NMultiplayerPlayerState).GetField(
            "_isMouseOver",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? UpdateMethod =
        typeof(NMultiplayerPlayerState).GetMethod(
            "UpdateHighlightedState",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Dictionary<ulong, NMultiplayerPlayerState> States = new();

    private static ulong? _activeNetId;

    /// <summary>头像创建时登记，供后续按 netId 查找。</summary>
    public static void Register(Player player, NMultiplayerPlayerState state)
    {
        States[player.NetId] = state;
    }

    /// <summary>设定当前高亮目标；传 null 表示全部熄灭。</summary>
    public static void Set(Player? target)
    {
        _activeNetId = target?.NetId;

        foreach (KeyValuePair<ulong, NMultiplayerPlayerState> pair in States)
        {
            Apply(pair.Value, _activeNetId != null && pair.Key == _activeNetId.Value);
        }
    }

    /// <summary>每拍重申一次，防止游戏在焦点/悬停变化时把它关掉。</summary>
    public static void Refresh()
    {
        if (_activeNetId == null)
        {
            return;
        }

        if (States.TryGetValue(_activeNetId.Value, out NMultiplayerPlayerState? state))
        {
            Apply(state, true);
        }
    }

    private static void Apply(NMultiplayerPlayerState? state, bool on)
    {
        if (state == null || !GodotObject.IsInstanceValid(state))
        {
            return;
        }

        try
        {
            MouseOverField?.SetValue(state, on);
            UpdateMethod?.Invoke(state, null);
        }
        catch
        {
            // 头像已被销毁
        }
    }
}
