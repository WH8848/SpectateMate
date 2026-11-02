# 发布到 Steam 创意工坊

Slay the Spire 2 的 AppId 是 **2868840**。官方提供了专用上传器，不要用 steamcmd。

- 上传器：https://github.com/megacrit/sts2-mod-uploader
- 教程：https://tutorials.sts2modding.com/docs/11-upload-workshop/

## 当前条目

**Workshop ID: 3832499210**
https://steamcommunity.com/sharedfiles/filedetails/?id=3832499210

## 工作区结构

上传器在 `E:\MOD\sts2\uploader\SpectateMate\`：

```
SpectateMate/
  workshop.json        条目配置（标题/描述/可见性/tags/依赖）
  content/             ↑ 上传的文件
    mod_manifest.json
    SpectateMate.dll
  image.png            封面，必须 < 1MB
  mod_id.txt           首次上传后自动生成，后续更新靠它定位条目
  README.md            上传器自带的说明
```

## 更新流程

```powershell
# 1. 重新编译（会自动部署到 mods 目录）
cd "E:\MOD\sts2\godot_project\Spectate teammate"
dotnet build -c Release -v m

# 2. 把新文件放进工作区
copy "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\（功能）右键队友头像观战\mod_manifest.json" "E:\MOD\sts2\uploader\SpectateMate\content\"
copy "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\（功能）右键队友头像观战\SpectateMate.dll" "E:\MOD\sts2\uploader\SpectateMate\content\"

# 3. 改 workshop.json 里的 changeNote，然后上传
cd "E:\MOD\sts2\uploader"
.\ModUploader.exe upload -w SpectateMate
```

## workshop.json 要点

| 字段 | 说明 |
|---|---|
| `title` | 工坊标题 |
| `description` | 支持 `<br>` 换行 |
| `visibility` | `public` / `private` / `unlisted` / `friends_only` |
| `tags` | **上传后无法在工坊修改**，要一次定好 |
| `dependencies` | **工坊 ID**（不是 mod id），RitsuLib 是 `3747602295` |
| `changeNote` | 更新说明 |

**注意**：json 里写了的字段会覆盖工坊上的内容。想保留工坊上手动改的值，就把对应字段删掉或设成 `null`。

## 第一次踩到的坑

- 用 steamcmd 上传 → 匿名账号无法创建条目，实名也要交互输密码，走不通
- 上传器所在路径**不能含中文**，否则 Steam 直接 fatal error
- 封面是 **`image.png`**（不是 jpg），必须小于 1MB
