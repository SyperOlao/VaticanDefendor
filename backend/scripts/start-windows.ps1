#Requires -Version 7.0
param(
    [string]$PostgresBinDirectory,
    [ValidateRange(1, 65535)][int]$DatabasePort = 55432,
    [ValidatePattern('^[a-z][a-z0-9_]*$')][string]$DatabaseName = 'vatican',
    [ValidateRange(1, 65535)][int]$ApiPort = 8080,
    [ValidateRange(1, 1000000)][int]$RewardPerChurchLevel = 1
)

$ErrorActionPreference = 'Stop'
$ProjectDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$BackendDirectory = Join-Path $ProjectDirectory 'backend'
$RuntimeDirectory = Join-Path $BackendDirectory '.runtime'
$DatabaseDirectory = Join-Path $RuntimeDirectory 'postgres'
$ConfigurationPath = Join-Path $RuntimeDirectory 'local.json'
$PythonExecutable = Join-Path $BackendDirectory '.venv/Scripts/python.exe'
$ServerExecutable = Join-Path $ProjectDirectory 'Builds/Stage2/Server/VaticanDefendor.exe'
$SettingsPath = Join-Path $RuntimeDirectory 'windows-start.json'
$PowerShellExecutable = Join-Path $PSHOME 'pwsh.exe'

foreach ($RequiredPath in @($ConfigurationPath, $PythonExecutable, (Join-Path $DatabaseDirectory 'PG_VERSION')))
{
    if (!(Test-Path -LiteralPath $RequiredPath))
    {
        throw "Local setup is missing: $RequiredPath. See docs/architecture/ONLINE_BACKEND_RUN.md."
    }
}
if (!(Test-Path -LiteralPath $ServerExecutable))
{
    throw 'Build the server in Unity: VaticanDefendor > Network > Build Development Server.'
}

$Configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$Selection = Get-Content -LiteralPath (Join-Path $ProjectDirectory 'Assets/Resources/NetworkContent.asset') -Raw
$SelectedGuid = [regex]::Match($Selection, '_release:.*guid: ([0-9a-f]{32})').Groups[1].Value
$ContentReleaseId = ''
foreach ($Metadata in Get-ChildItem -LiteralPath (Join-Path $ProjectDirectory 'Assets/ActiveCombat/Content/Releases') -Filter '*.asset.meta')
{
    if ((Get-Content -LiteralPath $Metadata.FullName -Raw) -match "(?m)^guid: $SelectedGuid\r?$")
    {
        $ReleaseContent = Get-Content -LiteralPath ($Metadata.FullName -replace '\.meta$', '') -Raw
        $ContentReleaseId = [regex]::Match($ReleaseContent, '_releaseId: (sha256-[0-9a-f]{64})').Groups[1].Value
        break
    }
}
if (!$SelectedGuid -or !$ContentReleaseId)
{
    throw 'The selected ContentRelease could not be resolved from Assets/Resources/NetworkContent.asset.'
}
$ProtocolSource = Get-Content -LiteralPath (Join-Path $ProjectDirectory 'Assets/Scripts/Infrastructure/Networking/Contracts/JoinProtocol.cs') -Raw
$ProtocolVersion = [int][regex]::Match($ProtocolSource, 'Version\s*=\s*(\d+)').Groups[1].Value

if (!$PostgresBinDirectory)
{
    $OptionsPath = Join-Path $DatabaseDirectory 'postmaster.opts'
    if (Test-Path -LiteralPath $OptionsPath)
    {
        $Options = Get-Content -LiteralPath $OptionsPath -Raw
        $PostgresExecutable = [regex]::Match($Options, '^"?(.+?postgres\.exe)"?\s').Groups[1].Value
        if ($PostgresExecutable)
        {
            $PostgresBinDirectory = Split-Path -Parent $PostgresExecutable
        }
    }
    if (!$PostgresBinDirectory)
    {
        $PostgresCommand = Get-Command pg_ctl.exe -ErrorAction SilentlyContinue
        if ($PostgresCommand)
        {
            $PostgresBinDirectory = Split-Path -Parent $PostgresCommand.Source
        }
    }
}
if (!$PostgresBinDirectory -or !(Test-Path -LiteralPath (Join-Path $PostgresBinDirectory 'pg_ctl.exe')))
{
    throw 'PostgreSQL was not found. Pass -PostgresBinDirectory with the directory containing pg_ctl.exe.'
}
$PostgresControl = Join-Path $PostgresBinDirectory 'pg_ctl.exe'

