$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path=Join-Path $gameRoot 'MultiplayerDev/Source/PlayerAppearance.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path))
$policy=$tree.GetRoot().DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'PlayerAppearancePolicy'}|Select-Object -First 1
if(!$policy){throw 'Production appearance policy not found'}
$test=@'
public static class AppearancePolicyProbe {
 public static int Run() {
  int checks=0;
  void Check(bool condition,string description){checks++;if(!condition)throw new Exception(description);}
  foreach(string name in new[]{"Idle","idle","IDLE","Npc_Idle_Loop"})Check(PlayerAppearancePolicy.ClipScore(name,false)>0,"Recognize native idle name");
  foreach(string name in new[]{"Walk","Walking","NPC_Walk_Forward"})Check(PlayerAppearancePolicy.ClipScore(name,true)>0,"Recognize native walk name");
  foreach(string name in new[]{"Freezing_Bench_Idle_m_Fix_2","Freezing_Walk_forward_4_steps_m_Fix","Bench_Idle","Sit_Idle","SleepIdle","PhoneIdle","SmokingIdle","ChairIdle","CrouchIdle","EatingIdle","ServingIdle","DanceIdle","TalkIdle","LaptopIdle","DoorIdle","LeanIdle","WorkIdle","HoldIdle","FishingIdle","DrunkIdle","HurtIdle","PickupIdle","TposeIdle","CoughIdle","DrinkIdle","Walk_Backward","Walk_Strafe","Walk_Stairs",null,"","  ","unrecognized"})
   foreach(bool walk in new[]{false,true})Check(PlayerAppearancePolicy.ClipScore(name,walk)==0,"Ambiguous activity clips must not become locomotion");
  Check(PlayerAppearancePolicy.ClipScore("Idle",false)>PlayerAppearancePolicy.ClipScore("Idle_02",false),"Exact neutral clip outranks variants");
  Check(PlayerAppearancePolicy.ClipScore("Walk",true)>PlayerAppearancePolicy.ClipScore("Walk_02",true),"Exact walk clip outranks variants");
  Check(PlayerAppearancePolicy.ClipScore("Idle",true)==0&&PlayerAppearancePolicy.ClipScore("Walk",false)==0,"Idle and walking remain distinct");
  Check(PlayerAppearancePolicy.ClipScore("IN1_X0186.Idle 1_m_fix_2",false)>PlayerAppearancePolicy.ClipScore("Npc_Idle_Loop",false),"Verified native standing clip wins over ambiguous variants");
  Check(PlayerAppearancePolicy.ClipScore("IN1_X0749. Normal Walk Forward long_m_fix_2",true)>PlayerAppearancePolicy.ClipScore("NPC_Walk_Forward",true),"Verified native normal walk wins over variants");
  return checks;
 }
}
'@
Add-Type -TypeDefinition ('using System; namespace NivalisTogether {'+$policy.ToFullString()+$test+'}')
"Appearance clip policy PASS $([NivalisTogether.AppearancePolicyProbe]::Run()) assertions"

Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$animation=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/UnityEngine.AnimationModule.dll'))
$clip=$animation.MainModule.Types|Where-Object FullName -eq 'UnityEngine.AnimationClip'
$sample=$clip.Methods|Where-Object {$_.Name -eq 'SampleAnimation' -and $_.Parameters.Count -eq 2}
$checks=0
function Check([bool]$condition,[string]$message){$script:checks++;if(!$condition){throw $message}}
Check ($sample.ReturnType.FullName -eq 'System.Void') 'SampleAnimation must return void'
Check ($sample.Parameters[0].ParameterType.FullName -eq 'UnityEngine.GameObject' -and !$sample.Parameters[0].ParameterType.IsByReference) 'SampleAnimation target must be reference object'
Check ($sample.Parameters[1].ParameterType.FullName -eq 'System.Single' -and !$sample.Parameters[1].ParameterType.IsByReference) 'SampleAnimation time must be primitive float'
Check (!$clip.IsValueType) 'AnimationClip receiver must be reference type'

$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')) {
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll') {
  try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
  $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
 }
}
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('PlayerAppearanceProbe').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($dir in @('MultiplayerDev/Source','MultiplayerDev/Shared')) {
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.cs') {
  $compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName),$null,$file.FullName)))
 }
}
$stream=[IO.MemoryStream]::new()
try {
 $result=$compilation.Emit($stream)
 $result.Diagnostics|Where-Object Severity -eq 'Error'|ForEach-Object ToString
 if(!$result.Success){throw 'Full source compilation failed'}
 $stream.Position=0;$compiled=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($stream)
 $appearance=$compiled.MainModule.Types|Where-Object FullName -eq 'NivalisTogether.PlayerAppearance'
 $types=@($appearance)+@($appearance.NestedTypes)
 $calls=@($types|ForEach-Object Methods|Where-Object HasBody|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object Operand)
 $components=@($calls|Where-Object {$_.Name -eq 'AddComponent'})
 Check ($components.Count -eq 4) 'Expected only the four explicitly copied visual component types'
 foreach($component in $components){Check ($component -is [Mono.Cecil.GenericInstanceMethod] -and $component.GenericArguments[0].FullName -in @('UnityEngine.MeshFilter','UnityEngine.MeshRenderer','UnityEngine.SkinnedMeshRenderer','UnityEngine.Animator')) 'No gameplay, physics, navigation or AI component may be created'}
 $copies=@($calls|Where-Object {$_.Name -eq 'Instantiate'})
 Check ($copies.Count -eq 1 -and $copies[0] -is [Mono.Cecil.GenericInstanceMethod] -and $copies[0].GenericArguments[0].FullName -eq 'UnityEngine.AnimationClip') 'Only clip assets may be instantiated; never NPC prefabs'
 Check (@($calls|Where-Object {$_.Name -eq 'set_runtimeAnimatorController'}).Count -eq 0) 'No native state machine controller may be attached'
 Check (@($calls|Where-Object {$_.Name -eq 'set_events'}).Count -eq 1) 'Owned clip events must be cleared before sampling'
 Check (@($calls|Where-Object {$_.Name -eq 'set_fireEvents'}).Count -eq 1) 'Animator animation events must be explicitly disabled'
 Check (@($calls|Where-Object {$_.DeclaringType.FullName -like 'HarmonyLib*'}).Count -eq 0) 'Appearance must not introduce native hooks'
 Check (@($calls|Where-Object {$_.Name -eq 'Add' -and $_.DeclaringType.FullName -like 'Il2CppSystem.Collections.Generic.List*'}).Count -eq 0) 'No generated native List value-wrapper writes'
 $compiled.Dispose()
}finally{$stream.Dispose();$animation.Dispose()}
"Appearance native ABI/component isolation PASS $checks checks"
'Full Source + Shared in-memory compile PASS; native visual acceptance still pending'
