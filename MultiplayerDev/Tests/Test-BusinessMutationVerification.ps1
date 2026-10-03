$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/BusinessStateReplication.cs'))).GetRoot()
$methods=@($tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('ApplyState','StageContainer','VerifyChangedInventories') })
if($methods.Count -ne 3) { throw 'Exact production apply/staging/verification methods are required.' }
$profile=$tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ApplyProfile' }
$prefix=@'
using System;using System.Collections.Generic;using System.Linq;using System.Diagnostics;
public static class BusinessMutationProbe {
 const int MaxVenues=2048,MaxPeople=20000;
 static long applied,nextProfileReport;
 static bool IsApplying;
 static string LastRefreshError;
 static Action RefreshUi;
 static Action<string> Diagnostic;
 sealed class ContainerProbe{}
 static readonly Manager manager=new Manager();
 static int captures,verifications;
 static Manager Ready()=>manager;
 static void ValidateWire(BusinessState state){}
 static BusinessVenueState CaptureVenue(VenueAreaGhost ghost,BusinessVenueState expected){captures+=3;return new BusinessVenueState{Fridge=Observe(ghost.Fridge),Cupboard=Observe(ghost.Cupboard),Furniture=Observe(ghost.Furniture)};}
 static BusinessContainerState Observe(ItemContainer c)=>new BusinessContainerState{Items=(SharedItemStack[])c._items};
 static void PrepareVenue(VenueAreaGhost ghost,BusinessVenueState row,BusinessVenueState observed,List<(Action Apply,Action Undo)> updates,ApplyProfile profile){
  StageContainer(ghost.Fridge,row.Fridge,observed.Fridge,updates,profile,row.VenueId,"fridge",null,null);
  StageContainer(ghost.Cupboard,row.Cupboard,observed.Cupboard,updates,profile,row.VenueId,"cupboard",null,null);
  StageContainer(ghost.Furniture,row.Furniture,observed.Furniture,updates,profile,row.VenueId,"furniture",null,null);
 }
 static Person ResolvePerson(string id)=>new Person();
 static void StagePerson(BusinessPersonState row,List<(Action Apply,Action Undo)> updates,ApplyProfile profile){}
 static object ResolveVenue(string id)=>new object();
 static BusinessPersonState CapturePerson(Person p)=>new BusinessPersonState();
 static bool SamePerson(BusinessPersonState a,BusinessPersonState b)=>true;
 static void SetPersonRaw(RuntimeData data,BusinessPersonState state,object work){}
 static void ObserveDifferencePhase(ApplyProfile profile,bool afterUi){}
 static bool SameContainer(BusinessContainerState a,BusinessContainerState b)=>Value(a)==Value(b);
 static int Value(BusinessContainerState state)=>state.Items[0].Items[0][0];
 static void RecordDifference(ApplyProfile p,ItemContainer t,BusinessContainerState r,BusinessContainerState o,string v,string k,int? a,int? b){}
 static ItemContainer PrepareContainer(BusinessContainerState state)=>new ItemContainer{_items=state.Items};
 static void AssertInventory(BusinessContainerState expected,ItemContainer actual){verifications++;if(Value(expected)!=Value(Observe(actual)))throw new InvalidOperationException("Native inventory mismatch");}
 static BusinessContainerState State(int value)=>new BusinessContainerState{Items=new[]{new SharedItemStack{Items=new[]{new[]{value}}}}};
 static BusinessState Setup(int count){
  manager.Ghosts.Clear();captures=verifications=0;applied=nextProfileReport=0;IsApplying=false;
  var rows=new List<BusinessVenueState>();
  for(int i=0;i<count;i++){
   string id="v"+i;var ghost=new VenueAreaGhost{Venue=new Venue{Guid=id},Fridge=PrepareContainer(State(1)),Cupboard=PrepareContainer(State(1)),Furniture=PrepareContainer(State(1))};
   manager.Ghosts[id]=ghost;rows.Add(new BusinessVenueState{GhostId=id,VenueId=id,Fridge=State(1),Cupboard=State(1),Furniture=State(1)});
  }
  return new BusinessState{Sequence=1,Venues=rows.ToArray()};
 }
 public static int Run(){
  int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
  var state=Setup(83);state.Venues[0].Fridge=State(2);
  ApplyState(state,new ApplyProfile());
  Check(captures==249,"Every native container is still observed before applying changes");
  Check(verifications==1,"Only one changed native container needs a second inventory read");
  Check(applied==1&&!IsApplying,"Successful commit advances sequence and releases applying flag");
  captures=verifications=0;state.Sequence++;ApplyState(state,new ApplyProfile());
  Check(captures==249&&verifications==0,"An unchanged snapshot still checks local drift without a second full read");
  var target=manager.Ghosts["v0"].Fridge;target._items=State(99).Items;
  state.Sequence++;captures=verifications=0;ApplyState(state,new ApplyProfile());
  Check(captures==249&&verifications==1&&Value(Observe(target))==2,"Same incoming inventory repairs newly introduced native drift");
  state=Setup(2);var shared=manager.Ghosts["v0"].Fridge;manager.Ghosts["v1"].Fridge=shared;
  state.Venues[0].Fridge=State(3);
  bool failed=false;try{ApplyState(state,new ApplyProfile());}catch(InvalidOperationException){failed=true;}
  Check(failed&&verifications==2,"Every alias of a changed pointer is verified, including a previously equal row");
  Check(Value(Observe(shared))==1&&applied==0&&!IsApplying,"Conflicting alias postcondition failure rolls back exact old inventory and sequence");
  state=Setup(1);target=manager.Ghosts["v0"].Fridge;target.FailWriteOnce=true;state.Venues[0].Fridge=State(4);
  failed=false;try{ApplyState(state,new ApplyProfile());}catch(InvalidOperationException){failed=true;}
  Check(failed&&verifications==1,"A rejected native write is detected by post-mutation verification");
  Check(Value(Observe(target))==1&&applied==0&&!IsApplying,"Failed native write rolls back and leaves sequence eligible for retry");
  ApplyState(state,new ApplyProfile());
  Check(applied==1&&Value(Observe(target))==4,"Same authoritative sequence can succeed after a rolled-back failure");
  return checks;
 }
'@
$boundary=@'
public class SharedItemStack{public int[][] Items;}
public class BusinessContainerState{public bool Manual,Refrigerated;public SharedItemStack[] Items;}
public class BusinessVenueState{public string GhostId,VenueId;public object[] Menu=Array.Empty<object>();public string[] Staff=Array.Empty<string>();public int[][] Profits=Array.Empty<int[]>();public int Ownership;public BusinessContainerState Fridge,Cupboard,Furniture;}
public class BusinessPersonState{public string PersonId,VenueId;public float StartHour,EndHour,LastPaidWage,WorkQuit,HappinessValue;}
public class BusinessState{public long Sequence;public BusinessVenueState[] Venues;public BusinessPersonState[] People=Array.Empty<BusinessPersonState>();}
public class RuntimeData{public object _worksAt;}
public class Person{public RuntimeData RuntimeData=new RuntimeData();}
public class Venue{public string Guid;}
public class VenueAreaGhost{public Venue Venue;public ItemContainer Fridge,Cupboard,Furniture;public T TryCast<T>() where T:class=>this as T;}
public class Manager{public readonly Dictionary<string,VenueAreaGhost> Ghosts=new();public VenueAreaGhost GetGhostById(string id)=>Ghosts[id];}
public class ItemContainer{
 static int pointer;public IntPtr Pointer=new IntPtr(++pointer);public bool FailWriteOnce;
 private object items;public object _items{get=>items;set{if(FailWriteOnce){FailWriteOnce=false;return;}items=value;}}
 public object _typeMap=new object(),_restriction=new object();public bool _IsManuallyUpdated_k__BackingField,_IsRefridgerated_k__BackingField;
}
public static class DedicatedProtocol{public static bool Text(string value,int max)=>!string.IsNullOrEmpty(value)&&value.Length<=max;}
'@
$code=$prefix+$profile.ToFullString()+($methods.ToFullString() -join "`n")+'}'+$boundary
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BusinessMutationProbe'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))
$stream=[IO.MemoryStream]::new()
try{
 $result=$comp.Emit($stream)
 if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Business mutation test compilation failed.'}
 $assembly=[Reflection.Assembly]::Load($stream.ToArray())
 $count=$assembly.GetType('BusinessMutationProbe').GetMethod('Run').Invoke($null,$null)
}finally{$stream.Dispose()}
"PASS: $count production business apply/staging/verification assertions (249-container drift observation, targeted checks, aliases, rollback and retry)."
