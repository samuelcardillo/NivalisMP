$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$path=Join-Path $devRoot 'Source/BusinessCraftStateReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path)).GetRoot()
$names=@('CaptureMeal','ObserveMeal','ObserveProcessor','CaptureSpot','Vector','Ref','SameMeal','SameProcessor','SameSpot','SameValues','SameAppliedPose')
$methods=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
if($methods.Count -ne $names.Count){throw 'Production observation methods missing.'}
$structs=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax] -and $_.Identifier.ValueText -in @('MealObservation','ProcessorObservation')})
if($structs.Count -ne 2){throw 'Scoped production observations missing.'}
$dtos=@($tree.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('BusinessMealState','BusinessProcessorState','BusinessProcessorSpot')})
$code=@'
using System;using System.Collections.Generic;
namespace Il2CppSystem.Collections.Generic{public class List<T>:System.Collections.Generic.List<T>{}}
public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}}
public struct Quaternion {public float x,y,z,w;public Quaternion(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}public static Quaternion identity=>new(0,0,0,1);public static Quaternion Normalize(Quaternion q){float norm=MathF.Sqrt(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w);return new(q.x/norm,q.y/norm,q.z/norm,q.w/norm);}}
public struct Bounds {public Vector3 center,size;}
public class Counter {
 public readonly Dictionary<string,int> Reads=new();
 public T Read<T>(string key,T value){Reads[key]=Count(key)+1;return value;}
 public int Count(string key)=>Reads.TryGetValue(key,out int value)?value:0;
}
public class Ghost:Counter {
 public string Id="ghost",PrefabId="prefab",ViewId="view";
 public int SceneIndex=1;public bool IsActive=true,IsViewLess,IsBakedInstance,HasLoadedView;
 public Vector3 Position=new(1,2,3);public Quaternion Rotation=new(0,0,0,1);
 public Bounds Bounds=new(){center=new(4,5,6),size=new(7,8,9)};
 public GhostReference<Ghost> parent=new("parent");public View LoadedView=new();
 public T TryCast<T>()where T:class=>this as T;
}
public class View {public T TryCast<T>()where T:class=>this as T;}
public class FoodProcessor:View{public bool IsDrinksMachine;}
public class HoldableGhost:Ghost {public bool _canBeHeld=true,_isHeld;}
public class TableGhost:Ghost{}
public class ChairGhost:Ghost{}
public class Tasks:Counter {
 public bool HasMakeMealTask(bool drinks){Read("make",0);return true;}
 public bool HasProcessingTask(int types){Read("processing",0);return true;}
}
public class VenueAreaGhost:Ghost {
 public Tasks TasksValue=new();public Tasks TaskQueue=>Read("tasks",TasksValue);
}
public class GhostReference<T>:Counter where T:Ghost {
 public string ReferencedId;public T ValueValue;public T Value=>Read("value",ValueValue);
 public GhostReference(string id){ReferencedId=id;}
}
public class ItemType {public string Guid="item";}
public class IngredientProcessorType:ItemType {public int AvailableProcessingTypes=7;}
public class Venue {public string Guid="venue";}
public class VenueOrderReference:Counter {
 public Venue VenueValue=new();public Venue Venue=>Read("venue",VenueValue);public string OrderId="order";
}
public class GhostAISpotReference:Counter {
 public int SpotId=5,SpotType=4;public Vector3 OffsetValue=new(7,8,9);
 public Vector3 PositionOffset=>Read("offset",OffsetValue);
}
public class RelativeVector3 {public Vector3 LocalOffset=new(2,3,4);}
public class MealGhost:HoldableGhost {
 public ItemType TypeValue=new();public ItemType Type=>Read("type",TypeValue);
 public VenueOrderReference OrderValue=new();public VenueOrderReference VenueOrderReference=>Read("order",OrderValue);
 public GhostAISpotReference Spot=new();public float Quality=.6f,Freshness=.7f;
 public bool _IsFailed_k__BackingField;public int _fillState=1,_playerAction=2;
 public GhostReference<Ghost> _reservedBy=new("reserved");
 public GhostReference<FoodProcessorGhost> ProcessorMade=new("processor");public GhostReference<ChairGhost> ForChair=new("chair");
}
public class GhostMealList {
 public Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>> backingList=new();
 public int Count=>backingList.Count;public GhostReference<MealGhost> this[int index]=>backingList[index];
}
public class FoodProcessorGhost:HoldableGhost {
 public GhostMealList MealsValue=new();public GhostMealList Meals=>Read("meals",MealsValue);
 public int[] RecipesValue=new[]{1,2,3,4};public int[] Recipes=>Read("recipes",RecipesValue);
 public GhostReference<VenueAreaGhost> VenueValue=new("venue-ghost"){ValueValue=new()};
 public GhostReference<VenueAreaGhost> Venue=>Read("venue",VenueValue);
 public bool _isInUse;public GhostReference<Ghost> _reservedBy=new("reserved");
 public GhostAISpotReference InteractionSpot=new(),ServeSpot=new();public RelativeVector3 SpawnMealPoint=new(),SpawnTrayPoint=new();
}
public class IngredientProcessorGhost:HoldableGhost {
 public IngredientProcessorType TypeValue=new();public IngredientProcessorType Type=>Read("type",TypeValue);
 public GhostReference<VenueAreaGhost> VenueValue=new("venue-ghost"){ValueValue=new()};
 public GhostReference<VenueAreaGhost> Venue=>Read("venue",VenueValue);
 public float ProcessingTime=20;public bool _isInUse;public GhostReference<Ghost> _reservedBy=new("reserved");public GhostAISpotReference InteractionSpot=new();
}
public static class CraftScopedObservationProbe {
'@+($structs.ToFullString()-join "`n")+($methods.ToFullString()-join "`n")+@'
 public static int Run(){
  int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
  void Reject(Action action,string why){try{action();}catch(InvalidOperationException){Check(true,why);return;}throw new Exception(why);}
  var meal=new MealGhost();var first=ObserveMeal(meal);
  Check(meal.Count("type")==1&&meal.Count("order")==1&&meal.OrderValue.Count("venue")==1,"Meal native item/order/venue wrappers are fetched exactly once");
  Check(meal.Spot.Count("offset")==1,"Spot vector is read once for all three coordinates");
  Check(ReferenceEquals(first.Item,meal.TypeValue)&&ReferenceEquals(first.Venue,meal.OrderValue.VenueValue),"Rollback owns the exact observed item and venue references");
  Check(first.State.ItemId=="item"&&first.State.VenueId=="venue"&&first.State.OrderId=="order"&&first.State.Quality==.6f&&first.State.Freshness==.7f,"Meal state retains native scalar and asset values");
  Check(SameValues(first.State.Pose,new float[]{1,2,3,0,0,0,1})&&SameValues(first.State.Bounds,new float[]{4,5,6,7,8,9})&&SameValues(first.State.SpotOffset,new float[]{7,8,9}),"Every pose, bounds and spot component survives scoped capture");
  Check(SameMeal(first.State,CaptureMeal(meal)),"Worker capture and client observation have identical wire semantics");
  var priorItem=meal.TypeValue;var priorVenue=meal.OrderValue.VenueValue;
  meal.TypeValue=new(){Guid="replacement"};meal.OrderValue=new(){VenueValue=new(){Guid="replacement-venue"},OrderId="new-order"};meal.Quality=.4f;meal.Spot.OffsetValue=new(10,11,12);
  var second=ObserveMeal(meal);
  Check(second.State.ItemId=="replacement"&&second.State.VenueId=="replacement-venue"&&second.State.Quality==.4f&&!SameMeal(first.State,second.State),"Each invocation freshly observes replaced native references and scalar drift");
  Check(ReferenceEquals(first.Item,priorItem)&&ReferenceEquals(first.Venue,priorVenue)&&first.State.ItemId=="item"&&first.State.SpotOffset[0]==7,"Earlier rollback preimage remains stable after native replacement");
  meal.OrderValue=null;Check(ObserveMeal(meal).State.VenueId==""&&ObserveMeal(meal).State.OrderId=="","Absent meal order remains valid");
  meal.TypeValue=null;Reject(()=>ObserveMeal(meal),"Missing meal item fails closed");
  var food=new FoodProcessorGhost();for(int i=0;i<100;i++)food.MealsValue.backingList.Add(new("meal-"+i));
  var foodBefore=ObserveProcessor(food,food,null,false);
  Check(food.Count("meals")==1&&food.Count("recipes")==1&&food.Count("venue")==1,"Processor parent wrappers are fetched once independently of recipe and meal counts");
  Check(food.VenueValue.Count("value")==0,"Client capture does not resolve worker availability or task queues");
  Check(foodBefore.State.Meals.Length==100&&foodBefore.State.Meals[99]=="meal-99"&&SameValues(foodBefore.State.Recipes,new[]{1,2,3,4}),"Scoped lists retain every recipe and meal identity in native order");
  var oldBacking=food.MealsValue.backingList;
  Check(ReferenceEquals(foodBefore.Meals,oldBacking)&&foodBefore.Type==null,"Food rollback preserves exact backing-list object");
  food.MealsValue=new();food.MealsValue.backingList.Add(new("new-meal"));food.RecipesValue=new[]{8};food._isInUse=true;
  var foodAfter=ObserveProcessor(food,food,null,false);
  Check(foodAfter.State.Meals.Length==1&&foodAfter.State.Meals[0]=="new-meal"&&foodAfter.State.Recipes[0]==8&&foodAfter.State.Busy,"Next invocation sees replaced containers and changed scalars");
  Check(!SameProcessor(foodBefore.State,foodAfter.State)&&ReferenceEquals(foodBefore.Meals,oldBacking)&&foodBefore.State.Meals.Length==100,"Comparison detects drift while original rollback identity and copied wire rows remain intact");
  food.HasLoadedView=true;food.LoadedView=new FoodProcessor();var available=ObserveProcessor(food,food,null,true);
  Check(available.State.AvailabilityKnown&&available.State.CanPrepare&&food.VenueValue.ValueValue.Count("tasks")==1,"Worker availability resolves the task queue exactly once");
  Check(SameProcessor(available.State,foodAfter.State),"Availability-only projection preserves comparison semantics");
  food.RecipesValue=null;Reject(()=>ObserveProcessor(food,food,null,false),"Missing recipes fail closed");food.RecipesValue=Array.Empty<int>();food.MealsValue=null;
  Reject(()=>ObserveProcessor(food,food,null,false),"Missing native meal list fails closed");
  food.MealsValue=new(){backingList=null};Reject(()=>ObserveProcessor(food,food,null,false),"Missing meal backing list fails closed");
  var ingredient=new IngredientProcessorGhost();var ingredientBefore=ObserveProcessor(ingredient,null,ingredient,true);
  Check(ingredient.Count("type")==1&&ingredient.Count("venue")==1&&ingredient.VenueValue.ValueValue.Count("tasks")==1,"Ingredient type, venue and worker queue are fetched once");
  Check(ingredientBefore.State.Ingredient&&ingredientBefore.State.TypeId=="item"&&ingredientBefore.State.ProcessingTime==20&&ingredientBefore.State.CanPrepare,"Ingredient configuration and availability remain complete");
  var oldType=ingredient.TypeValue;ingredient.TypeValue=new(){Guid="new-type"};ingredient.ProcessingTime=40;
  var ingredientAfter=ObserveProcessor(ingredient,null,ingredient,false);
  Check(ingredientAfter.State.TypeId=="new-type"&&ingredientAfter.State.ProcessingTime==40&&!SameProcessor(ingredientBefore.State,ingredientAfter.State),"Fresh ingredient observation detects replaced type and timing drift");
  Check(ReferenceEquals(ingredientBefore.Type,oldType)&&ingredientBefore.State.TypeId=="item"&&ingredientBefore.State.ProcessingTime==20,"Ingredient rollback preserves exact old type and scalar preimage");
  ingredient.TypeValue=null;Reject(()=>ObserveProcessor(ingredient,null,ingredient,false),"Missing ingredient type fails closed");
  return checks;
 }
}
'@+($dtos.ToFullString()-join "`n")
Add-Type -TypeDefinition $code
"PASS: $([CraftScopedObservationProbe]::Run()) production craft scoped-observation assertions; getter counts, fresh drift and exact rollback references."
