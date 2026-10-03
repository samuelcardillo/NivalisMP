$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/BusinessStateReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath)).GetRoot()
$classes=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]})
$owner=@($classes|Where-Object{$_.Identifier.ValueText -eq 'BusinessStateReplication'})[0]
$methods=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]})
$names=@('Reset','ValidateWire','ValidateContainerWire','Apply','ApplyProfiled','AcceptChunk','PrepareManaged')
$production=@($methods|Where-Object{$_.Identifier.ValueText -in $names}|ForEach-Object ToFullString)-join "`n"
$nested=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('ApplyProfile','EncodedBusinessUpdate','PreparedBusinessUpdate')}|ForEach-Object ToFullString)-join "`n"
# This fixture covers detached managed preparation and synchronous compatibility.
# Incremental native units are exercised separately by Test-BusinessIncremental.
foreach($member in $owner.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('Advance','Abort')}){$nested=$nested.Replace($member.ToFullString(),'')}
$nested=$nested.Replace('PreparedBusinessUpdate:IIncrementalPreparedWorldUpdate','PreparedBusinessUpdate:IPreparedWorldUpdate').Replace('private BusinessClientCursor cursor;','private object cursor;')
$fields=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax] -and ($_.ToString() -match '^private (const int MaxVenues|static (long sequence|byte\[\] incoming|int incomingOffset|int preparationGeneration|float incomingStarted|string incomingHash|long nextProfileReport))')}|ForEach-Object ToFullString)-join "`n"
$properties=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -in @('IsApplying','LastRefreshError','LastCaptureHash')}|ForEach-Object ToFullString)-join "`n"
$dtos=@($classes|Where-Object{$_.Parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax] -and $_.Identifier.ValueText -like 'Business*' -and $_.Identifier.ValueText -ne 'BusinessStateReplication'}|ForEach-Object ToFullString)-join "`n"
$economy=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/SharedEconomyReplication.cs'))).GetRoot()
$dtos+=($economy.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'SharedItemStack'}).ToFullString()
$apply=@($methods|Where-Object{$_.Identifier.ValueText -eq 'ApplyState'})[0]
$prefix=''
foreach($statement in $apply.Body.Statements){if($statement.ToString().StartsWith('var manager=')){break};$prefix+=$statement.ToFullString()}
if(!$prefix.Contains('if(!profile.WireValidated)') -or !$prefix.Contains('state.Sequence<=applied')){throw 'Native commit validation prefix changed; inspect extraction.'}
$validate=@($methods|Where-Object{$_.Identifier.ValueText -eq 'ValidateWire'})[0]
$instrumented=$validate.ToFullString().Replace($validate.Body.OpenBraceToken.ToFullString()+$validate.Body.Statements[0].ToFullString(),$validate.Body.OpenBraceToken.ToFullString()+"ValidationCalls++;OnValidate?.Invoke();`n"+$validate.Body.Statements[0].ToFullString())
$production=$production.Replace($validate.ToFullString(),$instrumented)
$generated=@'
using System;using System.IO;using System.Collections.Generic;using System.Text.Json;using System.Threading;using System.Diagnostics;
namespace NivalisTogether.Dedicated {
internal static class Time {
 internal static int OwnerThread;internal static float Now;
 internal static float realtimeSinceStartup {get{if(Environment.CurrentManagedThreadId!=OwnerThread)throw new Exception("Unity time accessed off thread");return Now;}}
}
internal sealed class ItemContainer{}
/*DTO*/
public static partial class BusinessStateReplication {
/*FIELDS*/
/*PROPERTIES*/
private sealed class ContainerProbe{}
private static readonly Dictionary<string,object> TrackedPeople=new();
internal static int NativeCommits,ValidationCalls,CommitThread,Reports;
internal static bool ThrowNative,ResetDuringNative,LastBackground;
internal static long LastDecode,LastPreparation,LastValidation;
internal static Action OnValidate;
internal static BusinessState LastState;
/*NESTED*/
/*METHODS*/
private static void ApplyState(BusinessState state,ApplyProfile profile){
/*NATIVE_PREFIX*/
 if(Environment.CurrentManagedThreadId!=Time.OwnerThread)throw new Exception("Native commit off owning thread");
 NativeCommits++;CommitThread=Environment.CurrentManagedThreadId;LastState=state;
 if(ThrowNative)throw new InvalidOperationException("Injected native commit failure");
 applied=state.Sequence;if(ResetDuringNative)Reset();
}
private static void ReportProfile(ApplyProfile profile){
 if(Environment.CurrentManagedThreadId!=Time.OwnerThread)throw new Exception("Diagnostic off owning thread");
 Reports++;LastBackground=profile.BackgroundPrepared;LastDecode=profile.DecodeJson;LastPreparation=profile.ManagedPreparation;LastValidation=profile.Validation;
}
}
}
'@
$generated=$generated.Replace('/*DTO*/',$dtos).Replace('/*FIELDS*/',$fields).Replace('/*PROPERTIES*/',$properties).Replace('/*NESTED*/',$nested).Replace('/*METHODS*/',$production).Replace('/*NATIVE_PREFIX*/',$prefix)
$outDir=Join-Path $PSScriptRoot ('business-preparation-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$generatedPath=Join-Path $outDir 'BusinessPreparation.generated.cs';[IO.File]::WriteAllText($generatedPath,$generated)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BusinessPreparationProbe').WithOptions($options).AddReferences($refs.ToArray())
$paths=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@((Join-Path $gameRoot 'MultiplayerDev/Source/PreparedWorldUpdate.cs'),$generatedPath,(Join-Path $PSScriptRoot 'BusinessPreparationTests.cs'))
foreach($path in $paths){$comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path)))}
$output=Join-Path $outDir 'BusinessPreparationProbe.dll';$stream=[IO.File]::Create($output);try{$result=$comp.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Business preparation production extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'BusinessPreparationProbe.runtimeconfig.json')
Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
& dotnet $output
if($LASTEXITCODE -ne 0){throw 'Business preparation regression failed.'}
"Evidence: $outDir"
