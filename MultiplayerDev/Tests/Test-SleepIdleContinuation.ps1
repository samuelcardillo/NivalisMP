[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
$devRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $devRoot
$sleepPath = Join-Path $devRoot 'Source/SleepReplication.cs'
$policyPath = Join-Path $devRoot 'Shared/SleepReportContinuation.cs'
$fixturePath = Join-Path $PSScriptRoot 'SleepIdleContinuationTests.cs'

# Compile actual production methods in this PowerShell process. The fixture only
# replaces native dependencies and observes calls; it does not load Unity, start
# a game/server process, write production binaries, or change server data.
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sleepPath))
$type = @($tree.GetRoot().DescendantNodes() | Where-Object {
    $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'SleepReplication'
})
if ($type.Count -ne 1) { throw 'Expected one production SleepReplication class.' }
$methodNames = @('Tick', 'CompleteSleep', 'StageSignature', 'StageText', 'Fail', 'Cut')
$members = @($type[0].Members | Where-Object {
    ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in $methodNames) -or
    ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsBusy')
})
if ($members.Count -ne ($methodNames.Count + 1)) { throw 'The production sleep methods changed; review the test extraction.' }
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
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SleepIdleContinuationTests_' + [Guid]::NewGuid().ToString('N')).WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]$trees)
    $stream = [IO.MemoryStream]::new()
    try {
        $result = $compilation.Emit($stream)
        $result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object ToString
        if (-not $result.Success) { throw 'Sleep idle continuation regression harness did not compile.' }
        $assembly = [Reflection.Assembly]::Load($stream.ToArray())
    } finally { $stream.Dispose() }
    $assembly.GetType('NivalisTogether.Dedicated.SleepReplication').GetMethod('RunRegressionTests').Invoke($null, @())
}
# Revert only the callback gate in memory to prove the old bug is detected.
# The production source and the extracted method on disk stay untouched.
$newGate = 'if(automaticReport&&!choicePending&&now-stageSince>=1)'
$oldGate = 'if(authorizedSleep&&!choicePending&&now-stageSince>=1&&(stage.Phase=="summary"||stage.Phase=="ranking"))'
if (-not $fixture.Contains($newGate)) { throw 'Automatic continuation gate changed; review the regression sensitivity check.' }
$negativeControl = ''
try { [void](Invoke-ManagedRegression $fixture.Replace($newGate, $oldGate)) }
catch {
    $exception = $_.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    $negativeControl = $exception.Message
}
if ($negativeControl -notlike '*empty-server summary invokes native next once*') {
    throw "Old callback gate did not fail at the expected regression: $negativeControl"
}
$count = Invoke-ManagedRegression $fixture
[pscustomobject]@{
    passed = $true
    assertions = $count
    oldGuardNegativeControl = $negativeControl
    scope = 'Actual production Tick, CompleteSleep, IsBusy and stage helpers with managed native/membership stubs; no native runtime test.'
    sleepSourceSha256 = (Get-FileHash -LiteralPath $sleepPath -Algorithm SHA256).Hash
    continuationPolicySha256 = (Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash
} | ConvertTo-Json
