$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$npcPath=Join-Path $devRoot 'Source/WorkerNpcSimulation.cs'
$mapPath=Join-Path $devRoot 'Source/PlayerMapRuntime.cs'
$npc=[IO.File]::ReadAllText($npcPath)
$npcTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/NpcWorldReplication.cs'))).GetRoot()
$limiter=($npcTree.DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'NpcDiscontinuityLog'} | Select-Object -First 1).ToFullString()
$mapTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($mapPath)).GetRoot()
$occupied=($mapTree.DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsSceneOccupied'} | Select-Object -First 1).ToFullString()
$sceneInit=($mapTree.DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'InitializeSceneNames'} | Select-Object -First 1).ToFullString()
$stubs=@'
using System;
using System.Collections.Generic;
using System.Reflection;
using Nivalis;
namespace HarmonyLib {
 public sealed class Harmony {public static readonly List<MethodInfo> Patched=new List<MethodInfo>();public Harmony(string id){} public void Patch(MethodInfo original,HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod finalizer=null){if(original==null)throw new Exception("Missing hook");Patched.Add(original);} }
 public sealed class HarmonyMethod {public HarmonyMethod(MethodInfo info){} }
}
namespace Il2CppSystem { public class Object {private static int next;public IntPtr Pointer=(IntPtr)(++next);public Type GetIl2CppType()=>GetType();public T TryCast<T>() where T:class=>this as T; } }
namespace Il2CppSystem.Collections.Generic {public class List<T>:System.Collections.Generic.List<T>{}}
namespace UnityEngine {
 public static class Time {public static double realtimeSinceStartupAsDouble=1;}
 public static class Random {public static int Range(int min,int max)=>min;}
 public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public static Vector3 zero=>default;public float sqrMagnitude=>x*x+y*y+z*z;public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
}
namespace Nivalis {
 public struct PositionRotation {public UnityEngine.Vector3 Position;}
 public sealed class Character:Il2CppSystem.Object {public bool Snap=true;public CharacterMovement Movement=new CharacterMovement();public GhostSystem.Ai.Agent Agent;public void MoveToSpot(GhostSystem.Ai.MoveToSpotCommand command){}}
 public sealed class CharacterMovement {public float PathProgress;}
 public sealed class PortalKey { public int SceneIndex;public string SceneName; }
 public sealed class TravelUnlockManager {public static TravelUnlockManager Instance=new TravelUnlockManager();public static bool InstanceNotNull()=>Instance!=null;public List<PortalKey> _allPortalKeys=new List<PortalKey>();}
}
namespace Nivalis.GhostSystem {
 public class Ghost:Il2CppSystem.Object {public string Id=Guid.NewGuid().ToString();public int SceneIndex=2;public bool IsValid=true,IsViewLess;public PositionRotation Transform;public GhostReference<Ghost> Parent; }
 public class WorldPointGhost:Ghost {}
 public class IdlePoint {public class IdlePointGhost:Ghost {}}
 public static class GhostManager {public static void TransferGhostToScene(Ghost ghost,int scene,PositionRotation transform){} public static void TransferGhostToScene(Ghost ghost,int scene){} }
 public sealed class GhostReference<T> {public string ReferencedId;public T Value;public GhostReference(T value){Value=value;ReferencedId=((Ghost)(object)value).Id;} }
 public sealed class WanderPoint {public sealed class WanderPointGhost:Ghost {
  public List<GhostReference<WanderPointGhost>> Neighbours=new List<GhostReference<WanderPointGhost>>();public List<GhostReference<WanderPointGhost>> TeleportNeighbours=new List<GhostReference<WanderPointGhost>>();public int Traffic,TrafficLimit=1;public bool FailFill;public int LastMinConnections=-1;public float LastThreshold;
  public void FillNextTeleportLocations(){} public void FillNextTeleportLocations(Il2CppSystem.Collections.Generic.List<WanderPointGhost> output,UnityEngine.Vector3 direction,float threshold){}
  public void FillNextLocations(Il2CppSystem.Collections.Generic.List<WanderPointGhost> output,UnityEngine.Vector3 direction,float threshold,int minConnections){LastMinConnections=minConnections;LastThreshold=threshold;if(FailFill)throw new Exception("native fill failed");foreach(var entry in Neighbours){var p=entry?.Value;if(p!=null&&p.IsValid&&p.Neighbours!=null&&p.Neighbours.Count>=minConnections&&p.Traffic<p.TrafficLimit)output.Add(p);}}
 } }
}
namespace Nivalis.GhostSystem.Ai {
 public sealed class AgentGhost:Nivalis.GhostSystem.Ghost {public UnityEngine.Vector3 Position{get=>Transform.Position;set{var p=Transform;p.Position=value;Transform=p;}}public bool Visible{get;set;}public bool HasLoadedView;public Il2CppSystem.Object LoadedView;public AgentAction CurrentAction;public ActionState CurrentActionState;public CharacterCommand CharacterCommand;public AgentState State=new AgentState();}
 public sealed class Agent:Il2CppSystem.Object {public AgentGhost MyGhost;public Nivalis.Character Character;public void OnDisable(){}public sealed class _SnapToGhostCoroutine_d__31 {public Agent __4__this;public bool MoveNext()=>false;}}
 public class PersonSchedule {public bool teleport;}
 public class AgentState {public PersonSchedule personSchedule;}
 public class CharacterCommand:Il2CppSystem.Object {public int Result;public float SimulateTimer=1;}
 public class MoveToSpotCommand:CharacterCommand {public Nivalis.GhostSystem.GhostReference<Nivalis.GhostSystem.Ghost> ghostReference;public UnityEngine.Vector3 FromPosition;public float PathProgress;public bool VerticalMultiplierCalculated;}
 public class ActionState:Nivalis.GhostSystem.CustomerLoop.ITeleportingActionState {public CharacterCommand CharacterCommand;public Nivalis.GhostSystem.GhostReference<Nivalis.GhostSystem.Ghost> ActionSource;public int Result,SubActionIndex,GroupedActionIndex;}
 public class IAgentInteractable:Il2CppSystem.Object{}
 public class SleepAgentAction {
  public bool TryGetValidInteractable(AgentGhost agent,ref IAgentInteractable target,PersonSchedule schedule)=>false;
  public class State:ActionState{}
  public class SleepSubAction {public void Enter(AgentGhost agent,State state){}public void Exit(AgentGhost agent,State state){}}
 }
 public class AgentActionType:Il2CppSystem.Object {public bool CanBeInterrupted=true,Requirements=true;public int RequirementChecks;public bool MeetsRequirements(AgentGhost agent,object schedule){RequirementChecks++;return Requirements;}}
 public class IdleAgentAction {public class State:ActionState{}public class TeleportIfScheduleDemandsIt{public void Enter(AgentGhost agent,State state){}}}
 public class AgentAction:Il2CppSystem.Object {public AgentActionType Type=new AgentActionType();public bool Exiting;public int TotalActionCount=1;public Il2CppSystem.Object GetSubAction(int index)=>new Il2CppSystem.Object();public bool IsExiting(AgentGhost agent)=>Exiting;}
 public class AgentGhostSimulator {public void SkipTime(float seconds,bool updateSimulators){} public void SkipTime(float seconds){}public void UpdateCurrentAgentAction(AgentGhost agent,float delta){} }
 public class AgentSubAction {public void Tick(AgentGhost agent,ActionState state,float delta){} }
 public sealed class IWorldLocation {public IntPtr Pointer;public int SceneIndex;public IWorldLocation(Nivalis.GhostSystem.Ghost value){Pointer=value.Pointer;SceneIndex=value.SceneIndex;} }
 public sealed class WanderAgentAction {
  public static Nivalis.GhostSystem.WanderPoint.WanderPointGhost NextWaypoint(AgentGhost agent,Nivalis.GhostSystem.WanderPoint.WanderPointGhost previous)=>previous;
  public sealed class State:ActionState {public Nivalis.GhostSystem.GhostReference<Nivalis.GhostSystem.WanderPoint.WanderPointGhost> destination;}
  public sealed class WanderToSubAction:AgentSubAction {public void GiveViewMoveCommand(AgentGhost agent,State state,IWorldLocation location,bool teleport){} public void Tick(AgentGhost agent,State state,float delta){} }
 }
}
namespace Nivalis.Apartment { public class ApartmentAreaGhost:Nivalis.GhostSystem.Ghost {} }
namespace Nivalis.GhostSystem.CustomerLoop {
 public class ITeleportingActionState:Il2CppSystem.Object {public int TargetSceneIndex=2;public bool ApartmentTransition;public Nivalis.GhostSystem.GhostReference<Nivalis.GhostSystem.Ghost> Target;}
 public class VisitVenueAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class Teleport{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class DeliverIngredientsActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class IdleAtWorkplaceActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class CleanupMealAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class CleanupStorageAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class CleanupTrashAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class MakeMealAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class ManageAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class ManageComplaintAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class ProcessIngredientAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class ServeMealAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
 public class TakeOrderAgentActionType {public class State:Nivalis.GhostSystem.Ai.ActionState{}public class TeleportIfNotInScene{public void Enter(Nivalis.GhostSystem.Ai.AgentGhost agent,State state){}}}
}
namespace NivalisTogether.Dedicated {
 public static class DedicatedWorldRuntime {public static bool IsDedicatedServer=true,IsIsolated=true;}
 public sealed class Context {public string Scene;public bool TravelPending;}
 public sealed class Entry {public Context Context;public string UntrustedPoseScene;}
 public static class PlayerMapRuntime {
  public static bool initialized=true;public static object emit=new object(),reject=new object();
  public static readonly Dictionary<string,Entry> Players=new Dictionary<string,Entry>();
  public static readonly Dictionary<int,string> SceneNames=new Dictionary<int,string>();
  public static void InitScenes()=>InitializeSceneNames();
'@
$stubs += $occupied + $sceneInit + '}' + $limiter + '}'
$tests=@'
using System;
using System.Collections.Generic;
using System.Reflection;
using WG=Nivalis.GhostSystem.WanderPoint.WanderPointGhost;
using Agent=Nivalis.GhostSystem.Ai.AgentGhost;
using Loc=Nivalis.GhostSystem.Ai.IWorldLocation;
using State=Nivalis.GhostSystem.Ai.WanderAgentAction.State;
using ActionState=Nivalis.GhostSystem.Ai.ActionState;
using MoveCommand=Nivalis.GhostSystem.Ai.MoveToSpotCommand;
using VisitState=Nivalis.GhostSystem.CustomerLoop.VisitVenueAgentActionType.State;
using DeliveryState=Nivalis.GhostSystem.CustomerLoop.DeliverIngredientsActionType.State;
using SleepState=Nivalis.GhostSystem.Ai.SleepAgentAction.State;
using Ghost=Nivalis.GhostSystem.Ghost;
using NivalisTogether.Dedicated;
public static class WorkerNpcTests {
 static readonly Type Hooks=typeof(WorkerNpcSimulation);
 sealed class ResumeFixture {
  public Agent Ghost=new Agent();public Nivalis.GhostSystem.Ai.Agent View=new Nivalis.GhostSystem.Ai.Agent();public Nivalis.Character Character=new Nivalis.Character();
  public Ghost Target=new WG();public MoveCommand Command;public Nivalis.GhostSystem.Ai.Agent._SnapToGhostCoroutine_d__31 Coroutine;
  public ResumeFixture(){
   Ghost.Position=new UnityEngine.Vector3(-33.450928f,-.74845153f,-15.587059f);
   Command=new MoveCommand{FromPosition=new UnityEngine.Vector3(-28.639477f,-.7180023f,-6.407457f),PathProgress=.5227256f,SimulateTimer=.73f,VerticalMultiplierCalculated=true,ghostReference=new Nivalis.GhostSystem.GhostReference<Ghost>(Target)};
   Ghost.CharacterCommand=Command;Ghost.CurrentActionState=new ActionState{CharacterCommand=Command};Ghost.CurrentAction=new Nivalis.GhostSystem.Ai.AgentAction();
   View.MyGhost=Ghost;View.Character=Character;Character.Agent=View;Character.Movement.PathProgress=.7f;Ghost.HasLoadedView=true;Ghost.LoadedView=View;
   Coroutine=new Nivalis.GhostSystem.Ai.Agent._SnapToGhostCoroutine_d__31{__4__this=View};
  }
  public void Mark(bool more=false)=>Call("AfterViewResume",Coroutine,more);
  public void Apply()=>Call("BeforeResumedMove",Character,Command);
 }
 static object Call(string method,params object[] args)=>Hooks.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
 static object Begin(Agent agent,WG from,out bool allow,out WG result){object[] args={agent,from,null,null};allow=(bool)Call("BeforeNextWaypoint",args);result=(WG)args[2];return args[3];}
 static WG Finish(WG candidate,object scope){object[] args={candidate,scope};Call("AfterNextWaypoint",args);return (WG)args[0];}
 static void End(object scope,Exception error=null){object retained=Call("EndNextWaypoint",error,scope);if(!ReferenceEquals(retained,error))throw new Exception("Native error must remain unchanged");}
 static bool CanAppend(WG from)=>(bool)Call("AllowTeleportCandidates",from);
 static object BeginTick(Agent agent,State state){object[] args={agent,state,null};Call("BeforeWanderTick",args);return args[2];}
 static void EndTick(object scope,Exception error=null){if(!ReferenceEquals(Call("EndWanderTick",error,scope),error))throw new Exception("Tick native error changed");}
 static bool Give(Agent agent,WG target,bool teleport=true,int? scene=null){object[] args={agent,new State{TargetSceneIndex=scene??agent.SceneIndex},new Loc(target),teleport};Call("BeforeGiveMoveCommand",args);return (bool)args[3];}
 static void Link(WG from,WG to){from.Neighbours.Add(new Nivalis.GhostSystem.GhostReference<WG>(to));}
 static object BeginMove(Agent agent,ActionState state,bool visit=true){object[] args={agent,state,null};Call(visit?"BeforeVisitMove":"BeforeDeliveryMove",args);return args[2];}
 static object BeginIdle(Agent agent,ActionState state,bool workplace){object[] args={agent,state,null};Call(workplace?"BeforeWorkplaceMove":"BeforeIdleMove",args);return args[2];}
 static bool ForcedVisible(Agent agent){object[] args={agent,false};bool run=(bool)Call("ObservedMoveVisibility",args);return !run&&(bool)args[1];}
 static void EndMove(object scope,Exception error=null){if(!ReferenceEquals(Call("EndObservedMove",error,scope),error))throw new Exception("Enter exception changed");}
 static bool WaitMove(Agent agent)=>(bool)Call("WaitForObservedMove",agent);
 static object BeginSleep(Agent agent){object[] args={agent,null};Call("BeforeSleepSelection",args);return args[1];}
 static void EndSleep(object scope,Exception error=null){if(!ReferenceEquals(Call("EndSleepSelection",error,scope),error))throw new Exception("Sleep selection exception changed");}
 static object TraceSleep(Agent agent,SleepState state,string stage){object[] args={agent,state,typeof(Nivalis.GhostSystem.Ai.SleepAgentAction.SleepSubAction).GetMethod(stage),null};Call("BeforeSleepTransition",args);return args[3];}
 static MoveCommand PrepareMove(Agent agent,ActionState state,Ghost target,bool visit=true){
  agent.CurrentActionState=state;agent.CurrentAction=new Nivalis.GhostSystem.Ai.AgentAction();
  object ms=BeginMove(agent,state,visit);if(!ForcedVisible(agent))throw new Exception("Expected scoped visibility in qualified native Enter");
  var cmd=new MoveCommand{ghostReference=new Nivalis.GhostSystem.GhostReference<Ghost>(target)};state.CharacterCommand=cmd;Call("AfterObservedMove",ms);EndMove(ms);return cmd;
 }
 public static int Run(){int checks=0;void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
  var travel=Nivalis.TravelUnlockManager.Instance;travel._allPortalKeys.Add(new Nivalis.PortalKey{SceneIndex=2,SceneName="Meridian"});travel._allPortalKeys.Add(new Nivalis.PortalKey{SceneIndex=3,SceneName="Docks"});
  PlayerMapRuntime.InitScenes();
  PlayerMapRuntime.Players.Add("one",new Entry{Context=new Context{Scene="Meridian",TravelPending=true},UntrustedPoseScene="Docks"});
  Check(PlayerMapRuntime.IsSceneOccupied(2),"Authenticated restoration protects its assigned map before first pose");
  Check(!PlayerMapRuntime.IsSceneOccupied(3),"Untrusted player pose cannot claim another map");
  Check(!PlayerMapRuntime.IsSceneOccupied(-1)&&!PlayerMapRuntime.IsSceneOccupied(999),"Invalid/unmapped scenes are not occupied");
  DedicatedWorldRuntime.IsDedicatedServer=false;Check(!PlayerMapRuntime.IsSceneOccupied(2),"Client/solo cannot acquire worker occupancy");DedicatedWorldRuntime.IsDedicatedServer=true;
  DedicatedWorldRuntime.IsIsolated=false;Check(!PlayerMapRuntime.IsSceneOccupied(2),"Nonisolated runtime remains untouched");DedicatedWorldRuntime.IsIsolated=true;
  PlayerMapRuntime.initialized=false;Check(!PlayerMapRuntime.IsSceneOccupied(2),"Uninitialized assignments cannot enable intervention");PlayerMapRuntime.initialized=true;
  var oldManager=travel;Nivalis.TravelUnlockManager.Instance=new Nivalis.TravelUnlockManager();Check(PlayerMapRuntime.IsSceneOccupied(2),"Frequent occupancy checks use initialized scalar scene metadata without native manager access");PlayerMapRuntime.InitScenes();Check(!PlayerMapRuntime.IsSceneOccupied(2),"World initialization replaces previous scalar scene metadata");Nivalis.TravelUnlockManager.Instance=oldManager;PlayerMapRuntime.InitScenes();
  var agent=new Agent();var from=new WG();var normal=new WG();var shortcut=new WG();Link(from,normal);
  from.TeleportNeighbours.Add(new Nivalis.GhostSystem.GhostReference<WG>(shortcut));
  var logs=new List<string>();WorkerNpcSimulation.Diagnostic=logs.Add;WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  Check(HarmonyLib.Harmony.Patched.Count==27,"Actual InstallPatches resolves all hooks despite overloads and inherited methods");
  foreach(var hook in HarmonyLib.Harmony.Patched){
   if(hook.Name=="Tick")Check(hook.DeclaringType==typeof(Nivalis.GhostSystem.Ai.WanderAgentAction.WanderToSubAction)&&hook.GetParameters()[1].ParameterType==typeof(State),"Actual installation selects the concrete State Tick, not inherited ActionState overload");
   if(hook.Name=="TransferGhostToScene")Check(hook.GetParameters().Length==3,"Actual installation selects the full-pose transfer overload");
   if(hook.Name=="SkipTime")Check(hook.GetParameters().Length==2,"Actual installation selects the boolean simulator-control overload");
  }
  object scope=Begin(agent,from,out bool allow,out WG result);
  Check(allow&&scope!=null,"Observed worker next-point selection keeps native algorithm running");
  Check(!CanAppend(from),"Only teleport candidate append is suppressed inside protected selection");
  Check(CanAppend(new WG()),"Unrelated waypoint traversal is not changed by outer selection");
  Check(Finish(normal,scope)==normal,"An authored normal neighbour survives unchanged");
  End(scope);Check(CanAppend(from),"Teleport candidate append restores after normal return");
  Check(!Give(agent,normal,false),"Native ordinary movement command is left ordinary");
  Check(Give(agent,normal),"Consumed edge cannot alter a later unrelated command");
  scope=Begin(agent,from,out allow,out result);Check(Finish(shortcut,scope)==normal,"Teleport-only closest-node fallback retries an actual regular edge");End(scope);
  Check(from.LastMinConnections==0&&from.LastThreshold==-1f,"Retry lifts only degree/direction preference through native FillNextLocations");
  Check(Give(agent,shortcut),"Rejected fallback never fabricates an arbitrary straight-line movement command");
  var noNeighbours=new WG();scope=Begin(agent,noNeighbours,out allow,out result);Check(Finish(noNeighbours,scope)==null,"Isolated point fails/reselects instead of a self-loop that never progresses");End(scope);
  scope=Begin(agent,from,out allow,out result);Check(Finish(null,scope)==normal,"Empty native result may retry a regular edge with fewer connections");End(scope);
  var otherScene=new WG{SceneIndex=3};Link(from,otherScene);scope=Begin(agent,from,out allow,out result);Check(Finish(otherScene,scope)==normal,"Retry discards cross-scene fallback and uses a same-map normal edge");End(scope);
  from.TeleportNeighbours.Add(new Nivalis.GhostSystem.GhostReference<WG>(normal));
  scope=Begin(agent,from,out allow,out result);Check(Finish(normal,scope)==normal,"A link listed in both sets remains a real normal edge");End(scope);
  Check(!Give(agent,normal),"Exact dual-listed normal edge selects native MoveToSpot construction rather than native teleport");
  scope=Begin(agent,from,out allow,out result);Finish(normal,scope);End(scope);Check(Give(agent,shortcut),"A different destination cannot consume a normal edge decision as permission");
  scope=Begin(agent,from,out allow,out result);Finish(normal,scope);End(scope);Check(Give(agent,normal,true,3),"Changing action target scene cannot reuse a same-map decision");
  scope=Begin(agent,from,out allow,out result);Finish(normal,scope);End(scope);agent.Id="reused-native-pointer";Check(Give(agent,normal),"A replacement ghost identity cannot inherit a previous pointer's edge");
  scope=Begin(agent,from,out allow,out result);Finish(normal,scope);End(scope);from.Neighbours.Clear();Check(Give(agent,normal),"Removed normal edge cannot override a later native command");Link(from,normal);
  scope=Begin(agent,from,out allow,out result);Finish(normal,scope);End(scope);PlayerMapRuntime.Players.Clear();Check(Give(agent,normal),"Disconnect restores native offstage behavior without stale occupancy");
  scope=Begin(agent,from,out allow,out result);Check(scope==null&&allow&&CanAppend(from),"Unoccupied map leaves all native selection paths unchanged");End(scope);
  PlayerMapRuntime.Players.Add("two",new Entry{Context=new Context{Scene="Meridian"}});
  scope=Begin(agent,from,out allow,out result);var away=new Agent{SceneIndex=3};object nested=Begin(away,from,out allow,out result);Check(CanAppend(from),"Nested unobserved actor temporarily suspends the outer actor's scope");End(nested);Check(!CanAppend(from),"Outer protected scope resumes after nested selection");End(scope,new Exception("native failure"));Check(CanAppend(from),"Exceptional native exit also clears visibility intervention");
  DedicatedWorldRuntime.IsDedicatedServer=false;scope=Begin(agent,from,out allow,out result);Check(allow&&scope==null&&CanAppend(from)&&Give(agent,normal),"Client/solo selection and movement never inherit worker changes");End(scope);DedicatedWorldRuntime.IsDedicatedServer=true;
  WorkerNpcSimulation.Configure(_=>throw new Exception("bad context"));scope=Begin(agent,from,out allow,out result);Check(!allow&&result==null&&scope==null&&CanAppend(from),"Occupancy failure follows native no-next-point path without leaving a scope active");End(scope);
  WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);scope=Begin(agent,from,out allow,out result);Finish(normal,scope);End(scope);WorkerNpcSimulation.Reset();Check(Give(agent,normal)&&CanAppend(from),"Session reset clears pending edge decisions and all intervention");
  WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  // Drive the production selection/command guards through multiple normal
  // waypoints. The lightweight native facade distinguishes MoveToSpot from
  // teleport and completes by real bounded progress, not an invented ACK.
  var points=new[]{new WG(),new WG(),new WG()};Link(points[0],points[1]);Link(points[1],points[2]);int completed=0;agent.Position=UnityEngine.Vector3.zero;
  for(int i=0;i<2;i++){
   scope=Begin(agent,points[i],out allow,out result);result=Finish(points[i+1],scope);End(scope);Check(result==points[i+1],"Normal route chooses the next distinct waypoint");
   bool teleport=Give(agent,result,false);float target=(i+1)*5;Check(!teleport,"Normal route remains a walking command");
   float before=agent.Position.x;for(int step=0;step<50;step++){float prior=agent.Position.x;agent.Position=new UnityEngine.Vector3(Math.Min(target,agent.Position.x+.1f),0,0);Check(agent.Position.x-prior<=.10001f,"Movement stays bounded while native command progresses");}
   Check(agent.Position.x>before&&Math.Abs(agent.Position.x-target)<.0001f,"Ordinary command completes and advances rather than remaining frozen");completed++;
  }
  Check(completed==2,"Native route can complete multiple sequential waypoints");
  var deadEnd=new WG();var leaf=new WG();Link(deadEnd,leaf);Link(leaf,deadEnd);
  scope=Begin(agent,deadEnd,out allow,out result);Check(Finish(deadEnd,scope)==leaf,"A degree-one normal neighbour prevents persistent closest-point retry");End(scope);
  leaf.Traffic=leaf.TrafficLimit;scope=Begin(agent,deadEnd,out allow,out result);Check(Finish(deadEnd,scope)==null,"Native traffic capacity remains enforced when retrying a degree-one route");End(scope);leaf.Traffic=0;
  var onlyCross=new WG();Link(onlyCross,otherScene);scope=Begin(agent,onlyCross,out allow,out result);Check(Finish(otherScene,scope)==null,"A graph containing only other-map neighbours cannot authorize straight-line travel");End(scope);
  Link(noNeighbours,noNeighbours);scope=Begin(agent,noNeighbours,out allow,out result);Check(Finish(noNeighbours,scope)==null,"Self-authored references cannot become a nonprogressing retry");End(scope);noNeighbours.Neighbours.Clear();
  // Model the audited native reservation order: Tick --old, NextWaypoint,
  // then either ++new or failure retaining old; Exit always --destination.
  noNeighbours.Traffic=4;var state=new State{destination=new Nivalis.GhostSystem.GhostReference<WG>(noNeighbours)};
  object ts=BeginTick(agent,state);noNeighbours.Traffic--;
  scope=Begin(agent,noNeighbours,out allow,out result);Check(Finish(noNeighbours,scope)==null&&noNeighbours.Traffic==4,"Failed Tick restores exactly its prior release before native Exit");
  Check(Finish(null,scope)==null&&noNeighbours.Traffic==4,"Repeated failure cannot restore a reservation twice");End(scope);EndTick(ts);noNeighbours.Traffic--;
  Check(noNeighbours.Traffic==3,"Native Exit releases once overall, preserving other NPC reservations");
  for(int repeat=0;repeat<20;repeat++){
   noNeighbours.Traffic++; // Native Enter reserves once for this action.
   ts=BeginTick(agent,state);noNeighbours.Traffic--;scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);EndTick(ts);noNeighbours.Traffic--;
   Check(noNeighbours.Traffic==3,"Repeated failed actions cannot drive shared traffic negative");
  }
  scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);Check(noNeighbours.Traffic==3,"Initial Enter selection with a previous point cannot invent a Tick reservation");
  ts=BeginTick(agent,state);scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);EndTick(ts);Check(noNeighbours.Traffic==3,"No native release means no compensation");
  state.destination=new Nivalis.GhostSystem.GhostReference<WG>(deadEnd);deadEnd.Traffic=2;ts=BeginTick(agent,state);deadEnd.Traffic--;
  scope=Begin(agent,deadEnd,out allow,out result);result=Finish(deadEnd,scope);End(scope);Check(result==leaf&&deadEnd.Traffic==1,"Successful degree-one retry retains the native old-node release");
  result.Traffic++;state.destination=new Nivalis.GhostSystem.GhostReference<WG>(result);EndTick(ts);result.Traffic--;
  Check(deadEnd.Traffic==1&&leaf.Traffic==0,"Successful retry transfers then releases the correct reservation");
  state.destination=new Nivalis.GhostSystem.GhostReference<WG>(noNeighbours);noNeighbours.Traffic=2;ts=BeginTick(agent,state);noNeighbours.Traffic--;
  var replacement=new WG{Traffic=8};state.destination=new Nivalis.GhostSystem.GhostReference<WG>(replacement);
  scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);EndTick(ts);Check(noNeighbours.Traffic==1&&replacement.Traffic==8,"Changed destination reference cannot recover another action's reservation");
  state.destination=new Nivalis.GhostSystem.GhostReference<WG>(noNeighbours);noNeighbours.Traffic=2;ts=BeginTick(agent,state);noNeighbours.Traffic--;
  object nestedTick=BeginTick(new Agent{SceneIndex=3},new State());scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);Check(noNeighbours.Traffic==1,"Nested unrelated Tick masks outer reservation ownership");EndTick(nestedTick);
  scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);EndTick(ts);Check(noNeighbours.Traffic==2,"Exact outer Tick ownership resumes after nested Tick returns");
  noNeighbours.Traffic=2;ts=BeginTick(agent,state);noNeighbours.Traffic--;noNeighbours.FailFill=true;scope=Begin(agent,noNeighbours,out allow,out result);Check(Finish(noNeighbours,scope)==null&&noNeighbours.Traffic==2,"Native retry exception still compensates only the audited release");End(scope);EndTick(ts,new Exception("native error"));noNeighbours.FailFill=false;
  noNeighbours.Traffic=3;scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);Check(noNeighbours.Traffic==3,"Exceptional Tick finalizer clears reservation scope");
  state.CharacterCommand=new Nivalis.GhostSystem.Ai.CharacterCommand{Result=0};ts=BeginTick(agent,state);Check(ts==null,"Pending command does not allocate a selection scope");noNeighbours.Traffic--;scope=Begin(agent,noNeighbours,out allow,out result);Finish(noNeighbours,scope);End(scope);EndTick(ts);Check(noNeighbours.Traffic==2,"Pending command cannot claim a completed-command selection release");state.CharacterCommand=null;
  ts=BeginTick(new Agent{SceneIndex=3},state);Check(ts==null,"Unoccupied NPC route ticks do not allocate selection scopes");EndTick(ts);
  noNeighbours.Traffic=3;ts=BeginTick(agent,state);noNeighbours.Traffic--;WorkerNpcSimulation.Reset();scope=Begin(agent,noNeighbours,out allow,out result);Check(Finish(noNeighbours,scope)==noNeighbours&&noNeighbours.Traffic==2,"Reset clears outstanding reservation intervention");End(scope);EndTick(ts);WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  // Low-frequency transfer diagnostics are observational, separately bounded
  // from route notices, and include the current native action/subaction.
  logs.Clear();agent.Transform=new Nivalis.PositionRotation{Position=new UnityEngine.Vector3(0,0,0)};agent.CurrentAction=new Nivalis.GhostSystem.Ai.AgentAction();agent.CurrentActionState=new State{SubActionIndex=0};
  var pose=new Nivalis.PositionRotation{Position=new UnityEngine.Vector3(50,0,0)};
  for(int i=0;i<8;i++)Call("ObserveTransfer",agent,2,pose);
  Check(logs.Count==5&&logs[0].Contains("NPC_NATIVE_TRANSFER attempt")&&logs[0].Contains("subIndex=0"),"Native transfer trace is bounded and identifies action/subaction");
  Check(agent.SceneIndex==2&&agent.Transform.Position.x==0,"Transfer diagnostics cannot alter the native ghost or requested transfer");
  UnityEngine.Time.realtimeSinceStartupAsDouble+=11;Call("ObserveTransfer",agent,2,pose);Check(logs.Count==6&&logs[5].Contains("suppressed=3"),"Transfer trace reports suppressed events after its bounded window");
  Call("ObserveTransfer",agent,2,new Nivalis.PositionRotation{Position=new UnityEngine.Vector3(1,0,0)});Check(logs.Count==6,"Ordinary native moves do not produce discontinuity trace");
  DedicatedWorldRuntime.IsDedicatedServer=false;Call("ObserveTransfer",agent,2,pose);Call("ObserveSkipTime",500f,true);Check(logs.Count==6,"Client/solo transfers and time skipping are not instrumented");DedicatedWorldRuntime.IsDedicatedServer=true;
  // Execute the actual qualified Enter scope and wait gate. The facade below
  // stands in only for the existing native MoveToSpot construction/simulator.
  var walker=new Agent();var venuePoint=new WG();var visit=new VisitState{Target=new Nivalis.GhostSystem.GhostReference<Ghost>(venuePoint)};
  var move=PrepareMove(walker,visit,venuePoint);
  Check(!ForcedVisible(walker)&&!walker.Visible,"Native offstage visibility resumes immediately after Enter");
  for(int step=0;step<20;step++){
   Check(!WaitMove(walker),"Qualified pending native move waits before teleport subaction can shortcut");
   var p=walker.Transform;p.Position.x+=.5f;walker.Transform=p;
  }
  Check(walker.Transform.Position.x==10&&walker.CurrentAction.Type.RequirementChecks==20,"Native simulator can progress while interruption requirements remain checked");
  move.Result=1;Check(WaitMove(walker),"Successful native command resumes ordinary subaction completion");move.Result=0;Check(WaitMove(walker),"Completed command cannot reactivate stale tracking");
  var deliveryPoint=new Nivalis.GhostSystem.WorldPointGhost();var delivery=new DeliveryState{Target=new Nivalis.GhostSystem.GhostReference<Ghost>(deliveryPoint)};
  move=PrepareMove(walker,delivery,deliveryPoint,false);Check(!WaitMove(walker),"Delivery uses the same qualified native WorldPoint movement lifecycle");move.Result=2;Check(WaitMove(walker),"Failed native command returns control to native failure processing");
  // Actual daytime regression: a public same-scene visit retains the native
  // apartment comparison bit while the worker changes its own context. Both
  // already-visible and offstage actors must finish their native path command.
  foreach(bool visible in new[]{false,true})foreach(bool isVisit in new[]{true,false}){
   walker.Visible=visible;ActionState daytime=isVisit?(ActionState)new VisitState{Target=visit.Target,ApartmentTransition=true}:new DeliveryState{Target=delivery.Target,ApartmentTransition=true};
   Ghost endpoint=isVisit?(Ghost)venuePoint:deliveryPoint;
   move=PrepareMove(walker,daytime,endpoint,isVisit);
   Check(daytime.ApartmentTransition&&walker.Visible==visible,"Public movement preserves native apartment bit and actual visibility");
   for(int step=0;step<3;step++){Check(!WaitMove(walker),"Daytime public native command survives stale apartment comparison");var p=walker.Transform;p.Position.x+=.5f;walker.Transform=p;}
   daytime.ApartmentTransition=false;Check(!WaitMove(walker),"Changing worker apartment comparison does not discard exact public command");
   daytime.ApartmentTransition=true;move.Result=1;Check(WaitMove(walker),"Daytime public command resumes native completion exactly once");Check(WaitMove(walker),"Completed daytime command is no longer intercepted");
  }
  walker.Visible=false;
  Action<ActionState> AssertExcluded=s=>{object ms=BeginMove(walker,s,s is VisitState);Check(!ForcedVisible(walker),"Unqualified movement never changes visibility");EndMove(ms);};
  AssertExcluded(new VisitState{Target=visit.Target,TargetSceneIndex=3});
  AssertExcluded(new VisitState{Target=visit.Target,TargetSceneIndex=-1,ApartmentTransition=true});
  var privateArea=new Nivalis.Apartment.ApartmentAreaGhost();
  walker.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(privateArea);AssertExcluded(new VisitState{Target=visit.Target,ApartmentTransition=true});walker.Parent=null;
  venuePoint.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(privateArea);AssertExcluded(new VisitState{Target=visit.Target,ApartmentTransition=true});venuePoint.Parent=null;
  var nestedPrivate=new Ghost{Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(privateArea)};
  venuePoint.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(nestedPrivate);AssertExcluded(visit);venuePoint.Parent=null;
  walker.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Ghost());walker.Parent.Value=null;AssertExcluded(visit);walker.Parent=null;
  venuePoint.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(venuePoint);AssertExcluded(visit);venuePoint.Parent=null;
  var cycleA=new Ghost();var cycleB=new Ghost();cycleA.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(cycleB);cycleB.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(cycleA);
  walker.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(cycleA);AssertExcluded(visit);walker.Parent=null;
  AssertExcluded(new VisitState{Target=new Nivalis.GhostSystem.GhostReference<Ghost>(deliveryPoint)});
  AssertExcluded(new DeliveryState{Target=visit.Target});
  AssertExcluded(new VisitState());
  venuePoint.SceneIndex=3;AssertExcluded(visit);venuePoint.SceneIndex=2;
  walker.IsViewLess=true;AssertExcluded(visit);walker.IsViewLess=false;
  walker.State.personSchedule=new Nivalis.GhostSystem.Ai.PersonSchedule{teleport=true};AssertExcluded(visit);walker.State.personSchedule=null;
  DedicatedWorldRuntime.IsDedicatedServer=false;AssertExcluded(visit);DedicatedWorldRuntime.IsDedicatedServer=true;
  PlayerMapRuntime.Players.Clear();AssertExcluded(visit);PlayerMapRuntime.Players.Add("two",new Entry{Context=new Context{Scene="Meridian"}});
  object msOuter=BeginMove(walker,visit);Check(ForcedVisible(walker),"Valid outer Enter has local visibility scope");
  object msInner=BeginMove(walker,new VisitState{ApartmentTransition=true});Check(!ForcedVisible(walker),"Unqualified nested Enter masks outer scope even for same agent");EndMove(msInner);Check(ForcedVisible(walker),"Outer scope resumes after nested Enter");EndMove(msOuter,new Exception("native enter"));Check(!ForcedVisible(walker),"Exceptional Enter restores native visibility");
  move=PrepareMove(walker,visit,venuePoint);visit.CharacterCommand=new MoveCommand();Check(WaitMove(walker),"Replaced command cannot leave native AI waiting on old command");
  move=PrepareMove(walker,visit,venuePoint);walker.CurrentActionState=new VisitState();Check(WaitMove(walker),"Replaced action state immediately releases tracking");
  move=PrepareMove(walker,visit,venuePoint);visit.SubActionIndex++;Check(WaitMove(walker),"Subaction advancement invalidates tracked command");visit.SubActionIndex=0;
  move=PrepareMove(walker,visit,venuePoint);walker.SceneIndex=3;Check(WaitMove(walker),"Actor scene transition releases observed-map wait");walker.SceneIndex=2;
  move=PrepareMove(walker,visit,venuePoint);visit.Result=2;Check(WaitMove(walker),"Native action cancellation remains authoritative");visit.Result=0;
  move=PrepareMove(walker,visit,venuePoint);walker.CurrentAction.Exiting=true;Check(WaitMove(walker),"Native exit/finally handling is never blocked by movement wait");
  move=PrepareMove(walker,visit,venuePoint);walker.CurrentAction.Type.Requirements=false;Check(WaitMove(walker),"Interrupted requirements return immediately to native failure lifecycle");
  move=PrepareMove(walker,visit,venuePoint);walker.CurrentAction.Type.CanBeInterrupted=false;walker.CurrentAction.Type.Requirements=false;Check(!WaitMove(walker)&&walker.CurrentAction.Type.RequirementChecks==0,"Noninterruptible native action does not acquire extra requirement checks");move.Result=1;WaitMove(walker);
  move=PrepareMove(walker,visit,venuePoint);visit.TargetSceneIndex=3;Check(WaitMove(walker),"Changed target scene cannot retain same-map movement override");visit.TargetSceneIndex=2;
  move=PrepareMove(walker,visit,venuePoint);walker.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(privateArea);Check(WaitMove(walker),"Entering actual private ancestry releases public movement guard");walker.Parent=null;
  move=PrepareMove(walker,visit,venuePoint);venuePoint.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(privateArea);Check(WaitMove(walker),"Target changing to apartment-owned releases public movement guard");venuePoint.Parent=null;
  move=PrepareMove(walker,visit,venuePoint);walker.IsViewLess=true;Check(WaitMove(walker),"Becoming viewless leaves native private/sleep transition unguarded");walker.IsViewLess=false;
  move=PrepareMove(walker,visit,venuePoint);walker.State.personSchedule=new Nivalis.GhostSystem.Ai.PersonSchedule{teleport=true};Check(WaitMove(walker),"New explicit schedule teleport releases the exact public command");walker.State.personSchedule=null;
  move=PrepareMove(walker,visit,venuePoint);var savedTarget=visit.Target;visit.Target=new Nivalis.GhostSystem.GhostReference<Ghost>(new WG());Check(WaitMove(walker),"Changed target identity releases stale movement wait");visit.Target=savedTarget;
  move=PrepareMove(walker,visit,venuePoint);PlayerMapRuntime.Players.Clear();Check(WaitMove(walker),"Last observer leaving a map releases its pending native command");PlayerMapRuntime.Players.Add("two",new Entry{Context=new Context{Scene="Meridian"}});Check(WaitMove(walker),"Rejoin does not resurrect a previously released command");
  move=PrepareMove(walker,visit,venuePoint);WorkerNpcSimulation.Reset();Check(WaitMove(walker)&&!ForcedVisible(walker),"Session stop clears both movement tracking and scoped visibility");WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  walker.CurrentActionState=visit;object ms=BeginMove(walker,visit);visit.CharacterCommand=new MoveCommand{ghostReference=visit.Target};Call("AfterObservedMove",ms);EndMove(ms);Check(WaitMove(walker),"Native Enter that never requested scoped visibility cannot be captured accidentally");
  ms=BeginMove(walker,visit);ForcedVisible(walker);Call("AfterObservedMove",ms);EndMove(ms);Check(WaitMove(walker),"Reused preexisting command is not treated as a newly created movement");
  ms=BeginMove(walker,visit);ForcedVisible(walker);visit.CharacterCommand=new MoveCommand{ghostReference=new Nivalis.GhostSystem.GhostReference<Ghost>(new WG())};Call("AfterObservedMove",ms);EndMove(ms);Check(WaitMove(walker),"Native command for another target cannot acquire movement override");
  // Execute both concrete idle hooks with native command construction represented
  // by a facade; scope, pending-command ownership and release are production code.
  foreach(bool workplace in new[]{false,true})foreach(bool nativeVisible in new[]{false,true}){
   var idleActor=new Agent{Visible=nativeVisible};var publicIdlePoint=new Nivalis.GhostSystem.IdlePoint.IdlePointGhost();
   ActionState IdleState(Ghost endpoint)=>workplace?(ActionState)new Nivalis.GhostSystem.CustomerLoop.IdleAtWorkplaceActionType.State{Target=new Nivalis.GhostSystem.GhostReference<Ghost>(endpoint),ApartmentTransition=true}:new Nivalis.GhostSystem.Ai.IdleAgentAction.State{Target=new Nivalis.GhostSystem.GhostReference<Ghost>(endpoint),ApartmentTransition=true};
   MoveCommand StartIdle(ActionState st){idleActor.CurrentActionState=st;idleActor.CurrentAction=new Nivalis.GhostSystem.Ai.AgentAction();object entered=BeginIdle(idleActor,st,workplace);Check(ForcedVisible(idleActor),"Public idle native Enter sees scoped visibility");var cmd=new MoveCommand{ghostReference=st.Target};st.CharacterCommand=cmd;Call("AfterObservedMove",entered);EndMove(entered);return cmd;}
   var idleState=IdleState(publicIdlePoint);var idleCommand=StartIdle(idleState);
   Check(idleActor.Visible==nativeVisible&&idleState.ApartmentTransition,"Idle hook preserves native visibility and cached apartment state");
   for(int step=0;step<4;step++){Check(!WaitMove(idleActor),"Idle native command remains pending across worker apartment mismatch");idleActor.Position=new UnityEngine.Vector3(step+1,0,0);}
   idleCommand.Result=1;Check(WaitMove(idleActor)&&idleActor.Position.x==4,"Idle completion resumes native action after actual movement");
   idleCommand=StartIdle(idleState);idleCommand.Result=2;Check(WaitMove(idleActor),"Idle native command failure cannot deadlock action");
   idleCommand=StartIdle(idleState);idleState.CharacterCommand=new MoveCommand{ghostReference=idleState.Target};Check(WaitMove(idleActor),"Idle command replacement releases exact pointer tracking");
   idleCommand=StartIdle(idleState);idleActor.CurrentAction.Type.Requirements=false;Check(WaitMove(idleActor),"Native idle interruption requirements release guard");
   idleCommand=StartIdle(idleState);idleActor.CurrentAction.Exiting=true;Check(WaitMove(idleActor),"Exiting idle action resumes native cleanup");
   idleCommand=StartIdle(idleState);idleActor.State.personSchedule=new Nivalis.GhostSystem.Ai.PersonSchedule{teleport=true};Check(WaitMove(idleActor),"New explicit scheduled teleport releases idle walking guard");idleActor.State.personSchedule=null;
   foreach(int excluded in new[]{0,1,2,3,4,5,6,7}){
    var ep=new Nivalis.GhostSystem.IdlePoint.IdlePointGhost();var excludedState=IdleState(ep);
    if(excluded==0)idleActor.State.personSchedule=new Nivalis.GhostSystem.Ai.PersonSchedule{teleport=true};
    if(excluded==1)ep.SceneIndex=3;
    if(excluded==2)excludedState.TargetSceneIndex=3;
    if(excluded==3)idleActor.IsViewLess=true;
    if(excluded==4)ep.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost());
    if(excluded==5)idleActor.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost());
    if(excluded==6)excludedState=IdleState(new WG());
    if(excluded==7)DedicatedWorldRuntime.IsDedicatedServer=false;
    object entered=BeginIdle(idleActor,excludedState,workplace);Check(!ForcedVisible(idleActor),"Idle private/cross-map/scheduled/viewless/wrong-target/client route remains native");EndMove(entered);
    idleActor.State.personSchedule=null;idleActor.IsViewLess=false;idleActor.Parent=null;DedicatedWorldRuntime.IsDedicatedServer=true;
   }
   idleCommand=StartIdle(idleState);publicIdlePoint.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost());Check(WaitMove(idleActor),"Endpoint becoming private releases idle pending command");publicIdlePoint.Parent=null;
   object denied=BeginIdle(idleActor,idleState,workplace);ForcedVisible(idleActor);idleState.Result=2;idleState.CharacterCommand=null;Call("AfterObservedMove",denied);EndMove(denied);Check(WaitMove(idleActor),"Native concrete schedule/reservation rejection creates no invented idle command");idleState.Result=0;
   idleCommand=StartIdle(idleState);WorkerNpcSimulation.Reset();Check(WaitMove(idleActor),"Disconnect discards idle command tracking");WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  }
  // Audited staff wrappers consume pre-existing targets and preserve their own
  // checks. Both native early result paths and actual base-command paths must
  // leave exact action/command ownership intact for every concrete family.
  foreach(string family in new[]{"CleanupMeal","CleanupStorage","CleanupTrash","MakeMeal","Manage","ManageComplaint","ProcessIngredient","ServeMeal","TakeOrder"}){
   var jobActor=new Agent();var jobTarget=new Ghost();
   ActionState JobState(){var t=typeof(ActionState).Assembly.GetType("Nivalis.GhostSystem.CustomerLoop."+family+"AgentActionType+State");var st=(ActionState)Activator.CreateInstance(t);st.Target=new Nivalis.GhostSystem.GhostReference<Ghost>(jobTarget);st.ApartmentTransition=true;return st;}
   object BeginJob(ActionState st){object[] args={jobActor,st,null};Call("Before"+family+"Move",args);return args[2];}
   MoveCommand StartJob(ActionState st){jobActor.CurrentActionState=st;jobActor.CurrentAction=new Nivalis.GhostSystem.Ai.AgentAction();object entered=BeginJob(st);Check(ForcedVisible(jobActor),family+" qualified base Enter visibility");var cmd=new MoveCommand{ghostReference=st.Target};st.CharacterCommand=cmd;Call("AfterObservedMove",entered);EndMove(entered);return cmd;}
   foreach(bool seen in new[]{false,true}){jobActor.Visible=seen;var st=JobState();var cmd=StartJob(st);Check(!WaitMove(jobActor)&&st.ApartmentTransition&&jobActor.Visible==seen,family+" pending native MoveToSpot survives worker focus without changing native flags");cmd.Result=1;Check(WaitMove(jobActor),family+" successful command releases guard");}
   var jobState=JobState();var jobCommand=StartJob(jobState);jobCommand.Result=2;Check(WaitMove(jobActor),family+" failed command releases guard");
   jobCommand=StartJob(jobState);jobState.CharacterCommand=new MoveCommand{ghostReference=jobState.Target};Check(WaitMove(jobActor),family+" exact command replacement releases guard");
   jobCommand=StartJob(jobState);jobActor.CurrentActionState=JobState();Check(WaitMove(jobActor),family+" exact action replacement releases guard");
   jobCommand=StartJob(jobState);jobActor.CurrentAction.Exiting=true;Check(WaitMove(jobActor),family+" native task interruption releases guard");
   foreach(int exclusion in new[]{0,1,2,3,4,5}){
    var st=JobState();if(exclusion==0)st.Target=null;if(exclusion==1)jobTarget.SceneIndex=3;if(exclusion==2)st.TargetSceneIndex=3;if(exclusion==3)jobActor.State.personSchedule=new Nivalis.GhostSystem.Ai.PersonSchedule{teleport=true};if(exclusion==4)jobTarget.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost());if(exclusion==5)jobActor.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost());
    object entered=BeginJob(st);Check(!ForcedVisible(jobActor),family+" unknown/private/scheduled/cross-map target keeps native behavior");EndMove(entered);jobTarget.SceneIndex=2;jobTarget.Parent=null;jobActor.Parent=null;jobActor.State.personSchedule=null;
   }
   foreach(int resultCode in new[]{1,2}){var st=JobState();jobActor.CurrentActionState=st;object entered=BeginJob(st);st.Result=resultCode;st.CharacterCommand=null;Call("AfterObservedMove",entered);EndMove(entered);Check(WaitMove(jobActor),family+" native near-target/validation early return is not replaced by invented movement");}
   jobCommand=StartJob(jobState);WorkerNpcSimulation.Reset();Check(WaitMove(jobActor),family+" session reset releases pending command");WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  }
  // Run the actual selector visibility guard. This native facade mirrors the
  // audited curfew-before-visible branch; the hook never replaces its result.
  object ss=BeginSleep(walker);var other=new Agent();
  Check(ss!=null&&ForcedVisible(walker)&&!ForcedVisible(other),"Occupied sleep selection protects only its exact actor");
  int visibleReads=0;Func<bool,bool> NativeCanSleep=curfew=>{if(curfew)return true;visibleReads++;return !ForcedVisible(walker)&&!walker.Visible;};
  Check(!NativeCanSleep(false)&&visibleReads==1,"Native outside-curfew selection rejects the remotely observed actor");
  Check(NativeCanSleep(true)&&visibleReads==1,"Native curfew branch skips visibility and retains its original result");
  EndSleep(ss);Check(!ForcedVisible(walker)&&!walker.Visible,"Selection return restores native visibility without a property write");
  Check(NativeCanSleep(false),"Native offstage visibility remains unchanged outside selection");
  foreach(var excluded in new Agent[]{null,new Agent{SceneIndex=3},new Agent{SceneIndex=-1},new Agent{IsValid=false}}){ss=BeginSleep(excluded);Check(ss==null&&!ForcedVisible(walker),"Unqualified sleep selector cannot acquire visibility scope");EndSleep(ss);}
  DedicatedWorldRuntime.IsDedicatedServer=false;ss=BeginSleep(walker);Check(ss==null&&!ForcedVisible(walker),"Client and solo sleep selection remains native");EndSleep(ss);DedicatedWorldRuntime.IsDedicatedServer=true;
  DedicatedWorldRuntime.IsIsolated=false;ss=BeginSleep(walker);Check(ss==null,"Nonisolated server sleep selection remains native");EndSleep(ss);DedicatedWorldRuntime.IsIsolated=true;
  ss=BeginSleep(walker);walker.IsValid=false;object si=BeginSleep(walker);Check(!ForcedVisible(walker),"Unqualified nested sleep masks the same actor's outer scope");EndSleep(si);walker.IsValid=true;Check(ForcedVisible(walker),"Outer sleep scope resumes after nested selector");
  si=BeginSleep(other);Check(ForcedVisible(other)&&!ForcedVisible(walker),"Qualified nested selector protects only the current actor");EndSleep(si,new Exception("native sleep"));Check(ForcedVisible(walker)&&!ForcedVisible(other),"Exceptional nested selector restores its exact outer actor");EndSleep(ss,new Exception("native outer sleep"));Check(!ForcedVisible(walker),"Exceptional selector exit clears override");
  ms=BeginMove(walker,visit);Check(ForcedVisible(walker),"Business scope active before nested sleep");si=BeginSleep(new Agent{SceneIndex=3});Check(!ForcedVisible(walker),"Unqualified nested sleep masks business visibility");EndSleep(si);Check(ForcedVisible(walker),"Business visibility resumes after nested sleep");EndMove(ms);
  ss=BeginSleep(walker);ms=BeginMove(walker,new VisitState{ApartmentTransition=true});Check(!ForcedVisible(walker),"Unqualified nested business Enter masks sleep visibility");EndMove(ms,new Exception("native business"));Check(ForcedVisible(walker),"Sleep visibility resumes after exceptional business Enter");EndSleep(ss);
  ss=BeginSleep(walker);ms=BeginMove(other,new VisitState{Target=visit.Target});Check(ForcedVisible(other)&&!ForcedVisible(walker),"Qualified business scope cannot inherit outer sleep actor");EndMove(ms);EndSleep(ss);
  WorkerNpcSimulation.Configure(_=>throw new Exception("bad sleep context"));ss=BeginSleep(walker);Check(ss==null&&!ForcedVisible(walker),"Selector occupancy failure leaves native result and visibility untouched");EndSleep(ss);WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
  ss=BeginSleep(walker);si=BeginSleep(other);WorkerNpcSimulation.Reset();EndSleep(si);EndSleep(ss);Check(!ForcedVisible(walker)&&!ForcedVisible(other),"Reset during nested sleep prevents stale scope restoration");
  WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);ss=BeginSleep(walker);si=BeginSleep(other);WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);EndSleep(si);EndSleep(ss);Check(!ForcedVisible(walker)&&!ForcedVisible(other),"Old selector finalizers cannot restore scopes after reconfiguration");
  ms=BeginMove(walker,visit);ForcedVisible(walker);ss=BeginSleep(other);WorkerNpcSimulation.Reset();visit.CharacterCommand=new MoveCommand{ghostReference=visit.Target};Call("AfterObservedMove",ms);EndSleep(ss);EndMove(ms);WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);Check(!ForcedVisible(walker)&&WaitMove(walker),"Reset inside cross-scope nesting cannot resurrect business tracking");
  // Existing native Sleep Enter/Exit stays authoritative. Record rare direct
  // relocation after it occurs; do not alter its result, position or view state.
  logs.Clear();var idlePoint=new Ghost();var sleepState=new SleepState{ActionSource=new Nivalis.GhostSystem.GhostReference<Ghost>(idlePoint),Result=1};walker.Position=UnityEngine.Vector3.zero;
  object trace=TraceSleep(walker,sleepState,"Enter");walker.Position=new UnityEngine.Vector3(50,0,0);walker.IsViewLess=true;Call("AfterSleepTransition",trace);
  Check(logs.Count==1&&logs[0].Contains("NPC_NATIVE_SLEEP stage=Enter")&&logs[0].Contains("viewless=False->True")&&logs[0].Contains("targetId="+idlePoint.Id),"Sleep entry relocation diagnostic identifies native source and view transition");
  Check(walker.Position.x==50&&walker.IsViewLess&&sleepState.Result==1,"Sleep instrumentation cannot cancel or alter a loaded native action");
  trace=TraceSleep(walker,sleepState,"Exit");walker.Position=new UnityEngine.Vector3(0,0,0);walker.IsViewLess=false;Call("AfterSleepTransition",trace);Check(logs.Count==2&&logs[1].Contains("stage=Exit")&&logs[1].Contains("viewless=True->False"),"Sleep exit relocation is independently attributed");
  trace=TraceSleep(walker,sleepState,"Enter");walker.Position=new UnityEngine.Vector3(12,0,0);Call("AfterSleepTransition",trace);Check(logs.Count==2,"Sleep diagnostic threshold excludes ordinary moves at twelve meters");
  for(int i=0;i<6;i++){trace=TraceSleep(walker,sleepState,"Exit");walker.Position=new UnityEngine.Vector3(walker.Position.x+20,0,0);Call("AfterSleepTransition",trace);}Check(logs.Count==5,"Sleep relocation diagnostic is capped at five events per window");
  UnityEngine.Time.realtimeSinceStartupAsDouble+=11;trace=TraceSleep(walker,sleepState,"Exit");walker.Position=new UnityEngine.Vector3(walker.Position.x+20,0,0);Call("AfterSleepTransition",trace);Check(logs.Count==6&&logs[5].Contains("suppressed=3"),"Sleep trace reports suppressed relocations in the next window");
  Check(TraceSleep(new Agent{SceneIndex=3},sleepState,"Enter")==null,"Unoccupied map sleep transitions are not traced");DedicatedWorldRuntime.IsDedicatedServer=false;Check(TraceSleep(walker,sleepState,"Exit")==null,"Client sleep transitions are not traced");DedicatedWorldRuntime.IsDedicatedServer=true;
  trace=TraceSleep(walker,sleepState,"Exit");WorkerNpcSimulation.Reset();walker.Position=new UnityEngine.Vector3(walker.Position.x+50,0,0);Call("AfterSleepTransition",trace);Check(logs.Count==6,"Session stop suppresses late native sleep diagnostics");
  WorkerNpcSimulation.Diagnostic=null;WorkerNpcSimulation.Configure(scene=>scene==2);
  var resumed=new ResumeFixture();var originalTarget=resumed.Command.ghostReference;var savedPosition=resumed.Ghost.Position;
  resumed.Mark();Check(resumed.Command.PathProgress==.5227256f&&resumed.Character.Movement.PathProgress==.7f,"Completed native initializer only records exact resume identities");
  resumed.Ghost.Position=new UnityEngine.Vector3(-33.4f,-.748f,-15.5f);savedPosition=resumed.Ghost.Position;
  resumed.Apply();
  Check((resumed.Command.FromPosition-savedPosition).sqrMagnitude==0,"Observed native10m rewind rebases onto fresh authoritative position");
  Check(resumed.Command.PathProgress==0&&resumed.Character.Movement.PathProgress==0,"Both normalized command and native path progress reset for remaining route");
  Check(ReferenceEquals(originalTarget,resumed.Command.ghostReference)&&ReferenceEquals(resumed.Command,resumed.Ghost.CharacterCommand)&&ReferenceEquals(resumed.Command,resumed.Ghost.CurrentActionState.CharacterCommand),"Rebase preserves native target and action-command ownership");
  Check(resumed.Command.SimulateTimer==.73f&&resumed.Command.VerticalMultiplierCalculated&&resumed.Command.Result==0,"Native speed multiplier, once-only vertical adjustment and result preserved");
  Check((resumed.Ghost.Position-savedPosition).sqrMagnitude==0&&resumed.Character.Snap,"No authoritative pose or native Snap bypass is written");
  resumed.Command.PathProgress=.4f;resumed.Command.FromPosition=new UnityEngine.Vector3(100,0,0);resumed.Apply();Check(resumed.Command.PathProgress==.4f&&resumed.Command.FromPosition.x==100,"Resume identity is consumed exactly once");
  var exclusions=new (string Name,Action<ResumeFixture> Change)[]{
   ("client",f=>DedicatedWorldRuntime.IsDedicatedServer=false),("nonisolated",f=>DedicatedWorldRuntime.IsIsolated=false),
   ("unoccupied scene",f=>f.Ghost.SceneIndex=3),("cross-map target",f=>f.Target.SceneIndex=3),
   ("private actor",f=>f.Ghost.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost())),
   ("private target",f=>f.Target.Parent=new Nivalis.GhostSystem.GhostReference<Ghost>(new Nivalis.Apartment.ApartmentAreaGhost())),
   ("explicit scheduled teleport",f=>f.Ghost.State.personSchedule=new Nivalis.GhostSystem.Ai.PersonSchedule{teleport=true}),
   ("invalid actor",f=>f.Ghost.IsValid=false),("viewless actor",f=>f.Ghost.IsViewLess=true),("invalid target",f=>f.Target.IsValid=false),
   ("missing target",f=>f.Command.ghostReference=null),("completed command",f=>f.Command.Result=1),
   ("new zero-progress journey",f=>f.Command.PathProgress=0),("completed progress",f=>f.Command.PathProgress=1),
   ("negative progress",f=>f.Command.PathProgress=-1),("NaN progress",f=>f.Command.PathProgress=float.NaN),("infinite progress",f=>f.Command.PathProgress=float.PositiveInfinity),
   ("ordinary non-snap walking",f=>f.Character.Snap=false),("missing movement",f=>f.Character.Movement=null),
   ("foreign character agent",f=>f.Character.Agent=new Nivalis.GhostSystem.Ai.Agent()),("foreign view character",f=>f.View.Character=new Nivalis.Character()),
   ("unloaded view",f=>f.Ghost.HasLoadedView=false),("pooled replacement view",f=>f.Ghost.LoadedView=new Nivalis.GhostSystem.Ai.Agent()),
   ("different current command",f=>f.Ghost.CharacterCommand=new MoveCommand()),("different state command",f=>f.Ghost.CurrentActionState.CharacterCommand=new MoveCommand())
  };
  foreach(var entry in exclusions)foreach(bool before in new[]{true,false}){
   DedicatedWorldRuntime.IsDedicatedServer=DedicatedWorldRuntime.IsIsolated=true;WorkerNpcSimulation.Configure(scene=>scene==2);
   var f=new ResumeFixture();if(before)entry.Change(f);f.Mark();if(!before)entry.Change(f);
   var origin=f.Command.FromPosition;float progress=f.Command.PathProgress,timer=f.Command.SimulateTimer;f.Apply();
   Check((f.Command.FromPosition-origin).sqrMagnitude==0&&f.Command.PathProgress.Equals(progress)&&f.Command.SimulateTimer==timer,"Resume exclusion preserves native "+entry.Name+" "+(before?"before initializer":"before move"));
  }
  DedicatedWorldRuntime.IsDedicatedServer=DedicatedWorldRuntime.IsIsolated=true;WorkerNpcSimulation.Configure(scene=>scene==2);
  foreach(string invalidation in new[]{"yield","disable","reset","identity","state","command","nan-position"}){
   var f=new ResumeFixture();f.Mark(invalidation=="yield");
   if(invalidation=="disable")Call("ForgetResumedView",f.View);
   if(invalidation=="reset"){WorkerNpcSimulation.Reset();WorkerNpcSimulation.Configure(scene=>scene==2);}
   if(invalidation=="identity")f.Ghost.Id=Guid.NewGuid().ToString();
   if(invalidation=="state")f.Ghost.CurrentActionState=new ActionState{CharacterCommand=f.Command};
   if(invalidation=="command"){f.Command=new MoveCommand{FromPosition=new UnityEngine.Vector3(9,0,0),PathProgress=.5f,ghostReference=f.Command.ghostReference};f.Ghost.CharacterCommand=f.Command;f.Ghost.CurrentActionState.CharacterCommand=f.Command;}
   if(invalidation=="nan-position")f.Ghost.Position=new UnityEngine.Vector3(float.NaN,0,0);
   var origin=f.Command.FromPosition;float progress=f.Command.PathProgress;f.Apply();Check((f.Command.FromPosition-origin).sqrMagnitude==0&&f.Command.PathProgress==progress,"Exact initializer lifetime rejects "+invalidation);
  }
  WorkerNpcSimulation.Reset();return checks;
 }
}
'@
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('WorkerNpcSimulationTests'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($code in @($stubs,$npc,$tests)){$comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))}
$stream=[IO.MemoryStream]::new()
try{
 $result=$comp.Emit($stream)
 $result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object ToString
 if(!$result.Success){throw 'Worker NPC selection/command regression compilation failed.'}
 $compiled=[Reflection.Assembly]::Load($stream.ToArray())
 $count=$compiled.GetType('WorkerNpcTests').GetMethod('Run').Invoke($null,$null)
 "Worker NPC actual selection/command/occupancy methods PASS $count assertions"
}finally{$stream.Dispose()}

