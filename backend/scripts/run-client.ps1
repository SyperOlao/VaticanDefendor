param(
    [switch]$Headless,
    [ValidateRange(1, 65535)][int]$ApiPort = 8080,
    [string]$Move
)
$ErrorActionPreference = 'Stop'
$Backend = Split-Path -Parent $PSScriptRoot
$Project = Split-Path -Parent $Backend
$Executable = Join-Path $Project 'Builds/Stage2/Client/VaticanDefendor.exe'
if (-not (Test-Path -LiteralPath $Executable))
{
    throw 'Build the client first.'
}
$env:VD_BACKEND_URL = "http://127.0.0.1:$ApiPort"
$LogDirectory = Join-Path $Project 'Builds/Gameplay'
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$Arguments = @('-logFile', (Join-Path $LogDirectory 'client.log'), '-vdRole', 'client', '-vdInsecureLocalBackend')
if ($Headless)
{
    $GuestPath = Join-Path $Backend '.runtime/local-guest.json'
    if (-not (Test-Path -LiteralPath $GuestPath))
    {
        Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$ApiPort/v1/guests" |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $GuestPath -Encoding utf8
    }
    $env:VD_GUEST_TOKEN = (Get-Content -LiteralPath $GuestPath -Raw | ConvertFrom-Json).token
    $Arguments += @('-batchmode', '-nographics')
}
if ($Move)
{
    $Arguments += @('-vdMove', $Move)
}
& $Executable @Arguments
