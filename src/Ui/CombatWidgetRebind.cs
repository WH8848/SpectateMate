using System;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.addons.mega_text;
using static SpectateMate.Ui.ReflectionUtil;

namespace SpectateMate.Ui;

/// <summary>
/// 把战斗中的三个牌堆按钮和能量球重绑到被观战者。
///
/// 共同点：这些控件在战斗开始时只 Initialize 一次，之后一直持有「我」的牌堆/玩家引用。
/// 观战是在战斗中途开始的，所以必须手动把引用换过去，否则数字和内容仍是自己的。
///
/// 换引用时一律先解绑旧目标的事件委托，否则同一个事件会同时喂给两个对象，
/// 表现为数字跳变或动画重复播放。
/// </summary>
internal static class CombatWidgetRebind
{
    public static void PileButton(NCombatCardPile? pile, Player player)
    {
        if (pile == null || !GodotObject.IsInstanceValid(pile))
        {
            return;
        }

        CardPile? current = Peek(pile, "_pile") as CardPile;
        PileType type = current?.Type ?? PileType.Discard;
        CardPile next;

        try
        {
            next = type.GetPile(player);
        }
        catch
        {
            return;
        }

        if (current == next)
        {
            return;
        }

        MethodInfo? onAdd = Method(pile.GetType(), "AddCard");
        MethodInfo? onRemove = Method(typeof(NCombatCardPile), "RemoveCard");

        Delegate? addHandler = null;
        Delegate? removeHandler = null;

        EventArgsPair(ref addHandler, ref removeHandler, pile, current, onAdd, onRemove);

        EventInfo? evtAdd = typeof(CardPile).GetEvent("CardAddFinished");
        EventInfo? evtRemove = typeof(CardPile).GetEvent("CardRemoveFinished");

        if (current != null)
        {
            if (evtAdd != null && addHandler != null) evtAdd.RemoveEventHandler(current, addHandler);
            if (evtRemove != null && removeHandler != null) evtRemove.RemoveEventHandler(current, removeHandler);
        }

        Set(pile, "_pile", next);

        if (evtAdd != null && addHandler != null) evtAdd.AddEventHandler(next, addHandler);
        if (evtRemove != null && removeHandler != null) evtRemove.AddEventHandler(next, removeHandler);

        Set(pile, "_currentCount", next.Cards.Count);
        if (Peek(pile, "_countLabel") is MegaLabel label)
        {
            label.SetTextAutoSize(next.Cards.Count.ToString());
        }

        // 消耗牌堆的可见性只在 Initialize 里按 Cards.Count 判断一次：
        //
        //   public override void Initialize(Player player) {
        //       base.Initialize(player);
        //       if (Pile.GetPile(player).Cards.Count > 0) { Visible = true; Enable(); }
        //   }
        //
        // 观战是战斗中途开始的，不会走 Initialize，按钮就停在我们自己的状态上
        // ——自己没有消耗牌时它一直是隐藏的，表现为「消耗牌堆消失」。
        if (pile is NExhaustPileButton exhaust)
        {
            // 有牌就滑入显示，没牌就滑出隐藏——退出观战重绑回自己时同样适用，
            // 否则队友有消耗牌、自己没有时，按钮会一直留在屏幕上。
            if (next.Cards.Count > 0)
            {
                exhaust.AnimIn();
            }
            else
            {
                exhaust.AnimOut();
            }
        }

        ModEntry.Log.Info(
            $"[{ModEntry.ModId}] pile {type} -> netId={player.NetId} ({next.Cards.Count} cards)");
    }

    /// <summary>
    /// 同步消耗牌堆按钮的可见性。
    ///
    /// NExhaustPileButton 只在 Initialize 里按 Cards.Count 判断一次是否显示，
    /// 另外就是 AddCard 时 AnimIn。虚无牌是「从手牌直接消耗」，
    /// 既不经过 Initialize 也不经过 AddCard，按钮就一直藏着——
    /// 观战时会觉得「进阶之灾消耗了，但消耗牌堆没出现」。
    /// </summary>
    public static void SyncExhaustVisibility(NCombatCardPile? pile, int count)
    {
        if (pile is not NExhaustPileButton exhaust || !GodotObject.IsInstanceValid(exhaust))
        {
            return;
        }

        if (count > 0)
        {
            if (!exhaust.Visible)
            {
                exhaust.AnimIn();
            }
        }
        else if (exhaust.Visible)
        {
            exhaust.AnimOut();
        }
    }

