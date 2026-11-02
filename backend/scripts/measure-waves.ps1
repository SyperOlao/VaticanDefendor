param(
    [ValidateSet('small', 'medium', 'large')][string[]]$Levels = @('small', 'medium', 'large'),
    [ValidateRange(8, 120)][int]$DurationSeconds = 12,
    [int]$Port = 8001
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
$logDir = Join-Path $project 'Builds/Stage6'
if (-not (Test-Path -LiteralPath $serverExe) -or -not (Test-Path -LiteralPath $clientExe)) {
    throw 'Build separate server and client Development players first.'
}
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$levelCounts = @{ small = 32; medium = 128; large = 512 }

foreach ($level in $Levels) {
    $runId = [guid]::NewGuid().ToString('D')
    $env:VD_DEV_TICKET = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $env:VD_DEV_ACCOUNT_ID = 'stage6-load-account'
    $serverLog = Join-Path $logDir "$level-server.log"
    $clientLog = Join-Path $logDir "$level-client.log"
    Remove-Item -LiteralPath $serverLog, $clientLog -ErrorAction SilentlyContinue
    $server = $null
    $client = $null
    try {
        $serverArguments = '-batchmode -nographics -logFile "' + $serverLog + '" -vdRole server -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port + ' -vdLoadLevel ' + $level
        $server = Start-Process -FilePath $serverExe -ArgumentList $serverArguments -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 3
        $clientArguments = '-batchmode -nographics -logFile "' + $clientLog + '" -vdRole client -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port
        $client = Start-Process -FilePath $clientExe -ArgumentList $clientArguments -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds $DurationSeconds
        $serverText = Get-Content -LiteralPath $serverLog -Raw
        $clientText = Get-Content -LiteralPath $clientLog -Raw
        if ($serverText.Contains('Crash!!!') -or $clientText.Contains('Crash!!!') -or
            -not $serverText.Contains("livingEnemies=$($levelCounts[$level])") -or
            -not $clientText.Contains('Connection approved and in game.')) {
            throw "$level did not reach its enemy level with an approved client. See $serverLog and $clientLog."
        }
        $samples = @([regex]::Matches($serverText, '\[ServerTick\] [^\r\n]+') |
            ForEach-Object { $_.Value })
        Write-Output "$level ($($levelCounts[$level]) enemies): $($samples[-1])"
    }
    finally {
        if ($client -ne $null) { Stop-Process -Id $client.Id -ErrorAction SilentlyContinue }
        if ($server -ne $null) { Stop-Process -Id $server.Id -ErrorAction SilentlyContinue }
        Remove-Item Env:VD_DEV_TICKET, Env:VD_DEV_ACCOUNT_ID -ErrorAction SilentlyContinue
    }
}
