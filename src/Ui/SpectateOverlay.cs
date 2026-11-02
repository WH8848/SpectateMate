using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using SpectateMate.Core;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace SpectateMate.Ui;

/// <summary>
/// Compact, click-through spectate window pinned to the top-right corner.
/// It never takes ownership of any vanilla node: cards are fresh <see cref="NCard"/>
/// instances rendered inside our own container, and mouse input passes straight
/// through to the game while it is open.
/// </summary>
public static class SpectateOverlay
{
    // 心跳周期：远端玩家的数据不会发事件，只能定期拉。
    // 0.05 秒意味着最坏滞后 50ms，肉眼基本无法察觉；
    // 每次心跳的工作量很小（比对手牌、约 20 次反射读字段），
    // 即使 20 次/秒也只有不到 1% 的单核占用。
    private const float RefreshSeconds = 0.05f;
    private const ulong ToggleDebounceMs = 150;

    private static ulong _lastToggleMs;
    private const int MaxCardNodes = 10;
    private const float PanelWidth = 520f;
    private const float CardScale = 0.28f;

    private static CanvasLayer? _layer;
    private static Control? _root;
    private static PanelContainer? _panel;
    private static Label? _title;
    private static Label? _vitals;
    private static Label? _status;
    private static Label? _hoverLine;
    private static Label? _emptyHand;
    private static HFlowContainer? _handRow;
    private static Godot.Timer? _refresh;
    private static Godot.Timer? _escapeWatch;
    private static bool _wasInCombat;
    private static bool _lastMyTurn;

    private static Player? _target;
    private static string _cachedName = string.Empty;
    private static string _handKey = string.Empty;
    // 没有自动观战：只有右键头像才打开，Esc 关闭。

    private static readonly List<NCard> _cards = new();
    private static readonly List<Control> _slots = new();
    private static bool _cardPoolWorks = true;

    public static bool IsOpen => _root != null && GodotObject.IsInstanceValid(_root);

    public static Player? Target => _target;

    public static void Toggle(Player player)
    {
        ulong now = Godot.Time.GetTicksMsec();
        if (now - _lastToggleMs < ToggleDebounceMs)
        {
            return;
        }

        _lastToggleMs = now;

        if (IsOpen && _target != null && _target.NetId == player.NetId)
        {
            Close();
            return;
        }

        // 只在战斗中允许观战。
        //
        // 战斗之外（奖励界面、地图、商店、篝火）没有可镜像的实时状态，
        // 强行进入只会让各处 rebind 拿到失效节点，之前的空覆盖层挡住卡牌奖励
        // 就是这么来的。这里从入口处直接拒掉，比在内部逐处兜底干净。
        if (!IsInCombat())
        {
            ModEntry.Log.Info($"[{ModEntry.ModId}] spectate is only available during combat");
            return;
        }

        Open(player);
    }

    /// <summary>现在是否轮到我出牌。LocalContext 此时指向的是真实的本地玩家。</summary>
    private static bool IsMyTurn()
    {
        try
        {
            if (!IsInCombat())
            {
                return false;
            }

            Player? me = SpectateUi.RunState != null
                ? LocalContext.GetMe(SpectateUi.RunState.Players)
                : null;

            return me?.PlayerCombatState?.Phase == PlayerTurnPhase.Play;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 是否还在战斗中。
    ///
    /// 用 IsOverOrEnding 的反面而不是 IsInProgress：后者只是 turnState 的透传，
    /// 战斗结束后仍会保持 true 直到下一场战斗开始，会把奖励界面、地图这些
    /// 非战斗场面误判成战斗中。
    /// </summary>
    private static bool IsInCombat()
    {
        try
        {
            return !CombatManager.Instance.IsOverOrEnding;
        }
        catch
        {
            return false;
        }
    }

    public static void Open(Player player)
    {
        Close();

        try
        {
            _target = player;
            AvatarHighlight.Set(player);
            _lastMyTurn = IsMyTurn();

            try
            {
                _wasInCombat = !CombatManager.Instance.IsOverOrEnding;
            }
            catch
            {
                _wasInCombat = true;
            }
            _cachedName = string.Empty;
            _handKey = string.Empty;
            SpectateRegistry.Remember(player);

            var layer = new CanvasLayer { Layer = 100, Name = "SpectateMateLayer" };
            _layer = layer;

            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _root = root;
            layer.AddChild(root);

            if (NGame.Instance is not { } game)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] NGame.Instance is null, overlay not attached");
                Close();
                return;
            }

            // 必须先进入场景树，否则 Timer.Start() 会报 "not inside the scene tree"。
            game.AddChild(layer);
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

            var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _panel = panel;
            root.AddChild(panel);
            panel.AnchorLeft = 1f;
            panel.AnchorRight = 1f;
            panel.AnchorTop = 0f;
            panel.AnchorBottom = 0f;
            panel.OffsetLeft = -(PanelWidth + 16f);
            panel.OffsetRight = -16f;
            panel.OffsetTop = 96f;
            panel.OffsetBottom = 96f;
            panel.GrowHorizontal = Control.GrowDirection.Begin;
            panel.GrowVertical = Control.GrowDirection.End;
            panel.CustomMinimumSize = new Vector2(PanelWidth, 0f);

            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.04f, 0.05f, 0.07f, 0.82f),
                BorderColor = new Color(0.72f, 0.58f, 0.28f, 0.85f),
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8
            };
            style.SetBorderWidthAll(1);
            style.SetContentMarginAll(10f);
            panel.AddThemeStyleboxOverride("panel", style);

