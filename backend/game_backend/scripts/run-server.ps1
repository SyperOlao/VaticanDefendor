param(
    [ValidateRange(1, 65535)][int]$ApiPort = 8080,
    [ValidateRange(0, 3600)][double]$GraceSeconds = 10,
    [ValidateRange(1, 86400)][double]$RetentionSeconds = 300,
    [ValidateSet('pause', 'continue')][string]$AfterGrace = 'pause',
    [ValidateSet('abandon', 'continue')][string]$OnExpiry = 'abandon',
    [ValidateRange(1, 3600)][double]$CheckpointSeconds = 30,
    [string]$LogName = 'server.log',
    [switch]$Metrics
)
$ErrorActionPreference = 'Stop'
if ($RetentionSeconds -le $GraceSeconds) { throw 'RetentionSeconds must exceed GraceSeconds.' }
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$config = Get-Content -LiteralPath (Join-Path $backend '.runtime/local.json') -Raw |
    ConvertFrom-Json
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
if (-not (Test-Path -LiteralPath $serverExe)) { throw 'Build the dedicated server first.' }
$logDirectory = Join-Path $project 'Builds/Gameplay'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$env:VD_BACKEND_URL = "http://127.0.0.1:$ApiPort"
$env:VD_SERVER_SECRET = $config.serverSecret
$env:VD_RUN_RESULT_DIR = Join-Path $backend '.runtime/pending-results'
$env:VD_SESSION_GRACE_SECONDS = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
    '{0}', $GraceSeconds)
$env:VD_SESSION_RETENTION_SECONDS = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
    '{0}', $RetentionSeconds)
$env:VD_SESSION_AFTER_GRACE = $AfterGrace
$env:VD_SESSION_ON_EXPIRY = $OnExpiry
$env:VD_CHECKPOINT_SECONDS = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
    '{0}', $CheckpointSeconds)
$arguments = @(
    '-batchmode', '-nographics', '-logFile', (Join-Path $logDirectory $LogName),
    '-vdRole', 'server', '-vdServerId', $config.serverId, '-vdPort', $config.port,
    '-vdInsecureLocalBackend'
)
if ($Metrics) { $arguments += '-vdMetrics' }
$process = Start-Process -FilePath $serverExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
Set-Content -LiteralPath (Join-Path $backend '.runtime/server.pid') -Value $process.Id
Write-Output "Dedicated server started; pid=$($process.Id), log=$LogName."
