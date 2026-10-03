$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dialogueTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/DialogueReplication.cs')))
$businessTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/BusinessReplication.cs')))
$protocolTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Shared/DedicatedProtocol.cs')))
function Select-ActualClass($tree, [string]$name) {
    $found = @($tree.GetRoot().DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $name
    })
    if ($found.Count -ne 1) { throw ('Missing production class ' + $name) }
    $found[0].ToFullString()
}
function Select-ActualMethod($tree, [string]$name) {
    $found = @($tree.GetRoot().DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq $name
    })
    if ($found.Count -ne 1) { throw ('Missing production method ' + $name) }
    $found[0].ToFullString()
}
$production = 'using System; using System.IO; namespace NivalisTogether.Dedicated {' +
    (Select-ActualClass $dialogueTree 'DialogueAccountBinding') +
    (Select-ActualClass $dialogueTree 'DialogueResult') +
    (Select-ActualClass $businessTree 'BusinessResult') +
    (Select-ActualClass $protocolTree 'PlayerPose') +
    'internal static class ActualAdapterMethods {' +
    (Select-ActualMethod $businessTree 'RequireActorAccount') +
    (Select-ActualMethod $dialogueTree 'AccountDialogueFinalizer') +
    'internal static void Require(PlayerPose pose,string account)=>RequireActorAccount(pose,account);' +
    'internal static Exception Finish(Exception error,IDisposable scope)=>AccountDialogueFinalizer(error,scope); }' +
    'internal static class PlayerAccountRuntime { internal static int Faults; public static void Fault(string reason) { Faults++; } } }'
