using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
namespace NivalisTogether
{
    [BepInPlugin("local.nivalis.together", "Nivalis Together - Dedicated Multiplayer", "0.5.2")]
    public sealed class Plugin : BasePlugin
    {
        internal static Plugin Instance;
        internal ConfigEntry<string> PlayerName;
        internal static readonly string[] Arguments = Environment.GetCommandLineArgs();
        internal static bool Worker => Array.IndexOf(Arguments, "-nivalis-worker") >= 0;
        internal static string Argument(string key, string fallback = "")
        { int index = Array.IndexOf(Arguments, key); return index >= 0 && index + 1 < Arguments.Length ? Arguments[index + 1] : fallback; }
        public override void Load()
        {
            Instance = this;
            PlayerName = Config.Bind("Connection", "PlayerName", "Explorer", "Name shown to other players.");
            try
            {
                if (Worker) DedicatedWorldRuntime.ConfigureIsolation(Argument("-nivalis-world-root"), true);
                AddComponent<TogetherDriver>();
                Log.LogInfo("Dedicated multiplayer 0.5.2 preview loaded. Role: " + (Worker ? "simulation worker" : "player"));
            }
            // During the loader Unity may ignore Application.Quit; never continue a worker after isolation failed.
            catch (Exception ex) { Log.LogFatal("Multiplayer initialization failed: " + ex); if (Worker) Environment.Exit(2); }
        }
    }
    public sealed class TogetherDriver : MonoBehaviour
    {
        private DedicatedSession session;
        public TogetherDriver(IntPtr ptr) : base(ptr) { }
        public void Start()
        {
            try { session = new DedicatedSession(); }
            catch (Exception ex) { Plugin.Instance.Log.LogError(ex); if (Plugin.Worker) Environment.Exit(2); }
        }
        public void Update() { session?.Tick(); }
        public void OnDestroy() { session?.Dispose(); session = null; }
        public void OnApplicationQuit() { session?.Dispose(); session = null; }
    }
}
