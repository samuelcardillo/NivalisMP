[CmdletBinding()]
param(
    [string]$ServerRoot = (Join-Path $PSScriptRoot 'server-data\default'),
    [string]$Name = 'Nivalis Server',
    [ValidateSet('0.0.0.0','127.0.0.1')][string]$Bind = '0.0.0.0',
    [ValidateRange(1,65534)][int]$Port = 47778,
    [ValidateRange(1,32)][int]$MaxPlayers = 4,
    [ValidateRange(0,600)][int]$WaitReadySeconds = 120,
    [switch]$WithGraphics
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this launcher with PowerShell 7 (pwsh).' }
$gameRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$serverDirectory = [IO.Path]::GetFullPath($ServerRoot).TrimEnd('\','/')
$serviceDll = Join-Path $PSScriptRoot 'build-v2\NivalisTogether.Server.dll'
$builtPlugin = Join-Path $PSScriptRoot 'build-v2\NivalisTogether.dll'
$installedPlugin = Join-Path $gameRoot 'BepInEx\plugins\NivalisTogether\NivalisTogether.dll'
$gameExe = Join-Path $gameRoot 'Nivalis Nights.exe'
$statusScript = Join-Path $PSScriptRoot 'Get-DedicatedServerStatus.ps1'
$statePath = Join-Path $serverDirectory 'launcher-state.json'
$dotnetExe = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
foreach ($required in @($serviceDll, $builtPlugin, $installedPlugin, $gameExe, $statusScript)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file is missing: $required. Build and install the dedicated plugin first." }
}
if ((Get-FileHash -LiteralPath $builtPlugin).Hash -ne (Get-FileHash -LiteralPath $installedPlugin).Hash) {
    throw 'The installed multiplayer plugin differs from build-v2. Close the game and run build-dedicated.ps1 -Target Plugin -Install before starting the server.'
}
if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 80 -or $Name -match '[\x00-\x1f\x7f]') { throw 'Server name must contain 1-80 printable characters.' }
if ($serverDirectory.Length -le [IO.Path]::GetPathRoot($serverDirectory).Length) { throw 'Use a dedicated world directory, not a drive root.' }
[void][IO.Directory]::CreateDirectory($serverDirectory)
for ($entry = [IO.DirectoryInfo]::new($serverDirectory); $entry; $entry = $entry.Parent) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Server storage must not use junctions or symbolic links.' }
}

function Quote-NativeArgument([string]$Value) {
    # Start-Process accepts a Windows command line, not an argv array. Escape each argument.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Save-LauncherState {
    $temporary = $statePath + '.writing'
    [IO.File]::WriteAllText($temporary, ($script:state | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $statePath, $true)
}
function Get-ProcessRecord([Diagnostics.Process]$Process, [string]$Executable) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "Process exited immediately: $Executable (exit $($Process.ExitCode))." }
    return [ordered]@{ Pid=$Process.Id; StartTimeUtc=$Process.StartTime.ToUniversalTime().ToString('O'); ExecutablePath=[IO.Path]::GetFullPath($Executable) }
}
function Stop-FailedLaunchProcess([Diagnostics.Process]$Process) {
    if ($null -eq $Process) { return }
    try { $Process.Refresh(); if (!$Process.HasExited) { $Process.Kill(); [void]$Process.WaitForExit(5000) } } catch { Write-Warning ('Could not stop this launch attempt: ' + $_.Exception.Message) }
}

