param(
    [Parameter(Mandatory=$true)][ValidatePattern('^sha256-[0-9a-f]{64}$')]
    [string]$ContentReleaseId,
    [Parameter(Mandatory=$true)][ValidateRange(1, 1000000)]
    [int]$RewardPerChurchLevel,
    [ValidateRange(1, 65535)][int]$ApiPort = 8082,
    [ValidatePattern('^[a-z][a-z0-9_]*$')][string]$DatabaseName = 'vatican'
)

$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$runtime = Join-Path $backend '.runtime'
$config = Get-Content -LiteralPath (Join-Path $runtime 'local.json') -Raw | ConvertFrom-Json
$python = (Resolve-Path (Join-Path $backend '.venv/Scripts/python.exe')).Path
$logDirectory = Join-Path $project 'Builds/Stage8'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$env:VD_DATABASE_URL = 'postgresql://vatican:' +
    [uri]::EscapeDataString($config.dbPassword) + "@127.0.0.1:5432/$DatabaseName"
$env:VD_SERVER_POOL_JSON = ConvertTo-Json -InputObject @(@{
    serverId = $config.serverId; host = $config.host; port = $config.port
    secret = $config.serverSecret
}) -Compress -Depth 4
$env:VD_RUN_REWARDS_JSON = @{
    $ContentReleaseId = @{ perChurchLevel = $RewardPerChurchLevel }
} | ConvertTo-Json -Compress -Depth 4
$env:PYTHONPATH = $backend
& $python -m vatican_backend.migrate
if ($LASTEXITCODE -ne 0) { throw 'Database migration failed.' }
$process = Start-Process -FilePath $python -WorkingDirectory $backend -ArgumentList @(
    '-m', 'uvicorn', 'vatican_backend.api:create_app', '--factory',
    '--host', '127.0.0.1', '--port', $ApiPort, '--no-access-log'
) -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logDirectory 'api.out.log') `
    -RedirectStandardError (Join-Path $logDirectory 'api.err.log')
Set-Content -LiteralPath (Join-Path $runtime 'stage8-api.pid') -Value $process.Id
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/health/ready" -TimeoutSec 2
        if ($health.status -eq 'ready') {
            Write-Output "Backend ready on port $ApiPort; pid=$($process.Id); schema=$($health.schemaVersion)."
            exit 0
        }
    } catch { Start-Sleep -Milliseconds 500 }
}
throw 'Backend did not become ready; see Builds/Stage8/api.err.log.'
