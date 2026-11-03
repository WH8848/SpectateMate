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
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] shuffle trail patch failed: {e.Message}");
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
    private static bool Prefix(CardPile startPile, CardPile targetPile, ref string trailPath)
    {
        try
        {
            // 同一个牌堆之间没有发生移动，不该有光效。
            //
            // CardPileCmd.Shuffle 会把抽牌堆的牌也并进待处理列表：
            //     list.AddRange(PileType.Draw.GetPile(player).Cards);
            // 弃牌堆为空时，list 里全是本来就在抽牌堆的牌，
            // Add 之后 oldPile == newPile == Draw，但填特效列表的条件
            // （旧堆和新堆都在 Draw/Discard/Deck 里）依然成立，
            // 于是「这一回合根本没洗牌」却播了一次洗牌动画。
            if (startPile != null && ReferenceEquals(startPile, targetPile))
            {
                return false;
            }

            if (!SpectateOverlay.IsOpen)
            {
                return true;
            }

            string? wanted = SpectateOverlay.Target?.Character?.TrailPath;

            if (string.IsNullOrEmpty(wanted))
            {
                return true;
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
