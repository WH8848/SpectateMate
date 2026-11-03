using System;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpectateMate.Ui;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using System.Reflection;

namespace SpectateMate.Patches;

/// <summary>
/// 诊断：出牌飞到播放区时，容器的状态到底是什么。
///
/// 原版 AppendPileLerpTween 里：
///     Vector2 targetPosition = typePile.GetTargetPosition(cardNode);
///     tween.TweenProperty(cardNode, "position", targetPosition, num);
///
/// 而 PileType.Play 的公式是：
///     PlayContainer.Size * 0.5f - node.Size * 0.5f + Vector2.Up * 100f
///
/// 这个值被当作「相对 PlayContainer 的局部坐标」。如果布局还没完成、
/// PlayContainer.Size 仍是 (0,0)，算出来就贴近 (0,-100)，也就是左上角。
///
/// 观战时 NCombatUi 是现构建的，布局完成时间可能比正常对局晚，
/// 这里把两张卡（加入播放区时、以及补间目标算出时）的状态都记下来。
/// </summary>
internal static class PlayContainerProbe
{
    private static Harmony? _harmony;

    public static void Install()
    {
        try
        {
            _harmony = new Harmony($"{ModEntry.ModId}.playprobe");

            var add = AccessTools.Method(typeof(NCombatUi), "AddToPlayContainer");
            if (add != null)
            {
                _harmony.Patch(add, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(AddPrefix)));
            }

            var lerp = AccessTools.Method(
                typeof(CardPileCmd),
                "AppendPileLerpTween",
                new[] { typeof(Tween), typeof(NCard), typeof(PileType), typeof(PileType?) });

            if (lerp != null)
            {
                _harmony.Patch(lerp, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(LerpPrefix)));
            }

            // NCardFlyVfx 的两个 Create 重载：起点都来自 card.GlobalPosition，
            // 这里把它、局部位置和父节点一起记下来。
            var flyPile = AccessTools.Method(
                typeof(NCardFlyVfx),
                "Create",
                new[] { typeof(NCard), typeof(PileType), typeof(bool), typeof(string) });

