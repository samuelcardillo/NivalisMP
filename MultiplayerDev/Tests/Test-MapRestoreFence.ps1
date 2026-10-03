param([switch]$OriginalGuardControl)
$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outDir=Join-Path $PSScriptRoot ('map-restore-fence-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$sessionPath=Join-Path $gameRoot 'MultiplayerDev/Source/DedicatedSession.cs'
$session=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sessionPath)).GetRoot()
$methods=@($session.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]})
$tick=@($methods|Where-Object{$_.Identifier.ValueText -eq 'TickClient'})[0]
$completion=@($tick.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax] -and $_.Condition.ToString().Contains('BoatReplication.TryRestorePlayerAttachment()')})
if($completion.Count -ne 1){throw 'Exact production map completion branch not found.'}
$selected=@('PumpEconomyPreparation','DispatchWorldEvent','QueueBudgetDeferredEvent','FlushDeferredTravelEvents','RetainMapEvent','CancelPreparedWorldUpdate')
$productionMethods=@(foreach($name in $selected){$found=@($methods|Where-Object{$_.Identifier.ValueText -eq $name});if($found.Count -ne 1){throw "Exact production method not found: $name"};$found[0].ToFullString()}) -join "`n"
$economyPath=Join-Path $gameRoot 'MultiplayerDev/Source/SharedEconomyReplication.cs'
$economy=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($economyPath)).GetRoot()
$fence=@($economy.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'BoundedPreparationFence'})[0].ToFullString()
$completionCode=$completion[0].ToFullString()
if($OriginalGuardControl){
 if(!$completionCode.Contains(' && !economyPreparation.IsFenced')){throw 'Corrected production guard missing; cannot construct old-guard control.'}
 $completionCode=$completionCode.Replace(' && !economyPreparation.IsFenced','')
}
$probe=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MapRestoreFenceProbe.cs')).Replace('/*PRODUCTION_COMPLETION*/',$completionCode).Replace('/*PRODUCTION_METHODS*/',$productionMethods).Replace('/*PRODUCTION_FENCE*/',$fence)
$generated=Join-Path $outDir 'MapRestoreFenceProbe.generated.cs';[IO.File]::WriteAllText($generated,$probe)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('MapRestoreFenceProbe').WithOptions($options).AddReferences($refs.ToArray())
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@((Join-Path $gameRoot 'MultiplayerDev/Source/PreparedWorldUpdate.cs'),$generated)
foreach($source in $sources){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$null,$source)))}
$path=Join-Path $outDir 'MapRestoreFenceProbe.dll';$stream=[IO.File]::Create($path);try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Map restore production extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'MapRestoreFenceProbe.runtimeconfig.json')
Get-FileHash -LiteralPath $sessionPath,$economyPath -Algorithm SHA256|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
if($OriginalGuardControl){
 $controlOutput=& dotnet $path 2>&1
 $controlOutput|Set-Content -LiteralPath (Join-Path $outDir 'original-guard-control.log')
 if($LASTEXITCODE -eq 0 -or !($controlOutput -match 'Asynchronous restore ACK crossed the preparation fence.')){throw 'Original-guard control did not reproduce the expected integration failure.'}
 'PASS original-guard control: the prior production branch reproduces the fenced asynchronous ACK failure.'
}else{
 & dotnet $path
 if($LASTEXITCODE -ne 0){throw 'Map restore fence regression failed.'}
}
"Evidence: $outDir"
