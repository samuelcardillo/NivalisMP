$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath = Join-Path $gameRoot 'MultiplayerDev/Source/PlayerChatOverlay.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$protocolPath = Join-Path $gameRoot 'MultiplayerDev/Shared/ChatProtocol.cs'
$protocol = [IO.File]::ReadAllText($protocolPath)
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($directory in @('dotnet', 'BepInEx/core', 'BepInEx/interop')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $directory) -Filter '*.dll') {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $null, $sourcePath)
$protocolTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($protocol, $null, $protocolPath)
$authTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Shared/AccountAuthentication.cs')))
$normalize = @($authTree.GetRoot().DescendantNodes() | Where-Object { $_.GetType().Name -eq 'MethodDeclarationSyntax' -and $_.Identifier.ValueText -eq 'NormalizeUsername' })
$nameConstants = @($authTree.GetRoot().DescendantNodes() | Where-Object { $_.GetType().Name -eq 'FieldDeclarationSyntax' -and $_.ToFullString().Contains('UsernameMin') })
if ($normalize.Count -ne 1 -or $nameConstants.Count -ne 1) { throw 'Actual account username validation not found.' }
$authSlice = 'using System; namespace NivalisTogether.Dedicated { public static class AccountAuthentication {' + $nameConstants[0].ToFullString() + $normalize[0].ToFullString() + '}}'
$authSliceTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($authSlice)
$sleepPath = Join-Path $gameRoot 'MultiplayerDev/Source/SleepLoadingOverlay.cs'
$sleepTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sleepPath), $null, $sleepPath)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('PlayerChatOverlayAbiCheck').WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree, $protocolTree, $sleepTree, $authSliceTree))
$stream = [IO.MemoryStream]::new()
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object ToString
if (!$result.Success) { throw 'Chat overlay does not compile against installed game wrappers.' }
'PASS: chat overlay and real protocol compile against installed wrappers in memory; no plugin emitted.'

# Execute the actual production input lifecycle and presentation fences without
# initializing native Unity. No copied state-machine implementation is tested.
$state = @($tree.GetRoot().DescendantNodes() | Where-Object { $_.GetType().Name -eq 'ClassDeclarationSyntax' -and $_.Identifier.ValueText -eq 'ChatInputLifecycle' })
if ($state.Count -ne 1) { throw 'Chat input lifecycle missing or ambiguous.' }
$methods = @($tree.GetRoot().DescendantNodes() | Where-Object { $_.GetType().Name -eq 'MethodDeclarationSyntax' -and $_.Identifier.ValueText -in @('VisibleToLocal','Literal') })
if ($methods.Count -ne 2) { throw 'Chat presentation fence methods missing or ambiguous.' }
$methodText = ($methods | ForEach-Object { $_.ToFullString().Replace('private static string Literal', 'internal static string Literal') }) -join "`n"
$pure = 'using System; namespace NivalisTogether.Dedicated {' + $state[0].ToFullString() + 'internal static class ChatPresentationProbe {' + $methodText + '}}'
$pureTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($pure)
$testsPath = Join-Path $PSScriptRoot 'PlayerChatOverlayTests.cs'
$testsTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($testsPath), $null, $testsPath)
$pureCompilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('PlayerChatInputTests' + [Guid]::NewGuid().ToString('N')).WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($pureTree,$protocolTree,$testsTree,$authSliceTree))
$stream = [IO.MemoryStream]::new()
try {
    $result = $pureCompilation.Emit($stream)
    if (!$result.Success) { throw ('Chat lifecycle tests compile failed: ' + ($result.Diagnostics -join "`n")) }
    $assembly = [Reflection.Assembly]::Load($stream.ToArray())
} finally { $stream.Dispose() }
$count = $assembly.GetType('PlayerChatOverlayTests').GetMethod('Run').Invoke($null, @())
"PASS: $count production input lifecycle, private-message visibility, literal text, and command regressions."

if ($source -match 'Time\.timeScale\s*=|Cursor\.\w+\s*=|ReleaseOverride\s*\(') { throw 'Chat mutates global time, cursor state or foreign input locks.' }
if ($source -notmatch 'inputPanel\.pauseTimeWhenOpen\s*=\s*false' -or $source -notmatch 'inputPanel\.pauseTimeCompletely\s*=\s*false') { throw 'Chat must not pause the world.' }
if ($source -notmatch 'label\.richText\s*=\s*false' -or $source -notmatch 'field\.richText\s*=\s*false') { throw 'User text must be literal in history and input.' }
if ($source -notmatch 'ChatProtocol\.ValidateMessage\(message\)' -or $source -notmatch 'ChatProtocol\.ValidateRejection\(rejection\)') { throw 'Server payload validation missing.' }
if ($source -match 'UnityEngine\.Input\.|Input\.GetKey') { throw 'Chat must use the installed Input System.' }
'PASS: no global pause/cursor mutation, only owned override tokens, literal text, validated payloads, current Input System.'
'Native visual/input acceptance is still required; this suite does not claim to emulate Unity EventSystem ordering.'
