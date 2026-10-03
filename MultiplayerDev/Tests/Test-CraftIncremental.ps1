$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/BusinessCraftStateReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath)).GetRoot()
$classes=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]})
$cursor=@($classes|Where-Object{$_.Identifier.ValueText -eq 'CraftApplyCursor'})[0].ToFullString()
$owner=@($classes|Where-Object{$_.Identifier.ValueText -eq 'BusinessCraftStateReplication'})[0]
$runtime=@($classes|Where-Object{$_.Identifier.ValueText -eq 'ClientCraftApply'})[0]
$commit=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'CommitPrepared'})[0].ToFullString()
$unit=@($runtime.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('CommitUnit','Claim','RemoveCurrent','RequireIdentity')}|ForEach-Object ToFullString)-join "`n"
$code=@'
using System;using System.Collections.Generic;using System.Linq;
namespace NivalisTogether.Dedicated {
/*CURSOR*/
internal sealed class Ghost {internal IntPtr Pointer;internal string Id;internal bool IsValid=true,_IsActive_k__BackingField=true;}
internal sealed class NativeManager {
 internal Ghost Current;internal Action Updated;internal int Removes,Lookups;
 internal Ghost GetGhostById(string id){Lookups++;return Current?.Id==id?Current:null;}
 internal void UpdateGhostViewPresence(Ghost ghost){Updated?.Invoke();}
 internal void DeregisterGhostImmediate(Ghost ghost){Removes++;Current=null;}
}
internal sealed class CraftUnitProbe {
 private bool mutated;private static bool Faulted;private bool current=true;
 private readonly Dictionary<IntPtr,string> identities=new();
 private readonly NativeManager manager=new();
 private bool SameWorld()=>current;
 private void CheckLive(){if(!current)throw new InvalidOperationException("World changed");}
 /*COMMIT*/
 /*UNIT*/
 internal static int Run(){
  int count=0;void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
  var probe=new CraftUnitProbe();int native=10,registry=0,undos=0;
  probe.CommitUnit(new(),new(),new(){(()=>native=20,()=>native=10),(()=>{if(native!=20)throw new Exception();},()=>{})});
  Check(native==20&&probe.mutated,"A complete verified entity commits synchronously");
  bool failed=false;try{probe.CommitUnit(new(){()=>registry++},new(){()=>registry--},new(){(()=>native=30,()=>{undos++;native=20;}),(()=>throw new InvalidOperationException("Native readback mismatch"),()=>{})});}catch(InvalidOperationException){failed=true;}
  Check(failed&&Faulted&&native==20&&registry==0&&undos==1,"Failed entity verification rolls back only its own complete mutation/registration and preserves prior units");
  Check(probe.mutated,"Failure cannot claim an untouched client mirror");
  probe=new CraftUnitProbe();native=20;undos=0;failed=false;Faulted=false;
  try{probe.CommitUnit(new(),new(),new(){(()=>native=30,()=>{native=20;undos++;}),(()=>{native=100;probe.current=false;throw new InvalidOperationException("Reset callback");},()=>{})});}catch(InvalidOperationException){failed=true;}
  Check(failed&&native==100&&undos==0&&!Faulted,"Rollback and failure flags never write into a replacement world after a throwing native callback");
  probe=new CraftUnitProbe();native=20;undos=0;failed=false;Faulted=false;int later=0;
  try{probe.CommitUnit(new(),new(),new(){(()=>{native=100;probe.current=false;},()=>undos++),(()=>later++,()=>undos++)});}catch(InvalidOperationException){failed=true;}
  Check(failed&&native==100&&undos==0&&later==0&&!Faulted,"A reset callback returning normally stops later apply delegates and preserves the replacement fault flag");
  probe=new CraftUnitProbe();failed=false;Faulted=false;later=0;undos=0;
  try{probe.CommitUnit(new(){()=>probe.current=false,()=>later++},new(){()=>undos++,()=>undos++},new(){(()=>later++,()=>undos++)});}catch(InvalidOperationException){failed=true;}
  Check(failed&&later==0&&undos==0&&!Faulted,"A reset during registration stops later registrations and all updates without poisoning new world");
  probe=new CraftUnitProbe();var oldGhost=new Ghost{Pointer=new IntPtr(9),Id="old"};probe.manager.Current=oldGhost;
  probe.manager.Updated=()=>probe.current=false;failed=false;
  try{probe.RemoveCurrent(oldGhost,"old");}catch(InvalidOperationException){failed=true;}
  Check(failed&&probe.manager.Removes==0&&probe.manager.Lookups==1,"View callback reset stops before any subsequent old-world lookup or deregistration");
  probe=new CraftUnitProbe();probe.Claim("meal",new Ghost{Pointer=new IntPtr(1)});probe.Claim("meal",new Ghost{Pointer=new IntPtr(1)});
  failed=false;try{probe.Claim("processor",new Ghost{Pointer=new IntPtr(1)});}catch(InvalidOperationException){failed=true;}
  Check(failed,"Different authoritative entity identities cannot alias one native pointer");
  failed=false;try{probe.Claim("absent",null);}catch(InvalidOperationException){failed=true;}Check(failed,"Missing native entity fails before publication");
  return count;
 }
}
internal static class CraftIncrementalProbe {
 public static int Main(){try{
  int checks=CraftUnitProbe.Run();void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
  var cursor=new CraftApplyCursor();var budget=new ClientMutationBudget(1000,4,32);
  var counts=new[]{1,3,2,2,1,2,3,1};var completed=new int[8];var trace=new List<string>();
  long now=0;int frame=0;bool published=false,done=false;
  while(!done){
   budget.BeginFrame(++frame);now=frame*100;
   done=cursor.Advance(()=>budget.TryStart(now),(phase,index)=>{
    if(phase>0)Check(completed[phase-1]==counts[phase-1],"Dependent phase never starts before all prior units complete");
    Check(index==completed[phase],"Yield resumes at exact next entity without repeat/skip");
    trace.Add(phase+":"+index);completed[phase]++;now+=6; // One indivisible slow native entity exhausts this frame.
    if(phase==7)published=true;
    return completed[phase]==counts[phase];
   });
   Check(budget.Actions==1,"One slow native unit prevents another unit in the same frame");
   Check(published==done,"Sequence/hash publication is reached only in the final completion phase");
   Check(frame<100,"Bounded complete entity cursor makes forward progress");
  }
  Check(trace.Count==counts.Sum()&&completed.SequenceEqual(counts),"Every phase/entity executes once across frame boundaries");
  int calls=0;cursor=new CraftApplyCursor();budget=new ClientMutationBudget(1000,100,2);budget.BeginFrame(1);
  Check(!cursor.Advance(()=>budget.TryStart(0),(phase,index)=>{calls++;return true;})&&calls==2,"Shared maximum action cap applies to cheap native units");
  Check(!cursor.Advance(()=>budget.TryStart(0),(phase,index)=>{calls++;return true;})&&calls==2,"Repeated same-frame advancement cannot reset shared budget");
  cursor.Abort();bool failed=false;try{cursor.Advance(()=>true,(phase,index)=>true);}catch(InvalidOperationException){failed=true;}Check(failed,"Aborted cursor never resumes or publishes");
  cursor=new CraftApplyCursor();calls=0;failed=false;
  try{cursor.Advance(()=>true,(phase,index)=>{calls++;throw new InvalidOperationException("Entity fault");});}catch(InvalidOperationException){failed=true;}
  Check(failed&&calls==1,"Failed atomic entity does not advance to another entity or cleanup/publication");
  Console.WriteLine("PASS CraftIncrementalProbe: "+checks+" assertions; production phase cursor, shared budget, entity rollback/readback failure and native identity alias guards.");return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
}
'@
$code=$code.Replace('/*CURSOR*/',$cursor).Replace('/*COMMIT*/',$commit).Replace('/*UNIT*/',$unit)
$outDir=Join-Path $PSScriptRoot ('craft-incremental-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$generated=Join-Path $outDir 'CraftIncremental.generated.cs';[IO.File]::WriteAllText($generated,$code)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CraftIncrementalProbe').WithOptions($options).AddReferences($refs.ToArray())
foreach($path in @($generated,(Join-Path $gameRoot 'MultiplayerDev/Shared/ClientMutationBudget.cs'))){$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path),$null,$path)))}
$output=Join-Path $outDir 'CraftIncrementalProbe.dll';$stream=[IO.File]::Create($output);try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Craft incremental extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'CraftIncrementalProbe.runtimeconfig.json')
Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
& dotnet $output
if($LASTEXITCODE -ne 0){throw 'Craft incremental test failed.'}
"Evidence: $outDir"
