# SpectateMate 项目交接文档

> 给协作者（人类或 AI）的完整上下文。读完这一份就能接着干活。

## 一、这是什么

**《杀戮尖塔 2》(Slay the Spire 2) 的一个多人观战模组。**

战斗中**右键队友头像**即可以只读方式观战，看到对方的全部实时状态。按 Esc（可改键）退出，战斗结束自动退出。

核心定位：

- **纯客户端**。不发送任何网络消息，不写存档，不改对局结果。
- **只读**。观战期间不能出牌、喝药、结束回合、点选牌界面。
- **观战者看到的应该和被观战者完全一致**——这是最高标准。手牌布局、出牌动画、牌堆内容、悬停高亮、选牌界面，都要和原版一模一样。

## 二、技术环境

| 项 | 值 |
|---|---|
| 游戏版本 | **0.113.0**（测试分支） |
| 引擎 | Godot 4.5.1 mono |
| 语言 | C# net9.0 |
| 框架 | **RitsuLib**（必需依赖，工坊 ID `3747602295`） |
| 补丁 | Harmony（RitsuLib 封装 + 直接用的 `0Harmony`） |
| 游戏安装 | `E:\SteamLibrary\steamapps\common\Slay the Spire 2` |
| 反编译源码 | `E:\MOD\sts2\sts2\0.113.0\src`（旧版在 `0.111.1\src`） |
| 工程目录 | `E:\MOD\sts2\godot_project\Spectate teammate` |
| 仓库 | https://github.com/WH8848/SpectateMate （分支 main） |
| 工坊条目 | **3832499210** |

编译命令：`dotnet build -c Release -v m`，要求 **0 警告 0 错误**。编译产物自动部署到游戏 mods 目录。

## 三、核心机制

### 1. 接管原生 UI（整个模组的基石）

原版所有 UI 组件都通过 `LocalContext.GetMe()` 解析「我该显示哪个玩家」。所以做法是：

**在 Harmony 的 Prefix 里把 `LocalContext.NetId` 临时换成队友，在 Finalizer 里换回来。**

这样所有原生组件（手牌、能量球、遗物栏、药水栏、顶栏）**自动**渲染队友的数据，我们一行布局代码都不用写。

见 `src/Ui/DisplayedPlayer.cs`。

> ⚠️ **最大的陷阱**：`DisplayedPlayer.IsActive` / `.Target` **只在 patch 的 Prefix→Finalizer 作用域内有效**。在别的任何地方读它们都是错的（通常恒为 false/null）。
> 外部代码一律用 `SpectateOverlay.IsOpen` / `SpectateOverlay.Target`。
> **这个坑踩过两次**，每次都导致整个功能块被静默跳过。

### 2. 手牌镜像（HandMirror）

原生手牌的节点属于本地玩家，不能直接显示队友的牌。做法：

- 把原生 `CardHolderContainer` 的 `Modulate.a` 设为 0（隐藏本地手牌）
- 在 `NPlayerHand` 下新建一层 `SpectateMateHandLayer`，在里面创建**镜像 holder**，复用原版的 `NHandCardHolder` 组件
- 劫持 `NPlayerHand.ActiveHolders` 这个属性，让它返回镜像 holder 列表——于是原版的 `RefreshLayout` 会自动帮我们排版

见 `src/Ui/HandMirror.cs`、`src/Patches/ActiveHoldersPatch.cs`。

**注意**：`NPlayerHand.GetCardHolder` 用的是私有 `Holders`（不是 `ActiveHolders`），所以劫持 `ActiveHolders` 不影响原版查找卡片的逻辑。这点很重要，改动前务必确认。

### 3. 出牌动画（CardFlight）

队友打出的牌，原生动画作用在**被隐藏的原生卡**上，观战者看不见。所以要在镜像卡上补一份：

- **出牌 → 播放区**：不再镜像（原版卡离开手牌容器后会自己恢复可见），交给原版播
- **手牌直接消耗 / 虚无**：补 `NCardExhaustQuickVfx`
- **进弃牌堆 / 消耗堆**：补 `NCardFlyVfx`，拖尾颜色取被观战者的 `Character.TrailPath`

见 `src/Ui/CardFlight.cs`。

### 4. 选牌界面（SpectateChoice）

队友在选牌时（发现三选一、从牌堆选、从手牌选），观战者要能只读地看到。

做法：用原版的 `NChooseACardSelectionScreen.ShowScreen` / `NCombatPileCardSelectScreen.Create` **自己建一个实例**，压进 `NOverlayStack`，然后禁用里面所有输入。

见 `src/Ui/SpectateChoice.cs`。

> ⚠️ **第二个大坑**：`NOverlayStack.Push` 会做两件事——把下层界面 `AfterOverlayHidden` 掉、把 `_backstop.MouseFilter` 设成 `Stop`。
> **一个零内容的空层一旦入栈，就会藏起下面的原生界面并接管全部输入。** 如果销毁路径有任何闪失，玩家面对的就是「看不见又点不动」的屏幕。
> 所以**入栈前必须校验界面里真有 `NCard` 节点**，没有就不入栈。

### 5. 落点修正（PlayTargetPositionFix）

原版算播放区落点：

```csharp
PileType.Play => PlayContainer.Size * 0.5f - node.Size * 0.5f + Vector2.Up * 100f
```

`- node.Size * 0.5` 把「容器中心」换算成「卡片左上角」。但 `NCard.UpdateVisuals` 开头有 `if (!IsNodeReady()) return;`，而原版创建卡片的顺序是「先 AddChild 再 UpdateVisuals」，所以 **`Size` 经常还是 `(0,0)`**，这一项等于没减。

