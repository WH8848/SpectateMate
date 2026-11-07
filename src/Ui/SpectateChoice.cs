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
    // 上一拍是否还处于选择模式的视觉状态，用于触发一次收尾清理。
    private static bool _handVisualActive;
    private static bool _subscribed;

    public static void RememberChooseACard(IReadOnlyList<CardModel> cards, bool canSkip, Player player)
    {
        _mode = Mode.ChooseACard;
        _cards = cards;
        _canSkip = canSkip;
        _owner = player;

        // 必须在进入选择模式时订阅：PlayerChoiceReceived 是「他确认」的唯一信号。
        // 之前只在 BeginHandSelection 里订阅，覆盖层这两种模式从来没订阅过，
        // 所以确认后界面永远不会自己消失。
        EnsureSubscribed();
    }

    public static void RememberCombatPile(CardPile pile, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, Player player)
    {
        _mode = Mode.CombatPile;
        _pile = pile;
        _prefs = prefs;
        _filter = filter;
        _owner = player;

        _lastPileSignature = string.Empty;

        // 同上：进入选择模式就必须订阅，否则收不到他确认的信号。
        EnsureSubscribed();
    }

    public static void BeginHandSelection(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        _handSelecting = true;
        _handOwner = player;
        _handFilter = filter;
        EnsureSubscribed();

        // 视觉切换交给 SyncHandSelection 每拍驱动，
        // 这样退出观战再进来也能重新套用。
    }

    /// <summary>他完成选择（结果已送达）→ 退出选择模式。</summary>
    public static void EndHandSelection()
    {
        _handSelecting = false;
        _handFilter = null;
        _handOwner = null;
        HandMirror.ClearHandSelection();
        HandMirror.ClearHighlight();

        // 幂等清理：即使某次显示过遮罩，这里也一定撤掉。
        HandMirror.ResetSelectionUi();
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

        // 覆盖层模式（发现 / 冲锋这类从牌堆选）没有别的结束信号：
        // 非 owner 分支只是 await 结果，结果送达时就走这里。
        // 不清掉 _mode 的话界面会一直挂在栈上，把后续操作挡死。
        if (_owner != null && player.NetId == _owner.NetId && _mode != Mode.None)
        {
            _mode = Mode.None;
            _cards = null;
            _pile = null;
            _prefs = null;
            _filter = null;
            Close();
        }
    }

    /// <summary>当前选择模式，供诊断使用。</summary>
    public static string CurrentMode => $"{_mode}/screen={(_screen != null)}";

    /// <summary>每拍调用：他在选牌就打开只读界面，选完就关掉。</summary>
    /// <summary>
    /// 上一次观察到的候选牌堆内容签名，用于判定他是否已经选完。
    ///
    /// 不能用数量：冲锋的效果是 TransformTo 把选中的牌替换成另一张，
    /// 牌堆总数不变，只比数量永远发现不了。
    /// </summary>
    private static string _lastPileSignature = string.Empty;

    private static string PileSignature(CardPile? pile)
    {
        if (pile == null)
        {
            return string.Empty;
        }

        try
        {
            var sb = new System.Text.StringBuilder();

            foreach (CardModel card in pile.Cards)
            {
                sb.Append(card.Id.Entry).Append(',');
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void Tick(Player? target, NetScreenType screenType)
    {
        // 先看候选牌堆有没有被选走。
        //
        // 原本靠 PlayerChoiceReceived 收尾，但日志证明那个事件在我们的场景里
        // 不触发：_mode 一直停在 CombatPile，Tick 每拍都判定该显示，
        // Show() 发现界面已被移除就又建一个，界面于是无限重建，
        // 表现出来就是「卡在选卡页面」，而且退出再进也一样。
        //
        // 牌堆内容变少意味着他选走了一张，这是最直接的信号。
        if (_mode == Mode.CombatPile && _pile != null)
        {
            string signature = PileSignature(_pile);

            if (signature.Length > 0)
            {
                if (_lastPileSignature.Length > 0 && signature != _lastPileSignature)
                {
                    ModEntry.Log.Info($"[{ModEntry.ModId}] choice pile changed; closing");

                    Close(clearMode: true);
                    return;
                }

                _lastPileSignature = signature;
            }
        }

        // 不能用 screenType == CardSelection 作为条件。
        //
        // 这个枚举只有在「本机自己弹出选择界面」时才会变成 CardSelection。
        // 观战时他选牌走的是非 owner 分支：FromChooseACardScreen / FromCombatPile
        // 在那里只是 await WaitForRemoteChoice，压根不会创建界面，
        // NetScreenType 始终停在 Room。用这个条件等于永远不显示。
        //
        // 改为只认「有没有待显示的候选」+「是不是他还活着在战斗」，
        // 结束信号交给 PlayerChoiceReceived（结果送达时必定触发）。
        bool shouldShow =
            target != null &&
            _owner != null &&
            _owner.NetId == target.NetId &&
            _mode != Mode.None &&
            IsInCombat(_owner);

        if (shouldShow)
        {
            Show();

            // 选牌网格的卡片是延迟创建的（日志里 Show 时 cards=0），
            // 只在 Show() 里禁用一次会漏掉后生成的卡片，观战者就能点到它们。
            // 这里每拍补一次，DisableInput 本身是幂等的。
            DisableInput(_screen as Node);
        }
        else
        {
            // 他已经不在选牌了：连状态一起清掉，避免界面反复重建。
            Close(clearMode: true);
        }

        SyncHandSelection(target, screenType);
    }

    private static void SyncHandSelection(Player? target, NetScreenType screenType)
    {
        // 注意：不能拿 screenType == CardSelection 当条件。
        // 手牌选择（弃牌/烧牌/暴政）走的是 NPlayerHand.SelectCards，
        // 它不压覆盖层，NetScreenType 始终是 Room，用了这个条件会导致
        // 遮罩和提示永远不出现。
        //
        // 结束信号只认 PlayerChoiceReceived——结果送达时必定触发。
        bool active =
            _handSelecting &&
            target != null &&
            _handOwner != null &&
            _handOwner.NetId == target.NetId;

        if (active)
        {
            // 只保留「不可选的牌隐藏」这一层。
            // 黑幕和顶部提示按需求取消，观战时不再显示。
            HandMirror.ApplyHandSelection(_handFilter);

            // 标记「有东西需要收尾」，退出选择时据此清理一次。
            _handVisualActive = true;
        }
        else if (_handVisualActive)
        {
            // 收尾：撤掉可能残留的遮罩/提示，并恢复全部手牌可见。
            HandMirror.ResetSelectionUi();
            HandMirror.ClearHandSelection();
            _handVisualActive = false;
        }
    }

    /// <summary>
    /// 战斗选牌的候选只在战斗中有意义。
    ///
    /// 战斗结束后 _owner.PlayerCombatState 变成 null，而 _mode 和候选还留着，
    /// 一旦他把选牌界面关掉、进入卡牌奖励，就会被误判成「他在选牌」，
    /// 于是一个空的全透明覆盖层被压到 NOverlayStack 栈顶，把奖励界面挡住，
    /// 表现为「卡牌奖励选不了」。
    /// </summary>
    private static bool IsInCombat(Player player)
    {
        try
        {
            return player.PlayerCombatState != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>战斗结束时清空全部选牌状态。</summary>
    public static void ResetAll()
    {
        Close();

        _mode = Mode.None;
        _cards = null;
        _pile = null;
        _prefs = null;
        _filter = null;
        _owner = null;
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

            _hoverId = null;

            DisableInput(_screen as Node, logCount: true);
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

    private static void DisableInput(Node? root, bool logCount = false)
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

        if (logCount)
        {
            ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: input disabled on {disabled} nodes");
        }
    }

    private static ModelId? _hoverId;

    /// <summary>
    /// 高亮他在选牌界面里悬停的那张。选牌界面里的卡也是我们自己创建的 NCard，
    /// 直接遍历界面子节点匹配 ModelId 即可。颜色由调用方按角色提供。
    /// </summary>
    public static void HighlightHovered(ModelId? id)
    {
        // 界面还没建出来时不能写缓存：否则等界面创建后，id 与缓存相同会被直接跳过，
        // 表现为「悬停高亮永远不出现」。
        if (_screen is not Node root || !GodotObject.IsInstanceValid(root))
        {
            return;
        }

        if (id == _hoverId)
        {
            return;
        }

        _hoverId = id;

        try
        {

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

                // 与原版一致：不改颜色，只让描边变粗（颜色沿用稀有度色）。
                if (match)
                {
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

    /// <summary>
    /// 关闭只读界面。重复调用安全。
    ///
    /// clearMode 用来区分两种「关闭」：
    ///   true  —— 他那边这次选择已经结束了（Tick 判定不该再显示），
    ///            必须连 _mode 一起清掉。否则下一拍 shouldShow 仍然成立，
    ///            Show() 发现 _screen 为空又重建一个，界面就会无限重建，
    ///            表现出来就是「卡在选卡页面」。
    ///   false —— 只是退出观战，_mode 要保留，重新观战才能立刻再看到。
    /// </summary>
    public static void Close(bool clearMode = false)
    {
        var screen = _screen;

        try
        {
            if (screen is GodotObject node && GodotObject.IsInstanceValid(node))
            {
                NOverlayStack.Instance?.Remove(screen);
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice peek close failed: {e.Message}");
        }

        // 兜底：无论 Remove 是否成功，都不能让这个界面留在屏幕上。
        //
        // Remove 抛异常时下面这行以前会把引用置空，于是节点既没被摘掉、
        // 也再没人拿得到——一个空的、可见的选牌界面就永远盖在屏幕上，
        // 战斗结束后的奖励界面会被它挡住，表现为「选不了卡牌奖励」。
        try
        {
            if (screen is Control control && GodotObject.IsInstanceValid(control))
            {
                ModEntry.Log.Info(
                    $"[{ModEntry.ModId}] choice peek closed: removing node " +
                    $"(visible={control.Visible} " +
                    $"parent={control.GetParent()?.Name.ToString() ?? "null"})");

                control.Visible = false;
                control.GetParent()?.RemoveChild(control);
                control.QueueFree();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice peek cleanup failed: {e.Message}");
        }

        _screen = null;
        _hoverId = null;

        if (clearMode)
        {
            _mode = Mode.None;
            _cards = null;
            _pile = null;
            _prefs = null;
            _filter = null;
            _lastPileSignature = string.Empty;
        }
    }

    /// <summary>
    /// 退出观战：连同选择模式一起清掉。
    ///
    /// 之前这里用 clearMode: false，想保留「他还在选」这个事实，
    /// 重新观战就能立刻显示。但那留下了一个隐患：
    /// mode 还在、界面却已经被移除，重新进入观战时 Tick 会立刻按旧 mode
    /// 重建界面——而那一刻候选牌往往还没同步过来，于是建出一个
    /// cards=0、visible=true 的空覆盖层。
    ///
    /// 日志里能看到退出后马上又进入观战（apply target=null 后接
    /// apply target=<队友>），每次都会重建一个空界面，屏幕上就多一层
    /// 看不见内容的遮挡，本地玩家自己的选牌因此点不到。
    ///
    /// 改成清空：重新观战时如果对方确实还在选，Tick 会在下一拍重新识别出来。
    /// </summary>
    public static void Reset()
    {
        Close(clearMode: true);
        HandMirror.ResetSelectionUi();
        HandMirror.ClearHandSelection();
    }
}