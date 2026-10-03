$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outDir=Join-Path $PSScriptRoot ('world-preparation-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$session=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/DedicatedSession.cs'))).GetRoot()
$fields=@($session.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax] -and $_.Declaration.Type.ToString().StartsWith('BoundedPreparationFence<')})
if($fields.Count-ne1-or$fields[0].Declaration.Type.ToString()-ne'BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>'){throw 'All world snapshots must use exactly one common session fence.'}
$apply=$session.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText-eq'ApplyWorldEvent'}
$switch=$apply.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax]}|Select-Object -First 1
foreach($kind in @('shared-economy-chunk','business-state-chunk','business-craft-chunk')){
 $section=$switch.Sections|Where-Object {$_.Labels.ToString().Contains('"'+$kind+'"')}
 $calls=@($section.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax]})
 if(@($calls|Where-Object {$_.Expression.ToString()-eq'economyPreparation.Start'}).Count-ne1-or
    @($calls|Where-Object {$_.Expression.ToString()-eq'AppliedWorldEvent'}).Count-ne0-or
    @($section.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax]}).Count-ne1){throw "Final $kind must start the shared fence and return before ACK."}
}
'PASS actual session wiring: one common fence and all three final chunks defer their ACK.'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithMainTypeName('WorldPreparationFenceTests')
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('WorldPreparationFenceTests').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Source') -Filter '*.cs'|ForEach-Object FullName)+(Join-Path $PSScriptRoot 'WorldPreparationFenceTests.cs')
foreach($source in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$path=Join-Path $outDir 'WorldPreparationFenceTests.dll';$stream=[IO.File]::Create($path)
try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'World preparation production compile failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'WorldPreparationFenceTests.runtimeconfig.json')
& dotnet $path
if($LASTEXITCODE-ne0){throw 'World preparation fence regression failed.'}
"Evidence: $outDir"
