using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;

namespace SpectateMate.Ui;

/// <summary>
/// 出牌动画与卡牌回收。
///
/// 三个阶段：
///   1. AnimatePlayedCard —— 伪「拿起」+ 飞向播放区（复刻 NCard.AnimCardToPlayPile 的参数）；
///   2. UpdateSettling    —— 每拍盯着 CardModel.Pile，等它落定到 Discard / Exhaust / Draw；
///   3. FlyToPile         —— 交给原版 NCardFlyVfx（带拖尾的弧形光带）飞向目标牌堆。
///
/// 所有节点最终都要归还 NodePool，归还前必须复位 modulate / scale / position，
/// 否则池里的卡被游戏复用时会出现透明卡或变形的卡。
/// </summary>
public static class CardFlight
{
    private const float PlayToPileSeconds = 0.2f;

    // 卡片在播放区的最低停留时间，对应原版打完牌后那 0.4~0.5 秒的等待。
    private const ulong PlayHoldMs = 400;

    private sealed class SettlingCard
    {
        public NHandCardHolder Holder = null!;
        public NCard Card = null!;
        public CardModel Model = null!;

        /// <summary>最早可以离开播放区的时间戳（毫秒）。</summary>
        public ulong ReadyAtMs;

        /// <summary>上一拍看到的去向。</summary>
        public PileType? LastSeen;

        /// <summary>该去向已连续保持的拍数。</summary>
        public int StableCount;
    }

    private static readonly List<SettlingCard> Settling = new();
    private static readonly List<NHandCardHolder> Detached = new();
    private static readonly Dictionary<NCard, Tween> FlightTweens = new();

    /// <summary>整体复位，供观战退出/战斗切换时调用。</summary>
    public static void ReleaseAll()
    {
        foreach (NHandCardHolder holder in Detached.ToArray())
        {
            DestroyHolder(holder);
        }

        Detached.Clear();
        Settling.Clear();
        FlightTweens.Clear();
    }

