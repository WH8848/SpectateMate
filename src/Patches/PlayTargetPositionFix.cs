using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

internal static class PlayTargetPositionFix
{
    private const double PlayEntrySeconds = 0.25;
    private const double ChoiceReturnSeconds = 0.2;

    public static void Install()
    {
        try
        {
            var harmony = new Harmony($"{ModEntry.ModId}.playtarget");
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(PileTypeExtensions), nameof(PileTypeExtensions.GetTargetPosition)),
                postfix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(AdjustTargetPosition)));
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(CardPileCmd), "AppendPlayPileLerpTween"),
                prefix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(PlaceBeforePlayTween)),
                postfix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(FollowSpectateDuringPlayTween)));
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(NCombatUi), nameof(NCombatUi.AddToPlayContainer)),
                postfix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(PlaceAfterAddingToPlayContainer)));
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(NCard), nameof(NCard.AnimCardToPlayPile)),
                postfix: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(PlaceAfterReturningFromChoice)));
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(NMultiplayerPlayerIntentHandler), "BeforeActionPausedForPlayerChoice"),
                transpiler: new HarmonyMethod(typeof(PlayTargetPositionFix), nameof(TrackPlayerChoiceTween)));
            ModEntry.Log.Info($"[{ModEntry.ModId}] play target fix: hooked");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] play target fix failed: {e.Message}");
        }
    }

    private static bool IsSpectatedCard(CardModel? model) =>
        SpectateOverlay.IsOpen &&
        SpectateOverlay.Target is { } target &&
        model is { IsMutable: true } &&
        model.Owner?.NetId == target.NetId;

    // 卡面以节点原点为中心，用播放区尺寸计算落点。
    private static Vector2 LandingPoint(Control play) => play.Size * 0.5f + Vector2.Up * 100f;

    internal static void CenterCardsAtVanillaLanding()
    {
        if (NCombatRoom.Instance?.Ui?.PlayContainer is not { } play)
        {
            return;
        }

        Vector2 center = LandingPoint(play);
        foreach (Node child in play.GetChildren())
        {
            // 入场已结束的牌可能仍在等待结算。只移动停在原版落点的牌，保留选牌等特效位移。
            if (child is NCard card && IsSpectatedCard(card.Model) && card.Model?.Pile?.Type == PileType.Play &&
                card.Position.IsEqualApprox(center - card.Size * 0.5f))
            {
                card.Position = center;
            }
        }
    }

    private static void AdjustTargetPosition(PileType pileType, NCard? node, ref Vector2 __result)
    {
        if (pileType == PileType.Play && IsSpectatedCard(node?.Model) &&
            NCombatRoom.Instance?.Ui?.PlayContainer is { } play)
        {
            __result = LandingPoint(play);
        }
    }

    private static void PlaceBeforePlayTween(NCard cardNode)
    {
        if (PlaceInPlayContainer(cardNode))
        {
            // 在换堆时同步移除镜像。快速结算可能在下一次手牌刷新前就离开 Play。
            HandMirror.RemovePlayedCard(cardNode.Model);
        }
    }

    private static void PlaceAfterAddingToPlayContainer(NCard card) => PlaceInPlayContainer(card);

    private static void FollowSpectateDuringPlayTween(Tween? tween, NCard cardNode) =>
        CenterDuringEntry(tween, cardNode, PlayEntrySeconds);

    private static void PlaceAfterReturningFromChoice(NCard __instance)
    {
        PlaceInPlayContainer(__instance);
        CenterDuringEntry(__instance.PlayPileTween, __instance, ChoiceReturnSeconds);
    }

    private static void CenterDuringEntry(Tween? tween, NCard card, double duration)
    {
        if (tween == null || card.Model is not { IsMutable: true, Owner: { } owner } model ||
            LocalContext.IsMe(owner) || NCombatRoom.Instance?.Ui?.PlayContainer is not { } play ||
            card.GetParent() != play)
        {
            return;
        }

        bool centered = IsSpectatedCard(model);

        // 与原版位置和缩放同一组补间，按顺序在原版位置之后更新，时长与原版缩放相同。
        tween.Parallel().TweenMethod(Callable.From((float _) =>
        {
            if (card.Model != model || card.GetParent() != play)
            {
                return;
            }

            // 重新进入观战时，原版补间可能仍在使用关闭观战时算出的落点。
            if (centered || IsSpectatedCard(model))
            {
                centered = true;
                card.Position = LandingPoint(play);
            }
        }), 0f, 1f, duration);
    }

    private static IEnumerable<CodeInstruction> TrackPlayerChoiceTween(IEnumerable<CodeInstruction> instructions)
    {
        var createTween = AccessTools.Method(typeof(Node), nameof(Node.CreateTween));
        var trackTween = AccessTools.Method(typeof(PlayTargetPositionFix), nameof(CreatePlayerChoiceTween));
        return new CodeMatcher(instructions)
            .MatchStartForward(CodeMatch.Calls(createTween))
            .ThrowIfInvalid("Player-choice card tween not found")
            .Set(OpCodes.Call, trackTween)
            .InstructionEnumeration();
    }

    private static Tween CreatePlayerChoiceTween(NCard card)
    {
        Tween tween = card.CreateTween();
        if (IsSpectatedCard(card.Model) && card.Model?.Pile?.Type == PileType.Play)
        {
            // 恢复出牌时，原版会先结束 PlayPileTween，再开始回到播放区的动画。
            card.PlayPileTween = tween;
        }

        return tween;
    }

    private static bool PlaceInPlayContainer(NCard card)
    {
        if (!IsSpectatedCard(card.Model) || card.Model?.Pile?.Type != PileType.Play ||
            NCombatRoom.Instance?.Ui?.PlayContainer is not { } play || card.GetParent() != play)
        {
            return false;
        }

        // 结算可在入场补间结束前开始，离场动画会直接读取此时的位置。
        card.Position = LandingPoint(play);
        return true;
    }
}
