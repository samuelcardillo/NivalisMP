using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NivalisTogether.Dedicated;
using NivalisTogether.Server;

public static partial class ChatServiceTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object PeerFor(Fixture f, string id) => ((IEnumerable)typeof(DedicatedService).GetField("_peers", Hidden).GetValue(f.Service))
        .Cast<object>().First(x => (string)x.GetType().GetField("Id").GetValue(x) == id);
    private static object StateFor(Fixture f, string id)
    {
        var table = typeof(DedicatedService).GetField("_chatPeers", Hidden).GetValue(f.Service);
        var args = new[] { PeerFor(f, id), null };
        return (bool)table.GetType().GetMethod("TryGetValue").Invoke(table, args) ? args[1] : null;
    }
    // Routing cases each start with a full bucket. Rate-limit cases below invoke
    // the actual handler at deterministic times instead of sleeping real seconds.
    private static void Refill(Fixture f, string id)
    { var state = StateFor(f, id); if (state != null) { state.GetType().GetField("Tokens").SetValue(state, 5d); state.GetType().GetField("Refilled").SetValue(state, default(DateTime)); } }
    private static void Drain(Fixture f) { for (int i = 0; i < 30; i++) f.Pump(); }
    private static void ClearChat(Fixture f)
    { Drain(f); foreach (var c in f.Clients) c.Received.RemoveAll(m => m.Type == ChatProtocol.MessageKind || m.Type == ChatProtocol.RejectedKind); }
    private static ChatRequest Request(string channel = "local", string target = "", string text = "hello") =>
        new ChatRequest { RequestId = Guid.NewGuid().ToString("N"), Channel = channel, TargetName = target, Text = text };
    private static ChatMessage SendChat(Fixture f, Client from, string id, ChatRequest request, params Client[] recipients)
    {
        Refill(f, id); ClearChat(f);
        Check(from.Connection.Send(ChatProtocol.SendKind, request), "Chat frame queued");
        Drain(f);
        ChatMessage result = null;
        foreach (var c in f.Clients)
        {
            var received = c.Received.Where(m => m.Type == ChatProtocol.MessageKind).ToArray();
            Check(received.Length == (recipients.Contains(c) ? 1 : 0), "Chat delivered exactly once only to expected recipients");
            Check(!c.Received.Any(m => m.Type == ChatProtocol.RejectedKind), "Successful chat has no rejection");
            foreach (var wire in received)
            {
                var value = wire.Read<ChatMessage>(); ChatProtocol.ValidateMessage(value);
                Check(value.RequestId == request.RequestId && value.Channel == request.Channel && value.Text == request.Text,
                    "Service preserves channel, text and request identity");
                if (result == null) result = value;
                else Check(result.MessageId == value.MessageId, "Recipients share one message identity");
            }
        }
        return result;
    }
    private static ChatRejection Denied(Fixture f, Client from, object payload, string expected)
    {
        ClearChat(f); from.Connection.Send(ChatProtocol.SendKind, payload); Drain(f);
        Check(from.Connection.Connected, "Chat rejection is nonfatal");
        var rejections = from.Received.Where(m => m.Type == ChatProtocol.RejectedKind).ToArray();
        Check(rejections.Length == 1, "Rejected chat has one sender-only rejection");
        var rejection = rejections[0].Read<ChatRejection>(); ChatProtocol.ValidateRejection(rejection);
        Check(rejection.Code == expected, "Expected rejection: " + expected + ", got " + rejection.Code);
        foreach (var c in f.Clients)
        {
            Check(!c.Received.Any(m => m.Type == ChatProtocol.MessageKind), "Rejected chat leaks to nobody");
            Check(c == from || !c.Received.Any(m => m.Type == ChatProtocol.RejectedKind), "Rejection is private to sender");
        }
        return rejection;
    }
    private static void Scene(Fixture f, Client worker, Client player, string id, string scene, long generation, string property = "", bool pending = false)
    {
        worker.Connection.Send("worldEvent", new WorldEvent { Kind = PlayerSceneContext.EventKind, TargetPlayerId = id,
            Data = JsonSerializer.SerializeToElement(new PlayerSceneContext { PlayerId = id, Scene = scene, PropertyId = property,
                Generation = generation, TravelPending = pending, Epoch = pending ? Guid.NewGuid().ToString("N") : "" }, DedicatedProtocol.Json) });
        var delivered = f.Wait(player, "worldEvent").Read<WorldEvent>();
        Check(delivered.Kind == PlayerSceneContext.EventKind, "Real worker scene assignment reaches its player");
        player.Connection.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { delivered.DeliveryId } });
        Drain(f);
    }
    private static void Run(string root)
    {
        using var f = new Fixture(root);
        var worker = f.Worker(out var world);
        var ledger = new AccountWorldState { WorldId = world.WorldId, Revision = 1, LegacyEscrow = new AccountInventoryState() };
        f.Checkpoint(worker, ledger, new byte[] { 1, 2, 3 }); f.Ready(worker, ledger);
        var a = f.Connect(); var aa = f.Authenticate(a, "register", "chat_alpha", "test-password-alpha");
        var b = f.Connect(); var ba = f.Authenticate(b, "register", "chat_beta", "test-password-bravo");
        var c = f.Connect(); var ca = f.Authenticate(c, "register", "chat_gamma", "test-password-charlie");
        var d = f.Connect(); var da = f.Authenticate(d, "register", "chat_delta", "test-password-delta");
        var accountOnly = f.Connect(); f.Authenticate(accountOnly, "register", "chat_select", "test-password-select");
        var unauthenticated = f.Connect();
        Denied(f, accountOnly, Request(), "not_ready");
        Denied(f, worker, Request(), "not_ready");
        f.Join(a, "native-model-a", out var aj); f.Join(b, "native-model-b", out var bj);
        f.Join(c, "native-model-a", out var cj); f.Join(d, "native-model-b", out var dj);
        ledger.Revision = 2;
        ledger.Accounts = new[] { aa, ba, ca, da }.Select((x, i) => new AccountState { AccountId = x.AccountId, StateRevision = 1,
            Initialized = true, Money = 100000, AppearanceId = i % 2 == 0 ? "native-model-a" : "native-model-b" }).ToArray();
        string checkpoint = worker.Received.Last(m => m.Type == "checkpointRequested").Read<CheckpointRequest>().RequestId;
        f.Checkpoint(worker, ledger, new byte[] { 4, 5, 6 }, checkpoint);
        f.Load(a, world.WorldId, 2); f.Load(b, world.WorldId, 2); f.Load(c, world.WorldId, 2);
        Denied(f, d, Request("global"), "not_ready");
        Denied(f, a, Request(), "map_not_ready");
        Scene(f, worker, a, aj.PlayerId, "Meridian", 1);
        Scene(f, worker, b, bj.PlayerId, "Meridian", 1, "apartment-property");
        Scene(f, worker, c, cj.PlayerId, "Lowtown", 1);

        var local = Request(text: "<b>literal local</b> 😀");
        var first = SendChat(f, a, aj.PlayerId, local, a, b);
        Check(first.SenderPlayerId == aj.PlayerId && first.SenderName == aa.Username, "Authenticated account supplies sender identity");
        Check(first.TargetName == "" && first.TargetPlayerId == "", "Local chat has no private recipient metadata");
        var global = SendChat(f, a, aj.PlayerId, Request("global"), a, b, c, d);
        Check(global.Channel == "global", "Global reaches loaded players and joined loading players across maps");
        var whisper = SendChat(f, a, aj.PlayerId, Request("whisper", "CHAT_GAMMA"), a, c);
        Check(whisper.TargetPlayerId == cj.PlayerId && whisper.TargetName == "chat_gamma", "Whisper resolves case-insensitive account name");
        SendChat(f, a, aj.PlayerId, Request("whisper", "chat_delta"), a, d);
        SendChat(f, a, aj.PlayerId, Request("whisper", "chat_alpha"), a);
        Refill(f, aj.PlayerId); Denied(f, a, Request("whisper", "chat_select"), "player_not_found");
        Refill(f, aj.PlayerId); var absent = Request("whisper", "unknown_account");
        Denied(f, a, absent, "player_not_found"); Denied(f, a, absent, "player_not_found");
        var changed = Request("global"); changed.RequestId = absent.RequestId;
        Denied(f, a, changed, "duplicate_request");

        // Arrival changes come only from the real authority transport. A local
        // recipient in transition receives neither the old nor destination map.
        Scene(f, worker, b, bj.PlayerId, "Meridian", 2, pending: true);
        SendChat(f, a, aj.PlayerId, Request(), a);
        Refill(f, bj.PlayerId); Denied(f, b, Request(), "map_not_ready");
        SendChat(f, b, bj.PlayerId, Request("global"), a, b, c, d);
        Scene(f, worker, b, bj.PlayerId, "Lowtown", 3);
        SendChat(f, a, aj.PlayerId, Request(), a);
        SendChat(f, b, bj.PlayerId, Request(), b, c);
        // Client-supplied pose/name/extra JSON fields must not stamp chat identity
        // or grant membership. The authoritative context still keeps A in Meridian.
        a.Connection.Send("playerPose", new PlayerPose { PlayerId = cj.PlayerId, Name = "chat_gamma", Scene = "Lowtown",
            Active = true, ContextGeneration = 1, PropertyId = "" }); Drain(f);
        ClearChat(f); Refill(f, aj.PlayerId);
        var spoofId = Guid.NewGuid().ToString("N");
        a.Connection.Send(ChatProtocol.SendKind, new { RequestId = spoofId, Channel = "local", TargetName = "", Text = "spoof attempt",
            SenderPlayerId = cj.PlayerId, SenderName = "chat_gamma", Scene = "Lowtown" }); Drain(f);
        var stamped = a.Received.Single(m => m.Type == ChatProtocol.MessageKind).Read<ChatMessage>();
        Check(stamped.SenderName == "chat_alpha" && stamped.SenderPlayerId == aj.PlayerId, "Forged sender and pose fields cannot impersonate an account");
        Check(!b.Received.Any(m => m.Type == ChatProtocol.MessageKind) && !c.Received.Any(m => m.Type == ChatProtocol.MessageKind), "Forged map and pose cannot leak local chat across maps");

        // Duplicate successful requests replay only the sender's same receipt,
        // with the original ID. Peers do not receive the message a second time.
        var dedupe = Request("global", text: "dedupe"); var original = SendChat(f, a, aj.PlayerId, dedupe, a, b, c, d);
        var replay = SendChat(f, a, aj.PlayerId, dedupe, a);
        Check(replay.MessageId == original.MessageId, "Duplicate request preserves original message identity");
        var mismatched = Request("global", text: "altered"); mismatched.RequestId = dedupe.RequestId;
        Denied(f, a, mismatched, "duplicate_request");
        foreach (object malformed in new object[] { null, "not-an-object", new[] { "array" }, new { RequestId = 19, Channel = "local", Text = "wrong type" } })
        { Refill(f, aj.PlayerId); Denied(f, a, malformed, "malformed"); }
        var invalid = Request(); invalid.Text = "bad\nline";
        Refill(f, aj.PlayerId); Denied(f, a, invalid, "malformed"); Denied(f, a, invalid, "malformed");
        invalid.Text = "changed"; Denied(f, a, invalid, "duplicate_request");
        Refill(f, aj.PlayerId); Denied(f, a, Request("other"), "malformed");
        Refill(f, aj.PlayerId); Denied(f, a, Request(text: new string('x', 513)), "malformed");

        TestRateAndBound(f, a, aj.PlayerId);
        var beforeDisconnect = Request("global", text: "same request across a new connection");
        SendChat(f, a, aj.PlayerId, beforeDisconnect, a, b, c, d);
        a.Dispose(); Drain(f);
        Check(!((IEnumerable)typeof(DedicatedService).GetField("_peers", Hidden).GetValue(f.Service)).Cast<object>()
            .Any(x => (string)x.GetType().GetField("Id").GetValue(x) == aj.PlayerId), "Disconnected peer is removed so weak chat state can be collected");
        var reconnect = f.Connect(); f.Authenticate(reconnect, "login", "CHAT_ALPHA", "test-password-alpha");
        f.Join(reconnect, "native-model-a", out var resumed);
        ledger.Revision = 3;
        checkpoint = worker.Received.Last(m => m.Type == "checkpointRequested").Read<CheckpointRequest>().RequestId;
        f.Checkpoint(worker, ledger, new byte[] { 7, 8, 9 }, checkpoint); f.Load(reconnect, world.WorldId, 3);
        var afterReconnect = SendChat(f, reconnect, resumed.PlayerId, beforeDisconnect, reconnect, b, c, d);
        Check(afterReconnect.SenderPlayerId == resumed.PlayerId && resumed.PlayerId != aj.PlayerId, "Reconnect uses fresh peer identity and does not replay old chat dedupe state");
        Refill(f, bj.PlayerId); Denied(f, b, Request("whisper", "offline_player"), "player_not_found");
        Check(!worker.Received.Any(m => m.Type == ChatProtocol.MessageKind || (m.Type == "worldEvent" && m.Read<WorldEvent>().Kind.StartsWith("chat"))), "Chat never enters the worker/world-event pipeline");
        Check(ledger.Accounts.All(x => x.Money == 100000), "Chat does not mutate account assets");
    }

    private static void TestRateAndBound(Fixture f, Client a, string id)
    {
        var method = typeof(DedicatedService).GetMethod("TryHandleChat", Hidden);
        var peer = PeerFor(f, id); Refill(f, id);
        DateTime now = DateTime.UtcNow;
        void Call(ChatRequest request, DateTime time)
        {
            var wire = new WireMessage { Type = ChatProtocol.SendKind, Payload = JsonSerializer.SerializeToElement(request, DedicatedProtocol.Json) };
            Check((bool)method.Invoke(f.Service, new[] { peer, wire, (object)time }), "Production handler consumes chat kind");
            Drain(f);
        }
        ClearChat(f);
        for (int i = 0; i < 5; i++) Call(Request("whisper", "chat_alpha", "burst " + i), now);
        Check(a.Received.Count(m => m.Type == ChatProtocol.MessageKind) == 5, "Initial bucket allows five messages");
        Call(Request(), now);
        Check(a.Received.Last(m => m.Type == ChatProtocol.RejectedKind).Read<ChatRejection>().Code == "rate_limit" && a.Connection.Connected, "Sixth message is rejected without disconnect");
        ClearChat(f); Call(Request(), now.AddSeconds(1.99));
        Check(!a.Received.Any(m => m.Type == ChatProtocol.MessageKind), "Partial token does not send");
        ClearChat(f); Call(Request(), now.AddSeconds(2));
        Check(a.Received.Count(m => m.Type == ChatProtocol.MessageKind) == 1, "One token refills after two seconds");
        ClearChat(f); Call(Request(), now.AddSeconds(-30));
        Check(a.Received.Any(m => m.Type == ChatProtocol.RejectedKind), "Backwards clock cannot mint tokens");
        for (int i = 0; i < 40; i++) Call(Request("whisper", "chat_alpha"), now.AddSeconds(4 + i * 2));
        var state = StateFor(f, id);
        int recent = (int)state.GetType().GetField("Recent").GetValue(state).GetType().GetProperty("Count").GetValue(state.GetType().GetField("Recent").GetValue(state));
        int order = (int)state.GetType().GetField("Order").GetValue(state).GetType().GetProperty("Count").GetValue(state.GetType().GetField("Order").GetValue(state));
        Check(recent == 32 && order == 32, "Per-connection result cache is bounded to exactly the most recent 32 requests");
        var unrelated = new WireMessage { Type = "ping" };
        Check(!(bool)method.Invoke(f.Service, new[] { peer, unrelated, (object)now }), "Chat handler leaves unrelated protocol traffic untouched");
    }
    public static int Main(string[] args)
    {
        try { int parserChecks = ChatProtocolTests.Run(); Run(args[0]); Console.WriteLine("PASS chat protocol " + parserChecks + " assertions; actual service " + checks + " assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
