// Native boundary doubles only. The lifecycle/scoping/ledger implementation is
// compiled verbatim from Source/PlayerAccountRuntime.cs by the test launcher.
namespace HarmonyLib {
 public sealed class HarmonyMethod { public HarmonyMethod(System.Type t,string n){} }
 public sealed class Harmony { public Harmony(string n){} public void UnpatchSelf(){} public void Patch(System.Reflection.MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod finalizer=null){} }
}
namespace Il2CppInterop.Runtime { public static class Il2CppType { public static System.Type Of<T>()=>typeof(T); } }
namespace Nivalis {
 public class NativeObject { public System.IntPtr Pointer=new System.IntPtr(1); public T TryCast<T>() where T:class=>this as T; }
 public sealed class PlayerManagerConfiguration:NativeObject { public int Money=75; }
 public sealed class Bootstrap { public System.Collections.Generic.Dictionary<System.Type,NativeObject> configurationMap=new System.Collections.Generic.Dictionary<System.Type,NativeObject>{{typeof(PlayerManagerConfiguration),new PlayerManagerConfiguration()}}; }
 public sealed class GameSceneManager { public static GameSceneManager _instance=new GameSceneManager(); public Bootstrap bootManager=new Bootstrap(); }
 public sealed class PlayerManager { public static PlayerManager _instance=new PlayerManager(); public Player LocalPlayer=new Player(); public sealed class Player:NativeObject { public InventorySystem.PlayerInventory Inventory=new InventorySystem.PlayerInventory(); } }
 public static class TimeOfDayManager { public static int GameplayGameDay=8; }
 public sealed class RentManager { public sealed class Rent { public NativeObject rentier; public string rentableGuid; } public void CollectReceipt(Rent r){} }
}
namespace Nivalis.InventorySystem {
 public sealed class PlayerInventory { public int _money; public ItemContainer Items=new ItemContainer(); }
 public sealed class ItemContainer { public int ItemCount; public int UpdateItemDecay(bool createRottenFood,int day){NivalisTogether.Dedicated.SharedEconomyReplication.DecayCalls++;return 0;} }
}
namespace Nivalis.GhostSystem.Ai { public sealed class Person{} public sealed class AgentGhost{} }
namespace Nivalis.GhostSystem.CustomerLoop {
 public sealed class Venue { public bool PlayerOwned=true; public string Guid="venue"; }
 public sealed class VenueAreaGhost { public Venue Venue=new Venue(); public void PayStaff(Nivalis.GhostSystem.Ai.Person p){} }
 public sealed class Order { public Venue Venue=new Venue(); }
 public sealed class VisitVenueAgentActionType { public sealed class State{} public sealed class LeaveVenueSubAction { public static void LeaveReview(Nivalis.GhostSystem.Ai.AgentGhost g,State s,Order o){} } }
}
namespace Nivalis.UI.EndOfDay { public sealed class EndOfDayDebtSection { public void Show(int money){} public void ChoicePicked(float percentage){} } }
namespace NivalisTogether {
 public static class DedicatedWorldRuntime { public static bool IsDedicatedServer=true,IsIsolated=true,IsWorldReady=true; }
 public static class PlayerAppearance { public static bool CanResolve(string id)=>id=="approved"; }
}
namespace NivalisTogether.Dedicated {
 public sealed class SharedItemStack { public string Type{get;set;}="";public int LastUpdate{get;set;}public int[][] Items{get;set;}=System.Array.Empty<int[]>(); }
 public sealed class PersonalInventoryState { public int Money{get;set;} public SharedItemStack[] Inventory{get;set;}=System.Array.Empty<SharedItemStack>(); }
 public static class PropertyOwnershipReplication {
  public static string StarterPropertyId="starter",AccountRentalOwner="";public static string OwnerOf(string p)=>p=="legacy"?AccountProtocol.LegacyOwnerId:"";public static string NativeOwnerOf(string p)=>p=="account-rental"?AccountRentalOwner:OwnerOf(p);
  public static bool GiftPending;public static int Reconciliations;
  public static bool ReconcileNativeGifts(AccountWorldState state){Reconciliations++;if(!GiftPending)return false;GiftPending=false;state.Owners=new[]{new PropertyOwner{PropertyId="11111111-1111-1111-1111-111111111111",Kind="business",OwnerAccountId=AccountProtocol.LegacyOwnerId}};return true;}
 }
 public static class SharedEconomyReplication {
  public static int DecayCalls,DefaultsCreated;public static bool ThrowApply;public static PersonalInventoryState Native=new PersonalInventoryState();
  public static PersonalInventoryState CapturePersonal(){Native.Money=Nivalis.PlayerManager._instance.LocalPlayer.Inventory._money;return Clone(Native);}
  public static string PersonalHash(PersonalInventoryState s)=>System.Text.Json.JsonSerializer.Serialize(s,DedicatedProtocol.Json);
  public static void ApplyPersonal(PersonalInventoryState s,bool refreshUi=false){if(ThrowApply){ThrowApply=false;throw new System.InvalidOperationException("native apply fault");}Native=Clone(s);Nivalis.PlayerManager._instance.LocalPlayer.Inventory._money=s.Money;Nivalis.PlayerManager._instance.LocalPlayer.Inventory.Items.ItemCount=s.Inventory.Length;}
  public static PersonalInventoryState CreateStartingInventory(Nivalis.PlayerManagerConfiguration c){DefaultsCreated++;return new PersonalInventoryState{Money=c.Money,Inventory=new[]{new SharedItemStack{Type="fresh",Items=new[]{new[]{0,8,5,8}}}}};}
  public static T Clone<T>(T v)=>System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(v,DedicatedProtocol.Json),DedicatedProtocol.Json);
 }
 public static class PlayerAccountRuntimeProbe {
  private static int checks;
  private static readonly string A=new string('a',32),B=new string('b',32);
  private static void Check(bool v,string m){checks++;if(!v)throw new System.Exception(m);}
  private static void Throws(System.Action f,string m){checks++;try{f();}catch{return;}throw new System.Exception(m);}
  private static void Money(int n)=>Nivalis.PlayerManager._instance.LocalPlayer.Inventory._money=n;
  private static int Money()=>Nivalis.PlayerManager._instance.LocalPlayer.Inventory._money;
  private static AccountPreparation Prep(string id)=>new AccountPreparation{AccountId=id,AppearanceId="approved"};
  private static bool Rent(Nivalis.RentManager.Rent value,out System.IDisposable scope){
   object[] args={value,null};var method=typeof(PlayerAccountRuntime).GetMethod("RentPrefix",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
   bool result=(bool)method.Invoke(null,args);scope=(System.IDisposable)args[1];return result;
  }
  private static void CheckSharedStarterRent(){
   PlayerAccountRuntime.Reset();DedicatedWorldRuntime.IsDedicatedServer=true;PropertyOwnershipReplication.StarterPropertyId="starter";
   var starter=new Nivalis.RentManager.Rent{rentier=Nivalis.PlayerManager._instance.LocalPlayer,rentableGuid="starter"};
   Check(Rent(starter,out var scope)&&scope==null,"uninitialized native rent path is unchanged");
   SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState{Money=900});
   PlayerAccountRuntime.InitializeWorker(new AccountWorldState{WorldId=new string('c',32),Revision=30});
   PlayerAccountRuntime.EnsureAccount(Prep(A));PropertyOwnershipReplication.AccountRentalOwner=A;
   Check(!Rent(starter,out scope)&&scope==null,"shared starter rent is suppressed before creating an account scope");
   Check(PlayerAccountRuntime.LegacyEscrowMoney==900&&Money()==0&&PlayerAccountRuntime.CurrentAccountId=="","shared starter rent preserves legacy escrow and neutral inventory");
   Check(!Rent(starter,out scope)&&scope==null&&PlayerAccountRuntime.LegacyEscrowMoney==900,"repeated starter collection cannot drain preserved legacy money");
   var other=new Nivalis.RentManager.Rent{rentier=new Nivalis.NativeObject{Pointer=new System.IntPtr(99)},rentableGuid="starter"};
   Check(Rent(other,out scope)&&scope==null,"nonlocal renter remains on the native billing path");
   DedicatedWorldRuntime.IsDedicatedServer=false;
   Check(Rent(starter,out scope)&&scope==null,"nonworker and solo native rent are unaffected even during teardown");
   DedicatedWorldRuntime.IsDedicatedServer=true;
   var personal=new Nivalis.RentManager.Rent{rentier=Nivalis.PlayerManager._instance.LocalPlayer,rentableGuid="account-rental"};
   Check(Rent(personal,out scope)&&scope!=null&&PlayerAccountRuntime.CurrentAccountId==A,"ordinary account-owned rental enters its owner's existing money scope");
   Money(Money()-25);scope.Dispose();
   Check(PlayerAccountRuntime.GetAccount(A).Money==499975&&PlayerAccountRuntime.LegacyEscrowMoney==900&&Money()==0,"ordinary rent charges only the owning account");
   var legacy=new Nivalis.RentManager.Rent{rentier=Nivalis.PlayerManager._instance.LocalPlayer,rentableGuid="legacy"};
   Check(Rent(legacy,out scope)&&scope!=null&&PlayerAccountRuntime.CurrentAccountId==AccountProtocol.LegacyOwnerId,"ordinary legacy rental retains its separate escrow billing scope");
   Money(Money()-10);scope.Dispose();
   Check(PlayerAccountRuntime.LegacyEscrowMoney==890&&PlayerAccountRuntime.GetAccount(A).Money==499975,"only shared starter rent is free; other legacy expenses remain accounted");
   PlayerAccountRuntime.Reset();PropertyOwnershipReplication.AccountRentalOwner="";SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState());
  }
  public static int Run(){
   PlayerAccountRuntime.Reset();DedicatedWorldRuntime.IsDedicatedServer=true;
   SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState{Money=900});
   var ledger=new AccountWorldState{WorldId=new string('c',32),Revision=7};PlayerAccountRuntime.InitializeWorker(ledger);
   Check(PlayerAccountRuntime.LegacyEscrowMoney==900&&Money()==0,"legacy assets escrow once, native neutral");
   var a=PlayerAccountRuntime.EnsureAccount(Prep(A));var b=PlayerAccountRuntime.EnsureAccount(Prep(B));
   Check(a.Money==500000&&b.Money==500000&&a.Inventory.GetArrayLength()==1,"explicit5000-lime grant, native starting items, never legacy/config wallet");
   Check(PlayerAccountRuntime.StartingLimes==5000&&PlayerAccountRuntime.StartingMoney==500000,"native hundredths conversion is explicit");
   Check(SharedEconomyReplication.DefaultsCreated==2,"one native grant per account");
   PlayerAccountRuntime.EnsureAccount(Prep(A));Check(SharedEconomyReplication.DefaultsCreated==2,"reconnect never repeats grant");
   using(PlayerAccountRuntime.WithAccount(A)){
    Money(70);using(PlayerAccountRuntime.WithAccount(A)){Money(65);Check(PlayerAccountRuntime.CurrentAccountId==A,"same-account nested context");}
    Check(Money()==65,"same-account child retains changes");
    using(PlayerAccountRuntime.WithAccount(B)){
     Check(Money()==500000,"different account starts own wallet");Money(60);
     using(PlayerAccountRuntime.WithAccount(A)){Check(Money()==65,"A-B-A sees earlier A changes");Money(50);}
     Check(Money()==60,"A-B-A restores B current ledger");
    }
    Check(Money()==50,"A-B-A restores newest A, not stale65");Money(45);
   }
   Check(Money()==0&&PlayerAccountRuntime.CurrentAccountId=="","outer restores neutral");
   Check(PlayerAccountRuntime.GetAccount(A).Money==45&&PlayerAccountRuntime.GetAccount(B).Money==60,"separate balances captured");
   var copy=PlayerAccountRuntime.GetAccount(A);copy.Money=999;Check(PlayerAccountRuntime.GetAccount(A).Money==45,"private result detached");
   ledger.DialogueOwnerAccountId=A;ledger.StarterPropertyId="22222222-2222-2222-2222-222222222222";PropertyOwnershipReplication.GiftPending=true;
   var checkpoint=PlayerAccountRuntime.CaptureLedger(8);Check(checkpoint.DialogueOwnerAccountId==A&&checkpoint.LegacyEscrow.Money==900,"ledger clone retains dialogue and escrow");
   Check(PropertyOwnershipReplication.Reconciliations==1&&checkpoint.Owners.Length==1&&checkpoint.Owners[0].OwnerAccountId==AccountProtocol.LegacyOwnerId,"actual capture reconciles late native gifts before cloning");
   Check(checkpoint.Accounts[0].Money==45&&checkpoint.Accounts[1].Money==60&&checkpoint.LegacyEscrow.Money==900,"ownership reconciliation does not transfer money or items");
   ledger.Owners[0].Kind="apartment";Check(checkpoint.Owners[0].Kind=="business","captured gift record detached from live ledger");ledger.Owners[0].Kind="business";
   using(PlayerAccountRuntime.WithAccount(A)){Money(40);}PlayerAccountRuntime.MarkCommitted(8);Check(PlayerAccountRuntime.IsDirty,"ack keeps mutations after captured revision");
   checkpoint=PlayerAccountRuntime.CaptureLedger(9);PlayerAccountRuntime.MarkCommitted(9);Check(!PlayerAccountRuntime.IsDirty,"matching new capture clears dirty");
   Throws(()=>PlayerAccountRuntime.MarkCommitted(8),"old ACK rejected");
   using(PlayerAccountRuntime.WithAccount(AccountProtocol.LegacyOwnerId)){Money(850);}Check(PlayerAccountRuntime.LegacyEscrowMoney==850&&PlayerAccountRuntime.GetAccount(A).Money==40,"legacy background effects remain separate");
   int calls=SharedEconomyReplication.DecayCalls;PlayerAccountRuntime.Tick();Check(SharedEconomyReplication.DecayCalls>calls,"day work applies native decay");
   calls=SharedEconomyReplication.DecayCalls;checkpoint=PlayerAccountRuntime.CaptureLedger(10);Check(SharedEconomyReplication.DecayCalls==calls,"capture after native snapshot performs no native decay");
   Throws(()=>PlayerAccountRuntime.WithAccount(new string('d',32)),"unknown account rejected");
   using(PlayerAccountRuntime.WithAccount(A)){Money(123);PlayerAccountRuntime.Fault("partial transaction");}
   Check(Money()==0&&PlayerAccountRuntime.FailClosed,"partial failure restores neutral and latches fault");
   Throws(()=>PlayerAccountRuntime.CaptureLedger(11),"partial failure cannot checkpoint");
   Check(ledger.Accounts[0].Money==40,"failed operation not captured into ledger");
   PlayerAccountRuntime.Reset();SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState());PlayerAccountRuntime.InitializeWorker(checkpoint);
   Check(PlayerAccountRuntime.LegacyEscrowMoney==850&&!PlayerAccountRuntime.IsDirty,"restart keeps escrow and neutral checkpoint");
   int grants=SharedEconomyReplication.DefaultsCreated;var restored=PlayerAccountRuntime.EnsureAccount(Prep(A));Check(SharedEconomyReplication.DefaultsCreated==grants&&restored.Money==40,"restart preserves existing balance and never grants again");
   var crossThread=System.Threading.Tasks.Task.Run(()=>{try{PlayerAccountRuntime.GetAccount(A);return false;}catch(System.InvalidOperationException){return true;}}).GetAwaiter().GetResult();Check(crossThread,"no native account operations on background thread");
   Money(1);Throws(()=>PlayerAccountRuntime.Tick(),"unattributed wallet drift faults");Check(PlayerAccountRuntime.FailClosed,"drift latch");
   PlayerAccountRuntime.Reset();SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState());PlayerAccountRuntime.InitializeWorker(checkpoint);
   SharedEconomyReplication.ThrowApply=true;Throws(()=>PlayerAccountRuntime.WithAccount(A),"native apply failure propagates");Check(PlayerAccountRuntime.FailClosed&&PlayerAccountRuntime.CurrentAccountId==""&&Money()==0,"failed activation cannot leave active context");
   PlayerAccountRuntime.Reset();SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState());PlayerAccountRuntime.InitializeWorker(checkpoint);
   var outer=PlayerAccountRuntime.WithAccount(A);var inner=PlayerAccountRuntime.WithAccount(B);Throws(()=>outer.Dispose(),"out-of-order scope fails");inner.Dispose();outer.Dispose();Check(Money()==0&&PlayerAccountRuntime.FailClosed,"out-of-order still permits cleanup, never commit");
   PlayerAccountRuntime.Reset();DedicatedWorldRuntime.IsDedicatedServer=false;PlayerAccountRuntime.ApplyClientAccount(checkpoint.Accounts[0]);Check(Money()==40,"private client apply uses exact account balance");
   Throws(()=>PlayerAccountRuntime.InitializeWorker(checkpoint),"client cannot initialize worker authority");
   PlayerAccountRuntime.Reset();DedicatedWorldRuntime.IsDedicatedServer=true;
   CheckSharedStarterRent();
   return checks;
  }
 }
}
