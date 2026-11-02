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
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Vfx;
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
    // 与原版 NCard.AnimCardToPlayPile 一致：0.2s、缩放到 0.8
    private const float PickUpSeconds = 0.08f;
    private const float PickUpLift = 40f;
    private const float PlayToPileSeconds = 0.2f;
    private const float PlayHoldSeconds = 0.18f;
    private const float PlaySettleSeconds = 0.22f;

    private static readonly List<NHandCardHolder> Holders = new();



    private static Control? _layer;
    private static NPlayerHand? _lastHand;

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
                if (hand != _lastHand)
                {
                    // 战斗切换：NPlayerHand 是新节点，旧的 layer / holder 全都属于
                    // 已销毁的场景。若不在这里整体作废，下面的 key 比对会因为
                    // 「队友手牌没变」而走复用分支，把已失效的 holder 继续交给
                    // ActiveHolders，表现为手牌区显示的是上一场的镜像牌、
                    // 而原生手牌又被 HideVanillaNodes 藏掉了。
                    _key = string.Empty;
                    _active = false;
                    _layer = null;
                    VanillaHandVisibility.ForgetState();
                    _hoverId = null;
                    _lastEnergy = int.MinValue;

                    Holders.Clear();
                    CardFlight.ReleaseAll();
                }

                _lastHand = hand;
            }

            if (cards == null)
            {
                Hide(hand, restoreVanilla: false);
                return;
            }

            CardFlight.UpdateSettling();

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
                VanillaHandVisibility.Hide(hand);
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
    /// <summary>
    /// 复刻原版手牌选择模式的视觉：不符合 filter 的牌直接隐藏。
    /// NPlayerHand.ActiveHolders 过滤 IsVisibleInTree()，所以隐藏的牌会自动
    /// 退出布局，其余牌由 RefreshLayout 重新收拢 —— 与被观战者看到的一致。
    /// </summary>
    public static void ApplyHandSelection(Func<CardModel, bool>? filter)
    {
        HandSelectionView.ApplyFilter(_lastHand, Holders, filter);
    }

    public static void ResetSelectionUi()
    {
        HandSelectionView.HideChrome(_lastHand);
    }

    public static void ClearHandSelection()
    {
        HandSelectionView.Clear(_lastHand, Holders);
    }

    private static ModelId? _hoverId;

    /// <summary>
    /// 高亮他鼠标正指着的牌。游戏只同步悬停（HoveredModelData），不同步「已选中」，
    /// 所以原版那种选中态做不到；但悬停本来就是选择时最明显的视觉反馈。
    /// </summary>
    public static void HighlightHovered(ModelId? id)
    {
        if (Holders.Count == 0)
        {
            // 镜像还没建好，不能写缓存，否则重建后会被当成「没变化」跳过。
            return;
        }

        if (id == _hoverId)
        {
            return;
        }

        _hoverId = id;

        try
        {
            foreach (NHandCardHolder holder in Holders)
            {
                NCard? card = holder?.CardNode;
                if (card == null || !GodotObject.IsInstanceValid(card))
                {
                    continue;
                }

                bool match = id != null && card.Model != null && card.Model.Id == id;

                // 与原版一致：不改颜色，只让描边变粗。
                // CardHighlight.Modulate 本来就是这张牌的稀有度色（稀有金 / 罕见青）。
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
            ModEntry.Log.Warn($"[{ModEntry.ModId}] hover highlight failed: {e.Message}");
        }
    }

    /// <summary>
    /// 兜底恢复：清掉镜像并把原生 holder 的可见性还回去。
    /// 用于「已经不在观战、但 ActiveHolders 还在被劫持」这种泄漏状态。
    /// </summary>
    public static void ForceHide()
    {
        if (!_active && !VanillaHandVisibility.IsHidden)
        {
            return;
        }

        ModEntry.Log.Warn($"[{ModEntry.ModId}] hand mirror leak detected; restoring vanilla holders");
        Hide(_lastHand, restoreVanilla: true);
        _hoverId = null;
    }

    private static readonly System.Reflection.MethodInfo? UpdateEnergyCostColorMethod =
        typeof(NCard).GetMethod(
            "UpdateEnergyCostColor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static int _lastEnergy = int.MinValue;

    /// <summary>
    /// 队友能量变化时刷新所有镜像卡的费用颜色。
    /// 卡面颜色只在 UpdateVisuals 时计算一次，手牌集合没变就不会重算，
    /// 于是会出现「能量已经回来，卡还是红的」。这里只调私有的费用刷新，
    /// 不动卡面其它状态（避免把高亮、描述一起重置）。
    /// </summary>
    public static void RefreshCostColors(int energy)
    {
        if (energy == _lastEnergy)
        {
            return;
        }

        _lastEnergy = energy;

        if (UpdateEnergyCostColorMethod == null || Holders.Count == 0)
        {
            return;
        }

        try
        {
            var args = new object[] { PileType.Hand };

            foreach (NHandCardHolder holder in Holders)
            {
                NCard? card = holder?.CardNode;
                if (card == null || !GodotObject.IsInstanceValid(card) || card.Model == null)
                {
                    continue;
                }

                UpdateEnergyCostColorMethod.Invoke(card, args);
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] refresh cost colors failed: {e.Message}");
        }
    }

    /// <summary>清掉所有高亮（选择结束、观战退出时用）。</summary>
    public static void ClearHighlight()
    {
        _hoverId = null;

        try
        {
            foreach (NHandCardHolder holder in Holders)
            {
                NCard? card = holder?.CardNode;
                if (card == null || !GodotObject.IsInstanceValid(card))
                {
                    continue;
                }

                card.CardHighlight.AnimHide();
            }
        }
        catch
        {
            // ignored
        }
    }

    public static void Hide(NPlayerHand? hand, bool restoreVanilla = true)
    {
        // 先把状态位清掉再做任何可能抛异常的事：FreeHolders / DestroyHolder
        // 一旦中途失败，_active 会停在 true，泄漏自检就会每拍反复触发、
        // 反复调用 Hide，形成死循环（日志里那句 leak detected 刷屏）。
        _active = false;
        _key = string.Empty;
        _hoverId = null;
        _lastEnergy = int.MinValue;

        hand ??= _lastHand;

        try
        {
            if (hand != null && GodotObject.IsInstanceValid(hand))
            {
                if (restoreVanilla)
                {
                    VanillaHandVisibility.Show(hand);
                }
                else
                {
                    // Still spectating: make sure nothing leaks back on screen.
                    VanillaHandVisibility.Hide(hand);
                }
            }
        }
        catch
        {
            // ignored
        }

        CardFlight.ReleaseAll();
        FreeHolders();

        if (_layer != null && GodotObject.IsInstanceValid(_layer))
        {
            _layer.GetParent()?.RemoveChild(_layer);
            _layer.QueueFree();
        }

        _layer = null;
    }

    private static void Rebuild(NPlayerHand hand, IReadOnlyList<CardModel> cards, StringName key)
    {
        VanillaHandVisibility.Hide(hand);

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
            CardFlight.AnimatePlayedCard(played, _layer);
        }

        Holders.Clear();
        Holders.AddRange(next);

        _key = key;
        _active = true;
        _hoverId = null;

        // 交给原版 RefreshLayout 重新排版（扇形、悬停抬起、相邻让位、焦点）。
        hand.ForceRefreshCardIndices();

        // 必须在 ForceRefreshCardIndices 之后执行：_active 为 false 的瞬间
        // RefreshLayout 会把原生编号写回来。
        VanillaHandVisibility.HideLabels(_lastHand);

        Control? ctr = VanillaHandVisibility.ContainerOf(hand);
        Diag(
            $"showing {Holders.Count} cards | ctrMod={ctr?.Modulate} " +
            $"ctrKids={ctr?.GetChildCount()} | labels={DescribeIndexLabels()}");
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
    private static readonly System.Reflection.FieldInfo? HandIndexLabelField =
        typeof(NHandCardHolder).GetField(
            "_handIndexLabel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>诊断用：把镜像 holder 的编号标签串起来，便于核对是否重号。</summary>
    private static string DescribeIndexLabels()
    {
        if (HandIndexLabelField == null || Holders.Count == 0)
        {
            return "-";
        }

        var sb = new System.Text.StringBuilder();

        foreach (NHandCardHolder holder in Holders)
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                sb.Append("?,");
                continue;
            }

            string text = HandIndexLabelField.GetValue(holder) is MegaLabel label ? label.Text : "?";

            if (holder.Visible)
            {
                sb.Append(text).Append(',');
            }
            else
            {
                sb.Append('(').Append(text).Append("),");
            }
        }

        return sb.ToString();
    }

    private static Control EnsureLayer(NPlayerHand hand)
    {
        // HandPosHelper coordinates are expressed in CardHolderContainer space.
        // Hosting the mirror layer under NPlayerHand put its origin at the bottom
        // strip instead, which threw every card into the top-left corner.
        Control? container = VanillaHandVisibility.ContainerOf(hand);

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
            // 统一走 CardFlight：它负责「卡片是否还挂在自己名下」的判断、
            // 归还前的 modulate/scale/position 复位，以及 NodePool.Free。
            CardFlight.DestroyHolder(holder);
        }

        Holders.Clear();
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