            var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", 4);
            panel.AddChild(box);

            _title = AddLabel(box, 17, new Color(0.95f, 0.85f, 0.6f));
            _vitals = AddLabel(box, 13, Colors.White);
            _status = AddLabel(box, 12, new Color(0.8f, 0.84f, 0.92f));
            _hoverLine = AddLabel(box, 13, new Color(0.75f, 0.95f, 0.8f));

            _emptyHand = AddLabel(box, 12, new Color(0.6f, 0.62f, 0.68f));
            _emptyHand.Text = "不在战斗中";
            _emptyHand.Visible = false;

            _handRow = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _handRow.AddThemeConstantOverride("h_separation", 4);
            _handRow.AddThemeConstantOverride("v_separation", 4);
            box.AddChild(_handRow);

            AddLabel(box, 11, new Color(0.5f, 0.52f, 0.58f)).Text = "右键头像 或 Esc 关闭";

            _refresh = new Godot.Timer { WaitTime = RefreshSeconds, OneShot = false };
            root.AddChild(_refresh);
            _refresh.Timeout += Refresh;
            _refresh.Start();

            _escapeWatch = new Godot.Timer { WaitTime = 0.1f, OneShot = false };
            root.AddChild(_escapeWatch);
            _escapeWatch.Timeout += WatchEscape;
            _escapeWatch.Start();

