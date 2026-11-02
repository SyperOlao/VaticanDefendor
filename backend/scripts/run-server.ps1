$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$config = Get-Content -LiteralPath (Join-Path $backend '.runtime/local.json') -Raw | ConvertFrom-Json
$env:VD_BACKEND_URL = 'http://127.0.0.1:8080'
$env:VD_SERVER_SECRET = $config.serverSecret
$logDir = Join-Path $project 'Builds/Stage4'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
& (Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe') -batchmode -nographics -logFile (Join-Path $logDir 'server.log') -vdRole server -vdServerId $config.serverId -vdPort $config.port -vdInsecureLocalBackend
