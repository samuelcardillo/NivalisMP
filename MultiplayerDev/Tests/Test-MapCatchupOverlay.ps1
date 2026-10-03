$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/MapCatchupOverlay.cs'
$source=[IO.File]::ReadAllText($sourcePath)
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source,$null,$sourcePath)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($directory in @('dotnet','BepInEx/core','BepInEx/interop')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $directory) -Filter '*.dll'){
  try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
  $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
 }
}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('MapCatchupOverlayAbiCheck').WithOptions($options).AddReferences($refs.ToArray()).AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@($tree))
$stream=[IO.MemoryStream]::new()
try{$result=$compilation.Emit($stream)}finally{$stream.Dispose()}
$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString
if(!$result.Success){throw 'Map catchup overlay failed installed-wrapper compilation'}
'PASS: actual overlay compiles in memory against installed native wrappers; no game execution or DLL output.'

# Run the actual visibility, cleanup and input-ownership methods with fake native
# objects. Only Create is replaced: real UI construction is ABI-compiled above.
# The production lock/state logic is extracted rather than copied into tests.
$class=@($tree.GetRoot().DescendantNodes()|Where-Object{$_-is[Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax]-and$_.Identifier.ValueText-eq'MapCatchupOverlay'})
if($class.Count-ne1){throw 'Missing overlay class'}
$members=($class[0].Members|Where-Object{!($_-is[Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]-and$_.Identifier.ValueText-eq'Create')}|ForEach-Object ToFullString)-join"`n"
$fixture=@'
using System;
using System.Collections.Generic;
using Nivalis;
using TMPro;
using UnityEngine;
using Object=UnityEngine.Object;
namespace UnityEngine {
 public class Object {
  private static long next;public IntPtr Pointer=new IntPtr(++next);public bool Destroyed;
  public static bool operator==(Object a,Object b){bool an=ReferenceEquals(a,null)||a.Destroyed,bn=ReferenceEquals(b,null)||b.Destroyed;return an?bn:!bn&&a.Pointer==b.Pointer;}
  public static bool operator!=(Object a,Object b)=>!(a==b);
  public override bool Equals(object o)=>ReferenceEquals(this,o);public override int GetHashCode()=>Pointer.GetHashCode();
  public static void Destroy(Object o){if(!ReferenceEquals(o,null))o.Destroyed=true;}
 }
 public class GameObject:Object{public bool activeSelf=true;public bool activeInHierarchy=>activeSelf&&!Destroyed;public void SetActive(bool v){activeSelf=v;}}
 public static class Debug{public static int Warnings;public static void LogWarning(string value){Warnings++;}}
}
namespace TMPro{public class TextMeshProUGUI:UnityEngine.Object{}}
namespace Nivalis {
 public sealed class OverrideableBool {
  private static long next;public IntPtr Pointer=new IntPtr(++next);public List<OverrideLock> Tokens=new List<OverrideLock>();
  public OverrideLock Override(object owner){var t=new OverrideLock{Owner=owner};Tokens.Add(t);return t;}
  public sealed class OverrideLock{public object Owner;public bool IsReleased;public int Releases;public bool ThrowOnRelease;public void Release(){Releases++;if(ThrowOnRelease)throw new InvalidOperationException("native release failed");IsReleased=true;}}
 }
 public class PlayerCharacterController:UnityEngine.Object {
  public OverrideableBool Movement=new OverrideableBool(),Camera=new OverrideableBool();public bool ThrowCamera;
  public OverrideableBool.OverrideLock DisableMovement(object owner)=>Movement.Override(owner);
  public OverrideableBool.OverrideLock LockCamera(object owner){if(ThrowCamera)throw new InvalidOperationException("native lock failed");return Camera.Override(owner);}
 }
 public class PlayerInteraction:UnityEngine.Object{public OverrideableBool Gate=new OverrideableBool();public OverrideableBool.OverrideLock DisableInteractions(object owner)=>Gate.Override(owner);}
 public class PlayerCharacter{public PlayerCharacterController _controller;public PlayerInteraction _interaction;}
 public class PlayerManager{public static PlayerManager Instance;public static bool InstanceNotNull()=>Instance!=null;public Player _localPlayer;public class Player{public PlayerCharacter _character;}}
 public class PlayerInputManager:UnityEngine.Object{public static PlayerInputManager Instance;public static bool InstanceNotNull()=>Instance!=null;public OverrideableBool PreventAllMenuShortcuts=new OverrideableBool();}
 public class UIPanel:UnityEngine.Object{public bool IsVisible;public void SetVisibleImmediate(){IsVisible=true;}public void SetInvisibleImmediate(){IsVisible=false;}}
}
namespace NivalisTogether.Dedicated {
 public static class TestWorld{public static int Creates;public static bool FailCreate;}
 public static class MapCatchupOverlay {
 __MEMBERS__
 private static void Create(){TestWorld.Creates++;root=new GameObject();root.SetActive(false);if(TestWorld.FailCreate)throw new InvalidOperationException("native UI creation failed");panel=new UIPanel();label=new TextMeshProUGUI();}
 }
 public static class MapCatchupOverlayTests {
  private static int checks;
  private static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
  private static T Field<T>(string name)=>(T)typeof(MapCatchupOverlay).GetField(name,System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null);
  private static OverrideableBool.OverrideLock Token(string name)=>Field<OverrideableBool.OverrideLock>(name);
  public static int Run(){
   var character=new PlayerCharacter{_controller=new PlayerCharacterController(),_interaction=new PlayerInteraction()};
   PlayerManager.Instance=new PlayerManager{_localPlayer=new PlayerManager.Player{_character=character}};PlayerInputManager.Instance=new PlayerInputManager();
   var sleepOwner=new GameObject();var sleepMove=character._controller.DisableMovement(sleepOwner);var sleepCamera=character._controller.LockCamera(sleepOwner);var sleepInteract=character._interaction.DisableInteractions(sleepOwner);var sleepMenu=PlayerInputManager.Instance.PreventAllMenuShortcuts.Override(sleepOwner);
   MapCatchupOverlay.Tick();Check(TestWorld.Creates==0,"hidden Tick inert");
   MapCatchupOverlay.SetVisible(true);Check(MapCatchupOverlay.IsVisible,"visible immediately");Check(MapCatchupOverlay.OwnedInputLockCount==4,"four owned locks");
   var oldRoot=Field<GameObject>("root");var move=Token("movementLock");var camera=Token("cameraLock");var interact=Token("interactionLock");var menu=Token("menuLock");
   Check(ReferenceEquals(move.Owner,oldRoot)&&ReferenceEquals(menu.Owner,oldRoot),"own root is token owner");
   MapCatchupOverlay.SetVisible(true);MapCatchupOverlay.Tick();Check(TestWorld.Creates==1&&ReferenceEquals(move,Token("movementLock")),"repeat show/tick preserves tokens");
   oldRoot.SetActive(false);Field<UIPanel>("panel").SetInvisibleImmediate();MapCatchupOverlay.Tick();Check(MapCatchupOverlay.IsVisible&&Field<UIPanel>("panel").IsVisible,"native hiding repaired while requested");
   move.Release();MapCatchupOverlay.Tick();Check(!ReferenceEquals(move,Token("movementLock"))&&!Token("movementLock").IsReleased,"released own token reacquired");Check(ReferenceEquals(camera,Token("cameraLock")),"other owned token retained");
   move=Token("movementLock");character._controller=new PlayerCharacterController();MapCatchupOverlay.Tick();Check(move.IsReleased&&camera.IsReleased,"old controller locks released");Check(MapCatchupOverlay.OwnedInputLockCount==4,"new controller locked");Check(ReferenceEquals(interact,Token("interactionLock")),"unchanged interaction not released");
   character._interaction=new PlayerInteraction();MapCatchupOverlay.Tick();Check(interact.IsReleased&&!Token("interactionLock").IsReleased,"replacement interaction locked");
   PlayerInputManager.Instance.PreventAllMenuShortcuts=new OverrideableBool();MapCatchupOverlay.Tick();Check(menu.IsReleased&&!Token("menuLock").IsReleased,"replacement gate on same manager locked");
   menu=Token("menuLock");PlayerInputManager.Instance=new PlayerInputManager();MapCatchupOverlay.Tick();Check(menu.IsReleased&&!Token("menuLock").IsReleased,"replacement input manager locked");
   move=Token("movementLock");Object.Destroy(oldRoot);MapCatchupOverlay.Tick();Check(move.IsReleased&&TestWorld.Creates==2,"destroyed canvas owner replaced");Check(MapCatchupOverlay.IsVisible&&MapCatchupOverlay.OwnedInputLockCount==4,"replacement canvas locks restored");
   move=Token("movementLock");camera=Token("cameraLock");interact=Token("interactionLock");menu=Token("menuLock");MapCatchupOverlay.Reset();Check(move.IsReleased&&camera.IsReleased&&interact.IsReleased&&menu.IsReleased,"Reset releases all owned tokens");Check(!MapCatchupOverlay.IsVisible&&MapCatchupOverlay.OwnedInputLockCount==0,"Reset hides presentation");
   Check(!sleepMove.IsReleased&&!sleepCamera.IsReleased&&!sleepInteract.IsReleased&&!sleepMenu.IsReleased,"foreign sleep tokens untouched");
   int created=TestWorld.Creates;MapCatchupOverlay.Tick();MapCatchupOverlay.SetVisible(false);Check(TestWorld.Creates==created,"discarded world never recreates without request");
   PlayerManager.Instance=null;PlayerInputManager.Instance=null;MapCatchupOverlay.SetVisible(true);Check(MapCatchupOverlay.IsVisible&&MapCatchupOverlay.OwnedInputLockCount==0,"visible safely while native player absent");
   PlayerManager.Instance=new PlayerManager{_localPlayer=new PlayerManager.Player{_character=character}};PlayerInputManager.Instance=new PlayerInputManager();MapCatchupOverlay.Tick();Check(MapCatchupOverlay.OwnedInputLockCount==4,"later native player gets locks");
   move=Token("movementLock");camera=Token("cameraLock");interact=Token("interactionLock");menu=Token("menuLock");move.ThrowOnRelease=true;MapCatchupOverlay.Reset();Check(camera.IsReleased&&interact.IsReleased&&menu.IsReleased,"one failed native release does not prevent other cleanup");Check(MapCatchupOverlay.OwnedInputLockCount==0&&Debug.Warnings==1,"failed release reference discarded with warning");
   TestWorld.FailCreate=true;try{MapCatchupOverlay.SetVisible(true);throw new Exception("expected creation failure");}catch(InvalidOperationException){}Check(!MapCatchupOverlay.IsVisible&&Field<GameObject>("root")==null,"partial creation cleaned");TestWorld.FailCreate=false;
   character._controller.ThrowCamera=true;try{MapCatchupOverlay.SetVisible(true);throw new Exception("expected acquisition failure");}catch(InvalidOperationException){}Check(!MapCatchupOverlay.IsVisible&&MapCatchupOverlay.OwnedInputLockCount==0,"partial lock acquisition cleaned");Check(character._controller.Movement.Tokens[character._controller.Movement.Tokens.Count-1].IsReleased,"partial movement token released");
   return checks;
  }
 }
}
'@
$fixture=$fixture.Replace('__MEMBERS__',$members)
Add-Type -TypeDefinition $fixture -WarningAction SilentlyContinue
$count=[NivalisTogether.Dedicated.MapCatchupOverlayTests]::Run()
"PASS: $count actual visibility/input ownership regressions with fake native objects."
if($source-match 'SleepLoadingOverlay\.|LoadingScreenUI|ReleaseOverride\s*\(|Time\.timeScale\s*=|Cursor\.\w+\s*=|TimeOfDayManager|CurfewManager'){throw 'Overlay must not mutate another barrier, global locks, cursor or world clock'}
if($source-notmatch 'canvas\.sortingOrder\s*=\s*32750'||$source-notmatch 'panel\.pauseTimeWhenOpen\s*=\s*false'||$source-notmatch 'panel\.pauseTimeCompletely\s*=\s*false'){throw 'Overlay sorting/time ownership changed'}
if($source-match 'new Button|AddComponent<Button>|FindObjectsOfType'){throw 'Map overlay added interactive controls or global array scans'}
'PASS: independent opaque presentation below sleep, no foreign barrier/global time mutations. Native visual acceptance remains required.'
