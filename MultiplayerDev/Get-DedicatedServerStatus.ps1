[CmdletBinding()]
param(
    [string]$ServerRoot = (Join-Path $PSScriptRoot 'server-data\default'),
    [switch]$AsJson,
    [switch]$NoNetwork,
    [ValidateRange(100,10000)][int]$TimeoutMilliseconds = 1500
)
$ErrorActionPreference = 'Stop'
$gameRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$serverDirectory = [IO.Path]::GetFullPath($ServerRoot)
$statePath = Join-Path $serverDirectory 'launcher-state.json'

function Test-SamePath([string]$First, [string]$Second) {
    return ![string]::IsNullOrWhiteSpace($First) -and ![string]::IsNullOrWhiteSpace($Second) -and
        [string]::Equals([IO.Path]::GetFullPath($First).TrimEnd('\','/'), [IO.Path]::GetFullPath($Second).TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)
}

function Get-OwnedProcess($Record, [string]$Kind, [string]$ExpectedDirectory) {
    $answer = [ordered]@{ Pid = $null; Running = $false; Owned = $false; Reason = 'No recorded process'; StartTimeUtc = $null }
    if ($null -eq $Record) { return [pscustomobject]$answer }
    $answer.Pid = $Record.Pid
    try {
        $process = Get-Process -Id ([int]$Record.Pid) -ErrorAction Stop
        $answer.Running = !$process.HasExited
        $actualStart = $process.StartTime.ToUniversalTime()
        $answer.StartTimeUtc = $actualStart.ToString('O')
        # PowerShell 7.5 converts ISO JSON dates to DateTime automatically. String coercion
        # would drop sub-second precision and incorrectly reject the correct process.
        $savedStart = if ($Record.StartTimeUtc -is [DateTime]) { $Record.StartTimeUtc.ToUniversalTime() } else {
            [DateTime]::Parse([string]$Record.StartTimeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        }
        if ([Math]::Abs(($actualStart - $savedStart).TotalMilliseconds) -gt 1) { $answer.Reason = 'PID belongs to a different process start'; return [pscustomobject]$answer }
        if (!(Test-SamePath $process.Path $Record.ExecutablePath)) { $answer.Reason = 'Executable path no longer matches'; return [pscustomobject]$answer }
        $native = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + [int]$Record.Pid) -ErrorAction Stop
        $command = [string]$native.CommandLine
        if ($command.IndexOf($ExpectedDirectory, [StringComparison]::OrdinalIgnoreCase) -lt 0) { $answer.Reason = 'Recorded world directory is absent from process command line'; return [pscustomobject]$answer }
        if ($Kind -eq 'Worker') {
            if (!(Test-SamePath $process.Path (Join-Path $gameRoot 'Nivalis Nights.exe')) -or !$command.Contains('-nivalis-worker') -or !$command.Contains('-nivalis-world-root')) {
                $answer.Reason = 'Process is not the recorded dedicated Unity worker'; return [pscustomobject]$answer
            }
        } elseif ($command.IndexOf((Join-Path $PSScriptRoot 'build-v2\NivalisTogether.Server.dll'), [StringComparison]::OrdinalIgnoreCase) -lt 0 -or !$command.Contains('--save-root')) {
            $answer.Reason = 'Process is not the dedicated service'; return [pscustomobject]$answer
        }
        $answer.Owned = $true; $answer.Reason = 'PID, start time, executable and world arguments match'
    } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { $answer.Reason = 'Process exited' }
      catch { $answer.Reason = 'Could not verify process identity: ' + $_.Exception.Message }
    return [pscustomobject]$answer
}

function Read-Frame([IO.Stream]$Stream) {
    $header = [byte[]]::new(4)
    for ($offset = 0; $offset -lt 4;) {
        $count = $Stream.Read($header, $offset, 4 - $offset)
        if ($count -eq 0) { throw 'The status connection closed.' }; $offset += $count
    }
    $length = ([long]$header[0] -shl 24) -bor ([long]$header[1] -shl 16) -bor ([long]$header[2] -shl 8) -bor $header[3]
    if ($length -lt 2 -or $length -gt 65536) { throw 'Invalid status frame length.' }
    $body = [byte[]]::new([int]$length)
    for ($offset = 0; $offset -lt $length;) {
        $count = $Stream.Read($body, $offset, [int]$length - $offset)
        if ($count -eq 0) { throw 'The status connection closed.' }; $offset += $count
    }
    return [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
}

function Get-ProtocolStatus([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $pending = $client.ConnectAsync('127.0.0.1', $Port)
        if (!$pending.Wait($TimeoutMilliseconds)) { throw 'Status connection timed out.' }
        [void]$pending.GetAwaiter().GetResult()
        $stream = $client.GetStream(); $stream.ReadTimeout = $TimeoutMilliseconds; $stream.WriteTimeout = $TimeoutMilliseconds
        $hello = Read-Frame $stream
        if ($hello.Type -ne 'serverHello' -or $hello.Sequence -ne 1 -or $hello.SessionId -notmatch '^[0-9a-f]{32}$') { throw 'Unrecognized dedicated server handshake.' }
        $request = @{ Type='statusQuery'; RequestId=''; SessionId=$hello.SessionId; Sequence=1; Payload=@{}; Mac='' } | ConvertTo-Json -Compress -Depth 5
        $body = [Text.Encoding]::UTF8.GetBytes($request)
        $header = [byte[]]::new(4)
        $header[0] = ($body.Length -shr 24) -band 255; $header[1] = ($body.Length -shr 16) -band 255
        $header[2] = ($body.Length -shr 8) -band 255; $header[3] = $body.Length -band 255
        $stream.Write($header,0,4); $stream.Write($body,0,$body.Length)
        $response = Read-Frame $stream
        if ($response.Type -ne 'serverStatus' -or $response.Sequence -ne 2 -or $response.SessionId -ne $hello.SessionId) { throw 'Invalid dedicated server status response.' }
        return $response.Payload
    } finally { $client.Dispose() }
}

$state = $null; $stateError = $null; $metadata = $null; $networkStatus = $null; $networkError = $null
if (Test-Path -LiteralPath $statePath) {
    try {
        if ((Get-Item -LiteralPath $statePath).Length -gt 32768) { throw 'Launcher state file is oversized.' }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($state.SchemaVersion -ne 1 -or !(Test-SamePath $state.GameRoot $gameRoot) -or !(Test-SamePath $state.ServerRoot $serverDirectory) -or $state.RunId -notmatch '^[0-9a-f]{32}$') { throw 'Launcher state identity is invalid.' }
        $expectedRuntime = Join-Path $serverDirectory ('runtime\' + $state.RunId)
        if (!(Test-SamePath $state.RuntimeRoot $expectedRuntime)) { throw 'Launcher runtime path is invalid.' }
    } catch { $stateError = $_.Exception.Message; $state = $null }
}
$service = Get-OwnedProcess $state.Service 'Service' $serverDirectory
$worker = Get-OwnedProcess $state.Worker 'Worker' $state.RuntimeRoot
$worldPath = Join-Path $serverDirectory 'world.json'
if (Test-Path -LiteralPath $worldPath) {
    try { if ((Get-Item -LiteralPath $worldPath).Length -le 16384) { $metadata = Get-Content -LiteralPath $worldPath -Raw | ConvertFrom-Json } } catch { }
}
if (!$NoNetwork -and $service.Owned -and $state.Port -ge 1 -and $state.Port -le 65534) {
    try {
        $networkStatus = Get-ProtocolStatus ([int]$state.Port)
        if ($metadata -and $networkStatus.WorldId -ne $metadata.WorldId) { throw 'The listening service has a different world identity.' }
    } catch { $networkError = $_.Exception.Message; $networkStatus = $null }
}
$ready = $service.Owned -and $worker.Owned -and $null -ne $networkStatus -and $networkStatus.Ready
$phase = if ($ready) { 'Ready' } elseif ($service.Owned -and $worker.Owned) { 'StartingOrUnavailable' } elseif ($service.Owned) { 'ServiceOnly' } elseif ($worker.Owned) { 'WorkerOnly' } elseif ($stateError) { 'InvalidState' } else { 'Stopped' }
$result = [pscustomobject][ordered]@{
    State=$phase; Ready=[bool]$ready; ServerRoot=$serverDirectory; RunId=$state.RunId
    WorldId=$metadata.WorldId; PersistedRevision=$metadata.Revision; SnapshotFile=$metadata.SnapshotFile
    Bind=$state.Bind; Port=$state.Port; DiscoveryPort=$state.DiscoveryPort
    Service=$service; Worker=$worker; Server=$networkStatus
    StateError=$stateError; NetworkError=$networkError
    ServiceLog=$state.ServiceLog; ServiceErrorLog=$state.ServiceErrorLog; WorkerLog=$state.WorkerLog
    WorkerStopFile=$state.WorkerStopFile; ServiceStopFile=$state.ServiceStopFile
    InternetAccess='Not configured or tested by these scripts'
}
if ($AsJson) { $result | ConvertTo-Json -Depth 8 } else { $result }
