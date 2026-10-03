[CmdletBinding()]
param(
    [string]$GamePath = '',
    [ValidateSet('Plugin','Server','All')] [string]$Target = 'All',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [version]'7.4') { throw 'Use PowerShell 7.4 or newer (pwsh).' }
if (-not $IsWindows) { throw 'The full game plugin build requires Windows. Use test-managed.ps1 for game-independent checks.' }
if (-not $GamePath) { $GamePath = Split-Path -Parent $PSScriptRoot }
$gameRoot = [IO.Path]::GetFullPath($GamePath)
$outDir = Join-Path $PSScriptRoot 'build-v2'
if ($Install -and $Target -eq 'Server') { throw '-Install installs the plugin; use -Target Plugin or All.' }
$requiredDirectories = @('dotnet')
if ($Target -ne 'Server') { $requiredDirectories += 'BepInEx/core', 'BepInEx/interop' }
foreach ($relative in $requiredDirectories) {
    $directory = Join-Path $gameRoot $relative
    if (-not (Test-Path -LiteralPath $directory -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $directory -Filter '*.dll' -File | Select-Object -First 1)) {
        throw "Missing build references in '$directory'. Point -GamePath at your installed game with BepInEx and generated interop assemblies. Launch the prepared game once to generate interop."
    }
}
if ($Install) {
    $gameExecutable = Join-Path $gameRoot 'Nivalis Nights.exe'
    foreach ($gameProcess in Get-CimInstance Win32_Process -Filter "Name='Nivalis Nights.exe'") {
        if (-not $gameProcess.ExecutablePath -or $gameProcess.ExecutablePath -eq $gameExecutable) {
            throw 'Close the game clients and dedicated simulation worker before installing the plugin. Building without -Install does not require closing them.'
        }
    }
}
$serviceOutput = [IO.Path]::GetFullPath((Join-Path $outDir 'NivalisTogether.Server.dll'))
foreach ($process in Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'") {
    if ($Target -ne 'Plugin' -and $process.CommandLine -and $process.CommandLine.IndexOf($serviceOutput, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Stop the dedicated server before replacing its build output.'
    }
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
[void][Microsoft.CodeAnalysis.CSharp.CSharpCompilation]
$buildScriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$manifestPath = Join-Path $outDir 'build-manifest.json'
$buildManifest = @{ SchemaVersion = 1; Artifacts = @{} }
if (Test-Path -LiteralPath $manifestPath) {
    $previousManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($previousManifest.SchemaVersion -eq 1 -and $previousManifest.Artifacts -is [System.Collections.IDictionary]) {
        $buildManifest = $previousManifest
    } else { throw 'Unsupported build manifest. Move it aside before rebuilding.' }
}

function Build-Assembly([string]$AssemblyName, [string[]]$SourceFiles, [bool]$GamePlugin) {
    $refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    $refDirs = @((Join-Path $gameRoot 'dotnet'))
    if ($GamePlugin) { $refDirs += (Join-Path $gameRoot 'BepInEx/core'), (Join-Path $gameRoot 'BepInEx/interop') }
    foreach ($directory in $refDirs) {
        foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.dll') {
            try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
            $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
        }
    }
    $kind = if ($GamePlugin) { [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary } else { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication }
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($kind).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($AssemblyName).WithOptions($options).AddReferences($refs.ToArray())
    $sourceRecords = [System.Collections.Generic.List[object]]::new()
    foreach ($source in $SourceFiles) {
        # Hash the exact bytes parsed, including any BOM, rather than reopening
        # a source file after compilation when it might already have changed.
        $sourceBytes = [IO.File]::ReadAllBytes($source)
        $sourceRecords.Add(@{
            Path = [IO.Path]::GetRelativePath($PSScriptRoot, $source).Replace('\', '/')
            SHA256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes))
        })
        $sourceStream = [IO.MemoryStream]::new($sourceBytes, $false)
        $reader = [IO.StreamReader]::new($sourceStream, [Text.Encoding]::UTF8, $true)
        try { $sourceText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceText, $null, $source)
        $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
    }
    $output = Join-Path $outDir ($AssemblyName + '.dll')
    $temporaryOutput = $output + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $stream = [IO.File]::Create($temporaryOutput)
        try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
        foreach ($diagnostic in $result.Diagnostics) { if ($diagnostic.Severity -ne 'Hidden') { Write-Host $diagnostic.ToString() } }
        if (-not $result.Success) { throw "Failed to build $AssemblyName. Existing output was preserved." }
        # Replacing a loaded output will fail without truncating the existing DLL.
        [IO.File]::Move($temporaryOutput, $output, $true)
    } finally {
        if (Test-Path -LiteralPath $temporaryOutput) { Remove-Item -LiteralPath $temporaryOutput }
    }
    if (-not $GamePlugin) {
        '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir ($AssemblyName + '.runtimeconfig.json'))
    }
    $targetName = if ($GamePlugin) { 'Plugin' } else { 'Server' }
    $record = @{
        AssemblyName = $AssemblyName
        File = $AssemblyName + '.dll'
        SHA256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        Sources = @($sourceRecords.ToArray())
        BuildScriptSHA256 = $buildScriptHash
        BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    }
    if (-not $GamePlugin) {
        $runtimeConfig = Join-Path $outDir ($AssemblyName + '.runtimeconfig.json')
        $record.RuntimeConfig = @{ File = $AssemblyName + '.runtimeconfig.json'; SHA256 = (Get-FileHash -LiteralPath $runtimeConfig -Algorithm SHA256).Hash }
    }
    # Partial builds update only their own attestation. They never claim that
    # a pre-existing DLL was rebuilt from the current sources.
    $buildManifest.Artifacts[$targetName] = $record
    $temporaryManifest = $manifestPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporaryManifest, ($buildManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryManifest, $manifestPath, $true)
    } finally {
        if (Test-Path -LiteralPath $temporaryManifest) { Remove-Item -LiteralPath $temporaryManifest }
    }
    Write-Host "Built $output"
}

$shared = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Shared') -Filter '*.cs' | ForEach-Object FullName)
if ($Target -in @('Server','All')) {
    $server = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Server') -Filter '*.cs' | ForEach-Object FullName)
    Build-Assembly 'NivalisTogether.Server' ($shared + $server) $false
}
if ($Target -in @('Plugin','All')) {
    $plugin = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -Filter '*.cs' | ForEach-Object FullName)
    Build-Assembly 'NivalisTogether' ($shared + $plugin) $true
    if ($Install) {
        $destination = Join-Path $gameRoot 'BepInEx/plugins/NivalisTogether/NivalisTogether.dll'
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $outDir 'NivalisTogether.dll') -Destination $destination -Force
        Write-Host "Installed $destination"
    }
}
