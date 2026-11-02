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
/// 手牌选择模式（弃牌 / 烧牌 / 暴政）的视觉。
///
/// 目前只保留两层功能：
///   1. ApplyFilter —— 不可选的牌 Visible = false，其余由 RefreshLayout 重新收拢，
///      与被观战者看到的一致；
///   2. HideChrome  —— 幂等地把顶部提示、全屏遮罩、确认按钮收回去。
///
/// 黑幕和提示文字按需求不再显示，所以没有对应的「显示」path；
/// 保留 HideChrome 是为了清掉任何可能残留的状态。
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

    /// <summary>恢复全部可见。</summary>
    public static void Clear(NPlayerHand? hand, IReadOnlyList<NHandCardHolder> holders)
    {
        ApplyFilter(hand, holders, null);
    }

    /// <summary>幂等地把选择模式的 UI 收回去。可重复调用。</summary>
    public static void HideChrome(NPlayerHand? hand)
    {
        try
        {
            if (hand == null || !GodotObject.IsInstanceValid(hand))
            {
                return;
            }

            if (SelectionHeaderField?.GetValue(hand) is MegaRichTextLabel header)
            {
                header.Visible = false;
            }

            if (SelectModeBackstopField?.GetValue(hand) is Control backstop)
            {
                backstop.MouseFilter = Control.MouseFilterEnum.Ignore;

                _backstopTween?.Kill();
                _backstopTween = null;

                if (!backstop.Visible && backstop.SelfModulate.A <= 0f)
                {
                    return;
                }

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

            if (ConfirmButtonField?.GetValue(hand) is NConfirmButton button)
            {
                button.Visible = true;
                button.Disable();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] selection ui reset failed: {e.Message}");
        }
    }
}
