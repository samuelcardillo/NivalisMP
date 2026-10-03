using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;
namespace NivalisTogether.Dedicated {
/*PRODUCTION_TYPES*/
internal static class Time { internal static float realtimeSinceStartup; internal static int frameCount; }
internal static class DedicatedWorldRuntime { internal static bool IsWorldReady=true; internal static string Scene="district"; }
internal static class DedicatedProtocol { internal static bool Text(string s,int max)=>!string.IsNullOrEmpty(s)&&s.Length<=max; }
internal struct RelationshipVector { internal int Business,Romance,Friend,Enemy; }
internal sealed class RuntimeData {
 internal int Writes;private bool met;private RelationshipVector relation;
 internal bool _hasBeenMet {get=>met;set{met=value;Writes++;}}
 internal RelationshipVector relationshipVector {get=>relation;set{relation=value;Writes++;}}
}
internal sealed class Person {
 internal string Guid;internal RuntimeData Data=new();internal int Reads;internal Action Read;
 internal RuntimeData RuntimeData {get{Reads++;Read?.Invoke();return Data;}}
}
internal sealed class AgentGhost { internal Person Person;internal T TryCast<T>()where T:class=>this as T; }
internal sealed class Registry {internal IntPtr Pointer=new(2);}
internal sealed class GhostManager {
 internal static bool HasInstance=true;internal static GhostManager Instance=new();
 internal IntPtr Pointer=new(1);internal Registry _ghostsDictionary=new();
 internal Dictionary<string,AgentGhost> Targets=new();internal int Lookups;internal Action Lookup;
 internal AgentGhost GetGhostById(string id){Lookups++;Lookup?.Invoke();return Targets.TryGetValue(id,out var g)?g:null;}
}
internal sealed class Matrix {internal int Calls;internal RelationshipVector Last;internal void SetVector(RelationshipVector v){Calls++;Last=v;}}
internal sealed class DialogueUI {internal static DialogueUI Instance;internal Matrix miniEmotionMatrix;}
internal sealed class ScriptableObjectDatabase {
 internal static ScriptableObjectDatabase Instance=new();internal readonly Dictionary<string,Person> People=new();
 internal T Provide<T>(string id)where T:class=>People.TryGetValue(id,out var person)?person as T:null;
}
internal static class DialoguePeopleSlicesTests {
 private static int checks,peopleGeneration;private static long clock;
 private static bool clientAuthority=true;private static object inertStep;private static Person shownSpeaker;
 private static readonly Dictionary<string,SharedDialoguePerson> ReplicaPeople=new();
 private static readonly Dictionary<string,long> PersonSequences=new(),TargetPersonSequences=new();
 private static readonly DialoguePeopleCursor PeopleCursor=new(1000,()=>clock);
 /*PRODUCTION_METHODS*/
 private static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
 private static void Reject(Action action,string why){try{action();}catch(InvalidDataException){Check(true,why);return;}throw new Exception(why);}
 private static void ResetFixture(){
  peopleGeneration++;PeopleCursor.Reset();ReplicaPeople.Clear();PersonSequences.Clear();TargetPersonSequences.Clear();
  GhostManager.HasInstance=true;GhostManager.Instance=new();DedicatedWorldRuntime.IsWorldReady=true;DedicatedWorldRuntime.Scene="district";
  clientAuthority=true;inertStep=null;shownSpeaker=null;DialogueUI.Instance=null;Time.frameCount=0;Time.realtimeSinceStartup=0;clock=0;
 }
 private static SharedDialoguePerson Row(int i,int value=1,string target=null)=>new(){PersonId="p"+i,TargetId=target??"g"+i,Met=true,Business=value,Romance=2,Friend=3,Enemy=4};
 private static void Accept(long sequence,params SharedDialoguePerson[] rows)=>AcceptPeople(new(){Sequence=sequence,People=rows});
 private static void Add(int count){
  for(int i=0;i<count;i++){GhostManager.Instance.Targets["g"+i]=new(){Person=new(){Guid="p"+i}};Accept(1,Row(i));}
 }
 private static void Frame(int frame,float now){Time.frameCount=frame;Time.realtimeSinceStartup=now;ApplyPeople();}
 private static int Written()=>GhostManager.Instance.Targets.Values.Count(g=>g.Person.Data.Writes>0);
 private static void BoundedAndFresh(){
  ResetFixture();Add(1589);inertStep=new();shownSpeaker=GhostManager.Instance.Targets["g0"].Person;DialogueUI.Instance=new(){miniEmotionMatrix=new()};
  Frame(1,0);Check(Written()==64&&GhostManager.Instance.Lookups==64,"At most 64 fresh native records run in one frame");
  Frame(1,0);Check(Written()==64,"Multiple Tick calls share the same frame budget");
  Accept(2,Row(100,9));
  for(int f=2;f<=25;f++){Accept(2+f,Row(0,f));Frame(f,(f-1)/30f);}
  Check(Written()==1589,"Continuous incoming packets never restart the pass or starve the tail");
  Check(GhostManager.Instance.Targets["g100"].Person.Data.relationshipVector.Business==9,"Queued target reads latest DTO when its record executes");
  Check(GhostManager.Instance.Targets.Values.All(g=>g.Person.Reads==(g.Person==shownSpeaker?2:1)),"Each native record reads RuntimeData once; presentation reads speaker once per completed pass");
  Check(DialogueUI.Instance.miniEmotionMatrix.Calls==1,"Emotion matrix refreshes after the whole pass, not each slice");
  int reads=GhostManager.Instance.Lookups;Frame(26,.9f);Check(GhostManager.Instance.Lookups==reads,"A completed fast pass waits until the next one-second cadence");
  int oldWrites=GhostManager.Instance.Targets["g100"].Person.Data.Writes;
  for(int f=27;f<=51;f++)Frame(f,1+(f-27)/30f);
  Check(GhostManager.Instance.Targets["g100"].Person.Data.Writes==oldWrites,"Fresh comparison skips unchanged native relationship setters");
  Check(DialogueUI.Instance.miniEmotionMatrix.Calls==2,"Normal dialogue display remains refreshed once per pass");
 }
 private static void TimeBudgetAndMissing(){
  ResetFixture();Add(10);GhostManager.Instance.Lookup=()=>clock+=3;
  Frame(1,0);Check(Written()==1,"One slow atomic native lookup exhausts the 2 ms slice");
  Frame(2,.03f);Check(Written()==2,"The next frame resumes the exact next record");
  ResetFixture();Add(2);GhostManager.Instance.Targets.Remove("g0");Frame(1,0);
  Check(Written()==1,"Missing ghost advances cursor without starving later records");
  GhostManager.Instance.Targets["g0"]=new(){Person=new(){Guid="p0"}};Frame(2,1);
  Check(Written()==2,"A missing ghost is retried on the next pass");
  Accept(2,Row(2));GhostManager.Instance.Targets["g2"]=new(){Person=new(){Guid="p2"}};Frame(3,2);
  Check(Written()==3,"New target IDs join a subsequent stable pass");
 }
 private static void WorldsAndCancellation(){
  ResetFixture();Add(100);Frame(1,0);var old=GhostManager.Instance;
  var current=new GhostManager{Pointer=new(101),_ghostsDictionary=new(){Pointer=new(102)}};
  foreach(var id in ReplicaPeople.Keys)current.Targets[id]=new(){Person=new(){Guid=ReplicaPeople[id].PersonId}};
  GhostManager.Instance=current;Frame(2,.03f);
  Check(current.Lookups==64&&old.Lookups==64,"Manager replacement restarts with fresh identities and never resumes old wrappers");
  current._ghostsDictionary=new(){Pointer=new(103)};Frame(3,.06f);
  Check(current.Lookups==128,"Registry replacement rebuilds managed target cursor");
  DedicatedWorldRuntime.Scene="other";Frame(4,.09f);Check(current.Lookups==192,"Scene change rebuilds cursor while preserving global relationship DTOs");
  ResetFixture();Add(2);var before=GhostManager.Instance;
  before.Lookup=()=>{peopleGeneration++;PeopleCursor.Reset();GhostManager.Instance=new(){Pointer=new(200)};};
  Frame(1,0);Check(before.Targets.Values.All(g=>g.Person.Data.Writes==0),"Reentrant reset after native lookup prevents stale-world writes");
  ResetFixture();Add(2);before=GhostManager.Instance;before.Targets["g0"].Person.Read=()=>{peopleGeneration++;PeopleCursor.Reset();};
  Frame(1,0);Check(Written()==0,"Session reset during RuntimeData read is rejected before mutation");
  ResetFixture();Add(2);GhostManager.Instance.Targets["g0"].Person.Guid="wrong";Frame(1,0);
  Check(GhostManager.Instance.Targets["g0"].Person.Reads==0&&Written()==1,"Reused ghost ID cannot mutate another person");
  clientAuthority=false;Frame(2,1);Check(GhostManager.Instance.Lookups==2,"Disabled client authority runs no native relationship work");
  clientAuthority=true;DedicatedWorldRuntime.IsWorldReady=false;Frame(3,2);Check(GhostManager.Instance.Lookups==2,"Unloaded worlds clear cursor without native work");
  DedicatedWorldRuntime.IsWorldReady=true;Frame(4,2);Check(GhostManager.Instance.Lookups==4,"A ready world starts a fresh pass after unload");
 }
 private static void SequencesAndMalformed(){
  ResetFixture();Add(1);Accept(3,Row(0,7));Accept(2,Row(0,99));Frame(1,0);
  Check(GhostManager.Instance.Targets["g0"].Person.Data.relationshipVector.Business==7,"Older sequence cannot overwrite latest relationship");
  int count=ReplicaPeople.Count;Reject(()=>Accept(4,Row(1),null),"Malformed packet is rejected");
  Check(ReplicaPeople.Count==count&&!PersonSequences.ContainsKey("p1"),"Malformed tail publishes none of the packet's valid prefix");
  Reject(()=>Accept(0,Row(1)),"Non-positive relationship sequence is invalid");
  Reject(()=>Accept(5,new SharedDialoguePerson{PersonId="p",TargetId=""}),"Empty native target identity is invalid");
  var person=GhostManager.Instance.Targets["g0"].Person;GhostManager.Instance.Targets["new"]=new(){Person=person};Accept(6,Row(0,12,"new"));
  person.Data.relationshipVector=new RelationshipVector{Business=123};int writes=person.Data.Writes;
  Check(ApplyPerson("g0",GhostManager.Instance,peopleGeneration)&&person.Data.Writes==writes,"Obsolete target alias cannot overwrite newer person sequence");
  Frame(2,1);Check(person.Data.relationshipVector.Business==12,"Latest target alias supplies authoritative relationship");
 }
 internal static int RunLegacyLookup(){try{
  checks=0;ResetFixture();Add(1);Accept(2,Row(0,4));var first=GhostManager.Instance.Targets["g0"].Person;Frame(1,0);
  Check(first.Data._hasBeenMet&&first.Data.relationshipVector.Business==4,"Current ghost and person identities receive relationship update");
  var wrong=new Person{Guid="other"};GhostManager.Instance.Targets["g0"].Person=wrong;Frame(2,1);
  Check(!wrong.Data._hasBeenMet&&wrong.Data.relationshipVector.Business==0,"Reused ghost identity cannot update a different person");
  var replacement=new Person{Guid="p0"};GhostManager.Instance=new(){Pointer=new(30)};GhostManager.Instance.Targets["g0"]=new(){Person=replacement};Accept(3,Row(0,9));Frame(3,2);
  Check(replacement.Data.relationshipVector.Business==9&&first.Data.relationshipVector.Business==4,"New world manager resolves fresh native person without retaining old-world wrapper");
  GhostManager.Instance.Targets.Clear();Frame(4,3);Check(replacement.Data.relationshipVector.Business==9,"Absent or unloaded ghost remains untouched");
  GhostManager.HasInstance=false;int looked=GhostManager.Instance.Lookups;Frame(5,4);Check(GhostManager.Instance.Lookups==looked,"Missing ghost manager is safe during transition");
  ScriptableObjectDatabase.Instance.People["p0"]=replacement;Check(ReferenceEquals(ResolveDisplayPerson("p0"),replacement),"Speaker resolves only on demand from native person catalog");
  ScriptableObjectDatabase.Instance.People["p0"]=wrong;Check(ResolveDisplayPerson("p0")==null&&ResolveDisplayPerson(null)==null&&ResolveDisplayPerson("absent")==null,"Unknown or mismatched display identity stays unresolved");
  Console.WriteLine("PASS DialoguePersonLookup: "+checks+" actual-production batched relationship and speaker identity checks.");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 public static int Main(){try{BoundedAndFresh();TimeBudgetAndMissing();WorldsAndCancellation();SequencesAndMalformed();Console.WriteLine("PASS DialoguePeopleSlicesTests: "+checks+" assertions; actual cursor/accept/apply methods, cadence, fresh native lookup, reset, alias sequences and malformed state.");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
internal static class LegacyDialoguePersonLookupProbe {public static int Main()=>DialoguePeopleSlicesTests.RunLegacyLookup();}
}
