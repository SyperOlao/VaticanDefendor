param([ValidateRange(1024, 65535)][int]$Port = 7991)

$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ServerExecutable = Join-Path $ProjectDirectory 'Builds/Stage2/Server/VaticanDefendor.exe'
$ClientExecutable = Join-Path $ProjectDirectory 'Builds/Stage2/Client/VaticanDefendor.exe'
$LogDirectory = Join-Path $ProjectDirectory 'Builds/Gameplay/Network'
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$ServerLog = Join-Path $LogDirectory 'server.log'
$ClientLog = Join-Path $LogDirectory 'client.log'
$PreviousTicket = $env:VD_DEV_TICKET
$PreviousAccount = $env:VD_DEV_ACCOUNT_ID
$RunIdentifier = [guid]::NewGuid().ToString('D')
$env:VD_DEV_TICKET = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:VD_DEV_ACCOUNT_ID = 'gameplay-network-verification'
$ServerProcess = $null
$ClientProcess = $null
try
{
    Remove-Item -LiteralPath $ServerLog, $ClientLog -ErrorAction SilentlyContinue
    $ServerProcess = Start-Process -FilePath $ServerExecutable -PassThru -WindowStyle Hidden -ArgumentList @(
        '-batchmode', '-nographics', '-logFile', ('"' + $ServerLog + '"'),
        '-vdRole', 'server', '-vdDevelopmentIdentity', '-vdRunId', $RunIdentifier,
        '-vdPort', $Port, '-vdLoadLevel', 'small')
    Start-Sleep -Seconds 2
    $ClientProcess = Start-Process -FilePath $ClientExecutable -PassThru -WindowStyle Hidden -ArgumentList @(
        '-batchmode', '-nographics', '-logFile', ('"' + $ClientLog + '"'),
        '-vdRole', 'client', '-vdDevelopmentIdentity', '-vdRunId', $RunIdentifier,
        '-vdPort', $Port, '-vdProbeReconnect')
    $null = $ClientProcess.Handle
    $Deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $Deadline)
    {
        $ServerProcess.Refresh()
        $ClientProcess.Refresh()
        $ServerText = ''
        $ClientText = ''
        if (Test-Path -LiteralPath $ServerLog)
        {
            $ServerText = Get-Content -LiteralPath $ServerLog -Raw
        }
        if (Test-Path -LiteralPath $ClientLog)
        {
            $ClientText = Get-Content -LiteralPath $ClientLog -Raw
        }
        if ($ServerText.Contains('Crash!!!') -or $ClientText.Contains('Crash!!!') -or
            $ClientText.Contains('[ReconnectProbe] FAIL:') -or $ClientText.Contains('Collection mismatched'))
        {
            throw "Network verification failed. See $LogDirectory."
        }
        if ($ClientText.Contains('[ReconnectProbe] PASS: Same client World reconnected to run ') -and $ServerText.Contains('[Waves] Spawned enemy'))
        {
            if (-not $ClientProcess.WaitForExit(15000))
            {
                throw "The reconnect probe passed but the player did not shut down in 15 seconds. See $LogDirectory."
            }
            $ClientProcess.Refresh()
            if ($ClientProcess.ExitCode -ne 0)
            {
                throw "The reconnect probe player exited with code $($ClientProcess.ExitCode). See $LogDirectory."
            }
            Write-Output "Standalone client/server verification passed: ghosts, waves, pause and reconnect. Logs: $LogDirectory."
            return
        }
        if ($ServerProcess.HasExited -or $ClientProcess.HasExited)
        {
            throw "A player exited before verification completed. See $LogDirectory."
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Network verification timed out. See $LogDirectory."
}
finally
{
    if ($ClientProcess -ne $null)
    {
        Stop-Process -Id $ClientProcess.Id -ErrorAction SilentlyContinue
    }
    if ($ServerProcess -ne $null)
    {
        Stop-Process -Id $ServerProcess.Id -ErrorAction SilentlyContinue
    }
    $env:VD_DEV_TICKET = $PreviousTicket
    $env:VD_DEV_ACCOUNT_ID = $PreviousAccount
}