            SpectateUi.Apply(player);
            SpectateUi.SetInteraction(false);
            Refresh();
            ModEntry.Log.Info($"[{ModEntry.ModId}] spectating netId={player.NetId}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Error($"[{ModEntry.ModId}] open failed: {e}");
            Close();
        }
    }

    public static void Close()
    {
        try
        {
            // QueueFree only takes effect at frame end; until then the old timers
            // would keep calling the static Refresh() against the NEW panel.
            // 心跳不在这里停。
            //
            // 观战期间 SyncExhaustVisibility 会按被观战者的数量收起消耗牌堆按钮。
            // 退出后如果只靠 CardAddFinished 事件恢复，一旦事件没绑上（这条路径
            // 已经出过多次问题），按钮就再也不会出现。
            // 保留心跳、在非观战状态下只做一次消耗牌同步，可以彻底绕开这个依赖。
            _escapeWatch?.Stop();
            _escapeWatch = null;

            // 观战结束，消耗牌堆的显示目标重新从当前数量推起。
            CombatWidgetRebind.ResetExhaustVisibilityCache();

            // overlay 自己的心跳会随 layer 一起销毁，这里换成常驻的那个。
            EnsureIdleRefresh();

            HandMirror.Hide(SpectateUi.Hand);
            FreeCards();

            if (_layer != null && GodotObject.IsInstanceValid(_layer))
            {
                _layer.QueueFree();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Error($"[{ModEntry.ModId}] close error: {e}");
        }
        finally
        {
            _layer = null;
            _root = null;
            _panel = null;
            _refresh = null;
            _escapeWatch = null;
            _handRow = null;
            _emptyHand = null;
            _title = null;
            _vitals = null;
            _status = null;
            _hoverLine = null;
            _target = null;
            _handKey = string.Empty;
            AvatarHighlight.Set(null);

            ModEntry.Log.Info($"[{ModEntry.ModId}] close: restoring bindings");

            // 先恢复手牌状态：Apply(null) 里有一串 rebind，任何一处抛异常
            // 都会跳过它，导致手牌一直停在灰色/下移状态。
            SpectateUi.RestoreOwnHandState();
            SpectateChoice.Reset();
            SpectateUi.SetInteraction(true);
            SpectateUi.Apply(null);

            ModEntry.Log.Info($"[{ModEntry.ModId}] close: bindings restored");
        }
    }

    public static void CloseAll()
    {
        Close();
        SpectateRegistry.Clear();
    }

    public static void OnCombatEnded()
    {
        if (!IsOpen)
        {
            SpectateChoice.ResetAll();
            return;
        }

        // 战斗结束就退出观战：战斗状态已经失效，继续留在观战画面只会显示陈旧数据，
        // 表现为「页面卡住」。只关窗口不清登记，头像还能再次右键进入。
        Close();

        // 手动的 Close 会保留选牌候选（方便退出再进立刻看到），
        // 但战斗结束必须彻底清空：否则进入卡牌奖励时会被误判成「他在选牌」，
        // 弹出一个空的全透明覆盖层把奖励界面挡住。
        SpectateChoice.ResetAll();
    }

    private static Label AddLabel(Container parent, int fontSize, Color color)
    {
        var label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label);
        return label;
    }

    private static void WatchEscape()
    {
        // 退出键来自 RitsuLib 设置（默认 Esc），不再写死。
        if (Godot.Input.IsKeyPressed(SpectateSettings.ExitKey))
        {
            Close();
        }
    }

    /// <summary>
    /// 常驻的轻量定时器，只在非观战时同步消耗牌堆按钮。
    ///
    /// 为什么不复用 _refresh：它挂在 overlay 的 layer 上，
    /// Close 一销毁 layer，定时器跟着没，退出观战后就没人兜底了。
    ///
    /// 为什么需要它：观战期间 SyncExhaustVisibility 会按被观战者的数量
    /// 收起消耗牌堆按钮；退出后如果只靠 CardAddFinished 事件恢复，
    /// 一旦事件没绑上，按钮就再也不会出现（这条路径已经反复出问题）。
    /// 每 0.2 秒比一次实际数量，代价可以忽略。
    /// </summary>
    private static Godot.Timer? _idleRefresh;

    private static void EnsureIdleRefresh()
    {
        try
        {
            if (_idleRefresh != null && GodotObject.IsInstanceValid(_idleRefresh))
            {
                return;
            }

            Node? host = NGame.Instance;
            if (host == null)
            {
                return;
            }

            _idleRefresh = new Godot.Timer { WaitTime = 0.2f, OneShot = false };
            host.AddChild(_idleRefresh);
            _idleRefresh.Timeout += () =>
            {
                if (IsOpen)
                {
                    return;
                }

                try
                {
                    SpectateUi.SyncExhaustPileOnly();
                }
                catch
                {
                    // ignored
                }
            };
            _idleRefresh.Start();
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] idle timer failed: {e.Message}");
        }
    }

    private static void Refresh()
    {
        if (!IsOpen)
        {
            return;
        }

        if (_target == null || _root == null || !GodotObject.IsInstanceValid(_root))
        {
            return;
        }

        // 战斗一结束就退出观战。
        //
        // 观战状态跨战斗残留是多个问题的共同来源：镜像 holder 属于已销毁的
        // NPlayerHand、牌堆按钮还指着上一场的目标、遮罩/高亮层留在树上。
        // CombatEndedEvent 在部分时序下不会触发（日志里看不到），这里用
        // 「上一拍还在战斗、这一拍不在了」做兜底，避免误伤战斗外的观战。
        try
        {
            // 用 IsOverOrEnding 而不是 IsInProgress。
            //
            // IsInProgress 只是 _turnState.IsInProgress 的透传，战斗结束后
            // _turnState 并不会立刻失效，它会继续保持 true 一直到下一场战斗，
            // 于是「从战斗中变成不在战斗中」这个判断永远不成立，
            // 观战就一直开到了卡牌奖励界面，把奖励界面盖住。
            //
            // IsOverOrEnding = IsEnding || !IsInProgress，IsEnding 在主要敌人
            // 全部死亡的那一刻就为真，比奖励界面弹出更早。
            bool inCombat = !CombatManager.Instance.IsOverOrEnding;

            if (_wasInCombat && !inCombat)
            {
                ModEntry.Log.Info($"[{ModEntry.ModId}] combat ended; leaving spectate");
                Close();
                return;
            }

            _wasInCombat = inCombat;
        }
        catch
        {
            // ignored
        }

        // 观战目标头像的高亮：游戏在焦点/悬停变化时会重算，这里每拍重申一次。
        try
        {
            AvatarHighlight.Refresh();
        }
        catch
        {
            // ignored
        }

        // 「下回合自动退出」：只在「从不是我的回合」变成「是我的回合」时触发，
        // 否则观战一开始就轮到自己行动的话会被立刻关掉。
        try
        {
            if (SpectateSettings.AutoExitNextTurn)
            {
                bool myTurnNow = IsMyTurn();

                if (!_lastMyTurn && myTurnNow)
                {
                    ModEntry.Log.Info($"[{ModEntry.ModId}] my turn started; leaving spectate");
                    Close();
                    return;
                }

                _lastMyTurn = myTurnNow;
            }
            else
            {
                _lastMyTurn = IsMyTurn();
            }
        }
        catch
        {
            // ignored
        }

        CheckMirrorLeak();

        // 手牌镜像放在最前面且独立保护：后面的文本刷新一旦抛异常，
        // 也绝不能让镜像/拦截整段失效（之前正是这样导致亮出本地手牌）。
        RefreshHandMirror();

        try
        {
            UpdateTexts();
        }
        catch (Exception e)
        {
            ModEntry.Log.Error($"[{ModEntry.ModId}] refresh error: {e}");
        }
    }

    /// <summary>观战已结束但镜像还挂着 → 立即恢复，避免影响自己的手牌。</summary>
    private static void CheckMirrorLeak()
    {
        if (!IsOpen && HandMirror.IsShowing)
        {
            HandMirror.ForceHide();
        }
    }

    private static void RefreshHandMirror()
    {
        try
        {
            PlayerCombatState? cs = null;
            try
            {
                cs = _target?.PlayerCombatState;
            }
            catch
            {
                // player object disposed
            }

            if (cs != null)
            {
                RebuildHand(cs);
                HandMirror.Sync(SpectateUi.Hand, cs.Hand.Cards);

                // 这两个必须每拍无条件跑，之前放在 hover 分支里，
                // 只在鼠标悬停到某张牌时才执行，所以原生编号一直没被压掉。
                // 两者各自独立：费用刷新会读 MaxEnergy（走游戏 hook 链），
                // 一旦它抛异常，编号压制就不能跟着被跳过。
                try
                {
                    HandMirror.RefreshCostColors(cs.Energy);
                }
                catch (Exception e)
                {
                    ModEntry.Log.Warn($"[{ModEntry.ModId}] cost refresh failed: {e.Message}");
                }

                try
                {
                    VanillaHandVisibility.HideLabels(SpectateUi.Hand);
                }
                catch (Exception e)
                {
                    ModEntry.Log.Warn($"[{ModEntry.ModId}] vanilla label hide failed: {e.Message}");
                }
            }
            else
            {
                FreeCards();
                _handKey = string.Empty;

                // 仍在观战中：保持原生卡面隐藏，绝不能把自己的牌亮出来
                HandMirror.Hide(SpectateUi.Hand, restoreVanilla: false);
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Error($"[{ModEntry.ModId}] hand mirror refresh failed: {e}");
        }
    }

    private static void UpdateTexts()
    {
        if (_target == null || _root == null || !GodotObject.IsInstanceValid(_root))
        {
            return;
        }

        if (_cachedName.Length == 0)
        {
            _cachedName = SafePlayerName(_target.NetId);
        }

        // 能量/星不会为远端玩家发变更事件，只能每拍主动刷。
        try
        {
            SpectateUi.TickDisplayed();
            SpectateUi.SyncHandEnabled(_target);
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] tick failed: {e.Message}");
        }

        PeerInputSynchronizer? input = null;
        PlayerCombatState? cs = null;
        Creature? creature = null;

        try
        {
            input = RunManager.Instance.InputSynchronizer;
            cs = _target.PlayerCombatState;
            creature = _target.Creature;
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] target read failed: {e.Message}");
        }

        // 选牌镜像必须最先驱动：下面的 UI 刷新一旦抛异常，整段都会被跳过，
        // 表现为「他打杂技时观战侧完全没反应」。
        try
        {
            NetScreenType screenNow = input != null
                ? input.GetScreenType(_target.NetId)
                : NetScreenType.None;

            SpectateChoice.Tick(_target, screenNow);
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice tick failed: {e.Message}");
        }

            if (_title != null)
            {
                _title.Text = $"观战 {_cachedName} · {SafeCharacter(_target)}";
            }

            if (_vitals != null)
            {
                // 玩家离开后 Creature 的内部状态可能已失效，直接读会抛
                // NullReferenceException（日志里那串 refresh error）。
                string hp;

                try
                {
                    hp = creature == null
                        ? "HP -"
                        : $"HP {creature.CurrentHp}/{creature.MaxHp}  格挡 {creature.Block}";
                }
                catch
                {
                    hp = "HP -";
                }
                // 读 MaxEnergy 会触发 Hook.ModifyMaxEnergy 遍历战斗 hook 监听器；
                // 那一链路上如果有坏监听器（别的 mod），这里就会抛 NRE 并让整个
                // UpdateTexts 中断——后面的选牌驱动也就不会执行了。
                string res;

                try
                {
                    res = cs == null
                        ? "不在战斗中"
                        : $"能量 {cs.Energy}/{cs.MaxEnergy}  星 {cs.Stars}";
                }
                catch
                {
                    res = cs == null ? "不在战斗中" : $"能量 {cs.Energy}";
                }

                _vitals.Text = hp + "    " + res;
            }

            if (_status != null)
            {
                NetScreenType screen = input != null ? input.GetScreenType(_target.NetId) : NetScreenType.None;
                string screenText = screen == NetScreenType.Room && cs != null ? "战斗中" : ScreenName(screen);
                int relics = SafeCount(() => _target.Relics.Count);
                int potions = SafeCount(() => System.Linq.Enumerable.Count(_target.Potions));
                string piles = cs == null
                    ? string.Empty
                    : $"手牌 {cs.Hand.Cards.Count} 抽 {cs.DrawPile.Cards.Count} 弃 {cs.DiscardPile.Cards.Count}";
                _status.Text = $"界面 {screenText}" + (piles.Length > 0 ? "    " + piles : string.Empty) + $"    遗物 {relics} 药水 {potions}";
            }

            if (_hoverLine != null)
            {
                if (input == null)
                {
                    _hoverLine.Text = "悬停 -";
                }
                else
                {
                    HoveredModelData hover = input.GetHoveredModelData(_target.NetId);

                // 卡面费用颜色只在 UpdateVisuals 时算一次，能量变了不会自动重算，
                // 会出现「能量已经回来，卡还是红的」。
                // 只同步悬停，不同步「已选中」，所以用悬停高亮复刻选择时的视觉反馈。
                try
                {
                    ModelId? hoveredId = hover.hoveredModelId;
                    HandMirror.HighlightHovered(hoveredId);
                    SpectateChoice.HighlightHovered(hoveredId);
                }
                catch (Exception e)
                {
                    ModEntry.Log.Warn($"[{ModEntry.ModId}] hover highlight tick failed: {e.Message}");
                }
                    bool targeting = input.GetIsTargeting(_target.NetId);
                    _hoverLine.Text = (targeting ? "瞄准 " : "悬停 ") + HoverText(hover);
                }
            }

            if (_emptyHand != null)
            {
                _emptyHand.Visible = cs == null;
            }

    }

    private static int SafeCount(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static void RebuildHand(PlayerCombatState cs)
    {
        if (_handRow == null)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (CardModel card in cs.Hand.Cards)
        {
            sb.Append(card.Id.Entry).Append(',');
        }

        string key = sb.ToString();
        if (key == _handKey)
        {
            return;
        }

        _handKey = key;
        FreeCards();

        foreach (CardModel card in cs.Hand.Cards)
        {
            if (_cards.Count >= MaxCardNodes)
            {
                break;
            }

            if (TryAddCardNode(card))
            {
                continue;
            }

            var tile = new PanelContainer { CustomMinimumSize = new Vector2(72f, 44f) };
            var tileLabel = new Label
            {
                Text = card.Title,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            tileLabel.AddThemeFontSizeOverride("font_size", 10);
            tile.AddChild(tileLabel);
            _handRow.AddChild(tile);
            _slots.Add(tile);
        }
    }

    private static bool TryAddCardNode(CardModel card)
    {
        if (!_cardPoolWorks || _handRow == null)
        {
            return false;
        }

        try
        {
            NCard? node = NCard.Create(card);
            if (node == null)
            {
                return false;
            }

            var slot = new Control
            {
                CustomMinimumSize = new Vector2(88f, 122f),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            node.MouseFilter = Control.MouseFilterEnum.Ignore;
            node.Size = NCard.defaultSize;
            node.Scale = new Vector2(CardScale, CardScale);
            node.Position = Vector2.Zero;
            slot.AddChild(node);
            _handRow.AddChild(slot);

            // NCard.Create 只设置 Model，文本/费用角标要靠 UpdateVisuals 生成。
            node.Visibility = ModelVisibility.Visible;
            node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);

            _cards.Add(node);
            _slots.Add(slot);
            return true;
        }
        catch (Exception e)
        {
            _cardPoolWorks = false;
            ModEntry.Log.Warn($"[{ModEntry.ModId}] card node unavailable, falling back to text: {e.Message}");
            return false;
        }
    }

    private static void FreeCards()
    {
        foreach (NCard card in _cards)
        {
            try
            {
                if (!GodotObject.IsInstanceValid(card))
                {
                    continue;
                }

                card.GetParent()?.RemoveChild(card);
                NodePool.Free(card);
            }
            catch (Exception e)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] free card: {e.Message}");
                try
                {
                    card.QueueFree();
                }
                catch
                {
                    // ignored
                }
            }
        }

        _cards.Clear();

        foreach (Control slot in _slots)
        {
            try
            {
                if (GodotObject.IsInstanceValid(slot))
                {
                    slot.QueueFree();
                }
            }
            catch
            {
                // ignored
            }
        }

        _slots.Clear();
    }

    private static string HoverText(HoveredModelData hover)
    {
        if (hover.hoveredModelId == null)
        {
            return "-";
        }

        AbstractModel? model = ModelDb.GetByIdOrNull<AbstractModel>(hover.hoveredModelId);
        if (model is CardModel card)
        {
            return card.Title;
        }

        if (model is RelicModel relic)
        {
            return relic.Title.GetFormattedText();
        }

        if (model is PotionModel potion)
        {
            return potion.Title.GetFormattedText();
        }

        return hover.hoveredModelId.Entry;
    }

    private static string SafePlayerName(ulong netId)
    {
        try
        {
            return PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, netId);
        }
        catch
        {
            return netId.ToString();
        }
    }

    private static string SafeCharacter(Player player)
    {
        try
        {
            return player.Character.Title.GetFormattedText();
        }
        catch
        {
            return "-";
        }
    }

    private static string ScreenName(NetScreenType screen)
    {
        return screen switch
        {
            NetScreenType.Room => "房间",
            NetScreenType.Map => "地图",
            NetScreenType.Rewards => "奖励结算",
            NetScreenType.CardSelection => "选牌",
            NetScreenType.DeckView => "看牌库",
            NetScreenType.CardPile => "看牌堆",
            NetScreenType.SimpleCardsView => "看牌",
            NetScreenType.SharedRelicPicking => "分遗物",
            NetScreenType.Settings => "设置",
            NetScreenType.Compendium => "图鉴",
            NetScreenType.PauseMenu => "暂停菜单",
            NetScreenType.GameOver => "结算",
            NetScreenType.Feedback => "反馈",
            NetScreenType.RemotePlayerExpandedState => "看你的牌库",
            _ => screen.ToString()
        };
    }
}