function GetProjectProcess([string]$ProcessFile, [string]$ExecutablePath, [string]$CommandFragment)
{
    if (!(Test-Path -LiteralPath $ProcessFile))
    {
        return $null
    }
    $ProcessIdentifier = [int](Get-Content -LiteralPath $ProcessFile -Raw)
    $RunningProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessIdentifier"
    if (!$RunningProcess)
    {
        return $null
    }
    if ($RunningProcess.ExecutablePath -ne $ExecutablePath -or
        ($CommandFragment -and !$RunningProcess.CommandLine.Contains($CommandFragment)))
    {
        throw "PID file points to another process: $ProcessFile. No process was stopped."
    }
    return Get-Process -Id $ProcessIdentifier
}

function GetProcessFamily([System.Diagnostics.Process]$RootProcess)
{
    $RootProcess
    foreach ($ChildProcess in Get-CimInstance Win32_Process -Filter "ParentProcessId=$($RootProcess.Id)")
    {
        $RunningChild = Get-Process -Id $ChildProcess.ProcessId -ErrorAction SilentlyContinue
        if ($RunningChild)
        {
            GetProcessFamily $RunningChild
        }
    }
}

$ApiProcessFile = Join-Path $RuntimeDirectory 'api.pid'
$ServerProcessFile = Join-Path $RuntimeDirectory 'server.pid'
$ApiProcess = GetProjectProcess $ApiProcessFile $PythonExecutable 'vatican_backend.api:create_app'
$ServerProcess = GetProjectProcess $ServerProcessFile $ServerExecutable "-vdServerId $($Configuration.serverId)"
$LaunchSettings = [ordered]@{
    ContentReleaseId = $ContentReleaseId
    RewardPerChurchLevel = $RewardPerChurchLevel
    DatabasePort = $DatabasePort
    DatabaseName = $DatabaseName
    ApiPort = $ApiPort
}
$SettingsJson = $LaunchSettings | ConvertTo-Json -Compress
if ($ApiProcess)
{
    if (!(Test-Path -LiteralPath $SettingsPath) -or
        (Get-Content -LiteralPath $SettingsPath -Raw).Trim() -ne $SettingsJson)
    {
        throw "Backend PID $($ApiProcess.Id) is already running with other or unknown settings. Stop it before changing the local stack."
    }
}
$ApiListeners = @(Get-NetTCPConnection -State Listen -LocalPort $ApiPort -ErrorAction SilentlyContinue)
$ApiProcessIdentifiers = @()
if ($ApiProcess)
{
    $ApiProcessIdentifiers = @((GetProcessFamily $ApiProcess).Id)
}
if (@($ApiListeners | Where-Object { $_.OwningProcess -notin $ApiProcessIdentifiers }).Count -gt 0)
{
    throw "TCP port $ApiPort belongs to another process. No process was stopped."
}
$GameListeners = @(Get-NetUDPEndpoint -LocalPort $Configuration.port -ErrorAction SilentlyContinue)
if ($GameListeners.Count -gt 0 -and
    (!$ServerProcess -or @($GameListeners.OwningProcess | Where-Object { $_ -ne $ServerProcess.Id }).Count -gt 0))
{
    throw "UDP port $($Configuration.port) belongs to another process. No process was stopped."
}

