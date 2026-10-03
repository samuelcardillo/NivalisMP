using System;
using System.Collections.Generic;
using System.Text.Json;
using NivalisTogether.Dedicated;

public static class PlayerMapRuntimeTests
{
    private static int assertions;
    private static readonly string A = "11111111111111111111111111111111", B = "22222222222222222222222222222222";
    private static readonly string P = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Q = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly List<WorldEvent> Events = new List<WorldEvent>();
    private static readonly List<string> Rejected = new List<string>();
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message) { bool caught = false; try { action(); } catch { caught = true; } Check(caught, message); }
    private static PlayerPresence Presence(string player, string account) => new PlayerPresence { PlayerId = player, AccountId = account, Joined = true };
    private static PlayerPose Pose(string player, string account, string scene, string property = "", float x = 3) => new PlayerPose {
        PlayerId = player, AccountId = account, Scene = scene, PropertyId = property, X = x, Y = 2, Z = 1, Yaw = 45, Active = true,
        ContextGeneration = PlayerMapRuntime.GetContext(player)?.Generation ?? 0 };
    private static InteractionRequest Ack(string player, string account, bool success = true, string scene = "1_Lowtown", string property = "")
    {
        var context = PlayerMapRuntime.GetContext(player);
        return new InteractionRequest { PlayerId = player, AccountId = account, RequestId = Guid.NewGuid().ToString("N"),
            Kind = PlayerMapRestore.AcknowledgementKind, TargetId = context.Epoch, ContextGeneration = context.Generation,
            Data = JsonSerializer.SerializeToElement(new PlayerMapRestoreAcknowledgement { Success = success, Scene = scene, PropertyId = property }, DedicatedProtocol.Json) };
    }
    public static int Main()
    {
        try
        {
            PlayerAccountRuntime.Setup(A, B); PlayerMapRuntime.Configure(Events.Add, (player, reason) => Rejected.Add(player));
            PlayerMapRuntime.InitializeWorker();
            Check(!PlayerMapRuntime.IsSceneOccupied(1), "no authenticated players means no observed NPC map");
            Check(PlayerAccountRuntime.GetSpawnLocation().Scene == "1_Lowtown", "migrates native initial map");
            var spawn = PlayerAccountRuntime.GetSpawnLocation(); spawn.Scene = "malicious";
            Check(PlayerAccountRuntime.GetSpawnLocation().Scene == "1_Lowtown", "spawn getter returns independent copy");
            PlayerAccountRuntime.EnsureSpawnLocation(new PlayerMapLocation { Scene = "3_Midtown" });
            Check(PlayerAccountRuntime.GetSpawnLocation().Scene == "1_Lowtown", "focus changes cannot replace spawn");
            Check(PlayerAccountRuntime.GetAccount(A).StateRevision == 2 && PlayerAccountRuntime.Generation == 1, "spawn migration only dirties world ledger");
            PlayerMapRuntime.Register(Presence(P, A), 0);
            Check(PlayerMapRuntime.IsSceneOccupied(1) && !PlayerMapRuntime.IsSceneOccupied(3), "pending authenticated restore observes its authoritative map only");
            int metadataReads = Nivalis.TravelUnlockManager.MetadataReads;
            for (int query = 0; query < 1000; query++) PlayerMapRuntime.IsSceneOccupied(1);
            Check(Nivalis.TravelUnlockManager.MetadataReads == metadataReads, "frequent NPC occupancy checks never reenter the native portal manager");
            Check(Events.Count == 2 && Events[0].Kind == PlayerSceneContext.EventKind && Events[1].Kind == PlayerMapRestore.EventKind, "assignment precedes restore");
            Check(Events.TrueForAll(e => e.TargetPlayerId == P), "registration is private");
            Check(PlayerMapRuntime.HasPendingRestores && PlayerMapRuntime.GetContext(P).Generation == 1, "initial membership is pending");
            Check(!PlayerMapRuntime.TryAcceptPose(Pose(P, A, "1_Lowtown")), "poses cannot claim authority during restoration");
            PlayerMapRuntime.FlushLocations(); Check(PlayerAccountRuntime.GetAccount(A).Location == null, "pending restore never persists spawn");
            var wrongGeneration = Ack(P, A); wrongGeneration.ContextGeneration++;
            PlayerMapRuntime.HandleRestoreAck(wrongGeneration, 1); Check(PlayerMapRuntime.IsPlayerPending(P), "wrong-generation ACK is inert");
            var wrongAccount = Ack(P, B); PlayerMapRuntime.HandleRestoreAck(wrongAccount, 1);
            Check(PlayerMapRuntime.IsPlayerPending(P), "cross-account ACK is inert");
            var wrongEpoch = Ack(P, A); wrongEpoch.TargetId = Guid.NewGuid().ToString("N");
            PlayerMapRuntime.HandleRestoreAck(wrongEpoch, 1); Check(PlayerMapRuntime.IsPlayerPending(P), "wrong-epoch ACK is inert");
            PlayerMapRuntime.HandleRestoreAck(Ack(P, A), 1);
            Check(!PlayerMapRuntime.HasPendingRestores && PlayerMapRuntime.GetContext(P).Generation == 2, "valid ACK grants next-generation membership");
            Check(PlayerMapRuntime.TryAcceptPose(Pose(P, A, "1_Lowtown")), "assigned pose accepted");
            Check(!PlayerMapRuntime.TryAcceptPose(Pose(P, A, "3_Midtown")), "movement cannot grant new scene");
            Check(!PlayerMapRuntime.TryAcceptPose(Pose(P, B, "1_Lowtown")), "movement cannot cross accounts");
            Check(!PlayerMapRuntime.TryAcceptPose(Pose(P, A, "1_Lowtown", "forged-property")), "movement cannot grant property membership");
            var stale = Pose(P, A, "1_Lowtown"); stale.ContextGeneration--;
            Check(!PlayerMapRuntime.TryAcceptPose(stale), "stale pose rejected");
            var inactive = Pose(P, A, ""); inactive.Active = false; inactive.X = 999;
            Check(PlayerMapRuntime.TryAcceptPose(inactive) && PlayerMapRuntime.GetLocation(P).X == 3, "inactive loading pose never overwrites coordinates");
            var copied = PlayerMapRuntime.GetLocation(P); copied.X = 500;
            Check(PlayerMapRuntime.GetLocation(P).X == 3, "location getter returns a copy");
            PlayerMapRuntime.FlushLocations();
            var account = PlayerAccountRuntime.GetAccount(A);
            Check(account.Location.X == 3 && account.StateRevision == 3, "approved coordinates persist once");
            Check(account.Money == 100000 && account.AppearanceId == "appearance-A" && account.Inventory.GetRawText() == "[]", "map persistence preserves personal assets");
            PlayerMapRuntime.FlushLocations(); Check(PlayerAccountRuntime.GetAccount(A).StateRevision == 3, "unchanged checkpoint does not churn account revision");
            var epoch = Guid.NewGuid().ToString("N"); PlayerMapRuntime.BeginTransition(P, epoch);
            long pendingGeneration = PlayerMapRuntime.GetContext(P).Generation;
            Check(!PlayerMapRuntime.TryAcceptPose(Pose(P, A, "1_Lowtown")), "travel blocks old district pose");
            PlayerMapRuntime.BeginTransition(P, epoch); Check(PlayerMapRuntime.GetContext(P).Generation == pendingGeneration, "identical begin is idempotent");
            Throws(() => PlayerMapRuntime.BeginTransition(P, Guid.NewGuid().ToString("N")), "overlapping transitions rejected");
            PlayerMapRuntime.CancelTransition(P, Guid.NewGuid().ToString("N")); Check(PlayerMapRuntime.IsPlayerPending(P), "wrong-epoch cancellation inert");
            PlayerMapRuntime.CancelTransition(P, epoch);
            Check(!PlayerMapRuntime.IsPlayerPending(P) && PlayerMapRuntime.GetLocation(P).X == 3, "cancel preserves original location");
            epoch = Guid.NewGuid().ToString("N"); PlayerMapRuntime.BeginTransition(P, epoch);
            PlayerMapRuntime.CommitContext(P, "3_Midtown", "", "midtown-portal");
            Check(!PlayerMapRuntime.IsSceneOccupied(1) && PlayerMapRuntime.IsSceneOccupied(3), "committed travel moves observed NPC map without waiting for client movement");
            Check(PlayerMapRuntime.GetLocation(P).Scene == "3_Midtown" && !PlayerMapRuntime.GetLocation(P).HasPosition, "verified travel commits scene without inventing coordinates");
            Check(!PlayerMapRuntime.TryAcceptPose(Pose(P, A, "1_Lowtown")), "source scene cannot reclaim membership after arrival");
            Check(PlayerMapRuntime.TryAcceptPose(Pose(P, A, "3_Midtown", x: 40)), "destination pose updates approved map");
            PlayerMapRuntime.Remove(P);
            Check(!PlayerMapRuntime.IsSceneOccupied(3), "disconnect removes map observation immediately");
            Check(PlayerAccountRuntime.GetAccount(A).Location.X == 40 && PlayerAccountRuntime.GetAccount(A).Location.PortalId == "midtown-portal", "disconnect persists destination and portal");
            DedicatedWorldRuntime.Scene = "4_WorkerFocus";
            PlayerMapRuntime.Register(Presence(P, A), 10);
            Check(PlayerMapRuntime.GetLocation(P).Scene == "3_Midtown", "reconnect uses account map independent of worker focus");
            PlayerMapRuntime.Register(Presence(Q, B), 10);
            Check(PlayerMapRuntime.GetLocation(Q).Scene == "1_Lowtown", "new account uses stable spawn independent of worker focus");
            PlayerMapRuntime.HandleRestoreAck(Ack(P, A, scene: "3_Midtown"), 11);
            PlayerMapRuntime.HandleRestoreAck(Ack(Q, B, success: false), 11);
            Check(Rejected.Count == 1 && Rejected[0] == Q && PlayerMapRuntime.GetContext(P) != null, "failed restoration rejects only that player");
            Check(PlayerAccountRuntime.GetAccount(B).Location == null, "failed restoration preserves old account location");
            PlayerMapRuntime.Register(Presence(Q, B), 20);
            PlayerMapRuntime.Tick(141);
            Check(Rejected.Count == 2 && PlayerMapRuntime.GetContext(Q) == null && PlayerMapRuntime.GetContext(P) != null, "restore timeout is per-player");
            PlayerMapRuntime.Register(Presence(Q, B), 150);
            PlayerMapRuntime.HandleRestoreAck(Ack(Q, B, scene: "wrong-scene"), 151);
            Check(Rejected.Count == 3 && PlayerAccountRuntime.GetAccount(B).Location == null, "wrong scene ACK cannot persist location");
            Throws(() => PlayerMapRuntime.Register(Presence(Q, B), double.NaN), "non-finite timeout rejected");
            var source = new PlayerMapLocation { Scene = "1_Lowtown", X = 10, HasPosition = true };
            PlayerAccountRuntime.SetMapLocation(B, source); source.X = 888;
            Check(PlayerAccountRuntime.GetAccount(B).Location.X == 10, "persistence copies input before storing");
            var revision = PlayerAccountRuntime.GetAccount(B).StateRevision;
            Check(!PlayerAccountRuntime.SetMapLocation(B, PlayerAccountRuntime.GetAccount(B).Location) && PlayerAccountRuntime.GetAccount(B).StateRevision == revision, "duplicate persistence is a no-op");
            PlayerAccountRuntime.OpenScope(); Throws(() => PlayerAccountRuntime.SetMapLocation(B, source), "location cannot mutate during account inventory scope"); PlayerAccountRuntime.CloseScope();
            PlayerMapRuntime.Remove(P); PlayerMapRuntime.Reset();
            Check(!PlayerMapRuntime.IsSceneOccupied(1) && !PlayerMapRuntime.IsSceneOccupied(3), "reset clears player observations and cached scene metadata");
            Nivalis.TravelUnlockManager.Instance = new Nivalis.TravelUnlockManager();
            Nivalis.TravelUnlockManager.Instance._allPortalKeys.Clear();
            Nivalis.TravelUnlockManager.Instance._allPortalKeys.Add(new Nivalis.PortalKey { SceneIndex = 7, SceneName = "1_Lowtown" });
            PlayerMapRuntime.Configure(Events.Add, (player, reason) => Rejected.Add(player)); PlayerMapRuntime.InitializeWorker();
            PlayerMapRuntime.Register(Presence(Q, B), 200);
            Check(PlayerMapRuntime.IsSceneOccupied(7) && !PlayerMapRuntime.IsSceneOccupied(1), "new world initialization replaces old scene-index mappings without native wrapper retention");
            PlayerMapRuntime.Reset(); Nivalis.TravelUnlockManager.Instance = null;
            PlayerMapRuntime.Configure(Events.Add, (player, reason) => Rejected.Add(player));
            Throws(PlayerMapRuntime.InitializeWorker, "missing world metadata cannot initialize an unprotected NPC map authority");
            Check(!PlayerMapRuntime.IsSceneOccupied(7), "failed initialization cannot reuse previous metadata"); PlayerMapRuntime.Reset();
            Console.WriteLine("PASS: " + assertions + " map authority and production persistence assertions."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

namespace NivalisTogether.Dedicated
{
    public static class DedicatedWorldRuntime { public static bool IsDedicatedServer = true, IsWorldReady = true, IsIsolated = true; public static string Scene = "1_Lowtown"; }
    public static class WorkerSceneResidency { public static string CurrentProperty => ""; }
}
namespace Nivalis
{
    public sealed class PortalKey { public int SceneIndex; public string SceneName; }
    public sealed class TravelUnlockManager
    {
        public static TravelUnlockManager Instance = new TravelUnlockManager();
        public static int MetadataReads;
        public static bool InstanceNotNull() { MetadataReads++; return Instance != null; }
        public List<PortalKey> _allPortalKeys = new List<PortalKey> {
            new PortalKey { SceneIndex = 1, SceneName = "1_Lowtown" }, new PortalKey { SceneIndex = 3, SceneName = "3_Midtown" } };
    }
    public sealed class Point { public float x = 1, y = 2, z = 3; }
    public sealed class Rotation { public Point eulerAngles = new Point(); }
    public sealed class Controller { public Rotation Rotation = new Rotation(); }
    public sealed class Transform { public Point position = new Point(); }
    public sealed class Character { public Controller Controller = new Controller(); public Transform transform = new Transform(); }
    public sealed class Player { public Character Character = new Character(); }
    public sealed class PlayerManager { public static PlayerManager Instance = new PlayerManager(); public Player LocalPlayer = new Player(); }
}
