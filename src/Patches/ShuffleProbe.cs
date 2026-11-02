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
            // CardPile 本身不持有 owner，从它里面的第一张牌取。
            string owner = "?";

            try
            {
                CardModel? sample = startPile?.Cards.Count > 0 ? startPile.Cards[0] : null;
                owner = sample?.Owner?.NetId.ToString() ?? "null";
            }
            catch
            {
                owner = "err";
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] shuffle vfx: {start}->{target} owner={owner} trail={trailPath}");
        }
        catch
        {
            // ignored
        }
    }
}
