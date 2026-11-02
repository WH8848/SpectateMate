using System;
using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

using static SpectateMate.Ui.ReflectionUtil;

namespace SpectateMate.Ui;

/// <summary>
/// A1 + A2: make the vanilla top bar / relic bar / potion bar / combat widgets
/// render the teammate instead of the local player.
///
/// Nothing is re-implemented: the widgets keep running their own logic, we only
/// redirect "who is the local player" while they re-initialise.
/// </summary>
internal static class SpectateUi
{
    private static NGlobalUi? _globalUi;
    private static RunState? _runState;
    private static NCombatUi? _combatUi;
    private static Player? _localPlayer;

    public static bool IsAvailable => _globalUi != null && _runState != null;

    public static NCombatUi? CombatUi
    {
        get
        {
            if (_combatUi != null && GodotObject.IsInstanceValid(_combatUi))
            {
                return _combatUi;
            }

            // Fallback: the Activate patch may not have run (e.g. spectating began
            // mid-combat), but the room always knows its current combat UI.
            try
            {
                return MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.Ui;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void CaptureGlobalUi(NGlobalUi globalUi, RunState runState)
    {
        _globalUi = globalUi;
        _runState = runState;
        try
        {
            // We are inside the patch (LocalContext is redirected), so resolve the
            // real local player from the net service instead.
            _localPlayer = runState.GetPlayer(RunManager.Instance.NetService.NetId);
        }
        catch
        {
            _localPlayer = null;
        }
    }

    public static void CaptureCombatUi(NCombatUi combatUi)
    {
        _combatUi = combatUi;

        try
        {
            if (combatUi?.Hand is { } hand)
            {
                _hand = hand;
            }
        }
        catch
        {
            // ignored
        }
    }

    private static NPlayerHand? _hand;

    /// <summary>
    /// The vanilla hand node. Cached on capture because CombatUi can briefly be
    /// unreachable across turn/combat boundaries, and losing it used to silently
    /// disable the whole hand mirror.
    /// </summary>
    public static NPlayerHand? Hand
    {
        get
        {
            if (_hand != null && GodotObject.IsInstanceValid(_hand))
            {
                return _hand;
            }

            try
            {
                return CombatUi?.Hand;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void Reset()
    {
        _globalUi = null;
        _runState = null;
        _combatUi = null;
        _hand = null;
        _localPlayer = null;
        _applied = false;
        _appliedNetId = null;
        SavedStates.Clear();
    }

    /// <summary>Points the vanilla UI at <paramref name="target"/> (null = back to local).</summary>
    private static bool _applied;
    private static ulong? _appliedNetId;

    public static void Apply(Player? target)
    {
        ulong? targetId = null;
        try
        {
            targetId = target?.NetId;
        }
        catch
        {
            // disposed player
        }

        if (_applied && _appliedNetId == targetId)
        {
            DisplayedPlayer.SetTarget(target);
            return;
        }

        _applied = true;
        _appliedNetId = targetId;

        // Takes over vanilla widgets; a failure here must NEVER break the overlay.
        try
        {
            DisplayedPlayer.SetTarget(target);

            Player? shown = target ?? _localPlayer;
            if (shown == null || !IsAvailable)
            {
                return;
            }

            DisplayedPlayer.With(shown, () => Rebuild(shown));
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] ui takeover skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Rebuild(Player shown)
    {
        RebuildTopBar();
        RebuildRelics();
        RebuildCombatUi(shown);
    }

    private static void RebuildTopBar()
    {
        Step("topbar", () =>
        {
            if (_globalUi?.TopBar is not { } topBar || _runState == null)
            {
                return;
            }

            Player? shown = DisplayedPlayer.Target ?? _localPlayer;
            if (shown == null)
            {
                return;
            }

            // NEVER call topBar.Initialize(): it re-adds modifier nodes, re-subscribes
            // RelicObtained, and its Portrait.Initialize moves the SHARED
            // Character.Icon into our portrait -> two avatars stacked on top of
            // each other and the teammate's own avatar left empty.
            Step("gold", () => RebindGold(topBar.Gold, shown));
            Step("hp", () => RebindHp(topBar.Hp, shown));
            Step("deck", () => RebindDeck(topBar.Deck, shown));

            Step("potion", () =>
            {
                if (topBar.PotionContainer == null)
                {
                    return;
                }

                ClearPotionContainer(topBar.PotionContainer);
                topBar.PotionContainer.Initialize(_runState);
            });
        });
    }

    /// <summary>
    /// Re-points a pile button at the teammate's pile. Initialize() only runs at
    /// combat start, so when spectating begins mid-combat each button still holds
    /// a reference to YOUR pile — its counters and contents stay yours.
    /// </summary>
    private static void RebindPileButton(NCombatCardPile? pile, Player player)
    {
        if (pile == null || !GodotObject.IsInstanceValid(pile))
        {
            return;
        }

        CardPile? current = Peek(pile, "_pile") as CardPile;
        PileType type = current?.Type ?? PileType.Discard;
        CardPile next;

        try
        {
            next = type.GetPile(player);
        }
        catch
        {
            return;
        }

        if (current == next)
        {
            return;
        }

        MethodInfo? onAdd = Method(pile.GetType(), "AddCard");
        MethodInfo? onRemove = Method(typeof(NCombatCardPile), "RemoveCard");

        Delegate? addHandler = null;
        Delegate? removeHandler = null;

        EventArgsPair(ref addHandler, ref removeHandler, pile, current, onAdd, onRemove);

        // Unbind from the old pile.
        EventInfo? evtAdd = typeof(CardPile).GetEvent("CardAddFinished");
        EventInfo? evtRemove = typeof(CardPile).GetEvent("CardRemoveFinished");

        if (current != null)
        {
            if (evtAdd != null && addHandler != null) evtAdd.RemoveEventHandler(current, addHandler);
            if (evtRemove != null && removeHandler != null) evtRemove.RemoveEventHandler(current, removeHandler);
        }

        Set(pile, "_pile", next);

        if (evtAdd != null && addHandler != null) evtAdd.AddEventHandler(next, addHandler);
        if (evtRemove != null && removeHandler != null) evtRemove.AddEventHandler(next, removeHandler);

        Set(pile, "_currentCount", next.Cards.Count);
        if (Peek(pile, "_countLabel") is MegaLabel label)
        {
            label.SetTextAutoSize(next.Cards.Count.ToString());
        }

        ModEntry.Log.Info(
            $"[{ModEntry.ModId}] pile {type} -> netId={player.NetId} ({next.Cards.Count} cards)");
    }

    private static void EventArgsPair(
        ref Delegate? addHandler,
        ref Delegate? removeHandler,
        NCombatCardPile pile,
        CardPile? current,
        MethodInfo? onAdd,
        MethodInfo? onRemove)
    {
        try
        {
            EventInfo? evtAdd = typeof(CardPile).GetEvent("CardAddFinished");
            EventInfo? evtRemove = typeof(CardPile).GetEvent("CardRemoveFinished");
            if (evtAdd == null || evtRemove == null || onAdd == null || onRemove == null)
            {
                return;
            }

            addHandler = Delegate.CreateDelegate(evtAdd.EventHandlerType!, pile, onAdd);
            removeHandler = Delegate.CreateDelegate(evtRemove.EventHandlerType!, pile, onRemove);
        }
        catch
        {
            addHandler = null;
            removeHandler = null;
        }
    }

    private static void SyncPileCount(NCombatCardPile? pile, int count)
    {
        if (pile == null || !GodotObject.IsInstanceValid(pile))
        {
            return;
        }

        if (Read<int>(pile, "_currentCount") == count)
        {
            return;
        }

        Set(pile, "_currentCount", count);

        if (Peek(pile, "_countLabel") is MegaLabel label)
        {
            label.SetTextAutoSize(count.ToString());
        }
    }

    private static void RebindGold(NTopBarGold? gold, Player player)
    {
        if (gold == null || !GodotObject.IsInstanceValid(gold))
        {
            return;
        }

        EventInfo? evt = typeof(Player).GetEvent("GoldChanged");
        MethodInfo? method = typeof(NTopBarGold).GetMethod(
            "UpdateGold",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (evt == null || method == null)
        {
            gold.Initialize(player);
            return;
        }

        Delegate? handler = Delegate.CreateDelegate(evt.EventHandlerType!, gold, method);
        if (handler == null)
        {
            gold.Initialize(player);
            return;
        }

        Player? previous = Read<Player?>(gold, "_player");
        if (previous != null)
        {
            evt.RemoveEventHandler(previous, handler);
        }

        Set(gold, "_player", player);
        evt.AddEventHandler(player, handler);
        Set(gold, "_currentGold", player.Gold);

        if (Peek(gold, "_goldLabel") is MegaLabel label)
        {
            label.SetTextAutoSize($"{player.Gold}");
        }
    }

    /// <summary>
    /// Deck button: shows HIS deck size, and because `_player` is re-pointed,
    /// clicking it opens NDeckViewScreen for him instead of for us.
    /// </summary>
    private static void RebindDeck(NTopBarDeckButton? deck, Player player)
    {
        if (deck == null || !GodotObject.IsInstanceValid(deck))
        {
            return;
        }

        EventInfo? onAdd = typeof(CardPile).GetEvent("CardAddFinished");
        EventInfo? onRemove = typeof(CardPile).GetEvent("CardRemoveFinished");
        MethodInfo? changed = typeof(NTopBarDeckButton).GetMethod(
            "OnPileContentsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (onAdd == null || onRemove == null || changed == null)
        {
            deck.Initialize(player);
            return;
        }

        Delegate? handler = Delegate.CreateDelegate(onAdd.EventHandlerType!, deck, changed);
        if (handler == null)
        {
            deck.Initialize(player);
            return;
        }

        if (Peek(deck, "_pile") is CardPile oldPile)
        {
            onAdd.RemoveEventHandler(oldPile, handler);
            onRemove.RemoveEventHandler(oldPile, handler);
        }

        CardPile newPile = PileType.Deck.GetPile(player);
        Set(deck, "_player", player);
        Set(deck, "_pile", newPile);
        onAdd.AddEventHandler(newPile, handler);
        onRemove.AddEventHandler(newPile, handler);

        InvokeMethod(deck, "OnPileContentsChanged");
    }

    private static void RebindHp(NTopBarHp? hp, Player player)
    {
        if (hp == null || !GodotObject.IsInstanceValid(hp))
        {
            return;
        }

        EventInfo? onCurrent = typeof(Creature).GetEvent("CurrentHpChanged");
        EventInfo? onMax = typeof(Creature).GetEvent("MaxHpChanged");
        MethodInfo? method = typeof(NTopBarHp).GetMethod(
            "UpdateHealth",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (onCurrent == null || onMax == null || method == null)
        {
            hp.Initialize(player);
            return;
        }

        Delegate? handler = Delegate.CreateDelegate(onCurrent.EventHandlerType!, hp, method);
        if (handler == null)
        {
            hp.Initialize(player);
            return;
        }

        Player? previous = Read<Player?>(hp, "_player");
        if (previous?.Creature is { } oldCreature)
        {
            onCurrent.RemoveEventHandler(oldCreature, handler);
            onMax.RemoveEventHandler(oldCreature, handler);
        }

        Set(hp, "_player", player);

        if (player.Creature is { } creature)
        {
            onCurrent.AddEventHandler(creature, handler);
            onMax.AddEventHandler(creature, handler);

            if (Peek(hp, "_hpLabel") is MegaLabel label)
            {
                label.SetTextAutoSize($"{creature.CurrentHp}/{creature.MaxHp}");
            }
        }
    }

    private static void RebuildRelics()
    {
        Step("relics", () =>
        {
            if (_globalUi?.RelicInventory is not { } inventory || _runState == null)
            {
                return;
            }

            Step("relic-clear", () => ClearRelicInventory(inventory));
            inventory.Initialize(_runState);
        });
    }

    private static void RebuildCombatUi(Player shown)
    {
        Step("combat", () =>
        {
            NCombatUi? ui = CombatUi;
            if (ui == null)
            {
                return;
            }

            Step("piles", () =>
            {
                if (Find<NCombatPilesContainer>(ui, "_combatPilesContainer") is { } piles)
                {
                    RebindPileButton(piles.DrawPile, shown);
                    RebindPileButton(piles.DiscardPile, shown);
                    RebindPileButton(piles.ExhaustPile, shown);
                }
            });

            Step("stars", () =>
            {
                if (Find<NStarCounter>(ui, "_starCounter") is { } stars)
                {
                    stars.Initialize(shown);
                }
            });

            Step("energy", () => RebuildEnergyCounter(shown));
        });
    }

    private static void RebuildEnergyCounter(Player shown)
    {
        NCombatUi? ui = CombatUi;
        if (ui == null)
        {
            return;
        }

        NEnergyCounter? current = Find<NEnergyCounter>(ui, "_energyCounter");
        if (current == null || !GodotObject.IsInstanceValid(current))
        {
            // No orb yet (not in combat). The Activate patch will build it correctly.
            return;
        }

        // Rebuild when the character uses a different orb scene, otherwise just re-point it.
        string? wanted = SafePath(shown);
        string? actual = SafePath(Read<Player?>(current, "_player"));
        if (wanted == actual)
        {
            RebindEnergyCounter(current, shown);
            return;
        }

        NEnergyCounter? fresh = NEnergyCounter.Create(shown);
        if (fresh == null)
        {
            RebindEnergyCounter(current, shown);
            return;
        }

        Node? container = ui.EnergyCounterContainer;
        if (container == null)
        {
            fresh.QueueFree();
            return;
        }

        container.AddChild(fresh);
        if (Find<NStarCounter>(ui, "_starCounter") is { } stars && GodotObject.IsInstanceValid(stars))
        {
            stars.Reparent(fresh, true);
        }

        Set(ui, "_energyCounter", fresh);
        current.QueueFree();
    }

    /// <summary>
    /// Re-points an existing orb: detach the old EnergyChanged handler, swap the
    /// player, attach the new handler and force a label refresh. Without this the
    /// number keeps showing the previously spectated player's energy.
    /// </summary>
    private static void RebindEnergyCounter(NEnergyCounter counter, Player player)
    {
        if (EnergyChangedEvent == null)
        {
            Set(counter, "_player", player);
            InvokeMethod(counter, "RefreshLabel");
            return;
        }

        Player? previous = Read<Player?>(counter, "_player");
        Action<int, int>? handler = CreateEnergyHandler(counter);
        if (handler != null && previous?.PlayerCombatState is { } oldState)
        {
            EnergyChangedEvent.RemoveEventHandler(oldState, handler);
        }

        Set(counter, "_player", player);

        if (handler != null && player.PlayerCombatState is { } newState)
        {
            EnergyChangedEvent.AddEventHandler(newState, handler);
        }

        InvokeMethod(counter, "RefreshLabel");
    }

    private static readonly EventInfo? EnergyChangedEvent =
        typeof(PlayerCombatState).GetEvent("EnergyChanged");

    private static Action<int, int>? CreateEnergyHandler(NEnergyCounter counter)
    {
        try
        {
            MethodInfo? method = typeof(NEnergyCounter).GetMethod(
                "OnEnergyChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                return null;
            }

            return (Action<int, int>?)Delegate.CreateDelegate(typeof(Action<int, int>), counter, method);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Blocks (or restores) local interaction while spectating.
    /// Only the HAND AREA and the END TURN button are blocked — piles, relics and
    /// potions stay clickable so the teammate's piles can be inspected.
    /// </summary>
    public static void SetInteraction(bool enabled)
    {
        if (enabled)
        {
            FreeHandBlocker();
            RestoreInteraction();
            return;
        }

        // 不再使用遮挡层：它会吃掉 hover，并把自己的 z 序压到提示框之上。
        // 出牌由 HandPlayBlockPatch（StartCardPlay prefix）从代码层拦死。
        Step("hand-keys", () => BlockInputFlags(Hand));
        Step("end-turn", () => Block(_combatUi?.EndTurnButton));
        Step("potions", () => Block(_globalUi?.TopBar?.PotionContainer));
    }

    private static Control? _handBlocker;

    private static void InstallHandBlocker()
    {
        NPlayerHand? hand = Hand;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] hand blocker skipped: hand unavailable");
            return;
        }

        if (_handBlocker != null && GodotObject.IsInstanceValid(_handBlocker))
        {
            return;
        }

        // Must live in CardHolderContainer space: NPlayerHand itself is only a thin
        // bottom strip, so a full-rect blocker there left the fan clickable.
        Control? container = null;
        try
        {
            container = hand.CardHolderContainer;
        }
        catch
        {
            // ignored
        }

        container ??= hand;

        var blocker = new Control
        {
            Name = "SpectateMateHandBlocker",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        container.AddChild(blocker);

        // Centre-bottom band only: the left/right corners hold the draw / discard /
        // exhaust pile buttons and must stay clickable.
        blocker.AnchorLeft = 0.2f;
        blocker.AnchorRight = 0.8f;
        blocker.AnchorTop = 1f;
        blocker.AnchorBottom = 1f;
        blocker.OffsetLeft = 0f;
        blocker.OffsetRight = 0f;
        blocker.OffsetTop = -520f;
        blocker.OffsetBottom = 0f;
        blocker.ZIndex = 4;

        _handBlocker = blocker;
    }

    private static void FreeHandBlocker()
    {
        try
        {
            if (_handBlocker != null && GodotObject.IsInstanceValid(_handBlocker))
            {
                _handBlocker.GetParent()?.RemoveChild(_handBlocker);
                _handBlocker.QueueFree();
            }
        }
        catch
        {
            // ignored
        }

        _handBlocker = null;
    }

    /// <summary>
    /// Disables input callbacks only (keeps _Process running so the mirror still
    /// animates). Numeric hotkeys live in the hand's unhandled-key handler.
    /// </summary>
    private static void BlockInputFlags(Node? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        if (!SavedStates.ContainsKey(node))
        {
            SavedStates[node] = Capture(node);
        }

        node.SetProcessInput(false);
        node.SetProcessUnhandledInput(false);
        node.SetProcessUnhandledKeyInput(false);
    }

    private static void Block(Node? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        if (!SavedStates.ContainsKey(node))
        {
            SavedStates[node] = Capture(node);
        }

        node.ProcessMode = Node.ProcessModeEnum.Disabled;
    }

    /// <summary>
    /// 观战期间手牌的禁用状态跟随「被观战的人」，而不是跟随自己。
    ///
    /// 自己结束回合时游戏会调用 NPlayerHand.AnimDisable（下移 100px + 变灰）。
    /// 但此时我们显示的是队友的手牌，他还在行动，不该被禁用。
    /// 这里每拍按他的 PlayerTurnPhase 对齐状态。
    /// </summary>
    public static void SyncHandEnabled(Player target)
    {
        Step("hand-enabled", () =>
        {
            NPlayerHand? hand = Hand;
            if (hand == null || !GodotObject.IsInstanceValid(hand))
            {
                return;
            }

            PlayerTurnPhase phase;
            try
            {
                phase = target.PlayerCombatState?.Phase ?? PlayerTurnPhase.None;
            }
            catch
            {
                phase = PlayerTurnPhase.None;
            }

            // 他还在自己的回合里 → 手牌保持正常显示
            bool active = phase is PlayerTurnPhase.Play
                or PlayerTurnPhase.Start
                or PlayerTurnPhase.AutoPrePlay;

            InvokeMethod(hand, active ? "AnimEnable" : "AnimDisable");
        });
    }

    /// <summary>
    /// 退出观战后把状态还回自己：该灰就灰、该下移就下移。
    /// </summary>
    public static void RestoreOwnHandState()
    {
        Step("hand-state-restore", () =>
        {
            NPlayerHand? hand = Hand;
            if (hand == null || !GodotObject.IsInstanceValid(hand))
            {
                return;
            }

            // 只在确定「现在轮到我出牌」时恢复，其余情况交给游戏自己管理。
            // 原先用 !PlayerActionsDisabled 判断，但它是全局标志——队友回合时
            // 也可能是 false，会把我的手牌错误地照亮，或者反过来留成灰的。
            bool myTurn = false;

            try
            {
                Player? me = _runState != null ? LocalContext.GetMe(_runState.Players) : null;
                myTurn = me?.PlayerCombatState?.Phase == PlayerTurnPhase.Play;
            }
            catch
            {
                myTurn = false;
            }

            if (myTurn)
            {
                InvokeMethod(hand, "AnimEnable");
            }
        });
    }

    /// <summary>
    /// Per-tick refresh of the values that do not emit change events for a remote
    /// player: energy orb label and star count.
    /// </summary>
    public static void TickDisplayed()
    {
        if (!DisplayedPlayer.IsActive)
        {
            return;
        }

        NCombatUi? ui = CombatUi;
        if (ui == null || !GodotObject.IsInstanceValid(ui))
        {
            return;
        }

        Player? shown = DisplayedPlayer.Target;
        if (shown == null)
        {
            return;
        }

        Step("tick-energy", () =>
        {
            // RefreshLabel dereferences _player.PlayerCombatState; outside combat
            // that is null and the call throws every tick.
            PlayerCombatState? state = null;
            try
            {
                state = shown.PlayerCombatState;
            }
            catch
            {
                // player disposed
            }

            if (state == null)
            {
                return;
            }

            if (Find<NEnergyCounter>(ui, "_energyCounter") is { } energy && GodotObject.IsInstanceValid(energy))
            {
                Set(energy, "_player", shown);
                InvokeMethod(energy, "RefreshLabel");
            }
        });

        Step("tick-stars", () =>
        {
            if (Find<NStarCounter>(ui, "_starCounter") is { } stars && GodotObject.IsInstanceValid(stars))
            {
                Set(stars, "_player", shown);
                int count = shown.PlayerCombatState?.Stars ?? 0;
                InvokeMethod(stars, "SetStarCountText", count);
            }
        });

        Step("tick-piles", () =>
        {
            // Pile counters update from CardAddFinished/CardRemoveFinished events
            // with +1/-1 deltas; a single missed or duplicated event drifts them.
            // Force the exact numbers instead.
            PlayerCombatState? state = null;
            try
            {
                state = shown.PlayerCombatState;
            }
            catch
            {
                // disposed
            }

            if (state == null)
            {
                return;
            }

            if (Find<NCombatPilesContainer>(ui, "_combatPilesContainer") is not { } piles)
            {
                return;
            }

            SyncPileCount(piles.DrawPile, state.DrawPile.Cards.Count);
            SyncPileCount(piles.DiscardPile, state.DiscardPile.Cards.Count);
            SyncPileCount(piles.ExhaustPile, state.ExhaustPile.Cards.Count);
        });
    }

    /// <summary>
    /// Restores exactly what was captured — never assumes "true". Turning a node's
    /// input processing back on when it never had it breaks target selection.
    /// </summary>
    private static void RestoreInteraction()
    {
        foreach (KeyValuePair<Node, NodeState> pair in SavedStates)
        {
            Node node = pair.Key;
            NodeState state = pair.Value;

            try
            {
                if (node == null || !GodotObject.IsInstanceValid(node))
                {
                    continue;
                }

                node.ProcessMode = state.ProcessMode;
                node.SetProcessInput(state.ProcessInput);
                node.SetProcessUnhandledInput(state.ProcessUnhandledInput);
                node.SetProcessUnhandledKeyInput(state.ProcessUnhandledKeyInput);
                if (state.IsControl && node is Control control)
                {
                    control.MouseFilter = state.MouseFilter;
                }
            }
            catch
            {
                // node went away with the combat
            }
        }

        SavedStates.Clear();
    }

    private sealed class NodeState
    {
        public Node.ProcessModeEnum ProcessMode;
        public bool ProcessInput;
        public bool ProcessUnhandledInput;
        public bool ProcessUnhandledKeyInput;
        public bool IsControl;
        public Control.MouseFilterEnum MouseFilter;
    }

    private static readonly Dictionary<Node, NodeState> SavedStates = new();

    private static NodeState Capture(Node node)
    {
        return new NodeState
        {
            ProcessMode = node.ProcessMode,
            ProcessInput = node.IsProcessingInput(),
            ProcessUnhandledInput = node.IsProcessingUnhandledInput(),
            ProcessUnhandledKeyInput = node.IsProcessingUnhandledKeyInput(),
            IsControl = node is Control,
            MouseFilter = node is Control control ? control.MouseFilter : Control.MouseFilterEnum.Stop,
        };
    }

    private static string? SafePath(Player? player)
    {
        try
        {
            return player?.Character?.EnergyCounterPath;
        }
        catch
        {
            return null;
        }
    }

    // ---- vanilla-node clearing (private members; each step is guarded) ----

    private static void ClearRelicInventory(NRelicInventory inventory)
    {
        Invoke(inventory, "DisconnectPlayerEvents");
        if (Read<IList?>(inventory, "_relicNodes") is { } nodes)
        {
            FreeAll(nodes);
            nodes.Clear();
        }

        Set(inventory, "_player", null);
    }

    private static void ClearPotionContainer(NPotionContainer container)
    {
        Invoke(container, "DisconnectPlayerEvents");

        if (Read<IList?>(container, "_holders") is { } holders)
        {
            FreeAll(holders);
            holders.Clear();
        }

        if (Read<Node?>(container, "_potionHolders") is { } holderRoot && GodotObject.IsInstanceValid(holderRoot))
        {
            foreach (Node child in holderRoot.GetChildren())
            {
                holderRoot.RemoveChild(child);
                child.QueueFree();
            }
        }

        Set(container, "_focusedHolder", null);
        Set(container, "_player", null);
    }

    private static void FreeAll(IList list)
    {
        var snapshot = new List<object?>(list.Count);
        foreach (object? item in list)
        {
            snapshot.Add(item);
        }

        foreach (object? item in snapshot)
        {
            if (item is Node node && GodotObject.IsInstanceValid(node))
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }
        }
    }

    // ---- tiny reflection helpers ----

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] ui step '{name}' failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
