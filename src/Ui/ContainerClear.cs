using System.Collections;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using static SpectateMate.Ui.ReflectionUtil;

namespace SpectateMate.Ui;

/// <summary>
/// 清空原生容器里的内容节点（遗物栏、药水栏）。
///
/// 观战时这些控件显示的是「我」的遗物和药水。由于每次战斗/遗物变化时
/// 游戏会自己往里塞节点，直接把容器的 _player 指过去并重建列表不可靠，
/// 这里改为：断开旧的事件订阅 → 释放并清空内部列表 → 把 _player 置空，
/// 之后由 SpectateUi 按需重建。
///
/// 所有字段访问都走 ReflectionUtil，任何一步拿不到就跳过，不会中断其它步骤。
/// </summary>
internal static class ContainerClear
{
    public static void Relics(NRelicInventory inventory)
    {
        Invoke(inventory, "DisconnectPlayerEvents");

        if (Read<IList?>(inventory, "_relicNodes") is { } nodes)
        {
            FreeAll(nodes);
            nodes.Clear();
        }

        Set(inventory, "_player", null);
    }

    public static void Potions(NPotionContainer container)
    {
        Invoke(container, "DisconnectPlayerEvents");

        if (Read<IList?>(container, "_holders") is { } holders)
        {
            FreeAll(holders);
            holders.Clear();
        }

        if (Read<Node?>(container, "_potionHolders") is { } holderRoot && GodotObject.IsInstanceValid(holderRoot))
        {
            foreach (Node child in holderRoot.GetChildren())
            {
                holderRoot.RemoveChild(child);
                child.QueueFree();
            }
        }

        Set(container, "_focusedHolder", null);
        Set(container, "_player", null);
    }

    /// <summary>
    /// 释放列表里的所有节点。先做快照，避免遍历时列表被修改。
    /// </summary>
    private static void FreeAll(IList list)
    {
        var snapshot = new List<object?>(list.Count);
        foreach (object? item in list)
        {
            snapshot.Add(item);
        }

        foreach (object? item in snapshot)
        {
            if (item is Node node && GodotObject.IsInstanceValid(node))
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }
        }
    }
}
