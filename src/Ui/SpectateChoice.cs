using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

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

    // 手牌选择模式（后空翻、计算下注这类）：不弹界面，只是把手牌过滤成可选的那些。
    private static bool _handSelecting;
    private static Func<CardModel, bool>? _handFilter;
    private static Player? _handOwner;
    private static string _handPrompt = string.Empty;
    private static bool _handUiShown;
    private static bool _subscribed;

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

    public static void BeginHandSelection(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        _handSelecting = true;
        _handOwner = player;
        _handFilter = filter;
        EnsureSubscribed();

        try
        {
            _handPrompt = prefs.Prompt.GetFormattedText();
        }
        catch
        {
            _handPrompt = string.Empty;
        }

        // 真正的视觉切换交给 SyncHandSelection，这样退出观战再进来也能重新套用。
        _handUiShown = false;
    }

    /// <summary>他完成选择（结果已送达）→ 退出选择模式。</summary>
    public static void EndHandSelection()
    {
        _handSelecting = false;
        _handFilter = null;
        _handOwner = null;
        _handPrompt = string.Empty;
        _handUiShown = false;
        HandMirror.ClearHandSelection();
        HandMirror.ApplySelectionUi(visible: false, null);
    }

    /// <summary>
    /// 订阅「收到远端选择结果」事件。这是唯一可靠的结束信号：
    /// FromHand 在本地的非 owner 分支只是 await WaitForRemoteChoice，
    /// 而 Harmony 的 Postfix 在方法启动时就会执行，拿不到完成时机。
    /// </summary>
    private static void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        try
        {
            RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived += OnRemoteChoiceReceived;
            _subscribed = true;
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice subscribe failed: {e.Message}");
        }
    }

    private static void OnRemoteChoiceReceived(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        if (_handOwner != null && player.NetId == _handOwner.NetId)
        {
            EndHandSelection();
        }
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

        SyncHandSelection(target);
    }

    private static void SyncHandSelection(Player? target)
    {
        bool active =
            _handSelecting &&
            target != null &&
            _handOwner != null &&
            _handOwner.NetId == target.NetId;

        if (active)
        {
            HandMirror.ApplyHandSelection(_handFilter);

            if (!_handUiShown)
            {
                // 复刻原版：顶部提示 + 确认按钮（按钮仅视觉）。
                HandMirror.ApplySelectionUi(visible: true, _handPrompt);
                _handUiShown = true;
            }
        }
        else if (_handUiShown)
        {
            // 只是暂时不可见（退出观战、切目标），保留状态以便回来时重新套用。
            HandMirror.ApplySelectionUi(visible: false, null);
            _handUiShown = false;
            HandMirror.ClearHandSelection();
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

            // 诊断：界面建出来了但卡片是空的，需要区分「没填充」和「填充了但不可见」。
            Node? probeRoot = _screen as Node;
            if (probeRoot != null)
            {
                int cards = probeRoot.FindChildren("*", nameof(NCard), true, false).Count;
                int holders = probeRoot.FindChildren("*", nameof(NCardHolder), true, false).Count;
                bool visible = probeRoot is CanvasItem ci && ci.Visible;
                Color mod = probeRoot is CanvasItem ci2 ? ci2.Modulate : Colors.White;
                ModEntry.Log.Info(
                    $"[{ModEntry.ModId}] choice peek: cards={cards} holders={holders} " +
                    $"visible={visible} modulate={mod}");
            }
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
                // 空牌堆时游戏自己会立即完成选择；这里若照常创建，
                // _Ready 里的 _pile.Cards.First() 会抛 InvalidOperationException，
                // 导致界面停在空白状态。
                if (_pile.Cards.Count == 0)
                {
                    ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: pile {_pile.Type} empty, skip");
                    return null;
                }

                NCombatPileCardSelectScreen screen = NCombatPileCardSelectScreen.Create(_pile, _prefs.Value, _filter);
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

                // NConfirmButton / 跳过按钮都是 NButton : NClickableControl : Control，
                // 不是 Godot 的 BaseButton，之前的 case BaseButton 根本匹配不到它们，
                // 这才是「观战者能点确认」的真正原因。
                // Disable() 内部会 FocusMode = None 并 SetProcessInput(false)。
                case NButton button:
                    // 观战者不能替他做任何操作，按钮没有存在的必要，直接隐藏。
                    // 这些界面都是我们自己创建的实例，隐藏不会影响他那边。
                    button.Visible = false;
                    button.Disable();
                    button.MouseFilter = Control.MouseFilterEnum.Ignore;
                    disabled++;
                    break;

                case BaseButton baseButton:
                    baseButton.Disabled = true;
                    disabled++;
                    break;
            }
        }

        ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: input disabled on {disabled} nodes");
    }

    private static ModelId? _hoverId;

    /// <summary>
    /// 高亮他在选牌界面里悬停的那张。选牌界面里的卡也是我们自己创建的 NCard，
    /// 直接遍历界面子节点匹配 ModelId 即可。
    /// </summary>
    public static void HighlightHovered(ModelId? id)
    {
        if (id == _hoverId)
        {
            return;
        }

        _hoverId = id;

        try
        {
            if (_screen is not Node root || !GodotObject.IsInstanceValid(root))
            {
                return;
            }

            foreach (Node node in root.FindChildren("*", nameof(NCardHolder), true, false))
            {
                if (node is not NCardHolder holder)
                {
                    continue;
                }

                NCard? card = holder.CardNode;
                if (card == null || !GodotObject.IsInstanceValid(card))
                {
                    continue;
                }

                bool match = id != null && card.Model != null && card.Model.Id == id;

                if (match)
                {
                    card.CardHighlight.Modulate = NCardHighlight.gold;
                    card.CardHighlight.AnimShow();
                }
                else
                {
                    card.CardHighlight.AnimHide();
                }
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice hover highlight failed: {e.Message}");
        }
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
        _hoverId = null;
    }

    /// <summary>
    /// 退出观战：只恢复视觉，保留「他还在选」这个事实。
    /// 他可能一直停在弃牌/烧牌的选择页面没动，重新观战时要能立刻再看到。
    /// </summary>
    public static void Reset()
    {
        Close();
        HandMirror.ApplySelectionUi(visible: false, null);
        HandMirror.ClearHandSelection();
        _handUiShown = false;
    }
}
