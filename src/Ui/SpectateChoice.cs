using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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

            // 断开一切输入：观战者不能替他做选择。
            _screen.ProcessMode = Node.ProcessModeEnum.Disabled;

            // ProcessMode.Disabled 会暂停淡入补间，手动补上最终状态。
            _screen.Modulate = Colors.White;

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

    /// <summary>退出观战时清空全部状态。</summary>
    public static void Reset()
    {
        Close();
        _cards = null;
        _owner = null;
        _canSkip = false;
    }
}
