$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/MultiplayerMenu.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath)).GetRoot()
$registry=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-eq'NativeMainMenuRegistry'})[0]
$menu=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-eq'MultiplayerMenu'})[0]
if(!$registry-or!$menu){throw 'Production menu registry or owner missing.'}
if($menu.ToFullString().Contains('FindObjectsOfType<MainMenuUI>')){throw 'Menu presentation must not scan world menus.'}
foreach($name in @('Tick','UpdateSessionMenus','RefreshPersonalSaveButtons')){
 $method=$menu.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq$name}
 if(!$method.ToFullString().Contains('NativeMainMenuRegistry.Snapshot()')){throw "$name must use known menu membership."}
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll'))
$interop=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
try{
 $native=$interop.MainModule.Types|Where-Object FullName -eq 'Nivalis.MainMenuUI'
 foreach($name in @('Start','Show','OnDestroy')){
  $matches=@($native.Methods|Where-Object{$_.Name-eq$name-and!$_.IsStatic-and$_.Parameters.Count-eq0-and$_.ReturnType.FullName-eq'System.Void'})
  if($matches.Count-ne1){throw "Native MainMenuUI.$name no-argument lifecycle ABI changed."}
 }
}finally{$interop.Dispose()}
'PASS actual native menu lifecycle ABI and all three discovery consumers use the registry.'
# Only the external IL2CPP delegate adapter becomes a managed Action facade;
# every registry method, scan condition and native hook selection is production.
$body=$registry.ToFullString().Replace('UnityAction<Scene, LoadSceneMode>','Action<Scene, LoadSceneMode>').Replace('UnityAction<Scene>','Action<Scene>')
$code=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NativeMainMenuRegistryTests.cs')).Replace('/*PRODUCTION_REGISTRY*/',$body)
$outDir=Join-Path $PSScriptRoot ('native-menu-registry-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$generated=Join-Path $outDir 'NativeMainMenuRegistry.generated.cs';[IO.File]::WriteAllText($generated,$code)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('NativeMainMenuRegistryTests').WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))
$output=Join-Path $outDir 'NativeMainMenuRegistryTests.dll';$stream=[IO.File]::Create($output);try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Native menu registry extracted compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'NativeMainMenuRegistryTests.runtimeconfig.json')
& dotnet $output
if($LASTEXITCODE-ne0){throw 'Native menu registry regression failed.'}
'Production SHA256: '+(Get-FileHash $sourcePath -Algorithm SHA256).Hash
"Evidence: $outDir"
