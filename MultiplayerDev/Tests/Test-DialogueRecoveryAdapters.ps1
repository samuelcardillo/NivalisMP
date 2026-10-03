$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/DialogueReplication.cs')))
$protocol = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Shared/DedicatedProtocol.cs')))
function Actual-Class($syntax, [string]$name) {
    $found = @($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $name })
    if($found.Count -ne 1){throw ('Missing actual class ' + $name)}
    $found[0].ToFullString()
}
function Actual-Method($syntax, [string]$name) {
    $found = @($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq $name })
    if($found.Count -ne 1){throw ('Missing actual method ' + $name)}
    $found[0].ToFullString()
}
$actual = 'using System; using System.IO; using System.Text; using System.Text.Json; using System.Collections.Generic; namespace NivalisTogether.Dedicated {'
foreach($name in @('InteractionRequest','WorldEvent')){$actual += Actual-Class $protocol $name}
$actual += 'public static class DedicatedProtocol { public static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive=false, MaxDepth=24 };' + (Actual-Method $protocol 'Text') + '}'
foreach($name in @('DialogueAccountBinding','DialogueRecoveryStepGate','DialogueInputGate','DialogueChoiceState','SharedDialogueState')){$actual += Actual-Class $tree $name}
$actual += 'internal static partial class ActualRecovery {'
foreach($name in @('SetRecoveryPlayerPresent','SendRecoveryChoice','CanSendRecovery','CanContinueDuringShutdown','ApplyRecovery','ApplyRecoveryRejection','HandleRecoveryInteraction','RecoveryRequestBinding','RecoveryEvent','RecoveryHeader','RecoveryMessage','TickRecovery','BindOwner')){$actual += Actual-Method $tree $name}
$actual += '}}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
    try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('DialogueRecoveryAdapterTests').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)).AddReferences($refs.ToArray())
$sources=@($actual,[IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Shared/DialogueRecoveryProtocol.cs')),[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DialogueRecoveryAdapterTests.cs')))
foreach($source in $sources){$compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)))}
$memory=[IO.MemoryStream]::new()
try{
    $result=$compilation.Emit($memory)
    foreach($diagnostic in $result.Diagnostics){if($diagnostic.Severity -ne 'Hidden'){$diagnostic.ToString()}}
    if(!$result.Success){throw 'Dialogue recovery adapter tests did not compile'}
    $outDir=Join-Path $PSScriptRoot 'dialogue-recovery-adapter-probe-build'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $output=Join-Path $outDir 'DialogueRecoveryAdapterTests.dll'
    [IO.File]::WriteAllBytes($output,$memory.ToArray())
}finally{$memory.Dispose()}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir 'DialogueRecoveryAdapterTests.runtimeconfig.json')
& dotnet $output
if($LASTEXITCODE -ne 0){throw 'Dialogue recovery adapter tests failed'}
