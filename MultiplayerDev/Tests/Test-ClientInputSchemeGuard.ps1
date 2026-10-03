$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source=Join-Path $gameRoot 'MultiplayerDev/Source/ClientInputSchemeGuard.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$policy=$tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'ClientInputSchemePolicy' } | Select-Object -First 1
if(!$policy){throw 'Production policy missing'}
$tests=@'
public static class InputSchemePolicyProbe {
    private static int checks;
    private static void Check(bool value,string message) { checks++; if(!value)throw new Exception(message); }
    public static int Run() {
        Check(!ClientInputSchemePolicy.ShouldBlock(true,false,true,null),"Unavailable asset must preserve native path");
        Check(!ClientInputSchemePolicy.ShouldBlock(true,false,true,Array.Empty<bool?>()),"Empty schemes must preserve native path");
        var large=new bool?[65];Array.Fill(large,false);
        Check(!ClientInputSchemePolicy.ShouldBlock(true,false,true,large),"Unbounded scheme list must preserve native path");
        var boundary=new bool?[64];Array.Fill(boundary,false);
        Check(ClientInputSchemePolicy.ShouldBlock(true,false,true,boundary),"Supported scan boundary");
        boundary[63]=true;Check(!ClientInputSchemePolicy.ShouldBlock(true,false,true,boundary),"Last supported scheme preserves native path");
        boundary[63]=null;Check(!ClientInputSchemePolicy.ShouldBlock(true,false,true,boundary),"Last unreadable scheme preserves native path");
        // Exhaust every supported/unsupported/unreadable scheme combination up to seven schemes.
        for(int length=1;length<=7;length++) {
            int cases=1;for(int i=0;i<length;i++)cases*=3;
            for(int encoded=0;encoded<cases;encoded++) {
                var items=new bool?[length];int digits=encoded;bool provenUnsupported=true;
                for(int i=0;i<length;i++){int digit=digits%3;digits/=3;items[i]=digit==0?false:digit==1?true:(bool?)null;provenUnsupported&=digit==0;}
                Check(ClientInputSchemePolicy.ShouldBlock(true,false,true,items)==provenUnsupported,"Only all-negative evidence may block");
                Check(!ClientInputSchemePolicy.ShouldBlock(false,false,true,items),"Single-player/disabled remains native");
                Check(!ClientInputSchemePolicy.ShouldBlock(true,true,true,items),"Worker remains native");
                Check(!ClientInputSchemePolicy.ShouldBlock(true,false,false,items),"Null device remains native");
            }
        }
        return checks;
    }
}
'@
Add-Type -TypeDefinition ('using System; namespace NivalisTogether {'+$policy.ToFullString()+$tests+'}')
$count=[NivalisTogether.InputSchemePolicyProbe]::Run()
"Client input scheme policy PASS $count assertions"

Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$game=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
$input=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Unity.InputSystem.dll'))
$checks=0
function Assert-Abi([bool]$condition,[string]$message){$script:checks++;if(!$condition){throw $message}}
$manager=$game.MainModule.Types|Where-Object FullName -eq 'Nivalis.PlayerInputManager'
$switch=@($manager.Methods|Where-Object Name -eq 'SwitchControlScheme')
Assert-Abi ($switch.Count -eq 1) 'Expected one native SwitchControlScheme'
Assert-Abi ($switch[0].ReturnType.FullName -eq 'System.Void') 'Hook return must be void'
Assert-Abi ($switch[0].Parameters.Count -eq 1) 'Hook must have one argument'
Assert-Abi ($switch[0].Parameters[0].ParameterType.FullName -eq 'UnityEngine.InputSystem.InputDevice') 'Hook argument must be InputDevice'
$device=$input.MainModule.Types|Where-Object FullName -eq 'UnityEngine.InputSystem.InputDevice'
Assert-Abi (!$device.IsValueType -and !$switch[0].Parameters[0].ParameterType.IsByReference) 'Hook argument must be reference type by value'
Assert-Abi (!$manager.IsValueType) 'Hook receiver must be reference type'
$scheme=$input.MainModule.Types|Where-Object FullName -eq 'UnityEngine.InputSystem.InputControlScheme'
$supports=$scheme.Methods|Where-Object Name -eq 'SupportsDevice'
Assert-Abi ($supports.ReturnType.FullName -eq 'System.Boolean' -and $supports.Parameters.Count -eq 1 -and $supports.Parameters[0].ParameterType.FullName -eq $device.FullName) 'SupportsDevice must take reference and return bool'
Assert-Abi (@($supports.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'il2cpp_object_unbox'}).Count -eq 2) 'SupportsDevice must unbox receiver and Boolean result'
$user=$input.MainModule.Types|Where-Object FullName -eq 'UnityEngine.InputSystem.Users.InputUser'
$paired=$user.Methods|Where-Object Name -eq 'get_pairedDevices'
Assert-Abi ($user.IsValueType -and @($user.Fields|Where-Object {!$_.IsStatic}).Count -eq 1 -and @($user.Fields|Where-Object {!$_.IsStatic})[0].FieldType.FullName -eq 'System.UInt32') 'InputUser must remain blittable uint ID'
Assert-Abi ($paired.ReturnType.FullName -like 'UnityEngine.InputSystem.Utilities.ReadOnlyArray*' -and $paired.ReturnType.FullName -notlike '*Nullable*') 'Pairing return must be non-Nullable boxed struct'
$readOnly=$input.MainModule.Types|Where-Object FullName -like 'UnityEngine.InputSystem.Utilities.ReadOnlyArray*'
$getCount=$readOnly.Methods|Where-Object Name -eq 'get_Count'
Assert-Abi ($getCount.ReturnType.FullName -eq 'System.Int32' -and @($getCount.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'il2cpp_object_unbox'}).Count -eq 2) 'Paired Count must unbox receiver and primitive result'

# Compile the complete current plugin to memory and inspect the emitted hook calls.
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($dir in @('dotnet','BepInEx/core','BepInEx/interop')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll') {
        try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('InputSchemeGuardProbe').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
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
    $stream.Position=0
    $compiled=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($stream)
    $guard=$compiled.MainModule.Types|Where-Object FullName -eq 'NivalisTogether.ClientInputSchemeGuard'
    $before=$guard.Methods|Where-Object Name -eq 'BeforeSwitch'
    $calls=@($before.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object Operand)
    Assert-Abi (@($calls|Where-Object {$_.DeclaringType.FullName -eq $scheme.FullName -and $_.Name -like 'op_*'}).Count -eq 0) 'No overloaded native value-type comparison may be called'
    Assert-Abi (@($calls|Where-Object {$_.Name -eq 'SupportsDevice'}).Count -eq 1) 'Guard must consult native configured scheme support'
    Assert-Abi (@($calls|Where-Object {$_.Name -in @('UnpairDevices','PerformPairingWithDevice','ActivateControlScheme','DisableDevice','RemoveDevice','MakeCurrent')}).Count -eq 0) 'Guard must not mutate device state'
    $compiled.Dispose()
}finally{$stream.Dispose();$input.Dispose();$game.Dispose()}
"Client input scheme ABI PASS $checks checks"
'Full Source + Shared in-memory compile PASS (no installation)'
