$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/DedicatedWorldRuntime.cs'
$source=[IO.File]::ReadAllText($sourcePath)
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
$names=@('IsLoadedGameplayScene','TryReleaseClientIsolationAtTitle','WaitForClientTitle','IsSceneLoadIdle','Probe','ClientTitleWaitReason','IsClientTitleReady','LoadSnapshot','TryBeginClientUnload')
$methods=@($tree.GetRoot().DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
if($methods.Count -ne $names.Count){throw 'Expected production isolation methods were not uniquely found'}
$bodies=($methods | ForEach-Object ToFullString) -join "`n"
$boot=$tree.GetRoot().DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsBootReady'}|Select-Object -First 1
if(!$boot){throw 'Production boot-readiness property not found'}
$bodies+="`n"+$boot.ToFullString()
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ClientIsolationReleaseTests.cs'))
$sessionTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/DedicatedSession.cs')))
$join=$sessionTree.GetRoot().DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'Join'}|Select-Object -First 1
if(!$join){throw 'Production Join method not found'}
$fixture=$fixture.Replace('// PRODUCTION_JOIN',$join.ToFullString())
$storageGuard=$tree.GetRoot().DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax] -and $_.Condition.ToString().StartsWith('existingGame != null && (dedicatedServer')}|Select-Object -First 1
if(!$storageGuard){throw 'Production storage-entry guard not found'}
$fixture=$fixture.Replace('// PRODUCTION_STORAGE_GUARD','internal static bool StorageEntryAllowed(bool dedicatedServer) { var existingGame = GetGame(); return !('+$storageGuard.Condition.ToFullString()+'); }')
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
function Compile-Run([string]$name,[string]$text){
 $compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)))
 $stream=[IO.MemoryStream]::new(); $result=$compilation.Emit($stream)
 if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Isolation regression memory compilation failed'}
 $assembly=[Reflection.Assembly]::Load($stream.ToArray());$stream.Dispose()
 return $assembly.EntryPoint.Invoke($null,$null)
}
$current=$fixture.Replace('// PRODUCTION_METHODS',$bodies)
if((Compile-Run 'ClientIsolationReleaseCurrent' $current) -ne 0){throw 'Production isolation regression failed'}
# Positive control: substitute ONLY the former dictionary-membership classifier.
# The complete release/load/manager fences still come from production unchanged.
$classifier=$methods | Where-Object {$_.Identifier.ValueText -eq 'IsLoadedGameplayScene'}
$legacy='private static bool IsLoadedGameplayScene(Scene scene, GameSceneManager game) { return scene.IsValid() && scene.isLoaded && game.IsGameplayScene(scene.buildIndex); }'
$legacySource=$fixture.Replace('// PRODUCTION_METHODS',$bodies.Replace($classifier.ToFullString(),$legacy))
$caught=$false
try { $null=Compile-Run 'ClientIsolationReleaseLegacyControl' $legacySource }
catch {
 if($_.Exception.ToString().Contains('runtime-only -1 scene does not retain isolation')){$caught=$true}
 else {throw}
}
if(!$caught){throw 'Regression failed to detect previous ContainsKey(-1) classifier'}
'PASS: previous ContainsKey classifier positive control fails on the runtime -1 scene.'
$title=$methods|Where-Object {$_.Identifier.ValueText -eq 'ClientTitleWaitReason'}
$stickyTitle=$title.ToFullString().Replace('if (game == null) return "scene manager unavailable";', 'if (game == null) return "scene manager unavailable"; if (game.IsGame) return "native gameplay still active";')
$caught=$false
try { $null=Compile-Run 'ClientIsolationStickyFlagControl' $fixture.Replace('// PRODUCTION_METHODS',$bodies.Replace($title.ToFullString(),$stickyTitle)) }
catch {
 if($_.Exception.ToString().Contains('sticky IsGame with fully torn-down title is ready')){$caught=$true}
 else{throw}
}
if(!$caught){throw 'Regression failed to detect sticky IsGame title gate'}
'PASS: old sticky IsGame gate positive control fails after actual teardown.'
$snapshot=$methods|Where-Object {$_.Identifier.ValueText -eq 'LoadSnapshot'}
$legacySnapshot=$snapshot.ToFullString().Replace('!IsBootReady || starting || (IsDedicatedServer ? GetGame().IsGame : !IsClientTitleReady())','!IsBootReady || GetGame().IsGame || starting')
$caught=$false
try { $null=Compile-Run 'ClientSnapshotStickyFlagControl' $fixture.Replace('// PRODUCTION_METHODS',$bodies.Replace($snapshot.ToFullString(),$legacySnapshot)) }
catch {
 if($_.Exception.ToString().Contains('Snapshot loading requires the title screen')){$caught=$true}
 else{throw}
}
if(!$caught){throw 'Regression failed to detect old snapshot-entry sticky flag'}
'PASS: old snapshot IsGame entry gate fails the real post-disconnect import path.'
'Production source SHA256: '+(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
'No game assemblies emitted, no native process launched, no save paths changed.'
