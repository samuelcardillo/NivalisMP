[CmdletBinding()]
param([string]$ServicePath = '', [string]$DotnetPath = '')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
$gameRoot = Split-Path -Parent $PSScriptRoot
if (-not $ServicePath) { $ServicePath = Join-Path $PSScriptRoot 'build-v2/NivalisTogether.Server.dll' }
if (-not (Test-Path -LiteralPath $ServicePath)) { throw 'Build the dedicated service first with build-dedicated.ps1 -Target Server.' }
if (-not $DotnetPath) { $DotnetPath = (Get-Command dotnet).Source }
$outDir = Join-Path $PSScriptRoot 'build-v2'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
[void][Microsoft.CodeAnalysis.CSharp.CSharpCompilation]
$references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('DedicatedTests').WithOptions($options).AddReferences($references.ToArray())
$sources = @((Join-Path $PSScriptRoot 'Tests/DedicatedTests.cs')) + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Shared') -Filter '*.cs' | ForEach-Object FullName)
foreach ($source in $sources) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $null, $source)
    $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
}
$assemblyPath = Join-Path $outDir 'DedicatedTests.dll'
$stream = [IO.File]::Create($assemblyPath)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
foreach ($diagnostic in $result.Diagnostics) { if ($diagnostic.Severity -ne 'Hidden') { Write-Host $diagnostic.ToString() } }
if (-not $result.Success) { throw 'Dedicated test compilation failed.' }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir 'DedicatedTests.runtimeconfig.json')
$artifactPath = Join-Path $PSScriptRoot ('Tests/dedicated-results-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
Write-Host "Protocol test artifacts: $artifactPath"
& $DotnetPath $assemblyPath ([IO.Path]::GetFullPath($ServicePath)) $DotnetPath $artifactPath
if ($LASTEXITCODE -ne 0) { throw "Dedicated protocol tests failed ($LASTEXITCODE); see $artifactPath." }
