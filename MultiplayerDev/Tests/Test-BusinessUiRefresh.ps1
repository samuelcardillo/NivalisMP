$ErrorActionPreference = 'Stop'
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/BusinessReplication.cs'))).GetRoot()
$methods = @()
foreach ($name in @('RefreshUi','VisibleBusinessUi')) {
    $method = $tree.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq $name } | Select-Object -First 1
    if (!$method) { throw "Production method missing: $name" }
    $methods += $method.ToFullString()
}
$stubs = @'
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Nivalis;
using Object = UnityEngine.Object;
public static class ReplicatedUiRegistry {
 public static T[] Snapshot<T>() => Object.FindObjectsOfType<T>(true);
}
namespace UnityEngine {
 public class GameObject { public bool activeInHierarchy=true; }
 public class MonoBehaviour {
  public bool isActiveAndEnabled=true;
  public GameObject gameObject=new GameObject();
  public Nivalis.UIPanel[] Parents=Array.Empty<Nivalis.UIPanel>();
  public T[] GetComponentsInParent<T>(bool includeInactive) => Parents.Cast<T>().ToArray();
 }
 public static class Object {
  public static readonly Dictionary<Type,Array> Instances=new Dictionary<Type,Array>();
  public static T[] FindObjectsOfType<T>(bool includeInactive) => Instances.TryGetValue(typeof(T),out var value)?(T[])value:Array.Empty<T>();
  public static void Set<T>(params T[] values) { Instances[typeof(T)]=values; }
 }
}
namespace Nivalis {
 public class UIPanel:MonoBehaviour {
  public bool IsVisible=true;
  public UIPanel(){Parents=new[]{this};}
  public T TryCast<T>() where T:class => this as T;
 }
 public class UIWindow:UIPanel { public bool IsOpen=true; }
}
public sealed class Venue { public object RuntimeData=new object(); }
public sealed class TransferUi:UIWindow {
 public Venue _venue=new Venue();public object _currentContainer=new object(),inventoryList=new object(),containerList=new object();public int Calls;
 public void RefreshLists(){if(_currentContainer==null||inventoryList==null||containerList==null||_venue?.RuntimeData==null)throw new NullReferenceException("Unbound transfer window");Calls++;}
 public void RefreshWarnings(){Calls++;}public void RefreshMiddlePanel(){Calls++;}
}
public sealed class VenueOverviewTabGeneralSection:MonoBehaviour {
 public Venue _venue=new Venue();public int Calls;public VenueOverviewTabGeneralSection(){Parents=new[]{new UIPanel()};}
 public void Show(Venue venue){Calls++;}
}
public sealed class VenueStaffOverviewTab:UIPanel {
 public Venue _forVenue=new Venue();public object staffList=new object();public int Calls;
 public void RefreshStaffList(Venue venue){Calls++;}public void RefreshWorkingHours(){Calls++;}
}
public sealed class HireStaffWindow:UIPanel {
 public Venue _forVenue=new Venue();public object staffList=new object();public int Calls;
 public void RefreshStaffList(){if(_forVenue?.RuntimeData==null||staffList==null)throw new NullReferenceException("Unbound hiring window");Calls++;}
}
public sealed class MenuModificationWindow:UIPanel {
 public Venue _venue=new Venue();public object menuItemList=new object(),recipeItemList=new object();public int Calls;
 public void RefreshMenuList(){Calls++;}public void RefreshRecipeList(){Calls++;}public void RefreshSatisfaction(){Calls++;}
}
'@
$tests = @'
public static class BusinessUiRefreshProbe {
 public static int Run() {
  int checks=0;void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
  var ready=new TransferUi();var closed=new TransferUi{IsOpen=false};var hidden=new TransferUi{IsVisible=false};
  var unbound=new TransferUi{_venue=null,_currentContainer=null};var noContainer=new TransferUi{_currentContainer=null};
  var noLeft=new TransferUi{inventoryList=null};var noRight=new TransferUi{containerList=null};var noRuntime=new TransferUi{_venue=new Venue{RuntimeData=null}};
  var closedParent=new TransferUi();closedParent.Parents=new UIPanel[]{closedParent,new UIWindow{IsOpen=false}};
  var disabled=new TransferUi{isActiveAndEnabled=false};var inactive=new TransferUi();inactive.gameObject.activeInHierarchy=false;
  var orphan=new TransferUi{Parents=Array.Empty<UIPanel>()};
  Object.Set(ready,closed,hidden,unbound,noContainer,noLeft,noRight,noRuntime,closedParent,disabled,inactive,orphan,null);
  var general=new VenueOverviewTabGeneralSection();var hiddenGeneral=new VenueOverviewTabGeneralSection{Parents=new UIPanel[]{new UIWindow{IsOpen=false}}};
  var staff=new VenueStaffOverviewTab();var hiddenStaff=new VenueStaffOverviewTab{IsVisible=false};var noStaff=new VenueStaffOverviewTab{_forVenue=null};
  var hire=new HireStaffWindow();var hiddenHire=new HireStaffWindow{IsVisible=false};var noHire=new HireStaffWindow{_forVenue=null};
  var menu=new MenuModificationWindow();var hiddenMenu=new MenuModificationWindow{IsVisible=false};var noMenu=new MenuModificationWindow{_venue=null};
  Object.Set(general,hiddenGeneral);Object.Set(staff,hiddenStaff,noStaff);Object.Set(hire,hiddenHire,noHire);Object.Set(menu,hiddenMenu,noMenu);
  ActualBusinessRefresh.Run();
  Check(ready.Calls==3,"Legitimately open bound transfer UI refreshes all three sections");
  foreach(var ui in new[]{closed,hidden,unbound,noContainer,noLeft,noRight,noRuntime,closedParent,disabled,inactive,orphan})Check(ui.Calls==0,"Closed, hidden or unbound transfer menu must not call native refresh");
  Check(general.Calls==1&&staff.Calls==2&&hire.Calls==1&&menu.Calls==3,"Skipping closed transfer menus does not block ready business windows");
  Check(hiddenGeneral.Calls==0&&hiddenStaff.Calls==0&&hiddenHire.Calls==0&&hiddenMenu.Calls==0,"Closed business panels and child tabs stay untouched");
  Check(noStaff.Calls==0&&noHire.Calls==0&&noMenu.Calls==0,"Unbound business windows stay untouched");
  closed.IsOpen=true;unbound._venue=new Venue();unbound._currentContainer=new object();
  ActualBusinessRefresh.Run();
  Check(closed.Calls==3&&unbound.Calls==3,"Opening and binding the same native window enables normal refresh again");
  Check(!ActualBusinessRefresh.Refreshing,"Refresh suppression flag released after updates");
  return checks;
 }
}
'@
$source = $stubs + 'public static class ActualBusinessRefresh { private static bool refreshing; public static bool Refreshing=>refreshing; public static void Run()=>RefreshUi();' + ($methods -join "`n") + '}' + $tests
Add-Type -TypeDefinition $source
"Business UI refresh production methods PASS $([BusinessUiRefreshProbe]::Run()) assertions"

$npc = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $gameRoot 'MultiplayerDev/Source/NpcWorldReplication.cs'))).GetRoot()
$field = $npc.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax] -and $_.Identifier.ValueText -eq 'LocomotionStates' } | Select-Object -First 1
$names = @($field.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax] } | ForEach-Object { $_.Token.ValueText })
$expected = @('Male.Movement.Movement','Female.Movement.Movement','Base Layer.Tripod Bot blend tree','Base Layer.idle','Base Layer.Bot Hovering')
if ($names.Count -ne 5 -or ($names -join "`n") -cne ($expected -join "`n")) { throw 'NPC movement state catalog no longer matches audited controller paths' }
'NPC exact locomotion state paths PASS (male, female, tripod blend tree, police idle, therapy hover)'
