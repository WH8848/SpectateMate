using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SpectateMate.Ui;

/// <summary>
/// 原生手牌可见性的唯一管理者。
///
/// 观战时需要把 CardHolderContainer 整体调到 alpha=0 来隐藏你的手牌
/// （按节点逐个隐藏不可行：新回合新建的 holder 会漏网）。
/// 但这份透明度如果没能恢复，玩家自己的手牌就会整片看不见——
/// 因此这里的每个入口都做成幂等，并提供 Resolve 这条多级回退链。
/// </summary>
public static class VanillaHandVisibility
{
    private static bool _hidden;
    private static Color _saved = Colors.White;

    /// <summary>容器当前是否被我们调成了透明。</summary>
    public static bool IsHidden => _hidden;

    /// <summary>只清标志，不碰节点。用于战斗切换时作废旧状态。</summary>
    public static void ForgetState()
    {
        _hidden = false;
        _saved = Colors.White;
    }

    /// <summary>从任意可用来源解析出当前的 CardHolderContainer。</summary>
    public static Control? Resolve(NPlayerHand? preferred)
    {
        return ContainerOf(preferred)
            ?? ContainerOf(SpectateUi.Hand)
            ?? ContainerOf(NCombatRoom.Instance?.Ui?.Hand);
    }

    public static Control? ContainerOf(NPlayerHand? hand)
    {
        try
        {
            return hand != null && GodotObject.IsInstanceValid(hand) ? hand.CardHolderContainer : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把容器整体调成透明。</summary>
    public static void Hide(NPlayerHand? hand)
    {
        Control? container = Resolve(hand);
        if (container == null)
        {
            return;
        }

        if (!_hidden || _saved.A <= 0f)
        {
            // 防御：万一保存时容器已经是透明的，恢复时会把它永远留在 0。
            _saved = container.Modulate.A > 0f ? container.Modulate : Colors.White;
            _hidden = true;
        }

        container.Modulate = new Color(_saved.R, _saved.G, _saved.B, 0f);
    }

    /// <summary>恢复容器原本的透明度。</summary>
    public static void Show(NPlayerHand? hand)
    {
        Control? container = Resolve(hand);
        if (container == null)
        {
            return;
        }

        if (_hidden)
        {
            container.Modulate = _saved;
            _hidden = false;
        }
    }

    /// <summary>
    /// 压掉原生 holder 的编号标签。
    /// 容器的 Modulate 只能保证「通常」看不见；只要某个 holder 的调制被单独覆盖，
    /// %HandIndex 就会露出来。SetIndexLabel(0) 会同时清空文字并置 Visible=false。
    /// 本地玩家每次抽牌都会新建 holder，所以要每拍调用（幂等且便宜）。
    /// </summary>
    public static void HideLabels(NPlayerHand? hand)
    {
        try
        {
            Control? container = Resolve(hand);
            if (container == null)
            {
                return;
            }

            foreach (Node child in container.GetChildren())
            {
                if (child is NHandCardHolder holder && GodotObject.IsInstanceValid(holder))
                {
                    holder.SetIndexLabel(0);
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 进入战斗时无条件复位。
    ///
    /// 观战时设下的 alpha=0 如果因为 hand 失效而没能恢复，容器会带着透明状态
    /// 活到下一场战斗，玩家自己的手牌就整片看不见，且只有「再观战一次」才修好。
    /// 这里不依赖任何历史状态，只要发现容器是透明的就恢复。
    /// </summary>
    public static void Reset()
    {
        _hidden = false;
        _saved = Colors.White;

        try
        {
            Control? container = Resolve(null);

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] reset vanilla state: container={(container != null)} " +
                $"alpha={container?.Modulate.A}");

            if (container == null)
            {
                return;
            }

            if (container.Modulate.A <= 0f)
            {
                container.Modulate = Colors.White;
                ModEntry.Log.Info($"[{ModEntry.ModId}] vanilla hand container was left transparent; restored");
            }
        }
        catch
        {
            // ignored
        }
    }
}
