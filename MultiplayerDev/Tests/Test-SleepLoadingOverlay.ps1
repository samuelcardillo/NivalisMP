$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath = Join-Path $gameRoot 'MultiplayerDev/Source/SleepLoadingOverlay.cs'
$source = [IO.File]::ReadAllText($sourcePath)

# Compile only this UI against installed wrappers without loading Unity/native code,
# writing a plugin DLL, or interrupting a running dedicated server.
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($directory in @('dotnet', 'BepInEx/core', 'BepInEx/interop')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $directory) -Filter '*.dll') {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SleepOverlayCheck').WithOptions($options).AddReferences($refs.ToArray())
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $null, $sourcePath)
$compilation = $compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
$stream = [IO.MemoryStream]::new()
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object ToString
if (!$result.Success) { throw 'Shared sleep overlay does not compile against installed game wrappers.' }
'PASS: shared sleep overlay compiles in memory against installed wrappers.'

# Execute the production's pure identity predicate without initializing the
# Unity-containing static class. This catches same-caption/different-step choices
# such as summary Continue followed by ranking Continue.
$predicate = @($tree.GetRoot().DescendantNodes() | Where-Object {
    $_.GetType().Name -eq 'MethodDeclarationSyntax' -and $_.Identifier.ValueText -eq 'ChoicePresentationChanged'
})
if ($predicate.Count -ne 1) { throw 'Production sleep choice identity predicate missing or ambiguous.' }
$pureSource = 'using System; public static class SleepChoiceIdentityProbe {' + $predicate[0].ToFullString() + '}'
$pureTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($pureSource)
$pureCompilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SleepChoiceIdentityProbe' + [Guid]::NewGuid().ToString('N')).WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($pureTree))
$pureStream = [IO.MemoryStream]::new()
try {
    $pureResult = $pureCompilation.Emit($pureStream)
    if (!$pureResult.Success) { throw ('Sleep identity probe compile failed: ' + ($pureResult.Diagnostics -join "`n")) }
    $pureAssembly = [Reflection.Assembly]::Load($pureStream.ToArray())
} finally { $pureStream.Dispose() }
$identityMethod = $pureAssembly.GetType('SleepChoiceIdentityProbe').GetMethod('ChoicePresentationChanged', [Reflection.BindingFlags]'NonPublic,Static')
$identityCases = @(
    @{ Name='same step preserves pending Continue'; Before='a:1:owner:summary'; After='a:1:owner:summary'; Old=@('Continue'); New=@('Continue'); Changed=$false },
    @{ Name='ranking Continue rearms after summary Continue'; Before='a:1:owner:summary'; After='a:2:owner:ranking'; Old=@('Continue'); New=@('Continue'); Changed=$true },
    @{ Name='new owner invalidates the previous callback'; Before='a:2:old:ranking'; After='a:2:new:ranking'; Old=@('Continue'); New=@('Continue'); Changed=$true },
    @{ Name='new epoch invalidates same-caption choices'; Before='a:1:owner:summary'; After='b:1:owner:summary'; Old=@('Continue'); New=@('Continue'); Changed=$true },
    @{ Name='waiting heartbeat preserves pending Cancel'; Before='a:waiting'; After='a:waiting'; Old=@('Cancel sleep'); New=@('Cancel sleep'); Changed=$false },
    @{ Name='new label updates available choice'; Before='a:waiting'; After='a:waiting'; Old=@('Cancel sleep'); New=@('Continue'); Changed=$true },
    @{ Name='removing choices invalidates pending actions'; Before='a:waiting'; After='a:waiting'; Old=@('Cancel sleep'); New=@(); Changed=$true },
    @{ Name='same loading state remains stable'; Before='a:synchronizing'; After='a:synchronizing'; Old=@(); New=@(); Changed=$false }
)
foreach ($case in $identityCases) {
    $actual = $identityMethod.Invoke($null, [object[]]@($case.Before, $case.After, [string[]]$case.Old, [string[]]$case.New))
    if ($actual -ne $case.Changed) { throw ('Sleep choice identity regression: ' + $case.Name) }
    'PASS choice identity: ' + $case.Name
}

Add-Type -Path (Join-Path $gameRoot 'BepInEx/core/Mono.Cecil.dll')
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameRoot 'BepInEx/interop/Assembly-CSharp.dll'))
$checks = @(
    @('Nivalis.PlayerCharacterController','DisableMovement','Nivalis.OverrideableBool/OverrideLock','Il2CppSystem.Object'),
    @('Nivalis.PlayerCharacterController','LockCamera','Nivalis.OverrideableBool/OverrideLock','Il2CppSystem.Object'),
    @('Nivalis.PlayerInteraction','DisableInteractions','Nivalis.OverrideableBool/OverrideLock','Il2CppSystem.Object'),
    @('Nivalis.OverrideableBool','Override','Nivalis.OverrideableBool/OverrideLock','Il2CppSystem.Object'),
    @('Nivalis.OverrideableBool/OverrideLock','Release','System.Void',''),
    @('Nivalis.OverrideableBool/OverrideLock','get_IsReleased','System.Boolean',''),
    @('Nivalis.UIPanel','SetVisibleImmediate','System.Void',''),
    @('Nivalis.UIPanel','SetInvisibleImmediate','System.Void',''),
    @('Nivalis.UIPanel','ForceSelect','System.Void','UnityEngine.GameObject'),
    @('Nivalis.UIManager','UpdateCursorState','System.Void','')
)
try {
    foreach ($check in $checks) {
        $type = $assembly.MainModule.GetType($check[0])
        if (!$type -or $type.BaseType.FullName -in @('Il2CppSystem.ValueType', 'System.ValueType')) {
            throw ('Sleep UI receiver is missing or is a value wrapper: ' + $check[0])
        }
        $methods = @($type.Methods | Where-Object {
            $_.Name -eq $check[1] -and !$_.IsStatic -and
            (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq $check[3]
        })
        if ($methods.Count -ne 1 -or $methods[0].ReturnType.FullName -ne $check[2]) {
            throw ('Sleep UI native ABI changed: ' + $check[0] + '.' + $check[1])
        }
        'PASS native ABI: ' + $methods[0].FullName
    }
} finally { $assembly.Dispose() }

# These are ownership boundaries, not screenshot/layout assertions. Real native
# acceptance still must observe the opaque screen, advancing animation, and lock
# release after cancellation, successful wake, and disconnect.
if ($source -match 'Harmony|LoadingScreenUI|ReleaseOverride\s*\(|Time\.timeScale\s*=|Cursor\.\w+\s*=') {
    throw 'Shared sleep overlay touches a global detour, native loading UI, cursor flag, or foreign override.'
}
if ($source -match 'TimeOfDayManager|CurfewManager|SleepManager|DayTransitionController') {
    throw 'Shared sleep presentation must not mutate or drive the native day simulation.'
}
if ($source -notmatch 'Time\.unscaledTime' -or $source -match 'Time\.deltaTime|Time\.time\b') {
    throw 'Shared sleep animation must remain independent of the gameplay time scale.'
}
if ($source -notmatch 'panel\.pauseTimeWhenOpen\s*=\s*false' -or $source -notmatch 'panel\.pauseTimeCompletely\s*=\s*false') {
    throw 'Shared sleep UI must not hold a native time pause.'
}
'PASS: independent unscaled presentation, no day simulation or global/native loading mutations; only owned override tokens are released.'
'PASS: 8 production choice-identity regressions, 10 installed native ABI checks, isolated compile, and ownership boundaries. Native visual acceptance remains separate.'
