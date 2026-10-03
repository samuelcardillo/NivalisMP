using System;
using System.Collections.Generic;
using System.Reflection;
using NivalisTogether.Dedicated;
using Object = UnityEngine.Object;

namespace HarmonyLib
{
    public sealed class HarmonyMethod
    {
        public MethodInfo Method;
        public HarmonyMethod(Type type, string name) { Method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static); }
    }
    public sealed class Harmony
    {
        public static readonly Dictionary<MethodInfo, HarmonyMethod> Before = new(), After = new();
        public static int PatchCalls, FailPatchCall, UnpatchCalls;
        public Harmony(string name) { }
        public void Patch(MethodInfo original, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        { if (++PatchCalls == FailPatchCall) throw new InvalidOperationException("Native patch failed"); if (prefix != null) Before.Add(original, prefix); if (postfix != null) After.Add(original, postfix); }
        public void UnpatchSelf() { UnpatchCalls++; Before.Clear(); After.Clear(); }
        public static void Awoke(UnityEngine.MonoBehaviour value) => After[value.GetType().GetMethod("Awake")].Method.Invoke(null, new object[] { value });
        public static void Destroy(UnityEngine.MonoBehaviour value)
        {
            var inherited = value.GetType().GetMethod("OnDestroy");
            var declared = inherited.DeclaringType.GetMethod("OnDestroy", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Before[declared].Method.Invoke(null, new object[] { value });
        }
    }
}
namespace UnityEngine
{
    public class Object
    {
        public bool Dead;
        public static int Searches, FailSearch;
        public static readonly Dictionary<Type, Array> Instances = new();
        public static T[] FindObjectsOfType<T>(bool inactive) { if (++Searches == FailSearch) throw new InvalidOperationException("Native bootstrap failed"); return Instances.TryGetValue(typeof(T), out var a) ? (T[])a : Array.Empty<T>(); }
        public static bool operator ==(Object a, Object b) => (ReferenceEquals(a, null) || a.Dead) == (ReferenceEquals(b, null) || b.Dead) &&
            ((ReferenceEquals(a, null) || a.Dead) || ReferenceEquals(a, b));
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => base.GetHashCode();
    }
    public class MonoBehaviour : Object
    {
        private static int pointer;
        public IntPtr Pointer = new IntPtr(++pointer);
        public T TryCast<T>() where T : class => this as T;
    }
}
namespace Nivalis
{
    public class UIPanel : UnityEngine.MonoBehaviour { public void OnDestroy() { } }
    public class UIWindow : UIPanel { public new void OnDestroy() { } }
    public class UISubPanel : UnityEngine.MonoBehaviour { public void OnDestroy() { } }
    public class WorldStateDisplayUI : UIPanel { public void Awake() { } public new void OnDestroy() { } }
    public class PlayerMoneyDisplayUI : UnityEngine.MonoBehaviour { public void Awake() { } public void OnDestroy() { } }
}
namespace Nivalis.UI
{
    public class ShopUINew : Nivalis.UIWindow { public void Awake() { } public new void OnDestroy() { } }
    public class PlayerInventoryUI : Nivalis.UISubPanel { public void Awake() { } }
}
namespace Nivalis.Locale.UI
{
    public class VenueOverviewTabGeneralSection : UnityEngine.MonoBehaviour { public void Awake() { } }
    public class VenueStaffOverviewTab : UnityEngine.MonoBehaviour { public void Awake() { } }
    public class HireStaffWindow : Nivalis.UIPanel { public void Awake() { } }
    public class MenuModificationWindow : UnityEngine.MonoBehaviour { public void Awake() { } }
}
namespace _Work_Lukasz.Scripts.UI { public class TransferMenuUI : Nivalis.UIWindow { public void Awake() { } } }

public static class ReplicatedUiRegistryTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        var first = new Nivalis.UI.ShopUINew();
        var oldHud = new Nivalis.WorldStateDisplayUI();
        Object.Instances[typeof(Nivalis.UI.ShopUINew)] = new[] { first };
        Object.Instances[typeof(Nivalis.WorldStateDisplayUI)] = new[] { oldHud };
        HarmonyLib.Harmony.FailPatchCall = 3;
        bool failed = false; try { ReplicatedUiRegistry.Initialize(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && HarmonyLib.Harmony.After.Count == 0 && HarmonyLib.Harmony.UnpatchCalls == 1, "Partial native patch failure cleans up owned hooks");
        HarmonyLib.Harmony.FailPatchCall = 0; Object.FailSearch = 8;
        failed = false; try { ReplicatedUiRegistry.Initialize(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && HarmonyLib.Harmony.After.Count == 0 && HarmonyLib.Harmony.UnpatchCalls == 2, "Partially seeded native scan failure also cleans up all hooks");
        var freshFirst = new Nivalis.UI.ShopUINew();
        Object.Instances[typeof(Nivalis.UI.ShopUINew)] = new[] { freshFirst }; first = freshFirst;
        Object.FailSearch = Object.Searches = 0;
        ReplicatedUiRegistry.Initialize();
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>().Length == 1, "Retry discards partial bootstrap instances before discovering fresh objects");
        Check(Object.Searches == 9, "One bootstrap scan for the nine supported UI types");
        Check(HarmonyLib.Harmony.After.Count == 9, "Every supported type has an exact native Awake hook");
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>()[0] == first, "Already-awake UI is discovered at startup");
        ReplicatedUiRegistry.Initialize();
        for (int i = 0; i < 100; i++) ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>();
        Check(Object.Searches == 9, "Repeated updates and reconnect initialization cannot scan the scene");
        var second = new Nivalis.UI.ShopUINew();
        HarmonyLib.Harmony.Awoke(second); HarmonyLib.Harmony.Awoke(second);
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>().Length == 2, "Late-created windows register once");
        var stable = ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>();
        HarmonyLib.Harmony.Destroy(first);
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>().Length == 1, "Native OnDestroy removes a still-live managed wrapper");
        Check(stable.Length == 2, "UI callbacks cannot invalidate an in-progress refresh enumeration");
        second.Dead = true;
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>().Length == 0, "Destroyed Unity objects are pruned even if their callback was omitted");
        var reused = new Nivalis.UI.ShopUINew { Pointer = second.Pointer };
        HarmonyLib.Harmony.Awoke(reused);
        Check(ReferenceEquals(ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>()[0], reused), "Native pointer reuse replaces the dead wrapper");
        var general = new Nivalis.Locale.UI.VenueOverviewTabGeneralSection();
        HarmonyLib.Harmony.Awoke(general); general.Dead = true;
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.Locale.UI.VenueOverviewTabGeneralSection>().Length == 0, "Components with no native OnDestroy are pruned safely");
        var inventory = new Nivalis.UI.PlayerInventoryUI();
        HarmonyLib.Harmony.Awoke(inventory); HarmonyLib.Harmony.Destroy(inventory);
        Check(ReplicatedUiRegistry.Snapshot<Nivalis.UI.PlayerInventoryUI>().Length == 0, "Inherited UISubPanel destruction removes inventory UI");
        ReplicatedUiRegistry.Initialize();
        Check(ReferenceEquals(ReplicatedUiRegistry.Snapshot<Nivalis.WorldStateDisplayUI>()[0], oldHud), "Persistent HUD remains tracked across map loads and reconnects");
        Check(Object.Searches == 9, "All recreation, destruction and reconnect paths remain free of scene scans");
        return checks;
    }
}
