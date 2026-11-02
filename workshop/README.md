# 发布与更新

Slay the Spire 2 的 AppId 是 **2868840**。用官方上传器，不要用 steamcmd。

- 上传器：https://github.com/megacrit/sts2-mod-uploader
- 教程：https://tutorials.sts2modding.com/docs/11-upload-workshop/

## 当前条目

**Workshop ID: 3832499210**
https://steamcommunity.com/sharedfiles/filedetails/?id=3832499210

## 工作区

`E:\MOD\sts2\uploader\SpectateMate\`

```
SpectateMate/
  workshop.json        标题 / 描述 / 可见性 / tags / 依赖 / changeNote
  content/             上传的文件
    mod_manifest.json
    SpectateMate.dll
  image.png            封面，< 1MB
  mod_id.txt           首次上传后自动生成，后续更新靠它定位条目
```

## 更新

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File ".\workshop\update.ps1"
```

或者手动三步：

```powershell
dotnet build -c Release -v m
copy "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\（功能）右键队友头像观战\*" "E:\MOD\sts2\uploader\SpectateMate\content\"
cd E:\MOD\sts2\uploader
.\ModUploader.exe upload -w SpectateMate
```

**不需要指定 ID**——上传器从工作区的 `mod_id.txt` 读 `3832499210`，不会重复创建条目。

## workshop.json 要点

| 字段 | 说明 |
|---|---|
| `title` | 工坊标题 |
| `description` | 支持 `<br>` 换行 |
| `visibility` | `public` / `private` / `unlisted` / `friends_only` |
| `tags` | **上传后无法在工坊修改**，要一次定好 |
| `dependencies` | **工坊 ID**（不是 mod id），RitsuLib 是 `3747602295` |
| `changeNote` | 更新说明，显示在工坊的更新记录里 |

**注意**：json 里写了的字段会覆盖工坊上的内容。想保留工坊上手动改的值，就把对应字段删掉或设成 `null`。

## 踩过的坑

| 坑 | 现象 |
|---|---|
| 用 steamcmd | `ERROR! Failed to create new workshop item.` —— 匿名账号不能建条目，实名又要交互输密码 |
| 上传器路径含中文 | `cannot run from a folder path that includes non-English characters` |
| 封面格式 | 必须是 `image.png`，< 1MB |
| 依赖写法 | 要写工坊 ID，不是 `STS2-RitsuLib` 这种 mod id |
