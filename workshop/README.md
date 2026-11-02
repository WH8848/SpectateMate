# 发布到 Steam 创意工坊

Slay the Spire 2 的 AppId 是 **2868840**。游戏本身只负责**下载并加载**工坊内容，
没有上传界面，所以要用 Steam 官方的 `steamcmd` 上传。

## 一次性准备

1. 下载 [steamcmd](https://developer.valvesoftware.com/wiki/SteamCMD) 解压到 `C:\steamcmd`
2. 首次运行会自更新，然后登录一次：
   ```
   C:\steamcmd\steamcmd.exe +login 你的Steam用户名
   ```
   输入密码和 Steam 令牌后 `quit`
3. 准备一张 `preview.png` 放在本目录（工坊封面，建议 512×512 或 16:9）

## 首次发布

```powershell
.\publish.ps1
```

脚本会：
1. `dotnet build -c Release` 重新打包
2. 把 `mod_manifest.json` 和 `SpectateMate.dll` 复制到 `content\`
3. 调 `steamcmd +workshop_build_item workshop_build_item.vdf`

上传成功后 steamcmd 会打印一个新的 `publishedfileid`，**把它填回
`workshop_build_item.vdf` 的 `publishedfileid`**，否则下次发布会重复创建条目。

## 后续更新

改完代码后重新跑一次 `publish.ps1` 即可，`publishedfileid` 保持不变，
工坊条目会被更新，`changenote` 里写的更新说明会显示在工坊页面。

## 上传内容的要求

游戏扫描模组的规则是「目录下每个 `.json` 都当作清单读」，
所以清单文件名不一定要叫 `mod_manifest.json`，但**必须有 `id` 字段**。
我们上传的就是：

```
content/
  mod_manifest.json     ← 含 id / version / min_game_version / dependencies
  SpectateMate.dll      ← 编译产物
```

没有 `.pck`（manifest 里 `has_pck: false`）。

## 依赖声明

`mod_manifest.json` 里已经写了：

```json
"dependencies": [{ "id": "STS2-RitsuLib" }]
```

玩家订阅后，游戏会提示他们同时订阅 RitsuLib，否则本模组不会加载。

## 版本更新时记得改

- `mod_manifest.json` 的 `version`
- `mod_manifest.json` 的 `min_game_version`（游戏大版本变了才需要）
- `workshop_build_item.vdf` 的 `changenote`
