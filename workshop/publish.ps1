# 把 SpectateMate 发布到 Steam 创意工坊
#
# 前置：
#   1. 已安装 steamcmd（https://developer.valvesoftware.com/wiki/SteamCMD）
#   2. 用你的账号登录过：steamcmd +login <用户名>
#
# 首次发布时 publishedfileid 必须是 0，上传成功后 steamcmd 会打印新的 ID，
# 把它填回 workshop_build_item.vdf，之后的更新才不会重复创建条目。

param(
    [string]$SteamCmd = "C:\\steamcmd\\steamcmd.exe"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$vdf  = Join-Path $here "workshop_build_item.vdf"
$content = Join-Path $here "content"

# 1. 重新打包
Push-Location (Split-Path -Parent $here)
try {
    dotnet build -c Release -v m | Out-Host
}
finally {
    Pop-Location
}

# 2. 收集要上传的文件
if (Test-Path $content) { Remove-Item $content -Recurse -Force }
New-Item -ItemType Directory -Path $content | Out-Null

$mods = "E:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\mods\\（功能）右键队友头像观战"
Copy-Item "$mods\\mod_manifest.json" $content
Copy-Item "$mods\\SpectateMate.dll"  $content

Write-Host "content:" -ForegroundColor Cyan
Get-ChildItem $content | Select-Object Name, Length | Format-Table -AutoSize | Out-Host

# 3. 上传
if (-not (Test-Path $SteamCmd)) {
    Write-Host "找不到 steamcmd：$SteamCmd" -ForegroundColor Yellow
    Write-Host "手动上传：" -ForegroundColor Yellow
    Write-Host "  $SteamCmd +login <用户名> +workshop_build_item \"$vdf\" +quit"
    return
}

& $SteamCmd +login (Read-Host "Steam 用户名") +workshop_build_item $vdf +quit
Write-Host "上传完成。首次发布会打印新的 publishedfileid，请填回 vdf。" -ForegroundColor Green
