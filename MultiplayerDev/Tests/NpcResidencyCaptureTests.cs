using System;
using System.Collections.Generic;
namespace NpcResidencyTests {
public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;} }
public struct Quaternion { public float x,y,z,w; }
public enum AnimatorCullingMode { AlwaysAnimate, Cull }
public class Animator { public AnimatorCullingMode cullingMode; public int GetInstanceID()=>1; }
public sealed class Transform { public Vector3 position; public Quaternion rotation; }
public sealed class GameObject { public bool activeInHierarchy=true; }
public sealed class Character { public GameObject gameObject=new GameObject();public Transform ThisTransform=new Transform();public float Pose,AnimationOffset;public Animator Animator=new Animator(); }
public class View { public Character Character=new Character(); public T TryCast<T>() where T:class=>this as T; }
public sealed class Agent:View {}
public sealed class Person { public string Guid="person"; }
public sealed class Ghost {
 public string Id,PrefabId="prefab",ViewId="view"; public Person Person=new Person();
 public bool IsValid=true,IsViewLess,IsBakedInstance,IsActive;public int SceneIndex;public Vector3 Position;public Quaternion Rotation;
 public bool FailViews=true; public int ViewReads;public Agent View=new Agent();
 public bool HasLoadedView {get{ViewReads++;if(FailViews)throw new Exception("Scene view read during load");return true;}}
 public View LoadedView {get{ViewReads++;if(FailViews)throw new Exception("Scene view read during load");return View;}}
}
public sealed class GhostManager {
 public static bool HasInstance=true;public static GhostManager Instance=new GhostManager();public bool IsSystemInitialized=true;
 public readonly List<Ghost> _agentGhosts=new List<Ghost>();
}
public sealed class CharacterManager {
 public static bool FailRead=true;public static int Reads;private static readonly CharacterManager instance=new CharacterManager();
 public static CharacterManager Instance {get{Reads++;if(FailRead)throw new Exception("Scene manager read during load");return instance;}}
 public IntPtr Pointer=(IntPtr)1;public bool FullAnimation;
}
public static class Time {public static double realtimeSinceStartupAsDouble=10;}
public static class DedicatedWorldRuntime {public static bool IsDedicatedServer=true,IsIsolated=true;}
public static class WorkerSceneResidency {public static bool IsBusy=true;}
/*PRODUCTION_DTO*/
public static class NpcWorldReplication {
 private static readonly HashSet<string> WorkerCaptureRoster=new HashSet<string>();
 public static bool clientAuthority;private static long captureSequence;private static CharacterManager workerCharacters;private static bool previousWorkerFullAnimation;
 private sealed class WorkerAnimator {internal Animator Animator;internal AnimatorCullingMode CullingMode;}
 private static readonly Dictionary<int,WorkerAnimator> WorkerAnimators=new Dictionary<int,WorkerAnimator>();
 private static void RestoreWorkerAnimation(){workerCharacters=null;WorkerAnimators.Clear();}
 private static void CaptureAnimation(Animator a,NpcWorldState state){state.HasAnimation=a!=null;}
 /*PRODUCTION_CAPTURE*/
}
public sealed class Events {public int PayloadBytes;public bool Npcs;public bool Contains(string kind)=>kind=="npcs"&&Npcs;}
public sealed class Session {
 public bool worker=true,loaded=true,terminalSession;public object connection=new object();public HashSet<string> present=new HashSet<string>{"player"};
 public double Now=10,nextNpc;public Events events=new Events();public List<NpcWorldFrame> Queued=new List<NpcWorldFrame>();public List<string> Logs=new List<string>();
 private void QueueNpcFrame(NpcWorldFrame frame){Queued.Add(frame);events.Npcs=true;}
 private void Log(string value)=>Logs.Add(value);
 public void Pump()=>CaptureResidencyNpcs();
 /*PRODUCTION_PUMP*/
}
public static class Probe {
 public static int Run(){
  int checks=0;void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
  var manager=GhostManager.Instance;var moving=new Ghost{Id="moving",SceneIndex=2,Position=new Vector3(1,2,3),Rotation=new Quaternion{w=1}};
  var offstage=new Ghost{Id="offstage",SceneIndex=-1,IsViewLess=true};var invalid=new Ghost{Id="invalid",IsValid=false};
  manager._agentGhosts.AddRange(new[]{moving,offstage,invalid,null});
  Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"No loading roster before a stable full baseline");
  CharacterManager.FailRead=false;moving.FailViews=offstage.FailViews=false;NpcWorldReplication.Capture();
  moving.ViewReads=offstage.ViewReads=CharacterManager.Reads=0;CharacterManager.FailRead=true;moving.FailViews=offstage.FailViews=true;
  var first=NpcWorldReplication.CaptureDuringWorkerResidency();
  Check(first.Npcs.Length==2&&first.Npcs[0].Id=="moving"&&first.Npcs[1].Id=="offstage","Complete valid global roster retained across maps/offstage");
  Check(first.Npcs[0].X==1&&first.Npcs[0].Y==2&&first.Npcs[0].Z==3&&first.Npcs[0].Qw==1,"Authoritative ghost pose copied exactly");
  Check(first.Npcs[0].PersonId=="person"&&first.Npcs[0].PrefabId=="prefab"&&first.Npcs[0].ViewId=="view","Native identity preserved");
  Check(!first.Npcs[0].HasAnimation&&first.Npcs[0].Layers.Length==0&&first.Npcs[0].Parameters.Length==0,"No fabricated view animation during load");
  Check(moving.ViewReads==0&&offstage.ViewReads==0&&CharacterManager.Reads==0,"Transient scene objects never read");
  moving.Position=new Vector3(2,2,3);Time.realtimeSinceStartupAsDouble+=.5;
  var second=NpcWorldReplication.CaptureDuringWorkerResidency();
  Check(second.Sequence==first.Sequence+1&&second.CapturedAt==10.5&&second.Npcs[0].X==2,"Fresh moving pose and ordinary monotonic sequence/timestamp");
  Check(first.Npcs[0].X==1,"Previously queued frame remains immutable");
  manager._agentGhosts.Remove(offstage);Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Partial native registry cannot authorize deletion");
  manager._agentGhosts.Clear();Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Transient empty registry cannot delete all client NPCs");
  manager._agentGhosts.AddRange(new[]{moving,offstage});Check(NpcWorldReplication.CaptureDuringWorkerResidency()!=null,"Stable registry recovery resumes capture without position mutation");
  var added=new Ghost{Id="new",SceneIndex=4};manager._agentGhosts.Add(added);Check(NpcWorldReplication.CaptureDuringWorkerResidency().Npcs.Length==3,"New persistent identities can join complete roster");manager._agentGhosts.Remove(added);
  GhostManager.HasInstance=false;Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Missing registry emits no destructive empty roster");GhostManager.HasInstance=true;
  manager.IsSystemInitialized=false;Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Uninitialized registry emits no roster");manager.IsSystemInitialized=true;
  NpcWorldReplication.clientAuthority=true;Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Client cannot use worker lane");NpcWorldReplication.clientAuthority=false;
  DedicatedWorldRuntime.IsDedicatedServer=false;Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Solo cannot use worker lane");DedicatedWorldRuntime.IsDedicatedServer=true;
  DedicatedWorldRuntime.IsIsolated=false;Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Unisolated world denied");DedicatedWorldRuntime.IsIsolated=true;
  WorkerSceneResidency.IsBusy=false;Check(NpcWorldReplication.CaptureDuringWorkerResidency()==null,"Outside residency ordinary capture stays responsible");WorkerSceneResidency.IsBusy=true;
  var session=new Session();session.Pump();Check(session.Queued.Count==1&&session.nextNpc==10.5,"Residency worker captures at normal half-second cadence");
  session.events.Npcs=false;session.Now=10.49;session.Pump();Check(session.Queued.Count==1,"Cadence is bounded");
  session.Now=10.5;session.events.Npcs=true;session.Pump();Check(session.Queued.Count==1,"Already queued roster never replaced or interleaved");
  session.events.Npcs=false;session.events.PayloadBytes=24*1024*1024;session.Pump();Check(session.Queued.Count==1,"Worker queue cap preserved");
  session.events.PayloadBytes=0;session.Pump();Check(session.Queued.Count==2&&session.Logs.Count==2,"Capture resumes when prior roster drains");
  for(int gate=0;gate<7;gate++){
   var blocked=new Session();if(gate==0)blocked.worker=false;if(gate==1)blocked.loaded=false;if(gate==2)blocked.connection=null;if(gate==3)blocked.terminalSession=true;
   if(gate==4)blocked.present.Clear();if(gate==5)WorkerSceneResidency.IsBusy=false;if(gate==6)manager.IsSystemInitialized=false;
   blocked.Pump();Check(blocked.Queued.Count==0,"Capture lifecycle gate "+gate);WorkerSceneResidency.IsBusy=true;manager.IsSystemInitialized=true;
  }
  CharacterManager.FailRead=false;moving.FailViews=offstage.FailViews=false;moving.View.Character.ThisTransform.position=new Vector3(100,2,3);
  var normal=NpcWorldReplication.Capture();Check(normal.Npcs[0].X==100&&normal.Npcs[0].HasAnimation,"Ordinary capture retains scene pose/animation");
  Check(normal.Sequence>second.Sequence,"Loading and ordinary captures share one sequence");
  return checks;
 }
}
}
