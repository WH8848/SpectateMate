using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpectateMate.Patches;

/// <summary>
/// 洗牌光效的探针。
///
/// 原版 CardPileCmd 里洗牌是这样触发的：
///
///     string trailPath = card.Owner.Character.TrailPath;
///     Node vfxContainer = card.Owner.Creature.GetVfxContainer();
///     NCardFlyShuffleVfx.Create(oldPile, targetPile, trailPath);
///
/// 也就是说颜色完全由 trailPath 决定，而它取自牌的 Owner。
/// 观战如果看到的是自己的颜色，说明这条链上的 Owner 不对。
///
/// 这里不修改任何行为，只在创建时记下 trailPath 和两端牌堆的数量，
/// 用来判断到底是「路径本身就是对方的」还是「被换成了本地玩家的」。
/// </summary>
internal static class ShuffleProbe
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.shuffle");

            var target = AccessTools.Method(
                typeof(NCardFlyShuffleVfx),
                "Create",
                new[] { typeof(CardPile), typeof(CardPile), typeof(string) });

            if (target == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] shuffle probe: Create not found");
                return;
            }

            _harmony.Patch(target, postfix: new HarmonyMethod(typeof(ShuffleProbe), nameof(Postfix)));
            ModEntry.Log.Info($"[{ModEntry.ModId}] shuffle probe: hooked");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] shuffle probe failed: {e.Message}");
        }
    }

    private static void Postfix(CardPile startPile, CardPile targetPile, string trailPath)
    {
        try
        {
            string start = startPile != null ? startPile.Type.ToString() : "?";
            string target = targetPile != null ? targetPile.Type.ToString() : "?";
            // CardPile 本身不持有 owner，从它里面的牌取。
            string owner = "?";
            string character = "?";
            int cardCount = -1;

            try
            {
                cardCount = startPile?.Cards.Count ?? -1;

                CardModel? sample = startPile != null && startPile.Cards.Count > 0
                    ? startPile.Cards[0]
                    : null;

                if (sample != null)
                {
                    owner = sample.Owner?.NetId.ToString() ?? "owner-null";
                    character = sample.Owner?.Character?.Id.Entry ?? "char-null";
                }
                else
                {
                    owner = "no-card";
                    character = "no-card";
                }
            }
            catch (Exception e)
            {
                owner = "err:" + e.GetType().Name;
            }

            ulong localId = 0;
            try
            {
                localId = MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.NetId;
            }
            catch
            {
                localId = 0;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] shuffle vfx: {start}->{target} cards={cardCount} " +
                $"owner={owner} char={character} local={localId} trail={trailPath}");
        }
        catch
        {
            // ignored
        }
    }
}