            if (flyPile != null)
            {
                _harmony.Patch(flyPile, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(FlyPrefix)));
            }

            var flyCreature = AccessTools.Method(
                typeof(NCardFlyVfx),
                "Create",
                new[] { typeof(NCard), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), typeof(string) });

            if (flyCreature != null)
            {
                _harmony.Patch(flyCreature, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(FlyPrefix)));
            }

            // 其余卡牌特效：它们的 Create 也都以 NCard 为首参，
            // 统统一遍，看是谁在播放区那类位置把卡片拿走的。
            var vfxTypes = new[]
            {
                typeof(NCardTransformVfx),
                typeof(NCardFlyPowerVfx),
                typeof(NCardExhaustVfx),
                typeof(NCardExhaustQuickVfx),
                typeof(NCardRemoveVfx),
                typeof(NCardUpgradeVfx),
            };

            int vfxHooked = 0;

            foreach (Type t in vfxTypes)
            {
                foreach (System.Reflection.MethodInfo m in t.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "Create")
                    {
                        continue;
                    }

                    ParameterInfo[] ps = m.GetParameters();

                    if (ps.Length == 0 || ps[0].ParameterType != typeof(NCard))
                    {
                        continue;
                    }

                    try
                    {
                        _harmony.Patch(m, prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(VfxPrefix)));
                        vfxHooked++;
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            ModEntry.Log.Info($"[{ModEntry.ModId}] vfx probe: hooked {vfxHooked}");

            // 远程玩家卡片的起点由 CreateCardNodeAndUpdateVisuals 决定：
            //     nCard.Position = GetCreatureNode(card.Owner.Creature).IntentContainer.GlobalPosition;
            // 意图栏尚未布局时该值可能是 (0,0)，卡片就从左上角出发。
            var createNode = AccessTools.Method(
                typeof(CardPileCmd),
                "CreateCardNodeAndUpdateVisuals",
                new[] { typeof(CardModel), typeof(PileType?), typeof(PileType), typeof(bool) });

            if (createNode != null)
            {
                _harmony.Patch(
                    createNode,
                    prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(CreateNodePrefix)),
                    postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(CreateNodePostfix)));

                ModEntry.Log.Info($"[{ModEntry.ModId}] create-node probe: hooked");
            }

            // 所有出牌路径（手动 / 自动 / FindOnTable 命中）都会经过这里，
            // 是唯一能兜住「卡片从哪来」的收口点。
            var moveNode = AccessTools.Method(
                typeof(CardPileCmd),
                "MoveCardNodeToNewPileBeforeTween",
                new[] { typeof(NCard), typeof(PileType) });

            if (moveNode != null)
            {
                _harmony.Patch(
                    moveNode,
                    prefix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(MoveNodePrefix)),
                    postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(MoveNodePostfix)));

                ModEntry.Log.Info($"[{ModEntry.ModId}] move-node probe: hooked");
            }

            var addPost = AccessTools.Method(typeof(NCombatUi), "AddToPlayContainer");

            if (addPost != null)
            {
                _harmony.Patch(addPost, postfix: new HarmonyMethod(typeof(PlayContainerProbe), nameof(AddPostfix)));
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play probe: add={add != null} lerp={lerp != null}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] play probe failed: {e.Message}");
        }
    }

    private static ulong _lastScanMs;

    /// <summary>
    /// 每 3 秒把屏幕上的每一张 NCard 的位置扫一遍。
    ///
    /// 手牌有 STUCK 检测、出牌有 play settle 探针，两边都显示位置正常，
    /// 但画面上仍有一张牌停在左上角——说明它走的是这两条之外的路。
    /// 直接全量扫描，找出所有贴在 (0,0) 附近的卡，以及它们的父节点。
    /// </summary>
    public static void ScanCards()
    {
        try
        {
            // 不检查 IsOpen：这个现象在未观战时也会出现，
            // 只在观战期间扫就会永远看不到它。
            ulong now = Godot.Time.GetTicksMsec();

            if (now - _lastScanMs < 1000)
            {
                return;
            }

            _lastScanMs = now;

            // 之前只扫 Ui，但停在左上角的那张牌并不在 Ui 之下，
            // 扫描结果里从来没有出现过它。改成从整个战斗房间开始遍历，
            // 把 VfxContainer、各种特效容器里的卡片也纳入范围。
            Node? ui = NCombatRoom.Instance;

            if (ui == null)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            int count = 0;

            foreach (Node node in ui.FindChildren("*", string.Empty, true, false))
            {
                if (node is not NCard card)
                {
                    continue;
                }

                count++;

                Vector2 g = card.GlobalPosition;

                sb.Append(card.Model?.Id.Entry ?? "?")
                    .Append("@(").Append((int)g.X).Append(',').Append((int)g.Y).Append(')')
                    .Append('/').Append(card.GetParent()?.Name ?? "?")
                    .Append(card.Visible ? "" : "(hidden)")
                    .Append(' ');

                // 卡片被塞进另一张卡的子视口：这是非原版的状态，
                // 它会在离屏渲染容器里算坐标，显示位置完全错位。
                CheckNestedViewport(card);
            }

            ModEntry.Log.Info($"[{ModEntry.ModId}] card scan({count}): {sb}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 飞行特效的起点等于 card.GlobalPosition。
    /// 若此刻卡片已经在屏幕中上部，光带就从那里窜出来，看着像「从左上角飞」。
    /// </summary>
    /// <summary>
    /// 通用：任何以 NCard 为首参的特效 Create，记录卡片此刻的位置。
    /// </summary>
    /// <summary>
    /// 卡片即将离开播放区时，如果它的入场补间还没跑完，先把位置补到落点。
    ///
    /// 入场补间绑在 NCombatRoom 上：
    ///     Tween tween = NCombatRoom.Instance.CreateTween().SetParallel();
    /// 速切导致该节点重建时，补间会被连带中止，卡片停在半路——
    /// 也就是「播放位置有时在正中、有时在左上角」的来源。
    ///
    /// 这个函数之后必定执行 cardNode.PlayPileTween?.Kill()，
    /// 位置就定在那一瞬间，所以调整 prefix 里将要保存的值即可，
    /// 不改变任何结算逻辑、也不动补间本身。
    /// 补间已经跑完（IsRunning 为假）时完全不触发。
    /// </summary>
    private static void CatchUpPlayTween(NCard cardNode)
    {
        try
        {
            Tween? tween = cardNode.PlayPileTween;

            if (tween == null || !tween.IsRunning())
            {
                return;
            }

            Control? play = NCombatRoom.Instance?.Ui?.PlayContainer;

            if (play == null || cardNode.GetParent() != play)
            {
                return;
            }

            Vector2 before = cardNode.GlobalPosition;
            Vector2 local = PileType.Play.GetTargetPosition(cardNode);
            Vector2 target = play.GetGlobalTransform() * local;
            cardNode.GlobalPosition = target;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play tween catch-up: {cardNode.Model?.Id.Entry} " +
                $"{before} -> {target}");
        }
        catch
        {
            // ignored
        }
    }

    private static void MoveNodePrefix(NCard cardNode, PileType newPileType, ref Vector2 __state)
    {
        __state = cardNode?.GlobalPosition ?? Vector2.Zero;

        try
        {
            if (cardNode != null && newPileType != PileType.Play)
            {
                CatchUpPlayTween(cardNode);
                __state = cardNode.GlobalPosition;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            // 不限制 IsOpen：速切之后卡片仍会被继续搬运，
            // 那些记录恰恰是判断它最终停在哪里的关键。
            if (newPileType == PileType.Play && !SpectateOverlay.IsOpen)
            {
                // 进入播放区只在观战期间关心，其余去向一律记录。
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] move-node: {cardNode?.Model?.Id.Entry} -> {newPileType} " +
                $"from={__state} local={cardNode?.Position} " +
                $"parent={cardNode?.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 换堆之后的落点记录。只观察，不修改任何位置。
    /// </summary>
    private static void MoveNodePostfix(NCard cardNode, PileType newPileType, Vector2 __state)
    {
        try
        {
            if (cardNode == null || !GodotObject.IsInstanceValid(cardNode))
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] move-node after: {cardNode.Model?.Id.Entry} -> {newPileType} " +
                $"saved={__state} now={cardNode.GlobalPosition} " +
                $"parent={cardNode.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    private static void CreateNodePrefix(CardModel card, bool owningPlayerIsLocal, ref Vector2 __state)
    {
        __state = Vector2.Zero;

        try
        {
            if (owningPlayerIsLocal)
            {
                return;
            }

            Node? creature = NCombatRoom.Instance?.GetCreatureNode(card.Owner?.Creature!);
            Control? intent = creature is NCreature nc ? nc.IntentContainer : null;
            __state = intent?.GlobalPosition ?? Vector2.Zero;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] create-node: {card.Id.Entry} local={owningPlayerIsLocal} " +
                $"creature={(creature != null)} intent={(__state)} intentSize={intent?.Size}");
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] create-node prefix failed: {e.Message}");
        }
    }

    /// <summary>
    /// 创建节点后的落点记录。只观察，不修改任何位置。
    /// </summary>
    private static void CreateNodePostfix(NCard __result, bool owningPlayerIsLocal, Vector2 __state)
    {
        try
        {
            if (__result == null || owningPlayerIsLocal || !SpectateOverlay.IsOpen)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] create-node after: {__result.Model?.Id.Entry} " +
                $"global={__result.GlobalPosition} intent={__state}");
        }
        catch
        {
            // ignored
        }
    }

    private static void VfxPrefix(object[] __args, System.Reflection.MethodBase __originalMethod)
    {
        try
        {
            if (!SpectateOverlay.IsOpen || __args.Length == 0 || __args[0] is not NCard card)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] vfx {__originalMethod.DeclaringType?.Name}.Create: " +
                $"{card.Model?.Id.Entry} global={card.GlobalPosition} local={card.Position} " +
                $"size={card.Size} scale={card.Scale} " +
                $"parent={card.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    private static void FlyPrefix(NCard card)
    {
        try
        {
            if (!SpectateOverlay.IsOpen)
            {
                return;
            }

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] fly create: {card.Model?.Id.Entry} " +
                $"global={card.GlobalPosition} local={card.Position} size={card.Size} " +
                $"parent={card.GetParent()?.Name.ToString() ?? "null"}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 卡片出现在另一个 PCard 的 CardSubViewport 之下时，记录一次。
    /// CardSubViewport 是 NCard 场景自带的离屏渲染容器，
    /// 正常情况下只会容纳卡片自己的渲染内容，不会装别的 NCard。
    /// </summary>
    private static void CheckNestedViewport(NCard card)
    {
        try
        {
            Node? parent = card.GetParent();

            if (parent == null || !parent.Name.ToString().Contains("CardSubViewport"))
            {
                return;
            }

            Node? grand = parent.GetParent();

            ModEntry.Log.Warn(
                $"[{ModEntry.ModId}] nested card: {card.Model?.Id.Entry} " +
                $"inside {parent.Name} of " +
                $"{(grand?.Name.ToString() ?? "?")} " +
                $"global={card.GlobalPosition}");

            // 修复：把被借用的卡片收回播放区。
            //
            // NMultiplayerCardIntent 的 Card 属性只替换自己的占位卡 Model，
            // 从不接收外部 NCard。而这里出现了外部 NCard 被直接加进
            // CardSubViewport 的情况——那个子视口处于离屏渲染位置，
            // 卡片于是显示在 (90,112) 这类局部坐标上，也就是左上角。
            //
            // 卡片模型仍在 Play 堆时，把它移回 PlayContainer 并摆到正确落点；
            // 模型已经不在 Play 堆的（已结算）则直接回收节点。
            Control? play = NCombatRoom.Instance?.Ui?.PlayContainer;
            PileType? pileType = card.Model?.Pile?.Type;

            if (play == null)
            {
                return;
            }

            parent.RemoveChild(card);

            if (pileType == PileType.Play)
            {
                play.AddChild(card);

                Vector2 local = PileType.Play.GetTargetPosition(card);
                card.GlobalPosition = play.GetGlobalTransform() * local;

                ModEntry.Log.Info(
                    $"[{ModEntry.ModId}] nested card RETURNED: {card.Model?.Id.Entry} " +
                    $"-> PlayContainer at {card.GlobalPosition}");
            }
            else
            {
                card.QueueFree();

                ModEntry.Log.Info(
                    $"[{ModEntry.ModId}] nested card FREED: {card.Model?.Id.Entry} " +
                    $"(pile={pileType?.ToString() ?? "null"})");
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 停留检测：卡片进入播放区超过 0.1 秒后，记录一次它的实际位置与目标位置。
    ///
    /// 用于区分两种截然不同的情况：
    ///   pos 与 target 接近  -> 补间正常，画面上那张在左上角的牌是别的节点；
    ///   pos 与 target 差很远 -> 补间确实没跑完，问题就在补间本身。
    /// </summary>
    private static void ScheduleDwell(NCard card)
    {
        try
        {
            SceneTree? tree = card.GetTree();

            if (tree == null)
            {
                return;
            }

            string name = card.Model?.Id.Entry ?? "?";

            SceneTreeTimer timer = tree.CreateTimer(0.1);
            timer.Timeout += () =>
            {
                try
                {
                    if (card == null || !GodotObject.IsInstanceValid(card))
                    {
                        return;
                    }

                    NCombatUi? ui = NCombatRoom.Instance?.Ui;
                    Control? play = ui?.PlayContainer;

                    if (play == null || card.GetParent() != play)
                    {
                        return;
                    }

                    Vector2 local = PileType.Play.GetTargetPosition(card);
                    Vector2 target = play.GetGlobalTransform() * local;
                    Vector2 pos = card.GlobalPosition;

                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] play dwell: {name} pos={pos} target={target} " +
                        $"delta={(pos - target).Length():0.0} size={card.Size} " +
                        $"scale={card.Scale} visible={card.Visible} " +
                        $"tween={(card.PlayPileTween != null && card.PlayPileTween.IsRunning())}");
                }
                catch
                {
                    // ignored
                }
            };
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>补间时长 0.25 秒，超过这个距离就认为卡片没有走到位。</summary>
    private const float SettleTolerance = 48f;

    /// <summary>
    /// 保底补位：卡片进入播放区 0.3 秒后（补间 0.25 秒理应结束）复核位置。
    ///
    /// 入场补间绑在 NCombatRoom 上，速切导致该节点重建时会被连带中止，
    /// 卡片就停在 (325, 450) ——那是 PlayQueue 的位置，屏幕左侧偏上，
    /// 也就是用户看到的「随机跳到左上角」。
    ///
    /// 补间正常跑完时位置与目标一致，这里完全不触发；
    /// 只有被打断、仍停在起点附近时才把它挪到落点。
    /// </summary>
    private static void ScheduleSettle(NCard card, int attempt = 0)
    {
        try
        {
            SceneTree? tree = card.GetTree();

            if (tree == null)
            {
                return;
            }

            string name = card.Model?.Id.Entry ?? "?";

            // 首检 0.2 秒：加速模式（0.01s）与快速模式（0.1s）的补间此时已结束。
            // 普通模式的补间是 0.25 秒，会走「仍在运行」分支再等 0.1 秒。
            SceneTreeTimer timer = tree.CreateTimer(attempt == 0 ? 0.2f : 0.1f);
            timer.Timeout += () =>
            {
                try
                {
                    if (card == null || !GodotObject.IsInstanceValid(card))
                    {
                        return;
                    }

                    // 补间还在跑就先别动它，否则会在动画中途把卡片拽到终点。
                    if (card.PlayPileTween != null && card.PlayPileTween.IsRunning())
                    {
                        if (attempt < 2)
                        {
                            ScheduleSettle(card, attempt + 1);
                        }

                        return;
                    }

                    Control? play = NCombatRoom.Instance?.Ui?.PlayContainer;

                    if (play == null || card.GetParent() != play)
                    {
                        return;
                    }

                    Vector2 local = PileType.Play.GetTargetPosition(card);
                    Vector2 target = play.GetGlobalTransform() * local;
                    Vector2 pos = card.GlobalPosition;

                    if ((pos - target).Length() <= SettleTolerance)
                    {
                        return;
                    }

                    card.GlobalPosition = target;

                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] play settle FIXED: {name} {pos} -> {target} " +
                        $"(after {(attempt == 0 ? "0.2" : "0.2+" + (attempt * 0.1).ToString("0.0"))}s)");
                }
                catch
                {
                    // ignored
                }
            };
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 每帧采样卡片在播放区的位置，直到它离开或超时。
    /// 用来还原「卡片到底有没有停在起点」这件事——
    /// 定时器只能看到某一瞬间，抓不住中途的跳变。
    /// </summary>
    private static void TrackPlayMotion(NCard card)
    {
        try
        {
            SceneTree? tree = card.GetTree();

            if (tree == null)
            {
                return;
            }

            string name = card.Model?.Id.Entry ?? "?";
            int frames = 0;
            Vector2 last = new(-9999f, -9999f);

            void Step()
            {
                try
                {
                    frames++;

                    if (card == null || !GodotObject.IsInstanceValid(card) || frames > 90)
                    {
                        tree.ProcessFrame -= Step;
                        return;
                    }

                    Control? play = NCombatRoom.Instance?.Ui?.PlayContainer;

                    if (play == null || card.GetParent() != play)
                    {
                        tree.ProcessFrame -= Step;
                        ModEntry.Log.Info(
                            $"[{ModEntry.ModId}] play track end: {name} after {frames} frames");
                        return;
                    }

                    Vector2 pos = card.GlobalPosition;

                    // 只在位置真正变化时记录，避免刷屏。
                    if ((pos - last).Length() < 40f && frames > 1)
                    {
                        return;
                    }

                    last = pos;

                    Vector2 target = play.GetGlobalTransform()
                        * PileType.Play.GetTargetPosition(card);

                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] play track f{frames}: {name} " +
                        $"pos={pos} target={target} size={card.Size} " +
                        $"scale={card.Scale.X:0.00} " +
                        $"tween={(card.PlayPileTween?.IsRunning() ?? false)}");
                }
                catch
                {
                    tree.ProcessFrame -= Step;
                }
            }

            tree.ProcessFrame += Step;
        }
        catch
        {
            // ignored
        }
    }

    private static void AddPrefix(NCombatUi __instance, NCard card)
    {
        try
        {
            Control? pc = __instance.PlayContainer;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] add-to-play: {card.Model?.Id.Entry} " +
                $"pc(size={pc?.Size} pos={pc?.GlobalPosition} scale={pc?.Scale}) " +
                $"card(local={card.Position} global={card.GlobalPosition} size={card.Size}) " +
                $"parent={card.GetParent()?.Name}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// reparent 之后立刻记一次，再过 0.35 秒（补间时长 0.25）记一次，
    /// 用来判断补间到底有没有把卡片带到位。
    /// </summary>
    private static void AddPostfix(NCombatUi __instance, NCard card)
    {
        try
        {
            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] add-to-play after: {card.Model?.Id.Entry} " +
                $"pos={card.Position} global={card.GlobalPosition}");

            ScheduleDwell(card);
            ScheduleSettle(card);
            TrackPlayMotion(card);

            SceneTree? tree = card.GetTree();

            if (tree == null)
            {
                return;
            }

            SceneTreeTimer timer = tree.CreateTimer(0.35);
            string name = card.Model?.Id.Entry ?? "?";
            timer.Timeout += () =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(card))
                    {
                        ModEntry.Log.Info(
                            $"[{ModEntry.ModId}] play settle: {name} card gone");
                        return;
                    }

                    ModEntry.Log.Info(
                        $"[{ModEntry.ModId}] play settle: {name} pos={card.Position} " +
                        $"global={card.GlobalPosition} scale={card.Scale} " +
                        $"parent={card.GetParent()?.Name}");
                }
                catch
                {
                    // ignored
                }
            };
        }
        catch
        {
            // ignored
        }
    }

    private static void LerpPrefix(NCard cardNode, PileType typePile)
    {
        try
        {
            if (typePile != PileType.Play)
            {
                return;
            }

            Control? pc = NCombatRoom.Instance?.Ui?.PlayContainer;

            ModEntry.Log.Info(
                $"[{ModEntry.ModId}] play lerp: card={cardNode.Model?.Id.Entry} " +
                $"target={typePile.GetTargetPosition(cardNode)} " +
                $"pc(size={pc?.Size} pos={pc?.GlobalPosition}) " +
                $"cardSize={cardNode.Size}");
        }
        catch
        {
            // ignored
        }
    }
}
