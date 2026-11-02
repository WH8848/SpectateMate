using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 把洗牌光效的颜色改成被观战者的。
///
/// 原版 CardPileCmd 里：
///
///     string trailPath = card.Owner.Character.TrailPath;
///     NCardFlyShuffleVfx.Create(oldPile, targetPile, trailPath);
///
/// 但观战时这条链拿到的 Owner 是本地玩家——日志实测：
/// 本地是铁甲战士、观战猎人，实际传进去的却是 card_trail_ironclad。
///
/// 观战语义很明确：屏幕上的一切都应该是被观战者的，所以这里直接把
/// trailPath 换成他的。非观战状态完全不动。
/// </summary>
internal static class ShuffleTrailFix
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.shuffletrail");

            var target = AccessTools.Method(
                typeof(NCardFlyShuffleVfx),
                "Create",
                new[] { typeof(CardPile), typeof(CardPile), typeof(string) });

            if (target == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] shuffle trail: Create not found");
                return;
            }

            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ShuffleTrailFix), nameof(Prefix)));

            var shuffle = AccessTools.Method(
                typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd),
                "Shuffle",
                new[]
                {
                    typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                    typeof(MegaCrit.Sts2.Core.Entities.Players.Player),
                });

            if (shuffle != null)
            {
                _harmony.Patch(
                    shuffle,
                    prefix: new HarmonyMethod(typeof(ShuffleTrailFix), nameof(ShufflePrefix)));
            }

            ModEntry.Log.Info($"[{ModEntry.ModId}] shuffle trail: hooked");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] shuffle trail patch failed: {e.Message}");
        }
    }

    /// <summary>
    /// 在 Shuffle 入口记录「洗的是谁」和「第一张牌属于谁」。
    ///
    /// 原版 Shuffle 是这样取的：
    ///     CardPile pile = PileType.Draw.GetPile(player);
    ///     List&lt;CardModel&gt; list = PileType.Discard.GetPile(player).Cards.ToList();
    ///     ... trailPath = card.Owner.Character.TrailPath;
    /// player 和 list 里的牌都应该属于被洗牌的那个人。如果两者不一致，
    /// 说明问题出在 Owner 上，而不是特效那一层。
    /// </summary>
    private static void ShufflePrefix(MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        try
        {
            string owner = "?";
            string charId = "?";

            try
            {
                var discard = MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard.GetPile(player);

                if (discard.Cards.Count > 0)
                {
                    CardModel first = discard.Cards[0];
                    owner = first.Owner?.NetId.ToString() ?? "owner-null";
                    charId = first.Owner?.Character?.Id.Entry ?? "char-null";
                }
                else
                {
                    owner = "empty";
                    charId = "empty";
                }
            }
            catch (Exception e)
            {
                owner = "err:" + e.GetType().Name;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] shuffle start: player={player.NetId} " +
                $"playerChar={player.Character?.Id.Entry} firstCardOwner={owner} " +
                $"firstCardChar={charId}");
        }
        catch
        {
            // ignored
        }
    }

    private static void Prefix(ref string trailPath)
    {
        try
        {
            if (!SpectateOverlay.IsOpen)
            {
                return;
            }

            string? wanted = SpectateOverlay.Target?.Character?.TrailPath;

            if (string.IsNullOrEmpty(wanted) || wanted == trailPath)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] shuffle trail: {trailPath} -> {wanted}");

            trailPath = wanted!;
        }
        catch
        {
            // ignored
        }
    }
}
