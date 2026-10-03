$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outDir=Join-Path $PSScriptRoot ('client-mutation-budget-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll'){
  try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
  $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
 }
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithMainTypeName('ClientMutationBudgetTests')
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ClientMutationBudgetTests').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Source') -Filter '*.cs'|ForEach-Object FullName)+(Join-Path $PSScriptRoot 'ClientMutationBudgetTests.cs')
foreach($source in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$path=Join-Path $outDir 'ClientMutationBudgetTests.dll';$stream=[IO.File]::Create($path)
try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Client mutation budget compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'ClientMutationBudgetTests.runtimeconfig.json')
$sources|ForEach-Object{Get-FileHash -LiteralPath $_ -Algorithm SHA256|Select-Object Path,Hash}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
& dotnet $path
if($LASTEXITCODE -ne 0){throw 'Client mutation budget regression failed.'}
"Evidence: $outDir"
