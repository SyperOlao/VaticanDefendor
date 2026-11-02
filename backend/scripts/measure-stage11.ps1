param(
    [ValidateRange(1, 6)][int]$Runs = 6,
    [ValidateSet('small', 'medium', 'large')][string]$Level = 'small',
    [ValidateRange(15, 180)][int]$DurationSeconds = 45,
    [ValidateRange(1024, 64000)][int]$FirstPort = 8200,
    [string]$NetworkSimulatorProfile,
    [string]$Move
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serverExe = Join-Path $project 'Builds/Stage2/Server/VaticanDefendor.exe'
$clientExe = Join-Path $project 'Builds/Stage2/Client/VaticanDefendor.exe'
if (-not (Test-Path -LiteralPath $serverExe) -or -not (Test-Path -LiteralPath $clientExe)) {
    throw 'Build both Stage2 player targets first.'
}
$output = Join-Path $project ('Builds/Stage11/load-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$runEntries = [System.Collections.Generic.List[object]]::new()
$processes = [System.Collections.Generic.List[object]]::new()
$samples = [System.Collections.Generic.List[object]]::new()
$events = [System.Collections.Generic.List[object]]::new()
$levelCount = @{ small = 32; medium = 128; large = 512 }[$Level]

try {
    for ($i = 0; $i -lt $Runs; $i++) {
        $runId = [guid]::NewGuid().ToString('D')
        $ticket = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        $port = $FirstPort + $i
        $env:VD_DEV_TICKET = $ticket
        $env:VD_DEV_ACCOUNT_ID = "stage11-load-$i"
        $serverLog = Join-Path $output "server-$i.log"
        $server = Start-Process -FilePath $serverExe -PassThru -WindowStyle Hidden -ArgumentList @(
            '-batchmode', '-nographics', '-logFile', $serverLog, '-vdRole', 'server',
            '-vdDevelopmentIdentity', '-vdRunId', $runId, '-vdPort', $port,
            '-vdLoadLevel', $Level, '-vdMetrics')
        $processes.Add($server)
        $events.Add([pscustomobject]@{ AtUtc = (Get-Date).ToUniversalTime().ToString('o');
            Index = $i; Role = 'Server'; Pid = $server.Id; Event = 'started' })
        $runEntries.Add([pscustomobject]@{ Index = $i; RunId = $runId; Port = $port;
            ServerPid = $server.Id; ClientPid = 0; ServerLog = $serverLog;
            ClientLog = (Join-Path $output "client-$i.log"); Ticket = $ticket })
    }
    Start-Sleep -Seconds 5
    foreach ($run in $runEntries) {
        if ((Get-Content -LiteralPath $run.ServerLog -Raw).Contains('Listening on UDP')) {
            $events.Add([pscustomobject]@{ AtUtc = (Get-Date).ToUniversalTime().ToString('o');
                Index = $run.Index; Role = 'Server'; Pid = $run.ServerPid;
                Event = 'ready_observed' })
        }
        $env:VD_DEV_TICKET = $run.Ticket
        $env:VD_DEV_ACCOUNT_ID = "stage11-load-$($run.Index)"
        $clientArguments = @(
            '-batchmode', '-nographics', '-logFile', $run.ClientLog, '-vdRole', 'client',
            '-vdDevelopmentIdentity', '-vdRunId', $run.RunId, '-vdPort', $run.Port,
            '-vdMetrics')
        if ($NetworkSimulatorProfile) {
            $clientArguments += @('--loadNetworkSimulatorJsonFile',
                (Resolve-Path -LiteralPath $NetworkSimulatorProfile).Path)
        }
        if ($Move) { $clientArguments += @('-vdMove', $Move) }
        $client = Start-Process -FilePath $clientExe -PassThru -WindowStyle Hidden -ArgumentList $clientArguments
        $processes.Add($client)
        $run.ClientPid = $client.Id
        $events.Add([pscustomobject]@{ AtUtc = (Get-Date).ToUniversalTime().ToString('o');
            Index = $run.Index; Role = 'Client'; Pid = $client.Id; Event = 'started' })
    }
    Remove-Item Env:VD_DEV_TICKET, Env:VD_DEV_ACCOUNT_ID -ErrorAction SilentlyContinue
    $runEntries | Select-Object Index,RunId,Port,ServerPid,ClientPid,ServerLog,ClientLog |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'runs.json')
    $previousCpu = @{}
    $previousTime = @{}
    for ($second = 0; $second -lt $DurationSeconds; $second += 2) {
        Start-Sleep -Seconds 2
        foreach ($run in $runEntries) {
            if (-not ($events | Where-Object { $_.Index -eq $run.Index -and
                    $_.Event -eq 'approved_observed' }) -and
                (Get-Content -LiteralPath $run.ClientLog -Raw).Contains(
                    'Connection approved and in game.')) {
                $events.Add([pscustomobject]@{ AtUtc = (Get-Date).ToUniversalTime().ToString('o');
                    Index = $run.Index; Role = 'Client'; Pid = $run.ClientPid;
                    Event = 'approved_observed' })
            }
            foreach ($role in @('Server', 'Client')) {
                $pidValue = if ($role -eq 'Server') { $run.ServerPid } else { $run.ClientPid }
                $process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
                if ($null -eq $process) { continue }
                $key = [string]$pidValue
                $cpu = [double]$process.CPU
                $now = Get-Date
                $cpuPercent = if ($previousCpu.ContainsKey($key)) {
                    100.0 * ($cpu - $previousCpu[$key]) /
                        [math]::Max(0.001, ($now - $previousTime[$key]).TotalSeconds)
                } else { 0.0 }
                $previousCpu[$key] = $cpu
                $previousTime[$key] = $now
                $samples.Add([pscustomobject]@{ Seconds = $second + 2; Index = $run.Index;
                    Role = $role; Pid = $pidValue; AtUtc = $now.ToUniversalTime().ToString('o');
                    CpuPercentOfOneCore =
                    [math]::Round($cpuPercent, 1); WorkingSetMiB =
                    [math]::Round($process.WorkingSet64 / 1MB, 1); PrivateMiB =
                    [math]::Round($process.PrivateMemorySize64 / 1MB, 1) })
            }
        }
    }
    $samples | Export-Csv -LiteralPath (Join-Path $output 'process-samples.csv') -NoTypeInformation
    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($run in $runEntries) {
        $serverText = Get-Content -LiteralPath $run.ServerLog -Raw
        $clientText = Get-Content -LiteralPath $run.ClientLog -Raw
        if (-not $serverText.Contains("livingEnemies=$levelCount")) {
            $failures.Add("Server $($run.Index) did not reach $levelCount enemies")
        }
        if (-not $clientText.Contains('Connection approved and in game.')) {
            $failures.Add("Client $($run.Index) did not join")
        }
        if ($serverText.Contains('Crash!!!') -or $clientText.Contains('Crash!!!')) {
            $failures.Add("Run $($run.Index) crashed")
        }
        if ($serverText.Contains('Battle finished')) {
            $failures.Add("Run $($run.Index) reached terminal state during measurement")
        }
    }
    Write-Output "Stage11 load artifacts: $output"
    if ($failures.Count -gt 0) { throw ($failures -join '; ') }
    Write-Output "$Runs independent runs reached $levelCount enemies each for $DurationSeconds seconds."
}
finally {
    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        $events.Add([pscustomobject]@{ AtUtc = (Get-Date).ToUniversalTime().ToString('o');
            Index = -1; Role = 'Process'; Pid = $process.Id; Event = 'stop_requested' })
    }
    $events | Export-Csv -LiteralPath (Join-Path $output 'events.csv') -NoTypeInformation
    Remove-Item Env:VD_DEV_TICKET, Env:VD_DEV_ACCOUNT_ID -ErrorAction SilentlyContinue
}

