param([switch]$LegacyLookupOnly)
$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/DialogueReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath)).GetRoot()
$typeNames=@('SharedDialoguePerson','DialoguePeopleState','DialoguePeopleCursor')
$types=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in $typeNames})
$methodNames=@('AcceptPeople','ApplyPeople','ApplyPerson','PeopleWorldCurrent','ResolveDisplayPerson')
$methods=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $methodNames})
if($types.Count-ne$typeNames.Count-or$methods.Count-ne$methodNames.Count){throw 'Production relationship slicing boundaries are missing.'}
foreach($name in @('Reset','SetClientAuthority')){
 $method=$tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq$name-and$_.Parent.Identifier.ValueText-eq'DialogueReplication'}
 if(!$method.ToFullString().Contains('PeopleCursor.Reset()')-or!$method.ToFullString().Contains('TargetPersonSequences.Clear()')){throw "$name must invalidate relationship cursor and target sequence state."}
}
$tick=$tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq'Tick'}
if(@($tick.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax]-and$_.Expression.ToString()-eq'ApplyPeople'}).Count-ne2){throw 'Both sandbox and normal client ticks must advance relationship slices.'}
$code=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DialoguePeopleSlicesTests.cs')).Replace('/*PRODUCTION_TYPES*/',(($types|ForEach-Object ToFullString)-join "`n")).Replace('/*PRODUCTION_METHODS*/',(($methods|ForEach-Object ToFullString)-join "`n"))
$outDir=Join-Path $PSScriptRoot ('dialogue-people-slices-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$generated=Join-Path $outDir 'DialoguePeopleSlices.generated.cs';[IO.File]::WriteAllText($generated,$code)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$entry=if($LegacyLookupOnly){'NivalisTogether.Dedicated.LegacyDialoguePersonLookupProbe'}else{'NivalisTogether.Dedicated.DialoguePeopleSlicesTests'}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithMainTypeName($entry)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('DialoguePeopleSlicesTests').WithOptions($options).AddReferences($refs.ToArray())
foreach($path in @($generated,(Join-Path $gameRoot 'MultiplayerDev/Shared/ClientMutationBudget.cs'))){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path)))}
$output=Join-Path $outDir 'DialoguePeopleSlicesTests.dll';$stream=[IO.File]::Create($output);try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Dialogue slicing production extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'DialoguePeopleSlicesTests.runtimeconfig.json')
& dotnet $output
if($LASTEXITCODE-ne0){throw 'Dialogue slicing regression failed.'}
'Production SHA256: '+(Get-FileHash $sourcePath -Algorithm SHA256).Hash
"Evidence: $outDir"
