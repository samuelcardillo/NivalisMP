$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
# Reuse the previously tested native-wrapper and Harmony boundary doubles.
$boundary=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ReplicatedUiRegistryTests.cs'))
$boundary=$boundary.Substring(0,$boundary.IndexOf('public static class ReplicatedUiRegistryTests'))
$boundary=$boundary.Replace('public class MonoBehaviour : Object','public class Scene { public bool Valid=true,isLoaded=true; public bool IsValid()=>Valid; } public class GameObject { public Scene scene=new Scene(); } public class MonoBehaviour : Object')
$boundary=$boundary.Replace('private static int pointer;','public GameObject gameObject=new GameObject(); private static int pointer;')
$boundary+=@'
namespace Nivalis {
 public class DoorInteraction:UnityEngine.MonoBehaviour{public void Awake(){}}
 public class HingeInteraction:UnityEngine.MonoBehaviour{public void Awake(){}}
 public class ItemCollectible:UnityEngine.MonoBehaviour{public void Start(){}}
}
namespace Nivalis.Dialogue { public class StartDialogueInteraction:UnityEngine.MonoBehaviour{public void Awake(){}public void OnEnable(){}public void OnDestroy(){}} }
public static class WorldTargetsProbe {
 static void Life(UnityEngine.MonoBehaviour value,string name)=>HarmonyLib.Harmony.After[value.GetType().GetMethod(name)].Method.Invoke(null,new object[]{value});
 public static int Run(){
  int checks=0;void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
  var door=new Nivalis.DoorInteraction();UnityEngine.Object.Instances[typeof(Nivalis.DoorInteraction)]=new[]{door};
  HarmonyLib.Harmony.FailPatchCall=2;bool failed=false;
  try{ReplicatedWorldTargets.Initialize();}catch(InvalidOperationException){failed=true;}
  Check(failed&&HarmonyLib.Harmony.After.Count==0&&HarmonyLib.Harmony.UnpatchCalls==1,"Partial world-target hook failure removes owned hooks for retry");
  HarmonyLib.Harmony.FailPatchCall=0;UnityEngine.Object.FailSearch=3;failed=false;
  try{ReplicatedWorldTargets.Initialize();}catch(InvalidOperationException){failed=true;}
  Check(failed&&HarmonyLib.Harmony.After.Count==0&&HarmonyLib.Harmony.UnpatchCalls==2,"Partial world-target seed failure removes owned hooks for retry");
  door=new Nivalis.DoorInteraction();UnityEngine.Object.Instances[typeof(Nivalis.DoorInteraction)]=new[]{door};
  UnityEngine.Object.FailSearch=UnityEngine.Object.Searches=0;
  ReplicatedWorldTargets.Initialize();Check(UnityEngine.Object.Searches==4,"Bootstrap searches each world type once");
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>().Length==1,"Preexisting map door discovered");
  for(int i=0;i<100;i++)ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>();
  Check(UnityEngine.Object.Searches==4,"Periodic refresh never searches the scene");
  door.gameObject.scene.isLoaded=false;
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>().Length==0,"Unloaded map cannot offer stale door interaction");
  door.gameObject.scene.isLoaded=true;
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>().Length==1,"Retained/pool object can return to a loaded map");
  door.gameObject.scene.Valid=false;
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>().Length==0,"Invalid scene is never exposed");
  door.gameObject.scene.Valid=true;door.Dead=true;
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>().Length==0,"Destroyed native wrapper is pruned without own OnDestroy");
  var replacement=new Nivalis.DoorInteraction{Pointer=door.Pointer};Life(replacement,"Awake");
  Check(ReferenceEquals(ReplicatedWorldTargets.Snapshot<Nivalis.DoorInteraction>()[0],replacement),"Scene reload with native pointer reuse keeps replacement only");
  var item=new Nivalis.ItemCollectible();Life(item,"Start");
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.ItemCollectible>().Length==1,"Late collectible registers through actual native Start ABI");
  var dialog=new Nivalis.Dialogue.StartDialogueInteraction();Life(dialog,"OnEnable");Life(dialog,"Awake");Life(dialog,"OnEnable");
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.Dialogue.StartDialogueInteraction>().Length==1,"Pooled dialogue reactivation deduplicates native pointer");
  HarmonyLib.Harmony.Destroy(dialog);
  Check(ReplicatedWorldTargets.Snapshot<Nivalis.Dialogue.StartDialogueInteraction>().Length==0,"Explicit native destruction removes dialogue reference");
  ReplicatedWorldTargets.Initialize();Check(UnityEngine.Object.Searches==4,"Reconnect retains lifecycle registry without rescanning");
  return checks;
 }
}
'@
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('WorldTargetsProbe'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($code in @($boundary,[IO.File]::ReadAllText((Join-Path $devRoot 'Source/ReplicatedWorldTargets.cs')))){
 $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))
}
$stream=[IO.MemoryStream]::new()
try{$result=$comp.Emit($stream);if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'World target regression compilation failed.'};$assembly=[Reflection.Assembly]::Load($stream.ToArray());$count=$assembly.GetType('WorldTargetsProbe').GetMethod('Run').Invoke($null,$null)}finally{$stream.Dispose()}
"PASS: $count production world-target lifecycle, pooling, scene membership and scan-count checks."
[void][Reflection.Assembly]::LoadFrom((Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll'))
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
try{
 foreach($entry in @(@('Nivalis.DoorInteraction','Awake'),@('Nivalis.HingeInteraction','Awake'),@('Nivalis.ItemCollectible','Start'),@('Nivalis.Dialogue.StartDialogueInteraction','Awake'),@('Nivalis.Dialogue.StartDialogueInteraction','OnEnable'),@('Nivalis.Dialogue.StartDialogueInteraction','OnDestroy'))){
  $type=$asm.MainModule.Types|Where-Object FullName -CEQ $entry[0]
  $method=@($type.Methods|Where-Object {$_.Name -ceq $entry[1] -and !$_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.FullName -eq 'System.Void'})
  if($method.Count -ne 1){throw "Native target lifecycle ABI mismatch: $entry"}
 }
}finally{$asm.Dispose()}
'PASS: all six hooked world-target native lifecycle ABIs verified.'
