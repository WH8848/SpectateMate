# Spectate Mate（依赖 RitsuLib）

联机时**右键队友头像**打开观战，看他的手牌、能量、遗物，并把游戏原生 UI 切到他身上。
纯客户端只读：不改协议、不改存档、队友不装也正常。

## 交互（只有这几个）

| 动作 | 效果 |
|---|---|
| **右键队友头像** | 打开 / 关闭观战 |
| 右键另一个队友头像 | 切换观战目标 |
| **Esc** | 退出观战 |

没有自动观战，没有快捷键，没有修饰键分支。

**观战期间本地操作被屏蔽**：手牌点击、数字键出牌、药水、结束回合、牌堆按钮全部不响应；
按 Esc 退出后立即恢复。

## 打开后你会看到

### 右上角小窗（鼠标穿透，不影响你操作）

```
观战 张三 · 铁甲战士
HP 64/80  格挡 0    能量 3/3  星 0
界面 战斗中   手牌 5 抽 12 弃 3   遗物 8 药水 2
悬停 Bash / 瞄准
[手牌卡面缩略]
右键头像 或 Esc 关闭
```

### 游戏原生 UI 被他接管

| 位置 | 变成 |
|---|---|
| 顶栏金币 / HP / 头像 / 牌库按钮 | 他的 |
| 能量球 / 星 / 抽弃牌堆数字 | 他的 |
| 左上遗物栏 | 他的遗物 |
| 顶部药水槽 | 他的药水 |
| 底部手牌栏 | 他的手牌（原位镜像） |

退出观战后全部恢复成你自己的。

## ⚠️ 副作用

观战期间顶部能量球是**你的位置、他的数字**，别拿它判断自己的费用。

## 实现机制

`LocalContext.NetId` 是 `public static` 可写属性，而所有原生 UI 都通过
`LocalContext.GetMe(...)` 找「该显示谁」。在原生 UI 的 `Initialize/Activate` 外围
（Prefix 压栈 / Finalizer 弹栈）把它临时指向队友，那些组件就会用他的数据重建自己。

手牌栏走的是镜像：只隐藏原生 `CardNode`，把自己的 `NCard` 挂进**同一个 holder**，
自动继承 holder 的位置/旋转/动画。全程不搬节点、不改 `CardNode.Model`、不写存档。

## 逐段关闭

| 症状 | 处理 |
|---|---|
| 手牌显示错乱 | `src/Ui/HandMirror.cs` 里 `Enabled` 改 `false` 重编译 |
| 顶部数值/遗物/药水不对 | `src/ModEntry.cs` 里注释掉对应的 `patcher.RegisterPatch<...>()` |

所有 UI 补丁都标了 `IsCritical = false`，单个失败只会被记为 ignored。
`SpectateUi.Apply` 外层还有 try/catch：**UI 接管失败绝不会影响小窗打开**。

## 编译 / 部署

    dotnet build -c Release

自动复制 `SpectateMate.dll` + `mod_manifest.json` 到 `<游戏>\mods\SpectateMate\`。
路径配置在 `SpectateMate.csproj` 顶部：`Sts2Dir` / `RitsuLibDir` / `RitsuLibApi`。

## 排查

`%APPDATA%\SlayTheSpire2\logs\godot.log`，搜 `[SpectateMate]`：

    [SpectateMate] init v1.0.0.0
    [SpectateMate] right-click bound on netId=7656119...
    [SpectateMate] spectating netId=7656119...

若出现 `ui step 'xxx' failed`，说明某块原生 UI 没接管成功，日志里会写明是哪一块。

## 已知限制

- 小窗鼠标穿透，卡面不能悬停看详情
- 他手牌比你多时，多出的牌在手牌栏放不下（小窗数量仍准确）
- 看不到队友**未提交**的选择候选（游戏只同步结果）
- 键盘仍作用于游戏本体，观战时别按数字键出牌
