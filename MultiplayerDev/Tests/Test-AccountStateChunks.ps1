$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AccountStateChunksTests').WithOptions($options).AddReferences($refs.ToArray())
$sources = @(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'AccountStateChunksTests.cs'
foreach ($source in $sources) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $null, $source)
    $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
}
$memory = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($memory)
    if (!$result.Success) { $result.Diagnostics | ForEach-Object ToString; throw 'Private account tests failed to compile.' }
    $assembly = [Reflection.Assembly]::Load($memory.ToArray())
    $code = $assembly.EntryPoint.Invoke($null, @())
    if ($code -ne 0) { throw 'Private account tests failed.' }
} finally { $memory.Dispose() }