    /// <summary>每拍强制对齐牌堆计数（远端变化不会为本机发事件）。</summary>
    public static void SyncPileCount(NCombatCardPile? pile, int count)
    {
        if (pile == null || !GodotObject.IsInstanceValid(pile))
        {
            return;
        }

        if (Read<int>(pile, "_currentCount") == count)
        {
            return;
        }

        Set(pile, "_currentCount", count);

        if (Peek(pile, "_countLabel") is MegaLabel label)
        {
            label.SetTextAutoSize(count.ToString());
        }
    }

    /// <summary>
    /// 重建或重指能量球。不同角色的能量球是不同的场景资源，所以要先比对
    /// EnergyCounterPath：换了角色就整体重建，同一个角色只换引用。
    /// </summary>
    public static void RebuildEnergy(NCombatUi? ui, Player shown)
    {
        if (ui == null || !GodotObject.IsInstanceValid(ui))
        {
            return;
        }

        NEnergyCounter? current = Find<NEnergyCounter>(ui, "_energyCounter");
        if (current == null || !GodotObject.IsInstanceValid(current))
        {
            // 还没进战斗，Activate 补丁会正确构建。
            return;
        }

        string? wanted = SafePath(shown);
        string? actual = SafePath(Read<Player?>(current, "_player"));

        if (wanted == actual)
        {
            RebindEnergy(current, shown);
            return;
        }

        NEnergyCounter? fresh = NEnergyCounter.Create(shown);
        if (fresh == null)
        {
            RebindEnergy(current, shown);
            return;
        }

        Node? container = ui.EnergyCounterContainer;
        if (container == null)
        {
            fresh.QueueFree();
            return;
        }

        container.AddChild(fresh);

        if (Find<NStarCounter>(ui, "_starCounter") is { } stars && GodotObject.IsInstanceValid(stars))
        {
            stars.Reparent(fresh, true);

            // 必须把 _player 也换过去，而且要先手动复位可见性。
            //
            // NStarCounter 里两处都是自或：
            //   RefreshVisibility: Visible = Visible || ShouldAlwaysShowStarCounter || stars > 0
            //   _Process:          同上
            // 一旦显示过就再也不会自动收起。所以光调 Initialize 是关不掉的，
            // 它内部的 RefreshVisibility 也会被那个 true 锁死。
            // 必须先显式置 false，再让 Initialize 按新角色重新判定。
            stars.Visible = false;
            stars.Initialize(shown);
        }

        Set(ui, "_energyCounter", fresh);
        current.QueueFree();
    }

    private static void EventArgsPair(
        ref Delegate? addHandler,
        ref Delegate? removeHandler,
        NCombatCardPile pile,
        CardPile? current,
        MethodInfo? onAdd,
        MethodInfo? onRemove)
    {
        try
        {
            EventInfo? evtAdd = typeof(CardPile).GetEvent("CardAddFinished");
            EventInfo? evtRemove = typeof(CardPile).GetEvent("CardRemoveFinished");
            if (evtAdd == null || evtRemove == null || onAdd == null || onRemove == null)
            {
                return;
            }

            addHandler = Delegate.CreateDelegate(evtAdd.EventHandlerType!, pile, onAdd);
            removeHandler = Delegate.CreateDelegate(evtRemove.EventHandlerType!, pile, onRemove);
        }
        catch
        {
            addHandler = null;
            removeHandler = null;
        }
    }

    /// <summary>
    /// 重指能量球：解绑旧 EnergyChanged、换 _player、绑新委托、刷一次标签。
    /// 少了这一步，数字会一直停在上一个被观战者的能量。
    /// </summary>
    private static void RebindEnergy(NEnergyCounter counter, Player player)
    {
        if (EnergyChangedEvent == null)
        {
            Set(counter, "_player", player);
            InvokeMethod(counter, "RefreshLabel");
            return;
        }

        Player? previous = Read<Player?>(counter, "_player");
        Action<int, int>? handler = CreateEnergyHandler(counter);
        if (handler != null && previous?.PlayerCombatState is { } oldState)
        {
            EnergyChangedEvent.RemoveEventHandler(oldState, handler);
        }

        Set(counter, "_player", player);

        if (handler != null && player.PlayerCombatState is { } newState)
        {
            EnergyChangedEvent.AddEventHandler(newState, handler);
        }

        InvokeMethod(counter, "RefreshLabel");
    }

    private static readonly EventInfo? EnergyChangedEvent =
        typeof(PlayerCombatState).GetEvent("EnergyChanged");

    private static Action<int, int>? CreateEnergyHandler(NEnergyCounter counter)
    {
        try
        {
            MethodInfo? method = typeof(NEnergyCounter).GetMethod(
                "OnEnergyChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                return null;
            }

            return (Action<int, int>?)Delegate.CreateDelegate(typeof(Action<int, int>), counter, method);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafePath(Player? player)
    {
        try
        {
            return player?.Character?.EnergyCounterPath;
        }
        catch
        {
            return null;
        }
    }
}
