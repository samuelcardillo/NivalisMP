$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CanonicalJsonTests').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+(Join-Path $PSScriptRoot 'CanonicalJsonTests.cs')
foreach($source in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$outDir=Join-Path $PSScriptRoot ('canonical-json-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$path=Join-Path $outDir 'CanonicalJsonTests.dll';$stream=[IO.File]::Create($path)
try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Canonical JSON regression compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'CanonicalJsonTests.runtimeconfig.json')
$sources|ForEach-Object{Get-FileHash -LiteralPath $_ -Algorithm SHA256|Select-Object Path,Hash}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
& dotnet $path (Join-Path $outDir 'results.json')
if($LASTEXITCODE -ne 0){throw 'Canonical JSON regression failed.'}
"Evidence: $outDir"
