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

    /// <summary>
    /// 观战时只保留被观战者的洗牌光效。
    ///
    /// 日志实测：
    ///     shuffle start: player=IRONCLAD(本地) firstCardOwner=IRONCLAD
    ///     shuffle vfx:   shufflePlayer=IRONCLAD target=SILENT trail=ironclad  ×11
    ///
    /// 洗牌是确定性锁步，两端都会执行同一个 Shuffle，所以本地玩家洗牌时
    /// 也会在本机创建特效——它就播在你的屏幕上，颜色自然是本地角色的。
    /// 而观战自己的牌堆根本看不见，这个特效纯属干扰。
    ///
    /// 猎人（被观战者）的洗牌那次反而一条 vfx 日志都没有，说明他的特效
    /// 本来就已经正确（或者没有牌需要飞）。
    ///
    /// 所以：观战期间，凡是不是被观战者发起的洗牌，直接不创建特效。
    /// </summary>
    /// <summary>
    /// 观战时把洗牌光带的贴图强制换成被观战者的。
    ///
    /// CharacterModel.TrailPath 就是按角色 Id 拼出来的：
    ///     "vfx/card_trail_" + Id.Entry.ToLowerInvariant()
    /// 所以拿到 Target.Character.TrailPath 就等于拿到了正确颜色：
    ///     IRONCLAD -> 红, SILENT -> 绿, REGENT -> 黄,
    ///     NECROBINDER -> 紫, DEFECT -> 蓝
    ///
    /// 不再判断「这次洗牌是谁发起的」。洗牌是确定性锁步，两端都会执行，
    /// 屏幕上只会出现一份光效，直接按观战目标取色即可，
    /// 这样不会因为归属判断出错而漏掉替换。
    /// </summary>
    private static bool Prefix(ref string trailPath)
    {
        try
        {
            if (!SpectateOverlay.IsOpen)
            {
                return true;
            }

            string? wanted = SpectateOverlay.Target?.Character?.TrailPath;

            if (string.IsNullOrEmpty(wanted))
            {
                return true;
            }

            if (wanted != trailPath)
            {
                ModEntry.Log.Info(
                    $"[{ModEntry.ModId}] shuffle trail: {trailPath} -> {wanted}");
            }

            trailPath = wanted!;
            return true;
        }
        catch
        {
            return true;
        }
    }
}
