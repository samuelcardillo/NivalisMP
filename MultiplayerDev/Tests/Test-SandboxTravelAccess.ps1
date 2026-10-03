$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$source=Join-Path $devRoot 'Source/MultiplayerSandboxRuntime.cs'
$syntax=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$names=@('IsPublicLocationDefinition','IsPublicTravelDestination','PublicTerminalAccess','PublicMapAccess')
$methods=@($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $names })
if($methods.Count -ne $names.Count) { throw 'Expected exact production public travel access methods.' }
$code='using System; using System.Collections.Generic; using Nivalis; namespace NivalisTogether.Dedicated { public static partial class MultiplayerSandboxRuntime { public static bool Active; private static IntPtr accessManager; private static readonly HashSet<IntPtr> PublicLocations=new HashSet<IntPtr>();'+
    (($methods | ForEach-Object ToFullString) -join [Environment]::NewLine)+'}}'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SandboxTravelAccessTests'+[Guid]::NewGuid().ToString('N')).WithOptions($options).AddReferences($refs.ToArray())
foreach($text in @($code,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SandboxTravelAccessTests.cs')))) {
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)))
}
$stream=[IO.MemoryStream]::new()
try {
    $result=$comp.Emit($stream)
    $result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object ToString
    if(!$result.Success) { throw 'Sandbox travel access test compilation failed.' }
    $test=[Reflection.Assembly]::Load($stream.ToArray())
    $checks=$test.GetType('NivalisTogether.Dedicated.MultiplayerSandboxRuntime').GetMethod('Run').Invoke($null,$null)
} finally { $stream.Dispose() }
Write-Output "PASS: $checks extracted production travel access checks."

Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
$nativeChecks=0
try {
    foreach($spec in @(@('Nivalis.SceneTransitionInteractable','CanInteract'),@('Nivalis.MapTile','get_IsFastTravelAllowed'))) {
        $type=$assembly.MainModule.Types | Where-Object FullName -eq $spec[0]
        $matches=@($type.Methods | Where-Object { $_.Name -eq $spec[1] -and $_.Parameters.Count -eq 0 -and !$_.IsStatic -and $_.ReturnType.FullName -eq 'System.Boolean' })
        if($matches.Count -ne 1) { throw "Native travel access hook mismatch: $($spec -join '.')" }
        $nativeChecks++
    }
    $player=($assembly.MainModule.Types|Where-Object FullName -eq 'Nivalis.PlayerManager').NestedTypes|Where-Object Name -eq Player
    if(($player.Properties|Where-Object Name -eq IsHoldingObject).PropertyType.FullName -ne 'System.Boolean') { throw 'Native held-object guard mismatch.' }; $nativeChecks++
} finally { $assembly.Dispose() }
Write-Output "PASS: $nativeChecks native travel access ABI checks."

# Compile the entire edited production file against actual interop assemblies,
# with only the two other project services replaced by signature-only facades.
foreach($dir in @('BepInEx/core','BepInEx/interop')) {
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll') {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$facade='namespace NivalisTogether.Dedicated { public static class DedicatedWorldRuntime { public static bool IsIsolated, IsWorldReady; } public static class DialogueReplication { public static void InstallSandboxPolicy() {} public static void ApplySandboxPolicy() {} } }'
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SandboxTravelNativeCompile').WithOptions($options).AddReferences($refs.ToArray())
foreach($text in @([IO.File]::ReadAllText($source),$facade)) {
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)))
}
$stream=[IO.MemoryStream]::new()
try {
    $result=$comp.Emit($stream)
    $result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object ToString
    if(!$result.Success) { throw 'Edited sandbox runtime failed native-reference compilation.' }
} finally { $stream.Dispose() }
Write-Output 'PASS: entire edited sandbox runtime compiles against actual native interop; no game execution.'
[pscustomobject]@{ Utc=[DateTime]::UtcNow.ToString('O'); Passed=$true; Assertions=$checks; NativeAbi=$nativeChecks; NativeReferenceCompile=$true; SourceSHA256=(Get-FileHash -LiteralPath $source).Hash; Scope='Extracted production policy with native facades, declared ABI and full file native-reference compile; does not claim live travel.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $devRoot 'inspection/sandbox-travel-access-tests.json')
