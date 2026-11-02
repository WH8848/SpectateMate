using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using static SpectateMate.Ui.ReflectionUtil;

namespace SpectateMate.Ui;

/// <summary>
/// 观战期间屏蔽本地交互。
///
/// 只锁「手牌区」和「结束回合」两处——牌堆、遗物、药水保持可点，
/// 这样观战时还能翻他的抽取/弃牌堆。
///
/// 出牌本身不靠这里：HandPlayBlockPatch 在 StartCardPlay 上直接返回 false，
/// 鼠标拖拽和数字键都会经过那个入口。这里只负责让按钮和输入回调静默。
///
/// 恢复时按捕获的快照逐项还原，而不是一律设成 true——
/// 把原本没有输入能力的节点强行打开，会破坏目标选择之类的既有状态。
/// </summary>
internal static class InteractionBlocker
{
    private static readonly Dictionary<Node, NodeState> Saved = new();

    public static void Set(bool enabled)
    {
        if (enabled)
        {
            Restore();
            return;
        }

        Guard("hand-keys", () => Flags(SpectateUi.Hand));
        Guard("end-turn", () => Disable(SpectateUi.CombatUi?.EndTurnButton));
        Guard("potions", () => Disable(SpectateUi.GlobalUi?.TopBar?.PotionContainer));
    }

    private static void Flags(Node? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        if (!Saved.ContainsKey(node))
        {
            Saved[node] = Snapshot(node);
        }

        node.SetProcessInput(false);
        node.SetProcessUnhandledInput(false);
        node.SetProcessUnhandledKeyInput(false);
    }

    private static void Disable(Node? node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        if (!Saved.ContainsKey(node))
        {
            Saved[node] = Snapshot(node);
        }

        node.ProcessMode = Node.ProcessModeEnum.Disabled;
    }

    private static void Restore()
    {
        foreach (KeyValuePair<Node, NodeState> pair in Saved)
        {
            Node node = pair.Key;
            NodeState state = pair.Value;

            try
            {
                if (node == null || !GodotObject.IsInstanceValid(node))
                {
                    continue;
                }

                node.ProcessMode = state.ProcessMode;
                node.SetProcessInput(state.ProcessInput);
                node.SetProcessUnhandledInput(state.ProcessUnhandledInput);
                node.SetProcessUnhandledKeyInput(state.ProcessUnhandledKeyInput);

                if (state.IsControl && node is Control control)
                {
                    control.MouseFilter = state.MouseFilter;
                }
            }
            catch
            {
                // 节点随战斗一起销毁了
            }
        }

        Saved.Clear();
    }

    public static void Clear()
    {
        Saved.Clear();
    }

    private sealed class NodeState
    {
        public Node.ProcessModeEnum ProcessMode;
        public bool ProcessInput;
        public bool ProcessUnhandledInput;
        public bool ProcessUnhandledKeyInput;
        public bool IsControl;
        public Control.MouseFilterEnum MouseFilter;
    }

    private static NodeState Snapshot(Node node)
    {
        return new NodeState
        {
            ProcessMode = node.ProcessMode,
            ProcessInput = node.IsProcessingInput(),
            ProcessUnhandledInput = node.IsProcessingUnhandledInput(),
            ProcessUnhandledKeyInput = node.IsProcessingUnhandledKeyInput(),
            IsControl = node is Control,
            MouseFilter = node is Control control ? control.MouseFilter : Control.MouseFilterEnum.Stop,
        };
    }

    private static void Guard(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] ui step '{name}' failed: {e.Message}");
        }
    }
}
