using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using NivalisTogether.Dedicated;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace NivalisTogether
{
    // All entry points run on Unity's main thread. Networking must enqueue work there.
    public static class DedicatedWorldRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PersistentPathCall();

        private static readonly PersistentPathCall pathCallback = RedirectPersistentPath;
        private static INativeDetour pathDetour;
        private static PersistentPathCall originalPathCall;
        private static IntPtr pathString, pathHandle;
        private static Harmony harmony;
        private static string root, originalRoot, originalEs2Path, originalPcPath;
        private static int mainThread;
        private static bool dedicated, starting;
        private static bool previousBackground;
        private static int previousFrameRate;
        private static string importSlot;
        private static bool loadedSnapshot;
        private static bool titleLoadCompleted;
        private static bool gameplaySceneLoadCompleted, snapshotLoadCompleted;
        private static int startingSceneId = -1;
        private static bool minimalRuntime;
        private static bool skipCoroutineHooks;
        private static bool traceStartup;
        private static int startupTraceChecks;
        private static Type snapshotLoadRoutineType;
        private static PropertyInfo snapshotLoadSaveName;

        public static bool IsMinimalRuntime { get { return minimalRuntime; } }

        public static bool IsIsolated { get { return pathDetour != null && root != null; } }
        public static bool IsDedicatedServer { get { return dedicated && IsIsolated; } }
        public static string StorageDirectory { get { return root ?? ""; } }
        public static string ClientIsolationReleaseWaitReason { get; private set; } = "";

        public static bool IsBootReady
        {
            get
            {
                if (!IsIsolated) return false;
                var game = GetGame();
                return game != null && IsSceneLoadIdle(game) &&
                    game.global != null && game.global.AreManagersLoaded &&
                    SerializationManager.InstanceNotNull() &&
                    (game.IsGame || (game.title != null && game.title.AreManagersLoaded && game.title.IsUiLoaded &&
                        !string.IsNullOrEmpty(game.titleScene) && SceneManager.GetSceneByName(game.titleScene).isLoaded &&
                        (!IsDedicatedServer || skipCoroutineHooks || titleLoadCompleted)));
            }
        }

        public static bool IsWorldReady
        {
            get
            {
                bool trace = traceStartup && dedicated && starting && startupTraceChecks++ < 60;
                if (trace) Trace("IsWorldReady: enter");
                if (!IsIsolated) return false;
                var game = GetGame(trace);
                if (game == null || !Probe(trace, "game.IsGame", () => game.IsGame) || !IsSceneLoadIdle(game, trace) ||
                    Probe(trace, "game.IsLoadingExternal", () => game.IsLoadingExternal) ||
                    !Probe(trace, "game.gameplay != null", () => game.gameplay != null) ||
                    !Probe(trace, "game.IsGameplayInitialized", () => game.IsGameplayInitialized) ||
                    !Probe(trace, "SerializationManager.InstanceNotNull", () => SerializationManager.InstanceNotNull()) ||
                    Probe(trace, "SerializationManager.Instance.IsInitializing", () => SerializationManager.Instance.IsInitializing))
                {
                    if (trace) Trace("IsWorldReady: false (manager/scene load)");
                    return false;
                }
                if (!Probe(trace, "PlayerManager.InstanceNotNull", () => PlayerManager.InstanceNotNull())) return false;
                if (trace) Trace("IsWorldReady: before PlayerManager.Instance.LocalPlayer");
                var local = PlayerManager.Instance.LocalPlayer;
                if (trace) Trace("IsWorldReady: after PlayerManager.Instance.LocalPlayer; null=" + (local == null));
                bool ready = local != null && Probe(trace, "local.Character != null", () => local.Character != null) &&
                    Probe(trace, "CurrentGameplaySceneName nonempty", () => !string.IsNullOrEmpty(game.CurrentGameplaySceneName)) &&
                    // Native LoadRoutine can finish before LoadAreaRoutine's resource
                    // cleanup/fade/locks. Both completions are required after an import.
                    // Coroutine-free diagnostic mode deliberately cannot publish readiness.
                    !starting && gameplaySceneLoadCompleted && (!loadedSnapshot || snapshotLoadCompleted);
                if (trace) Trace("IsWorldReady: return " + ready);
                return ready;
            }
        }

        public static string Scene
        {
            get { var game = GetGame(); return game == null ? "" : game.CurrentGameplaySceneName ?? ""; }
        }

        private static bool IsSceneLoadIdle(GameSceneManager game, bool trace = false)
        {
            // IsLoading alone becomes false before LoadAreaRoutine finishes; starting a new
            // world there races the outstanding title scene activation/unload coroutine.
            if (Probe(trace, "game.IsLoading", () => game.IsLoading) ||
                Probe(trace, "game.levelLoading", () => game.levelLoading) ||
                Probe(trace, "game.isTitleChange", () => game.isTitleChange) ||
                Probe(trace, "GameSceneManager.IsUnloadingGameplay", () => GameSceneManager.IsUnloadingGameplay)) return false;
            if (trace) Trace("IsSceneLoadIdle: before LoadingOperation");
            var loadingOperation = game.LoadingOperation;
            if (trace) Trace("IsSceneLoadIdle: after LoadingOperation; null=" + (loadingOperation == null));
            if (loadingOperation != null && !Probe(trace, "LoadingOperation.isDone", () => loadingOperation.isDone)) return false;
            if (trace) Trace("IsSceneLoadIdle: before _activeOperation");
            var activeOperation = game._activeOperation;
            if (trace) Trace("IsSceneLoadIdle: after _activeOperation; null=" + (activeOperation == null));
            bool idle = activeOperation == null || Probe(trace, "_activeOperation.isDone", () => activeOperation.isDone);
            if (trace) Trace("IsSceneLoadIdle: return " + idle);
            return idle;
        }

        private static bool Probe(bool trace, string name, Func<bool> read)
        {
            if (trace) Trace("Before " + name);
            bool value = read();
            if (trace) Trace("After " + name + "=" + value);
            return value;
        }

        public static void ConfigureIsolation(string directory, bool dedicatedServer = false)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new ArgumentException("The multiplayer storage directory must be absolute.", nameof(directory));
            string target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsIsolated)
            {
                RequireMainThread();
                if (SamePath(target, root) && dedicated == dedicatedServer) { VerifyIsolation(); return; }
                throw new InvalidOperationException("Leave the current multiplayer world before changing its storage directory.");
            }
            if (target.Length <= Path.GetPathRoot(target).Length)
                throw new ArgumentException("A drive root cannot be used for multiplayer storage.", nameof(directory));
            var existingGame = GetGame();
            if (existingGame != null && (dedicatedServer
                ? existingGame.IsGame || existingGame.IsLoading || GameSceneManager.IsUnloadingGameplay
                : ClientTitleWaitReason(existingGame).Length != 0))
                throw new InvalidOperationException("Multiplayer storage can only be selected from the title screen or before game startup.");

            mainThread = Thread.CurrentThread.ManagedThreadId;
            originalRoot = Path.GetFullPath(Application.persistentDataPath);
            if (IsWithin(target, originalRoot) || IsWithin(originalRoot, target))
                throw new InvalidOperationException("Multiplayer storage must be separate from the single-player save directory.");
            Directory.CreateDirectory(target);
            // Reject junctions/symlinks: textual path isolation must also mean filesystem isolation.
            for (var item = new DirectoryInfo(target); item != null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Multiplayer storage cannot use a linked directory.");

            originalEs2Path = ES2FilenameData.persistentDataPath;
            originalPcPath = ES2GlobalSettings.defaultPCDataPath;
            previousBackground = Application.runInBackground;
            previousFrameRate = Application.targetFrameRate;
            root = target;
            dedicated = dedicatedServer;
            minimalRuntime = dedicatedServer && (string.Equals(Environment.GetEnvironmentVariable("NIVALIS_MINIMAL_RUNTIME"), "1", StringComparison.Ordinal) ||
                Array.Exists(Environment.GetCommandLineArgs(), arg => string.Equals(arg, "-nivalis-minimal-runtime", StringComparison.OrdinalIgnoreCase)));
            skipCoroutineHooks = dedicatedServer && (string.Equals(Environment.GetEnvironmentVariable("NIVALIS_SKIP_COROUTINE_HOOKS"), "1", StringComparison.Ordinal) ||
                Array.Exists(Environment.GetCommandLineArgs(), arg => string.Equals(arg, "-nivalis-skip-coroutine-hooks", StringComparison.OrdinalIgnoreCase)));
            traceStartup = dedicatedServer && string.Equals(Environment.GetEnvironmentVariable("NIVALIS_TRACE_STARTUP"), "1", StringComparison.Ordinal);
            Trace("ConfigureIsolation: pin path; minimal=" + minimalRuntime + "; skipCoroutineHooks=" + skipCoroutineHooks);
            pathString = IL2CPP.ManagedStringToIl2Cpp(target);
            pathHandle = IL2CPP.il2cpp_gchandle_new(pathString, true);
            if (pathString == IntPtr.Zero || pathHandle == IntPtr.Zero)
            {
                root = null;
                throw new InvalidOperationException("Could not pin the isolated storage path.");
            }
            try
            {
                IntPtr entry = IL2CPP.il2cpp_resolve_icall("UnityEngine.Application::get_persistentDataPath()");
                if (entry == IntPtr.Zero) throw new InvalidOperationException("Unity's persistent storage entry point was not found.");
                // Native game methods call this icall directly; patching only the C# getter misses them.
                pathDetour = INativeDetour.CreateAndApply(entry, pathCallback, out originalPathCall);
                ES2FilenameData.persistentDataPath = target;
                ES2GlobalSettings.defaultPCDataPath = target;
                // LoadingInfo is cached by the title save browser and captures its path in .ctor.
                SerializationManager.LoadingInfo.instance.Path = target + "/";
                InstallPatches();
                MultiplayerSandboxRuntime.Install();
                Trace("ConfigureIsolation: hooks installed");
                VerifyIsolation();
                Trace("ConfigureIsolation: verified");
                Application.runInBackground = true;
                if (dedicated) Application.targetFrameRate = 30;
            }
            catch
            {
                // No game start/load/write is attempted if isolation failed.
                // A worker must never resume ordinary startup against personal storage after
                // a partial configuration failure. Its caller terminates the process.
                if (!dedicatedServer) RemoveIsolation();
                throw;
            }
        }

        public static void BeginNewWorld()
        {
            Trace("BeginNewWorld: enter");
            RequireMainThread();
            VerifyIsolation();
            MultiplayerSandboxRuntime.PrepareForLoad();
            Trace("BeginNewWorld: isolation verified");
            if (!IsBootReady || GetGame().IsGame || starting)
                throw new InvalidOperationException("A new server world requires a fully initialized title screen.");
            foreach (string file in Directory.GetFiles(root, "*.sav", SearchOption.TopDirectoryOnly))
                if (!string.Equals(Path.GetFileName(file), "global.sav", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A new world requires an empty gameplay save directory. Select a new world directory; existing saves are never imported.");
            loadedSnapshot = false;
            gameplaySceneLoadCompleted = snapshotLoadCompleted = false;
            startingSceneId = -1;
            starting = true;
            DisableWorkerShaderPrewarming(GameSceneManager.Instance);
            Trace("BeginNewWorld: before native StartGame");
            // Native fresh-game path uses manager configuration defaults and the default travel sequence.
            GameSceneManager.Instance.StartGame();
            Trace("BeginNewWorld: native StartGame returned");
        }

        public static void LoadSnapshot(string absolutePath)
        {
            Trace("LoadSnapshot: enter");
            RequireMainThread();
            VerifyIsolation();
            MultiplayerSandboxRuntime.PrepareForLoad();
            Trace("LoadSnapshot: isolation verified");
            if (!IsBootReady || starting || (IsDedicatedServer ? GetGame().IsGame : !IsClientTitleReady()))
                throw new InvalidOperationException("Snapshot loading requires the title screen.");
            string source = ValidateSnapshotPath(absolutePath);
            var info = new FileInfo(source);
            if (!info.Exists || info.Length < 16 || info.Length > 128L * 1024 * 1024)
                throw new InvalidDataException("The server snapshot is missing or exceeds the supported size.");

            importSlot = "multiplayer_import_" + Guid.NewGuid().ToString("N");
            string staging = Path.Combine(root, importSlot + ".sav");
            File.Copy(source, staging, false);
            // Native SaveHeader is a value type. The generated out-SaveHeader wrapper
            // incorrectly passes pointer-sized storage, corrupting the native stack.
            // Both native header validation and native Load begin with these two ES2
            // scalar reads. Use the existing scalar API and let native Load read the
            // remaining game data internally without marshaling SaveHeader across runtimes.
            Trace("LoadSnapshot: before ES2Reader.Create header preflight");
            var headerReader = ES2Reader.Create(staging);
            Trace("LoadSnapshot: after ES2Reader.Create");
            try
            {
                Trace("LoadSnapshot: before ES2 header Version");
                int version = headerReader.Read<int>();
                Trace("LoadSnapshot: after ES2 header Version=" + version);
                if (version < SerializationManagerConfiguration.Instance.MinimalSupportedSaveVersion ||
                    version > SerializationManagerConfiguration.Instance.LatestSaveVersion)
                    throw new InvalidDataException("The server snapshot uses an incompatible game save version.");
                Trace("LoadSnapshot: before ES2 header SceneId");
                int sceneId = headerReader.Read<int>();
                Trace("LoadSnapshot: after ES2 header SceneId=" + sceneId);
                if (!GameSceneManager.Instance.IsGameplayScene(sceneId))
                    throw new InvalidDataException("The server snapshot does not identify a valid gameplay scene.");
                startingSceneId = sceneId;
            }
            finally
            {
                Trace("LoadSnapshot: before ES2 header reader Dispose");
                headerReader.Dispose();
                Trace("LoadSnapshot: after ES2 header reader Dispose");
            }
            loadedSnapshot = true;
            gameplaySceneLoadCompleted = snapshotLoadCompleted = false;
            starting = true;
            DisableWorkerShaderPrewarming(GameSceneManager.Instance);
            Trace("LoadSnapshot: before native Load");
            SerializationManager.Instance.Load(importSlot);
            Trace("LoadSnapshot: native Load returned");
        }

        public static bool WriteSnapshot(string absolutePath)
        {
            RequireMainThread();
            VerifyIsolation();
            if (!IsWorldReady) return false;
            MultiplayerSandboxRuntime.Tick();
            string destination = ValidateSnapshotPath(absolutePath);
            string slot = "multiplayer_export_" + Guid.NewGuid().ToString("N");
            string saved = Path.Combine(root, slot + ".sav");
            try
            {
                if (!SerializationManager.Instance.Save(slot, false) || !File.Exists(saved)) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                string temporary = destination + ".writing";
                File.Copy(saved, temporary, true);
                File.Move(temporary, destination, true);
                return true;
            }
            finally
            {
                // Only our exact generated slot artifacts are removed.
                foreach (string suffix in new[] { ".sav", ".sav.temp", ".png" })
                {
                    string generated = Path.Combine(root, slot + suffix);
                    if (File.Exists(generated)) File.Delete(generated);
                }
            }
        }

        public static void RestoreSinglePlayerStorage()
        {
            RequireMainThread();
            if (!IsIsolated) return;
            var game = GetGame();
            if (game == null || game.IsGame || game.IsLoading || GameSceneManager.IsUnloadingGameplay ||
                game.title == null || !game.title.AreManagersLoaded || !game.title.IsUiLoaded)
                throw new InvalidOperationException("Return completely to the title screen before restoring single-player storage.");
            // Global managers can retain server achievements/settings. Reload them only in a fresh process.
            throw new InvalidOperationException("Restart Nivalis Nights to return to single-player. Multiplayer storage stays isolated until exit.");
        }

        private static string ValidateSnapshotPath(string path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Snapshot paths must be absolute.");
            string full = Path.GetFullPath(path);
            if (!IsWithin(full, root) || SamePath(full, root) || !full.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Snapshots must be .sav files inside this multiplayer world's storage directory.");
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)); dir != null && IsWithin(dir.FullName, root); dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Snapshot paths cannot use linked directories.");
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Snapshot files cannot be links.");
            return full;
        }

        private static void VerifyIsolation()
        {
            if (!IsIsolated || !SamePath(Application.persistentDataPath, root) ||
                !SamePath(ES2FilenameData.persistentDataPath, root))
                throw new InvalidOperationException("Multiplayer storage isolation could not be verified; world operation refused.");
        }

        private static bool SamePath(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWithin(string path, string directory)
        {
            return SamePath(path, directory) || Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static GameSceneManager GetGame(bool trace = false)
        {
            if (!Probe(trace, "GameSceneManager.InstanceNotNull", () => GameSceneManager.InstanceNotNull())) return null;
            if (trace) Trace("Before GameSceneManager.Instance");
            var game = GameSceneManager.Instance;
            if (trace) Trace("After GameSceneManager.Instance");
            return game;
        }

        private static void RequireMainThread()
        {
            if (mainThread == 0 || Thread.CurrentThread.ManagedThreadId != mainThread)
                throw new InvalidOperationException("World runtime operations must execute on Unity's main thread.");
        }

        private static IntPtr RedirectPersistentPath() { return pathString; }

        private static void InstallPatches()
        {
            harmony = new Harmony("local.nivalis.together.world-isolation");
            Patch(typeof(SerializationManager), "CaptureView", new[] { typeof(string) }, nameof(SkipWhileIsolated));
            Patch(typeof(SerializationManager), "SaveSettingsIfNeeded", Type.EmptyTypes, nameof(SkipWhileIsolated));
            Patch(typeof(SerializationManager), "Save", new[] { typeof(string), typeof(bool) }, nameof(ValidateSaveName));
            Patch(typeof(SerializationManager), "Load", new[] { typeof(string) }, nameof(ValidateSaveName));
            Patch(typeof(SteamworksManager), "TryInitialize", Type.EmptyTypes, nameof(SkipOnServer));
            Patch(typeof(SteamworksManager), "Update", Type.EmptyTypes, nameof(SkipOnServer));
            Patch(typeof(SteamworksManager), "GetSteamLanguage", Type.EmptyTypes, nameof(WorkerSteamLanguage));
            Patch(typeof(SettingsManager), "ApplySteamDefaultLanguage", Type.EmptyTypes, nameof(SkipOnServer));
            // The original release inherits SettingsCategory.Initialize; Update 2
            // adds an override whose migration reads a missing key before defaults.
            // Patch only that override, never the shared base initializer.
            if (HasGraphicsSettingsOverride(typeof(SettingsManager.GraphicsSettings),
                typeof(Il2CppSystem.Collections.Generic.Dictionary<string, string>)))
                Patch(typeof(SettingsManager.GraphicsSettings), "Initialize",
                    new[] { typeof(Il2CppSystem.Collections.Generic.Dictionary<string, string>) }, nameof(WorkerGraphicsSettings));
            Patch(typeof(Nivalis.PlayerInputManager), "get_IsController", Type.EmptyTypes, nameof(WorkerIsController));
            Patch(typeof(GameSceneManager), "Awake", Type.EmptyTypes, nameof(EnsureWorkerInputDevices));
            Patch(typeof(GameSceneManager), "get_IsGameplayInitialized", Type.EmptyTypes, nameof(GameplayInitializedPrefix));
            // Reflection cubemap blending uses render textures and faults with Unity's
            // null graphics device. Preserve the light/time simulation and skip only
            // the reflection manager's GPU update, including in diagnostic minimal mode.
            Patch(typeof(Nivalis.DayNightCycle.ReflectionProbeManager), "UpdateReflectionProbes",
                new[] { typeof(float) }, nameof(SkipOnServer));
            if (!skipCoroutineHooks)
                Patch(typeof(GameSceneManager._LoadAreaRoutine_d__85), "MoveNext", Type.EmptyTypes, null, nameof(SceneLoadStep));
            Patch(typeof(SteamworksManager), "UnlockAchievement", new[] { typeof(string) }, nameof(SkipWhileIsolated));
            Patch(typeof(SteamworksManager), "ClearAchievement", new[] { typeof(string) }, nameof(SkipWhileIsolated));
            Patch(typeof(SteamworksManager), "ClearAllAchievements", Type.EmptyTypes, nameof(SkipWhileIsolated));
            if (!minimalRuntime)
            {
                Patch(typeof(UIScrollViewHandler), "Update", Type.EmptyTypes, nameof(SkipOnServer));
                Patch(typeof(RedAlliance.Tools.Wiring3.WireManager), "StartComputeShader", Type.EmptyTypes, nameof(SkipOnServer));
                Patch(typeof(RedAlliance.Tools.Wiring3.WireManager), "DrawWires", Type.EmptyTypes, nameof(SkipOnServer));
                Patch(typeof(LoadingScreenUI), "Show", new[] { typeof(bool), typeof(Il2CppSystem.Action) }, nameof(LoadingScreenPrefix));
                Patch(typeof(LoadingScreenUI), "Hide", Type.EmptyTypes, nameof(SkipOnServer));
                Patch(typeof(ShadersPrewarmingData), "EnsureShadersArePrewarmed", Type.EmptyTypes, nameof(PrewarmPrefix));
                Patch(typeof(ShadersPrewarmingData), "EnsureShadersArePrewarmedRoutine", Type.EmptyTypes, nameof(PrewarmPrefix));
            }
            Patch(typeof(ES2GlobalSettings), "Awake", Type.EmptyTypes, null, nameof(ResetEs2Path));
            Patch(typeof(SerializationManager.LoadingInfo), "Reset", Type.EmptyTypes, null, nameof(ResetLoadingInfoPath));
            Patch(typeof(SerializationManager.LoadingInfo), "get_NewInstance", Type.EmptyTypes, null, nameof(ResetLoadingInfoResultPath));
            if (!skipCoroutineHooks)
            {
                // Iterator suffixes are compiler implementation details: the two
                // supported releases use 42 and 47. Resolve the actual reference
                // wrapper instead of encoding either assembly-specific type token.
                snapshotLoadRoutineType = FindSnapshotLoadRoutine(typeof(SerializationManager));
                snapshotLoadSaveName = snapshotLoadRoutineType.GetProperty("saveName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Patch(snapshotLoadRoutineType, "MoveNext", Type.EmptyTypes, null, nameof(SnapshotLoadStep));
                Trace("Snapshot load hook selected: " + snapshotLoadRoutineType.FullName);
            }
        }

        private static bool HasGraphicsSettingsOverride(Type settingsType, Type dictionaryType)
        {
            MethodInfo method = settingsType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { dictionaryType }, null);
            if (method == null) return false;
            if (method.ReturnType != typeof(void) || method.IsStatic || method.ContainsGenericParameters)
                throw new InvalidOperationException("Unsupported native graphics settings initialization contract.");
            return true;
        }

        private static Type FindSnapshotLoadRoutine(Type managerType)
        {
            const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly;
            Type found = null;
            foreach (Type candidate in managerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!candidate.Name.StartsWith("_LoadRoutine_d__", StringComparison.Ordinal)) continue;
                MethodInfo step = candidate.GetMethod("MoveNext", members, null, Type.EmptyTypes, null);
                PropertyInfo slot = candidate.GetProperty("saveName", members);
                PropertyInfo owner = candidate.GetProperty("__4__this", members);
                if (!candidate.IsClass || candidate.IsAbstract || candidate.ContainsGenericParameters ||
                    step == null || step.ReturnType != typeof(bool) || step.IsStatic || step.ContainsGenericParameters ||
                    slot == null || slot.PropertyType != typeof(string) || slot.GetIndexParameters().Length != 0 ||
                    slot.GetGetMethod(true) == null || slot.GetGetMethod(true).IsStatic ||
                    owner == null || owner.PropertyType != managerType || owner.GetIndexParameters().Length != 0 ||
                    owner.GetGetMethod(true) == null || owner.GetGetMethod(true).IsStatic)
                    throw new InvalidOperationException("Unsupported native snapshot load routine contract: " + candidate.FullName);
                if (found != null)
                    throw new InvalidOperationException("Ambiguous native snapshot load routines: " + managerType.FullName);
                found = candidate;
            }
            if (found == null)
                throw new MissingMethodException("No compatible native snapshot load routine exists in " + managerType.FullName);
            return found;
        }

        private static void Patch(Type type, string name, Type[] parameters, string prefix, string postfix = null)
        {
            MethodInfo original = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null);
            if (original == null) throw new MissingMethodException(type.FullName, name);
            harmony.Patch(original,
                prefix == null ? null : new HarmonyMethod(typeof(DedicatedWorldRuntime), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(DedicatedWorldRuntime), postfix));
        }

        private static bool SkipWhileIsolated() { return !IsIsolated; }
        private static bool SkipOnServer() { return !IsDedicatedServer; }
        private static bool WorkerSteamLanguage(ref string __result)
        {
            if (!IsDedicatedServer) return true;
            __result = "english";
            return false;
        }
        private static bool WorkerIsController(ref bool __result)
        {
            if (!IsDedicatedServer) return true;
            __result = false;
            return false;
        }
        private static bool GameplayInitializedPrefix(GameSceneManager __instance, ref bool __result)
        {
            // Native StartGame sets IsGame before it creates gameplay managers. Existing
            // callers (including other mods) may ask this property during that interval.
            // The original getter dereferences gameplay without a null guard.
            if (!IsIsolated || (__instance != null && __instance.gameplay != null)) return true;
            __result = false;
            return false;
        }
        private static void EnsureWorkerInputDevices(GameSceneManager __instance)
        {
            if (!IsDedicatedServer) return;
            Trace("GameSceneManager.Awake prefix: ensure input devices");
            DisableWorkerShaderPrewarming(__instance);
            // Native character and camera update code reads Keyboard.current/Mouse.current
            // without null checks. Headless Unity has no physical devices; empty virtual
            // devices preserve initialization and movement code without supplying input.
            if (Keyboard.current == null)
                InputSystem.AddDevice("Keyboard", "NivalisWorkerKeyboard", null).Cast<Keyboard>().MakeCurrent();
            if (Mouse.current == null)
                InputSystem.AddDevice("Mouse", "NivalisWorkerMouse", null).Cast<Mouse>().MakeCurrent();
            Trace("GameSceneManager.Awake prefix: input devices ready");
        }
        private static void DisableWorkerShaderPrewarming(GameSceneManager game)
        {
            if (!IsDedicatedServer || game == null || game.shadersPrewarmingData == null) return;
            // LoadAreaRoutine inlines creation of the prewarm coroutine, bypassing the
            // public EnsureShadersArePrewarmedRoutine wrapper and any prefix on it.
            // The coroutine checks this flag before calling ShaderWarmup_Injected;
            // that presentation-only call caused the captured native worker AV.
            game.shadersPrewarmingData.isPrewarmed = true;
            Trace("Worker shader prewarming disabled on active GameSceneManager asset");
        }
        private static void ValidateSaveName(string __0)
        {
            VerifyIsolation();
            string saveName = __0;
            if (string.IsNullOrWhiteSpace(saveName) || saveName != Path.GetFileName(saveName) ||
                saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || saveName == "." || saveName == "..")
                throw new InvalidOperationException("Unsafe multiplayer save slot name rejected.");
        }
        private static bool LoadingScreenPrefix(LoadingScreenUI __instance, Il2CppSystem.Action __1)
        {
            if (!IsDedicatedServer) return true;
            LoadingScreenUI.showing = false;
            if (__1 != null) __1.Invoke();
            return false;
        }
        private static void PrewarmPrefix(ShadersPrewarmingData __instance)
        {
            if (IsDedicatedServer) __instance.isPrewarmed = true;
        }
        private static void ResetEs2Path()
        {
            if (!IsIsolated) return;
            ES2FilenameData.persistentDataPath = root;
            ES2GlobalSettings.defaultPCDataPath = root;
        }
        private static void WorkerGraphicsSettings(SettingsManager.GraphicsSettings __instance,
            ref Il2CppSystem.Collections.Generic.Dictionary<string, string> __0)
        {
            if (!IsDedicatedServer) return;
            // Update 2 reads TextureQuality before the base initializer supplies
            // defaults. A fresh isolated worker has no settings file yet.
            if (__0 != null && __0.ContainsKey("TextureQuality")) return;
            var defaults = __instance.DefaultSettings;
            if (defaults == null || !defaults.TryGetValue("TextureQuality", out string value) || value == null)
                throw new InvalidOperationException("Native default texture quality is unavailable.");
            if (__0 == null) __0 = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
            __0.Add("TextureQuality", value);
            Trace("Worker graphics settings: supplied missing native TextureQuality default");
        }
        private static void ResetLoadingInfoPath(SerializationManager.LoadingInfo __instance)
        {
            if (IsIsolated) __instance.Path = root + "/";
        }
        private static void ResetLoadingInfoResultPath(SerializationManager.LoadingInfo __result)
        {
            if (IsIsolated && __result != null) __result.Path = root + "/";
        }
        private static void SnapshotLoadStep(object __instance, bool __result)
        {
            // __instance is a generated IL2CPP reference wrapper, never a boxed
            // native value type. Reflection preserves the release-specific wrapper.
            if (!IsIsolated || !loadedSnapshot || !starting || __result) return;
            if (__instance == null || snapshotLoadRoutineType == null || snapshotLoadSaveName == null ||
                !snapshotLoadRoutineType.IsInstanceOfType(__instance))
                throw new InvalidOperationException("Native snapshot load callback used an unexpected reference wrapper.");
            if ((string)snapshotLoadSaveName.GetValue(__instance) == importSlot)
            {
                snapshotLoadCompleted = true;
                Trace("Snapshot load routine completed; sceneComplete=" + gameplaySceneLoadCompleted);
                if (gameplaySceneLoadCompleted) starting = false;
            }
        }
        private static void SceneLoadStep(GameSceneManager._LoadAreaRoutine_d__85 __instance, bool __result)
        {
            if (IsDedicatedServer && !__result)
                Trace("LoadAreaRoutine completed: " + __instance.toSceneName + "; title=" + __instance.title);
            if (!IsIsolated || __result) return;
            var game = __instance.__4__this;
            if (game == null) return;
            if (IsDedicatedServer && __instance.title && string.Equals(__instance.toSceneName, game.titleScene, StringComparison.Ordinal) &&
                SceneManager.GetSceneByName(game.titleScene).isLoaded)
                titleLoadCompleted = true;
            if (starting && !__instance.title && !__instance.toCreditsScene && game.IsGame &&
                string.Equals(__instance.toSceneName, game.CurrentGameplaySceneName, StringComparison.Ordinal))
            {
                var scene = game.CurrentGameplayScene;
                if (scene.IsValid() && scene.isLoaded && game.IsGameplayScene(scene.buildIndex) &&
                    (!loadedSnapshot || scene.buildIndex == startingSceneId))
                {
                    gameplaySceneLoadCompleted = true;
                    Trace("World scene fully loaded: " + __instance.toSceneName + "; imported=" + loadedSnapshot +
                        "; snapshotRoutineComplete=" + snapshotLoadCompleted);
                    if (!loadedSnapshot || snapshotLoadCompleted) starting = false;
                }
            }
        }

        private static void Trace(string message)
        {
            // Separate flushed disk log survives abrupt native exits without invoking Unity
            // logging from a Harmony callback. No credentials or personal paths are logged.
            if (!dedicated || string.IsNullOrEmpty(root)) return;
            try { File.AppendAllText(Path.Combine(root, "runtime-trace.log"),
                DateTime.UtcNow.ToString("O") + " tid=" + Thread.CurrentThread.ManagedThreadId + " " + message + Environment.NewLine); }
            catch { }
        }

        public static bool TryBeginClientUnload()
        {
            RequireMainThread();
            if (!IsIsolated || IsDedicatedServer) return false;
            var game = GetGame();
            // A rejected import can leave us at the already-unloaded title with
            // native IsGame still true. EndGame again would start a redundant
            // title transition before the caller can release isolated storage.
            if (ClientTitleWaitReason(game).Length == 0) return false;
            if (game == null || !game.IsGame || !IsSceneLoadIdle(game) || game.IsLoadingExternal ||
                (SerializationManager.InstanceNotNull() && SerializationManager.Instance.IsInitializing)) return false;
            // Failed imports may have no character, so IsWorldReady is too strict.
            // All load operations must nevertheless finish before EndGame starts.
            game.EndGame(); return true;
        }

        public static bool TryReleaseClientIsolationAtTitle()
        {
            ClientIsolationReleaseWaitReason = "";
            if (!IsIsolated) return true;
            RequireMainThread();
            if (IsDedicatedServer) throw new InvalidOperationException("A running server cannot return to personal storage.");
            string reason = ClientTitleWaitReason(GetGame());
            if (reason.Length != 0) return WaitForClientTitle(reason);
            RemoveIsolation();
            // Title loading may have populated this cache while the multiplayer
            // save path was still pinned. Refresh only after restoring the path.
            SerializationManager.LoadingInfo.instance.Reset();
            return true;
        }

        private static bool WaitForClientTitle(string reason)
        { ClientIsolationReleaseWaitReason = reason; return false; }

        public static bool IsClientTitleReady() => ClientTitleWaitReason(GetGame()).Length == 0;

        private static string ClientTitleWaitReason(GameSceneManager game)
        {
            // Native EndGame unloads the gameplay objects/scenes but does not
            // clear IsGame. Both reconnection and storage release must inspect
            // the actual teardown, rather than that sticky session flag.
            if (game == null) return "scene manager unavailable";
            if (!IsSceneLoadIdle(game)) return "native scene load or unload still active";
            if (game.IsLoadingExternal) return "external loading still active";
            if (!SerializationManager.InstanceNotNull()) return "serialization manager unavailable";
            if (SerializationManager.Instance.IsInitializing) return "native serialization still initializing";
            if (game.gameplay != null && (game.gameplay.AreManagersLoaded || game.gameplay.IsUiLoaded))
                return "native gameplay objects still loaded";
            if (game.title == null) return "title objects unavailable";
            if (!game.title.AreManagersLoaded) return "title managers not loaded";
            if (!game.title.IsUiLoaded) return "title UI not loaded";
            if (string.IsNullOrEmpty(game.titleScene) || !SceneManager.GetSceneByName(game.titleScene).isLoaded)
                return "title scene not loaded";
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                // Native LoadArea can store Logo_Screen in its current gameplay
                // field, so inspect every scene for remaining district objects.
                var current = SceneManager.GetSceneAt(i);
                if (IsLoadedGameplayScene(current, game))
                    return "loaded gameplay scene: " + current.name + " (" + current.buildIndex + ")";
            }
            return "";
        }

        private static bool IsLoadedGameplayScene(Scene scene, GameSceneManager game)
        {
            // IsGameplayScene is only a dictionary ContainsKey. Native startup
            // can insert -1 for WorldLocation assets absent from build settings,
            // which misclassifies runtime-only scenes. GetWorldLocation itself
            // excludes -1; require a built, loaded world scene before retaining
            // the multiplayer save-path lock.
            return scene.IsValid() && scene.isLoaded && scene.buildIndex >= 0 &&
                game.GetWorldLocation(scene.buildIndex) != null;
        }

        private static void RemoveIsolation()
        {
            if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
            if (pathDetour != null) { pathDetour.Dispose(); pathDetour = null; }
            // Release the IL2CPP root only after no native call can return its pointer.
            if (pathHandle != IntPtr.Zero) { IL2CPP.il2cpp_gchandle_free(pathHandle); pathHandle = IntPtr.Zero; }
            pathString = IntPtr.Zero;
            if (originalEs2Path != null) ES2FilenameData.persistentDataPath = originalEs2Path;
            if (originalPcPath != null) ES2GlobalSettings.defaultPCDataPath = originalPcPath;
            if (originalRoot != null) SerializationManager.LoadingInfo.instance.Path = originalRoot.TrimEnd('\\', '/') + "/";
            Application.runInBackground = previousBackground;
            Application.targetFrameRate = previousFrameRate;
            root = null;
            MultiplayerSandboxRuntime.Reset();
            dedicated = starting = loadedSnapshot = false;
            titleLoadCompleted = false;
            gameplaySceneLoadCompleted = snapshotLoadCompleted = false;
            startingSceneId = -1;
            minimalRuntime = false;
            skipCoroutineHooks = false;
            traceStartup = false;
            startupTraceChecks = 0;
        }
    }
}
