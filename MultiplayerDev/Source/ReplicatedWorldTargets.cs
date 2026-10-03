using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using Nivalis.Dialogue;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>Native lifecycle discovery; membership is rechecked at each use.</summary>
    internal static class ReplicatedWorldTargets
    {
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.world-targets");
        private static readonly Dictionary<Type, ISet> Sets = new();
        private static bool initialized;
        private interface ISet { void Track(MonoBehaviour value); void Forget(IntPtr pointer); void Seed(); }
        private sealed class Set<T> : ISet where T : MonoBehaviour
        {
            private readonly Dictionary<IntPtr,T> instances = new();
            public void Track(MonoBehaviour value)
            {
                if(value==null)return;
                var typed=value.TryCast<T>();
                if(typed!=null)instances[typed.Pointer]=typed;
            }
            public void Forget(IntPtr pointer)=>instances.Remove(pointer);
            public void Seed(){foreach(var value in Object.FindObjectsOfType<T>(true))if(value!=null)instances[value.Pointer]=value;}
            internal T[] Snapshot()
            {
                var result=new List<T>();var dead=new List<IntPtr>();
                foreach(var pair in instances)
                {
                    if(pair.Value==null){dead.Add(pair.Key);continue;}
                    var scene=pair.Value.gameObject.scene;
                    // Keep living pooled/disabled objects registered, but never
                    // expose an unloaded or invalid scene as an interaction target.
                    if(scene.IsValid()&&scene.isLoaded)result.Add(pair.Value);
                }
                foreach(var pointer in dead)instances.Remove(pointer);
                return result.ToArray();
            }
        }
        internal static void Initialize()
        {
            if(initialized)return;
            try
            {
                Register<DoorInteraction>("Awake");Register<HingeInteraction>("Awake");
                Register<ItemCollectible>("Start");Register<StartDialogueInteraction>("Awake");
                foreach(var set in Sets.Values)set.Seed();
                initialized=true;
            }
            catch
            {
                try{Patches.UnpatchSelf();}
                finally{Sets.Clear();initialized=false;}
                throw;
            }
        }
        private static void Register<T>(string lifecycle) where T:MonoBehaviour
        {
            Sets.Add(typeof(T),new Set<T>());
            var method=Declared(typeof(T),lifecycle)??throw new MissingMethodException(typeof(T).FullName,lifecycle);
            Patches.Patch(method,postfix:new HarmonyMethod(typeof(ReplicatedWorldTargets),nameof(TrackPostfix)));
            var enable=Declared(typeof(T),"OnEnable");
            if(enable!=null)Patches.Patch(enable,postfix:new HarmonyMethod(typeof(ReplicatedWorldTargets),nameof(TrackPostfix)));
            var destroy=Declared(typeof(T),"OnDestroy");
            if(destroy!=null)Patches.Patch(destroy,prefix:new HarmonyMethod(typeof(ReplicatedWorldTargets),nameof(DestroyPrefix)));
        }
        private static MethodInfo Declared(Type type,string name)=>type.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
        private static void TrackPostfix(MonoBehaviour __instance){foreach(var set in Sets.Values)set.Track(__instance);}
        private static void DestroyPrefix(MonoBehaviour __instance)
        {
            if(ReferenceEquals(__instance,null))return;
            foreach(var set in Sets.Values)set.Forget(__instance.Pointer);
        }
        internal static T[] Snapshot<T>() where T:MonoBehaviour
        {
            Initialize();
            return ((Set<T>)Sets[typeof(T)]).Snapshot();
        }
    }
}
