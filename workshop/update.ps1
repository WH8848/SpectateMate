# 一键更新工坊条目（3832499210）
#
# 做的事：
#   1. dotnet build -c Release（会自动部署到 mods 目录）
#   2. 把新产物复制到上传器工作区的 content/
#   3. 调用 ModUploader 上传
#
# 用法：
#   pwsh -NoProfile -ExecutionPolicy Bypass -File .\update.ps1
#
# 更新说明改 workshop.json 里的 changeNote。

param(
    [string]$Uploader = "E:\\MOD\\sts2\\uploader\\ModUploader.exe",
    [string]$Workspace = "E:\\MOD\\sts2\\uploader\\SpectateMate"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Split-Path -Parent $here
$mods = "E:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\mods\\（功能）右键队友头像观战"

Write-Host "[1/3] build" -ForegroundColor Cyan
Push-Location $project
try   { dotnet build -c Release -v m | Out-Host }
finally { Pop-Location }

Write-Host "[2/3] copy" -ForegroundColor Cyan
Copy-Item (Join-Path $mods "mod_manifest.json") (Join-Path $Workspace "content") -Force
Copy-Item (Join-Path $mods "SpectateMate.dll")  (Join-Path $Workspace "content") -Force

Write-Host "[3/3] upload" -ForegroundColor Cyan
& $Uploader upload -w $Workspace
