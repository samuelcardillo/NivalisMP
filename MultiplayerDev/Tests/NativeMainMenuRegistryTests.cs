using System;using System.Collections.Generic;using System.Reflection;using System.Linq;
using Object=NivalisTogether.MenuObject;
namespace NivalisTogether {
internal struct Scene {internal bool Valid,Loaded;internal bool IsValid()=>Valid;internal bool isLoaded=>Loaded;}
internal enum LoadSceneMode {Single,Additive}
internal sealed class GameObject {internal Scene scene;internal bool activeSelf=true;}
internal sealed class MainMenuUI {
 internal IntPtr Pointer;internal GameObject gameObject=new(){scene=new(){Valid=true,Loaded=true}};internal bool Destroyed,title;
 public void Start(){}public void Show(){}public void OnDestroy(){}
 public static bool operator==(MainMenuUI a,MainMenuUI b){bool an=ReferenceEquals(a,null)||a.Destroyed,bn=ReferenceEquals(b,null)||b.Destroyed;return an&&bn||!an&&!bn&&ReferenceEquals(a,b);}
 public static bool operator!=(MainMenuUI a,MainMenuUI b)=>!(a==b);
 public override bool Equals(object b)=>ReferenceEquals(this,b);public override int GetHashCode()=>Pointer.GetHashCode();
}
internal static class SceneManager {
 internal static event Action<Scene,LoadSceneMode> sceneLoaded;internal static event Action<Scene> sceneUnloaded;
 internal static void Loaded()=>sceneLoaded?.Invoke(new(){Valid=true,Loaded=true},LoadSceneMode.Additive);
 internal static void Unloaded()=>sceneUnloaded?.Invoke(new(){Valid=true,Loaded=false});
}
internal static class MenuObject {
 internal static readonly List<MainMenuUI> World=new();internal static int Scans;internal static bool ThrowScan;
 internal static T[] FindObjectsOfType<T>(bool inactive)where T:class{
  if(!inactive)throw new Exception("Inactive menu discovery required");Scans++;if(ThrowScan)throw new InvalidOperationException("Injected discovery failure");return World.OfType<T>().ToArray();
 }
}
internal sealed class HarmonyMethod {internal readonly MethodInfo Method;internal HarmonyMethod(MethodInfo method){Method=method;}}
internal sealed class Harmony {
 internal static readonly Dictionary<string,(MethodInfo Target,HarmonyMethod Prefix,HarmonyMethod Postfix)> Hooks=new();internal Harmony(string id){}
 internal void Patch(MethodInfo target,HarmonyMethod prefix=null,HarmonyMethod postfix=null){Hooks.Add(target.Name,(target,prefix,postfix));}
 internal void UnpatchSelf()=>Hooks.Clear();
 internal static void Fire(string name,MainMenuUI menu){var h=Hooks[name];(h.Prefix??h.Postfix).Method.Invoke(null,new object[]{menu});}
}
/*PRODUCTION_REGISTRY*/
internal static class NativeMainMenuRegistryTests {
 private static int checks,next;
 private static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
 private static MainMenuUI Menu(bool active=true,bool title=false){var menu=new MainMenuUI{Pointer=new(++next),title=title};menu.gameObject.activeSelf=active;return menu;}
 private static int Retained()=>((System.Collections.IDictionary)typeof(NativeMainMenuRegistry).GetField("Menus",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)).Count;
 public static int Main(){try{
  NativeMainMenuRegistry.Initialize();NativeMainMenuRegistry.Initialize();
  Check(Harmony.Hooks.Count==3&&Harmony.Hooks.ContainsKey("Start")&&Harmony.Hooks.ContainsKey("Show")&&Harmony.Hooks.ContainsKey("OnDestroy"),"Exact lifecycle hooks install once across menu/session instances");
  Check(Harmony.Hooks.Values.All(h=>h.Target.DeclaringType==typeof(MainMenuUI)&&h.Target.GetParameters().Length==0&&h.Target.ReturnType==typeof(void)),"Actual patch resolver selects declared no-argument MainMenuUI methods");
  var title=Menu(title:true);var pause=Menu(active:false);MenuObject.World.AddRange(new[]{title,pause});
  var first=NativeMainMenuRegistry.Snapshot();Check(first.Length==2&&first.Contains(pause),"One inclusive seed discovers never-activated gameplay pause menu and title menu");
  Check(MenuObject.Scans==1,"Startup performs one world scan");
  for(int i=0;i<600;i++)NativeMainMenuRegistry.Snapshot();Check(MenuObject.Scans==1,"Ten minutes of periodic cached snapshots cause no extra world scans");
  var late=Menu();Harmony.Fire("Start",late);var known=NativeMainMenuRegistry.Snapshot();Check(known.Contains(late)&&MenuObject.Scans==1,"Native Start discovers a later menu without scanning");
  long revision=NativeMainMenuRegistry.Revision;Harmony.Fire("Show",late);Check(NativeMainMenuRegistry.Revision==revision,"Repeated Show does not duplicate or invalidate existing membership");
  var lazy=Menu();Harmony.Fire("Show",lazy);Check(NativeMainMenuRegistry.Snapshot().Contains(lazy)&&MenuObject.Scans==1,"Show also discovers late menus whose Start was missed");
  var nextPause=Menu(active:false);MenuObject.World.Add(nextPause);SceneManager.Loaded();SceneManager.Loaded();
  Check(MenuObject.Scans==1,"Additive scene callbacks only mark discovery pending");
  known=NativeMainMenuRegistry.Snapshot();Check(known.Contains(nextPause)&&MenuObject.Scans==2,"Several scene callbacks coalesce into one seed that includes inactive menus");
  Check(known.Contains(title)&&known.Contains(late),"A district load preserves existing persistent title/session menus");
  for(int i=0;i<100;i++)NativeMainMenuRegistry.Snapshot();Check(MenuObject.Scans==2,"Scene seed is consumed once and never becomes periodic");
  Harmony.Fire("OnDestroy",late);late.Destroyed=true;Check(!NativeMainMenuRegistry.Snapshot().Contains(late),"Native destroy removes its retained wrapper");
  lazy.Destroyed=true;Check(!NativeMainMenuRegistry.Snapshot().Contains(lazy),"Unity-null entries are pruned even if a destroy hook was missed");
  pause.gameObject.scene=new(){Valid=true,Loaded=false};SceneManager.Unloaded();known=NativeMainMenuRegistry.Snapshot();
  Check(!known.Contains(pause)&&known.Contains(title)&&MenuObject.Scans==2,"Scene unload prunes unloaded menus without a new scan or clearing persistent title");
  int baseline=Retained();for(int i=0;i<100;i++){var menu=Menu();Harmony.Fire("Start",menu);Harmony.Fire("OnDestroy",menu);}
  Check(Retained()==baseline,"Repeated menu creation/destruction cannot grow retained wrappers");
  SceneManager.Loaded();MenuObject.ThrowScan=true;bool failed=false;try{NativeMainMenuRegistry.Snapshot();}catch(InvalidOperationException){failed=true;}
  Check(failed,"Discovery failure remains explicit");MenuObject.ThrowScan=false;int scans=MenuObject.Scans;NativeMainMenuRegistry.Snapshot();
  Check(MenuObject.Scans==scans+1,"A failed one-shot seed is retried instead of losing inactive UI forever");
  foreach(var menu in MenuObject.World)menu.Destroyed=true;NativeMainMenuRegistry.Snapshot();Check(Retained()==0,"All destroyed native menu wrappers are released");
  scans=MenuObject.Scans;for(int i=0;i<100;i++)NativeMainMenuRegistry.Snapshot();Check(MenuObject.Scans==scans,"An empty registry in gameplay does not restart world scans");
  var reconnect=Menu(title:true);Harmony.Fire("Start",reconnect);Check(NativeMainMenuRegistry.Snapshot().Single()==reconnect,"Returning title is immediately available for account reconnect and solo restoration");
  Console.WriteLine("PASS NativeMainMenuRegistryTests: "+checks+" assertions; actual registry lifecycle, inactive menus, scene coalescing, pruning, reconnect and no periodic scans.");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
}
