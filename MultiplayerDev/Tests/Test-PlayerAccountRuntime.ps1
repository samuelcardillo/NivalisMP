$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$c=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('PlayerAccountLifecycleProbe'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
$files=@((Join-Path $gameRoot 'MultiplayerDev/Source/PlayerAccountRuntime.cs'),(Join-Path $PSScriptRoot 'PlayerAccountRuntimeTests.cs'))
$files+=@(Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MultiplayerDev/Shared') -Filter '*.cs'|ForEach-Object FullName)
foreach($file in $files){$c=$c.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file),$null,$file)))}
$ms=[IO.MemoryStream]::new();$result=$c.Emit($ms)
$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString
if(!$result.Success){throw 'Production account lifecycle test compilation failed'}
$assembly=[Reflection.Assembly]::Load($ms.ToArray());$ms.Dispose()
$checks=$assembly.GetType('NivalisTogether.Dedicated.PlayerAccountRuntimeProbe').GetMethod('Run').Invoke($null,@())
"Production account lifecycle PASS $checks checks (native boundary doubles, no game process)"

Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$native=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
$all=[Collections.Generic.List[object]]::new()
function Visit($t){$all.Add($t);foreach($n in $t.NestedTypes){Visit $n}}
foreach($t in $native.MainModule.Types){Visit $t}
$checks=0
function Check([bool]$condition,[string]$message){$script:checks++;if(!$condition){throw $message}}
foreach($spec in @(@('Nivalis.GhostSystem.CustomerLoop.VenueAreaGhost','PayStaff'),@('Nivalis.RentManager','CollectReceipt'),@('Nivalis.GhostSystem.CustomerLoop.VisitVenueAgentActionType/LeaveVenueSubAction','LeaveReview'),@('Nivalis.UI.EndOfDay.EndOfDayDebtSection','Show'),@('Nivalis.UI.EndOfDay.EndOfDayDebtSection','ChoicePicked'))){
 $t=$all|Where-Object FullName -eq $spec[0];$m=@($t.Methods|Where-Object Name -eq $spec[1])
 Check ($m.Count -eq 1 -and $m[0].ReturnType.FullName -eq 'System.Void') ('Exact void hook '+$spec[1])
 foreach($p in $m[0].Parameters){Check (!$p.ParameterType.IsByReference) ('No byref native hook input '+$spec[1]);$pt=$all|Where-Object FullName -eq $p.ParameterType.FullName;Check ($p.ParameterType.FullName -in @('System.Int32','System.Single') -or ($pt -and !$pt.IsValueType)) ('Only proven references/primitives '+$spec[1])}
}
$container=$all|Where-Object FullName -eq 'Nivalis.InventorySystem.ItemContainer'
foreach($name in @('_items','_typeMap')){
 $setter=($container.Properties|Where-Object Name -eq $name).SetMethod
 Check (@($setter.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'il2cpp_gc_wbarrier_set_field'}).Count -eq 1) ('GC barrier for quiet '+$name+' swap')
}
$decay=$container.Methods|Where-Object Name -eq UpdateItemDecay
Check ($decay.ReturnType.FullName -eq 'System.Int32' -and $decay.Parameters[0].ParameterType.FullName -eq 'System.Boolean' -and $decay.Parameters[1].ParameterType.FullName -eq 'System.Int32') 'Decay outer API uses only primitive values'
"Native account hook/layout ABI PASS $checks checks"
