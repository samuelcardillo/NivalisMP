using System;using System.Collections.Generic;using System.Diagnostics;
namespace NivalisTogether.Dedicated {
public static class BusinessIncrementalTests {
 private static int checks,frame,preparationGeneration,observations,reports,legacyCommits,failVenue=-1;
 private static long applied,nextProfileReport;
 private static bool IsApplying,resetDuringWrite,replaceDuringWrite,verifyFailure;
 private static int staleUndoCalls,followingWrites;
 private static string LastCaptureHash="",LastRefreshError="";
 private static Action RefreshUi;private static Action<string> Diagnostic;
 private static readonly Dictionary<int,ItemContainer> targets=new();
 private static readonly Dictionary<int,int> people=new();
 private static Manager manager=new();
 private sealed class Manager {public IntPtr Pointer=new(91);}
 private sealed class ItemContainer {public IntPtr Pointer;public int Value;}
 private sealed class ContainerProbe{}
 internal sealed class BusinessContainerState {public int Value;}
 internal sealed class BusinessVenueState {public int Id,Pointer,Value;}
 internal sealed class BusinessPersonState {public int Id,Value;}
 internal sealed class BusinessState {public long Sequence;public BusinessVenueState[] Venues=Array.Empty<BusinessVenueState>();public BusinessPersonState[] People=Array.Empty<BusinessPersonState>();}
 private static Manager Ready()=>manager;
 private static bool SameContainer(BusinessContainerState a,BusinessContainerState b)=>a.Value==b.Value;
 private static void ApplyProfiled(BusinessState state,ApplyProfile profile){legacyCommits++;applied=state.Sequence;}
 private static void ReportProfile(ApplyProfile profile){reports++;}
 private static void Reset(){preparationGeneration++;applied=0;LastCaptureHash="";LastRefreshError="";IsApplying=false;observations=reports=legacyCommits=staleUndoCalls=followingWrites=0;targets.Clear();people.Clear();manager=new();RefreshUi=null;Diagnostic=null;failVenue=-1;resetDuringWrite=replaceDuringWrite=verifyFailure=false;}
 private static void StagePerson(BusinessPersonState row,List<(Action Apply,Action Undo)> updates,ApplyProfile profile){
  observations++;int before=people[row.Id];if(before==row.Value)return;profile.ChangedPeople++;
  updates.Add((()=>people[row.Id]=row.Value,()=>people[row.Id]=before));
 }
 private static void VerifyClientPerson(BusinessPersonState row){if(people[row.Id]!=row.Value)throw new InvalidOperationException("Staff verify mismatch");}
 private static void ApplyClientVenueUnit(Manager manager,BusinessVenueState row,ApplyProfile profile,Dictionary<IntPtr,BusinessContainerState> aliases){
  var target=targets[row.Pointer];observations++;int before=target.Value;
  var desired=new BusinessContainerState{Value=row.Value};RememberClientAlias(aliases,target,desired);
  var updates=new List<(Action Apply,Action Undo)>();
  if(before!=row.Value){
   profile.ChangedVenues++;profile.ChangedContainers++;profile.ChangedInventoryPointers.Add(target.Pointer);profile.InventoryChecks.Add((desired,target));
   updates.Add((()=>{target.Value=row.Value;if(resetDuringWrite){preparationGeneration++;applied=0;LastCaptureHash="";}if(replaceDuringWrite)BusinessIncrementalTests.manager=new(){Pointer=new IntPtr(92)};if(failVenue==row.Id)throw new InvalidOperationException("Injected partially written venue failure");},()=>{staleUndoCalls++;target.Value=before;}));
   if(resetDuringWrite||replaceDuringWrite)updates.Add((()=>followingWrites++,()=>staleUndoCalls++));
  }
  CommitClientUnit(updates,profile);
 }
 private static void VerifyChangedInventories(ApplyProfile profile){
  if(verifyFailure)throw new InvalidOperationException("Injected post-apply verification failure");
  foreach(var row in profile.InventoryChecks)if(row.Target.Value!=row.Expected.Value)throw new InvalidOperationException("Native drift");
 }
 /*PRODUCTION*/
 private static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
 private static void Reject(Action action,string why){try{action();}catch(InvalidOperationException){Check(true,why);return;}throw new Exception(why);}
 private static BusinessState State(int count){
  var state=new BusinessState{Sequence=1,Venues=new BusinessVenueState[count]};
  for(int i=0;i<count;i++){state.Venues[i]=new(){Id=i,Pointer=i+1,Value=100+i};targets[i+1]=new(){Pointer=new IntPtr(i+1),Value=i};}return state;
 }
 private static PreparedBusinessUpdate Prepare(BusinessState state)=>new(state,"committed-hash",preparationGeneration,0,0,0,true);
 private static ClientMutationBudget Budget()=>new(Stopwatch.Frequency,10000,1);
 private static bool Step(PreparedBusinessUpdate update,ClientMutationBudget budget){budget.BeginFrame(++frame);return PreparedWorldUpdate.Advance(update,budget);}
 private static void SlicingAndFreshness(){
  Reset();var state=State(3);var update=Prepare(state);var budget=Budget();int ui=0;RefreshUi=()=>ui++;
  Check(!Step(update,budget)&&targets[1].Value==100&&targets[2].Value==1,"One granted venue commits; subsequent entities stay untouched");
  Check(applied==0&&LastCaptureHash==""&&ui==0&&reports==0,"Partial progress cannot publish sequence, hash, UI or completed profile");
  targets[2].Value=77;Check(!Step(update,budget)&&targets[2].Value==101&&observations==2,"Second unit freshly observes native state changed between frames");
  Check(!Step(update,budget)&&targets[3].Value==102,"Each venue has its own atomic unit");
  Check(Step(update,budget)&&applied==1&&LastCaptureHash=="committed-hash"&&ui==1&&reports==1,"Only final granted publication completes snapshot");
  Reject(()=>Step(update,budget),"Completed cursor cannot run again");
  Reset();state=State(2);update=Prepare(state);budget=Budget();budget.BeginFrame(++frame);budget.TryStart(Stopwatch.GetTimestamp());
  Check(!update.Advance(budget)&&observations==0,"Already spent shared frame budget permits no native work");
  Check(!Step(update,budget)&&observations==1,"Next frame makes progress without starvation");
  Reject(update.Commit,"Partly advanced update cannot enter synchronous full-commit path");
 }
 private static void FailureAndCancellation(){
  Reset();var state=State(2);var update=Prepare(state);var budget=Budget();Step(update,budget);targets[2].Value=77;failVenue=1;
  Reject(()=>Step(update,budget),"Later unit failure propagates to session disconnect");
  Check(targets[1].Value==100&&targets[2].Value==77,"Successful earlier unit stays disposable; failing unit restores its fresh current baseline");
  Check(applied==0&&LastCaptureHash==""&&!IsApplying,"Failure cannot publish success or leave applying guard set");
  Reject(()=>Step(update,budget),"Failed cursor cannot retry");
  Reset();state=State(1);update=Prepare(state);budget=Budget();verifyFailure=true;
  Reject(()=>Step(update,budget),"Post-apply verification failure aborts unit");Check(targets[1].Value==0,"Verification failure rolls unit back exactly");
  Reset();state=State(2);update=Prepare(state);budget=Budget();Step(update,budget);update.Abort();
  Reject(()=>Step(update,budget),"Aborted cursor cannot touch another entity");Check(targets[2].Value==1&&applied==0&&LastCaptureHash=="","Abort leaves no published snapshot");
  Reset();state=State(2);update=Prepare(state);budget=Budget();Step(update,budget);preparationGeneration++;
  Reject(()=>Step(update,budget),"Reset generation invalidates partially applied cursor");
  Reset();state=State(2);update=Prepare(state);budget=Budget();Step(update,budget);manager=new(){Pointer=new IntPtr(92)};
  Reject(()=>Step(update,budget),"Native manager replacement cannot receive the remaining snapshot");Check(targets[2].Value==1,"Replacement detected before second unit");
  Reset();state=State(1);update=Prepare(state);budget=Budget();resetDuringWrite=true;
  Reject(()=>Step(update,budget),"Reentrant reset during native write prevents continuation/publication");Check(applied==0&&LastCaptureHash=="","Reentrant reset cannot republish old success");
  Check(staleUndoCalls==0&&followingWrites==0&&targets[1].Value==100,"Reset skips subsequent callbacks and old-world rollback");
  Reset();state=State(1);update=Prepare(state);budget=Budget();replaceDuringWrite=true;
  Reject(()=>Step(update,budget),"Reentrant native manager replacement stops current unit");
  Check(staleUndoCalls==0&&followingWrites==0&&applied==0,"World replacement cannot execute stale update or undo callbacks");
  Reset();state=State(1);update=Prepare(state);budget=Budget();Step(update,budget);RefreshUi=()=>{preparationGeneration++;};
  Reject(()=>Step(update,budget),"Reset during final UI callback suppresses completion");Check(applied==0&&LastCaptureHash=="","UI reset preserves unpublished state");
 }
 private static void AliasesAndStaff(){
  Reset();var state=State(2);state.Venues[1].Pointer=1;var update=Prepare(state);var budget=Budget();Step(update,budget);
  Reject(()=>Step(update,budget),"Conflicting alias in later venue fails before overwriting earlier target");Check(targets[1].Value==100&&applied==0,"Alias conflict does not silently last-write-win");
  Reset();state=State(2);state.Venues[1].Pointer=1;state.Venues[1].Value=100;update=Prepare(state);budget=Budget();Step(update,budget);Step(update,budget);
  Check(Step(update,budget)&&targets[1].Value==100,"Matching cross-unit alias is allowed");
  Reset();state=new(){Sequence=1,People=new BusinessPersonState[70]};for(int i=0;i<70;i++){state.People[i]=new(){Id=i,Value=100+i};people[i]=i;}
  update=Prepare(state);budget=Budget();Check(!Step(update,budget)&&observations==32&&people[31]==131&&people[32]==32,"Staff units are bounded to32 fresh records");
  Check(!Step(update,budget)&&observations==64,"Second bounded staff group consumes the next frame grant");
  Check(!Step(update,budget)&&observations==70&&applied==0,"Final staff group still waits for publication");
  Check(Step(update,budget)&&applied==1,"Staff cursor publishes after all groups");
  Reset();state=State(1);update=Prepare(state);update.Commit();Check(legacyCommits==1&&applied==1,"Synchronous compatibility remains full-transaction path");
 }
 public static int Run(){SlicingAndFreshness();FailureAndCancellation();AliasesAndStaff();return checks;}
}
}
