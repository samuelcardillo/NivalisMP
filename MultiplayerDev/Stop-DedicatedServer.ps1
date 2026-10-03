[CmdletBinding()]
param(
    [string]$ServerRoot = (Join-Path $PSScriptRoot 'server-data\default'),
    [ValidateRange(1,300)][int]$WorkerTimeoutSeconds = 45,
    [ValidateRange(1,60)][int]$ServiceTimeoutSeconds = 10,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$serverDirectory = [IO.Path]::GetFullPath($ServerRoot).TrimEnd('\','/')
$statusScript = Join-Path $PSScriptRoot 'Get-DedicatedServerStatus.ps1'
$statePath = Join-Path $serverDirectory 'launcher-state.json'
if (!(Test-Path -LiteralPath $statePath -PathType Leaf)) {
    Write-Host 'No server launch is recorded for this world directory.'
    & $statusScript -ServerRoot $serverDirectory -NoNetwork
    return
}

function Stop-VerifiedProcess([string]$Kind) {
    $current = & $statusScript -ServerRoot $serverDirectory -NoNetwork
    $record = if ($Kind -eq 'Worker') { $current.Worker } else { $current.Service }
    if (!$record.Owned) { return }
    $process = Get-Process -Id ([int]$record.Pid) -ErrorAction SilentlyContinue
    if (!$process) { return }
    # Recheck start time immediately before stopping, to protect against PID reuse.
    $actual = $process.StartTime.ToUniversalTime()
    $expected = if ($record.StartTimeUtc -is [DateTime]) { $record.StartTimeUtc.ToUniversalTime() } else {
        [DateTime]::Parse([string]$record.StartTimeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    if ([Math]::Abs(($actual-$expected).TotalMilliseconds) -gt 1) { throw "The $Kind PID changed identity; it was not stopped." }
    $process.Kill()
    if (!$process.WaitForExit(5000)) { throw "The verified $Kind process has not exited after forced stop." }
}

$lock = $null
try {
    try { $lock = [IO.File]::Open((Join-Path $serverDirectory 'launcher.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another start or stop operation is already managing this server directory.' }
    $status = & $statusScript -ServerRoot $serverDirectory -NoNetwork
    if ($status.StateError) { throw ('Cannot safely stop processes from invalid launcher state: ' + $status.StateError) }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $runtimeRoot = Join-Path $serverDirectory ('runtime\' + $status.RunId)
    $workerStop = Join-Path $runtimeRoot 'stop-worker.request'
    $serviceStop = Join-Path $serverDirectory 'stop-service.request'
    foreach ($marker in @($workerStop,$serviceStop)) {
        if ((Test-Path -LiteralPath $marker) -and (((Get-Item -LiteralPath $marker -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Stop markers cannot be symbolic links.'
        }
    }
    # Status already verifies the runtime path. Derive markers rather than trusting saved marker paths.
    foreach ($markerDirectory in @($serverDirectory,$runtimeRoot)) {
        if (Test-Path -LiteralPath $markerDirectory) {
            for ($entry = [IO.DirectoryInfo]::new($markerDirectory); $entry; $entry=$entry.Parent) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Stop markers cannot use linked directories.' }
            }
        }
    }
    $forcedWorker = $false; $forcedService = $false
    if ($status.Worker.Owned) {
        [IO.File]::WriteAllText($workerStop, [DateTime]::UtcNow.ToString('O'), [Text.UTF8Encoding]::new($false))
        Write-Host 'Requested a final worker checkpoint and graceful exit.'
        $deadline = [DateTime]::UtcNow.AddSeconds($WorkerTimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 500
            $status = & $statusScript -ServerRoot $serverDirectory -NoNetwork
            if (!$status.Worker.Owned) { break }
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($status.Worker.Owned) {
            if (!$Force) {
                throw "The worker has not completed its checkpoint/exit after $WorkerTimeoutSeconds seconds. The service remains running. Inspect worker.log, or rerun with -Force to stop only these recorded processes; changes after the last committed snapshot may be lost."
            }
            Stop-VerifiedProcess 'Worker'; $forcedWorker=$true
            Write-Warning 'Worker was force-stopped. Persistence is limited to the last snapshot already committed by the service.'
        }
    }

    $status = & $statusScript -ServerRoot $serverDirectory -NoNetwork
    if ($status.Service.Owned) {
        [IO.File]::WriteAllText($serviceStop, [DateTime]::UtcNow.ToString('O'), [Text.UTF8Encoding]::new($false))
        Write-Host 'Requested graceful service shutdown.'
        $deadline = [DateTime]::UtcNow.AddSeconds($ServiceTimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 250
            $status = & $statusScript -ServerRoot $serverDirectory -NoNetwork
            if (!$status.Service.Owned) { break }
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($status.Service.Owned) {
            if (!$Force) { throw "The service has not exited after $ServiceTimeoutSeconds seconds. Rerun with -Force only if a forced shutdown is intended." }
            Stop-VerifiedProcess 'Service'; $forcedService=$true
        }
    }
    $state | Add-Member -NotePropertyName Phase -NotePropertyValue 'Stopped' -Force
    $state | Add-Member -NotePropertyName StoppedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O')) -Force
    $state | Add-Member -NotePropertyName ForcedWorkerStop -NotePropertyValue $forcedWorker -Force
    $state | Add-Member -NotePropertyName ForcedServiceStop -NotePropertyValue $forcedService -Force
    $temporary=$statePath+'.writing'
    [IO.File]::WriteAllText($temporary, ($state|ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$statePath,$true)
    Write-Host 'Recorded dedicated processes stopped. World snapshots and logs were retained.'
    & $statusScript -ServerRoot $serverDirectory -NoNetwork
} finally { if ($lock) { $lock.Dispose() } }
