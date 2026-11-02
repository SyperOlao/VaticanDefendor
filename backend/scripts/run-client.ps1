param(
    [switch]$Headless,
    [string]$Move,
    [string]$AttackEvery,
    [string]$AttackAim
)
$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$env:VD_BACKEND_URL = 'http://127.0.0.1:8080'
$localGuest = Join-Path $backend '.runtime/local-guest.json'
if (-not $env:VD_GUEST_TOKEN -and (Test-Path -LiteralPath $localGuest)) {
    $env:VD_GUEST_TOKEN = (Get-Content -LiteralPath $localGuest -Raw | ConvertFrom-Json).token
}
$logDir = Join-Path $project 'Builds/Stage4'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$arguments = @('-logFile', (Join-Path $logDir 'client.log'), '-vdRole', 'client', '-vdInsecureLocalBackend')
if ($Headless) { $arguments += @('-batchmode', '-nographics') }
if ($Move) { $arguments += @('-vdMove', $Move) }
if ($AttackEvery) { $arguments += @('-vdAttackEvery', $AttackEvery) }
if ($AttackAim) { $arguments += @('-vdAttackAim', $AttackAim) }
& (Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe') @arguments
