using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.addons.mega_text;

namespace SpectateMate.Ui;

/// <summary>
/// 手牌选择模式（弃牌 / 烧牌 / 暴政）的视觉复刻。
///
/// 原版 NPlayerHand.SelectCards 做三件事：
///   1. 不可选的牌 Visible = false，其余由 RefreshLayout 重新收拢；
///   2. 顶部 _selectionHeader 显示提示文字；
///   3. _selectModeBackstop 淡入成不透明，画面整体变暗。
///
/// 观战时这些控件都在同一个 NPlayerHand 上，直接复用，位置和样式天然一致；
/// 确认按钮则完全隐藏——观战者本来就不能替他选，留着只会诱导点击。
///
/// 这里只做视觉，不参与任何状态机；状态由 SpectateChoice 持有。
/// </summary>
public static class HandSelectionView
{
    private static readonly System.Reflection.FieldInfo? SelectionHeaderField =
        typeof(NPlayerHand).GetField(
            "_selectionHeader",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.FieldInfo? ConfirmButtonField =
        typeof(NPlayerHand).GetField(
            "_selectModeConfirmButton",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.FieldInfo? SelectModeBackstopField =
        typeof(NPlayerHand).GetField(
            "_selectModeBackstop",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static Tween? _backstopTween;
    private static bool _warnedHeader;
    private static bool _warnedBackstop;

    /// <summary>按 filter 隐藏不可选的牌；filter 为 null 表示全部可选。</summary>
    public static void ApplyFilter(
        NPlayerHand? hand,
        IReadOnlyList<NHandCardHolder> holders,
        Func<CardModel, bool>? filter)
    {
        try
        {
            bool changed = false;

            foreach (NHandCardHolder holder in holders)
            {
                if (holder == null || !GodotObject.IsInstanceValid(holder))
                {
                    continue;
                }

                CardModel? model = holder.CardNode?.Model;
                bool visible = filter == null || (model != null && filter(model));

                if (holder.Visible != visible)
                {
                    holder.Visible = visible;
                    changed = true;
                }
            }

            if (changed && hand != null && GodotObject.IsInstanceValid(hand))
            {
                hand.ForceRefreshCardIndices();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] hand selection filter failed: {e.Message}");
        }
    }

    /// <summary>顶部提示、遮罩、确认按钮的显示与隐藏。</summary>
    public static void ApplyChrome(NPlayerHand? hand, bool visible, string? prompt)
    {
        try
        {
            if (hand == null || !GodotObject.IsInstanceValid(hand))
            {
                return;
            }

            if (SelectionHeaderField?.GetValue(hand) is MegaRichTextLabel header)
            {
                header.Visible = visible;

                if (visible && !string.IsNullOrEmpty(prompt))
                {
                    header.Text = "[center]" + prompt + "[/center]";
                }
            }
            else if (visible && !_warnedHeader)
            {
                _warnedHeader = true;
                ModEntry.Log.Warn($"[{ModEntry.ModId}] selection header node missing; prompt will not show");
            }

            if (SelectModeBackstopField?.GetValue(hand) is Control backstop)
            {
                backstop.MouseFilter = Control.MouseFilterEnum.Ignore;
                _warnedBackstop = false;

                // 必须 kill 上一个补间：退出观战时的淡出还没跑完就重新进入，
                // 两个补间会同时改 self_modulate:a，表现为闪一下然后一直亮着。
                _backstopTween?.Kill();
                _backstopTween = null;

                if (visible)
                {
                    if (!backstop.Visible)
                    {
                        Color from = backstop.SelfModulate;
                        from.A = 0f;
                        backstop.SelfModulate = from;
                    }

                    backstop.Visible = true;
                    _backstopTween = backstop.CreateTween();
                    _backstopTween.TweenProperty(backstop, "self_modulate:a", 1f, 0.2f)
                        .SetTrans(Tween.TransitionType.Cubic)
                        .SetEase(Tween.EaseType.Out);
                }
                else
                {
                    _backstopTween = backstop.CreateTween();
                    _backstopTween.TweenProperty(backstop, "self_modulate:a", 0f, 0.2f)
                        .SetTrans(Tween.TransitionType.Cubic)
                        .SetEase(Tween.EaseType.Out);
                    _backstopTween.TweenCallback(Callable.From(() =>
                    {
                        Color zero = backstop.SelfModulate;
                        zero.A = 0f;
                        backstop.SelfModulate = zero;
                        backstop.Visible = false;
                    }));
                }
            }
            else if (visible && !_warnedBackstop)
            {
                _warnedBackstop = true;
                ModEntry.Log.Warn($"[{ModEntry.ModId}] selection backstop node missing; screen will not dim");
            }

            if (ConfirmButtonField?.GetValue(hand) is NConfirmButton button)
            {
                // 确认按钮直接隐藏：观战者不能替他选，按钮没有存在的意义。
                button.Visible = !visible;
                button.Disable();

                if (visible)
                {
                    button.MouseFilter = Control.MouseFilterEnum.Ignore;
                }
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] selection ui failed: {e.Message}");
        }
    }

    /// <summary>退出选择模式：所有牌恢复可见。</summary>
    public static void Clear(NPlayerHand? hand, IReadOnlyList<NHandCardHolder> holders)
    {
        ApplyFilter(hand, holders, null);
    }

    /// <summary>彻底复位，供观战退出时调用。</summary>
    public static void Reset(NPlayerHand? hand)
    {
        ApplyChrome(hand, visible: false, null);

        _backstopTween?.Kill();
        _backstopTween = null;
    }
}