结果是落点在 `(960,440)`（尺寸为零）和 `(810,229)`（尺寸正常）之间跳变。

**观战画面里正确的是 `(960,440)`**（不减尺寸的那个）。所以给 `GetTargetPosition` 挂了 postfix，观战时固定返回不减尺寸的结果。

## 四、踩过的坑（血泪清单）

| 坑 | 现象 | 教训 |
|---|---|---|
| `DisplayedPlayer.IsActive` 在 patch 外读 | 整个 TickDisplayed 块被静默跳过，消耗牌堆按钮永远不出现 | 外部一律用 `SpectateOverlay.IsOpen` |
| `Control.Modulate.A = 0` | 完全透明的界面**照样拦截鼠标** | 隐藏 ≠ 不响应，必须设 `MouseFilter = Ignore` |
| 空覆盖层入栈 | 藏起下层界面 + 接管输入 = 点不动 | 入栈前校验内容 |
| 状态标志与实际节点不同步 | `ForgetState()` 清标志但不碰节点 → 手牌永久透明 | 恢复时**以节点实际状态为准**，标志只作辅助 |
| `NStarCounter.Visible` | 是自或运算，设 false 后再 Initialize 才有效 | 读源码确认语义 |
| 能力卡打出后 `Pile` 是 `PileType.None` 不是 `null` | 能力卡永远停在播放区 | 两个都要判断 |
| `NCardExhaustQuickVfx.Create` 内部已 AddChild | 我们再 AddChild 一次会把它弄没 | 注意副作用 |
| 用 steamcmd 上传工坊 | 匿名账号建不了条目 | 用官方 `ModUploader` |
| 上传器路径含中文 | fatal error | 放全英文路径 |
| 工坊封面 | 必须是 `image.png` < 1MB | 不是 jpg |
| `workshop.json` 删掉 title/description | 上传器会提交**空标题**，不是保留原值 | 必须显式提供 |

## 五、目录结构

```
src/
  ModEntry.cs                     入口：装 6 个 RitsuLib patch + 若干 Harmony patch
  Patches/
    AvatarRightClickPatch.cs      NMultiplayerPlayerState._Ready 时绑右键
    UiOverridePatches.cs          战斗 UI 激活时接管 + 恢复
    ActiveHoldersPatch.cs         劫持 ActiveHolders 返回镜像列表
    HandStatePatch.cs             观战时跳过 UpdateHandDisabledState
    ChoiceProbe.cs                三个选牌入口的 Prefix（只记录，不改行为）
    PlayTargetPositionFix.cs      落点公式修正
    ShuffleProbe.cs               洗牌光带颜色修正 + 同牌堆不播特效
  Ui/
    SpectateOverlay.cs            观战生命周期、心跳、开关
    SpectateUi.cs                 原生 UI 接管与恢复
    HandMirror.cs                 手牌镜像
    CardFlight.cs                 出牌/消耗/弃牌特效
    SpectateChoice.cs             选牌只读界面
    CombatWidgetRebind.cs         牌堆按钮、能量球重建
    DisplayedPlayer.cs            LocalContext.NetId 重定向
    VanillaHandVisibility.cs      原生手牌容器透明度管理
    InteractionBlocker.cs         输入拦截
    HandSelectionView.cs          手牌选择界面
    TopBarRebind.cs               顶栏重绑
    AvatarHighlight.cs            头像高亮环
    SpectateSettings.cs           RitsuLib 设置项
```

## 六、硬约束（不要违反）

1. **不发送任何网络消息**，不写存档
2. **不修改原版的行为**，只做只读呈现
3. **不要 detach 或 park 原版 holder**（会导致原版状态错乱）
4. **不要设置 `CardNode.Model`**（会污染模型）
5. 改原版动画之前，先读反编译源码确认时序

## 七、当前状态

**功能完整**，已发布到工坊（版本 1.1.0）。

最近一轮修的问题：

- 适配 0.113.0（`RitsuLibApi` 从 0.111.0 改成 0.113.0）
- 空覆盖层不再入栈（选牌点不动的根因）
- 手牌可见性恢复改为按节点实际透明度判断
- 落点固定为 `(960,440)`

**0.113.0 的接口变更对本模组无影响**（`AfterDeath` 新参数、`HpLossHookPhase` 位值重排、`GameMode.Custom` 拆分等七类改动，代码中零引用）。

## 八、已知问题 / 待办

1. **队友机器跑的是旧 dll**（`D:\SteamLibrary` 路径），需要重启游戏加载工坊更新
2. **`choice peek` 的轮询开销**：数据没到时每拍会尝试建界面（现在已加校验，但仍每拍检查一次）
3. **README 严重过时**
4. 双开测试方法见下文

## 九、怎么测（双开）

游戏原生支持双开，用官方参数：

```powershell
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2"

# 主机（Test Host, netId=1）
.\SlayTheSpire2.exe -fastmp host_standard

# 等 15 秒让房间建好，再开客户端（Test Client 1, netId=1000）
.\SlayTheSpire2.exe -fastmp join -clientId=1000
```

两个进程**存档自动隔离**（路径里带 userId）。日志共用同一份，注意区分。

## 十、发布流程

```powershell
dotnet build -c Release -v m
# 复制到 E:\MOD\sts2\uploader\SpectateMate\content\
cd E:\MOD\sts2\uploader
.\ModUploader.exe upload -w SpectateMate
```

**`workshop.json` 的 `title` / `description` 必须保留原值**，只改 `changeNote`。删掉会被提交成空字符串。
