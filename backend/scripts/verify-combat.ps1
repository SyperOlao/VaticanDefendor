param([int]$Port = 7995)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
$logDir = Join-Path $project 'Builds/Stage5'
if (-not (Test-Path -LiteralPath $serverExe) -or -not (Test-Path -LiteralPath $clientExe)) {
    throw 'Build the server and client Development players separately before running this check.'
}
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-CombatCase {
    param([string]$Name, [string]$Aim, [string]$ExpectedServer, [string]$ExpectedClient)

    $runId = [guid]::NewGuid().ToString('D')
    $env:VD_DEV_TICKET = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $env:VD_DEV_ACCOUNT_ID = 'combat-verification-account'
    $serverLog = Join-Path $logDir "$Name-server.log"
    $clientLog = Join-Path $logDir "$Name-client.log"
    Remove-Item -LiteralPath $serverLog, $clientLog -ErrorAction SilentlyContinue
    $server = $null
    $client = $null
    try {
        $serverArguments = '-batchmode -nographics -logFile "' + $serverLog + '" -vdRole server -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port
        $server = Start-Process -FilePath $serverExe -ArgumentList $serverArguments -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 3
        $clientArguments = '-batchmode -nographics -logFile "' + $clientLog + '" -vdRole client -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port + ' -vdAttackEvery 0.05 -vdAttackAim ' + $Aim
        $client = Start-Process -FilePath $clientExe -ArgumentList $clientArguments -PassThru -WindowStyle Hidden

        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $serverText = if (Test-Path -LiteralPath $serverLog) { Get-Content -LiteralPath $serverLog -Raw } else { '' }
            $clientText = if (Test-Path -LiteralPath $clientLog) { Get-Content -LiteralPath $clientLog -Raw } else { '' }
            $client.Refresh()
            if ($client.HasExited -or $serverText.Contains('Crash!!!') -or $clientText.Contains('Crash!!!')) {
                throw "$Name player exited or crashed. See $serverLog and $clientLog."
            }
            if ($serverText.Contains($ExpectedServer) -and $clientText.Contains($ExpectedClient)) {
                if ($Name -eq 'forward') {
                    if (-not $serverText.Contains('outcome=Hit damage=30 targetHP=23 dead=0') -or
                        -not $clientText.Contains('outcome=Hit damage=30 targetHP=23 dead=0') -or
                        -not $serverText.Contains('outcome=Cooldown') -or
                        ([regex]::Matches($serverText, 'dead=1 gold=158 xp=18 kills=1')).Count -ne 1) {
                        Start-Sleep -Milliseconds 200
                        continue
                    }
                }
                if ($Name -eq 'backward' -and ($serverText.Contains('outcome=Hit') -or
                    $serverText.Contains('gold=158') -or $clientText.Contains('outcome=Hit'))) {
                    throw 'Backward attack unexpectedly hit or awarded a kill.'
                }
                Write-Output "$Name passed: server and client confirmed expected combat result for $runId."
                return
            }
            Start-Sleep -Milliseconds 250
        }
        throw "$Name timed out. See $serverLog and $clientLog."
    }
    finally {
        if ($client -ne $null) { Stop-Process -Id $client.Id -ErrorAction SilentlyContinue }
        if ($server -ne $null) { Stop-Process -Id $server.Id -ErrorAction SilentlyContinue }
        Remove-Item Env:VD_DEV_TICKET, Env:VD_DEV_ACCOUNT_ID -ErrorAction SilentlyContinue
    }
}

Invoke-CombatCase -Name 'forward' -Aim '0,1' -ExpectedServer 'outcome=Hit damage=23 targetHP=0 dead=1 gold=158 xp=18 kills=1' -ExpectedClient 'outcome=Hit damage=23 targetHP=0 dead=1 gold=158 xp=18 kills=1'
Invoke-CombatCase -Name 'backward' -Aim '0,-1' -ExpectedServer 'outcome=Miss damage=0 targetHP=-1 dead=0 gold=150 xp=0 kills=0' -ExpectedClient 'outcome=Miss damage=0 targetHP=-1 dead=0 gold=150 xp=0 kills=0'