# Hook ABI checks are against the installed game's interop metadata, not stubs.
[void][Reflection.Assembly]::LoadFrom((Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll'))
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
$types=[Collections.Generic.List[object]]::new()
function AddTypes($type){$types.Add($type);foreach($nested in $type.NestedTypes){AddTypes $nested}}
foreach($type in $asm.MainModule.Types){AddTypes $type}
$contracts=@(
 @('Nivalis.GhostSystem.Ai.WanderAgentAction','NextWaypoint',$true,'Nivalis.GhostSystem.WanderPoint/WanderPointGhost',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.WanderPoint/WanderPointGhost')),
 @('Nivalis.GhostSystem.WanderPoint/WanderPointGhost','FillNextTeleportLocations',$false,'System.Void',@('Il2CppSystem.Collections.Generic.List`1<Nivalis.GhostSystem.WanderPoint/WanderPointGhost>','UnityEngine.Vector3','System.Single')),
 @('Nivalis.GhostSystem.Ai.WanderAgentAction/WanderToSubAction','GiveViewMoveCommand',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.Ai.WanderAgentAction/State','Nivalis.GhostSystem.Ai.IWorldLocation','System.Boolean')),
 @('Nivalis.GhostSystem.Ai.WanderAgentAction/WanderToSubAction','Tick',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.Ai.WanderAgentAction/State','System.Single')),
 @('Nivalis.GhostSystem.GhostManager','TransferGhostToScene',$true,'System.Void',@('Nivalis.GhostSystem.Ghost','System.Int32','Nivalis.PositionRotation')),
 @('Nivalis.GhostSystem.Ai.AgentGhostSimulator','SkipTime',$false,'System.Void',@('System.Single','System.Boolean')),
 @('Nivalis.GhostSystem.WanderPoint/WanderPointGhost','FillNextLocations',$false,'System.Void',@('Il2CppSystem.Collections.Generic.List`1<Nivalis.GhostSystem.WanderPoint/WanderPointGhost>','UnityEngine.Vector3','System.Single','System.Int32')),
 @('Nivalis.GhostSystem.CustomerLoop.VisitVenueAgentActionType/Teleport','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.VisitVenueAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.DeliverIngredientsActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.DeliverIngredientsActionType/State')),
 @('Nivalis.GhostSystem.Ai.IdleAgentAction/TeleportIfScheduleDemandsIt','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.Ai.IdleAgentAction/State')),
 @('Nivalis.GhostSystem.CustomerLoop.IdleAtWorkplaceActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.IdleAtWorkplaceActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.CleanupMealAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.CleanupMealAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.CleanupStorageAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.CleanupStorageAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.CleanupTrashAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.CleanupTrashAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.MakeMealAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.MakeMealAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.ManageAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.ManageAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.ManageComplaintAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.ManageComplaintAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.ProcessIngredientAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.ProcessIngredientAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.ServeMealAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.ServeMealAgentActionType/State')),
 @('Nivalis.GhostSystem.CustomerLoop.TakeOrderAgentActionType/TeleportIfNotInScene','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.CustomerLoop.TakeOrderAgentActionType/State')),
 @('Nivalis.GhostSystem.Ai.AgentGhost','get_Visible',$false,'System.Boolean',@()),
 @('Nivalis.GhostSystem.Ai.AgentGhostSimulator','UpdateCurrentAgentAction',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','System.Single')),
 @('Nivalis.GhostSystem.Ai.SleepAgentAction','TryGetValidInteractable',$false,'System.Boolean',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.Ai.IAgentInteractable&','Nivalis.GhostSystem.Ai.PersonSchedule')),
 @('Nivalis.GhostSystem.Ai.SleepAgentAction/SleepSubAction','Enter',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.Ai.SleepAgentAction/State')),
 @('Nivalis.GhostSystem.Ai.SleepAgentAction/SleepSubAction','Exit',$false,'System.Void',@('Nivalis.GhostSystem.Ai.AgentGhost','Nivalis.GhostSystem.Ai.SleepAgentAction/State')),
 @('Nivalis.GhostSystem.Ai.Agent/_SnapToGhostCoroutine_d__31','MoveNext',$false,'System.Boolean',@()),
 @('Nivalis.Character','MoveToSpot',$false,'System.Void',@('Nivalis.GhostSystem.Ai.MoveToSpotCommand')),
 @('Nivalis.GhostSystem.Ai.Agent','OnDisable',$false,'System.Void',@())
)
foreach($contract in $contracts){
 $type=$types | Where-Object FullName -eq $contract[0]
 $method=@($type.Methods | Where-Object Name -eq $contract[1])
 if($method.Count-ne1-or$method[0].IsStatic-ne$contract[2]-or$method[0].ReturnType.FullName-ne$contract[3]-or($method[0].Parameters.ParameterType.FullName-join'|')-ne($contract[4]-join'|')){throw "Native hook ABI changed: $($contract[0]).$($contract[1])"}
 if($method[0].HasGenericParameters-or$type.HasGenericParameters){throw 'Wander hooks must not detour shared generic native methods.'}
}
'Worker NPC native method ABI PASS 28 exact nongeneric signatures; no live native acceptance implied'
& (Join-Path $PSScriptRoot 'Test-WorkerNpcHookInstallation.ps1')
