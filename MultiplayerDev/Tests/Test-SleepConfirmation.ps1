[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
$devRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $devRoot
$sleepPath = Join-Path $devRoot 'Source/SleepReplication.cs'
$policyPath = Join-Path $devRoot 'Shared/SleepRecoveryProtocol.cs'
$fixturePath = Join-Path $PSScriptRoot 'SleepConfirmationTests.cs'

# Compile actual production client methods in this PowerShell process. Only
# native UI, bed identity, transport and isolated-world dependencies are stubs.
# No Unity/server process, production binary, installed plugin or save is used.
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sleepPath))
$type = @($tree.GetRoot().DescendantNodes() | Where-Object {
    $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'SleepReplication'
})
if ($type.Count -ne 1) { throw 'Expected one production SleepReplication class.' }
$methodNames = @('RouteClientInteraction', 'ShowReplica', 'ShowPopup', 'OwnsPopup', 'HidePopup', 'Send', 'CancelLocalReadiness', 'Apply', 'ValidateWire', 'Cut')
$propertyNames = @('IsLocallyReady', 'IsBusy')
$members = @($type[0].Members | Where-Object {
    ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $methodNames) -or
    ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -in $propertyNames)
})
if ($members.Count -ne ($methodNames.Count + $propertyNames.Count)) { throw 'The production sleep methods changed; review the test extraction.' }
$fixture = [IO.File]::ReadAllText($fixturePath)
$marker = '/* EXTRACTED_SLEEP_METHODS */'
if (($fixture.Split($marker)).Length -ne 2) { throw 'Expected one extraction marker.' }
$fixture = $fixture.Replace($marker, (($members | ForEach-Object ToFullString) -join "`n"))
$policySource = [IO.File]::ReadAllText($policyPath)
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
function Invoke-ManagedRegression([string]$fixtureSource) {
    $trees = @(
        [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($fixtureSource, $null, $fixturePath)
        [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($policySource, $null, $policyPath)
    )
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SleepConfirmationTests_' + [Guid]::NewGuid().ToString('N')).WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]$trees)
    $stream = [IO.MemoryStream]::new()
    try {
        $result = $compilation.Emit($stream)
        $result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object ToString
        if (-not $result.Success) { throw 'Sleep confirmation regression harness did not compile.' }
        $assembly = [Reflection.Assembly]::Load($stream.ToArray())
    } finally { $stream.Dispose() }
    $assembly.GetType('NivalisTogether.Dedicated.SleepReplication').GetMethod('RunRegressionTests').Invoke($null, @())
}

# Restore the old unconditional waiting-phase dismissal only in memory. The
# negative control must fail the actual second-player popup preservation case.
$waitingBranch = 'if(replica.Phase=="waiting"){'
if (($fixture.Split($waitingBranch)).Length -ne 2) { throw 'Waiting branch changed; review the negative control.' }
$negativeControl = ''
try { [void](Invoke-ManagedRegression $fixture.Replace($waitingBranch, $waitingBranch + 'HidePopup();')) }
catch {
    $exception = $_.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    $negativeControl = $exception.Message
}
if ($negativeControl -notlike '*second player confirmation survives repeated waiting frames*') {
    throw "Old waiting dismissal did not fail at the expected regression: $negativeControl"
}
$callbackDismissal = 'HidePopup();action();'
if (($fixture.Split($callbackDismissal)).Length -ne 2) { throw 'Confirmation callback changed; review its negative control.' }
$callbackNegativeControl = ''
try { [void](Invoke-ManagedRegression $fixture.Replace($callbackDismissal, 'popupOwned=false;action();')) }
catch {
    $exception = $_.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    $callbackNegativeControl = $exception.Message
}
if ($callbackNegativeControl -notlike '*confirm callback closes owned popup before authoritative readiness*') {
    throw "Old callback ownership release did not fail at the expected regression: $callbackNegativeControl"
}
$count = Invoke-ManagedRegression $fixture
[pscustomobject]@{
    passed = $true
    assertions = $count
    oldDismissalNegativeControl = $negativeControl
    oldCallbackNegativeControl = $callbackNegativeControl
    scope = 'Actual production RouteClientInteraction, ShowReplica, popup ownership/callback methods, Send, CancelLocalReadiness, Apply, IsBusy/IsLocallyReady and SleepRecoveryProtocol. Native UI, bed identity, transport and world dependencies are managed stubs; no native runtime test.'
    sleepSourceSha256 = (Get-FileHash -LiteralPath $sleepPath -Algorithm SHA256).Hash
    recoveryPolicySha256 = (Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash
    fixtureSha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
} | ConvertTo-Json
