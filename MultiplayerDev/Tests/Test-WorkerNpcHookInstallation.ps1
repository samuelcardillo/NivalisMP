$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$outRoot=Join-Path $PSScriptRoot 'worker-npc-hook-probe-build'
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
$npc=[IO.File]::ReadAllText((Join-Path $devRoot 'Source/WorkerNpcSimulation.cs'))
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $devRoot 'Source/NpcWorldReplication.cs'))).GetRoot()
$limiter=($tree.DescendantNodes() | Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'NpcDiscontinuityLog'} | Select-Object -First 1).ToFullString()
# Execute the actual production InstallPatches against the installed generated
# interop types, including inherited overloads. Only Harmony's detour operation
# and process-role state are replaced; no Unity initialization or game process.
$facade=@'
using System;
using System.Collections.Generic;
using System.Reflection;
namespace HarmonyLib {
 public sealed class Harmony {
  public static readonly List<MethodInfo> Installed=new List<MethodInfo>();
  public Harmony(string id){}
  public void Patch(MethodInfo original,HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod finalizer=null){
   if(original==null)throw new Exception("Null native hook");Installed.Add(original);
  }
 }
 public sealed class HarmonyMethod {public HarmonyMethod(MethodInfo method){if(method==null)throw new Exception("Null patch handler");}}
}
namespace NivalisTogether.Dedicated {
 public static class DedicatedWorldRuntime {public static bool IsDedicatedServer=>false;public static bool IsIsolated=>false;}
'@
$facade+=$limiter+'}'
$runner=@'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
public static class NpcHookInstallationProbe {
 public static int Main(string[] args){
  string root=args[0];
  AssemblyLoadContext.Default.Resolving+=(context,name)=>{
   foreach(string relative in new[]{"BepInEx/interop","BepInEx/core"}){
    string file=Path.Combine(root,relative,name.Name+".dll");
    if(File.Exists(file))return context.LoadFromAssemblyPath(file);
   }
   return null;
  };
  try{return Run();}catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
 [MethodImpl(MethodImplOptions.NoInlining)]
 private static int Run(){
  NivalisTogether.Dedicated.WorkerNpcSimulation.Configure(_=>false);
  var methods=HarmonyLib.Harmony.Installed;
  if(methods.Count!=27)throw new Exception("Expected twenty-seven native patch installations.");
  foreach(var method in methods){
   var parameters=method.GetParameters();
   if(method.Name=="Tick"&&(method.DeclaringType.FullName!="Nivalis.GhostSystem.Ai.WanderAgentAction+WanderToSubAction"||parameters[1].ParameterType.FullName!="Nivalis.GhostSystem.Ai.WanderAgentAction+State"))throw new Exception("Installed inherited rather than concrete Tick.");
   Console.WriteLine(method.DeclaringType.FullName+"."+method.Name+"("+string.Join(",",Array.ConvertAll(parameters,p=>p.ParameterType.FullName))+")");
  }
  NivalisTogether.Dedicated.WorkerNpcSimulation.Reset();
  Console.WriteLine("Worker NPC actual InstallPatches PASS against installed interop; detours substituted, no native execution.");return 0;
 }
}
'@
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($relative in @('dotnet','BepInEx/core','BepInEx/interop')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $relative) -Filter '*.dll'){
  if($file.Name -eq '0Harmony.dll'){continue}
  try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
  $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
 }
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithMainTypeName('NpcHookInstallationProbe')
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('NpcHookInstallationProbe').WithOptions($options).AddReferences($refs.ToArray())
foreach($source in @($npc,$facade,$runner)){$comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))}
$assembly=Join-Path $outRoot 'NpcHookInstallationProbe.dll'
$stream=[IO.File]::Create($assembly)
try{$result=$comp.Emit($stream)}finally{$stream.Dispose()}
$result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object ToString
if(!$result.Success){throw 'Native hook installation probe failed to compile.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outRoot 'NpcHookInstallationProbe.runtimeconfig.json')
& dotnet $assembly $gameRoot
if($LASTEXITCODE -ne 0){throw 'Native hook installation reflection probe failed.'}
