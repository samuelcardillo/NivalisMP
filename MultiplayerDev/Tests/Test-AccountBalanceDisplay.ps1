$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/SharedEconomyReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath))
$method=@($tree.GetRoot().DescendantNodes() | Where-Object {
    $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -eq 'RefreshPersonalDisplay'
})
if($method.Count -ne 1){throw 'Could not find the exact production display refresh'}
$production=@'
using System;
using Object=UnityEngine.Object;
using Nivalis;
public static class ReplicatedUiRegistry {
 public static T[] Snapshot<T>() => Object.FindObjectsOfType<T>(true);
}
public static class ActualBalanceRefresh {
 public static string LastRefreshError="";
 public static int ShopRefreshes;
 private static void RefreshVisibleShops(){ShopRefreshes++;}
 private static bool IsShopPresentationException(Exception e)=>false;
 public static void Apply(int before,int after)=>RefreshPersonalDisplay(before,after);
'@ + $method[0].ToFullString() + '}'
$boundary=@'
using System;
namespace UnityEngine {
 public sealed class GameObject { public bool activeInHierarchy=true; }
 public static class Object {
  public static Nivalis.PlayerMoneyDisplayUI[] Money=Array.Empty<Nivalis.PlayerMoneyDisplayUI>();
  public static Nivalis.WorldStateDisplayUI[] Navigation=Array.Empty<Nivalis.WorldStateDisplayUI>();
  public static Nivalis.UI.PlayerInventoryUI[] Inventory=Array.Empty<Nivalis.UI.PlayerInventoryUI>();
  public static T[] FindObjectsOfType<T>(bool includeInactive) => typeof(T)==typeof(Nivalis.PlayerMoneyDisplayUI)?(T[])(object)Money:
   typeof(T)==typeof(Nivalis.WorldStateDisplayUI)?(T[])(object)Navigation:(T[])(object)Inventory;
 }
}
namespace Nivalis {
 public sealed class WorldStateDisplayUI {
  public bool isActiveAndEnabled=true;
  public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
  public Nivalis.UI.ValueDisplayUi moneyDisplay=new Nivalis.UI.ValueDisplayUi();
  public int Calls;
  public void OnPlayerMoneyChanged(int oldMoney,int newMoney) {
   if(oldMoney!=newMoney)throw new Exception("Account presentation must use the exact current balance");
   Calls++;moneyDisplay.SetMoney(newMoney,false,false);
  }
 }
 public sealed class PlayerMoneyDisplayUI {
  public bool isActiveAndEnabled=true;
  public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
  public object moneyTMP=new object(),deltaHolder,moneyDeltaText,_player;
  public int Calls,Displayed=-1;
  public void UpdateMoney(int oldMoney,int newMoney) {
   if(moneyTMP==null)throw new Exception("Missing native balance label");
   Calls++;Displayed=newMoney;
   if(oldMoney==newMoney)return;
   if(deltaHolder==null||moneyDeltaText==null)throw new Exception("Native delta animation requires optional objects");
   throw new Exception("Account synchronization must not animate a money transaction");
  }
 }
}
namespace Nivalis.UI {
 public sealed class ValueDisplayUi {
  public bool isActiveAndEnabled=true;
  public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
  public object valueText=new object(),colorChangingGraphics=new object();
  public int Displayed=-1;
  public void SetMoney(int money,bool icon,bool sign) {
   if(valueText==null||colorChangingGraphics==null)throw new Exception("Incomplete currency view");
   if(icon||sign)throw new Exception("Native top-navigation formatting must remain unchanged");
   Displayed=money;
  }
 }
 public sealed class PlayerInventoryUI {
  public bool isActiveAndEnabled=true,IsVisible=true;
  public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
  public object inventoryItems=new object(),_playerItems=new object();
  public int Calls;
  public void RefreshInventoryDisplay(){Calls++;}
 }
}
public static class AccountBalanceDisplayProbe {
 static int checks;
 static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
 public static int Run(){
  var balance=new Nivalis.PlayerMoneyDisplayUI();
  var noLabel=new Nivalis.PlayerMoneyDisplayUI{moneyTMP=null};
  var disabled=new Nivalis.PlayerMoneyDisplayUI{isActiveAndEnabled=false};
  var inactive=new Nivalis.PlayerMoneyDisplayUI();inactive.gameObject.activeInHierarchy=false;
  var inventory=new Nivalis.UI.PlayerInventoryUI();
  var hiddenInventory=new Nivalis.UI.PlayerInventoryUI{IsVisible=false};
  UnityEngine.Object.Money=new[]{balance,noLabel,disabled,inactive,null};
  UnityEngine.Object.Inventory=new[]{inventory,hiddenInventory,null};
  ActualBalanceRefresh.Apply(0,100000);
  Check(balance.Calls==1&&balance.Displayed==100000,"Initial account balance refreshes without optional delta objects or cached player");
  Check(noLabel.Calls==0&&disabled.Calls==0&&inactive.Calls==0,"Incomplete/inactive native views remain untouched");
  Check(inventory.Calls==1&&hiddenInventory.Calls==0,"Existing visible-inventory refresh remains intact");
  Check(ActualBalanceRefresh.ShopRefreshes==1,"Shop refresh preserved");
  ActualBalanceRefresh.Apply(100000,75000);
  Check(balance.Calls==2&&balance.Displayed==75000,"Authoritative correction updates balance without transaction animation");
  ActualBalanceRefresh.Apply(75000,75000);
  Check(balance.Calls==3&&balance.Displayed==75000,"Unchanged private state repairs a stale balance label");
  ActualBalanceRefresh.Apply(75000,0);
  Check(balance.Displayed==0,"A genuinely empty authoritative wallet is displayed exactly");
  var top=new Nivalis.WorldStateDisplayUI();
  var noDisplay=new Nivalis.WorldStateDisplayUI{moneyDisplay=null};
  var noTopLabel=new Nivalis.WorldStateDisplayUI();noTopLabel.moneyDisplay.valueText=null;
  var noColors=new Nivalis.WorldStateDisplayUI();noColors.moneyDisplay.colorChangingGraphics=null;
  var disabledTop=new Nivalis.WorldStateDisplayUI{isActiveAndEnabled=false};
  var inactiveTop=new Nivalis.WorldStateDisplayUI();inactiveTop.gameObject.activeInHierarchy=false;
  var disabledDisplay=new Nivalis.WorldStateDisplayUI();disabledDisplay.moneyDisplay.isActiveAndEnabled=false;
  var inactiveDisplay=new Nivalis.WorldStateDisplayUI();inactiveDisplay.moneyDisplay.gameObject.activeInHierarchy=false;
  UnityEngine.Object.Navigation=new[]{top,noDisplay,noTopLabel,noColors,disabledTop,inactiveTop,disabledDisplay,inactiveDisplay,null};
  UnityEngine.Object.Money=Array.Empty<Nivalis.PlayerMoneyDisplayUI>();
  ActualBalanceRefresh.Apply(0,100000);
  Check(top.Calls==1&&top.moneyDisplay.Displayed==100000,"Actual top navigation refreshes when secondary money display is absent");
  Check(noDisplay.Calls==0&&noTopLabel.Calls==0&&noColors.Calls==0,"Missing native top-navigation dependencies are skipped");
  Check(disabledTop.Calls==0&&inactiveTop.Calls==0&&disabledDisplay.Calls==0&&inactiveDisplay.Calls==0,"Inactive top-navigation views remain untouched");
  ActualBalanceRefresh.Apply(100000,75000);
  Check(top.Calls==2&&top.moneyDisplay.Displayed==75000,"Top navigation follows later authoritative corrections");
  top.moneyDisplay.Displayed=0;ActualBalanceRefresh.Apply(75000,75000);
  Check(top.Calls==3&&top.moneyDisplay.Displayed==75000,"Unchanged private snapshot repairs stale top navigation");
  UnityEngine.Object.Money=new[]{balance};ActualBalanceRefresh.Apply(75000,0);
  Check(top.moneyDisplay.Displayed==0&&balance.Displayed==0,"Both independent currency displays support an empty wallet");
  Check(top.Calls==4&&balance.Calls==5,"Both display paths are refreshed exactly once per applicable update");
  return checks;
 }
}
'@
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){
 try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
 $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
}
$c=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AccountBalanceDisplay'+[Guid]::NewGuid().ToString('N')).WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($text in @($production,$boundary)){$c=$c.AddSyntaxTrees([Microsoft.CodeAnalysis.CSharp.SyntaxFactory]::ParseSyntaxTree($text))}
$stream=[IO.MemoryStream]::new();try{$result=$c.Emit($stream);if(!$result.Success){$result.Diagnostics|ForEach-Object ToString;throw 'Display test compilation failed'};$assembly=[Reflection.Assembly]::Load($stream.ToArray())}finally{$stream.Dispose()}
$checks=$assembly.GetType('AccountBalanceDisplayProbe').GetMethod('Run').Invoke($null,@())
"Production account balance refresh PASS $checks checks (extracted method, native boundaries doubled)"

# Compile the integrated source without loading native code or writing a plugin.
foreach($dir in @('BepInEx/core','BepInEx/interop')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.dll'){
  try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue}
  $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
 }
}
$full=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AccountBalanceIntegration').WithOptions([Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)).AddReferences($refs.ToArray())
foreach($dir in @('MultiplayerDev/Source','MultiplayerDev/Shared')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot $dir) -Filter '*.cs'){
  $full=$full.AddSyntaxTrees([Microsoft.CodeAnalysis.CSharp.SyntaxFactory]::ParseSyntaxTree([IO.File]::ReadAllText($file.FullName)))
 }
}
$stream=[IO.MemoryStream]::new();try{$result=$full.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Integrated source compilation failed'}
'Full Source + Shared compilation PASS (memory only, no plugin output)'
