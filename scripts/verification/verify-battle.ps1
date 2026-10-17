param(
    [string]$Output = "",
    [switch]$SkipBuild,
    [switch]$SkipBrowser
)
$ErrorActionPreference = "Stop"
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $taskRoot
if (-not $Output) { $Output = "artifacts/battle-$(Get-Date -Format yyyyMMdd-HHmmss)" }
function Invoke-Checked {
    param([string]$Program, [string[]]$Parameters)
    if (Get-Command rtk -ErrorAction SilentlyContinue) { & rtk proxy $Program @Parameters }
    else { & $Program @Parameters }
    if ($LASTEXITCODE -ne 0) { throw "验证失败：$Program $Parameters；保留首个差异与记录，停止后续验收。" }
}
if (-not $SkipBuild) {
    Invoke-Checked "npm.cmd" @("run", "build:ui")
    Invoke-Checked "dotnet" @("build", "QQT.sln")
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null
Invoke-Checked "python" @("scripts/verification/battle.py", "--only", "all", "--output", "$Output/network")
Invoke-Checked "python" @("scripts/verification/battle.py", "--only", "limit", "--tick-interval", "1", "--url", "ws://127.0.0.1:5081", "--output", "$Output/limit")
Invoke-Checked "dotnet" @("src/QQT.Battle.Checks/bin/Debug/net10.0/QQT.Battle.Checks.dll", $taskRoot, (Join-Path $taskRoot "$Output/simulation-boundary"))
Invoke-Checked "python" @("scripts/verification/regression.py", "$Output/regression")
Invoke-Checked "dotnet" @("src/QQT.Core.Checks/bin/Debug/net10.0/QQT.Core.Checks.dll", (Join-Path $taskRoot "scripts/m0-chain-victory.json"), (Join-Path $taskRoot "scripts/items/items-12-vision-activation.json"))
if (-not $SkipBrowser) {
    Invoke-Checked "node" @("scripts/verification/browser.cjs", "$Output/browser")
}
@{ status = $(if ($SkipBrowser) { "partial-browser-skipped" } else { "passed" }); output = $Output } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $Output "summary.json")
Write-Output "验收完成：$Output；比赛胜负和验证结果分别记录。"
