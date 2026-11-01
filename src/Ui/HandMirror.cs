using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Pooling;

namespace SpectateMate.Ui;

/// <summary>
/// B1: mirror the teammate's hand into the vanilla hand bar without taking
/// ownership of vanilla nodes.
///
/// Each mirrored card is wrapped in a real <see cref="NHandCardHolder"/> created
/// through its public factory. That holder carries the entire vanilla animation
/// behaviour (position/angle/scale lerp, hover lift, press feedback), so the fan
/// moves exactly like the real hand instead of being tweened by us.
/// Holders live in our own layer so they never pollute NPlayerHand.ActiveHolders.
/// </summary>
internal static class HandMirror
{
    /// <summary>Flip to false to disable the whole feature without touching anything else.</summary>
    public static readonly bool Enabled = true;

    private const float SpawnOffsetY = 180f;
    private const float PlayedFlightSeconds = 0.32f;

    private static readonly List<NHandCardHolder> Holders = new();
    private static readonly List<NHandCardHolder> Detached = new();
    private static readonly Dictionary<NCard, Tween> FlightTweens = new();

    private static Control? _layer;
    private static NPlayerHand? _lastHand;
    private static bool _vanillaHidden;
    private static Color _savedModulate = Colors.White;
    private static StringName _key = string.Empty;
    private static bool _active;
    private static string _lastDiag = string.Empty;

    public static bool IsShowing => _active;

    /// <summary>
    /// The holders the vanilla layout should position. Empty when not spectating,
    /// which makes ActiveHoldersPatch fall through to the original getter.
    /// </summary>
    public static IReadOnlyList<NHandCardHolder> MirrorHolders =>
        _active && Holders.Count > 0 ? Holders : Array.Empty<NHandCardHolder>();

    private static readonly System.Reflection.FieldInfo? FocusedHolderField =
        typeof(NPlayerHand).GetField(
            "<FocusedHolder>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.FieldInfo? LastFocusedIdxField =
        typeof(NPlayerHand).GetField(
            "_lastFocusedHolderIdx",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void Diag(string message)
    {
        if (_lastDiag == message)
        {
            return;
        }

        _lastDiag = message;
        ModEntry.Log.Info($"[{ModEntry.ModId}] mirror: {message}");
    }

    public static void Sync(NPlayerHand? hand, IReadOnlyList<CardModel>? cards)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (hand == null || !GodotObject.IsInstanceValid(hand))
            {
                hand = _lastHand;

                if (hand == null || !GodotObject.IsInstanceValid(hand))
                {
                    Diag("hand unavailable");
                    Hide(null, restoreVanilla: false);
                    return;
                }
            }
            else
            {
                _lastHand = hand;
            }

            if (cards == null)
            {
                Hide(hand, restoreVanilla: false);
                return;
            }

            StringName key = BuildKey(cards);
            if (key != _key)
            {
                Rebuild(hand, cards, key);
            }
            else
            {
                // key 相同也要保持隐藏：队友手牌为 0 时 key 是空串，而 Close()
                // 也会把 _key 清成空串 —— 之前 _active 为 false 就整段跳过了，
                // 导致重进观战直接露出本地手牌。
                HideVanillaNodes(hand);
                _active = true;
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Error($"[{ModEntry.ModId}] hand mirror failed: {e}");
            Hide(hand);
        }
    }

    /// <param name="restoreVanilla">
    /// True when leaving spectate (give the real cards back). False while merely
    /// emptied (teammate played everything) — restoring there flashed OUR cards.
    /// </param>
    public static void Hide(NPlayerHand? hand, bool restoreVanilla = true)
    {
        hand ??= _lastHand;

        try
        {
            if (hand != null && GodotObject.IsInstanceValid(hand))
            {
                if (restoreVanilla)
                {
                    ShowVanillaNodes(hand);
                }
                else
                {
                    // Still spectating: make sure nothing leaks back on screen.
                    HideVanillaNodes(hand);
                }
            }
        }
        catch
        {
            // ignored
        }

        foreach (NHandCardHolder flying in Detached.ToArray())
        {
            DestroyHolder(flying);
        }

        Detached.Clear();
        FreeHolders();

        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            _layer.GetParent()?.RemoveChild(_layer);
            _layer.QueueFree();
        }

        _layer = null;
        _key = string.Empty;
        _active = false;
    }