$lock = $null; $serviceProcess = $null; $workerProcess = $null; $launched = $false
try {
    try { $lock = [IO.File]::Open((Join-Path $serverDirectory 'launcher.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another start or stop operation is already managing this server directory.' }
    $previous = & $statusScript -ServerRoot $serverDirectory -NoNetwork
    if ($previous.Service.Owned -or $previous.Worker.Owned) { throw 'This server already has a running service or worker. Use Get-DedicatedServerStatus.ps1 or Stop-DedicatedServer.ps1 first.' }
    if ($previous.StateError) { throw ('Resolve the invalid launcher state before starting: ' + $previous.StateError) }

    # Do not launch against a port owned by an unrelated process.
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse($Bind), $Port)
    try { $probe.Start() } catch { throw "TCP $Port is already unavailable. No processes were stopped." } finally { $probe.Stop() }
    $runId = [Guid]::NewGuid().ToString('N')
    $runtimeRoot = Join-Path $serverDirectory ('runtime\' + $runId)
    [void][IO.Directory]::CreateDirectory($runtimeRoot)
    $serviceLog = Join-Path $runtimeRoot 'service.log'
    $serviceErrorLog = Join-Path $runtimeRoot 'service-errors.log'
    $workerLog = Join-Path $runtimeRoot 'worker.log'
    $workerStop = Join-Path $runtimeRoot 'stop-worker.request'
    $serviceStop = Join-Path $serverDirectory 'stop-service.request'
    $authorityFile = Join-Path $serverDirectory 'authority-secret.txt'
    if (Test-Path -LiteralPath $serviceStop) { Remove-Item -LiteralPath $serviceStop -Force }
    $script:state = [ordered]@{
        SchemaVersion=1; RunId=$runId; GameRoot=$gameRoot; ServerRoot=$serverDirectory; RuntimeRoot=$runtimeRoot
        Name=$Name; Bind=$Bind; Port=$Port; DiscoveryPort=($Port+1); StartedUtc=[DateTime]::UtcNow.ToString('O')
        Phase='StartingService'; Service=$null; Worker=$null; LastError=$null
        ServiceLog=$serviceLog; ServiceErrorLog=$serviceErrorLog; WorkerLog=$workerLog
        WorkerStopFile=$workerStop; ServiceStopFile=$serviceStop
    }
    Save-LauncherState
    $arguments = @($serviceDll, '--bind', $Bind, '--port', "$Port", '--name', $Name, '--max-players', "$MaxPlayers", '--save-root', $serverDirectory, '--stop-file', $serviceStop)
    $commandLine = ($arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    # The launcher/worker share the persisted authority file, even if this shell has an override.
    $priorAuthorityOverride = [Environment]::GetEnvironmentVariable('NIVALIS_AUTHORITY_SECRET', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('NIVALIS_AUTHORITY_SECRET', $null, 'Process')
        $serviceProcess = Start-Process -FilePath $dotnetExe -ArgumentList $commandLine -WorkingDirectory $gameRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $serviceLog -RedirectStandardError $serviceErrorLog
    } finally { [Environment]::SetEnvironmentVariable('NIVALIS_AUTHORITY_SECRET', $priorAuthorityOverride, 'Process') }
    $launched = $true; $script:state.Service = Get-ProcessRecord $serviceProcess $dotnetExe; Save-LauncherState
    $serviceDeadline = [DateTime]::UtcNow.AddSeconds(25)
    $listening = $false
    while ([DateTime]::UtcNow -lt $serviceDeadline) {
        $serviceProcess.Refresh()
        if ($serviceProcess.HasExited) { throw "The dedicated service exited with code $($serviceProcess.ExitCode). See $serviceErrorLog" }
        if (Test-Path -LiteralPath $authorityFile -PathType Leaf) {
            $status = & $statusScript -ServerRoot $serverDirectory -TimeoutMilliseconds 600
            if ($status.Service.Owned -and $status.Server) { $listening = $true; break }
        }
        Start-Sleep -Milliseconds 250
    }
    if (!$listening) { throw "The service did not provide its local secret and status handshake. See $serviceLog" }
    $script:state.Phase = 'StartingWorker'; Save-LauncherState
    $arguments = @('-batchmode')
    if (!$WithGraphics) { $arguments += '-nographics' }
    $arguments += @('-logFile', $workerLog, '-nivalis-worker', '-nivalis-world-root', $runtimeRoot,
        '-nivalis-server', '127.0.0.1', '-nivalis-port', "$Port", '-nivalis-authority-file', $authorityFile,
        '-nivalis-stop-file', $workerStop)
    $commandLine = ($arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    $workerProcess = Start-Process -FilePath $gameExe -ArgumentList $commandLine -WorkingDirectory $gameRoot -WindowStyle Hidden -PassThru
    $script:state.Worker = Get-ProcessRecord $workerProcess $gameExe; Save-LauncherState
    Write-Host "Dedicated service and Unity worker started. TCP $Port; LAN discovery UDP $($Port+1)."
    Write-Host "Server world: $serverDirectory"
    $readyDeadline = [DateTime]::UtcNow.AddSeconds($WaitReadySeconds)
    $nextProgress = [DateTime]::MinValue
    do {
        $status = & $statusScript -ServerRoot $serverDirectory -TimeoutMilliseconds 1000
        if (!$status.Service.Owned -or !$status.Worker.Owned) { throw "A launched process exited or could not be verified. See $serviceErrorLog and $workerLog" }
        if ($status.Ready) { $script:state.Phase='Ready'; Save-LauncherState; Write-Host 'Server world is ready for players.'; break }
        if ($WaitReadySeconds -eq 0 -or [DateTime]::UtcNow -ge $readyDeadline) { break }
        if ([DateTime]::UtcNow -ge $nextProgress) {
            Write-Host ('Waiting for the server world: ' + $status.Server.State)
            $nextProgress=[DateTime]::UtcNow.AddSeconds(10)
        }
        Start-Sleep -Milliseconds 750
    } while ($true)
    if (!$status.Ready) { Write-Warning "Processes are running, but world readiness is not yet confirmed. Check Get-DedicatedServerStatus.ps1 and $workerLog" }
    $status
} catch {
    $launchFailure = $_
    if ($launched) {
        Stop-FailedLaunchProcess $workerProcess
        Stop-FailedLaunchProcess $serviceProcess
        $script:state.Phase='Failed'; $script:state.LastError=$launchFailure.Exception.Message
        try { Save-LauncherState } catch { }
    }
    throw
} finally { if ($lock) { $lock.Dispose() } }
