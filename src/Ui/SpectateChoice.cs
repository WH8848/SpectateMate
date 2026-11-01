using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace SpectateMate.Ui;

/// <summary>
/// 观战时复用原版选牌界面，只读展示被观战者的候选。
///
/// 关键约束：
/// * NChooseACardSelectionScreen.CardsSelected() 只有它自己被 await 时才会把界面
///   从 NOverlayStack 移出。我们不 await（不能替他做选择），所以关闭必须自己 Remove。
/// * 界面内被点击会 _completionSource.SetResult(...)，虽然不会抛异常，但界面不会关，
///   所以必须 ProcessMode.Disabled 断掉输入。
/// * Disabled 会同时暂停 AfterOverlayOpened 里的淡入补间，需手动把 Modulate 设为白。
/// </summary>
public static class SpectateChoice
{
    private static IReadOnlyList<CardModel>? _cards;
    private static bool _canSkip;
    private static Player? _owner;
    private static NChooseACardSelectionScreen? _screen;

    public static void Remember(IReadOnlyList<CardModel> cards, bool canSkip, Player player)
    {
        _cards = cards;
        _canSkip = canSkip;
        _owner = player;
    }

    /// <summary>每拍调用：他在选牌就打开只读界面，选完就关掉。</summary>
    public static void Tick(Player? target, NetScreenType screen)
    {
        bool shouldShow =
            target != null &&
            _cards != null &&
            _cards.Count > 0 &&
            _owner != null &&
            _owner.NetId == target.NetId &&
            screen == NetScreenType.CardSelection;

        if (shouldShow)
        {
            Show();
        }
        else
        {
            Close();
        }
    }

    private static void Show()
    {
        try
        {
            if (_screen != null && GodotObject.IsInstanceValid(_screen))
            {
                return;
            }

            _screen = NChooseACardSelectionScreen.ShowScreen(_cards!, _canSkip);
            if (_screen == null || !GodotObject.IsInstanceValid(_screen))
            {
                _screen = null;
                return;
            }

            // 不能直接把 ProcessMode 设成 Disabled：那会连 _Ready 里的
            // 卡片飞入补间和淡入一起冻住，三张卡会叠在初始位置，看起来只有一张。
            // 改为逐个掐掉输入源。
            _screen.MouseFilter = Control.MouseFilterEnum.Ignore;

            int disabled = 0;
            foreach (Node node in _screen.FindChildren("*", string.Empty, true, false))
            {
                switch (node)
                {
                    case NCardHolder holder:
                        holder.Hitbox.SetEnabled(false);
                        holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
                        disabled++;
                        break;

                    case BaseButton button:
                        button.Disabled = true;
                        disabled++;
                        break;
                }
            }

            ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: input disabled on {disabled} nodes");

            ModEntry.Log.Info($"[{ModEntry.ModId}] choice peek: showing {_cards!.Count} candidates");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice peek show failed: {e.Message}");
            _screen = null;
        }
    }

    /// <summary>关闭只读界面。重复调用安全。</summary>
    public static void Close()
    {
        try
        {
            if (_screen != null && GodotObject.IsInstanceValid(_screen))
            {
                NOverlayStack.Instance?.Remove(_screen);
            }
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] choice peek close failed: {e.Message}");
        }

        _screen = null;
    }

    /// <summary>
    /// 退出观战时只关界面，保留最近一次候选。
    /// 他可能还停在选牌界面没动，重新观战时要能立刻再看到。
    /// </summary>
    public static void Reset()
    {
        Close();
    }
}
