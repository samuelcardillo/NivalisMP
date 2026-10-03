$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = Join-Path $PSScriptRoot ('independent-transition-results-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($dir in @('dotnet','BepInEx/core','BepInEx/interop')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll') {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('IndependentTransitionsProbe').WithOptions($options).AddReferences($refs.ToArray())
$sources = @(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Source') -Filter '*.cs' | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'IndependentTransitionsProbe.cs'
foreach ($source in $sources) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $null, $source)
    $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
}
$output = Join-Path $outDir 'IndependentTransitionsProbe.dll'
$stream = [IO.File]::Create($output)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
foreach ($diagnostic in $result.Diagnostics) { if ($diagnostic.Severity -ne 'Hidden') { $diagnostic.ToString() } }
if (!$result.Success) { throw 'Independent transition probe compilation failed' }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir 'IndependentTransitionsProbe.runtimeconfig.json')
& dotnet $output $gameRoot | Tee-Object -FilePath (Join-Path $outDir 'result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Independent transition probe failed' }
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Source/TravelReplication.cs'),(Join-Path $gameRoot 'MultiplayerDev/Source/ApartmentReplication.cs') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
