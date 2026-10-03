$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outDir=Join-Path $PSScriptRoot ('map-catchup-client-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$sessionPath=Join-Path $gameRoot 'MultiplayerDev/Source/DedicatedSession.cs'
$session=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sessionPath)).GetRoot()
$names=@('BeginClientMutationFrame','ObserveMapCatchupContext','UpdateMapCatchupPresentation','ReportMapCatchupProgress','TrackAppliedRetainedMapDelivery','ResetMapCatchup','RetainMapEvent','FlushDeferredTravelEvents','ApplyMapCatchupBarrier','AcceptMapCatchupConfirmation')
$methods=@($session.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names}|ForEach-Object ToFullString)
if($methods.Count -ne $names.Count){throw 'Production method extraction mismatch.'}
$property=@($session.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsNativeMapCatchup'}|ForEach-Object ToFullString)
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MapCatchupClientTests.cs')).Replace('/*PRODUCTION*/',($methods+$property -join "`n"))
$generated=Join-Path $outDir 'MapCatchupClientTests.generated.cs';[IO.File]::WriteAllText($generated,$fixture)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('MapCatchupClientTests').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@($generated)
foreach($source in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$path=Join-Path $outDir 'MapCatchupClientTests.dll';$stream=[IO.File]::Create($path);try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Map catch-up client extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'MapCatchupClientTests.runtimeconfig.json')
& dotnet $path
if($LASTEXITCODE -ne 0){throw 'Map catch-up client regression failed.'}
Get-FileHash -LiteralPath $sessionPath -Algorithm SHA256|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
"Evidence: $outDir"
