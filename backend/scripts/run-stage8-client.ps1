param([ValidateRange(1, 65535)][int]$ApiPort = 8082,
      [string]$LogName = 'client.log')

$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
if (-not (Test-Path -LiteralPath $clientExe)) { throw 'Build the client first.' }
$logDirectory = Join-Path $project 'Builds/Stage8'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$guest = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$ApiPort/v1/guests"
$guest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (
    Join-Path $backend '.runtime/stage8-guest.json') -Encoding utf8
$env:VD_BACKEND_URL = "http://127.0.0.1:$ApiPort"
$env:VD_GUEST_TOKEN = $guest.token
$process = Start-Process -FilePath $clientExe -ArgumentList @(
    '-batchmode', '-nographics', '-logFile', (Join-Path $logDirectory $LogName),
    '-vdRole', 'client', '-vdInsecureLocalBackend'
) -PassThru -WindowStyle Hidden
Set-Content -LiteralPath (Join-Path $backend '.runtime/stage8-client.pid') -Value $process.Id
Write-Output "Headless client started; pid=$($process.Id), log=$LogName."
