<#
.SYNOPSIS
Package a clearly labelled experimental alpha from this checkout's build.
.DESCRIPTION
Includes only the mod's binaries, launcher scripts, and public documentation.
Does not include a game, loader, generated bindings, user data, or test fixtures.
This packaging check is not native gameplay acceptance.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+-alpha(?:[.-][A-Za-z0-9]+)*$')]
    [string]$Version = '0.5.2-alpha'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dev = Join-Path $repo 'MultiplayerDev'
$out = Join-Path $repo 'dist'
$name = 'NivalisMP-' + $Version
$stage = Join-Path $out $name
$zip = Join-Path $out ($name + '.zip')
foreach ($path in @($stage,$zip,($zip+'.sha256'))) {
    if (Test-Path -LiteralPath $path) { throw "Release output already exists: $path" }
}
function SafeInput([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $repo $Relative))
    if (!$path.StartsWith($repo + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing or invalid release input: $Relative"
    }
    $entry = Get-Item -LiteralPath $path
    while ($entry) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release inputs must not use symlinks or junctions.' }
        $entry = if ($entry -is [IO.FileInfo]) { $entry.Directory } else { $entry.Parent }
    }
    return $path
}
$files = [ordered]@{
    'BepInEx/plugins/NivalisTogether/NivalisTogether.dll' = 'MultiplayerDev/build-v2/NivalisTogether.dll'
    'MultiplayerDev/build-v2/NivalisTogether.dll' = 'MultiplayerDev/build-v2/NivalisTogether.dll'
    'MultiplayerDev/build-v2/NivalisTogether.Server.dll' = 'MultiplayerDev/build-v2/NivalisTogether.Server.dll'
    'MultiplayerDev/build-v2/NivalisTogether.Server.runtimeconfig.json' = 'MultiplayerDev/build-v2/NivalisTogether.Server.runtimeconfig.json'
    'MultiplayerDev/Start-DedicatedServer.ps1' = 'MultiplayerDev/Start-DedicatedServer.ps1'
    'MultiplayerDev/Stop-DedicatedServer.ps1' = 'MultiplayerDev/Stop-DedicatedServer.ps1'
    'MultiplayerDev/Get-DedicatedServerStatus.ps1' = 'MultiplayerDev/Get-DedicatedServerStatus.ps1'
    'NivalisMP-README.md' = 'docs/RELEASE-README.md'
    'NivalisMP-INSTALL.md' = 'docs/INSTALL.md'
    'NivalisMP-LICENSE.txt' = 'LICENSE'
    'NivalisMP-THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
}
$inputs = @{}
foreach ($entry in $files.GetEnumerator()) { $inputs[$entry.Key] = SafeInput $entry.Value }
$protocolSource = [IO.File]::ReadAllText((SafeInput 'MultiplayerDev/Shared/DedicatedProtocol.cs'))
$protocolMatch = [regex]::Match($protocolSource,'public\s+const\s+int\s+Version\s*=\s*(\d+)')
if (!$protocolMatch.Success) { throw 'Protocol version could not be read.' }
$expectedAssemblies = @{
    'MultiplayerDev/build-v2/NivalisTogether.dll' = 'NivalisTogether'
    'MultiplayerDev/build-v2/NivalisTogether.Server.dll' = 'NivalisTogether.Server'
}
foreach ($entry in $expectedAssemblies.GetEnumerator()) {
    if ([Reflection.AssemblyName]::GetAssemblyName((SafeInput $entry.Key)).Name -cne $entry.Value) { throw 'Unexpected release assembly identity.' }
}
$build = Get-Content -LiteralPath (SafeInput 'MultiplayerDev/build-v2/build-manifest.json') -Raw | ConvertFrom-Json
if ($build.SchemaVersion -ne 1) { throw 'Unsupported build manifest; rebuild both targets.' }
$buildScriptHash = (Get-FileHash -LiteralPath (SafeInput 'MultiplayerDev/build-dedicated.ps1')).Hash
foreach ($target in 'Plugin','Server') {
    $artifact = $build.Artifacts.$target
    $assembly = if ($target -eq 'Plugin') { 'NivalisTogether' } else { 'NivalisTogether.Server' }
    if (!$artifact -or $artifact.AssemblyName -cne $assembly -or $artifact.File -cne ($assembly+'.dll') -or
        $artifact.BuildScriptSHA256 -cne $buildScriptHash -or
        $artifact.SHA256 -cne (Get-FileHash -LiteralPath (SafeInput ('MultiplayerDev/build-v2/'+$assembly+'.dll'))).Hash) {
        throw "The $target binary or build script differs from its build manifest. Rebuild before packaging."
    }
    $folders = @('Shared', $(if ($target -eq 'Plugin') { 'Source' } else { 'Server' }))
    $expectedSources = @(foreach ($folder in $folders) {
        Get-ChildItem -LiteralPath (Join-Path $dev $folder) -File -Filter '*.cs' | ForEach-Object { $folder+'/'+$_.Name }
    })
    $recordedSources = @($artifact.Sources | ForEach-Object Path)
    if (@(Compare-Object ($expectedSources|Sort-Object) ($recordedSources|Sort-Object)).Count -or
        @($recordedSources|Select-Object -Unique).Count -ne $recordedSources.Count) {
        throw "The $target source list differs from its build manifest."
    }
    foreach ($source in $artifact.Sources) {
        if ((Get-FileHash -LiteralPath (SafeInput ('MultiplayerDev/'+$source.Path))).Hash -cne $source.SHA256) {
            throw "Source changed after compilation: $($source.Path). Rebuild before packaging."
        }
    }
}
if ($build.Artifacts.Server.RuntimeConfig.File -cne 'NivalisTogether.Server.runtimeconfig.json' -or
    $build.Artifacts.Server.RuntimeConfig.SHA256 -cne
    (Get-FileHash -LiteralPath (SafeInput 'MultiplayerDev/build-v2/NivalisTogether.Server.runtimeconfig.json')).Hash) {
    throw 'Server runtime configuration differs from its build manifest.'
}
$sourceHashes = @(foreach ($directory in 'Source','Shared','Server') {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $dev $directory) -File -Filter '*.cs' | Sort-Object Name) {
        [ordered]@{ Path="MultiplayerDev/$directory/$($file.Name)"; SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash }
    }
})
[void][IO.Directory]::CreateDirectory($stage)
$manifestFiles = @(foreach ($entry in $files.GetEnumerator()) {
    $src = $inputs[$entry.Key]
    $dst = Join-Path $stage $entry.Key
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $dst))
    $hash = (Get-FileHash -LiteralPath $src).Hash
    Copy-Item -LiteralPath $src -Destination $dst
    if ((Get-FileHash -LiteralPath $dst).Hash -cne $hash) { throw 'Release input changed during packaging.' }
    [ordered]@{ Path=$entry.Key; Bytes=(Get-Item -LiteralPath $dst).Length; SHA256=$hash }
})
$manifest = [ordered]@{
    Project='NivalisMP'; Version=$Version; Protocol=[int]$protocolMatch.Groups[1].Value
    PackagedAtUtc=[DateTime]::UtcNow.ToString('o'); Channel='ALPHA'
    KnownBugs=@("Can't rent shops (yet)",'NPCs are acting a bit weird')
    NativeGameplayAcceptance='Incomplete; this alpha is not a claim of hitch-free or all-map/all-NPC acceptance.'
    BuildManifestSHA256=(Get-FileHash -LiteralPath (SafeInput 'MultiplayerDev/build-v2/build-manifest.json')).Hash
    SourceRepository='https://github.com/samuelcardillo/NivalisMP'
    Files=$manifestFiles; Sources=$sourceHashes
}
[IO.File]::WriteAllText((Join-Path $stage 'NivalisMP-release.json'),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $expected = @($files.Keys) + 'NivalisMP-release.json'
    $actual = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    if (@(Compare-Object ($expected|Sort-Object) ($actual|Sort-Object)).Count) { throw 'ZIP contents differ from the explicit release allowlist.' }
    foreach ($item in $manifestFiles) {
        $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\','/') -ceq $item.Path }
        $inputStream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)) } finally { $inputStream.Dispose() }
        if ($hash -cne $item.SHA256 -or $entry.Length -ne $item.Bytes) { throw 'ZIP entry integrity check failed.' }
    }
} finally { $archive.Dispose() }
$zipHash = (Get-FileHash -LiteralPath $zip).Hash
[IO.File]::WriteAllText($zip+'.sha256',($zipHash+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Archive=$zip;SHA256=$zipHash;Files=$manifestFiles.Count+1;Channel=$manifest.Channel;Protocol=$manifest.Protocol}
