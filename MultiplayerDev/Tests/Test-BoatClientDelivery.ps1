$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir=Join-Path $PSScriptRoot 'boat-client-delivery-probe-build'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$refs=[System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll') {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithMainTypeName('BoatClientDeliveryTests')
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BoatClientDeliveryTests').WithOptions($options).AddReferences($refs.ToArray())
$files=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Source') -Filter '*.cs')+@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs')+@(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'BoatClientDeliveryTests.cs'))
foreach($file in $files) {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName),$null,$file.FullName)
    $compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
}
$outFile=Join-Path $outDir 'BoatClientDeliveryTests.dll'
$stream=[IO.File]::Create($outFile)
try { $result=$compilation.Emit($stream) } finally { $stream.Dispose() }
foreach($diagnostic in $result.Diagnostics) { if($diagnostic.Severity -ne 'Hidden') { $diagnostic.ToString() } }
if(!$result.Success) { throw 'Boat client delivery probe compilation failed.' }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir 'BoatClientDeliveryTests.runtimeconfig.json')
& dotnet $outFile
if($LASTEXITCODE -ne 0) { throw 'Boat client delivery regression checks failed.' }
