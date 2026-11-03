using System;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SpectateMate.Patches;

/// <summary>
/// 诊断：出牌飞到播放区时，容器的状态到底是什么。
///
/// 原版 AppendPileLerpTween 里：
///     Vector2 targetPosition = typePile.GetTargetPosition(cardNode);
///     tween.TweenProperty(cardNode, "position", targetPosition, num);
///
/// 而 PileType.Play 的公式是：
///     PlayContainer.Size * 0.5f - node.Size * 0.5f + Vector2.Up * 100f
///
/// 这个值被当作「相对 PlayContainer 的局部坐标」。如果布局还没完成、
/// PlayContainer.Size 仍是 (0,0)，算出来就贴近 (0,-100)，也就是左上角。
///
/// 观战时 NCombatUi 是现构建的，布局完成时间可能比正常对局晚，
/// 这里把两张卡（加入播放区时、以及补间目标算出时）的状态都记下来。
/// </summary>
internal static class PlayContainerProbe
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.playprobe");

            var add = AccessTools.Method(typeof(NCombatUi), "AddToPlayContainer");
            if (add != null)
            {
                _harmony.Patch(add, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(AddPrefix)));
            }

            var lerp = AccessTools.Method(
                typeof(CardPileCmd),
                "AppendPileLerpTween",
                new[] { typeof(Tween), typeof(NCard), typeof(PileType), typeof(PileType?) });

            if (lerp != null)
            {
                _harmony.Patch(lerp, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(LerpPrefix)));
            }

            var addPost = AccessTools.Method(typeof(NCombatUi), "AddToPlayContainer");

            if (addPost != null)
            {
                _harmony.Patch(addPost, postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(AddPostfix)));
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play probe: add={add != null} lerp={lerp != null}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] play probe failed: {e.Message}");
        }
    }

    private static void AddPrefix(NCombatUi __instance, NCard card)
    {
        try
        {
            Control? pc = __instance.PlayContainer;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] add-to-play: {card.Model?.Id.Entry} " +
                $"pc(size={pc?.Size} pos={pc?.GlobalPosition} scale={pc?.Scale}) " +
                $"card(local={card.Position} global={card.GlobalPosition} size={card.Size}) " +
                $"parent={card.GetParent()?.Name}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// reparent 之后立刻记一次，再过 0.35 秒（补间时长 0.25）记一次，
    /// 用来判断补间到底有没有把卡片带到位。
    /// </summary>
    private static void AddPostfix(NCombatUi __instance, NCard card)
    {
        try
        {
            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] add-to-play after: {card.Model?.Id.Entry} " +
                $"pos={card.Position} global={card.GlobalPosition}");

            SceneTree? tree = card.GetTree();

            if (tree == null)
            {
                return;
            }

            SceneTreeTimer timer = tree.CreateTimer(0.35);
            string name = card.Model?.Id.Entry ?? "?";
            timer.Timeout += () =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(card))
                    {
                        ModEntry.Log.Info(
                            $"[{ModEntry.ModId}] play settle: {name} card gone");
                        return;
                    }

                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] play settle: {name} pos={card.Position} " +
                        $"global={card.GlobalPosition} scale={card.Scale} " +
                        $"parent={card.GetParent()?.Name}");
                }
                catch
                {
                    // ignored
                }
            };
        }
        catch
        {
            // ignored
        }
    }

    private static void LerpPrefix(NCard cardNode, PileType typePile)
    {
        try
        {
            if (typePile != PileType.Play)
            {
                return;
            }

            Control? pc = NCombatRoom.Instance?.Ui?.PlayContainer;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play lerp: card={cardNode.Model?.Id.Entry} " +
                $"target={typePile.GetTargetPosition(cardNode)} " +
                $"pc(size={pc?.Size} pos={pc?.GlobalPosition}) " +
                $"cardSize={cardNode.Size}");
        }
        catch
        {
            // ignored
        }
    }
}
