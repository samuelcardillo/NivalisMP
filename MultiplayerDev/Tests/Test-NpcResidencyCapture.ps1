$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$npcPath=Join-Path $devRoot 'Source/NpcWorldReplication.cs'
$sessionPath=Join-Path $devRoot 'Source/DedicatedSession.cs'
$npc=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($npcPath)).GetRoot()
$session=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sessionPath)).GetRoot()
function Methods($tree,[string[]]$names) {
 $found=@($tree.DescendantNodes()|Where-Object {$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names})
 if($found.Count-ne$names.Count){throw 'Production capture method extraction failed.'}
 return ($found|ForEach-Object ToFullString)-join "`n"
}
$capture=Methods $npc @('Capture','CaptureDuringWorkerResidency','CaptureState')
$pump=Methods $session @('CaptureResidencyNpcs')
$dto=@($npc.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -in @('NpcWorldFrame','NpcWorldState','AnimatorLayerSnapshot','AnimatorParameterSnapshot')})
$code=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NpcResidencyCaptureTests.cs'))
$code=$code.Replace('/*PRODUCTION_CAPTURE*/',$capture).Replace('/*PRODUCTION_PUMP*/',$pump).Replace('/*PRODUCTION_DTO*/',($dto|ForEach-Object ToFullString)-join "`n")
Add-Type -TypeDefinition $code
$checks=[NpcResidencyTests.Probe]::Run()
$worker=Methods $session @('TickWorker')
if($worker -notmatch 'if \(WorkerSceneResidency.IsBusy\)\s*\{\s*TravelReplication.Tick\(Now\); ApartmentReplication.Tick\(Now\);\s*CaptureResidencyNpcs\(\);\s*return;') {throw 'Worker busy branch no longer pumps the bounded capture lane.'}
"PASS: $checks production-extracted residency capture/queue checks plus worker busy-branch wiring. No native process."
Get-FileHash -LiteralPath $npcPath,$sessionPath -Algorithm SHA256|Select-Object Path,Hash
