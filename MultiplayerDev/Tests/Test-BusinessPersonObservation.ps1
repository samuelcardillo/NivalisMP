$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/BusinessStateReplication.cs'))).GetRoot()
$names=@('CapturePerson','StagePerson','AddPersonUpdate','SetPersonRaw','ResolvePerson','ResolveVenue','SamePerson')
$methods=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
$observation=$tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax] -and $_.Identifier.ValueText -eq 'PersonObservation'}
$dto=$tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'BusinessPersonState'}
if($methods.Count -ne 8 -or !$observation -or !$dto){throw 'Production staff observation/staging methods and DTO are required.'}
$source=@'
using System;using System.Collections.Generic;using System.Diagnostics;using System.Reflection;
namespace BusinessPersonObservationTests {
 public enum VenueTasks {None=0}public enum Happiness{None=0}
 public struct Vector2{public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}}
 public struct HappinessBreakdown{public Happiness Happiness;public int WageInfluence,SkillInfluence;public float Value;}
 public sealed class Venue {public string Guid;}
 public sealed class RuntimePersonData {
  private static int next;public IntPtr Pointer=new IntPtr(++next);public Venue _worksAt;
  public bool HasWork,Hireable;public int Wage,BaseWorkingHours;public VenueTasks Tasks;public float LastPaidWage,WorkQuit;
  private Vector2 hours;private HappinessBreakdown happiness;public int HoursReads,HappinessReads;
  public Vector2 WorkingHours{get{HoursReads++;return hours;}set{hours=value;}}
  public HappinessBreakdown WorkSatisfaction{get{HappinessReads++;return happiness;}set{happiness=value;}}
 }
 public sealed class Person {
  private RuntimePersonData runtime=new RuntimePersonData();private string guid;
  public int RuntimeReads,GuidReads,PropertyRuntimeReads;
  public RuntimePersonData _RuntimeData_k__BackingField{get{RuntimeReads++;return runtime;}set{runtime=value;}}
  public RuntimePersonData RuntimeData{get{PropertyRuntimeReads++;return runtime;}}
  public string Guid{get{GuidReads++;return guid;}set{guid=value;}}
 }
 public sealed class ScriptableObjectDatabase {
  public static readonly ScriptableObjectDatabase Instance=new();public int Provides;
  public readonly Dictionary<string,object> Values=new();
  public T Provide<T>(string id)where T:class{Provides++;return Values.TryGetValue(id,out var value)?value as T:null;}
 }
 public static class Probe {
  sealed class ApplyProfile{public long Observation,Comparison;public int ChangedPeople;}
'@+$observation.ToFullString()+($methods.ToFullString()-join "`n")+@'
  static BusinessPersonState Expected()=>new(){PersonId="person",VenueId="venue",HasWork=true,Hireable=true,Wage=11,BaseWorkingHours=8,
   StartHour=9.25f,EndHour=17.25f,Tasks=3,LastPaidWage=2.5f,WorkQuit=.25f,Happiness=2,WageInfluence=3,SkillInfluence=4,HappinessValue=.75f};
  static BusinessPersonState Copy(BusinessPersonState source){var copy=new BusinessPersonState();foreach(var property in typeof(BusinessPersonState).GetProperties())property.SetValue(copy,property.GetValue(source));return copy;}
  public static int Run(){
   int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
   var db=ScriptableObjectDatabase.Instance;var person=new Person{Guid="person"};var data=person._RuntimeData_k__BackingField;var work=new Venue{Guid="venue"};
   db.Values["person"]=person;db.Values["venue"]=work;var row=Expected();SetPersonRaw(data,row,work);
   var updates=new List<(Action Apply,Action Undo)>();var profile=new ApplyProfile();
   person.RuntimeReads=person.GuidReads=person.PropertyRuntimeReads=0;db.Provides=0;data.HoursReads=data.HappinessReads=0;
   StagePerson(row,updates,profile);
   Check(updates.Count==0&&profile.ChangedPeople==0,"Equal fresh native fields do not stage mutations");
   Check(person.RuntimeReads==1&&person.GuidReads==1&&person.PropertyRuntimeReads==0,"Each row checks identity once and reads the audited runtime backing field once");
   Check(db.Provides==2,"Unchanged staff still validates both person and requested workplace");
   Check(data.HoursReads==1&&data.HappinessReads==1,"Each native value struct is read once per observation");
   for(int i=0;i<300;i++)StagePerson(row,updates,profile);
   long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<1589;i++)StagePerson(row,updates,profile);long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
   Check(allocated==0,"1589 unchanged observations allocate no DTOs, closure objects or temporary lists in the production observer");
   Check(SamePerson(CapturePerson(person),row),"Worker capture retains every original wire field");
   foreach(var property in typeof(BusinessPersonState).GetProperties()){
    var changed=Copy(row);object value=property.GetValue(changed);
    if(property.PropertyType==typeof(string))property.SetValue(changed,(string)value+"changed");
    else if(property.PropertyType==typeof(bool))property.SetValue(changed,!(bool)value);
    else if(property.PropertyType==typeof(int))property.SetValue(changed,(int)value+1);
    else if(property.PropertyType==typeof(float))property.SetValue(changed,MathF.BitIncrement((float)value));
    else throw new Exception("Uncovered person field "+property.Name);
    Check(!new PersonObservation(row.PersonId,data).Matches(changed),"Exact fresh observation detects every field including one-ULP drift: "+property.Name);
   }
   foreach(string property in new[]{"StartHour","EndHour","LastPaidWage","WorkQuit","HappinessValue"}){
    var malformed=Copy(row);typeof(BusinessPersonState).GetProperty(property).SetValue(malformed,float.NaN);
    SetPersonRaw(data,malformed,work);
    Check(!new PersonObservation(row.PersonId,data).Matches(malformed),"NaN never creates a false unchanged proof: "+property);
    SetPersonRaw(data,row,work);
   }
   data.Wage=99;updates.Clear();profile=new ApplyProfile();StagePerson(row,updates,profile);
   Check(updates.Count==1&&profile.ChangedPeople==1,"Identical incoming DTO repairs newly introduced native drift");
   updates[0].Apply();Check(data.Wage==11,"Staged mutation applies authoritative scalar");
   updates[0].Undo();Check(data.Wage==99&&ReferenceEquals(data._worksAt,work),"Rollback restores exact original scalar and workplace identity");
   var snapshot=new PersonObservation(row.PersonId,data);data.Wage=71;
   Check(snapshot.ToState().Wage==99,"Captured rollback values are stable even if native data changes later");
   SetPersonRaw(data,row,work);data.WorkQuit=float.NaN;updates.Clear();StagePerson(row,updates,new ApplyProfile());updates[0].Apply();
   Check(data.WorkQuit==row.WorkQuit,"Finite authoritative state repairs malformed native floating-point state");
   updates[0].Undo();Check(float.IsNaN(data.WorkQuit),"Rollback preserves the exact malformed local preimage rather than replacing it with desired data");
   SetPersonRaw(data,row,work);data.HappinessReads=data.HoursReads=0;var previousData=data;
   var replacement=new RuntimePersonData();SetPersonRaw(replacement,row,work);replacement.Wage=81;person._RuntimeData_k__BackingField=replacement;
   updates.Clear();StagePerson(row,updates,new ApplyProfile());updates[0].Apply();
   Check(replacement.Wage==11&&previousData.Wage==11,"Newly replaced runtime record is freshly observed and modified on the next snapshot");
   updates[0].Undo();Check(replacement.Wage==81,"Rollback targets the newly observed runtime identity");
   updates.Clear();StagePerson(row,updates,new ApplyProfile());var newest=new RuntimePersonData();newest.Wage=66;person._RuntimeData_k__BackingField=newest;
   bool failed=false;try{updates[0].Apply();}catch(InvalidOperationException){failed=true;}updates[0].Undo();
   Check(failed&&newest.Wage==66&&replacement.Wage==81,"Runtime replacement between observation and commit is rejected, and rollback cannot mutate the replacement");
   person._RuntimeData_k__BackingField=data;SetPersonRaw(data,row,work);
   foreach(string failure in new[]{"missingPerson","wrongPersonId","missingRuntime","missingWork","wrongWorkId"}){
    db.Values["person"]=person;db.Values["venue"]=work;person.Guid="person";work.Guid="venue";person._RuntimeData_k__BackingField=data;
    if(failure=="missingPerson")db.Values.Remove("person");if(failure=="wrongPersonId")person.Guid="wrong";
    if(failure=="missingRuntime")person._RuntimeData_k__BackingField=null;if(failure=="missingWork")db.Values.Remove("venue");if(failure=="wrongWorkId")work.Guid="wrong";
    updates.Clear();failed=false;try{StagePerson(row,updates,new ApplyProfile());}catch(ArgumentException){failed=true;}
    Check(failed&&updates.Count==0,"Malformed native identity/workplace rejected before staging: "+failure);
   }
   db.Values["person"]=person;person.Guid="person";person._RuntimeData_k__BackingField=data;work.Guid="venue";db.Values["venue"]=work;
   var noWork=Copy(row);noWork.VenueId="";noWork.HasWork=false;SetPersonRaw(data,noWork,null);updates.Clear();StagePerson(noWork,updates,new ApplyProfile());
   Check(updates.Count==0&&SamePerson(CapturePerson(person),noWork),"Null native workplace remains a valid exact observation");
   Console.WriteLine("Unchanged1589PersonObservationAllocatedBytes="+allocated);return checks;
  }
 }
'@+$dto.ToFullString()+'}'
Add-Type -TypeDefinition $source
"PASS: $([BusinessPersonObservationTests.Probe]::Run()) production staff observation, identity, allocation, mutation and rollback assertions."
