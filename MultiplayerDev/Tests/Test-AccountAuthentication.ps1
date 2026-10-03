$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AccountAuthenticationTests').WithOptions($options).AddReferences($refs.ToArray())
$sources = @(
    (Join-Path $gameRoot 'MultiplayerDev/Shared/DedicatedProtocol.cs'),
    (Join-Path $gameRoot 'MultiplayerDev/Shared/AccountAuthentication.cs'),
    (Join-Path $PSScriptRoot 'AccountAuthenticationTests.cs')
)
foreach ($source in $sources) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $null, $source)
    $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
}
$memory = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($memory)
    foreach ($diagnostic in $result.Diagnostics) { if ($diagnostic.Severity -ne 'Hidden') { $diagnostic.ToString() } }
    if (!$result.Success) { throw 'Account authentication compilation failed' }
    $outDir = Join-Path $PSScriptRoot 'account-authentication-probe-build'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $output = Join-Path $outDir 'AccountAuthenticationTests.dll'
    [IO.File]::WriteAllBytes($output, $memory.ToArray())
} finally { $memory.Dispose() }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir 'AccountAuthenticationTests.runtimeconfig.json')
& dotnet $output
if ($LASTEXITCODE -ne 0) { throw 'Account authentication tests failed' }