    private static void Rebuild(NPlayerHand hand, IReadOnlyList<CardModel> cards, StringName key)
    {
        HideVanillaNodes(hand);

        Control layer = EnsureLayer(hand);
        int count = Math.Min(cards.Count, 10);

        // 按 CardModel 实例匹配已有 holder：还在手里的牌保留原节点，
        // 这样它们的位移动画由 RefreshLayout 平滑完成，而不是被销毁重建。
        var existing = new Dictionary<CardModel, NHandCardHolder>(Holders.Count);
        foreach (NHandCardHolder holder in Holders)
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                continue;
            }

            if (holder.CardNode?.Model is { } model)
            {
                existing[model] = holder;
            }
        }

        var next = new List<NHandCardHolder>(count);

        for (int i = 0; i < count; i++)
        {
            CardModel model = cards[i];

            if (!existing.TryGetValue(model, out NHandCardHolder? holder) || holder == null)
            {
                holder = CreateHolder(hand, layer, model);
                if (holder == null)
                {
                    continue;
                }
            }
            else
            {
                existing.Remove(model);
            }

            next.Add(holder);
        }

        // 仍留在 existing 里的，就是他打出去的那几张。
        foreach (NHandCardHolder played in existing.Values)
        {
            AnimatePlayedCard(played);
        }

        Holders.Clear();
        Holders.AddRange(next);

        _key = key;
        _active = true;

        // 交给原版 RefreshLayout 重新排版（扇形、悬停抬起、相邻让位、焦点）。
        hand.ForceRefreshCardIndices();

        Control? ctr = ContainerOf(hand);
        Diag(
            $"showing {Holders.Count} cards | layer={_layer?.Position}/{_layer?.Size} " +
            $"ctr={ctr?.Position}/{ctr?.Size}");
    }

    private static NHandCardHolder? CreateHolder(NPlayerHand hand, Control layer, CardModel model)
    {
        NCard? card = NCard.Create(model);
        if (card == null)
        {
            return null;
        }

        card.Visibility = ModelVisibility.Visible;
        card.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);

        NHandCardHolder holder = NHandCardHolder.Create(card, hand);
        holder.MouseFilter = Control.MouseFilterEnum.Stop;
        layer.AddChild(holder);
        holder.SetClickable(true);

        // _Ready 会关闭 Hitbox，只有 AnimPosition 在接近目标时才重新开启。
        // 这里显式打开，否则镜像卡收不到悬停。
        holder.Hitbox.SetEnabled(true);
        holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;

        card.Visible = true;
        card.Modulate = Colors.White;
        card.Scale = Vector2.One;
        card.Position = Vector2.Zero;
        holder.UpdateCard();

        holder.Connect(
            NHandCardHolder.SignalName.HolderFocused,
            Callable.From<NHandCardHolder>(OnHolderFocused));
        holder.Connect(
            NHandCardHolder.SignalName.HolderUnfocused,
            Callable.From<NHandCardHolder>(OnHolderUnfocused));

        return holder;
    }

    /// <summary>
    /// 他打出的牌：立刻脱离布局（其余牌马上回流），卡面飞向屏幕中心并淡出。
    /// 补间作用在 CardNode 上而不是 holder，避开 holder 自身的 AnimPosition 协程。
    /// </summary>
    private static void AnimatePlayedCard(NHandCardHolder holder)
    {
        try
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                return;
            }

            NCard? card = holder.CardNode;
            Detached.Add(holder);

            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                DestroyHolder(holder);
                return;
            }

            Vector2 centre = card.GetViewportRect().Size * 0.5f;

            Tween tween = card.CreateTween();
            tween.SetParallel(true);
            tween.TweenProperty(card, "global_position", centre, PlayedFlightSeconds)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.In);
            tween.TweenProperty(card, "scale", Vector2.One * 1.35f, PlayedFlightSeconds)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            tween.TweenProperty(card, "modulate:a", 0f, PlayedFlightSeconds)
                .SetDelay(PlayedFlightSeconds * 0.45f);
            tween.Chain().TweenCallback(Callable.From(() => DestroyHolder(holder)));
            FlightTweens[card] = tween;
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] played-card animation failed: {e.Message}");
            DestroyHolder(holder);
        }
    }

    private static void DestroyHolder(NHandCardHolder holder)
    {
        try
        {
            Detached.Remove(holder);

            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                return;
            }

            NCard? card = holder.CardNode;

            if (card != null && GodotObject.IsInstanceValid(card))
            {
                ResetPooledCard(card);
            }

            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();

            if (card != null && GodotObject.IsInstanceValid(card))
            {
                card.GetParent()?.RemoveChild(card);
                NodePool.Free(card);
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 归还对象池前必须把动画改过的属性复位：池里的节点会被游戏直接复用，
    /// 残留的 modulate.a = 0 会让本地手牌出现透明卡，残留 scale 会让卡变大。
    /// </summary>
    private static void ResetPooledCard(NCard card)
    {
        if (FlightTweens.TryGetValue(card, out Tween? tween))
        {
            try
            {
                if (tween != null && tween.IsValid())
                {
                    tween.Kill();
                }
            }
            catch
            {
                // ignored
            }

            FlightTweens.Remove(card);
        }

        card.Modulate = Colors.White;
        card.Scale = Vector2.One;
        card.Position = Vector2.Zero;
    }

    private static Control EnsureLayer(NPlayerHand hand)
    {
        // HandPosHelper coordinates are expressed in CardHolderContainer space.
        // Hosting the mirror layer under NPlayerHand put its origin at the bottom
        // strip instead, which threw every card into the top-left corner.
        Control? container = ContainerOf(hand);

        // Host the mirror under NPlayerHand, NOT under CardHolderContainer:
        // that container gets its Modulate zeroed to hide the vanilla cards, and
        // anything parented inside it would vanish along with them.
        Node parent = hand;

        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            if (_layer.GetParent() == parent)
            {
                ApplyLayerBounds(_layer, container);
                return _layer;
            }

            _layer.GetParent()?.RemoveChild(_layer);
            parent.AddChild(_layer);
            ApplyLayerBounds(_layer, container);
            return _layer;
        }

        var layer = new Control
        {
            Name = "SpectateMateHandLayer",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        parent.AddChild(layer);
        ApplyLayerBounds(layer, container);
        _layer = layer;
        return layer;
    }

    /// <summary>
    /// Copies the FULL transform of CardHolderContainer, not just its position.
    /// HandPosHelper returns coordinates in that container's local space, so any
    /// scale / rotation / pivot the container carries would otherwise be missing
    /// from the mirror — cards end up slightly offset or wrongly sized.
    /// </summary>
    private static void ApplyLayerBounds(Control layer, Control? container)
    {
        if (container != null && GodotObject.IsInstanceValid(container))
        {
            // 复制锚点与偏移，而不是复制当前的 Position/Size。
            // 回合结束会把手牌整体下移（AnimDisable），窗口缩放也会改变容器尺寸；
            // 只有锚点相同，镜像层才会和原生容器保持完全重合。
            layer.AnchorLeft = container.AnchorLeft;
            layer.AnchorRight = container.AnchorRight;
            layer.AnchorTop = container.AnchorTop;
            layer.AnchorBottom = container.AnchorBottom;
            layer.OffsetLeft = container.OffsetLeft;
            layer.OffsetRight = container.OffsetRight;
            layer.OffsetTop = container.OffsetTop;
            layer.OffsetBottom = container.OffsetBottom;

            layer.Scale = container.Scale;
            layer.Rotation = container.Rotation;
            layer.PivotOffset = container.PivotOffset;
        }
        else
        {
            layer.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            layer.Position = Vector2.Zero;
            layer.Size = Vector2.Zero;
        }

        // Keep the default z-order: a high value here would draw the mirror
        // above NGame's HoverTipsContainer and hide every tooltip.
        layer.ZIndex = 0;
    }

    private static void FreeHolders()
    {
        foreach (NHandCardHolder holder in Holders)
        {
            try
            {
                if (!GodotObject.IsInstanceValid(holder))
                {
                    continue;
                }

                NCard? card = holder.CardNode;
                if (card != null && GodotObject.IsInstanceValid(card))
                {
                    ResetPooledCard(card);
                }

                holder.GetParent()?.RemoveChild(holder);
                holder.QueueFree();

                if (card != null && GodotObject.IsInstanceValid(card))
                {
                    card.GetParent()?.RemoveChild(card);
                    NodePool.Free(card);
                }
            }
            catch (Exception e)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] free mirror holder: {e.Message}");
            }
        }

        Holders.Clear();
    }

    private static void HideVanillaNodes(NPlayerHand hand)
    {
        Control? container = ContainerOf(hand);
        if (container == null)
        {
            return;
        }

        if (!_vanillaHidden)
        {
            _savedModulate = container.Modulate;
            _vanillaHidden = true;
        }

        // Container-level transparency beats per-node hiding: holders the game
        // creates for a brand-new turn are invisible too, with zero bookkeeping.
        container.Modulate = new Color(_savedModulate.R, _savedModulate.G, _savedModulate.B, 0f);
    }

    private static void ShowVanillaNodes(NPlayerHand hand)
    {
        Control? container = ContainerOf(hand);
        if (container == null)
        {
            return;
        }

        if (_vanillaHidden)
        {
            container.Modulate = _savedModulate;
            _vanillaHidden = false;
        }
    }

    private static void OnHolderFocused(NHandCardHolder holder)
    {
        NPlayerHand? hand = _lastHand;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
        {
            return;
        }

        // GetIndex() is an extension that assumes a real hand holder; use our own
        // list index instead. RefreshLayout only needs FocusedHolder for the
        // identity check and _lastFocusedHolderIdx for the default-focus fallback.
        int index = Holders.IndexOf(holder);
        FocusedHolderField?.SetValue(hand, holder);
        LastFocusedIdxField?.SetValue(hand, index);
        hand.ForceRefreshCardIndices();
    }

    private static void OnHolderUnfocused(NHandCardHolder holder)
    {
        NPlayerHand? hand = _lastHand;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
        {
            return;
        }

        FocusedHolderField?.SetValue(hand, null);
        LastFocusedIdxField?.SetValue(hand, -1);
        hand.ForceRefreshCardIndices();
    }

    private static Control? ContainerOf(NPlayerHand? hand)
    {
        try
        {
            return hand != null && GodotObject.IsInstanceValid(hand) ? hand.CardHolderContainer : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ActiveHolders filters on IsVisibleInTree(), so a holder created moments ago
    /// (new turn draw) is absent from it and would keep its card face on screen.
    /// Walk the raw children instead.
    /// </summary>
    private static IEnumerable<NHandCardHolder> AllVanillaHolders(NPlayerHand hand)
    {
        Control? container = null;

        try
        {
            container = hand.CardHolderContainer;
        }
        catch
        {
            // ignored
        }

        if (container == null || !GodotObject.IsInstanceValid(container))
        {
            return hand.ActiveHolders;
        }

        var holders = new List<NHandCardHolder>();
        foreach (Node child in container.GetChildren())
        {
            if (child is NHandCardHolder holder)
            {
                holders.Add(holder);
            }
        }

        return holders;
    }

    private static void RestoreHolder(NHandCardHolder holder)
    {
        try
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                return;
            }

            if (holder.CardNode is { } node && GodotObject.IsInstanceValid(node))
            {
                node.Visible = true;
            }
        }
        catch
        {
            // holder may have been freed
        }
    }

    private static StringName BuildKey(IReadOnlyList<CardModel> cards)
    {
        var sb = new StringBuilder();
        foreach (CardModel card in cards)
        {
            sb.Append(card.Id.Entry)
                .Append('#')
                .Append(card.CurrentUpgradeLevel)
                .Append('@')
                .Append(card.Enchantment?.Id.Entry ?? "-")
                .Append('|');
        }

        return new StringName(sb.ToString());
    }
}
