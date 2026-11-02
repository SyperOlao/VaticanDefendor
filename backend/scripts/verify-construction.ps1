param([int]$Port = 7997)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
$logDir = Join-Path $project 'Builds/Stage7'
if (-not (Test-Path -LiteralPath $serverExe) -or -not (Test-Path -LiteralPath $clientExe)) {
    throw 'Build both Development players before the construction check.'
}
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-ConstructionCase {
    param([string]$Name, [string]$Build, [string]$Expected, [int]$Replicated,
        [long]$Gold,
        [bool]$Repeat = $false,
        [string]$Production = '')

    $runId = [guid]::NewGuid().ToString('D')
    $env:VD_DEV_TICKET = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    $env:VD_DEV_ACCOUNT_ID = 'construction-verification-account'
    $serverLog = Join-Path $logDir "$Name-server.log"
    $clientLog = Join-Path $logDir "$Name-client.log"
    Remove-Item -LiteralPath $serverLog, $clientLog -ErrorAction SilentlyContinue
    $server = $null
    $client = $null
    try {
        $serverArguments = '-batchmode -nographics -logFile "' + $serverLog + '" -vdRole server -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port
        $server = Start-Process -FilePath $serverExe -ArgumentList $serverArguments -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 3
        $repeatFlag = if ($Repeat) { ' -vdBuildRepeat' } else { '' }
        $clientArguments = '-batchmode -nographics -logFile "' + $clientLog + '" -vdRole client -vdDevelopmentIdentity -vdRunId ' + $runId + ' -vdPort ' + $Port + ' -vdBuild ' + $Build + $repeatFlag
        $client = Start-Process -FilePath $clientExe -ArgumentList $clientArguments -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            $serverText = if (Test-Path -LiteralPath $serverLog) { Get-Content -LiteralPath $serverLog -Raw } else { '' }
            $clientText = if (Test-Path -LiteralPath $clientLog) { Get-Content -LiteralPath $clientLog -Raw } else { '' }
            $client.Refresh()
            if ($client.HasExited -or $serverText.Contains('Crash!!!') -or $clientText.Contains('Crash!!!')) {
                throw "$Name player exited or crashed. See $serverLog and $clientLog."
            }
            if ($serverText.Contains($Expected) -and $clientText.Contains($Expected) -and
                ($Replicated -eq 0 -or $clientText.Contains("replicatedStructures=$Replicated")) -and
                $clientText.Contains("replicatedGold=$Gold") -and
                ($Production -eq '' -or ($serverText.Contains($Production) -and
                    $clientText.Contains('replicatedStoredGold=25')))) {
                if ($Repeat -and ([regex]::Matches($serverText, 'result=Built built=3 gold=120')).Count -ne 2) {
                    Start-Sleep -Milliseconds 200
                    continue
                }
                Write-Output "$Name passed: $Expected; run=$runId."
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

Invoke-ConstructionCase -Name 'wall-repeat' -Build 'structure.wall,2,0,4,0' -Expected 'result=Built built=3 gold=120' -Replicated 3 -Gold 120 -Repeat $true
Invoke-ConstructionCase -Name 'outside' -Build 'structure.wall,99,0,99,0' -Expected 'result=OutOfBounds built=0 gold=150' -Replicated 0 -Gold 150
Invoke-ConstructionCase -Name 'sawmill' -Build 'structure.sawmill,-2,2,-2,2' -Expected 'result=Built built=1 gold=100' -Replicated 1 -Gold 100 -Production 'stored=25 cycles=1'
