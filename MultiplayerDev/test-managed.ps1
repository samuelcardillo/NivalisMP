[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [version]'7.4') { throw 'Use PowerShell 7.4 or newer (pwsh).' }

# PowerShell ships Roslyn and a complete managed runtime. No game, .NET SDK,
# NuGet packages, private fixtures, or installed multiplayer server are needed.
[void][Microsoft.CodeAnalysis.CSharp.CSharpCompilation]
$testFiles = @(
    'ManagedTestRunner.cs', 'ChatProtocolTests.cs', 'BoatTriggerGeometryTests.cs',
    'PlayerSceneMembershipTests.cs', 'AccountAuthenticationTests.cs',
    'AccountStateChunksTests.cs', 'ApartmentTests.cs', 'SleepConsensusTests.cs',
    'MapCatchupProgressTests.cs', 'PeerWorldEventQueueTests.cs',
    'DedicatedBackpressureTests.cs', 'WorldFlowTests.cs'
)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Shared') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Server') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$sources += @($testFiles | ForEach-Object { Join-Path $PSScriptRoot "Tests/$_" })
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll' -File) {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithMainTypeName('ManagedTestRunner')
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('NivalisMP.ManagedTests').WithOptions($options).AddReferences($refs.ToArray())
foreach ($source in $sources) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $null, $source)
    $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
}
$stream = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($stream)
    foreach ($diagnostic in $result.Diagnostics) {
        if ($diagnostic.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) { Write-Host $diagnostic.ToString() }
    }
    if (-not $result.Success) { throw 'Managed test compilation failed.' }
    $assembly = [Reflection.Assembly]::Load($stream.ToArray())
} finally { $stream.Dispose() }
$exitCode = $assembly.EntryPoint.Invoke($null, @())
if ($exitCode -ne 0) { throw "Managed tests failed ($exitCode)." }
