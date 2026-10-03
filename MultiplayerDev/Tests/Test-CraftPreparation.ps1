$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/BusinessCraftStateReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath)).GetRoot()
$classes=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]})
$owner=@($classes|Where-Object{$_.Identifier.ValueText -eq 'BusinessCraftStateReplication'})[0]
$methods=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]})
$names=@('Reset','Validate','Text','OptionalId','Floats','BoundedVector','ValidateSpot','Apply','ApplyProfiled','AcceptChunk','PrepareManaged')
$production=@($methods|Where-Object{$_.Identifier.ValueText -in $names}|ForEach-Object ToFullString)-join "`n"
$nested=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('CraftProfile','EncodedCraftUpdate','PreparedCraftUpdate')}|ForEach-Object ToFullString)-join "`n"
$fields=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax] -and ($_.ToString() -match '^private (const int MaxMeals|static (bool patched|long sequence|byte\[\] incoming|int incomingOffset|int preparationGeneration|float incomingStarted|string incomingHash|long nextProfileReport))')}|ForEach-Object ToFullString)-join "`n"
$properties=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -in @('IsApplying','Faulted','LastCaptureHash')}|ForEach-Object ToFullString)-join "`n"
$dtos=@($classes|Where-Object{$_.Parent -is [Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax] -and $_.Identifier.ValueText -like 'Business*' -and $_.Identifier.ValueText -ne 'BusinessCraftStateReplication'}|ForEach-Object ToFullString)-join "`n"
$cursor=@($classes|Where-Object{$_.Identifier.ValueText -eq 'CraftApplyCursor'})[0].ToFullString()
$apply=@($methods|Where-Object{$_.Identifier.ValueText -eq 'ApplyState'})[0]
$prefix=@($apply.Body.Statements|Select-Object -First 5|ForEach-Object ToFullString)-join "`n"
if(!$prefix.Contains('if(!validated)') -or !$prefix.Contains('state.Sequence<=applied')){throw 'Native commit validation prefix changed; inspect extraction.'}
$authority=@($methods|Where-Object{$_.Identifier.ValueText -eq 'SetClientAuthority'})[0]
$authorityGuard=@($authority.Body.Statements|Where-Object{$_.ToString().StartsWith('if(clientAuthority!=enabled)')})
if($authorityGuard.Count -ne 1){throw 'Authority epoch invalidation missing.'}
$generated=@'
using System;using System.IO;using System.Collections.Generic;using System.Text.Json;using System.Threading;
using Stopwatch=System.Diagnostics.Stopwatch;
namespace NivalisTogether.Dedicated {
internal static class Time {
 internal static int OwnerThread;internal static float Now;
 internal static float realtimeSinceStartup {get {if(Environment.CurrentManagedThreadId!=OwnerThread)throw new Exception("Unity time accessed off thread");return Now;}}
}
/*DTO*/
/*CURSOR*/
public static partial class BusinessCraftStateReplication {
/*FIELDS*/
/*PROPERTIES*/
private static readonly Dictionary<string,object> Availability=new(),CreatedProcessors=new();
private static readonly List<object> MealGhosts=new();
internal static int NativeCommits,ValidationCalls,CommitThread;
internal static bool ThrowNative,ResetDuringNative;
internal static BusinessCraftState LastState;
internal static CraftProfile LastProfile;
internal static int IncrementalUnits,IncrementalAborts,FailIncrementalPhase=-1;
private sealed class ClientCraftApply {
 private readonly CraftApplyCursor cursor=new();private readonly BusinessCraftState state;private readonly CraftProfile profile;
 internal ClientCraftApply(BusinessCraftState state,CraftProfile profile,int generation,Dictionary<string,BusinessProcessorState> availability,HashSet<string> mealIds){this.state=state;this.profile=profile;}
 internal bool Advance(ClientMutationBudget budget)=>cursor.Advance(()=>budget.TryStart(Stopwatch.GetTimestamp()),(phase,index)=>{
  IncrementalUnits++;if(phase==FailIncrementalPhase)throw new InvalidOperationException("Injected unit failure");
  if(phase==7)ApplyState(state,profile,true);return true;
 });
 internal void Abort(){IncrementalAborts++;cursor.Abort();}
}
/*NESTED*/
/*METHODS*/
internal static void SetAuthority(bool enabled){/*AUTHORITY*/ clientAuthority=enabled;}
private static void ApplyState(BusinessCraftState state,CraftProfile profile,bool validated=false){
/*NATIVE_PREFIX*/
 if(Environment.CurrentManagedThreadId!=Time.OwnerThread)throw new Exception("Native commit off owning thread");
 NativeCommits++;CommitThread=Environment.CurrentManagedThreadId;LastState=state;
 if(ThrowNative)throw new InvalidOperationException("Injected native commit failure");
 applied=state.Sequence;if(ResetDuringNative)Reset();
}
private static void ReportProfile(BusinessCraftState state,CraftProfile profile){LastProfile=profile;}
}
}
'@
# Instrument only the actual pure validator's entry; all validation branches and callers are production source.
$validate=@($methods|Where-Object{$_.Identifier.ValueText -eq 'Validate'})[0]
$instrumented=$validate.ToFullString().Replace($validate.Body.OpenBraceToken.ToFullString()+$validate.Body.Statements[0].ToFullString(),$validate.Body.OpenBraceToken.ToFullString()+"ValidationCalls++;`n"+$validate.Body.Statements[0].ToFullString())
$production=$production.Replace($validate.ToFullString(),$instrumented)
$generated=$generated.Replace('/*DTO*/',$dtos).Replace('/*CURSOR*/',$cursor).Replace('/*FIELDS*/',$fields).Replace('/*PROPERTIES*/',$properties).Replace('/*NESTED*/',$nested).Replace('/*METHODS*/',$production).Replace('/*NATIVE_PREFIX*/',$prefix).Replace('/*AUTHORITY*/',$authorityGuard[0].ToFullString())
$outDir=Join-Path $PSScriptRoot ('craft-preparation-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$generatedPath=Join-Path $outDir 'CraftPreparation.generated.cs';[IO.File]::WriteAllText($generatedPath,$generated)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CraftPreparationProbe').WithOptions($options).AddReferences($refs.ToArray())
$paths=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)+@((Join-Path $gameRoot 'MultiplayerDev/Source/PreparedWorldUpdate.cs'),$generatedPath,(Join-Path $PSScriptRoot 'CraftPreparationTests.cs'))
foreach($path in $paths){$comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path)))}
$output=Join-Path $outDir 'CraftPreparationProbe.dll';$stream=[IO.File]::Create($output);try{$result=$comp.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Craft preparation production extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'CraftPreparationProbe.runtimeconfig.json')
Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
& dotnet $output
if($LASTEXITCODE -ne 0){throw 'Craft preparation regression failed.'}
"Evidence: $outDir"
