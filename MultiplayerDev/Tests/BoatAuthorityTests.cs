using System;
using System.Collections.Generic;
using System.Text.Json;
using Nivalis;
using NivalisTogether.Dedicated;

public static class BoatAuthorityTests
{
    private const string A = "11111111111111111111111111111111", B = "22222222222222222222222222222222", C = "33333333333333333333333333333333";
    private const string P = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Q = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static int assertions;
    private static readonly List<WorldEvent> Events = new List<WorldEvent>();
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.InvalidDataException) { rejected = true; } Check(rejected, message); }
    private static void Denied(Func<WorldEvent> action, string message) { var value=action().Data.Deserialize<BoatWorldState>(DedicatedProtocol.Json); Check(!value.Success && value.TargetPlayerId.Length==32, message); }
    private static PlayerBoatState Boat(string account = A) => PlayerAccountRuntime.GetBoat(account);
    private static BoatWorldState Snapshot() => BoatAuthority.CaptureSnapshot().Data.Deserialize<BoatWorldState>(DedicatedProtocol.Json);
    private static PlayerBoatState PublicBoat(string account = A) => Array.Find(Snapshot().Boats, b => b.OwnerAccountId == account);
    private static void Actor(double now, float x = 0, string player = P, string account = A)
    {
        var context = PlayerMapRuntime.Contexts[player];
        BoatAuthority.UpdatePlayerPose(new PlayerPose { PlayerId = player, AccountId = account, Scene = context.Scene,
            ContextGeneration = context.Generation, Active = true, X = x }, now);
    }
    private static BoatPose Pose(long sequence, float x = 0, bool driving = false, bool boarded = true, float? fuel = null)
        => new BoatPose { PlayerId = P, AccountId = A, BoatId = Boat().BoatId, Scene = "1_Lowtown", ContextGeneration = PlayerMapRuntime.Contexts[P].Generation,
            Sequence = sequence, AppliedFuelRevision = Boat().Revision, X = x, Qw = 1, Fuel = fuel ?? Boat().Fuel, Driving = driving, Boarded = boarded };
    private static InteractionRequest Request(string kind, string dock, string player = P, string account = A)
        => new InteractionRequest { Kind = kind, PlayerId = player, AccountId = account, RequestId = Guid.NewGuid().ToString("N"), TargetId = dock,
            ContextGeneration = PlayerMapRuntime.Contexts[player].Generation,
            Data = JsonSerializer.SerializeToElement(new BoatInteractionIntent { BoatId = Boat(account).BoatId, DockId = dock }, DedicatedProtocol.Json) };
    private static WorldEvent Command(string kind, string dock, double now)
    { using (PlayerAccountRuntime.WithAccount(A)) return BoatAuthority.Handle(Request(kind, dock), now); }
    private static void Commit() => BoatAuthority.MarkCommitted(PlayerAccountRuntime.Snapshot());
    public static int Run()
    {
        PlayerAccountRuntime.Setup(A, B, C);
        BoatAuthority.Configure(Events.Add, (_, __) => { });
        PlayerMapRuntime.Contexts[P] = new PlayerSceneContext { PlayerId = P, Scene = "1_Lowtown", Generation = 1 };
        PlayerMapRuntime.Contexts[Q] = new PlayerSceneContext { PlayerId = Q, Scene = "1_Lowtown", Generation = 1 };
        BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId = P, AccountId = A, Joined = true });
        BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId = Q, AccountId = B, Joined = true });
        var grant = Boat(); Check(grant.Scene == "" && grant.Fuel == 100, "new account gets one unplaced full basic boat");
        Check(BoatAuthority.EnsureAccountBoat(A).BoatId == grant.BoatId && Boat().Revision == grant.Revision, "repeated grant preserves boat and revision");
        Check(Boat().BoatId != Boat(B).BoatId, "separate accounts get separate boat identities");
        Reject(() => BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId = P, AccountId = B, Joined = true }), "presence cannot switch accounts");
        Actor(1); Actor(1, 0, Q, B);
        var forgedBoat = Request("boat-recall", "dock-a");
        forgedBoat.Data = JsonSerializer.SerializeToElement(new BoatInteractionIntent { BoatId = Boat(B).BoatId, DockId = "dock-a" }, DedicatedProtocol.Json);
        var deniedBoat = BoatAuthority.Handle(forgedBoat, 1).Data.Deserialize<BoatWorldState>(DedicatedProtocol.Json);
        Check(!deniedBoat.Success && deniedBoat.BoatId == Boat().BoatId && deniedBoat.BoatId != Boat(B).BoatId,
            "spoofed boat request returns a denial addressed to the authenticated owner's boat");
        Check(deniedBoat.TargetPlayerId == P && deniedBoat.RequestId == forgedBoat.RequestId && deniedBoat.Operation == "recall",
            "boat denial preserves private recipient and pending-control request identity");
        var deniedDelivery = new BoatClientEventLedger().Observe(deniedBoat, A, Boat().BoatId);
        Check(deniedDelivery.Operation != null && !deniedDelivery.Operation.Success,
            "actual client event ledger delivers the denial to its notice and pending-control handler");
        var forgedAccount = Request("boat-recall", "dock-a"); forgedAccount.AccountId = B;
        var deniedAccount = BoatAuthority.Handle(forgedAccount, 1).Data.Deserialize<BoatWorldState>(DedicatedProtocol.Json);
        Check(!deniedAccount.Success && deniedAccount.BoatId == Boat().BoatId,
            "denied boat identity comes from registered presence rather than spoofed request account");
        var oldLedger = PlayerAccountRuntime.Snapshot();
        var recall = Request("boat-recall", "dock-a"); var result = BoatAuthority.Handle(recall, 1);
        Check(Boat().DockId == "dock-a" && Boat().Scene == "1_Lowtown", "recall assigns the native dock and district");
        Check(PublicBoat().Scene == "", "uncommitted recall does not leak into snapshots");
        long revision = Boat().Revision;
        Check(BoatAuthority.Handle(recall, 1).Data.GetRawText() == result.Data.GetRawText() && Boat().Revision == revision, "duplicate request is idempotent");
        var changed = Request("boat-undock", "dock-a"); changed.RequestId = recall.RequestId;
        Denied(() => BoatAuthority.Handle(changed, 1), "changed request replay returns a private denial");
        BoatAuthority.MarkCommitted(oldLedger);
        Check(PublicBoat().Scene == "", "older in-flight checkpoint cannot release pending command");
        Check(!BoatAuthority.AcceptPose(Pose(1), 1.1), "poses cannot mutate an uncommitted command");
        Commit(); Check(PublicBoat().DockId == "dock-a", "matching committed revision releases new boat state");
        Denied(() => BoatAuthority.Handle(Request("boat-recall", "dock-a", Q, B), 1.2), "second account receives denial for occupied dock without failing worker");
        var offline = BoatAuthority.EnsureAccountBoat(C); offline.Scene = "1_Lowtown"; offline.DockId = "dock-b";
        PlayerAccountRuntime.SetBoat(C, offline);
        Denied(() => BoatAuthority.Handle(Request("boat-recall", "dock-b", Q, B), 1.2), "offline owner's dock lease is preserved");
        Actor(1.3, 0.2f); Check(BoatAuthority.AcceptPose(Pose(1, 0.2f, true), 1.3), "reserved native dock placement accepts immobile cockpit occupancy at its canonical berth");
        Check(!BoatAuthority.AcceptPose(Pose(1, 0.2f), 1.5), "duplicate movement sequence is rejected");
        Check(!BoatAuthority.AcceptPose(Pose(2, 0.2f), 1.31), "movement rate is bounded");
        Check(!BoatAuthority.AcceptPose(Pose(2, 30), 1.5), "docked boat cannot move away through a pose");
        Check(PublicBoat().PlayerDriving && PublicBoat().DockId == "dock-a", "cockpit occupancy does not release dock lease");
        Check(!BoatAuthority.AcceptPose(Pose(2, 2, fuel: 101), 1.5), "movement cannot grant fuel");
        var impostor = Pose(2, 2); impostor.BoatId = Boat(B).BoatId;
        Check(!BoatAuthority.AcceptPose(impostor, 1.5), "movement cannot control another owner's boat");
        Command("boat-undock", "dock-a", 1.6);
        Check(PublicBoat().DockId == "dock-a", "pending undock keeps previous public dock state");
        Denied(() => BoatAuthority.Handle(Request("boat-recall", "dock-a", Q, B), 1.7), "old dock lease survives until undock commits");
        Commit(); Check(Boat().DockId == "", "undock releases own lease after commit");
        long beforeMotionRevision = Boat().Revision;
        Actor(2, 5); Check(BoatAuthority.AcceptPose(Pose(2, 5, true, fuel: 90), 2), "owner can drive undocked boat and consume fuel");
        Check(Boat().Revision == beforeMotionRevision && Boat().Fuel == 100, "ordinary boat motion does not dirty persistent accounts");
        Check(PublicBoat().X == 5 && PublicBoat().Fuel == 90, "live authoritative motion reaches network snapshots");
        BoatAuthority.FlushPositions();
        Check(Boat().Revision == beforeMotionRevision + 1 && Boat().Fuel == 90, "checkpoint flush persists latest boat motion once");
        BoatAuthority.FlushPositions(); Check(Boat().Revision == beforeMotionRevision + 1, "unchanged flush does not churn boat revisions");
        Check(!BoatAuthority.AcceptPose(Pose(3, 500, true, fuel: 90), 2.2), "implausible movement jump is rejected");
        var foreign = Pose(3, 5, true); foreign.AccountId = B;
        Check(!BoatAuthority.AcceptPose(foreign, 2.2), "movement account binding is checked");
        var staleGeneration = Pose(3, 5, true); staleGeneration.ContextGeneration++;
        Check(!BoatAuthority.AcceptPose(staleGeneration, 2.2), "unassigned context generation is rejected");
        PlayerMapRuntime.Contexts[P].TravelPending = true;
        Check(!BoatAuthority.AcceptPose(Pose(3, 5, true), 2.2), "map transition fences movement");
        PlayerMapRuntime.Contexts[P].TravelPending = false; PlayerMapRuntime.Contexts[P].PropertyId = "apartment";
        Check(!BoatAuthority.AcceptPose(Pose(3, 5, true), 2.2), "private apartment context cannot drive a public boat");
        PlayerMapRuntime.Contexts[P].PropertyId = "";
        Check(!BoatAuthority.AcceptPose(Pose(3, 5, true), 6), "stale character pose cannot authorize boat movement");
        Actor(6, 5); Command("boat-dock", "dock-a", 6); Commit();
        long beforeFuelRevision = Boat().Revision; int beforeMoney = PlayerAccountRuntime.Accounts[A].Money;
        Denied(() => BoatAuthority.Handle(Request("boat-refuel", "dock-a"), 6.1), "refuel requires owner's native account scope");
        Check(Boat().Revision == beforeFuelRevision, "rejected refuel does not mutate boat");
        PlayerAccountRuntime.Accounts[A].Money = 500;
        Denied(() => Command("boat-refuel", "dock-a", 6.1), "insufficient personal funds return denial without worker failure");
        Check(Boat().Fuel == 90 && PlayerAccountRuntime.Accounts[A].Money == 500, "failed fuel purchase leaves wallet and tank unchanged");
        PlayerAccountRuntime.Accounts[A].Money = beforeMoney;
        var priorFuelLedger = PlayerAccountRuntime.Snapshot();
        var inFlightBeforeRefuel = Pose(3, fuel: 90);
        Command("boat-refuel", "dock-a", 6.2);
        long paidFuelRevision = Boat().Revision;
        Check(Boat().Fuel == 100 && PlayerAccountRuntime.Accounts[A].Money == beforeMoney - 1000, "native fuel price charges only the owner's wallet");
        Check(PlayerAccountRuntime.Accounts[B].Money == 100000 && Boat(B).Fuel == 100, "other account wallet and boat remain untouched");
        Check(PublicBoat().Fuel == 90, "fuel state is withheld until wallet checkpoint commits");
        BoatAuthority.MarkCommitted(priorFuelLedger); Check(PublicBoat().Fuel == 90, "older revision cannot expose newly purchased fuel");
        Commit(); Check(PublicBoat().Fuel == 100, "fuel and wallet become visible after matching commit");
        Actor(6.25, 0);
        Check(!BoatAuthority.AcceptPose(inFlightBeforeRefuel, 6.25), "already in-flight old fuel cannot erase a committed purchase before native receipt application");
        var futureFuel = Pose(3); futureFuel.AppliedFuelRevision = paidFuelRevision + 1;
        Check(!BoatAuthority.AcceptPose(futureFuel, 6.25), "client cannot acknowledge a future fuel revision");
        BoatAuthority.FlushPositions(); Commit();
        Check(Boat().Fuel == 100 && PublicBoat().Fuel == 100 && PlayerAccountRuntime.Accounts[A].Money == beforeMoney - 1000,
            "next checkpoint preserves the paid full tank and exact debit despite stale in-flight pose");
        Command("boat-undock", "dock-a", 6.3); Commit(); Actor(6.4, 0);
        var consumedAfterApply = Pose(3, 0, true, fuel: 99.5f); consumedAfterApply.AppliedFuelRevision = paidFuelRevision;
        Check(BoatAuthority.AcceptPose(consumedAfterApply, 6.4), "native fuel consumption resumes with applied refuel revision after undock");
        BoatAuthority.FlushPositions(); Commit();
        Check(Boat().Fuel == 99.5f && PlayerAccountRuntime.Accounts[A].Money == beforeMoney - 1000,
            "later checkpoint persists legitimate fuel consumption without charging again");
        var staleAfterCheckpoint = BoatProtocol.Copy(inFlightBeforeRefuel); staleAfterCheckpoint.Sequence = 4;
        Check(!BoatAuthority.AcceptPose(staleAfterCheckpoint, 6.45) && PublicBoat().Fuel == 99.5f,
            "a later checkpoint or undock does not clear the applied-fuel fence");
        var portal = new BoatPortal { Pointer = new IntPtr(100), keyTo = new PortalKey { SceneName = "2_Meridian_Market" } };
        Reject(() => BoatAuthority.ValidateTravel(P, Boat(B).BoatId, portal, 6.4), "native overlap cannot grant travel in another account's boat");
        Check(BoatReplication.ContactCalls == 0, "account ownership is checked before native geometry");
        BoatReplication.ContactKind = "outside";
        Reject(() => BoatAuthority.ValidateTravel(P, Boat().BoatId, portal, 6.4), "nearby boat center cannot substitute actual hull overlap");
        BoatReplication.ContactKind = "empty";
        Reject(() => BoatAuthority.ValidateTravel(P, Boat().BoatId, portal, 6.4), "unsupported or absent native hull boxes fail closed");
        BoatReplication.ContactKind = "invalid";
        Reject(() => BoatAuthority.ValidateTravel(P, Boat().BoatId, portal, 6.4), "invalid native collider geometry cannot grant a passage");
        BoatReplication.ContactKind = "edge";
        var departure = BoatAuthority.ValidateTravel(P, Boat().BoatId, portal, 6.4);
        Check(departure.BoatId == Boat().BoatId && departure.PlayerDriving, "owned driving boat obtains a passage authorization");
        Check(BoatReplication.LastContactBoat.BoatId == departure.BoatId && BoatReplication.LastContactBoat.X == departure.X,
            "collider geometry uses the authoritative owner's accepted boat pose");
        Check(BoatReplication.LastContact.PortalBox.X > 4, "rotated authored hull may touch while its center is outside the old radius");
        int priorContactCalls = BoatReplication.ContactCalls; BoatReplication.ContactKind = "outside";
        Check(BoatAuthority.ValidateTravel(P, Boat().BoatId, portal, 6.5).BoatId == departure.BoatId && BoatReplication.ContactCalls == priorContactCalls,
            "accepted passage retains its exact validated departure across asynchronous preparation");
        Check(!BoatAuthority.AcceptPose(Pose(4, 1, true), 6.6), "authorized passage freezes normal boat movement");
        PlayerMapRuntime.Contexts[P].TravelPending = true; PlayerMapRuntime.Contexts[P].Generation = 2;
        var arrival = Pose(4, 100, true); arrival.Scene = "2_Meridian_Market";
        var invalidArrival = BoatProtocol.Copy(arrival); invalidArrival.Fuel++;
        Reject(() => BoatAuthority.CommitTravel(P, invalidArrival, arrival.Scene), "travel acknowledgement cannot grant fuel");
        var staleFuelArrival = BoatProtocol.Copy(arrival); staleFuelArrival.AppliedFuelRevision = paidFuelRevision - 1;
        Reject(() => BoatAuthority.CommitTravel(P, staleFuelArrival, arrival.Scene), "map arrival cannot bypass the applied refuel revision fence");
        Reject(() => BoatAuthority.CommitTravel(P, arrival, "wrong-scene"), "travel cannot grant an unapproved destination");
        var olderCheckpointBoat=Boat();int eventsBeforeArrival=Events.Count;
        BoatAuthority.CommitTravel(P, arrival, arrival.Scene);
        Check(Boat().Scene == arrival.Scene && Boat().X == 100 && Boat().DockId == "", "validated pending-generation arrival persists boat destination");
        Check(Events.Count==eventsBeforeArrival+1,"arrival publishes its boat without waiting for periodic Tick or an older in-flight checkpoint");
        var arrivalEvent=Events[Events.Count-1];
        var publishedArrival=arrivalEvent.Data.Deserialize<BoatWorldState>(DedicatedProtocol.Json);
        var publishedBoat=Array.Find(publishedArrival.Boats,b=>b.OwnerAccountId==A);
        Check(arrivalEvent.Kind==BoatProtocol.EventKind && arrivalEvent.TargetPlayerId=="" && publishedArrival.Operation=="snapshot",
            "arrival uses the established live global snapshot channel rather than fabricating a durable action receipt");
        Check(publishedBoat.Scene==arrival.Scene && publishedBoat.Revision==Boat().Revision && publishedBoat.Revision>olderCheckpointBoat.Revision &&
            publishedBoat.X==100 && publishedBoat.PlayerAboard && publishedBoat.PlayerDriving,
            "arrival snapshot carries committed account boat identity, newer revision, destination and native attachment");
        Check(publishedArrival.RuntimePoses.Length==0,"arrival publication cannot carry stale source-scene runtime poses");
        BoatClientStateProbe.Reset(A);BoatClientStateProbe.SetOwnState(publishedBoat);BoatClientStateProbe.SetOwnState(olderCheckpointBoat);
        Check(BoatClientStateProbe.State.Scene==arrival.Scene && BoatClientStateProbe.State.Revision==publishedBoat.Revision,
            "actual client SetOwnState ignores an older checkpoint boat after ordered arrival publication");
        VerifyArrivalOrdering(arrivalEvent);
        Reject(() => BoatAuthority.CommitTravel(P, arrival, arrival.Scene), "arrival acknowledgement cannot commit twice");
        PlayerMapRuntime.Contexts[P].TravelPending = false; PlayerMapRuntime.Contexts[P].Generation = 3; PlayerMapRuntime.Contexts[P].Scene = arrival.Scene;
        Actor(7, 102); var finalPose = BoatProtocol.Copy(arrival); finalPose.ContextGeneration=3; finalPose.Sequence=5; finalPose.X=102;
        Check(BoatAuthority.AcceptPose(finalPose,7), "new settled generation resumes boat motion after arrival");
        BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId = P, AccountId = A, Joined = false });
        Check(Boat().PlayerDriving && Boat().PlayerAboard && Boat().X == 102 && PublicBoat() == null, "disconnect flushes latest motion and preserves saved at-sea attachment");
        BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId = P, AccountId = A, Joined = true });
        Check(PublicBoat().PlayerAboard && PublicBoat().PlayerDriving && PublicBoat().X == 102, "reconnect restores the saved driving boat instead of stranding character");
        Actor(7.2, 102); var reconnectedPose = Pose(1, 102, true, fuel: 99.4f); reconnectedPose.Scene = arrival.Scene;
        Check(BoatAuthority.AcceptPose(reconnectedPose, 7.2), "reconnected native boat resumes consumption from its restored fuel revision");
        BoatAuthority.FlushPositions(); Check(Boat().Fuel == 99.4f, "reconnect fuel consumption persists without resetting the paid tank");
        BoatAuthority.Tick(10); int published = Events.Count; BoatAuthority.Tick(10.05);
        Check(Events.Count == published, "unchanged roster does not publish redundant high-frequency snapshots");
        BoatAuthority.Reset(); BoatAuthority.CancelTravel(P); Check(true,"travel cancellation is safe before boat authority configuration");
        RecallTrajectory();
        return assertions;
    }

    private static void VerifyArrivalOrdering(WorldEvent boat)
    {
        var scheduler=new WorldEventScheduler();
        scheduler.Enqueue(new WorldEvent{Kind="npcs",Data=JsonSerializer.SerializeToElement(new{Sequence=1})});
        scheduler.Enqueue(boat);
        scheduler.Enqueue(new WorldEvent{Kind=PlayerSceneContext.EventKind,Data=JsonSerializer.SerializeToElement(new{Scene="2_Meridian_Market"})});
        scheduler.Enqueue(new WorldEvent{Kind="group-travel",Data=JsonSerializer.SerializeToElement(new{Phase="complete"})});
        Check(!scheduler.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),
            "control credit cannot let scene context or travel-complete pass the queued arrival boat while bulk credit is exhausted");
        var order=new List<string>();
        while(scheduler.TryPeek(WorldEventFlow.BulkWindowBytes,WorldEventFlow.ControlWindowBytes,out var next))
        {order.Add(next.Event.Kind);scheduler.Remove(next);}
        Check(order.Count==4 && order.IndexOf(BoatProtocol.EventKind)<order.IndexOf(PlayerSceneContext.EventKind) &&
            order.IndexOf(BoatProtocol.EventKind)<order.IndexOf("group-travel"),
            "actual scheduler delivers boat arrival before settled context and travel-complete barriers");
    }

    private static void RecallTrajectory()
    {
        PlayerAccountRuntime.Setup(A, B);
        DedicatedWorldRuntime.Scene = "1_Lowtown";
        BoatAuthority.Configure(Events.Add, (_, __) => { });
        PlayerMapRuntime.Contexts[P] = new PlayerSceneContext { PlayerId=P, Scene="1_Lowtown", Generation=1 };
        PlayerMapRuntime.Contexts[Q] = new PlayerSceneContext { PlayerId=Q, Scene="1_Lowtown", Generation=1 };
        BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId=P, AccountId=A, Joined=true });
        BoatAuthority.SetPlayerPresent(new PlayerPresence { PlayerId=Q, AccountId=B, Joined=true });
        var peer = Boat(B);
        Actor(100); Command("boat-recall", "dock-a", 100); Commit();
        long sequence=1;
        foreach(float offset in new[] { -21.8f, -16.89f, -12.46f, -8.88f, -5.81f, -3.42f, -1.54f })
        {
            Actor(101+sequence*0.2);
            Check(!BoatAuthority.AcceptPose(Pose(sequence++,offset,boarded:false),101+sequence*0.2),
                "native recall animation cannot become the persisted dock position: "+offset);
            Check(PublicBoat().X==0 && PublicBoat().DockId=="dock-a", "rejected recall animation retains canonical berth and reservation");
        }
        Actor(104);
        var rotated=Pose(sequence,0,boarded:false);rotated.Qw=0;rotated.Qy=1;
        Check(!BoatAuthority.AcceptPose(rotated,104),"first dock pose must match canonical berth rotation");
        Check(BoatAuthority.AcceptPose(Pose(sequence++,0,boarded:false),104.2),"finished recall publishes its exact native berth without a poisoned baseline");
        Check(!BoatAuthority.AcceptPose(Pose(sequence,-21.8f,boarded:false),104.4),"late recall animation stays rejected after canonical publication");
        Actor(104.6); Command("boat-undock","dock-a",104.6); Commit();
        Actor(105,0.05f);
        Check(BoatAuthority.AcceptPose(Pose(sequence++,0.05f,true),105),"normal undock accepts native cockpit boarding after recall finishes");
        Check(PublicBoat().PlayerAboard && PublicBoat().PlayerDriving,"accepted undock publishes persistent attachment flags");
        Actor(105.2,0.1f);
        Check(BoatAuthority.AcceptPose(Pose(sequence++,0.1f,true,fuel:99.99f),105.2),"driving and fuel synchronization continue after recall");
        BoatAuthority.FlushPositions();
        Check(Boat().PlayerDriving && Boat().PlayerAboard && Boat().Fuel==99.99f,"checkpoint retains native driving and consumed fuel");
        Actor(105.4,100);
        Check(!BoatAuthority.AcceptPose(Pose(sequence,100,true,fuel:99.99f),105.4),"recall correction does not widen ordinary undocked movement allowance");
        Check(Boat(B).BoatId==peer.BoatId && Boat(B).Scene==peer.Scene && Boat(B).Fuel==peer.Fuel && Boat(B).Revision==peer.Revision,
            "recall trajectory and undock do not mutate another owner's boat");
        BoatAuthority.Reset();
    }
}
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public float sqrMagnitude => x*x+y*y+z*z; public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
    public sealed class Collider { public Vector3 ClosestPoint(Vector3 point)=>point; }
    public sealed class Scene { public string name="1_Lowtown"; public bool isLoaded=true; }
    public sealed class GameObject { public Scene scene=new Scene(); }
}
namespace Nivalis
{
    public sealed class PortalKey { public object Apartment; public string SceneName; }
    public sealed class BoatPortal { public IntPtr Pointer; public bool isActiveAndEnabled=true, IsTravelAllowed=true; public PortalKey keyTo;
        public UnityEngine.GameObject gameObject=new UnityEngine.GameObject(); public T GetComponent<T>() where T:new()=>new T(); public T GetComponentInChildren<T>() where T:new()=>new T(); }
    public sealed class PlayerManager { public static PlayerManager Instance=new PlayerManager(); public LocalPlayer LocalPlayer=new LocalPlayer(); }
    public sealed class LocalPlayer { public Inventory Inventory=new Inventory(); }
    public sealed class Inventory { public int _money; public int Money=>_money; public void TakeMoney(int amount) { _money-=amount; } }
}
namespace NivalisTogether.Dedicated
{
    public static class DedicatedWorldRuntime { public static bool IsDedicatedServer=true; public static string Scene="1_Lowtown"; }
    public static class PlayerMapRuntime { public static readonly Dictionary<string,PlayerSceneContext> Contexts=new Dictionary<string,PlayerSceneContext>();
        public static PlayerSceneContext GetContext(string player)=>Contexts.TryGetValue(player,out var value)?PlayerSceneContext.Copy(value):null; }
    public static class PlayerAccountRuntime
    {
        public static readonly Dictionary<string,AccountState> Accounts=new Dictionary<string,AccountState>(); public static string CurrentAccountId="";
        public static void Setup(params string[] ids) { Accounts.Clear(); foreach(var id in ids)Accounts[id]=new AccountState { AccountId=id,Money=100000,Initialized=true }; }
        public static PlayerBoatState GetBoat(string account)=>BoatProtocol.Copy(Accounts[account].Boat);
        public static PlayerBoatState EnsureBoat(string account,PlayerBoatState value) { if(Accounts[account].Boat==null)Accounts[account].Boat=BoatProtocol.Copy(value); return GetBoat(account); }
        public static PlayerBoatState SetBoat(string account,PlayerBoatState value) { BoatProtocol.Validate(value); var old=Accounts[account].Boat;
            if(old==null||old.BoatId!=value.BoatId||old.PrefabId!=value.PrefabId||value.OwnerAccountId!=account)throw new InvalidOperationException();
            var saved=BoatProtocol.Copy(value);saved.Revision=old.Revision+1;Accounts[account].Boat=saved;return GetBoat(account); }
        public static PlayerBoatState[] ListBoats() { var boats=new List<PlayerBoatState>();foreach(var account in Accounts.Values)if(account.Boat!=null)boats.Add(BoatProtocol.Copy(account.Boat));return boats.ToArray(); }
        public static AccountWorldState Snapshot() { var entries=new List<AccountState>();foreach(var account in Accounts.Values)entries.Add(new AccountState { AccountId=account.AccountId,Boat=BoatProtocol.Copy(account.Boat),Money=account.Money });return new AccountWorldState { Accounts=entries.ToArray() }; }
        public static IDisposable WithAccount(string account) { if(CurrentAccountId!="")throw new InvalidOperationException();CurrentAccountId=account;PlayerManager.Instance.LocalPlayer.Inventory._money=Accounts[account].Money;return new Scope(); }
        private sealed class Scope:IDisposable { public void Dispose(){Accounts[CurrentAccountId].Money=PlayerManager.Instance.LocalPlayer.Inventory.Money;CurrentAccountId="";PlayerManager.Instance.LocalPlayer.Inventory._money=0;} }
    }
    public sealed class BoatDockDescription { public string DockId="",Scene="1_Lowtown";public float X,Y,Z,Qx,Qy,Qz,Qw=1,InteractionX,InteractionY,InteractionZ,FuelCapacity=100,FuelPricePerLiter=100; }
    public static class BoatReplication
    {
        public static string ContactKind = "match";
        public static int ContactCalls;
        public static PlayerBoatState LastContactBoat;
        public static BoatTriggerContact LastContact;
        public static PlayerBoatState CreateStartingState(string account)=>new PlayerBoatState { BoatId=Guid.NewGuid().ToString("N"),OwnerAccountId=account,PrefabId="native-basic",Fuel=100,Revision=1 };
        public static BoatDockDescription DescribeDock(string dock)=>new BoatDockDescription { DockId=dock };
        public static BoatTriggerContact DescribePortalContact(PlayerBoatState boat, BoatPortal portal)
        {
            ContactCalls++; LastContactBoat = BoatProtocol.Copy(boat);
            var target = new BoatTriggerBox { HalfX = 1, HalfY = 1, HalfZ = 1 };
            var hull = new BoatTriggerBox { X = boat.X, Y = boat.Y, Z = boat.Z, HalfX = 1, HalfY = 1, HalfZ = 1 };
            if (ContactKind == "outside") target.X = 500;
            if (ContactKind == "invalid") hull.HalfX = float.NaN;
            if (ContactKind == "edge")
            {
                target.X = target.Z = 7;
                hull.HalfZ = 10; hull.HalfX = 0.5f;
                hull.Qy = MathF.Sin(MathF.PI / 8); hull.Qw = MathF.Cos(MathF.PI / 8);
            }
            return LastContact = new BoatTriggerContact { PortalBox = target, HullBoxes = ContactKind == "empty" ? Array.Empty<BoatTriggerBox>() : new[] { hull } };
        }
    }
}
