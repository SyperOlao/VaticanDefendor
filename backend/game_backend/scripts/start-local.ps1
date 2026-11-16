param(
    [Parameter(Mandatory=$true)][ValidatePattern('^sha256-[0-9a-f]{64}$')][string]$ContentReleaseId,
    [Parameter(Mandatory=$true)][ValidateRange(1, 1000000)][int]$RewardPerChurchLevel
)
$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$runtime = Join-Path $backend '.runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$configPath = Join-Path $runtime 'local.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    $config = [ordered]@{
        serverId = [guid]::NewGuid().ToString('D')
        host = '127.0.0.1'
        port = 7979
        dbPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
        serverSecret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
    }
    $config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $configPath -Encoding utf8
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$pool = ConvertTo-Json -InputObject @(@{serverId=$config.serverId;host=$config.host;port=$config.port;secret=$config.serverSecret}) -Compress -Depth 4
$rewards = @{ $ContentReleaseId = @{ perChurchLevel = $RewardPerChurchLevel } } | ConvertTo-Json -Compress -Depth 4
$envPath = Join-Path $runtime 'compose.env'
@("VD_DB_PASSWORD=$($config.dbPassword)", "VD_SERVER_POOL_JSON='$pool'", "VD_RUN_REWARDS_JSON='$rewards'") | Set-Content -LiteralPath $envPath -Encoding utf8
docker compose --env-file $envPath -f (Join-Path $backend 'compose.yaml') up -d --build
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed.' }
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        $ready = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/health/ready' -TimeoutSec 2
        if ($ready.status -eq 'ready') {
            Write-Output "Backend and PostgreSQL ready. ServerId=$($config.serverId), UDP=$($config.port)."
            exit 0
        }
    } catch { Start-Sleep -Seconds 2 }
}
throw 'Backend did not become ready; inspect docker compose logs.'
