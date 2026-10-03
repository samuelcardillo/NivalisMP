$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BoatProtocolProbe' + [Guid]::NewGuid().ToString('N')).WithOptions(
    [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($references.ToArray())
$files = @((Join-Path $gameRoot 'MultiplayerDev/Source/PlayerAccountRuntime.cs'),
    (Join-Path $PSScriptRoot 'PlayerAccountRuntimeTests.cs'), (Join-Path $PSScriptRoot 'BoatProtocolTests.cs'))
$files += @(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs' | ForEach-Object FullName)
foreach ($file in $files) {
    $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@(
        [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file), $null, $file)))
}
$stream = [IO.MemoryStream]::new()
$result = $compilation.Emit($stream)
$result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object ToString
if (!$result.Success) { throw 'Production boat protocol/account runtime test compilation failed.' }
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$checks = $assembly.GetType('NivalisTogether.Dedicated.BoatProtocolProbe').GetMethod('Run').Invoke($null, @())
"Production boat protocol and account persistence PASS $checks checks (native inventory boundary doubles; no game process)"
