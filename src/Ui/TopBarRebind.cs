using System;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
// 游戏源码里这两个类的命名空间是小写的 sts2，与 NTopBarDeckButton 的大写不同。
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.addons.mega_text;
using static SpectateMate.Ui.ReflectionUtil;

namespace SpectateMate.Ui;

/// <summary>
/// 把顶栏的三个控件从「我」重绑到「被观战者」：金币、生命、卡组。
///
/// 统一套路：解绑旧目标的事件 → 换 _player / _pile 字段 → 绑新事件 → 手动刷一次显示。
/// 之所以不能直接调 Initialize，是因为它会重新订阅、可能重复加监听器，
/// 而且 NTopBarDeckButton.Initialize 还会重建内部状态。
///
/// 任何一环反射失败都退回 Initialize，保证至少数字是对的。
/// </summary>
internal static class TopBarRebind
{
    public static void Gold(NTopBarGold? gold, Player player)
    {
        if (gold == null || !GodotObject.IsInstanceValid(gold))
        {
            return;
        }

        EventInfo? evt = typeof(Player).GetEvent("GoldChanged");
        MethodInfo? method = typeof(NTopBarGold).GetMethod(
            "UpdateGold",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (evt == null || method == null)
        {
            gold.Initialize(player);
            return;
        }

        Delegate? handler = Delegate.CreateDelegate(evt.EventHandlerType!, gold, method);
        if (handler == null)
        {
            gold.Initialize(player);
            return;
        }

        Player? previous = Read<Player?>(gold, "_player");
        if (previous != null)
        {
            evt.RemoveEventHandler(previous, handler);
        }

        Set(gold, "_player", player);
        evt.AddEventHandler(player, handler);
        Set(gold, "_currentGold", player.Gold);

        if (Peek(gold, "_goldLabel") is MegaLabel label)
        {
            label.SetTextAutoSize($"{player.Gold}");
        }
    }

    /// <summary>
    /// 卡组按钮：显示他的牌组数量；因为 _player 被重指，点击时打开的是他的牌组。
    /// </summary>
    public static void Deck(NTopBarDeckButton? deck, Player player)
    {
        if (deck == null || !GodotObject.IsInstanceValid(deck))
        {
            return;
        }

        EventInfo? onAdd = typeof(CardPile).GetEvent("CardAddFinished");
        EventInfo? onRemove = typeof(CardPile).GetEvent("CardRemoveFinished");
        MethodInfo? changed = typeof(NTopBarDeckButton).GetMethod(
            "OnPileContentsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (onAdd == null || onRemove == null || changed == null)
        {
            deck.Initialize(player);
            return;
        }

        Delegate? handler = Delegate.CreateDelegate(onAdd.EventHandlerType!, deck, changed);
        if (handler == null)
        {
            deck.Initialize(player);
            return;
        }

        if (Peek(deck, "_pile") is CardPile oldPile)
        {
            onAdd.RemoveEventHandler(oldPile, handler);
            onRemove.RemoveEventHandler(oldPile, handler);
        }

        CardPile newPile = PileType.Deck.GetPile(player);
        Set(deck, "_player", player);
        Set(deck, "_pile", newPile);
        onAdd.AddEventHandler(newPile, handler);
        onRemove.AddEventHandler(newPile, handler);

        InvokeMethod(deck, "OnPileContentsChanged");
    }

    public static void Hp(NTopBarHp? hp, Player player)
    {
        if (hp == null || !GodotObject.IsInstanceValid(hp))
        {
            return;
        }

        EventInfo? onCurrent = typeof(Creature).GetEvent("CurrentHpChanged");
        EventInfo? onMax = typeof(Creature).GetEvent("MaxHpChanged");
        MethodInfo? method = typeof(NTopBarHp).GetMethod(
            "UpdateHealth",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (onCurrent == null || onMax == null || method == null)
        {
            hp.Initialize(player);
            return;
        }

        Delegate? handler = Delegate.CreateDelegate(onCurrent.EventHandlerType!, hp, method);
        if (handler == null)
        {
            hp.Initialize(player);
            return;
        }

        Player? previous = Read<Player?>(hp, "_player");
        if (previous?.Creature is { } oldCreature)
        {
            onCurrent.RemoveEventHandler(oldCreature, handler);
            onMax.RemoveEventHandler(oldCreature, handler);
        }

        Set(hp, "_player", player);

        if (player.Creature is { } creature)
        {
            onCurrent.AddEventHandler(creature, handler);
            onMax.AddEventHandler(creature, handler);

            if (Peek(hp, "_hpLabel") is MegaLabel label)
            {
                label.SetTextAutoSize($"{creature.CurrentHp}/{creature.MaxHp}");
            }
        }
    }
}