$PreviousDatabaseUrl = $env:VD_DATABASE_URL
$bStartedApi = $false
$bStartedServer = $false
try
{
    & $PostgresControl status -D $DatabaseDirectory *> $null
    if ($LASTEXITCODE -eq 3)
    {
        Write-Output "Starting PostgreSQL on 127.0.0.1:$DatabasePort."
        & $PostgresControl start -D $DatabaseDirectory -l (Join-Path $RuntimeDirectory 'postgres.log') `
            -o "-p $DatabasePort -h 127.0.0.1" -w -t 30
        if ($LASTEXITCODE -ne 0)
        {
            throw 'PostgreSQL failed to start. See backend/.runtime/postgres.log.'
        }
    }
    elseif ($LASTEXITCODE -ne 0)
    {
        throw 'Could not inspect the project PostgreSQL cluster.'
    }
    $env:VD_DATABASE_URL = 'postgresql://vatican:' + [uri]::EscapeDataString($Configuration.dbPassword) +
        "@127.0.0.1:${DatabasePort}/$DatabaseName"
    $DatabaseCheck = @'
import os, sys
from pathlib import Path
import psycopg
try:
    with psycopg.connect(os.environ["VD_DATABASE_URL"], connect_timeout=3) as Connection:
        Directory = Connection.execute("SHOW data_directory").fetchone()[0]
        if Path(Directory).resolve() != Path(sys.argv[1]).resolve():
            raise RuntimeError("Another PostgreSQL cluster is listening on the requested port")
except Exception as Error:
    print("Project database check failed: " + type(Error).__name__, file=sys.stderr)
    sys.exit(1)
'@
    & $PythonExecutable -c $DatabaseCheck $DatabaseDirectory
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Cannot connect to the project database. Check DatabasePort, DatabaseName and local.json credentials.'
    }
    if (!$ApiProcess)
    {
        Write-Output "Starting backend for $ContentReleaseId (local test reward: $RewardPerChurchLevel)."
        & $PowerShellExecutable -NoProfile -File (Join-Path $PSScriptRoot 'start-api-local.ps1') `
            -ContentReleaseId $ContentReleaseId -RewardPerChurchLevel $RewardPerChurchLevel `
            -DatabasePort $DatabasePort -DatabaseName $DatabaseName -ApiPort $ApiPort
        $ApiExitCode = $LASTEXITCODE
        $ApiProcess = GetProjectProcess $ApiProcessFile $PythonExecutable 'vatican_backend.api:create_app'
        $bStartedApi = $null -ne $ApiProcess
        if ($ApiExitCode -ne 0 -or !$ApiProcess)
        {
            throw 'Backend startup failed. See Builds/Gameplay/api.err.log.'
        }
        Set-Content -LiteralPath $SettingsPath -Value $SettingsJson -Encoding utf8
    }
    $Health = Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/health/ready" -TimeoutSec 3
    if ($Health.status -ne 'ready')
    {
        throw 'Backend is not ready.'
    }
    if (!$ServerProcess)
    {
        Write-Output 'Starting the dedicated server.'
        & $PowerShellExecutable -NoProfile -File (Join-Path $PSScriptRoot 'run-server.ps1') -ApiPort $ApiPort
        $ServerExitCode = $LASTEXITCODE
        $ServerProcess = GetProjectProcess $ServerProcessFile $ServerExecutable "-vdServerId $($Configuration.serverId)"
        $bStartedServer = $null -ne $ServerProcess
        if ($ServerExitCode -ne 0 -or !$ServerProcess)
        {
            throw 'Dedicated server startup failed. See Builds/Gameplay/server.log.'
        }
    }
    if ($ServerProcess.WaitForExit(1500))
    {
        throw 'Dedicated server exited during startup. See Builds/Gameplay/server.log; rebuild it with Build Development Server after code or content changes.'
    }
    $ServerCheck = @'
import os, sys, time
import psycopg
with psycopg.connect(os.environ["VD_DATABASE_URL"], connect_timeout=3, autocommit=True) as Connection:
    for Attempt in range(40):
        Server = Connection.execute(
            "SELECT content_release_id, protocol_version FROM game_servers WHERE id=%s AND last_seen > now() - interval '10 seconds'",
            (sys.argv[1],)).fetchone()
        if Server:
            if Server != (sys.argv[2], int(sys.argv[3])):
                print("Server build is outdated. In Unity run VaticanDefendor > Network > Build Development Server.", file=sys.stderr)
                sys.exit(2)
            sys.exit(0)
        time.sleep(0.5)
print("Dedicated server did not register with the backend. See Builds/Gameplay/server.log.", file=sys.stderr)
sys.exit(1)
'@
    & $PythonExecutable -c $ServerCheck $Configuration.serverId $ContentReleaseId $ProtocolVersion
    if ($LASTEXITCODE -ne 0 -or $ServerProcess.HasExited -or
        !(Get-NetUDPEndpoint -LocalPort $Configuration.port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $ServerProcess.Id }))
    {
        throw 'Dedicated server is not ready. Check Builds/Gameplay/server.log and rebuild the server if necessary.'
    }
    Write-Output "Local stack ready. Backend: http://127.0.0.1:$ApiPort; game UDP: $($Configuration.port)."
    Write-Output 'In Unity select VaticanDefendor > Network > Play Local Online.'
    Write-Output "Logs: $(Join-Path $ProjectDirectory 'Builds/Gameplay'). Services continue running after this script exits."
}
catch
{
    if ($bStartedServer -and !$ServerProcess.HasExited)
    {
        Stop-Process -InputObject $ServerProcess
    }
    if ($bStartedApi -and !$ApiProcess.HasExited)
    {
        $ApiFamily = @(GetProcessFamily $ApiProcess)
        [array]::Reverse($ApiFamily)
        foreach ($FamilyProcess in $ApiFamily)
        {
            if (!$FamilyProcess.HasExited)
            {
                Stop-Process -InputObject $FamilyProcess
            }
        }
    }
    throw
}
finally
{
    $env:VD_DATABASE_URL = $PreviousDatabaseUrl
}