$test = @'
using System;
using System.Text.Json;
using NivalisTogether.Dedicated;
internal static class AccountAdapterTests {
    private static int checks;
    private static void Check(bool value,string message) {checks++;if(!value)throw new Exception(message);}
    private static void Throws(Action action,string message) {checks++;try{action();}catch{return;}throw new Exception(message);}
    private sealed class Scope : IDisposable {internal int Count;internal bool Fails;public void Dispose(){Count++;if(Fails)throw new InvalidOperationException("restore");}}
    public static int Main() {
        string accountA=Guid.NewGuid().ToString("N"),accountB=Guid.NewGuid().ToString("N");
        string playerA=Guid.NewGuid().ToString("N"),playerB=Guid.NewGuid().ToString("N"),reconnected=Guid.NewGuid().ToString("N");
        var binding=new DialogueAccountBinding();
        Check(binding.AccountId==""&&binding.PlayerId=="","empty initial owner");
        foreach(string bad in new[]{null,"","bad","not-an-account"}) {
            Check(!binding.CanBind(bad),"invalid account cannot claim dialogue");
            Check(!binding.Bind(playerA,bad),"invalid account rejected without mutation");
            Check(!binding.Bind(bad,accountA),"invalid actor rejected without mutation");
        }
        Check(binding.Bind(playerA,accountA)&&binding.Matches(playerA,accountA),"first authenticated actor owns conversation");
        Check(!binding.Matches(playerA,accountB)&&!binding.Matches(playerB,accountA),"both account and session are required");
        Check(!binding.Bind(playerB,accountB)&&binding.Matches(playerA,accountA),"other account cannot steal active branch");
        binding.Release();
        Check(binding.AccountId==accountA&&binding.PlayerId=="","disconnect retains conversation account");
        Check(!binding.Matches(playerA,accountA)&&!binding.CanBind(accountB),"disconnected session cannot evaluate or transfer branch");
        Check(binding.Bind(reconnected,accountA)&&binding.Matches(reconnected,accountA)&&!binding.Matches(playerA,accountA),"same account reconnect gets new session authority");
        binding.Restore(accountA);
        Check(binding.AccountId==accountA&&binding.PlayerId==""&&!binding.CanBind(accountB),"restart restores account with no assumed live actor");
        Throws(()=>binding.Restore("bad"),"corrupt persisted owner rejected");
        Check(binding.Bind(reconnected,accountA),"restored same account can resume");
        binding.Clear();
        Check(binding.AccountId==""&&binding.PlayerId==""&&binding.Bind(playerB,accountB),"native completion releases account binding");
        binding.Restore("");
        Check(binding.AccountId==""&&binding.PlayerId=="","empty restore is neutral");
        for(int i=0;i<32;i++) {
            string account=Guid.NewGuid().ToString("N"),actor=Guid.NewGuid().ToString("N");
            binding.Clear();Check(binding.Bind(actor,account),"fresh conversation binds");
            binding.Release();Check(!binding.CanBind(Guid.NewGuid().ToString("N")),"each disconnected conversation remains account restricted");
            Check(binding.Bind(Guid.NewGuid().ToString("N"),account),"each account may reconnect");
        }
        ActualAdapterMethods.Require(new PlayerPose{AccountId=accountA},accountA);Check(true,"craft actor binding accepts matching account");
        Throws(()=>ActualAdapterMethods.Require(new PlayerPose{AccountId=accountB},accountA),"craft captured account cannot use replacement actor");
        Throws(()=>ActualAdapterMethods.Require(null,accountA),"craft missing actor rejected");
        Throws(()=>ActualAdapterMethods.Require(new PlayerPose{AccountId="bad"},"bad"),"matching malformed craft account rejected");
        Check(new BusinessResult().NeedsCheckpoint,"craft completion and ordinary business mutations require checkpoint by default");
        Check(!new DialogueResult{Success=true}.NeedsCheckpoint,"dialogue step acknowledgement proceeds while native dialogue blocks checkpoint; rewards remain separately durable");
        string start=JsonSerializer.Serialize(new BusinessResult{Success=true,NeedsCheckpoint=false});
        Check(JsonDocument.Parse(start).RootElement.GetProperty("NeedsCheckpoint").ValueKind==JsonValueKind.False,"craft reservation acknowledgement explicitly bypasses forced save");
        var scope=new Scope();
        Check(ActualAdapterMethods.Finish(null,scope)==null&&scope.Count==1&&PlayerAccountRuntime.Faults==0,"normal native scope disposes exactly once");
        var nativeError=new InvalidOperationException("native");scope=new Scope();
        Check(ReferenceEquals(ActualAdapterMethods.Finish(nativeError,scope),nativeError)&&scope.Count==1&&PlayerAccountRuntime.Faults==1,"native failure faults before scope restoration and preserves exception");
        scope=new Scope{Fails=true};int before=PlayerAccountRuntime.Faults;
        Check(ActualAdapterMethods.Finish(null,scope) is InvalidOperationException&&scope.Count==1&&PlayerAccountRuntime.Faults==before+1,"restoration failure faults and propagates");
        scope=new Scope{Fails=true};before=PlayerAccountRuntime.Faults;
        Check(ReferenceEquals(ActualAdapterMethods.Finish(nativeError,scope),nativeError)&&scope.Count==1&&PlayerAccountRuntime.Faults==before+2,"native plus restore failures preserve original and remain closed");
        Check(ActualAdapterMethods.Finish(nativeError,null)==nativeError,"unscoped native exception remains unchanged");
        Console.WriteLine("Account adapters PASS: "+checks+" assertions using actual production binding, craft validation, result DTO and finalizer.");return 0;
    }
}
'@
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AccountAdapterTests').WithOptions($options).AddReferences($refs.ToArray())
foreach($source in @($production,$test)) { $compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source))) }
$memory=[IO.MemoryStream]::new()
try {
    $result=$compilation.Emit($memory)
    foreach($diagnostic in $result.Diagnostics) {if($diagnostic.Severity -ne 'Hidden'){$diagnostic.ToString()}}
    if(!$result.Success){throw 'Account adapter tests did not compile'}
    $outDir=Join-Path $PSScriptRoot 'account-adapter-probe-build'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $output=Join-Path $outDir 'AccountAdapterTests.dll'
    [IO.File]::WriteAllBytes($output,$memory.ToArray())
}finally{$memory.Dispose()}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}' | Set-Content -LiteralPath (Join-Path $outDir 'AccountAdapterTests.runtimeconfig.json')
& dotnet $output
if($LASTEXITCODE -ne 0){throw 'Account adapter tests failed'}

# No new native value-type argument is introduced by the account scopes:
# node evaluation is a void method on a reference receiver. The two request
# hooks retain the already-existing DialogueEventArgs argument; __state is
# exclusively managed Harmony state and is never native struct storage.
Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
try {
    $manager=$assembly.MainModule.Types | Where-Object FullName -eq 'Nivalis.Dialogue.DialogueTreeProgressManager'
    if($manager.BaseType.FullName -in @('System.ValueType','Il2CppSystem.ValueType')){throw 'Unsafe native dialogue receiver'}
    foreach($name in @('Update','HandleCurrentDialogueNode','OnDialogueRequest','ProcessDialogueRequestNow')) {
        $method=@($manager.Methods | Where-Object Name -eq $name)
        if($method.Count -ne 1 -or $method[0].IsStatic -or $method[0].ReturnType.FullName -ne 'System.Void'){throw ('Unexpected dialogue target '+$name)}
        $signature=@($method[0].Parameters | ForEach-Object {$_.ParameterType.FullName})
        $expected=if($name -in @('Update','HandleCurrentDialogueNode')){''}else{'Nivalis.Dialogue.DialogueEventArgs'}
        if(($signature -join ',') -ne $expected){throw ('Changed native signature '+$name)}
    }
    'Account adapter ABI PASS: 2 safe void/no-argument manager hooks; 2 existing request signatures unchanged; managed IDisposable state only.'
} finally { $assembly.Dispose() }
