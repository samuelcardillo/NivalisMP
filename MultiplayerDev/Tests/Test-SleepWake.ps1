[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
$devRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $devRoot
$outRoot = Join-Path $PSScriptRoot ('sleep-wake-results-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $outRoot | Out-Null
[void][Microsoft.CodeAnalysis.CSharp.CSharpCompilation]
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SleepWakeTests').WithOptions($options).AddReferences($refs.ToArray())
$sources = @(Get-ChildItem -LiteralPath (Join-Path $devRoot 'Shared') -Filter '*.cs' | ForEach-Object FullName) + @(Join-Path $PSScriptRoot 'SleepWakeTests.cs')
foreach ($source in $sources) { $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $null, $source))) }
$dll = Join-Path $outRoot 'SleepWakeTests.dll'
$stream = [IO.File]::Create($dll)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
foreach ($diagnostic in $result.Diagnostics) { if ($diagnostic.Severity -eq 'Error') { Write-Host $diagnostic.ToString() } }
if (-not $result.Success) { throw 'Sleep wake policy did not compile.' }
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outRoot 'SleepWakeTests.runtimeconfig.json')
& dotnet $dll | Tee-Object -FilePath (Join-Path $outRoot 'result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Sleep wake policy tests failed.' }
Write-Host "Evidence: $outRoot"
