param([string]$MenuPath)
$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll'){
  try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
  $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
 }
}
$shared=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs' | ForEach-Object FullName)
function Compile-Memory([string]$name,[string[]]$sources,[Microsoft.CodeAnalysis.OutputKind]$kind){
 $options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($kind)
 $compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name).WithOptions($options).AddReferences($refs.ToArray())
 foreach($path in $sources){$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path);$compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))}
 $stream=[IO.MemoryStream]::new();$result=$compilation.Emit($stream)
 if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Account UI/flow memory compilation failed'}
 $bytes=$stream.ToArray();$stream.Dispose();return ,$bytes
}
$flow=Join-Path $gameRoot 'MultiplayerDev/Source/AccountClientFlow.cs'
$menu=Join-Path $gameRoot 'MultiplayerDev/Source/MultiplayerMenu.cs'
if($MenuPath){$menu=(Resolve-Path -LiteralPath $MenuPath).Path}
$null=Compile-Memory 'AccountMenuCompile' ($shared+@($flow,$menu)) ([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
'PASS: current native Menu+AccountClientFlow+Shared compilation (in memory).'
$bytes=Compile-Memory 'AccountClientFlowTests' ($shared+@($flow,(Join-Path $PSScriptRoot 'AccountClientFlowTests.cs'))) ([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$assembly=[Reflection.Assembly]::Load($bytes)
$outDir=Join-Path $gameRoot ('MultiplayerDev/inspection/account-client-tests/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$result=$assembly.EntryPoint.Invoke($null,[object[]]@(,[string[]]@($outDir)))
if($result -ne 0){throw 'Client flow regression failed'}
$menuTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($menu))
$methodNames=@('AcquireAccountInputGuard','ReleaseAccountInputGuard','ResetForTitle')
$methods=@($menuTree.GetRoot().DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $methodNames})
if($methods.Count -ne $methodNames.Count){throw 'Account native guard fixture could not extract production methods'}
$guardSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AccountMenuGuardTests.cs')).Replace('// PRODUCTION_METHODS',(($methods | ForEach-Object ToFullString) -join "`n"))
$guardCompilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AccountMenuGuardTests').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($guardSource)))
$guardStream=[IO.MemoryStream]::new(); $guardEmit=$guardCompilation.Emit($guardStream)
if(!$guardEmit.Success){$guardEmit.Diagnostics|ForEach-Object ToString;throw 'Account guard regression compilation failed'}
$guardAssembly=[Reflection.Assembly]::Load($guardStream.ToArray());$guardStream.Dispose()
if($guardAssembly.EntryPoint.Invoke($null,$null) -ne 0){throw 'Account guard regression failed'}
$presentation=@($menuTree.GetRoot().DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'DialogueRecoveryPresentation'})
if($presentation.Count -eq 1){
 $recoverySource='using System;using System.Collections.Generic;using NivalisTogether.Dedicated;namespace NivalisTogether {'+$presentation[0].ToFullString()+'}'
 $recoveryCompilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('DialogueRecoveryMenuTests').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)).AddReferences($refs.ToArray())
 foreach($file in ($shared+@(Join-Path $PSScriptRoot 'DialogueRecoveryMenuTests.cs'))){$recoveryCompilation=$recoveryCompilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file))))}
 $recoveryCompilation=$recoveryCompilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($recoverySource)))
 $recoveryStream=[IO.MemoryStream]::new();$recoveryEmit=$recoveryCompilation.Emit($recoveryStream)
 if(!$recoveryEmit.Success){$recoveryEmit.Diagnostics|ForEach-Object ToString;throw 'Dialogue recovery presentation compilation failed'}
 $recoveryAssembly=[Reflection.Assembly]::Load($recoveryStream.ToArray());$recoveryStream.Dispose()
 if($recoveryAssembly.EntryPoint.Invoke($null,$null) -ne 0){throw 'Dialogue recovery presentation regression failed'}
}
"Evidence folder: $outDir"
