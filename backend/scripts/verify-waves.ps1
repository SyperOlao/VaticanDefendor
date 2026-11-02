param([int]$Port = 8004, [ValidateRange(12, 120)][int]$DurationSeconds = 22)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
$logDir = Join-Path $project 'Builds/Stage6'
if (-not (Test-Path -LiteralPath $serverExe) -or -not (Test-Path -LiteralPath $clientExe)) {
    throw 'Build separate server and client Development players first.'
}
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$runId = [guid]::NewGuid().ToString('D')
$env:VD_DEV_TICKET = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:VD_DEV_ACCOUNT_ID = 'stage6-wave-account'
$serverLog = Join-Path $logDir 'normal-server.log'
$clientLog = Join-Path $logDir 'normal-client.log'
Remove-Item -LiteralPath $serverLog, $clientLog -ErrorAction SilentlyContinue
$server = $null
$client = $null
try {
    $serverArgs = '-batchmode -nographics -logFile "' + $serverLog + '" -vdRole server -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port
    $server = Start-Process -FilePath $serverExe -ArgumentList $serverArgs -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $clientArgs = '-batchmode -nographics -logFile "' + $clientLog + '" -vdRole client -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port + ' -vdAttackEvery 0.25 -vdAttackAim 0,1'
    $client = Start-Process -FilePath $clientExe -ArgumentList $clientArgs -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds $DurationSeconds
    $serverText = Get-Content -LiteralPath $serverLog -Raw
    $clientText = Get-Content -LiteralPath $clientLog -Raw
    $client.Refresh()
    $server.Refresh()
    if ($server.HasExited -or $client.HasExited -or $serverText.Contains('Crash!!!') -or $clientText.Contains('Crash!!!')) {
        throw "A player exited or crashed. See $serverLog and $clientLog."
    }
    if (-not $clientText.Contains('Connection approved and in game.') -or
        -not $serverText.Contains('[Waves] Started wave 1') -or
        -not $serverText.Contains('[Waves] Spawned enemy wave=1') -or
        -not $serverText.Contains('[EnemyCombat] heroHP=')) {
        throw "Expected transport, wave or enemy Combat evidence is absent. See $serverLog and $clientLog."
    }
    $fullWave = $serverText.IndexOf('livingEnemies=7')
    if (-not $serverText.Contains('dead=1 gold=166 xp=36 kills=2') -or
        -not $clientText.Contains('dead=1 gold=166 xp=36 kills=2') -or
        $fullWave -lt 0 -or
        -not [regex]::IsMatch($serverText.Substring($fullWave), 'livingEnemies=[1-6](?:\D|$)')) {
        throw "Confirmed enemy kill, reward or despawn evidence is absent. See $serverLog and $clientLog."
    }
    $samples = @([regex]::Matches($serverText, '\[ServerTick\] [^\r\n]+') | ForEach-Object { $_.Value })
    if ($samples.Count -lt 3) { throw "Too few server tick samples in $serverLog." }
    Write-Output "Normal wave and enemy attack passed for RunId=$runId."
    $samples | Select-Object -Last 3
}
finally {
    if ($client -ne $null) { Stop-Process -Id $client.Id -ErrorAction SilentlyContinue }
    if ($server -ne $null) { Stop-Process -Id $server.Id -ErrorAction SilentlyContinue }
    Remove-Item Env:VD_DEV_TICKET, Env:VD_DEV_ACCOUNT_ID -ErrorAction SilentlyContinue
}
