param([ValidateRange(1, 65535)][int]$ApiPort = 8082,
      [string]$LogName = 'server.log')

$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$config = Get-Content -LiteralPath (Join-Path $backend '.runtime/local.json') -Raw |
    ConvertFrom-Json
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
if (-not (Test-Path -LiteralPath $serverExe)) { throw 'Build the dedicated server first.' }
$logDirectory = Join-Path $project 'Builds/Stage8'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$env:VD_BACKEND_URL = "http://127.0.0.1:$ApiPort"
$env:VD_SERVER_SECRET = $config.serverSecret
$env:VD_RUN_RESULT_DIR = Join-Path $backend '.runtime/pending-results'
$process = Start-Process -FilePath $serverExe -ArgumentList @(
    '-batchmode', '-nographics', '-logFile', (Join-Path $logDirectory $LogName),
    '-vdRole', 'server', '-vdServerId', $config.serverId, '-vdPort', $config.port,
    '-vdInsecureLocalBackend'
) -PassThru -WindowStyle Hidden
Set-Content -LiteralPath (Join-Path $backend '.runtime/stage8-server.pid') -Value $process.Id
Write-Output "Dedicated server started; pid=$($process.Id), log=$LogName."
