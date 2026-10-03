$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path = Join-Path $gameRoot 'MultiplayerDev/Source/NpcWorldReplication.cs'
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path)).GetRoot()
function ExtractClass([string]$name) {
    $node = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$node) { throw "Production class absent: $name" }; $node.ToFullString()
}
function ExtractMethod([string]$name) {
    $node = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$node) { throw "Production method absent: $name" }; $node.ToFullString()
}
function ExtractStruct([string]$name) {
    $node = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$node) { throw "Production struct absent: $name" }; $node.ToFullString()
}
$stubs = @'
#pragma warning disable 0649
using System;
using System.Collections.Generic;
using UnityEngine;
using Nivalis;
namespace UnityEngine {
 public static class Mathf {
  public static float Clamp01(float value)=>Math.Clamp(value,0,1);
  public static float Clamp(float value,float min,float max)=>Math.Clamp(value,min,max);
  public static float MoveTowards(float current,float target,float delta)=>Math.Abs(target-current)<=delta?target:current+Math.Sign(target-current)*delta;
  public static float Exp(float value)=>(float)Math.Exp(value);
 }
 public static class Time { private static float now=10,delta=.02f; public static int NowReads,DeltaReads; public static float realtimeSinceStartup {get{NowReads++;return now;}set{now=value;}} public static float unscaledDeltaTime {get{DeltaReads++;return delta;}set{delta=value;}} }
 public struct Vector3 { public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public float sqrMagnitude=>x*x+y*y+z*z;public static Vector3 operator -(Vector3 first,Vector3 second)=>new Vector3(first.x-second.x,first.y-second.y,first.z-second.z);public static Vector3 Lerp(Vector3 first,Vector3 second,float value)=>new Vector3(first.x+(second.x-first.x)*value,first.y+(second.y-first.y)*value,first.z+(second.z-first.z)*value); }
 public struct Quaternion { public float x,y,z,w;public static Quaternion Slerp(Quaternion first,Quaternion second,float value)=>second; }
 public sealed class GameObject {public bool activeInHierarchy=true;}
 public sealed class Transform {public Vector3 position;public Quaternion rotation;public int Writes;public void SetPositionAndRotation(Vector3 p,Quaternion q){position=p;rotation=q;Writes++;}}
 public enum AnimatorCullingMode { AlwaysAnimate,CullCompletely }
 public enum AnimatorControllerParameterType { Float=1,Int=3,Bool=4,Trigger=9 }
 public sealed class AnimatorControllerParameter { public string name="";public int nameHash;public AnimatorControllerParameterType type;public float defaultFloat;public int defaultInt; }
 public struct AnimatorStateInfo { public int fullPathHash;public float normalizedTime; }
 public sealed class RuntimeAnimatorController {private static int id;public IntPtr Pointer=new IntPtr(++id);public string name="NPC_Male";}
 public sealed class Animator {
  private static int id;public IntPtr Pointer=new IntPtr(++id);
  public bool isInitialized=true,enabled=false,applyRootMotion=false,fireEvents=false,Rendered;
  public RuntimeAnimatorController runtimeAnimatorController=new RuntimeAnimatorController();public int layerCount=1,Selected,PlayCalls,ParameterCalls;public float SelectedTime;
  public AnimatorCullingMode cullingMode;
  public int SupportedState=123;public bool Transitioning,ClaimAllStates,RejectAllPlays;
  public AnimatorControllerParameter[] parameters=Array.Empty<AnimatorControllerParameter>();
  public readonly Dictionary<int,float> Floats=new Dictionary<int,float>();
  public readonly Dictionary<int,bool> Bools=new Dictionary<int,bool>();
  public readonly List<int> ResetTriggers=new List<int>();
  public readonly List<float> Evaluations=new List<float>();
  public bool HasState(int layer,int hash)=>layer==0&&(ClaimAllStates||hash==SupportedState);
  public static int StringToHash(string name)=>name switch {"Male.Movement.Movement"=>1931367365,"Female.Movement.Movement"=>unchecked((int)4203236556),"Base Layer.Tripod Bot blend tree"=>373026467,"Base Layer.idle"=>-178524345,"Base Layer.Bot Hovering"=>440812355,_=>0};
  public void Play(int state,int layer,float time){PlayCalls++;if(RejectAllPlays||state!=SupportedState)return;Selected=state;SelectedTime=time;Transitioning=false;}
  public void Update(float delta){Evaluations.Add(delta);isInitialized=true;Rendered=Selected!=0;SelectedTime+=delta;}
  public void SetFloat(int hash,float value){ParameterCalls++;Floats[hash]=value;}
  public void SetInteger(int hash,int value){ParameterCalls++;}
  public void SetBool(int hash,bool value){ParameterCalls++;Bools[hash]=value;}
  public float GetFloat(int hash)=>Floats.TryGetValue(hash,out float value)?value:0;
  public int GetInteger(int hash)=>0;
  public bool GetBool(int hash)=>Bools.TryGetValue(hash,out bool value)&&value;
  public void ResetTrigger(int hash){ResetTriggers.Add(hash);}
  public void SetLayerWeight(int layer,float value){}
  public float GetLayerWeight(int layer)=>1;
  public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer)=>new AnimatorStateInfo{fullPathHash=Selected,normalizedTime=SelectedTime};
  public bool IsInTransition(int layer)=>Transitioning;
 }
}
namespace Nivalis.GhostSystem.Ai {
 public class View {public T TryCast<T>() where T:class=>this as T;}
 public sealed class Agent:View {public Nivalis.Character Character;public AgentGhost MyGhost;}
 public sealed class AgentGhost { public string Id="npc"; public bool IsValid=true,IsActive,HasLoadedView=true;public View LoadedView;public UnityEngine.Vector3 Position;public UnityEngine.Quaternion Rotation; }
}
namespace Nivalis { public sealed class Character {
 public bool CameraVisible=true;public PoolElement PooledElement=new PoolElement();
 public Animator Animator=new Animator();public float Pose,AnimationOffset;public int AiCalls;
 public Movement Movement;public object cancelSource;
 public GameObject gameObject=new GameObject();public Transform ThisTransform=new Transform();
 public Nivalis.GhostSystem.Ai.Agent Agent;
 public static readonly List<Character> instances=new List<Character>();
 public void DoUpdate(){AiCalls++;throw new Exception("Replica animation must not run AI");}
 public void CancelAction(){cancelSource=null;}
}
public sealed class PoolElement {private static int id;public IntPtr Pointer=new IntPtr(++id);public ulong Version;}
public sealed class Movement {public Nav Agent,Obstacle;}
public sealed class Nav {public bool enabled;}
public sealed class CharacterManager {public static CharacterManager Instance;public void CancelSetFakeTask(){}public void RemoveFakeCharacters(){}}
}
'@
$tests = @'
public static class NpcAnimationEvaluationProbe {
 public static int Run() {
  int checks=0;void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
  NpcWorldState Snapshot(int hash=123)=>new NpcWorldState{HasAnimation=true,Pose=.7f,AnimationOffset=.2f,Layers=new[]{new AnimatorLayerSnapshot{StateHash=hash,NormalizedTime=.37f,Weight=1}},Parameters=new[]{new AnimatorParameterSnapshot{Hash=42,Type=1,Float=.5f}}};
  var character=new Character();var state=Snapshot();
  Check(ActualNpcAnimation.Apply(character,state),"Valid resident snapshot is accepted");
  Check(character.Animator.Rendered&&character.Animator.Evaluations.Count==1&&character.Animator.Evaluations[0]==0,"Applying state immediately evaluates disabled animator skeleton instead of retaining T-pose");
  Check(character.Animator.PlayCalls==1&&character.Animator.SelectedTime==.37f&&character.Animator.ParameterCalls==1,"Authoritative state/time/parameters are preserved");
  Check(!character.Animator.enabled&&!character.Animator.applyRootMotion&&!character.Animator.fireEvents&&character.AiCalls==0,"Pose evaluation does not enable root motion, clip callbacks, animator auto-update or native AI");
  var fresh=new Character();fresh.Animator.isInitialized=false;
  Check(ActualNpcAnimation.Apply(fresh,state)&&fresh.Animator.isInitialized&&fresh.Animator.Rendered,"New pooled view initializes rendering before applying authoritative pose");
  Check(fresh.Animator.Evaluations.Count==2&&fresh.Animator.Evaluations.TrueForAll(delta=>delta==0),"Initialization and initial authoritative pose evaluation do not advance time");
  var empty=new Character();var emptyState=Snapshot();emptyState.Layers=Array.Empty<AnimatorLayerSnapshot>();
  Check(!ActualNpcAnimation.Apply(empty,emptyState)&&empty.Animator.Evaluations.Count==0,"Empty animation packet does not suppress locomotion fallback");
  foreach(int badHash in new[]{0,999}){
   var invalid=new Character();Check(!ActualNpcAnimation.Apply(invalid,Snapshot(badHash))&&!invalid.Animator.Rendered&&invalid.Animator.PlayCalls==0,"Missing or invalid native base state selects fallback instead of claiming valid animation");
  }
  var noController=new Character();noController.Animator.runtimeAnimatorController=null;
  Check(!ActualNpcAnimation.Apply(noController,state)&&noController.Animator.Evaluations.Count==0,"Unavailable native controller is deferred safely");
  Check(NpcLocomotionPolicy.UsableAnimation(true,1,-123,true),"Signed female animation hashes remain valid");
  Check(!NpcLocomotionPolicy.UsableAnimation(true,1,0,true)&&!NpcLocomotionPolicy.UsableAnimation(false,1,123,true),"Unevaluated worker animation cannot advertise a usable pose");
  // These are the exact state hashes from current game controller assets, both
  // backed by their authored Male/Female_Reference_T_pose clips.
  foreach(int bindPose in new[]{305286492,1573150859}){
   var reference=new Character();reference.Animator.SupportedState=bindPose;
   Check(!NpcLocomotionPolicy.UsableAnimation(true,1,bindPose,true),"An existing reference T-pose is not valid worker animation");
   Check(!ActualNpcAnimation.Apply(reference,Snapshot(bindPose))&&reference.Animator.PlayCalls==0,"An authored T-pose packet cannot overwrite a gameplay skeleton");
   reference.Animator.Selected=bindPose;var captured=new NpcWorldState();ActualNpcAnimation.Capture(reference.Animator,captured);
   Check(!captured.HasAnimation&&captured.Layers.Length==0,"Worker does not publish its headless initialization T-pose as gameplay animation");
  }
  var nativeAction=new Animator{Selected=123};var actionCapture=new NpcWorldState();ActualNpcAnimation.Capture(nativeAction,actionCapture);
  Check(actionCapture.HasAnimation&&actionCapture.Layers.Length==1&&actionCapture.Layers[0].StateHash==123,"A valid worker gameplay action retains authoritative animation capture");
  AnimatorControllerParameter Float(string name,int hash,float value)=>new AnimatorControllerParameter{name=name,nameHash=hash,type=AnimatorControllerParameterType.Float,defaultFloat=value};
  foreach(int hash in new[]{1931367365,unchecked((int)4203236556),373026467}){
   var replica=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=new Nivalis.GhostSystem.Ai.AgentGhost(),TargetSpeed=1,RenderMotionSpeed=1,LastPoseTime=Time.realtimeSinceStartup};
   var animator=replica.Character.Animator;animator.SupportedState=hash;
   animator.parameters=new[]{Float("Speed",1,0),Float("Pose",2,.8f),Float("Walk",3,1),Float("Idle",4,1),Float("Neutral",5,1),new AnimatorControllerParameter{name="SitChair",nameHash=6,type=AnimatorControllerParameterType.Bool},new AnimatorControllerParameter{name="Greeting",nameHash=7,type=AnimatorControllerParameterType.Trigger}};
   animator.Floats[3]=animator.Floats[4]=animator.Floats[5]=0;animator.Bools[6]=true;
   ActualNpcAnimation.Fallback(replica);
   Check(animator.Selected==hash&&animator.Rendered,"A loaded view receives evaluated neutral animation before its first movement snapshot");
   Check(animator.Floats[1]==0&&animator.Floats[2]==0,"An unseen pose cannot infer walking and resets stale seated pose variation");
   Check(animator.Floats[3]==1&&animator.Floats[4]==1&&animator.Floats[5]==1,"Fallback restores authored direct blend weights rather than zeroing all clips into bind pose");
   Check(!animator.Bools[6]&&animator.ResetTriggers.Contains(7),"Fallback clears pooled action flags and pending triggers");
   replica.PoseSeen=true;ActualNpcAnimation.Fallback(replica);
   Check(animator.Floats[1]>0&&animator.PlayCalls==1&&animator.Evaluations.Count==3,"Authoritative displacement advances walking without resetting clip phase every frame");
   Time.realtimeSinceStartup+=2;ActualNpcAnimation.Fallback(replica);
   Check(animator.Floats[1]==0&&animator.Evaluations.Count==4,"Stale movement returns to an animated idle instead of continuing to walk");
   Check(!animator.enabled&&!animator.applyRootMotion&&!animator.fireEvents&&replica.Character.AiCalls==0,"Fallback evaluation leaves native AI, clip callbacks, root motion and automatic animator update disabled");
  }
  foreach(int idleHash in new[]{-178524345,440812355}){
   var replica=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=new Nivalis.GhostSystem.Ai.AgentGhost()};replica.Character.Animator.SupportedState=idleHash;
   ActualNpcAnimation.Fallback(replica);ActualNpcAnimation.Fallback(replica);
   Check(!replica.HasFallbackSpeed&&replica.Character.Animator.Selected==idleHash&&replica.Character.Animator.Rendered,"Verified stationary bot idle evaluates without requiring a Speed parameter");
   Check(replica.Character.Animator.Evaluations.Count==3&&replica.Character.Animator.PlayCalls==1,"Stationary bot hover/idle continues between snapshots");
  }
  var femaleReplica=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=new Nivalis.GhostSystem.Ai.AgentGhost(),PoseSeen=true,TargetSpeed=1,LastPoseTime=Time.realtimeSinceStartup};
  var femaleAnimator=femaleReplica.Character.Animator;femaleAnimator.runtimeAnimatorController.name="NPC_Female";femaleAnimator.SupportedState=-91730740;femaleAnimator.ClaimAllStates=true;femaleAnimator.Selected=-2070790217;
  ActualNpcAnimation.Fallback(femaleReplica);
  Check(femaleReplica.FallbackStateHash==-91730740&&femaleAnimator.Selected==-91730740&&femaleAnimator.PlayCalls==1,"Female controller with false-positive HasState for male path leaves the stale seated pose using its authored female movement path");
  float femaleTime=femaleAnimator.SelectedTime;ActualNpcAnimation.Fallback(femaleReplica);
  Check(femaleAnimator.PlayCalls==1&&femaleAnimator.SelectedTime>femaleTime,"Verified female locomotion continues without per-frame Play resets");
  var genericReplica=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=new Nivalis.GhostSystem.Ai.AgentGhost()};var genericAnimator=genericReplica.Character.Animator;genericAnimator.runtimeAnimatorController.name="Custom";genericAnimator.SupportedState=373026467;genericAnimator.ClaimAllStates=true;genericAnimator.Selected=222;
  ActualNpcAnimation.Fallback(genericReplica);
  Check(genericReplica.FallbackStateHash==373026467&&genericAnimator.PlayCalls==3,"Fallback rejects native HasState false positives until Play plus evaluation reaches the requested state");
  var rejectedReplica=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=new Nivalis.GhostSystem.Ai.AgentGhost()};var rejectedAnimator=rejectedReplica.Character.Animator;rejectedAnimator.ClaimAllStates=true;rejectedAnimator.RejectAllPlays=true;rejectedAnimator.Selected=222;
  ActualNpcAnimation.Fallback(rejectedReplica);int rejectedPlays=rejectedAnimator.PlayCalls;ActualNpcAnimation.Fallback(rejectedReplica);
  Check(rejectedReplica.FallbackStateHash==0&&rejectedPlays==5&&rejectedAnimator.PlayCalls==rejectedPlays,"Unavailable fallback is diagnosed once and rejected candidates are not retried every render frame");
  var unsupported=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=new Nivalis.GhostSystem.Ai.AgentGhost()};unsupported.Character.Animator.SupportedState=555;
  ActualNpcAnimation.Fallback(unsupported);ActualNpcAnimation.Fallback(unsupported);
  Check(unsupported.Character.Animator.PlayCalls==0&&unsupported.Character.Animator.Evaluations.Count==0&&ActualNpcAnimation.Reports==2,"Unsupported controllers are diagnosed once without guessing a state");
  var authoritative=new ActualNpcAnimation.Replica{Character=new Character(),HasWorkerAnimation=true};ActualNpcAnimation.Fallback(authoritative);
  Check(authoritative.Character.Animator.Evaluations.Count==0&&authoritative.Character.Animator.PlayCalls==0,"Fallback never replaces a valid server action animation");
  var pooled=new ActualNpcAnimation.Replica{Character=new Character(),HasWorkerAnimation=true,FallbackInitialized=true,RenderSpeed=1};var replacement=new Character();
  ActualNpcAnimation.Bind(pooled,replacement);
  Check(pooled.Character==replacement&&!pooled.HasWorkerAnimation&&!pooled.FallbackInitialized&&pooled.RenderSpeed==0,"Replacing a pooled character invalidates animation from its previous skeleton");
  var rendered=new Character();var ghost=new Nivalis.GhostSystem.Ai.AgentGhost();var view=new Nivalis.GhostSystem.Ai.Agent{Character=rendered,MyGhost=ghost};ghost.LoadedView=view;rendered.Agent=view;
  var live=new ActualNpcAnimation.Replica{Character=rendered,Ghost=ghost,PoseSeen=true,LastPoseTime=Time.realtimeSinceStartup,HasWorkerAnimation=true};
  rendered.Animator.Selected=123;
  Check(ActualNpcAnimation.Renderable(ghost,rendered)&&!ghost.IsActive,"A valid native rendered view remains eligible while its worker ghost is inactive");
  ActualNpcAnimation.RunTick(live,false);
  Check(rendered.Animator.Evaluations.Count==1&&rendered.Animator.Evaluations[0]>0&&rendered.ThisTransform.Writes==1,"Actual render tick advances skeleton and interpolation for an inactive ghost's loaded canonical view");
  Check(rendered.AiCalls==0,"Rendering inactive native ghosts does not execute their AI");
  rendered.gameObject.activeInHierarchy=false;ActualNpcAnimation.RunTick(live,false);
  Check(!ActualNpcAnimation.Renderable(ghost,rendered)&&rendered.Animator.Evaluations.Count==1,"An inactive GameObject is not animated");
  rendered.gameObject.activeInHierarchy=true;ghost.HasLoadedView=false;ActualNpcAnimation.RunTick(live,false);
  Check(!ActualNpcAnimation.Renderable(ghost,rendered)&&rendered.Animator.Evaluations.Count==1,"An unloaded ghost view is not animated");
  ghost.HasLoadedView=true;ghost.IsValid=false;ActualNpcAnimation.RunTick(live,false);
  Check(!ActualNpcAnimation.Renderable(ghost,rendered)&&rendered.Animator.Evaluations.Count==1,"An invalid ghost cannot keep rendering a stale character");
  ghost.IsValid=true;var orphan=new Character{Agent=view};
  Check(!ActualNpcAnimation.Renderable(ghost,orphan),"A pooled character retaining a ghost reference cannot replace its canonical live view");
  Character.instances.Clear();Character.instances.Add(rendered);Character.instances.Add(orphan);
  ActualNpcAnimation.RunTick(live,true);
  Check(live.Character==rendered&&rendered.Animator.Evaluations.Count==2&&orphan.Animator.Evaluations.Count==0,"Actual cleanup ignores an active stale pooled reference after binding the canonical character");
  view.Character=orphan;ActualNpcAnimation.RunTick(live,false);
  Check(rendered.Animator.Evaluations.Count==2,"Old replica stops animation when a new canonical character takes over");
  var motionCharacter=new Character();var motionGhost=new Nivalis.GhostSystem.Ai.AgentGhost();
  var motionView=new Nivalis.GhostSystem.Ai.Agent{Character=motionCharacter,MyGhost=motionGhost};motionGhost.LoadedView=motionView;motionCharacter.Agent=motionView;
  var motion=new ActualNpcAnimation.Replica{Character=motionCharacter,Ghost=motionGhost,PoseSeen=true,HasWorkerAnimation=true,LastPoseTime=Time.realtimeSinceStartup-2.4f};
  motion.Playback.Add(0,new ActualNpcAnimation.RenderPose(new Vector3(0,0,0),new Quaternion()));
  motion.Playback.Add(2.4,new ActualNpcAnimation.RenderPose(new Vector3(2.4f,0,0),new Quaternion()));
  motionCharacter.Animator.Selected=123;ActualNpcAnimation.RunTick(motion,false);
  Check(Math.Abs(motionCharacter.ThisTransform.position.x-.02f)<.0001f&&motionCharacter.Animator.Evaluations.Count==1,"Actual render tick follows sparse confirmed positions and keeps a valid 2.4-second-cadence action animated");
  float beforeHitch=motionCharacter.ThisTransform.position.x;Time.unscaledDeltaTime=1;ActualNpcAnimation.RunTick(motion,false);
  Check(motionCharacter.ThisTransform.position.x-beforeHitch<=(float)NpcPlayback<float>.MaxAdvanceSeconds+.0001f&&motionCharacter.Animator.Evaluations[1]<=(float)NpcPlayback<float>.MaxAdvanceSeconds,"Actual render tick caps both position and animation advancement after a one-second frame hitch");
  Time.unscaledDeltaTime=.02f;motion.HasWorkerAnimation=false;motionCharacter.Animator.SupportedState=1931367365;
  motionCharacter.Animator.parameters=new[]{Float("Speed",1,0)};ActualNpcAnimation.RunTick(motion,false);
  Check(motion.RenderMotionSpeed==1&&motionCharacter.Animator.Floats[1]>0,"Locomotion fallback uses actual rendered motion and remains walking across an ordinary sparse snapshot interval");
  Check(!motionCharacter.Animator.enabled&&!motionCharacter.Animator.applyRootMotion&&!motionCharacter.Animator.fireEvents&&motionCharacter.AiCalls==0,"Timestamped playback and sparse fallback preserve all replica authority guards");
  motion.Position=new Vector3(8,2,-4);motion.Rotation=new Quaternion{y=.7f,w=.7f};motion.LastSourceTime=12.5;motion.RenderMotionSpeed=motion.RenderSpeed=1;
  var unbound=new ActualNpcAnimation.Replica{Ghost=new Nivalis.GhostSystem.Ai.AgentGhost(),Position=new Vector3(99,0,0),LastSourceTime=99,RenderMotionSpeed=1,RenderSpeed=1};
  unbound.Playback.Add(0,new ActualNpcAnimation.RenderPose(new Vector3(0,0,0),new Quaternion()));unbound.Playback.Add(.5,new ActualNpcAnimation.RenderPose(new Vector3(.5f,0,0),new Quaternion()));unbound.Playback.Advance(.02);
  float unboundBefore=unbound.Playback.Current.Position.x;int unboundCount=unbound.Playback.SampleCount;
  var staleBinding=new ActualNpcAnimation.Replica{Character=new Character(),Ghost=motionGhost,Position=new Vector3(50,0,0),LastSourceTime=50,RenderSpeed=1};
  staleBinding.Playback.Add(0,new ActualNpcAnimation.RenderPose(new Vector3(3,0,0),new Quaternion()));
  var sameAnimator=motionCharacter.Animator;int evaluations=sameAnimator.Evaluations.Count,plays=sameAnimator.PlayCalls,parameters=sameAnimator.ParameterCalls,writes=motionCharacter.ThisTransform.Writes;
  float lastReceipt=motion.LastPoseTime;bool fallbackInitialized=motion.FallbackInitialized;
  ActualNpcAnimation.RunMapReset(motion,unbound,staleBinding);
  Check(motion.Playback.SampleCount==1&&motion.Playback.Current.Position.x==8&&motion.Playback.Current.Position.y==2&&motion.Playback.Current.Position.z==-4,"Map-load reset discards old playback and uses the bound replica's latest confirmed target");
  Check(motion.Playback.Current.Rotation.y==.7f&&motion.Playback.Current.Rotation.w==.7f&&motion.RenderMotionSpeed==0&&motion.RenderSpeed==0,"Map-load reset retains confirmed rotation and clears stale walking speeds");
  Check(motionCharacter.Animator==sameAnimator&&sameAnimator.Evaluations.Count==evaluations&&sameAnimator.PlayCalls==plays&&sameAnimator.ParameterCalls==parameters&&motionCharacter.ThisTransform.Writes==writes&&motionCharacter.AiCalls==0,"Reset changes only managed playback without invoking animator, transform or native AI callbacks");
  Check(motion.LastPoseTime==lastReceipt&&motion.FallbackInitialized==fallbackInitialized,"Map-load reset preserves authoritative freshness and the already bound skeleton");
  Check(unbound.Playback.SampleCount==unboundCount&&unbound.Playback.Current.Position.x==unboundBefore&&unbound.RenderMotionSpeed==1&&unbound.RenderSpeed==1,"Map-load reset leaves unbound replicas untouched");
  Check(staleBinding.Playback.Current.Position.x==3&&staleBinding.RenderSpeed==1&&staleBinding.Character.Animator.Evaluations.Count==0,"A noncanonical pooled character is skipped by map-load reset");
  Check(!motion.Playback.Add(12.5,new ActualNpcAnimation.RenderPose(new Vector3(100,0,0),new Quaternion())),"Reset anchors at the latest source timestamp and rejects its duplicate");
  view.Character=rendered;live.LastPoseTime=Time.realtimeSinceStartup;Time.NowReads=Time.DeltaReads=0;
  ActualNpcAnimation.RunRender(live,motion);
  Check(Time.NowReads==1&&Time.DeltaReads==1,"Worker animation and fallback share one native clock read per rendering tick regardless of replica count");
  var cadence=new NpcAnimationCadence();float animationDelta,total=0;int scheduled=0;
  for(int i=0;i<50;i++)if(cadence.Advance(.02f,false,false,out animationDelta)){scheduled++;total+=animationDelta;}
  Check(scheduled==10,"Hidden animation evaluates at ten hertz at fifty render frames per second");
  Check(cadence.Advance(.02f,true,false,out animationDelta),"Becoming visible evaluates immediately before the hidden deadline");total+=animationDelta;
  Check(Math.Abs(total-1.02f)<.00001f,"Hidden batching preserves all accumulated clip elapsed time without a two-thirds-speed clamp");
  for(int i=0;i<10;i++)Check(cadence.Advance(.02f,true,false,out animationDelta)&&Math.Abs(animationDelta-.02f)<.00001f,"Visible animation evaluates every frame at normal elapsed time");
  cadence.Reset();Check(cadence.Advance(3,false,false,out animationDelta)&&animationDelta<=(float)NpcPlayback<float>.MaxAdvanceSeconds,"An off-camera three-second hitch consumes only one capped source frame");
  cadence.Advance(.02f,false,false,out animationDelta);cadence.Advance(.02f,false,false,out animationDelta);
  Check(cadence.Advance(3,true,false,out animationDelta)&&animationDelta<=.2f&&Math.Abs(animationDelta-(.04f+(float)NpcPlayback<float>.MaxAdvanceSeconds))<.00001f,"Visible entry consumes bounded hidden accumulation plus a capped hitch");
  cadence.Reset();Check(cadence.Advance(0,false,false,out animationDelta)&&animationDelta==0,"New hidden skeleton receives immediate zero-time initialization");
  Check(!cadence.Advance(float.NaN,false,false,out animationDelta)&&!cadence.Advance(-1,false,false,out animationDelta),"Invalid frame clocks cannot grow animation debt");
  Check(cadence.Advance(.01f,false,true,out animationDelta)&&animationDelta==.01f,"New view or animator forces evaluation before the cadence deadline");
  var hiddenCharacter=new Character{CameraVisible=false};var hiddenGhost=new Nivalis.GhostSystem.Ai.AgentGhost();
  var hiddenView=new Nivalis.GhostSystem.Ai.Agent{Character=hiddenCharacter,MyGhost=hiddenGhost};hiddenGhost.LoadedView=hiddenView;hiddenCharacter.Agent=hiddenView;
  var hidden=new ActualNpcAnimation.Replica{Character=hiddenCharacter,Ghost=hiddenGhost,PoseSeen=true,HasWorkerAnimation=true,WorkerAnimation=state,LastPoseTime=Time.realtimeSinceStartup};
  hidden.Playback.Add(0,new ActualNpcAnimation.RenderPose(new Vector3(0,0,0),new Quaternion()));hidden.Playback.Add(2,new ActualNpcAnimation.RenderPose(new Vector3(2,0,0),new Quaternion()));
  int guardsBefore=ActualNpcAnimation.Guards;
  for(int i=0;i<50;i++)ActualNpcAnimation.RunRender(hidden);
  int advanced=0;float advancedSeconds=0;foreach(float dt in hiddenCharacter.Animator.Evaluations)if(dt>0){advanced++;advancedSeconds+=dt;}
  Check(advanced==10&&hiddenCharacter.ThisTransform.Writes==50&&ActualNpcAnimation.Guards-guardsBefore==50,"Actual hidden render path schedules ten skeleton updates but retains fifty transform writes and authority checks");
  Check(hiddenCharacter.Animator.Rendered&&hiddenCharacter.Animator.Selected==123&&hidden.HasWorkerAnimation,"Hidden initialization applies its valid worker action instead of a bind pose");
  hiddenCharacter.CameraVisible=true;ActualNpcAnimation.RunRender(hidden);
  float sum=0;foreach(float dt in hiddenCharacter.Animator.Evaluations)sum+=dt;
  Check(Math.Abs(sum-1.02f)<.00001f,"Actual worker animator consumes all hidden frame time immediately on camera entry");
  hiddenCharacter.CameraVisible=false;int priorEvaluations=hiddenCharacter.Animator.Evaluations.Count;ActualNpcAnimation.RunRender(hidden);
  Check(hiddenCharacter.Animator.Evaluations.Count==priorEvaluations,"First ordinary hidden frame skips only skeleton evaluation");
  var newAnimator=new Animator{Selected=305286492,isInitialized=false};hiddenCharacter.Animator=newAnimator;ActualNpcAnimation.RunRender(hidden);
  Check(newAnimator.isInitialized&&newAnimator.Selected==123&&newAnimator.Rendered&&hidden.HasWorkerAnimation&&newAnimator.Evaluations.Count>0,"Replacing animator on the same character immediately restores fresh worker animation and initializes its skeleton");
  newAnimator.Selected=305286492;hiddenCharacter.PooledElement.Version++;priorEvaluations=newAnimator.Evaluations.Count;ActualNpcAnimation.RunRender(hidden);
  Check(newAnimator.Selected==123&&newAnimator.Evaluations.Count>priorEvaluations&&hidden.HasWorkerAnimation,"Same-pointer pooled reuse resets cadence and reapplies worker pose even before its next hidden deadline");
  var changedController=new RuntimeAnimatorController{name="Custom"};newAnimator.runtimeAnimatorController=changedController;newAnimator.SupportedState=1931367365;newAnimator.Selected=305286492;newAnimator.parameters=new[]{Float("Speed",1,0)};
  ActualNpcAnimation.RunRender(hidden);
  Check(!hidden.HasWorkerAnimation&&hidden.FallbackInitialized&&newAnimator.Selected==1931367365&&newAnimator.Rendered,"Incompatible replacement controller immediately selects an audited fallback instead of preserving an unusable worker state");
  hiddenCharacter.gameObject.activeInHierarchy=false;priorEvaluations=newAnimator.Evaluations.Count;ActualNpcAnimation.RunRender(hidden);
  Check(newAnimator.Evaluations.Count==priorEvaluations,"Off-camera cadence cannot animate a disabled pooled character");
  hiddenCharacter.gameObject.activeInHierarchy=true;ActualNpcAnimation.RunRender(hidden);
  Check(newAnimator.Evaluations.Count>priorEvaluations,"Reactivated canonical view initializes immediately without hidden cadence debt");
  newAnimator.Evaluations.Clear();hidden.AnimationCadence.Reset();hidden.LastPoseTime=Time.realtimeSinceStartup;
  for(int i=0;i<50;i++)ActualNpcAnimation.RunRender(hidden);
  hiddenCharacter.CameraVisible=true;ActualNpcAnimation.RunRender(hidden);sum=0;advanced=0;
  foreach(float dt in newAnimator.Evaluations){sum+=dt;if(dt>0)advanced++;}
  Check(advanced==11&&Math.Abs(sum-1.02f)<.00001f,"Actual locomotion fallback preserves full cumulative hidden animation time and immediate visible entry");
  Check(hiddenCharacter.AiCalls==0&&!newAnimator.enabled&&!newAnimator.applyRootMotion&&!newAnimator.fireEvents,"Hidden animation batching never enables native AI, automatic animation, root motion or clip events");
  return checks;
 }
}
'@
$source = $stubs + 'namespace NivalisTogether.Dedicated { using Nivalis.GhostSystem.Ai; public static class CrowdReplication {public static bool ClientUsesLocalDecorations=true;}'
foreach ($name in @('NpcLocomotionPolicy','NpcPlayback','NpcAnimationCadence','NpcWorldState','AnimatorLayerSnapshot','AnimatorParameterSnapshot')) { $source += ExtractClass $name }
$source += 'public static class ActualNpcAnimation { public static bool Apply(Character character,NpcWorldState state)=>ApplyAnimation(character,state);public static void Capture(Animator animator,NpcWorldState state)=>CaptureAnimation(animator,state);public static void Fallback(Replica replica)=>TickFallbackAnimation(replica,Time.unscaledDeltaTime,Time.realtimeSinceStartup);public static void Bind(Replica replica,Character character)=>BindCharacter(replica,character);public static int Reports;private static void Report(string message){Reports++;}private static void RestoreRendering(Replica replica){}'
$source += 'private static readonly Dictionary<string,Replica> Replicas=new Dictionary<string,Replica>();private static bool clientAuthority=true;private static float nextCleanup;public static int Guards;private static void FreezeRendering(Replica replica){Guards++;}public static bool Renderable(AgentGhost ghost,Character character)=>HasRenderableView(ghost,character);public static void RunTick(Replica replica,bool cleanup){Replicas.Clear();Replicas.Add(replica.Ghost.Id,replica);nextCleanup=cleanup?0:float.MaxValue;Tick();}'
$source += 'public static void RunMapReset(params Replica[] replicas){Replicas.Clear();for(int i=0;i<replicas.Length;i++)Replicas.Add(i.ToString(),replicas[i]);ResetPlaybackAfterMapLoad();}'
$source += 'public static void RunRender(params Replica[] replicas){Replicas.Clear();for(int i=0;i<replicas.Length;i++)Replicas.Add(i.ToString(),replicas[i]);TickRendering();}'
$replica = ExtractClass 'Replica'
$source += $replica.Replace('private sealed class Replica','public sealed class Replica')
$source += (ExtractStruct 'RenderPose').Replace('private readonly struct RenderPose','public readonly struct RenderPose')
$source += ExtractMethod 'InterpolatePose'
$field = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax] -and $_.Declaration.Variables.Identifier.ValueText -contains 'LocomotionStates' } | Select-Object -First 1
if (!$field) { throw 'Production fallback states field absent' }; $source += $field.ToFullString()
$source += ExtractMethod 'ApplyAnimation'
$source += ExtractMethod 'CaptureAnimation'
$source += ExtractMethod 'PrepareReplicaAnimator'
$source += ExtractMethod 'TickFallbackAnimation'
$source += ExtractMethod 'SelectFallbackState'
$source += ExtractMethod 'BindCharacter'
$source += ExtractMethod 'HasRenderableView'
$source += ExtractMethod 'Tick'
$source += ExtractMethod 'TickRendering'
$source += ExtractMethod 'RefreshAnimationIdentity'
$source += ExtractMethod 'ResetPlaybackAfterMapLoad'
$source += '}' + $tests + '}'
Add-Type -TypeDefinition $source
"NPC resident animation actual methods PASS $([NivalisTogether.Dedicated.NpcAnimationEvaluationProbe]::Run()) assertions"
