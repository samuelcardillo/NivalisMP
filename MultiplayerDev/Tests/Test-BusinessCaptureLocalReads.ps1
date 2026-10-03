$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/BusinessStateReplication.cs'))).GetRoot()
$methods=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('TrackPerson','CaptureVenue','CaptureContainerState','NativeCapacity','CapacityRestriction')})
$dtos=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('BusinessContainerState','BusinessMenuItem','BusinessVenueState')})
if($methods.Count-ne7-or$dtos.Count-ne3){throw 'Actual production capture helpers and DTOs required.'}
$source=@'
using System;using System.Collections.Generic;using System.Linq;
namespace BusinessCaptureLocalReads {
 public sealed class Slot<T>{public T Value;public int Reads;public T Read(){Reads++;return Value;}public Slot(T value){Value=value;}}
 public sealed class OptionalInt{public bool HasValue;public int Value;public OptionalInt(int value,bool present=true){Value=value;HasValue=present;}}
 public sealed class Tag{public string Guid;}
 public sealed class SharedItemStack{public int Value;}
 public sealed class ItemContainerRestriction{
  public Slot<OptionalInt> Items=new(new OptionalInt(30)),Slots=new(new OptionalInt(5));public Slot<Tag> Filter=new(new Tag{Guid="food"});
  public OptionalInt MaxItems=>Items.Read();public OptionalInt MaxSlots=>Slots.Read();public Tag TagFilter=>Filter.Read();public bool AcceptsIngredients=true;
 }
 public sealed class ItemContainer{
  public Slot<ItemContainerRestriction> RestrictionSlot=new(new ItemContainerRestriction());public int RawReads;
  public ItemContainerRestriction Restriction=>RestrictionSlot.Read();public ItemContainerRestriction _restriction{get{RawReads++;return RestrictionSlot.Value;}}
  public bool IsManuallyUpdated=true,IsRefridgerated=true,Match=true;public SharedItemStack[] NativeItems=new[]{new SharedItemStack{Value=1}};
 }
 public static class SharedEconomyReplication{
  public static int Matches,Captures;
  public static bool TryMatchNativeItems(ItemContainer container,SharedItemStack[] expected,out int count){Matches++;count=expected.Length;return container.Match;}
  public static SharedItemStack[] CaptureNativeItems(ItemContainer container){Captures++;return container.NativeItems;}
 }
 public static class DedicatedProtocol{public static bool Text(string text,int limit)=>!string.IsNullOrEmpty(text)&&text.Length<=limit;}
 public sealed class Person{public Slot<string> Id;public int RuntimeReads;public string Guid=>Id.Read();public object RuntimeData{get{RuntimeReads++;return this;}}public Person(string id){Id=new(id);}}
 public sealed class MenuItem{public Slot<Tag> Recipe;public Tag recipe=>Recipe.Read();public int price;public MenuItem(string id,int amount){Recipe=new(new Tag{Guid=id});price=amount;}}
 public sealed class ObservedList<T>{public readonly List<T> Values=new();public int CountReads,IndexReads;public int Count{get{CountReads++;return Values.Count;}}public T this[int i]{get{IndexReads++;return Values[i];}}}
 public sealed class BooleanValue{public bool Value=true;}
 public sealed class Venue{public string Guid="venue";public Slot<OptionalInt> Debt=new(new OptionalInt(42));public OptionalInt currentDebt=>Debt.Read();}
 public sealed class Finances{public Slot<Dictionary<int,int>> Profits=new(new Dictionary<int,int>{{3,30},{1,10}});public Dictionary<int,int> _profitsPerDay=>Profits.Read();}
 public sealed class Joint{public ItemContainer _RefridgeratedInventory_k__BackingField=new(),_NormalInventory_k__BackingField=new();}
 public sealed class VenueAreaGhost{
  public Slot<BooleanValue> Open=new(new BooleanValue());public Slot<Joint> JointSlot=new(new Joint());public Slot<ObservedList<MenuItem>> Menu=new(new ObservedList<MenuItem>());
  public Slot<ObservedList<Person>> Staff=new(new ObservedList<Person>());public Slot<Venue> VenueSlot=new(new Venue());public Slot<Finances> FinancesSlot=new(new Finances());
  public BooleanValue _isOpen=>Open.Read();public Joint JointInventory=>JointSlot.Read();public ObservedList<MenuItem> _menu=>Menu.Read();public ObservedList<Person> staff=>Staff.Read();public Venue Venue=>VenueSlot.Read();public Finances _finances=>FinancesSlot.Read();
  public string Id="ghost";public int OwnershipType=2,currentLevel=4,mealsServed=9,totalVisitsCount=20;public bool wasVisitedByPlayer=true;
  public ItemContainer FridgeInventory=new(),CupboardInventory=new(),FurnitureInventory=new();
 }
/*DTO*/
 public static class Probe{
  const int MaxPeople=20000;static readonly Dictionary<string,Person> TrackedPeople=new();
/*METHODS*/
  static void Reject(Action action,string why,ref int checks){checks++;try{action();}catch(InvalidOperationException){return;}throw new Exception(why);}
  public static int Run(){int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
   var ghost=new VenueAreaGhost();ghost.Menu.Value.Values.Add(new MenuItem("r2",4));ghost.Menu.Value.Values.Add(new MenuItem("r1",8));ghost.Staff.Value.Values.Add(new Person("z"));ghost.Staff.Value.Values.Add(new Person("a"));
   var before=CaptureVenue(ghost);
   Check(before.GhostId=="ghost"&&before.VenueId=="venue"&&before.Open&&before.Ownership==2&&before.Level==4&&before.MealsServed==9&&before.Visits==20&&before.Visited&&before.Debt==42,"All venue scalar fields remain intact");
   Check(before.Menu[0].RecipeId=="r2"&&before.Menu[1].RecipeId=="r1"&&before.Menu[0].Price==4&&before.Menu[1].Price==8,"User-visible menu order and prices remain intact");
   Check(string.Join(",",before.Staff)=="a,z"&&before.Profits[0][0]==1&&before.Profits[1][0]==3&&before.Profits[1][1]==30,"Staff and profit snapshots remain canonically sorted");
   Check(ghost.Open.Reads==1&&ghost.JointSlot.Reads==1&&ghost.Menu.Reads==1&&ghost.Staff.Reads==1&&ghost.VenueSlot.Reads==1&&ghost.FinancesSlot.Reads==1,"Each venue wrapper property is fetched once per capture");
   Check(ghost.Menu.Value.CountReads==1&&ghost.Menu.Value.IndexReads==2&&ghost.Menu.Value.Values.All(x=>x.Recipe.Reads==1),"Menu count and each recipe wrapper are read once");
   Check(ghost.Staff.Value.CountReads==1&&ghost.Staff.Value.IndexReads==2&&ghost.FinancesSlot.Value.Profits.Reads==1&&ghost.VenueSlot.Value.Debt.Reads==1,"Staff list, profit dictionary and debt wrappers are read once");
   Check(ghost.Staff.Value.Values.All(p=>p.Id.Reads==2&&p.RuntimeReads==1),"TrackPerson reads identity once, plus one separate captured staff identity");
   Check(ghost.JointSlot.Value._NormalInventory_k__BackingField.RawReads==1&&ghost.JointSlot.Value._RefridgeratedInventory_k__BackingField.RawReads==1,"CapacityRestriction resolves each native restriction once");
   var c=ghost.FridgeInventory;var restriction=c.RestrictionSlot.Value;
   Check(restriction.Items.Reads==1&&restriction.Slots.Reads==1&&restriction.Filter.Reads==1&&c.RestrictionSlot.Reads==1,"Container optional and tag wrappers are each read once");
   Check(before.Fridge.Manual&&before.Fridge.Refrigerated&&before.Fridge.AcceptsIngredients&&before.Fridge.MaxItems==30&&before.Fridge.MaxSlots==5&&before.Fridge.TagFilter=="food","Fresh container metadata is unchanged");
   ghost.Menu.Value=new ObservedList<MenuItem>();ghost.Menu.Value.Values.Add(new MenuItem("new",77));ghost.Staff.Value=new ObservedList<Person>();ghost.Staff.Value.Values.Add(new Person("next"));
   ghost.VenueSlot.Value=new Venue{Guid="replacement"};ghost.VenueSlot.Value.Debt.Value=new OptionalInt(-9);ghost.FinancesSlot.Value=new Finances();ghost.FinancesSlot.Value.Profits.Value=new Dictionary<int,int>{{7,88}};ghost.Open.Value=new BooleanValue{Value=false};
   c.RestrictionSlot.Value=new ItemContainerRestriction();c.RestrictionSlot.Value.Items.Value=new OptionalInt(99);c.RestrictionSlot.Value.Slots.Value=new OptionalInt(0,false);c.RestrictionSlot.Value.Filter.Value=null;c.IsManuallyUpdated=false;c.IsRefridgerated=false;c.RestrictionSlot.Value.AcceptsIngredients=false;
   var next=CaptureVenue(ghost,before);
   Check(next.VenueId=="replacement"&&next.Debt==-9&&!next.Open&&next.Menu.Length==1&&next.Menu[0].RecipeId=="new"&&next.Menu[0].Price==77,"Next invocation rereads replaced native venue/menu/debt wrappers");
   Check(next.Staff.Length==1&&next.Staff[0]=="next"&&next.Profits.Length==1&&next.Profits[0][0]==7&&next.Profits[0][1]==88,"Next invocation rereads replaced staff and finances");
   Check(!next.Fridge.Manual&&!next.Fridge.Refrigerated&&!next.Fridge.AcceptsIngredients&&next.Fridge.MaxItems==99&&next.Fridge.MaxSlots==null&&next.Fridge.TagFilter=="","Identical item contents never hide fresh metadata drift");
   Check(SharedEconomyReplication.Matches==3&&ReferenceEquals(next.Fridge.Items,before.Fridge.Items),"Each supplied item snapshot is freshly matched before reuse");
   c.Match=false;c.NativeItems=new[]{new SharedItemStack{Value=99}};int captures=SharedEconomyReplication.Captures;var changed=CaptureContainerState(c,before.Fridge.Items);
   Check(ReferenceEquals(changed.Items,c.NativeItems)&&SharedEconomyReplication.Captures==captures+1,"Fresh item mismatch still captures the changed authoritative local inventory");
   c.RestrictionSlot.Value.Items.Value=null;c.RestrictionSlot.Value.Slots.Value=null;
   var absent=CaptureContainerState(c);Check(absent.MaxItems==null&&absent.MaxSlots==null,"Absent nullable wrappers retain their previous container semantics");
   Reject(()=>CapacityRestriction(c),"Joint capacity without OptionalInt must remain invalid",ref checks);
   c.RestrictionSlot.Value=null;Reject(()=>CapacityRestriction(c),"Missing restriction must remain invalid",ref checks);Reject(()=>CapacityRestriction(null),"Missing joint container must remain invalid",ref checks);Reject(()=>CaptureContainerState(null),"Missing business inventory remains invalid",ref checks);
   Reject(()=>NativeCapacity(null),"Native capacity requires initialized OptionalInt",ref checks);Check(NativeCapacity(new OptionalInt(0,false))==null&&NativeCapacity(new OptionalInt(int.MinValue))==int.MinValue,"Valid optional capacity retains absent/full Int32 range");
   var valid=new VenueAreaGhost();valid.Menu.Value.Values.Add(new MenuItem("bad",1));valid.Menu.Value.Values[0].Recipe.Value=null;Reject(()=>CaptureVenue(valid),"Unresolved native menu recipe remains invalid",ref checks);
   valid.Menu.Value.Values[0]=null;Reject(()=>CaptureVenue(valid),"Null native menu item remains invalid",ref checks);
   foreach(int which in new[]{0,1,2,3}){valid=new VenueAreaGhost();if(which==0)valid.Open.Value=null;if(which==1)valid.JointSlot.Value=null;if(which==2)valid.Menu.Value=null;if(which==3)valid.Staff.Value=null;Reject(()=>CaptureVenue(valid),"Uninitialized venue wrapper remains invalid",ref checks);}
   valid=new VenueAreaGhost();valid.FinancesSlot.Value=null;valid.VenueSlot.Value.Debt.Value=null;var noFinance=CaptureVenue(valid);Check(noFinance.Profits.Length==0&&noFinance.Debt==null,"Optional absent finances/debt remain supported");
   return checks;
  }
 }
}
'@
$source=$source.Replace('/*DTO*/',($dtos.ToFullString()-join"`n")).Replace('/*METHODS*/',($methods.ToFullString()-join"`n"))
Add-Type -TypeDefinition $source -Language CSharp
$count=[BusinessCaptureLocalReads.Probe]::Run()
"PASS $count actual business capture assertions: bounded wrapper reads, fresh replacement/drift, canonical output and native-null validation."