    /// <summary>每拍调用：检查挂起中的牌是否已经落定。</summary>
    public static void UpdateSettling()
    {
        if (Settling.Count == 0)
        {
            return;
        }

        foreach (SettlingCard item in Settling.ToArray())
        {
            if (!GodotObject.IsInstanceValid(item.Card) || !GodotObject.IsInstanceValid(item.Holder))
            {
                Settling.Remove(item);
                continue;
            }

            // 还没到最低停留时间就继续等，别急着送走。
            if (Godot.Time.GetTicksMsec() < item.ReadyAtMs)
            {
                continue;
            }

            PileType? destination = null;

            try
            {
                destination = item.Model.Pile?.Type;
            }
            catch
            {
                destination = null;
            }

            // 去向必须连续两拍一致才采纳。
            //
            // 虚无牌的实际流转是 Play -> Discard -> Exhaust：先按普通牌进弃牌堆，
            // 再被虚无效果消耗。如果第一拍看到 Discard 就送走，
            // 后面的 Exhaust 再也拿不到，表现就是「本该碎裂的牌飞进了弃牌堆」。
            // 多等一拍，中转状态就被过滤掉了。
            if (destination != item.LastSeen)
            {
                item.LastSeen = destination;
                item.StableCount = 0;
                continue;
            }

            item.StableCount++;

            if (item.StableCount < 2)
            {
                continue;
            }

            switch (destination)
            {
                case PileType.Discard:
                case PileType.Exhaust:
                case PileType.Draw:
                    Settling.Remove(item);
                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] settle {item.Model.Id.Entry} -> {destination.Value}");
                    FlyToPile(item, destination.Value);
                    break;

                case PileType.Hand:
                    // 牌被打消 / 退回手牌：手牌镜像会重新建出这张，播放区这份直接回收。
                    DiscardSettled(item);
                    break;

                case null or PileType.None when IsPower(item.Model):
                    // 能力卡打出后会彻底消失，不进任何牌堆。
                    // 原版在这条路径上播的是 PlayPowerCardFlyVfx（飞向能力栏），
                    // 不会回到 Pile 判定。如果不在这里收掉，卡片会永远停在播放区。
                    //
                    // 注意要把 PileType.None 和 null 都算上：
                    // 卡牌消失时 Model.Pile 可能退化成 Pile 对象且 Type = None，
                    // 只匹配 null 会漏掉这种情况（上一版就是这么漏的）。
                    ModEntry.Log.Info($"[{ModEntry.ModId}] power card settled: {item.Model.Id.Entry}");
                    DiscardSettled(item);
                    break;

                // Play：他还在选择目标或结算中，停在播放区继续等。
                // None 且不是能力卡：可能是中间态，同样再等等。
            }
        }
    }

    /// <summary>把一张被打出的牌从手牌镜像里摘出来并送进播放区。</summary>
    public static void AnimatePlayedCard(NHandCardHolder holder, Control? layer)
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

            // 从 holder 里摘出来变成自由节点：否则飞行的 global_position 会被
            // holder 的扇形旋转/缩放反算扭曲，路径不是直线。
            if (layer != null && GodotObject.IsInstanceValid(layer))
            {
                card.Reparent(layer, true);   // keep global transform
            }

            Vector2 playTarget = PlayTargetGlobal(card);

            Tween tween = card.CreateTween();

            // 直接飞向播放区，不做任何「拿起」预动作。
            //
            // 原版那一下是真实拖拽的产物：按住时卡片跟随鼠标并放大到 1.15 倍，
            // 松手那一刻它已经处在放大状态，于是接管过去直接飞走。
            // 拖拽过程不进网络，我们拿不到，任何模拟都会露出破绽
            // （之前那版先胀一下再缩回去，看起来像蹲起）。
            // 与其模拟得不像，不如让这一张直接从扇形位置起飞。
            //
            // 复刻 NCard.AnimCardToPlayPile：飞到播放区、缩放到 0.8、0.2 秒、不淡出。
            tween.TweenProperty(card, "global_position", playTarget, PlayToPileSeconds)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            tween.Parallel()
                .TweenProperty(card, "scale", Vector2.One * 0.8f, PlayToPileSeconds)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);

            tween.TweenCallback(Callable.From(() => BeginSettling(holder, card)));

            FlightTweens[card] = tween;
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] played-card animation failed: {e.Message}");
            DestroyHolder(holder);
        }
    }

    /// <summary>
    /// PileType.Play.GetTargetPosition 返回的是相对 PlayContainer 的局部坐标
    /// （原版会把卡片 reparent 进去），而 Discard 返回全局坐标。这里统一换算。
    /// </summary>
    private static Vector2 PlayTargetGlobal(NCard card, PileType pile = PileType.Play)
    {
        try
        {
            if (pile != PileType.Play)
            {
                return pile.GetTargetPosition(card);
            }

            NCombatUi? ui = NCombatRoom.Instance?.Ui;
            if (ui?.PlayContainer is not { } container)
            {
                return card.GlobalPosition;
            }

            Vector2 local = PileType.Play.GetTargetPosition(card);
            return container.GlobalPosition + container.Scale * local;
        }
        catch
        {
            return card.GlobalPosition;
        }
    }

    private static void BeginSettling(NHandCardHolder holder, NCard card)
    {
        try
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder) || card == null || !GodotObject.IsInstanceValid(card))
            {
                DestroyHolder(holder);
                return;
            }

            Settling.Add(new SettlingCard
            {
                Holder = holder,
                Card = card,
                Model = card.Model!,

                // 最低停留时间。
                //
                // 原版打完牌后还有一整套：淡出 -> 暂停 0.1~0.2 秒 ->
                // 换成播放区样式 -> 从下方升起 -> 再等 0.4~0.5 秒。
                // 总共接近一秒，卡片在播放区是「摆在那里给你看清」的。
                //
                // 我们只做了飞到播放区那 0.2 秒，随后就盯着 Pile 的变化。
                // 他结算得越快（比如开了加速模式），Pile 变得越早，
                // 卡片一闪就飞走了。这里补一个保底停留，让观感回到正常节奏。
                ReadyAtMs = Godot.Time.GetTicksMsec() + PlayHoldMs,
            });
        }
        catch
        {
            DestroyHolder(holder);
        }
    }

    /// <summary>
    /// 复用原版的 NCardFlyVfx：它自带拖尾和贝塞尔控制点，这就是原版打出牌后
    /// 那条「弧形彩色光带」的来源。拖尾贴图取自 Character.TrailPath。
    /// </summary>
    private static void FlyToPile(SettlingCard item, PileType pile)
    {
        try
        {
            // 消耗不是飞行：原版 CardPileCmd 对 Exhaust 走的是 NCardExhaustVfx
            // （卡片被侵蚀、碎裂并伴随粒子），和飞向弃牌堆/抽牌堆完全是两套特效。
            if (pile == PileType.Exhaust)
            {
                PlayExhaust(item);
                return;
            }

            Detached.Remove(item.Holder);

            Player? owner = null;
            Node? container = null;

            try
            {
                owner = item.Model.Owner;
                container = owner?.Creature?.GetVfxContainer();
            }
            catch
            {
                container = null;
            }

            if (container == null || owner == null)
            {
                DestroyHolder(item.Holder);
                return;
            }

            item.Card.Reparent(container);

            NCardFlyVfx? vfx = NCardFlyVfx.Create(
                item.Card,
                pile,
                isAddingToPile: true,
                TrailPath(owner) ?? string.Empty);

            if (vfx == null)
            {
                DestroyHolder(item.Holder);
                return;
            }

            container.AddChildSafely(vfx);

            // NCardFlyVfx 只把卡片 Visible=false 然后自己 QueueFree，不回收卡片本体。
            NCard flying = item.Card;
            vfx.TreeExited += () =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(flying) || flying.GetParent() == null)
                    {
                        return;
                    }

                    ResetPooledCard(flying);
                    flying.GetParent()?.RemoveChild(flying);
                    NodePool.Free(flying);
                }
                catch
                {
                    // ignored
                }
            };

            if (item.Holder != null && GodotObject.IsInstanceValid(item.Holder))
            {
                item.Holder.QueueFree();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] fly-to-pile vfx failed: {e.Message}");
            DestroyHolder(item.Holder);
        }
    }

    /// <summary>
    /// 回收一张已经结束停留的牌，连同卡片本体一起。
    ///
    /// 不能只调 DestroyHolder：出牌时卡片已经被 Reparent 到镜像层，
    /// holder 只剩空壳，DestroyHolder 里 ownsCard 判定为 false，
    /// 会故意跳过卡片回收（那是为了防止重复归还对象池）。
    /// 结果卡片永久留在层上——能力卡打出后一直悬在空中就是这么来的。
    /// </summary>
    private static void DiscardSettled(SettlingCard item)
    {
        Settling.Remove(item);

        try
        {
            NCard? card = item.Card;

            if (card != null
                && GodotObject.IsInstanceValid(card)
                && card.GetParent() != null
                && card.GetParent() != item.Holder)
            {
                ResetPooledCard(card);
                card.GetParent()?.RemoveChild(card);
                NodePool.Free(card);
            }
        }
        catch
        {
            // ignored
        }

        DestroyHolder(item.Holder);
    }

    private static bool IsPower(CardModel model)
    {
        try
        {
            return model.Type == CardType.Power;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 光带颜色：观战时统一取被观战者的，其余情况用牌主的。
    ///
    /// 观战时 Owner 相关的链路可能解析到本地玩家（洗牌特效就实测拿错了），
    /// 而屏幕上的一切都应该是被观战者的，所以这里按观战目标取。
    /// </summary>
    private static string? TrailPath(Player? owner)
    {
        try
        {
            if (SpectateOverlay.IsOpen && SpectateOverlay.Target?.Character is { } target)
            {
                return target.TrailPath;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            return owner?.Character?.TrailPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从手牌直接进弃牌堆 / 抽牌堆时补一份飞行动画。
    ///
    /// 原版在 CardPileCmd 里做的是：把卡片 reparent 到角色 VFX 容器，
    /// 创建 NCardFlyVfx，再挂到同一个容器下。但它取的是
    /// NCard.FindOnTable(Model)——找到的是被我们藏起来的原生卡，
    /// 特效在看不见的地方播完。
    /// 这里在镜像卡上按同样的步骤补一遍。
    /// </summary>
    public static void PlayFlyFromHand(NHandCardHolder? holder, PileType pile)
    {
        try
        {
            NCard? card = holder?.CardNode;
            if (holder == null || card == null || !GodotObject.IsInstanceValid(card))
            {
                DestroyHolder(holder);
                return;
            }

            CardModel? model = card.Model;
            Detached.Remove(holder);

            Player? owner = null;
            Node? container = null;

            try
            {
                owner = model?.Owner;
                container = owner?.Creature?.GetVfxContainer();
            }
            catch
            {
                container = null;
            }

            if (container == null || owner == null)
            {
                DestroyHolder(holder);
                return;
            }

            card.Reparent(container);

            NCardFlyVfx? vfx = NCardFlyVfx.Create(
                card,
                pile,
                isAddingToPile: true,
                TrailPath(owner) ?? string.Empty);

            if (vfx == null)
            {
                DestroyHolder(holder);
                return;
            }

            container.AddChildSafely(vfx);

            NCard flying = card;
            vfx.TreeExited += () =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(flying) || flying.GetParent() == null)
                    {
                        return;
                    }

                    ResetPooledCard(flying);
                    flying.GetParent()?.RemoveChild(flying);
                    NodePool.Free(flying);
                }
                catch
                {
                    // ignored
                }
            };

            if (GodotObject.IsInstanceValid(holder))
            {
                holder.QueueFree();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] hand fly vfx failed: {e.Message}");
            DestroyHolder(holder);
        }
    }

    /// <summary>
    /// 从手牌直接消耗（虚无、回合结束消耗）时播的碎裂特效。
    ///
    /// 原版走 CardPileCmd 的 Exhaust 分支，用 NCardExhaustQuickVfx；
    /// 但它取的是 NCard.FindOnTable(model)，找到的是被我们藏起来的那张原生卡，
    /// 特效在看不见的地方播完，观战者什么也看不到。
    /// 所以这里在镜像卡上补一份同样的特效。
    /// </summary>
    public static void PlayExhaustFromHand(NHandCardHolder? holder)
    {
        try
        {
            NCard? card = holder?.CardNode;
            if (holder == null || card == null || !GodotObject.IsInstanceValid(card))
            {
                DestroyHolder(holder);
                return;
            }

            Detached.Remove(holder);

            // Create 内部已经把特效挂到 cardNode.CardVfxContainer 上了，
            // 千万不要再 AddChild 一次（会把它从卡片下摘走）。
            NCardExhaustQuickVfx? vfx = NCardExhaustQuickVfx.Create(card);
            if (vfx == null)
            {
                DestroyHolder(holder);
                return;
            }

            TaskHelper.RunSafely(vfx.PlayAnimation());

            // 不能立刻销毁 holder。
            //
            // 卡片和特效都还挂在 holder 的子树里：holder 一销毁，
            // CardVfxContainer 跟着没，特效连一帧都播不出来——这正是
            // 「既看不到碎裂、也没有任何反应」的原因。
            // PlayAnimation 内部会在 0.4 秒预备后销毁卡片本体、2 秒后销毁自己，
            // 所以这里只要延迟回收空壳就行。
            if (GodotObject.IsInstanceValid(holder))
            {
                Tween cleanup = holder.CreateTween();
                cleanup.TweenInterval(2.6f);
                cleanup.TweenCallback(Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(holder))
                    {
                        DestroyHolder(holder);
                    }
                }));
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] hand exhaust vfx failed: {e.Message}");
            DestroyHolder(holder);
        }
    }

    /// <summary>
    /// 原地播消耗特效：Create 会把卡片从当前父节点摘走并塞进特效内部，
    /// 之后由 PlayAnimation 接管销毁，所以这里不能再去回收卡片。
    /// </summary>
    private static void PlayExhaust(SettlingCard item)
    {
        try
        {
            Detached.Remove(item.Holder);

            NCardExhaustVfx? vfx = NCardExhaustVfx.Create(item.Card);
            if (vfx == null)
            {
                DestroyHolder(item.Holder);
                return;
            }

            NCombatRoom.Instance?.Ui?.AddChildSafely(vfx);
            TaskHelper.RunSafely(vfx.PlayAnimation());

            // holder 只是空壳，卡片已经归特效管。
            if (item.Holder != null && GodotObject.IsInstanceValid(item.Holder))
            {
                item.Holder.QueueFree();
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] exhaust vfx failed: {e.Message}");
            DestroyHolder(item.Holder);
        }
    }

    /// <summary>销毁 holder 并归还它名下的卡片。</summary>
    public static void DestroyHolder(NHandCardHolder? holder)
    {
        try
        {
            if (holder == null)
            {
                return;
            }

            Detached.Remove(holder);

            if (!GodotObject.IsInstanceValid(holder))
            {
                return;
            }

            NCard? card = holder.CardNode;

            // 出牌动画会把 CardNode reparent 到角色 VFX 容器，由 NCardFlyVfx 接管，
            // 最终在 vfx.TreeExited 里归还。必须确认卡片还挂在自己名下，
            // 否则会重复归还（Tried to free object ... back to pool）。
            bool ownsCard = card != null
                && GodotObject.IsInstanceValid(card)
                && card.GetParent() == holder;

            if (ownsCard && card != null)
            {
                ResetPooledCard(card);
            }

            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();

            if (ownsCard && card != null && GodotObject.IsInstanceValid(card))
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
    /// 归还对象池前把动画改过的属性复位：池里的节点会被游戏直接复用，
    /// 残留的 modulate.a = 0 会让本地手牌出现透明卡。
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
}