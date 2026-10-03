$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/BusinessCraftStateReplication.cs'))).GetRoot()
$names=@('StageOrders','OrderLookupConsistent','CaptureOrders','CaptureTypes','Ref','PrepareTypes','SameOrders','SameOrder','SamePrices','SameValues','CommitPrepared','DescribeOrdersMismatch')
$methods=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
if($methods.Count -ne $names.Count){throw 'Required production craft order observation/staging/transaction methods missing.'}
$dtos=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('BusinessOrderState','BusinessOrderPrice','BusinessDeliveredMeal','BusinessVenueOrders')})
$code=@'
using System;using System.Collections.Generic;using System.Linq;
namespace Il2CppSystem.Collections.Generic {
 public class List<T>:System.Collections.Generic.List<T>{}
 public class Dictionary<K,V>:System.Collections.Generic.Dictionary<K,V>{}
}
public class Ghost{}
public class CustomerGhost:Ghost{}
public class TableGhost:Ghost{}
public class ChairGhost:Ghost{}
public class MealGhost:Ghost{}
public static class GhostReferenceProbe{public static readonly HashSet<string> Missing=new();public static int ResolutionChecks;}
public class GhostReference<T>where T:Ghost{
 public string ReferencedId;public GhostReference(string id){ReferencedId=id;}
 // Native equality against a ghost resolves the stored identity. An absent
 // local ghost compares equal to null even though the reference preserves its ID.
 public static bool operator ==(GhostReference<T> reference,T ghost){
  GhostReferenceProbe.ResolutionChecks++;
  return ReferenceEquals(ghost,null)&&(ReferenceEquals(reference,null)||string.IsNullOrEmpty(reference.ReferencedId)||GhostReferenceProbe.Missing.Contains(reference.ReferencedId));
 }
 public static bool operator !=(GhostReference<T> reference,T ghost)=>!(reference==ghost);
 public override bool Equals(object value)=>ReferenceEquals(this,value);
 public override int GetHashCode()=>base.GetHashCode();
}
public class ItemType{public string Guid;}
public class Venue{static int id;public string Guid;public IntPtr Pointer=new IntPtr(++id);}
public class FoodDeliveredState{public ItemType Meal;public GhostReference<MealGhost> ActualMeal;public int Price;public float MealQuality,FreshQuality;}
public class Order{
 static int id;public IntPtr Pointer=new IntPtr(++id);public string Id;public bool IsValid;public Venue Venue;
 public GhostReference<CustomerGhost> ForCustomer;public GhostReference<TableGhost> ForTable;public GhostReference<ChairGhost> ForChair;
 public int ConsumedMeals;public float ServiceQuality;
 public Il2CppSystem.Collections.Generic.List<ItemType> OrderedMeals,PreparedMealTypes;
 public Il2CppSystem.Collections.Generic.Dictionary<string,int> OrderedForPrice;
 public Il2CppSystem.Collections.Generic.List<FoodDeliveredState> DeliveredMeals;
}
public class Orders{public Il2CppSystem.Collections.Generic.List<Order> _active=new();public Il2CppSystem.Collections.Generic.Dictionary<string,Order> _activeIdLookup=new();}
public class VenueAreaGhost{public string Id;public Venue Venue;public Orders Orders=new();public T TryCast<T>()where T:class=>this as T;}
public class GhostManager{public VenueAreaGhost Venue;public VenueAreaGhost GetGhostById(string id)=>Venue!=null&&Venue.Id==id?Venue:null;}
public static class NativeValueList{public static void Add<T>(Il2CppSystem.Collections.Generic.List<T> list,T value)=>list.Add(value);}
public static class CraftOrderProbe {
 static bool Faulted;
 static ItemType ResolveItem(string id)=>new ItemType{Guid=id};
 static BusinessVenueOrders Expected()=>new BusinessVenueOrders{GhostId="g",VenueId="venue",Orders=new[]{new BusinessOrderState{
  Id="order",Valid=true,CustomerId="customer",TableId="table",ChairId="chair",Ordered=new[]{"food"},Prepared=new[]{"food"},Consumed=1,ServiceQuality=.5f,
  Prices=new[]{new BusinessOrderPrice{Key="food",Price=10}},Delivered=new[]{new BusinessDeliveredMeal{ItemId="food",MealId="meal",Price=10,Quality=.6f,Freshness=.7f}}
 }}};
 public static int Run(){
  int count=0;void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
  var manager=new GhostManager{Venue=new VenueAreaGhost{Id="g",Venue=new Venue{Guid="venue"}}};
  var updates=new System.Collections.Generic.List<(Action Apply,Action Undo)>();var expected=Expected();
  Check(StageOrders(manager,expected,updates)&&updates.Count==1,"Missing native order is staged");
  CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);Check(OrderLookupConsistent(manager.Venue),"Applied native list and lookup agree");
  var original=manager.Venue.Orders._active;var originalIndex=manager.Venue.Orders._activeIdLookup;
  updates.Clear();Check(!StageOrders(manager,expected,updates)&&updates.Count==0,"Fresh equal native order avoids allocation/replacement");
  original[0].ServiceQuality=.75f;Check(StageOrders(manager,expected,updates),"Same incoming DTO detects fresh native order drift");
  CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);Check(manager.Venue.Orders._active[0].ServiceQuality==.5f,"Drift correction restores authoritative value");
  updates.Clear();manager.Venue.Orders._activeIdLookup.Clear();
  Check(StageOrders(manager,expected,updates),"Equal active DTO with missing index entry is repaired");CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);
  updates.Clear();manager.Venue.Orders._activeIdLookup["order"]=new Order{Id="order",Venue=manager.Venue.Venue};
  Check(StageOrders(manager,expected,updates),"Equal ID mapped to a different native order object is repaired");CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);
  updates.Clear();manager.Venue.Orders._active[0].Venue=new Venue{Guid="venue"};
  Check(StageOrders(manager,expected,updates),"Matching venue GUID with a wrong native venue pointer is repaired");CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);
  updates.Clear();manager.Venue.Orders._activeIdLookup["extra"]=new Order{Id="extra",Venue=manager.Venue.Venue};
  Check(StageOrders(manager,expected,updates),"An extra stale lookup entry cannot hide behind equal active rows");CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);
  updates.Clear();var prior=manager.Venue.Orders._active;var priorIndex=manager.Venue.Orders._activeIdLookup;
  manager.Venue.Orders._active[0].DeliveredMeals[0].FreshQuality=.1f;
  Check(StageOrders(manager,expected,updates),"Last nested delivered-meal field is observed before a no-op decision");
  updates.Add((()=>throw new InvalidOperationException("later native mutation failed"),()=>{}));
  bool failed=false;try{CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);}catch(InvalidOperationException){failed=true;}
  Check(failed&&Faulted&&ReferenceEquals(manager.Venue.Orders._active,prior)&&ReferenceEquals(manager.Venue.Orders._activeIdLookup,priorIndex),"Later transaction failure restores exact old native list/index identities");
  Check(manager.Venue.Orders._active[0].DeliveredMeals[0].FreshQuality==.1f,"Rollback restores local baseline rather than authoritative replacement");
  // Worker references may be absent in this client's ghost roster. Preserve
  // their wire identities through the same stage/commit/capture path as native.
  expected=Expected();var incoming=expected.Orders[0];incoming.CustomerId="absent-customer";incoming.TableId="absent-table";incoming.ChairId="absent-chair";incoming.Delivered[0].MealId="consumed-meal";
  GhostReferenceProbe.Missing.UnionWith(new[]{incoming.CustomerId,incoming.TableId,incoming.ChairId,incoming.Delivered[0].MealId});
  Check(new GhostReference<CustomerGhost>(incoming.CustomerId)==null,"Fixture reproduces native unresolved-reference null equality");
  GhostReferenceProbe.ResolutionChecks=0;updates.Clear();
  Check(StageOrders(manager,expected,updates),"Authoritative unresolved references stage normally");
  updates.Add((()=>{if(!OrderLookupConsistent(manager.Venue)||!SameOrders(expected,CaptureOrders(manager.Venue)))throw new InvalidOperationException("Native venue order roundtrip discarded an unresolved reference identity");},()=>{}));
  CommitPrepared(Array.Empty<Action>(),Array.Empty<Action>(),updates);
  Check(SameOrders(expected,CaptureOrders(manager.Venue)),"Customer, table, chair and consumed meal IDs survive immediate verified readback");
  Check(GhostReferenceProbe.ResolutionChecks==0,"Capturing identity does not resolve a local ghost or invoke native equality");
  updates.Clear();Check(!StageOrders(manager,expected,updates)&&updates.Count==0,"An equal order with unresolved references remains a no-op");
  GhostReferenceProbe.Missing.Clear();Check(SameOrders(expected,CaptureOrders(manager.Venue)),"Later ghost arrival cannot change captured order identity");
  Check(Ref<CustomerGhost>(null)==""&&Ref(new GhostReference<CustomerGhost>(""))=="","Missing wrappers and empty references remain empty");
  var observed=CaptureOrders(manager.Venue);observed.Orders[0].ChairId="different-private-id";
  Check(!SameOrders(expected,observed)&&DescribeOrdersMismatch(expected,observed)=="Orders[0].ChairId","Readback still rejects changed identity and names only the mismatched field");
  observed=CaptureOrders(manager.Venue);observed.Orders[0].Delivered[0].Freshness=.2f;
  Check(!SameOrders(expected,observed)&&DescribeOrdersMismatch(expected,observed)=="Orders[0].Delivered[0].Freshness","Nested readback diagnostics identify the last delivered field without payload values");
  observed=CaptureOrders(manager.Venue);observed.Orders=Array.Empty<BusinessOrderState>();
  Check(DescribeOrdersMismatch(expected,observed)=="Orders.Length","Roster length diagnostics remain bounded");
  manager.Venue.Orders._active.Clear();manager.Venue.Orders._activeIdLookup.Clear();updates.Clear();expected.Orders=Array.Empty<BusinessOrderState>();
  Check(!StageOrders(manager,expected,updates)&&updates.Count==0,"Empty native list with an empty consistent index is a true no-op");
  manager.Venue.Orders._activeIdLookup["stale"]=new Order{Id="stale"};
  Check(StageOrders(manager,expected,updates),"Empty native list with stale index is repaired");
  return count;
 }
'@+($methods.ToFullString()-join "`n")+'}'+($dtos.ToFullString()-join "`n")
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$gameRoot=Split-Path -Parent $devRoot
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('CraftOrderProbe'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))
$stream=[IO.MemoryStream]::new()
try{$result=$comp.Emit($stream);if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Native craft order staging fixture failed to compile.'};$assembly=[Reflection.Assembly]::Load($stream.ToArray());$checks=$assembly.GetType('CraftOrderProbe').GetMethod('Run').Invoke($null,$null)}finally{$stream.Dispose()}
"PASS: $checks production fresh native order observation/index consistency/staging/rollback checks."
