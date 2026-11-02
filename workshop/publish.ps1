# 把 SpectateMate 发布到 Steam 创意工坊
#
# 用法：
#   .\publish.ps1                          # 只打包并收集文件，不上传
#   .\publish.ps1 -User 你的Steam用户名     # 打包并上传（会提示输入密码/令牌）
#
# 首次发布时 publishedfileid 必须是 0，上传成功后 steamcmd 会打印新 ID，
# 把它填回 workshop_build_item.vdf，之后的更新才不会重复创建条目。

param(
    [string]$User = "",
    [string]$SteamCmd = "E:\\MOD\\sts2\\steamcmd\\steamcmd.exe"
)

$ErrorActionPreference = "Stop"
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$vdf     = Join-Path $here "workshop_build_item.vdf"
$content = Join-Path $here "content"
$project = Split-Path -Parent $here

# 1. 重新打包
Write-Host "[1/3] dotnet build" -ForegroundColor Cyan
Push-Location $project
try   { dotnet build -c Release -v m | Out-Host }
finally { Pop-Location }

# 2. 收集文件
Write-Host "[2/3] collect files" -ForegroundColor Cyan
if (Test-Path $content) { Remove-Item $content -Recurse -Force }
New-Item -ItemType Directory -Path $content | Out-Null

$mods = "E:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\mods\\（功能）右键队友头像观战"
Copy-Item (Join-Path $mods "mod_manifest.json") $content
Copy-Item (Join-Path $mods "SpectateMate.dll")  $content
Get-ChildItem $content | Select-Object Name, Length | Format-Table -AutoSize | Out-Host

# 3. 上传
if (-not (Test-Path $SteamCmd)) {
    Write-Host "找不到 steamcmd：$SteamCmd" -ForegroundColor Yellow
    return
}

if ([string]::IsNullOrWhiteSpace($User)) {
    Write-Host "[3/3] 跳过上传（未提供 -User）" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "上传需要实名登录，请执行：" -ForegroundColor Yellow
    Write-Host "  $SteamCmd +login <你的Steam用户名>" -ForegroundColor White
    Write-Host "  然后在 Steam> 提示符下输入：" -ForegroundColor Yellow
    Write-Host "  workshop_build_item \"$vdf\"" -ForegroundColor White
    Write-Host "  成功后输入 quit 退出，把输出的 publishedfileid 填回 vdf。" -ForegroundColor Yellow
    return
}

Write-Host "[3/3] upload" -ForegroundColor Cyan
& $SteamCmd +login $User +workshop_build_item $vdf +quit
Write-Host "完成。首次发布会打印新的 publishedfileid，请填回 vdf。" -ForegroundColor Green
