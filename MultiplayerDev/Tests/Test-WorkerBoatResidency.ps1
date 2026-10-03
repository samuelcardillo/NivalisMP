[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$devRoot=Split-Path -Parent $PSScriptRoot
$gameRoot=Split-Path -Parent $devRoot
$source=Join-Path $devRoot 'Source/WorkerSceneResidency.cs'
$syntax=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$methods=@($syntax.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('Ensure','BeginClientRestore','DetachWorkerBoat') })
if($methods.Count -ne 3) { throw 'Expected the actual worker and client context entry points plus detachment helper.' }
$prefix=@'
using System; using System.Collections.Generic;
namespace NivalisTogether.Dedicated { public static class WorkerSceneResidency {
 private static object operation;
 private static PlayerMapLocation completedWorkerContext;
 public static bool ClientRestoreComplete;
 public static bool IsWorker, MatchPosition, LastBeginClient;
 public static int BeginCalls, IdleCalls;
 public static readonly List<string> Logs = new List<string>();
 public static Action<string> Diagnostic = Logs.Add;
 public static PlayerMapLocation Completed { get => completedWorkerContext; set => completedWorkerContext = value; }
 public static bool Pending { set => operation = value ? new object() : null; }
 public static void ResetTest() {
  operation = null; completedWorkerContext = null; ClientRestoreComplete = false;
  IsWorker = MatchPosition = true; LastBeginClient = false; BeginCalls = IdleCalls = 0;
  BoatReplication.Attached = BoatReplication.ThrowOnDetach = false; BoatReplication.DetachCalls = 0; Logs.Clear(); }
 private static void RequireAuthority(bool worker) { if(IsWorker != worker) throw new InvalidOperationException("Wrong authority."); }
 private static void Validate(PlayerMapLocation context, double timestamp) { if(context == null || !double.IsFinite(timestamp)) throw new InvalidOperationException("Invalid context."); }
 private static bool Same(PlayerMapLocation a, PlayerMapLocation b) => true;
 private static bool ContextMatches(PlayerMapLocation value) => true;
 private static bool PositionMatches(PlayerMapLocation value) => MatchPosition;
 private static void RequireIdleNative() { IdleCalls++; if(BoatReplication.Attached) throw new InvalidOperationException("Still aboard."); }
 private static void Begin(PlayerMapLocation context, string epoch, double timestamp, bool client) { BeginCalls++; LastBeginClient = client; }
'@
$production=$prefix+[Environment]::NewLine+(($methods | ForEach-Object ToFullString) -join [Environment]::NewLine)+[Environment]::NewLine+'} }'
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll') {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('WorkerBoatResidencyTests'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($code in @($production,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WorkerBoatResidencyTests.cs')))) {
    $comp=$comp.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code)))
}
$stream=[IO.MemoryStream]::new()
try {
    $result=$comp.Emit($stream)
    $result.Diagnostics | Where-Object Severity -ne Hidden | ForEach-Object { $_.ToString() }
    if(!$result.Success) { throw 'Worker boat context regression compilation failed.' }
    $testAssembly=[Reflection.Assembly]::Load($stream.ToArray())
    $assertions=$testAssembly.GetType('WorkerBoatResidencyTests').GetMethod('Run').Invoke($null,$null)
} finally { $stream.Dispose() }
[pscustomobject]@{
    Utc=[DateTime]::UtcNow.ToString('O');Passed=$true;Assertions=$assertions;
    SourceSHA256=(Get-FileHash -LiteralPath $source).Hash;
    Scope='Actual production context entry points with a native detachment facade. Verifies fast-path cleanup ordering, authority, failure containment and untouched client restore. Native boat travel still requires live acceptance.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $devRoot 'inspection/worker-boat-residency-tests.json')
Write-Output "PASS: $assertions production worker boat residency assertions."
