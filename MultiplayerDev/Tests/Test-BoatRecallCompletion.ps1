$ErrorActionPreference='Stop'
$dev=Split-Path -Parent $PSScriptRoot
$source=Join-Path $dev 'Source/BoatReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$nodes=@($tree.GetRoot().DescendantNodes()|Where-Object {
 ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -in @('NativeRecallInProgress','UpdateRecall','DockPostfix')) -or
 ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsRecalling')
})
if($nodes.Count-ne4){throw 'Recall lifecycle method extraction changed.'}
$body=($nodes|ForEach-Object ToFullString)-join [Environment]::NewLine
$fixture=@'
using System;
public static class Time { public static float realtimeSinceStartup; }
public class Loaded { public BoatDock Dock; public T TryCast<T>() where T:class => Dock as T; }
public class Reference { public string ReferencedId; public DockGhost Value; }
public class DockGhost { public Loaded LoadedView; public Reference DockedGhost; }
public class BoatGhost { public Reference Dock; }
public class BoatController { public BoatGhost MyGhost; }
public class BoatDock { public bool IsRecalling; public DockGhost MyGhost; }
public static class RecallTests {
static bool configured=true,worker; static string recallDock="dock",OwnBoatId="own";
static float recallDeadline=45; static BoatGhost ownGhost; static BoatDock dock;
static int notices,checks,nativeDepth; static Action<string> notice=_=>notices++;
static BoatDock ResolveDock(string id)=>id=="dock"?dock:null;
static bool IsOwn(BoatController b)=>b?.MyGhost==ownGhost;
static void Check(bool result,string message){checks++;if(!result)throw new Exception(message);}
// ACTUAL
public static int Run() {
 dock=new BoatDock{IsRecalling=true,MyGhost=new DockGhost{DockedGhost=new Reference{ReferencedId="own"}}};
 ownGhost=new BoatGhost{Dock=new Reference{ReferencedId="dock",Value=dock.MyGhost}};
 dock.MyGhost.LoadedView=new Loaded{Dock=dock}; var boat=new BoatController{MyGhost=ownGhost};
 Check(IsRecalling,"recall pending gate is active");
 nativeDepth=1;dock.IsRecalling=false;DockPostfix(dock,boat);
 Check(recallDock=="dock","synchronous native startup callback cannot complete recall before its coroutine handle is assigned");
 nativeDepth=0;dock.IsRecalling=true;
 // Native initialization Dock and final RecallSequence.Dock both run before
 // the native coroutine field is cleared; neither is completion evidence.
 DockPostfix(dock,boat);
 Check(recallDock=="dock" && IsRecalling,"Dock callback while coroutine is active cannot release pose publication");
 Time.realtimeSinceStartup=9;UpdateRecall();
 Check(IsRecalling,"native recall animation remains fenced for its full duration");
 dock.IsRecalling=false;UpdateRecall();
 Check(recallDock=="" && !IsRecalling,"following Tick releases completed native recall without another Dock callback");
 dock.IsRecalling=true;
 Check(IsRecalling,"native recall itself suppresses poses even if the managed marker was cleared");
 worker=true;Check(!IsRecalling,"client native fallback does not run on worker");worker=false;
 configured=false;Check(!IsRecalling,"disabled client does not query native recall state");configured=true;
 recallDock="dock";dock.IsRecalling=false;dock.MyGhost.DockedGhost.ReferencedId="other";UpdateRecall();
 Check(IsRecalling,"another owner's dock occupancy cannot finish our recall");
 dock.MyGhost.DockedGhost.ReferencedId="own";ownGhost.Dock.ReferencedId="other-dock";UpdateRecall();
 Check(IsRecalling,"boat attached to another berth cannot finish requested recall");
 ownGhost.Dock.ReferencedId="dock";var saved=dock;dock=null;UpdateRecall();
 Check(IsRecalling,"missing loaded native dock cannot finish recall");dock=saved;
 dock.IsRecalling=true;Time.realtimeSinceStartup=46;UpdateRecall();
 Check(recallDock=="" && notices==1 && IsRecalling,"timeout notifies once but native active sequence still blocks pose publication");
 UpdateRecall();Check(notices==1,"timeout notice is not repeated each Tick");
 dock.IsRecalling=false;Check(!IsRecalling,"finished native sequence releases timeout fallback");
 return checks;
}
}
'@
$fixture=$fixture.Replace('// ACTUAL',$body)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem (Join-Path (Split-Path -Parent $dev) 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('RecallTest'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($fixture)))
$stream=[IO.MemoryStream]::new();try{
 $result=$comp.Emit($stream);if(!$result.Success){throw (($result.Diagnostics|ForEach-Object ToString)-join "`n")}
 $asm=[Reflection.Assembly]::Load($stream.ToArray());$checks=$asm.GetType('RecallTests').GetMethod('Run').Invoke($null,$null)
}finally{$stream.Dispose()}
$capture=@($tree.GetRoot().DescendantNodes()|Where-Object {$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq'CaptureOwnPose'})
if($capture.Count-ne1-or$capture[0].ToString()-notmatch 'ownState == null \|\| IsRecalling \|\|'){throw 'All callers of CaptureOwnPose must share the native recall gate.'}
[pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('O');Passed=$true;Assertions=$checks;CaptureGuardVerified=$true;SourceSHA256=(Get-FileHash $source).Hash;Scope='Actual native recall lifecycle methods against callback-order and native-object facades; no game execution.'}|ConvertTo-Json|Set-Content (Join-Path $dev 'inspection/boat-recall-completion-tests.json')
"PASS: $checks recall lifecycle assertions plus CaptureOwnPose gate."
