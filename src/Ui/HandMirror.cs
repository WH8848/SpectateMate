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

    private static readonly List<NHandCardHolder> Holders = new();

    private static Control? _layer;
    private static NPlayerHand? _lastHand;
    private static bool _vanillaHidden;
    private static int _focusedIndex = -1;
    private static Color _savedModulate = Colors.White;
    private static StringName _key = string.Empty;
    private static bool _active;
    private static string _lastDiag = string.Empty;

    public static bool IsShowing => _active;

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

        FreeHolders();

        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            _layer.GetParent()?.RemoveChild(_layer);
            _layer.QueueFree();
        }

        _layer = null;
        _key = string.Empty;
        _active = false;
        _focusedIndex = -1;
    }

    private static void Rebuild(NPlayerHand hand, IReadOnlyList<CardModel> cards, StringName key)
    {
        FreeHolders();
        HideVanillaNodes(hand);

        Control layer = EnsureLayer(hand);
        int count = Math.Min(cards.Count, 10);
        if (count <= 0)
        {
            _key = key;
            _active = true;
            Diag("showing 0 cards");
            return;
        }

        Vector2 scale = HandPosHelper.GetScale(count);
        bool firstPaint = !_active;

        for (int i = 0; i < count; i++)
        {
            NCard? card = NCard.Create(cards[i]);
            if (card == null)
            {
                continue;
            }

            card.Visibility = ModelVisibility.Visible;
            card.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);

            NHandCardHolder holder = NHandCardHolder.Create(card, hand);
            holder.MouseFilter = Control.MouseFilterEnum.Stop;
            layer.AddChild(holder);
            holder.SetClickable(true);

            // _Ready disables the hitbox and only AnimPosition re-enables it if the
            // holder is already near its target. Enable it explicitly so the mirror
            // actually receives hover — otherwise the click falls through to the
            // (invisible) vanilla holder underneath and the card never lifts.
            holder.Hitbox.SetEnabled(true);
            holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;

            // Holder._Ready only refreshes once; force the card visuals now that
            // both the holder and the card node are inside the tree.
            card.Visible = true;
            card.Modulate = Colors.White;
            card.Scale = Vector2.One;
            card.Position = Vector2.Zero;
            holder.UpdateCard();

            Vector2 target = HandPosHelper.GetPosition(count, i);
            float angle = HandPosHelper.GetAngle(count, i);

            if (firstPaint)
            {
                holder.Position = target;
                holder.SetAngleInstantly(angle);
                holder.SetScaleInstantly(scale);
            }
            else
            {
                // Start below the fan; the holder's own _Process slides it in.
                holder.Position = target + new Vector2(0f, SpawnOffsetY);
                holder.SetAngleInstantly(angle);
                holder.SetScaleInstantly(scale);
            }

            holder.SetTargetPosition(target);
            holder.SetTargetAngle(angle);
            holder.SetTargetScale(scale);

            holder.Connect(
                NHandCardHolder.SignalName.HolderFocused,
                Callable.From<NHandCardHolder>(OnHolderFocused));
            holder.Connect(
                NHandCardHolder.SignalName.HolderUnfocused,
                Callable.From<NHandCardHolder>(OnHolderUnfocused));

            Holders.Add(holder);
        }

        _key = key;
        _active = true;
        Diag($"showing {Holders.Count} cards (holders={hand.ActiveHolders.Count})");
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
        layer.SetAnchorsPreset(Control.LayoutPreset.TopLeft);

        if (container != null && GodotObject.IsInstanceValid(container))
        {
            layer.Position = container.Position;
            layer.Scale = container.Scale;
            layer.Rotation = container.Rotation;
            layer.PivotOffset = container.PivotOffset;

            if (container.Size.X > 0f && container.Size.Y > 0f)
            {
                layer.Size = container.Size;
            }
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
        int index = Holders.IndexOf(holder);
        if (index < 0)
        {
            return;
        }

        _focusedIndex = index;
        ApplyFocusLayout();
    }

    private static void OnHolderUnfocused(NHandCardHolder holder)
    {
        int index = Holders.IndexOf(holder);
        if (index != _focusedIndex)
        {
            return;
        }

        _focusedIndex = -1;
        ApplyFocusLayout();
    }

    /// <summary>
    /// Same rules as NPlayerHand.RefreshLayout: the focused card straightens up and
    /// lifts by half its hitbox height, neighbours slide aside proportionally.
    /// We can't use the vanilla method because our holders are not in
    /// ActiveHolders — the game does not know they exist.
    /// </summary>
    private static void ApplyFocusLayout()
    {
        int count = Holders.Count;
        if (count == 0)
        {
            return;
        }

        Vector2 scale = HandPosHelper.GetScale(count);
        for (int i = 0; i < count; i++)
        {
            NHandCardHolder holder = Holders[i];
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                continue;
            }

            Vector2 position = HandPosHelper.GetPosition(count, i);

            if (_focusedIndex < 0)
            {
                holder.SetTargetPosition(position);
                holder.SetTargetScale(scale);
                holder.SetTargetAngle(HandPosHelper.GetAngle(count, i));
                holder.ZIndex = 0;
                continue;
            }

            if (i == _focusedIndex)
            {
                holder.SetAngleInstantly(0f);
                holder.SetScaleInstantly(Vector2.One);
                position.Y = (0f - holder.Hitbox.Size.Y) * 0.5f + 2f;
                holder.SetTargetPosition(position);
                holder.ZIndex = 20;
                continue;
            }

            float push = Mathf.Lerp(100f, 0f, Mathf.Min(1f, Mathf.Abs(_focusedIndex - i) / 4f));
            position += Vector2.Left * Mathf.Sign(_focusedIndex - i) * push;
            holder.SetTargetPosition(position);
            holder.SetTargetScale(scale);
            holder.SetTargetAngle(HandPosHelper.GetAngle(count, i));
            holder.ZIndex = 0;
        }
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
