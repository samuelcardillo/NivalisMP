$ErrorActionPreference='Stop'
$dev=Split-Path -Parent $PSScriptRoot
$source=Join-Path $dev 'Source/BoatReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source))
$method=@($tree.GetRoot().DescendantNodes()|Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'IsNativeTravelComplete' })
$policy=@($tree.GetRoot().DescendantNodes()|Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'BoatRestoredAttachmentPolicy' })
if($method.Count-ne1-or$policy.Count-ne1){throw 'Boat arrival method extraction changed.'}
$fixture=@'
using System;
public class PlayerBoatState { public string OwnerAccountId; }
public class PortalKey { public string SceneName; public int SceneIndex; }
public class BoatGhost { public string Id; public bool IsValid=true, Boarded=true, Driven=true; public int SceneIndex=3; public IntPtr Pointer=(IntPtr)100; }
public class Character { public IntPtr Pointer=(IntPtr)30; public Controller Controller; public BoatController DrivenBoat; public double Position=12.345; }
public class Controller { public BoatController boardedBoat; }
public class Cockpit { public bool Occupied=true; public Character Player; }
public class BoatController {
 public BoatGhost MyGhost; public IntPtr Pointer; public bool Occupied=true, BlockEnter, ThrowEnter; public Cockpit Cockpit;
 public int Exits,Enters; public Action OnEnter;
 // Native OnDeckExit clears the persistent Boarded flag, but only clears
 // controller.boardedBoat when that exact controller matches this view.
 public void OnDeckExit(Controller c){Exits++;if(!MyGhost.IsValid||!MyGhost.Boarded)return;MyGhost.Boarded=false;if(c.boardedBoat?.Pointer==Pointer)c.boardedBoat=null;}
 // Native OnDeckEnter returns BEFORE assignment when Boarded is already true.
 public void OnDeckEnter(Controller c){Enters++;if(ThrowEnter)throw new Exception("native callback failed");if(BlockEnter||!MyGhost.IsValid||MyGhost.Boarded)return;MyGhost.Boarded=true;c.boardedBoat=this;OnEnter?.Invoke();}
}
public static class WorkerSceneResidency { public static bool ClientRestoreComplete=true; }
public static class DedicatedWorldRuntime { public static string Scene="Mountain"; }
public class Sequence { public bool IsInProgress; }
public static class BoatPortal { public static Sequence sequence=new Sequence(); }
public class LocalPlayer { public Character Character; }
public class PlayerManager { public static PlayerManager Instance=new PlayerManager(); public LocalPlayer LocalPlayer=new LocalPlayer(); }
public static class Debug { public static int Lines; public static void Log(string x){Lines++;} }
// POLICY
public static class ArrivalTests {
 static bool configured=true,worker; static string accountId="account",OwnBoatId="own";
 static PlayerBoatState ownState; static BoatGhost ownGhost; static BoatController OwnBoatController;
 static int nativeDepth,checks; static PortalKey key;
 static BoatController Boat=>OwnBoatController;
 static Character Player=>PlayerManager.Instance.LocalPlayer.Character;
 static void Check(bool result,string message){checks++;if(!result)throw new Exception(message);}
 static void Reset(){
  configured=true;worker=false;nativeDepth=0;Debug.Lines=0;WorkerSceneResidency.ClientRestoreComplete=true;
  DedicatedWorldRuntime.Scene="Mountain";BoatPortal.sequence=new Sequence();
  ownState=new PlayerBoatState{OwnerAccountId="account"};ownGhost=new BoatGhost{Id="own"};
  var player=new Character{Controller=new Controller()};PlayerManager.Instance.LocalPlayer.Character=player;
  OwnBoatController=new BoatController{Pointer=(IntPtr)200,MyGhost=ownGhost,Cockpit=new Cockpit{Player=player}};
  player.DrivenBoat=Boat;player.Controller.boardedBoat=Boat;
  key=new PortalKey{SceneName="Mountain",SceneIndex=3};
 }
 static void Rejected(Action mutation,string reason){Reset();mutation();Check(!IsNativeTravelComplete("own",key),reason);Check(Boat.Enters==0&&Boat.Exits==0,"rejected gate cannot invoke native deck callbacks: "+reason);}
 static void MustThrow(Action action,string reason){bool threw=false;try{action();}catch{threw=true;}Check(threw,reason);Check(nativeDepth==0,"nativeDepth is restored after failure");}
 // ACTUAL
 public static int Run(){
  Reset();Check(IsNativeTravelComplete("own",key),"complete current cockpit accepts without deck NavMesh");
  Check(Boat.Enters==0&&Boat.Exits==0&&Debug.Lines==0,"complete pointer state is idempotent");
  Reset();var old=new BoatController{Pointer=(IntPtr)199,MyGhost=ownGhost};Player.Controller.boardedBoat=old;
  Boat.OnDeckEnter(Player.Controller);Check(Player.Controller.boardedBoat==old,"native callback alone reproduces persistent Boarded short circuit");Boat.Enters=0;
  Check(IsNativeTravelComplete("own",key),"same owned ghost replaces stale portal source-view reference");
  Check(Player.Controller.boardedBoat==Boat&&ownGhost.Boarded&&Boat.Exits==1&&Boat.Enters==1,"normal exit/enter callbacks repair exact pointer");
  Check(Player.Position==12.345&&Player.DrivenBoat==Boat&&Boat.Cockpit.Player==Player&&ownGhost.Driven,"repair preserves cockpit, position and driving");
  Check(Debug.Lines==1&&nativeDepth==0,"single repair records old/current pointer evidence");
  Check(IsNativeTravelComplete("own",key)&&Boat.Exits==1&&Boat.Enters==1,"second completion check does not replay callbacks");
  Reset();Player.Controller.boardedBoat=null;Check(IsNativeTravelComplete("own",key)&&Boat.Exits==1,"missing source-view pointer with retained Boarded is repaired");
  Reset();ownGhost.Boarded=false;Player.Controller.boardedBoat=null;Check(IsNativeTravelComplete("own",key)&&Boat.Exits==0&&Boat.Enters==1,"unboarded native state needs only normal enter callback");
  Rejected(()=>configured=false,"disabled session");Rejected(()=>worker=true,"worker cannot attach a client");
  Rejected(()=>ownState=null,"missing private state");Rejected(()=>ownState.OwnerAccountId="foreign","foreign authenticated account");
  Rejected(()=>WorkerSceneResidency.ClientRestoreComplete=false,"bootstrap still restoring");
  Rejected(()=>BoatPortal.sequence.IsInProgress=true,"native portal still running");
  Rejected(()=>DedicatedWorldRuntime.Scene="Docks","native map mismatch");Rejected(()=>ownGhost.SceneIndex=1,"ghost logical map mismatch");
  Rejected(()=>ownGhost.IsValid=false,"invalid native ghost");
  Rejected(()=>Boat.MyGhost=new BoatGhost{Id="own",Pointer=(IntPtr)101},"matching ID on another ghost pointer");
  Rejected(()=>Boat.Occupied=false,"native boat not occupied");Rejected(()=>Boat.Cockpit.Occupied=false,"cockpit not occupied");
  Rejected(()=>Boat.Cockpit.Player=new Character{Pointer=(IntPtr)31},"foreign cockpit player");
  Rejected(()=>ownGhost.Driven=false,"native driven flag missing");
  Rejected(()=>Player.DrivenBoat=new BoatController{Pointer=(IntPtr)199,MyGhost=ownGhost},"stale driven controller must wait for native cockpit entry");
  Rejected(()=>Player.Controller.boardedBoat=new BoatController{Pointer=(IntPtr)500,MyGhost=new BoatGhost{Id="foreign"}},"foreign boarding identity is not overwritten");
  Reset();Check(!IsNativeTravelComplete("other",key)&&!IsNativeTravelComplete("own",null),"wrong requested boat or absent destination rejects");
  Reset();Player.Controller.boardedBoat=null;Boat.BlockEnter=true;MustThrow(()=>IsNativeTravelComplete("own",key),"no ACK if native callback does not establish attachment");
  Reset();Player.Controller.boardedBoat=null;Boat.ThrowEnter=true;MustThrow(()=>IsNativeTravelComplete("own",key),"native callback exception remains a failure");
  Reset();Player.Controller.boardedBoat=null;Boat.OnEnter=()=>OwnBoatController=new BoatController{Pointer=(IntPtr)201,MyGhost=ownGhost};
  MustThrow(()=>IsNativeTravelComplete("own",key),"view replacement during native callback cannot acknowledge stale view");
  return checks;
 }
}
'@
$fixture=$fixture.Replace('// POLICY',$policy[0].ToFullString()).Replace('// ACTUAL',$method[0].ToFullString())
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem (Join-Path (Split-Path -Parent $dev) 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$comp=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BoatArrivalTest'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($fixture)))
$stream=[IO.MemoryStream]::new();try{
 $result=$comp.Emit($stream);if(!$result.Success){throw (($result.Diagnostics|ForEach-Object ToString)-join "`n")}
 $asm=[Reflection.Assembly]::Load($stream.ToArray());$checks=$asm.GetType('ArrivalTests').GetMethod('Run').Invoke($null,$null)
}finally{$stream.Dispose()}
$travel=[IO.File]::ReadAllText((Join-Path $dev 'Source/TravelReplication.cs'))
if($travel-notmatch 'BoatReplication.IsNativeTravelComplete\(current.BoatId, destination\)'){throw 'Native arrival ACK path no longer invokes exact boat completion guard.'}
[pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('O');Passed=$true;Assertions=$checks;SourceSHA256=(Get-FileHash $source).Hash;Scope='Actual arrival method and attachment policy with native callback semantics confirmed from installed GameAssembly; no native game execution or acceptance.'}|ConvertTo-Json|Set-Content (Join-Path $dev 'inspection/boat-travel-attachment-tests.json')
"PASS: $checks native travel attachment checks; actual ACK gate retained."
