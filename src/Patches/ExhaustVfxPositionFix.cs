using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using SpectateMate.Ui;

namespace SpectateMate.Patches;

/// <summary>
/// 修复：卡牌消耗时被搬到左上角结算。
///
/// NCardExhaustVfx.Create 的流程：

///     vfx._position = cardNode.GlobalPosition;              // 记住卡片此刻的位置
///     cardNode.GetParent()?.RemoveChildSafely(cardNode);
///     vfx._cardParentContainer.AddChildSafely(cardNode);    // 卡片挪进自己的容器
///
/// 然后 PlayAnimation：

///     base.GlobalPosition = _position;                      // 特效摆回卡片原处
///     _cardNode.Position = _cardParentContainer.Size / 2f;  // 卡片在容器里居中
///
/// 所以卡片的最终全局位置 = _position + 容器尺寸的一半。
/// 容器是 512x512，一半就是 (256, 256)——日志里那些
/// 「MALAISE pos=(256,256) parent=CenterContainer」正是这个值。
///
/// 一旦 Create 被调用时卡片还在 (0,0)（刚创建、尚未布局），
/// _position 就被记成零，特效摆到左上角，卡片随之出现在 (256,256)。
///
/// 修正：Prefix 先记下卡片当时的位置；Postfix 若发现它无效，
/// 直接把 _position 改成播放区中心。只改特效的摆放位置，
/// 不碰卡片模型、不碰原版的搬运逻辑。
/// </summary>
internal static class ExhaustVfxPositionFix
{
    private static Harmony? _harmony;

    /// <summary>小于这个距离就认为位置无效。</summary>
    private const float MinValidDistance = 32f;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.exhaustvfxpos");

            var create = AccessTools.Method(typeof(NCardExhaustVfx), "Create", new[] { typeof(NCard) });

            if (create == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] exhaust vfx pos: hook target missing");
                return;
            }

            _harmony.Patch(
                create,
                prefix: new HarmonyMethod(typeof(ExhaustVfxPositionFix), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(ExhaustVfxPositionFix), nameof(Postfix)));

            ModEntry.Log.Info($"[{ModEntry.ModId}] exhaust vfx pos: hooked");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] exhaust vfx pos install failed: {e.Message}");
        }
    }

    private static void Prefix(NCard cardNode, out Vector2 __state)
    {
        __state = Vector2.Zero;

        try
        {
            __state = cardNode?.GlobalPosition ?? Vector2.Zero;
        }
        catch
        {
            __state = Vector2.Zero;
        }
    }

    private static void Postfix(NCardExhaustVfx __result, Vector2 __state)
    {
        try
        {
            if (__result == null || !GodotObject.IsInstanceValid(__result))
            {
                return;
            }

            if (__state.Length() > MinValidDistance)
            {
                return;
            }

            Vector2 center = PlayCenter(__result);

            if (center.Length() <= MinValidDistance)
            {
                return;
            }

            var field = ReflectionUtil.Field(__result.GetType(), "_position");

            if (field == null)
            {
                ModEntry.Log.Warn($"[{ModEntry.ModId}] exhaust vfx pos: _position field not found");
                return;
            }

            field.SetValue(__result, center);

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] exhaust vfx pos FIXED: {__state} -> {center}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] exhaust vfx pos postfix failed: {e.Message}");
        }
    }

    /// <summary>播放区中心。拿不到就退回屏幕中部偏上——和出牌等待区同一高度。</summary>
    private static Vector2 PlayCenter(Node anyNode)
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;

        if (ui?.PlayContainer is { } pc && pc.Size.X > 1f && pc.Size.Y > 1f)
        {
            Vector2 local = new(pc.Size.X * 0.5f, pc.Size.Y * 0.5f - 100f);
            return pc.GetGlobalTransform() * local;
        }

        Vector2 size = anyNode.GetTree()?.Root?.GetVisibleRect().Size ?? Vector2.Zero;

        if (size.X < 1f || size.Y < 1f)
        {
            size = new Vector2(1920f, 1080f);
        }

        return new Vector2(size.X * 0.5f, size.Y * 0.5f - 100f);
    }
}
