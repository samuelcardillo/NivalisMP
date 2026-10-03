[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$source=Join-Path $devRoot 'Shared/BoatTriggerGeometry.cs'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BoatTriggerGeometryTests'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($file in @($source,(Join-Path $PSScriptRoot 'BoatTriggerGeometryTests.cs'))) {
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file),$null,$file)))
}
$stream=[IO.MemoryStream]::new()
try {
    $result=$comp.Emit($stream)
    $result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object { $_.ToString() }
    if(!$result.Success) { throw 'Boat collider geometry regression compilation failed.' }
    $testAssembly=[Reflection.Assembly]::Load($stream.ToArray())
    $assertions=$testAssembly.GetType('BoatTriggerGeometryTests').GetMethod('Run').Invoke($null,$null)
} finally { $stream.Dispose() }
[pscustomobject]@{
    Utc=[DateTime]::UtcNow.ToString('O');Passed=$true;Assertions=$assertions;
    SourceSHA256=(Get-FileHash -LiteralPath $source).Hash;
    Scope='Pure production 15-axis oriented-box intersection. Rotated hull, near misses, edge cross products and invalid geometry; no live native behavior claimed.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $devRoot 'inspection/boat-trigger-geometry-tests.json')
Write-Output "PASS: $assertions production boat trigger geometry assertions."
