$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function ReadTree([string]$relative) {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot $relative))).GetRoot()
}
function ReadClass($tree, [string]$name) {
    $found = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$found) { throw "Production class not found: $name" }
    $found.ToFullString()
}
$gameplay = ReadTree 'MultiplayerDev/Source/GameplayReplication.cs'
$crowd = ReadTree 'MultiplayerDev/Source/CrowdReplication.cs'
$weather = ReadTree 'MultiplayerDev/Source/TimeWeatherReplication.cs'
$npcs = ReadTree 'MultiplayerDev/Source/NpcWorldReplication.cs'
$protocol = ReadTree 'MultiplayerDev/Shared/DedicatedProtocol.cs'
$textMethod = $protocol.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'Text' } | Select-Object -First 1
$captureEvents = $gameplay.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'CaptureEvents' } | Select-Object -First 1
if (!$textMethod -or !$captureEvents) { throw 'Production protocol/chunk methods missing' }
$tests = @'
public static class IndependentMapReplicaProbe {
 public static int Run() {
  int checks=0;
  void Check(bool value,string reason) { checks++; if(!value) throw new Exception(reason); }
  void Reject(Action value,string reason) { bool rejected=false; try{value();}catch(ArgumentException){rejected=true;}Check(rejected,reason); }
  SharedDoorState Door(string id,string scene,bool open) => new SharedDoorState{TargetId=id,Scene=scene,Open=open};
  var cache = new SceneDoorStateCache();
  cache.Observe(10,Door("lowtown-front","1_Lowtown",true));
  cache.Observe(11,Door("district-two-front","2_Midtown",false));
  Check(cache.TryGet("lowtown-front","1_Lowtown",out var first)&&first.Open,"Worker focus change must retain the first map's opened door");
  Check(!cache.TryGet("district-two-front","1_Lowtown",out _),"A different-map state must never apply to local components");
  cache.Observe(9,Door("lowtown-front","1_Lowtown",false));
  Check(cache.TryGet("lowtown-front","1_Lowtown",out first)&&first.Open,"Late frame must not revert door state after changing maps");
  cache.Observe(12,Door("lowtown-front","1_Lowtown",false));
  Check(cache.TryGet("lowtown-front","1_Lowtown",out first)&&!first.Open,"Returning map receives the newest state");
  Reject(()=>cache.Observe(13,Door("lowtown-front","2_Midtown",true)),"Cross-map identity reassignment rejected");
  Check(cache.TryGet("lowtown-front","1_Lowtown",out first)&&!first.Open,"Rejected reassignment does not corrupt prior map state");
  foreach(string scene in new[]{"",null,"bad\nmap",new string('x',129)})Reject(()=>cache.Observe(14,Door("invalid",scene,true)),"Missing or malformed map rejected");
  Reject(()=>cache.Observe(0,Door("invalid","1_Lowtown",true)),"Invalid sequence rejected");
  cache.Clear();
  Check(!cache.TryGet("lowtown-front","1_Lowtown",out _),"Disconnect clears previous-world door state");
  cache.Observe(1,Door("lowtown-front","1_Lowtown",true));
  Check(cache.TryGet("lowtown-front","1_Lowtown",out first)&&first.Open,"Fresh session may restart sequence numbering");
  foreach(bool success in new[]{false,true}) {
   var notice=new GameplayState{PlayerId="player-a",RequestId="door-request",Success=success,Message="World is busy. Please try again."};
   Check(GameplayStatePolicy.ValidateNoticeOnly("interaction-result",notice),"Unsequenced busy/confirmation notice must not disconnect either map");
   Check(cache.TryGet("lowtown-front","1_Lowtown",out first)&&first.Open,"Notice-only validation does not mutate cached door state");
   notice.Sequence=47;notice.Scene="2_Midtown";
   Check(GameplayStatePolicy.ValidateNoticeOnly("interaction-result",notice),"Sequenced nonmutating notice also remains a notice");
  }
  Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("gameplay-state",new GameplayState()),"Empty authoritative frame cannot masquerade as a notice");
  Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("gameplay-state",new GameplayState{Sequence=1}),"Authoritative frame still needs its source map");
  Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("gameplay-state",new GameplayState{Scene="1_Lowtown"}),"Authoritative frame still needs a positive sequence");
  Check(!GameplayStatePolicy.ValidateNoticeOnly("gameplay-state",new GameplayState{Sequence=1,Scene="1_Lowtown"}),"Valid empty authoritative map frame is retained");
  foreach(string mutation in new[]{"door","trash","pickup"}) {
   var changed=new GameplayState{Doors=mutation=="door"?new[]{Door("door","1_Lowtown",true)}:Array.Empty<SharedDoorState>(),RemovedTrash=mutation=="trash"?new[]{"trash"}:Array.Empty<string>(),CollectedItems=mutation=="pickup"?new[]{"pickup"}:Array.Empty<string>()};
   Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("interaction-result",changed),"Mutation cannot bypass ordering as an unsequenced notice");
   changed.Sequence=2;
   Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("interaction-result",changed),"Mutation cannot bypass scene validation as a notice");
   changed.Scene="1_Lowtown";
   Check(!GameplayStatePolicy.ValidateNoticeOnly("interaction-result",changed),"Valid sequenced mutation remains eligible for application");
  }
  Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("interaction-result",new GameplayState{Sequence=-1}),"Negative sequence is malformed even on a notice");
  Reject(()=>GameplayStatePolicy.ValidateNoticeOnly("interaction-result",new GameplayState{Scene="invalid\nscene"}),"Malformed scene is rejected even on a notice");
  var doors=new SharedDoorState[193];for(int i=0;i<doors.Length;i++)doors[i]=Door("door"+i,i%2==0?"1_Lowtown":"2_Midtown",i%3==0);
  var removed=Enumerable.Range(0,257).Select(i=>"trash"+i).ToArray();
  var collected=Enumerable.Range(0,129).Select(i=>"pickup"+i).ToArray();
  var frame=new GameplayState{Sequence=42,Scene="2_Midtown",Doors=doors,RemovedTrash=removed,CollectedItems=collected};
  var chunks=GameplayReplication.CaptureEvents(frame);Check(chunks.Length==4,"Chunking keeps complete cross-map sparse state bounded");
  var received=new SceneDoorStateCache();var trash=new HashSet<string>();var items=new HashSet<string>();
  foreach(var chunk in chunks.Reverse()){
   Check(chunk.Kind=="gameplay-state"&&Encoding.UTF8.GetByteCount(chunk.Data.GetRawText())<=48000,"Packet kind and byte budget retained");
   var state=chunk.Data.Deserialize<GameplayState>(DedicatedProtocol.Json);
   Check(state.Scene=="2_Midtown"&&state.Sequence==42,"Worker capture context survives chunk serialization");
   foreach(var door in state.Doors)received.Observe(state.Sequence,door);
   foreach(string id in state.RemovedTrash)trash.Add(id);foreach(string id in state.CollectedItems)items.Add(id);
  }
  Check(received.Values.Count()==193&&trash.Count==257&&items.Count==129,"No cross-map door or global tombstone lost across chunks");
  foreach(var door in doors)Check(received.TryGet(door.TargetId,door.Scene,out var restored)&&restored.Open==door.Open,"Exact scene-qualified door state survives wire roundtrip");
  foreach(bool authority in new[]{false,true})foreach(int version in new[]{7,8,9,10,11})foreach(bool real in new[]{false,true})foreach(bool ghost in new[]{false,true}){
   bool result=CrowdDecorationPolicy.AllowsLocalSimulation(authority,version,real,ghost);
   Check(result==(authority&&version>=8&&!real&&!ghost),"Protocol8 and later permit only ghostless decorative fillers to simulate locally");
  }
  var rain=new SceneWeatherAccumulation();var snow=new SceneWeatherAccumulation();
  rain.Advance(100,0,false,0,.01f,.02f,.03f);snow.Advance(100,0,true,.5f,.01f,.02f,.03f);
  Check(rain.Puddles==0&&rain.Wetness==0&&rain.Snow==0&&snow.Snow==1,"Independent maps initialize to their own climate");
  rain.Advance(110,.5f,false,0,.01f,.02f,.03f);snow.Advance(110,0,true,.5f,.01f,.02f,.03f);
  Check(Math.Abs(rain.Puddles-.1f)<.0001f&&Math.Abs(rain.Wetness-.2f)<.0001f&&snow.Puddles==0&&snow.Snow==1,"Rain accumulation cannot contaminate another map's snow climate");
  rain.Advance(110,.5f,false,0,.01f,.02f,.03f);Check(Math.Abs(rain.Puddles-.1f)<.0001f,"Paused server clock cannot keep accumulating client weather");
  rain.Advance(200,.5f,false,0,.01f,.02f,.03f);Check(rain.Puddles==1&&rain.Wetness==1,"Sleep time jump uses native settled accumulation");
  rain.Advance(210,0,false,0,.01f,.02f,.03f);Check(Math.Abs(rain.Puddles-.9f)<.0001f&&Math.Abs(rain.Wetness-.8f)<.0001f,"Weather dries gradually between authoritative ticks");
  snow.Advance(120,0,true,0,.01f,.02f,.03f);Check(snow.Snow==1,"Native snow holds while freezing without active blizzard");
  snow.Advance(130,0,false,0,.01f,.02f,.03f);Check(Math.Abs(snow.Snow-.7f)<.0001f,"Native snow melts after climate becomes non-snowing");
  rain.Advance(5,0,false,0,.01f,.02f,.03f);Check(rain.Puddles==0&&rain.Wetness==0,"World time reset cannot apply negative accumulation");
  var outdoors=new WeatherSceneSnapshot{SceneName="1_Lowtown",SceneIndex=11,Rain=.9f};
  var indoors=new WeatherSceneSnapshot{SceneName="Apartment",SceneIndex=12,Rain=0};
  var climates=new[]{outdoors,indoors};
  Check(SceneWeatherPolicy.Find(climates,12,"1_Lowtown")==indoors,"Weather index wins over an additive manager scene's display name");
  Check(SceneWeatherPolicy.Find(climates,13,"1_Lowtown")==null,"Unknown map never borrows worker climate by same name");
  Check(SceneWeatherPolicy.Find(climates,11,"1_Lowtown")==outdoors,"Remote map receives its own authoritative rain");
  Check(SceneWeatherPolicy.Find(new[]{new WeatherSceneSnapshot{SceneName="Additive",SceneIndex=-1}},-1,"Additive")!=null,"Unindexed auxiliary weather only matches its exact scene name");
  Check(NpcLocomotionPolicy.Speed(.2f,.2f,true)==1,"Continuous server-owned NPC movement selects a walking animation");
  Check(NpcLocomotionPolicy.Speed(0,.2f,true)==0,"A stopped NPC returns to standing");
  Check(NpcLocomotionPolicy.Speed(.02f,.2f,true)==0,"Position noise cannot make an idle NPC walk");
  Check(NpcLocomotionPolicy.Speed(30,.2f,true)==0,"NPC teleports do not create a rush animation");
  Check(NpcLocomotionPolicy.Speed(.2f,.2f,false)==0,"First frame and map transition do not infer motion from another map");
  foreach(float elapsed in new[]{0,-1,float.NaN,float.PositiveInfinity})Check(NpcLocomotionPolicy.Speed(.2f,elapsed,true)==0,"Invalid capture intervals cannot create locomotion");
  Check(NpcLocomotionPolicy.Speed(.2f,4,true)==1,"A slow but confirmed source interval still represents movement");
  Check(NpcLocomotionPolicy.FreshSpeed(1,1)==1&&NpcLocomotionPolicy.FreshSpeed(1,2)==0,"Animation stops if authoritative movement stops arriving");
  Check(NpcLocomotionPolicy.FreshSpeed(1,2.4f,2.4f)==1&&NpcLocomotionPolicy.FreshSpeed(1,4.7f,2.4f)==1,"Valid sparse source cadence does not incorrectly idle walking animation");
  Check(NpcLocomotionPolicy.FreshSpeed(1,4.9f,2.4f)==0,"Animation eventually idles after missing multiple sparse samples");
  return checks;
 }
}
'@
$source = 'using System; using System.Collections.Generic; using System.Linq; using System.Text; using System.Text.Json; using System.IO; using System.Security.Cryptography; namespace NivalisTogether.Dedicated {'
$source += 'public static class DedicatedProtocol { public static readonly JsonSerializerOptions Json = new JsonSerializerOptions();' + $textMethod.ToFullString() + '}'
$source += ReadClass (ReadTree 'MultiplayerDev/Shared/CanonicalJson.cs') 'CanonicalJson'
foreach ($name in @('SharedDoorState','GameplayState','SceneDoorStateCache','GameplayStatePolicy')) { $source += ReadClass $gameplay $name }
$source += ReadClass $crowd 'CrowdDecorationPolicy'
foreach ($name in @('WeatherSceneSnapshot','SceneWeatherAccumulation','SceneWeatherPolicy')) { $source += ReadClass $weather $name }
$source += ReadClass $npcs 'NpcLocomotionPolicy'
$source += ReadClass $protocol 'WorldEvent'
$source += 'public static class GameplayReplication {' + $captureEvents.ToFullString() + '}' + $tests + '}'
Add-Type -TypeDefinition $source
"Independent map replica behavior PASS $([NivalisTogether.Dedicated.IndependentMapReplicaProbe]::Run()) assertions"

# Check the complete production sources against the currently generated native
# bindings without writing/replacing a DLL or touching any running game process.
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($dir in @('dotnet','BepInEx/core','BepInEx/interop')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll') {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('IndependentMapReplicaCompile').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach ($dir in @('MultiplayerDev/Source','MultiplayerDev/Shared')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.cs') {
        $compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName),$null,$file.FullName)))
    }
}
$stream = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($stream)
    $result.Diagnostics | Where-Object Severity -eq 'Error' | ForEach-Object ToString
    if (!$result.Success) { throw 'Full Source + Shared in-memory compilation failed' }
} finally { $stream.Dispose() }
'Independent map full production compile PASS; no native live acceptance implied'
