using System;
using System.Collections.Generic;
using DedicatedWorldRuntime = RuntimeProbe;

// RuntimeProbe receives the actual production methods from DedicatedWorldRuntime.
// Only their native dependencies and side-effect endpoints are replaced here.
internal static class MultiplayerSandboxRuntime
{
    internal static void PrepareForLoad() { }
}
internal struct Scene
{
    internal string name;
    internal int buildIndex;
    internal bool isLoaded, Valid;
    internal bool IsValid() => Valid;
}
internal static class SceneManager
{
    internal static readonly List<Scene> Scenes = new List<Scene>();
    internal static int sceneCount => Scenes.Count;
    internal static Scene GetSceneAt(int i) => Scenes[i];
    internal static Scene GetSceneByName(string name) => Scenes.Find(s => s.name == name);
}
internal sealed class Operation { internal bool isDone; }
internal sealed class TitleObjects { internal bool AreManagersLoaded, IsUiLoaded; }
internal sealed class GameSceneManager
{
    internal static GameSceneManager Instance => RuntimeProbe.Game;
    internal static bool IsUnloadingGameplay;
    internal bool IsGame, IsLoading, levelLoading, isTitleChange, IsLoadingExternal;
    internal string titleScene = "Logo_Screen";
    internal TitleObjects title = new TitleObjects { AreManagersLoaded = true, IsUiLoaded = true };
    internal TitleObjects global = new TitleObjects { AreManagersLoaded = true };
    internal TitleObjects gameplay;
    internal int EndGameCalls;
    internal void EndGame() { EndGameCalls++; isTitleChange = true; }
    internal Operation LoadingOperation, _activeOperation;
    internal readonly Dictionary<int, object> WorldLocations = new Dictionary<int, object>();
    internal int Lookups;
    internal bool IsGameplayScene(int index) => WorldLocations.ContainsKey(index);
    internal object GetWorldLocation(int index)
    {
        Lookups++;
        return index < 0 ? null : WorldLocations.TryGetValue(index, out var result) ? result : null;
    }
}
internal static class SerializationManager
{
    internal sealed class State {
        internal bool IsInitializing;
        internal int LoadCalls;
        internal void Load(string slot) {
            if (!RuntimeProbe.IsIsolated || RuntimeProbe.Game.IsLoadingExternal) throw new Exception("Invalid native Load ordering");
            LoadCalls++; RuntimeProbe.Game.IsLoadingExternal = true;
        }
    }
    internal static State Instance = new State();
    internal static bool InstanceNotNull() => Instance != null;
    internal sealed class LoadingInfo
    {
        internal static readonly LoadingInfo instance = new LoadingInfo();
        internal int ResetCalls;
        internal void Reset()
        {
            if (RuntimeProbe.IsIsolated) throw new Exception("Cache reset before restoring personal path");
            ResetCalls++; RuntimeProbe.Effects.Add("cache reset");
        }
    }
}
// Detached filesystem/ES2 boundaries; no native saves or disk files are used.
internal sealed class InvalidDataException : Exception { internal InvalidDataException(string value) : base(value) { } }
internal sealed class FileInfo
{
    internal static long Bytes = 32;
    internal bool Exists => true;
    internal long Length => Bytes;
    internal FileInfo(string path) { }
}
internal static class File { internal static int Copies; internal static void Copy(string source, string target, bool overwrite) { Copies++; } }
internal static class Path { internal static string Combine(string left, string right) => left + "/" + right; }
internal sealed class ES2Reader
{
    internal static int Version = 153, SceneId = 5, Created, Disposed;
    private int index;
    internal static ES2Reader Create(string path) { Created++; return new ES2Reader(); }
    internal T Read<T>() => (T)(object)(index++ == 0 ? Version : SceneId);
    internal void Dispose() { Disposed++; }
}
internal sealed class SerializationManagerConfiguration
{
    internal static readonly SerializationManagerConfiguration Instance = new SerializationManagerConfiguration();
    internal int MinimalSupportedSaveVersion = 100, LatestSaveVersion = 153;
}
internal static class RuntimeProbe
{
    internal static bool IsIsolated, IsDedicatedServer;
    internal static GameSceneManager Game;
    internal static int Removed, MainThreadChecks;
    internal static readonly List<string> Effects = new List<string>();
    internal static bool starting, loadedSnapshot, gameplaySceneLoadCompleted, snapshotLoadCompleted;
    private static bool skipCoroutineHooks, titleLoadCompleted = true;
    private static int startingSceneId = -1;
    private static string root = "isolated", importSlot;
    public static string ClientIsolationReleaseWaitReason { get; private set; }
    private static GameSceneManager GetGame() => Game;
    private static void RequireMainThread() { MainThreadChecks++; }
    private static void Trace(string value) { }
    private static void VerifyIsolation() { if (!IsIsolated) throw new InvalidOperationException("Storage is not isolated"); }
    private static string ValidateSnapshotPath(string value) => value;
    private static void DisableWorkerShaderPrewarming(GameSceneManager game) { }
    private static void RemoveIsolation() { Removed++; Effects.Add("personal path restored"); IsIsolated = false; }
    internal static bool Classify(Scene scene) => IsLoadedGameplayScene(scene, Game);
    internal static void Setup()
    {
        IsIsolated = true; IsDedicatedServer = false; Removed = MainThreadChecks = 0;
        Effects.Clear(); SerializationManager.LoadingInfo.instance.ResetCalls = 0;
        SerializationManager.Instance = new SerializationManager.State();
        starting = loadedSnapshot = gameplaySceneLoadCompleted = snapshotLoadCompleted = false;
        File.Copies = ES2Reader.Created = ES2Reader.Disposed = 0;
        FileInfo.Bytes = 32; ES2Reader.Version = 153; ES2Reader.SceneId = 5;
        GameSceneManager.IsUnloadingGameplay = false; Game = new GameSceneManager();
        // Native Awake can index an unbuilt WorldLocation at -1. Its own
        // GetWorldLocation(-1) nevertheless returns null.
        Game.WorldLocations[-1] = new object(); Game.WorldLocations[5] = new object();
        SceneManager.Scenes.Clear();
        SceneManager.Scenes.Add(new Scene { name = "Logo_Screen", buildIndex = 0, Valid = true, isLoaded = true });
        SceneManager.Scenes.Add(new Scene { name = "DontDestroyOnLoad", buildIndex = -1, Valid = true, isLoaded = true });
    }
    // PRODUCTION_METHODS
    // PRODUCTION_STORAGE_GUARD
}
internal sealed class MultiplayerJoinRequest
{
    internal string PlayerName = "Tester", Password = "";
    internal ServerEntry Server = new ServerEntry();
}
internal sealed class ServerEntry { internal string Host = "localhost"; internal int Port = 47778; }
internal static class DedicatedProtocol { internal static bool Text(string value, int limit) => !string.IsNullOrEmpty(value) && value.Length <= limit; }
internal sealed class Plugin
{
    internal static readonly Plugin Instance = new Plugin();
    internal Setting PlayerName = new Setting();
    internal sealed class Setting { internal string Value; }
}
internal sealed class JoinProbe
{
    internal bool terminalSession;
    internal int Connections;
    internal string LastStatus;
    internal Menu menu = new Menu();
    internal sealed class Menu { internal void SetBusy(bool value) { } }
    private void Connect(string host, int port, string name, string password) { Connections++; }
    private void Status(string message) { LastStatus = message; }
    internal void TryJoin() => Join(new MultiplayerJoinRequest());
    // PRODUCTION_JOIN
}
internal static class ClientIsolationReleaseTests
{
    private static int checks;
    private static void Check(bool value, string text) { checks++; if (!value) throw new Exception("FAIL: " + text); }
    private static Scene District(bool valid = true, bool loaded = true) => new Scene { name = "District", buildIndex = 5, Valid = valid, isLoaded = loaded };
    private static void Block(Action alter, string reason)
    {
        RuntimeProbe.Setup(); alter();
        Check(!RuntimeProbe.TryReleaseClientIsolationAtTitle(), "fence blocks: " + reason);
        Check(RuntimeProbe.ClientIsolationReleaseWaitReason == reason, "specific wait reason: " + reason);
        Check(RuntimeProbe.IsIsolated && RuntimeProbe.Removed == 0 && SerializationManager.LoadingInfo.instance.ResetCalls == 0 && RuntimeProbe.Effects.Count == 0,
            "no path/cache side effect before fence: " + reason);
        Check(!RuntimeProbe.IsClientTitleReady(), "the same fence blocks title join: " + reason);
        if (RuntimeProbe.Game != null) Check(!RuntimeProbe.StorageEntryAllowed(false), "storage entry retains actual title fence: " + reason);
    }
    private static void BlockSnapshot(Action alter, string message)
    {
        RuntimeProbe.Setup(); RuntimeProbe.Game.IsGame = true; alter();
        bool startingBefore = RuntimeProbe.starting;
        bool rejected = false;
        try { RuntimeProbe.LoadSnapshot("isolated/received-world.sav"); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "snapshot rejects " + message);
        Check(File.Copies == 0 && ES2Reader.Created == 0 &&
            (SerializationManager.Instance == null || SerializationManager.Instance.LoadCalls == 0) && RuntimeProbe.starting == startingBefore,
            "snapshot has no staging/header/native-load effects before " + message);
    }
    public static int Main()
    {
        RuntimeProbe.Setup();
        Check(RuntimeProbe.Game.IsGameplayScene(-1), "fixture includes native ContainsKey(-1) false positive");
        Check(!RuntimeProbe.Classify(SceneManager.GetSceneAt(1)), "runtime-only -1 scene does not retain isolation");
        Check(RuntimeProbe.Game.Lookups == 0, "negative build index never enters native world lookup");
        Check(!RuntimeProbe.Classify(District(valid: false)), "invalid district scene does not block");
        Check(!RuntimeProbe.Classify(District(loaded: false)), "unloaded district scene does not block");
        Check(RuntimeProbe.Classify(District()), "valid loaded real district blocks");
        Check(!RuntimeProbe.Classify(SceneManager.GetSceneAt(0)), "loaded title scene is not gameplay");
        RuntimeProbe.Game.WorldLocations[7] = null;
        Check(!RuntimeProbe.Classify(new Scene { name = "Missing world location", Valid = true, isLoaded = true, buildIndex = 7 }), "null dictionary world location is not gameplay");
        RuntimeProbe.Game.WorldLocations[0] = new object();
        Check(RuntimeProbe.Classify(SceneManager.GetSceneAt(0)), "built index zero still blocks when it truly resolves to a world location");

        Block(() => RuntimeProbe.Game = null, "scene manager unavailable");
        Block(() => RuntimeProbe.Game.gameplay = new TitleObjects { AreManagersLoaded = true }, "native gameplay objects still loaded");
        Block(() => RuntimeProbe.Game.gameplay = new TitleObjects { IsUiLoaded = true }, "native gameplay objects still loaded");
        Block(() => SerializationManager.Instance = null, "serialization manager unavailable");
        Block(() => SerializationManager.Instance.IsInitializing = true, "native serialization still initializing");
        Block(() => RuntimeProbe.Game.IsLoading = true, "native scene load or unload still active");
        Block(() => RuntimeProbe.Game.levelLoading = true, "native scene load or unload still active");
        Block(() => RuntimeProbe.Game.isTitleChange = true, "native scene load or unload still active");
        Block(() => GameSceneManager.IsUnloadingGameplay = true, "native scene load or unload still active");
        Block(() => RuntimeProbe.Game.LoadingOperation = new Operation(), "native scene load or unload still active");
        Block(() => RuntimeProbe.Game._activeOperation = new Operation(), "native scene load or unload still active");
        Block(() => RuntimeProbe.Game.IsLoadingExternal = true, "external loading still active");
        Block(() => RuntimeProbe.Game.title = null, "title objects unavailable");
        Block(() => RuntimeProbe.Game.title.AreManagersLoaded = false, "title managers not loaded");
        Block(() => RuntimeProbe.Game.title.IsUiLoaded = false, "title UI not loaded");
        Block(() => RuntimeProbe.Game.titleScene = "", "title scene not loaded");
        Block(() => RuntimeProbe.Game.titleScene = "Missing_Title", "title scene not loaded");
        Block(() => SceneManager.Scenes[0] = new Scene { name = "Logo_Screen", buildIndex = 0, Valid = true }, "title scene not loaded");
        Block(() => SceneManager.Scenes.Add(District()), "loaded gameplay scene: District (5)");

        RuntimeProbe.Setup(); RuntimeProbe.IsDedicatedServer = true;
        bool rejected = false; try { RuntimeProbe.TryReleaseClientIsolationAtTitle(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && RuntimeProbe.IsIsolated && RuntimeProbe.Removed == 0, "worker cannot restore personal storage");

        RuntimeProbe.Setup();
        RuntimeProbe.Game.LoadingOperation = new Operation { isDone = true };
        RuntimeProbe.Game._activeOperation = new Operation { isDone = true };
        SceneManager.Scenes.Add(District(loaded: false));
        SceneManager.Scenes.Add(new Scene { name = "Built utility", buildIndex = 9, isLoaded = true, Valid = true });
        Check(RuntimeProbe.TryReleaseClientIsolationAtTitle(), "safe title releases despite runtime/utility/unloaded scenes and completed operations");
        Check(!RuntimeProbe.IsIsolated && RuntimeProbe.Removed == 1 && SerializationManager.LoadingInfo.instance.ResetCalls == 1, "one restoration and one cache reset");
        Check(string.Join(",", RuntimeProbe.Effects) == "personal path restored,cache reset", "restore path precedes cache reset");
        Check(RuntimeProbe.ClientIsolationReleaseWaitReason == "", "successful release clears prior wait reason");
        Check(RuntimeProbe.MainThreadChecks == 1, "isolated release enforces main thread");
        for (int i = 0; i < 4; i++) Check(RuntimeProbe.TryReleaseClientIsolationAtTitle(), "repeated released-state poll succeeds");
        Check(RuntimeProbe.Removed == 1 && SerializationManager.LoadingInfo.instance.ResetCalls == 1 && RuntimeProbe.MainThreadChecks == 1,
            "repeated poll cannot restore or reset cache twice");

        RuntimeProbe.Setup(); SceneManager.Scenes.Add(District());
        Check(!RuntimeProbe.TryReleaseClientIsolationAtTitle(), "wait while real gameplay is still loaded");
        SceneManager.Scenes.RemoveAt(SceneManager.Scenes.Count - 1);
        Check(RuntimeProbe.TryReleaseClientIsolationAtTitle() && RuntimeProbe.ClientIsolationReleaseWaitReason == "" && RuntimeProbe.Removed == 1,
            "native unload completion permits later release and clears granular wait reason");

        RuntimeProbe.Setup(); RuntimeProbe.IsIsolated = false;
        Check(RuntimeProbe.StorageEntryAllowed(false), "initial title permits client storage selection");
        var firstJoin = new JoinProbe(); firstJoin.TryJoin();
        Check(firstJoin.Connections == 1, "initial title joins normally");
        RuntimeProbe.Setup(); RuntimeProbe.Game.IsGame = true;
        RuntimeProbe.Game.gameplay = new TitleObjects();
        Check(RuntimeProbe.IsClientTitleReady(), "sticky IsGame with fully torn-down title is ready");
        var beforeRelease = new JoinProbe(); beforeRelease.TryJoin();
        Check(beforeRelease.Connections == 0, "actual Join retains isolation ownership fence even at ready title");
        Check(RuntimeProbe.TryReleaseClientIsolationAtTitle(), "sticky IsGame does not retain isolation after actual teardown");
        Check(RuntimeProbe.Game.IsGame, "release never mutates the native session flag");
        Check(RuntimeProbe.StorageEntryAllowed(false), "post-disconnect title permits client storage selection despite sticky flag");
        Check(!RuntimeProbe.StorageEntryAllowed(true), "dedicated startup cannot replace storage in an existing native game");
        var reconnect = new JoinProbe(); reconnect.TryJoin();
        Check(reconnect.Connections == 1, "post-disconnect title reconnects despite sticky IsGame");
        var terminal = new JoinProbe { terminalSession = true }; terminal.TryJoin();
        Check(terminal.Connections == 0, "actual Join retains terminal-session ownership fence");
        RuntimeProbe.Game.gameplay.AreManagersLoaded = true;
        var liveManagers = new JoinProbe(); liveManagers.TryJoin();
        Check(liveManagers.Connections == 0, "actual Join blocks live gameplay managers with sticky flag");
        RuntimeProbe.Game.gameplay.AreManagersLoaded = false; SceneManager.Scenes.Add(District());
        var liveScene = new JoinProbe(); liveScene.TryJoin();
        Check(liveScene.Connections == 0, "actual Join blocks a real loaded gameplay scene");
        RuntimeProbe.Setup(); RuntimeProbe.Game = null;
        Check(RuntimeProbe.StorageEntryAllowed(true), "dedicated storage isolation still initializes before native startup");

        RuntimeProbe.Setup(); RuntimeProbe.Game.IsGame = true; RuntimeProbe.Game.gameplay = new TitleObjects();
        Check(!RuntimeProbe.TryBeginClientUnload() && RuntimeProbe.Game.EndGameCalls == 0,
            "already-unloaded sticky title never starts another EndGame");
        RuntimeProbe.LoadSnapshot("isolated/received-world.sav");
        Check(SerializationManager.Instance.LoadCalls == 1 && File.Copies == 1 && ES2Reader.Created == 1 && ES2Reader.Disposed == 1,
            "actual snapshot path stages validates and starts exactly one native load after sticky-flag teardown");
        Check(RuntimeProbe.starting && RuntimeProbe.loadedSnapshot && RuntimeProbe.Game.IsLoadingExternal,
            "entry validation occurs before native Load deliberately sets external-loading flag");
        Check(!RuntimeProbe.TryBeginClientUnload() && RuntimeProbe.Game.EndGameCalls == 0,
            "a started external import cannot be unloaded prematurely");
        BlockSnapshot(() => RuntimeProbe.Game.levelLoading = true, "active scene loading");
        BlockSnapshot(() => GameSceneManager.IsUnloadingGameplay = true, "native unload");
        BlockSnapshot(() => RuntimeProbe.Game.IsLoadingExternal = true, "external loading");
        BlockSnapshot(() => RuntimeProbe.Game.LoadingOperation = new Operation(), "pending native operation");
        BlockSnapshot(() => SerializationManager.Instance.IsInitializing = true, "serialization initialization");
        BlockSnapshot(() => RuntimeProbe.Game.gameplay = new TitleObjects { AreManagersLoaded = true }, "live gameplay managers");
        BlockSnapshot(() => RuntimeProbe.Game.gameplay = new TitleObjects { IsUiLoaded = true }, "live gameplay UI");
        BlockSnapshot(() => SceneManager.Scenes.Add(District()), "loaded gameplay scene");
        BlockSnapshot(() => RuntimeProbe.Game.title.IsUiLoaded = false, "incomplete title UI");
        BlockSnapshot(() => RuntimeProbe.starting = true, "duplicate import");
        BlockSnapshot(() => RuntimeProbe.IsDedicatedServer = true, "worker already started game");
        RuntimeProbe.Setup(); RuntimeProbe.Game.IsGame = true; ES2Reader.Version = 999;
        bool badHeader = false;
        try { RuntimeProbe.LoadSnapshot("isolated/received-world.sav"); } catch (InvalidDataException) { badHeader = true; }
        Check(badHeader && SerializationManager.Instance.LoadCalls == 0 && !RuntimeProbe.starting && ES2Reader.Disposed == 1,
            "invalid header fails before native import and disposes its reader");
        Check(!RuntimeProbe.TryBeginClientUnload() && RuntimeProbe.Game.EndGameCalls == 0 && !RuntimeProbe.Game.isTitleChange,
            "failed-before-native-load cleanup does not restart title transition");
        Check(RuntimeProbe.TryReleaseClientIsolationAtTitle(), "failed-before-native-load cleanup can restore title storage immediately");
        RuntimeProbe.Setup(); RuntimeProbe.Game.IsGame = true; RuntimeProbe.Game.gameplay = new TitleObjects { AreManagersLoaded = true };
        Check(RuntimeProbe.TryBeginClientUnload() && RuntimeProbe.Game.EndGameCalls == 1, "live gameplay still begins native unload");
        RuntimeProbe.Setup(); RuntimeProbe.Game.IsGame = true; SceneManager.Scenes.Add(District());
        Check(RuntimeProbe.TryBeginClientUnload() && RuntimeProbe.Game.EndGameCalls == 1, "partial gameplay with a loaded scene still unloads");
        Console.WriteLine("PASS: " + checks + " actual production client-isolation classifier/release assertions.");
        return 0;
    }
}
