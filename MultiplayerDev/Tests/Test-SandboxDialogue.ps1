$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$path=Join-Path $root 'MultiplayerDev/Source/DialogueReplication.cs'
$source=[IO.File]::ReadAllText($path)
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
$names=@('ApplySandboxPolicy','CloseSandboxPanel','ClearPresentationState','WorkerOnly','NarrativeWorkerOnly','SandboxNarrativePrefix','SandboxGetDialogueStart','WorkerDialogueStarting','ClientUiEvent','ChoiceAuthority','ReportChoiceAuthority','ResumePrefix')
$methods=@($tree.GetRoot().DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
if($methods.Count -ne $names.Count){throw 'Expected unique actual sandbox methods'}
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SandboxDialogueTests.cs')).Replace('// ACTUAL_METHODS',(($methods|ForEach-Object ToFullString)-join "`n"))
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem (Join-Path $root 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SandboxDialogueRegression').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($fixture)))
$stream=[IO.MemoryStream]::new()
try{
 $result=$compilation.Emit($stream)
 if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Sandbox dialogue test compilation failed'}
 $assembly=[Reflection.Assembly]::Load($stream.ToArray())
 if($assembly.EntryPoint.Invoke($null,$null) -ne 0){throw 'Sandbox dialogue checks failed'}
}finally{$stream.Dispose()}
$install=($tree.GetRoot().DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'InstallPatches'}).ToFullString()
foreach($unsafe in @('"OnDialogueRequest"','"ProcessDialogueRequestNow"','"OnDialogueEnd"','"DialogueUIOnEOnChoiceMade"','"DialogueUIOnEOnNewPageShown"')){
 if($install.Contains($unsafe)){throw ('Unsafe native value-wrapper hook still registered: '+$unsafe)}
}
'PASS: five unsafe native-value callback registration groups absent.'
'Production SHA256: '+(Get-FileHash $path -Algorithm SHA256).Hash
