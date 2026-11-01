using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace SpectateMate.Ui;

/// <summary>
/// 观战时复用原版选牌界面，只读展示被观战者的候选。
///
/// 两种来源：
/// * FromChooseACardScreen —— 发现、飞溅这类「从 N 张里挑一张」（NChooseACardSelectionScreen）
/// * FromCombatPile      —— 冲锋（抽牌堆）、涅奥之怒（弃牌堆）这类（NCombatPileCardSelectScreen）
///
/// 关键约束：
/// * CardsSelected() 只有被 await 时才会把界面移出 NOverlayStack。我们不 await
///   （不能替他做选择），所以关闭必须自己 Remove。
/// * 界面内被点击会 SetResult，虽然不抛异常但界面不会关，所以必须禁掉输入。
/// * 不能直接用 ProcessMode.Disabled：那会连飞入补间一起冻结，卡片会叠在一起。
/// </summary>
public static class SpectateChoice
{
    private enum Mode
    {
        None,
        ChooseACard,
        CombatPile,
    }

    private static Mode _mode;
    private static IReadOnlyList<CardModel>? _cards;
    private static bool _canSkip;
    private static CardPile? _pile;
    private static CardSelectorPrefs? _prefs;
    private static Func<CardModel, bool>? _filter;
    private static Player? _owner;
    private static IOverlayScreen? _screen;

    public static void RememberChooseACard(IReadOnlyList<CardModel> cards, bool canSkip, Player player)
    {
        _mode = Mode.ChooseACard;
        _cards = cards;
        _canSkip = canSkip;
        _owner = player;
    }

    public static void RememberCombatPile(CardPile pile, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, Player player)
    {
        _mode = Mode.CombatPile;
        _pile = pile;
        _prefs = prefs;
        _filter = filter;
        _owner = player;
    }

    /// <summary>每拍调用：他在选牌就打开只读界面，选完就关掉。</summary>
    public static void Tick(Player? target, NetScreenType screenType)
    {
        bool shouldShow =
            target != null &&
            _owner != null &&
            _owner.NetId == target.NetId &&
            screenType == NetScreenType.CardSelection &&
            _mode != Mode.None;

        if (shouldShow)
        {
            Show();
        }
        else
        {
            Close();
        }
    }

    private static void Show()
    {
        try
        {
            if (_screen != null && GodotObject.IsInstanceValid(_screen as GodotObject))
            {
                return;
            }

            _screen = BuildScreen();
            if (_screen == null)
            {
                _mode = Mode.None;
                return;
            }

            DisableInput(_screen as Node);
            ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: {_mode} ({DescribeCount()})");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice peek show failed: {e.Message}");
            _screen = null;
        }
    }

    private static IOverlayScreen? BuildScreen()
    {
        switch (_mode)
        {
            case Mode.ChooseACard when _cards != null && _cards.Count > 0:
                return NChooseACardSelectionScreen.ShowScreen(_cards, _canSkip);

            case Mode.CombatPile when _pile != null && _prefs != null:
            {
                NCombatPileCardSelectScreen screen = NCombatPileCardSelectScreen.Create(_pile!, _prefs.Value, _filter);
                NOverlayStack.Instance?.Push(screen);
                return screen;
            }

            default:
                return null;
        }
    }

    private static string DescribeCount() =>
        _mode == Mode.CombatPile ? $"pile={_pile?.Type}" : $"{_cards?.Count ?? 0} 张";

    private static void DisableInput(Node? root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            return;
        }

        if (root is Control control)
        {
            control.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        int disabled = 0;
        foreach (Node node in root.FindChildren("*", string.Empty, true, false))
        {
            switch (node)
            {
                case NCardHolder holder:
                    holder.Hitbox.SetEnabled(false);
                    holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
                    disabled++;
                    break;

                case BaseButton button:
                    button.Disabled = true;
                    disabled++;
                    break;
            }
        }

        ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: input disabled on {disabled} nodes");
    }

    /// <summary>关闭只读界面。重复调用安全。</summary>
    public static void Close()
    {
        try
        {
            if (_screen is GodotObject node && GodotObject.IsInstanceValid(node))
            {
                NOverlayStack.Instance?.Remove(_screen);
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice peek close failed: {e.Message}");
        }

        _screen = null;
    }

    /// <summary>
    /// 退出观战时只关界面，保留最近一次候选。
    /// 他可能还停在选牌界面没动，重新观战时要能立刻再看到。
    /// </summary>
    public static void Reset()
    {
        Close();
    }
}
