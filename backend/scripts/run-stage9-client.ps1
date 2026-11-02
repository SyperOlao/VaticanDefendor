param(
    [ValidateRange(1, 65535)][int]$ApiPort = 8082,
    [string]$LogName = 'client.log',
    [string]$Build,
    [uint32]$BuildOperationId = 0,
    [double]$AttackEvery = 0,
    [switch]$DisableBurst,
    [double]$PauseAfterSeconds = -1,
    [ValidateSet(0, 1)][int]$PauseValue = 1,
    [switch]$ProbeReconnect,
    [switch]$Metrics,
    [string]$GuestFile = 'stage9-guest.json'
)
$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent $backend
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
if (-not (Test-Path -LiteralPath $clientExe)) { throw 'Build the client first.' }
$runtime = Join-Path $backend '.runtime'
$guestPath = Join-Path $runtime $GuestFile
if (Test-Path -LiteralPath $guestPath) {
    $guest = Get-Content -LiteralPath $guestPath -Raw | ConvertFrom-Json
} else {
    $guest = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$ApiPort/v1/guests"
    $guest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $guestPath -Encoding utf8
}
$logDirectory = Join-Path $project 'Builds/Stage9'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$env:VD_BACKEND_URL = "http://127.0.0.1:$ApiPort"
$env:VD_GUEST_TOKEN = $guest.token
$arguments = @('-batchmode', '-nographics', '-logFile', (Join-Path $logDirectory $LogName),
    '-vdRole', 'client', '-vdInsecureLocalBackend')
if ($DisableBurst) { $arguments += '-disable-burst-compilation' }
if ($Metrics) { $arguments += '-vdMetrics' }
if ($ProbeReconnect) { $arguments += '-vdProbeReconnect' }
if ($Build) {
    $arguments += @('-vdBuild', $Build)
    if ($BuildOperationId -gt 0) { $arguments += @('-vdBuildOperationId', $BuildOperationId) }
}
if ($AttackEvery -gt 0) {
    $arguments += @('-vdAttackEvery', [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        '{0}', $AttackEvery))
}
if ($PauseAfterSeconds -ge 0) {
    $arguments += @('-vdPauseAfter', [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        '{0}', $PauseAfterSeconds), '-vdPauseValue', [string]$PauseValue)
}
$process = Start-Process -FilePath $clientExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
Set-Content -LiteralPath (Join-Path $runtime 'stage9-client.pid') -Value $process.Id
Write-Output "Client started; pid=$($process.Id), log=$LogName."
