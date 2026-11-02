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
    private const float PickUpSeconds = 0.08f;
    private const float PickUpLift = 40f;
    private const float PlayToPileSeconds = 0.2f;

    private sealed class SettlingCard
    {
        public NHandCardHolder Holder = null!;
        public NCard Card = null!;
        public CardModel Model = null!;
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

            PileType? destination = null;

            try
            {
                destination = item.Model.Pile?.Type;
            }
            catch
            {
                destination = null;
            }

            switch (destination)
            {
                case PileType.Discard:
                case PileType.Exhaust:
                case PileType.Draw:
                    Settling.Remove(item);
                    FlyToPile(item, destination.Value);
                    break;

                case PileType.Hand:
                    // 牌被打消 / 退回手牌：手牌镜像会重新建出这张，播放区这份直接回收。
                    Settling.Remove(item);
                    DestroyHolder(item.Holder);
                    break;

                // Play / None：他还在选择目标或结算中，停在播放区继续等。
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
            Vector2 pickedUp = card.GlobalPosition + Vector2.Up * PickUpLift;

            Tween tween = card.CreateTween();

            // 伪「拿起」：原版是真实拖拽，这里用一次轻微上抬+放大代替。
            tween.TweenProperty(card, "global_position", pickedUp, PickUpSeconds)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            tween.Parallel()
                .TweenProperty(card, "scale", card.Scale * 1.15f, PickUpSeconds)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);

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
                owner.Character.TrailPath);

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
