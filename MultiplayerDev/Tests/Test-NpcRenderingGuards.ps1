$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path = Join-Path $gameRoot 'MultiplayerDev/Source/NpcWorldReplication.cs'
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path)).GetRoot()
function ExtractMethod([string]$name) {
    $node = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$node) { throw "Production method absent: $name" }
    $node.ToFullString()
}
$source = @'
using System;
namespace NpcRenderingGuards {
 public enum AnimatorCullingMode { AlwaysAnimate,CullCompletely }
 public sealed class Animator {
  private bool root,events;private AnimatorCullingMode culling;
  public int RootWrites,EventWrites,CullingWrites;
  public bool applyRootMotion { get=>root;set{root=value;RootWrites++;} }
  public bool fireEvents { get=>events;set{events=value;EventWrites++;} }
  public AnimatorCullingMode cullingMode { get=>culling;set{culling=value;CullingWrites++;} }
  public int Writes=>RootWrites+EventWrites+CullingWrites;
  public void ClearCounts(){RootWrites=EventWrites=CullingWrites=0;}
 }
 public sealed class Nav {
  private bool state;public int Writes;
  public bool enabled {get=>state;set{state=value;Writes++;}}
 }
 public sealed class Movement {
  private Nav agent,obstacle;public int AgentReads,ObstacleReads;
  public Nav Agent {get{AgentReads++;return agent;}set{agent=value;}}
  public Nav Obstacle {get{ObstacleReads++;return obstacle;}set{obstacle=value;}}
 }
 public sealed class Character {
  private Animator animator;private Movement movement;
  public int AnimatorReads,MovementReads,CancelCalls;public object cancelSource;
  public Animator Animator {get{AnimatorReads++;return animator;}set{animator=value;}}
  public Movement Movement {get{MovementReads++;return movement;}set{movement=value;}}
  public void CancelAction(){CancelCalls++;cancelSource=null;}
 }
 public readonly struct RenderPose {public RenderPose(int position,int rotation){}}
 public sealed class Playback {public void Clear(){}public void Add(double time,RenderPose pose,bool reset){}}
 public sealed class Replica {
  public Cadence AnimationCadence=new Cadence();public bool AnimationIdentitySeen;public object WorkerAnimation;
  public Character Character;public bool Placed,HasWorkerAnimation,FallbackInitialized;
  public bool OriginalRootMotion,OriginalFireEvents,OriginalNavEnabled,OriginalObstacleEnabled;
  public AnimatorCullingMode OriginalCullingMode;public double LastSourceTime;
  public float RenderMotionSpeed,RenderSpeed;public int Position,Rotation;public Playback Playback=new Playback();
 }
 public sealed class Cadence {public void Reset(){}}
 public static class CrowdReplication {
  public static int Registrations,Removals;public static Animator Latest;
  public static void SetAuthorityAnimator(Animator animator,bool enabled){Latest=animator;if(enabled)Registrations++;else Removals++;}
 }
 public static class ActualGuards {
  public static void Freeze(Replica value)=>FreezeRendering(value);
  public static void Restore(Replica value)=>RestoreRendering(value);
  public static void Bind(Replica value,Character character)=>BindCharacter(value,character);
'@
$source += ExtractMethod 'FreezeRendering'
$source += ExtractMethod 'RestoreRendering'
$source += ExtractMethod 'BindCharacter'
$source += '}'
$limiter = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'NpcDiscontinuityLog' } | Select-Object -First 1
if (!$limiter) { throw 'Production discontinuity limiter absent' }
$source += $limiter.ToFullString()
$source += @'
 public static class Probe {
  public static int Run(){
   int checks=0;void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
   var animator=new Animator{applyRootMotion=true,fireEvents=true,cullingMode=AnimatorCullingMode.CullCompletely};
   var agent=new Nav{enabled=true};var obstacle=new Nav{enabled=true};
   var movement=new Movement{Agent=agent,Obstacle=obstacle};var character=new Character{Animator=animator,Movement=movement};
   var replica=new Replica{Character=character};
   void Clear(){animator.ClearCounts();agent.Writes=obstacle.Writes=0;character.AnimatorReads=character.MovementReads=movement.AgentReads=movement.ObstacleReads=0;}
   Clear();ActualGuards.Freeze(replica);
   Check(!animator.applyRootMotion&&!animator.fireEvents&&animator.cullingMode==AnimatorCullingMode.AlwaysAnimate&&!agent.enabled&&!obstacle.enabled,"First guard restores all five authority settings");
   Check(animator.Writes==3&&agent.Writes==1&&obstacle.Writes==1,"Each changed native setting is written once");
   Check(character.AnimatorReads==1&&character.MovementReads==1&&movement.AgentReads==1&&movement.ObstacleReads==1,"Guard resolves each component once per call");
   int registrations=CrowdReplication.Registrations;Clear();ActualGuards.Freeze(replica);
   Check(animator.Writes==0&&agent.Writes==0&&obstacle.Writes==0,"An unchanged replica performs no redundant native setting writes");
   Check(CrowdReplication.Registrations==registrations+1&&CrowdReplication.Latest==animator,"Animation callback authority remains registered every guarded tick");
   Check(character.AnimatorReads==1&&character.MovementReads==1&&movement.AgentReads==1&&movement.ObstacleReads==1,"Unchanged guard still resolves current components instead of persistent caches");

   animator.applyRootMotion=true;animator.fireEvents=true;animator.cullingMode=AnimatorCullingMode.CullCompletely;agent.enabled=true;obstacle.enabled=true;
   Clear();ActualGuards.Freeze(replica);
   Check(animator.Writes==3&&agent.Writes==1&&obstacle.Writes==1&&!animator.applyRootMotion&&!agent.enabled,"Native re-enablement is corrected on the next guard call");
   var replacementAnimator=new Animator{applyRootMotion=true,fireEvents=true,cullingMode=AnimatorCullingMode.CullCompletely};
   var replacementAgent=new Nav{enabled=true};var replacementObstacle=new Nav{enabled=true};
   character.Animator=replacementAnimator;character.Movement=new Movement{Agent=replacementAgent,Obstacle=replacementObstacle};
   ActualGuards.Freeze(replica);
   Check(!replacementAnimator.applyRootMotion&&!replacementAnimator.fireEvents&&replacementAnimator.cullingMode==AnimatorCullingMode.AlwaysAnimate&&!replacementAgent.enabled&&!replacementObstacle.enabled,"Replacing components on the same pooled character cannot retain an obsolete cache");
   Check(CrowdReplication.Latest==replacementAnimator,"The current animator receives the authority callback guard");

   var originalAnimator=new Animator{applyRootMotion=true,fireEvents=false,cullingMode=AnimatorCullingMode.CullCompletely};
   var originalAgent=new Nav{enabled=false};var originalObstacle=new Nav{enabled=true};
   var originalCharacter=new Character{Animator=originalAnimator,Movement=new Movement{Agent=originalAgent,Obstacle=originalObstacle},cancelSource=new object()};
   var binding=new Replica();ActualGuards.Bind(binding,originalCharacter);
   Check(binding.OriginalRootMotion&&!binding.OriginalFireEvents&&binding.OriginalCullingMode==AnimatorCullingMode.CullCompletely&&!binding.OriginalNavEnabled&&binding.OriginalObstacleEnabled,"Binding saves the exact original component state");
   Check(originalCharacter.CancelCalls==1&&originalCharacter.cancelSource==null,"Binding retains native-action cancellation");
   ActualGuards.Freeze(binding);int removals=CrowdReplication.Removals;ActualGuards.Restore(binding);
   Check(originalAnimator.applyRootMotion&&!originalAnimator.fireEvents&&originalAnimator.cullingMode==AnimatorCullingMode.CullCompletely&&!originalAgent.enabled&&originalObstacle.enabled,"Restore returns each setting to its saved state");
   Check(CrowdReplication.Removals==removals+1&&CrowdReplication.Latest==originalAnimator,"Restore removes the original animator authority registration");
   originalAnimator.ClearCounts();originalAgent.Writes=originalObstacle.Writes=0;ActualGuards.Restore(binding);
   Check(originalAnimator.Writes==0&&originalAgent.Writes==0&&originalObstacle.Writes==0,"Repeated restoration does not rewrite unchanged native settings");
   ActualGuards.Bind(binding,originalCharacter);Check(originalCharacter.CancelCalls==1,"An unchanged binding does not cancel native action again");
   var nextCharacter=new Character{Animator=new Animator(),Movement=new Movement()};ActualGuards.Bind(binding,nextCharacter);
   Check(binding.Character==nextCharacter&&!binding.Placed&&!binding.HasWorkerAnimation&&!binding.FallbackInitialized,"Replacing a character still resets playback and animation binding");
   ActualGuards.Freeze(new Replica());ActualGuards.Restore(new Replica());
   ActualGuards.Freeze(new Replica{Character=new Character()});ActualGuards.Restore(new Replica{Character=new Character()});
   Check(true,"Unbound or absent components safely skip native guards");
   var limiter=new NpcDiscontinuityLog();int suppressed;
   foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1d})
    Check(!limiter.TryTake(invalid,out suppressed)&&suppressed==0,"Invalid diagnostic clocks cannot consume capacity");
   for(int i=0;i<NpcDiscontinuityLog.Limit;i++) Check(limiter.TryTake(100,out suppressed)&&suppressed==0,"First window admits exactly the configured event capacity");
   for(int i=0;i<23;i++) Check(!limiter.TryTake(109.999,out suppressed),"Burst diagnostics stay bounded throughout the entire window");
   Check(limiter.TryTake(110,out suppressed)&&suppressed==23,"Exact ten-second boundary admits the next event and reports every suppressed event");
   Check(limiter.TryTake(110,out suppressed)&&suppressed==0,"Suppression count is reported once");
   limiter.Reset();
   for(int i=0;i<NpcDiscontinuityLog.Limit;i++) Check(limiter.TryTake(1,out suppressed)&&suppressed==0,"Authority reset clears window and suppression state");
   Check(!limiter.TryTake(1,out suppressed),"New session has the same strict event limit");
   Check(limiter.TryTake(0,out suppressed)&&suppressed==1,"A restarted monotonic clock starts a new bounded window with prior suppression visible");
   return checks;
  }
 }
}
'@
Add-Type -TypeDefinition $source
"NPC rendering guard actual methods PASS $([NpcRenderingGuards.Probe]::Run()) assertions"
