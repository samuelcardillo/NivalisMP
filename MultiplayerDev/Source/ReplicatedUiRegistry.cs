using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using Nivalis.Locale.UI;
using UnityEngine;
using TransferUi = _Work_Lukasz.Scripts.UI.TransferMenuUI;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>
    /// Main-thread UI discovery for replicated state. The single bootstrap covers
    /// objects that awoke before joining; native Awake tracks subsequent instances.
    /// Refreshes never search the scene. Each snapshot rejects destroyed Unity
    /// objects, including components without their own OnDestroy callback.
    /// </summary>
    internal static class ReplicatedUiRegistry
    {
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.ui-registry");
        private static readonly Dictionary<Type, IUiSet> Sets = new();
        private static bool initialized;

        private interface IUiSet
        {
            void Track(MonoBehaviour component);
            void Forget(IntPtr pointer);
            void Seed();
        }

        private sealed class UiSet<T> : IUiSet where T : MonoBehaviour
        {
            private readonly Dictionary<IntPtr, T> instances = new();
            public void Track(MonoBehaviour component)
            {
                if (component == null) return;
                var ui = component.TryCast<T>();
                if (ui != null) instances[ui.Pointer] = ui;
            }
            public void Forget(IntPtr pointer) => instances.Remove(pointer);
            public void Seed()
            {
                foreach (var ui in Object.FindObjectsOfType<T>(true))
                    if (ui != null) instances[ui.Pointer] = ui;
            }
            internal T[] Snapshot()
            {
                var live = new List<T>(instances.Count);
                List<IntPtr> dead = null;
                foreach (var pair in instances)
                {
                    // A managed IL2CPP wrapper can survive its native Unity object.
                    // Test Unity's lifetime before any component/property access.
                    if (pair.Value == null)
                    {
                        (dead ??= new List<IntPtr>()).Add(pair.Key);
                        continue;
                    }
                    live.Add(pair.Value);
                }
                if (dead != null) foreach (var pointer in dead) instances.Remove(pointer);
                // Native refresh can create/destroy another window; do not expose
                // a live dictionary enumerator across those callbacks.
                return live.ToArray();
            }
        }

        internal static void Initialize()
        {
            if (initialized) return;
            try
            {
                Register<TransferUi>();
                Register<VenueOverviewTabGeneralSection>();
                Register<VenueStaffOverviewTab>();
                Register<HireStaffWindow>();
                Register<MenuModificationWindow>();
                Register<Nivalis.UI.ShopUINew>();
                Register<Nivalis.UI.PlayerInventoryUI>();
                Register<WorldStateDisplayUI>();
                Register<PlayerMoneyDisplayUI>();
                // Some derived UI types inherit destruction handlers. Others do not
                // define one; their destroyed references are pruned by Snapshot.
                PatchDestroy(typeof(UIPanel));
                PatchDestroy(typeof(UIWindow));
                PatchDestroy(typeof(UISubPanel));
                foreach (var set in Sets.Values) set.Seed();
                initialized = true;
            }
            catch
            {
                try { Patches.UnpatchSelf(); }
                finally { Sets.Clear(); initialized = false; }
                throw;
            }
        }

        private static void Register<T>() where T : MonoBehaviour
        {
            Sets.Add(typeof(T), new UiSet<T>());
            var awake = typeof(T).GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (awake == null) throw new MissingMethodException(typeof(T).FullName, "Awake");
            Patches.Patch(awake, postfix: new HarmonyMethod(typeof(ReplicatedUiRegistry), nameof(AwakePostfix)));
            PatchDestroy(typeof(T));
        }

        private static void PatchDestroy(Type type)
        {
            var method = type.GetMethod("OnDestroy", BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (method != null)
                Patches.Patch(method, prefix: new HarmonyMethod(typeof(ReplicatedUiRegistry), nameof(DestroyPrefix)));
        }

        private static void AwakePostfix(MonoBehaviour __instance)
        {
            foreach (var set in Sets.Values) set.Track(__instance);
        }

        private static void DestroyPrefix(MonoBehaviour __instance)
        {
            if (ReferenceEquals(__instance, null)) return;
            foreach (var set in Sets.Values) set.Forget(__instance.Pointer);
        }

        internal static T[] Snapshot<T>() where T : MonoBehaviour
        {
            Initialize();
            if (!Sets.TryGetValue(typeof(T), out var set))
                throw new ArgumentException("Unregistered replicated UI type: " + typeof(T).FullName);
            return ((UiSet<T>)set).Snapshot();
        }
    }
}
