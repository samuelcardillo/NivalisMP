using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NivalisTogether.Dedicated;

internal static class AccountStateChunksTests
{
    private static int checks;
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static void Reject(Action action, string message) { checks++; try { action(); } catch { return; } throw new Exception(message); }
    private static WorldEvent Clone(WorldEvent value) => JsonSerializer.Deserialize<WorldEvent>(JsonSerializer.Serialize(value, DedicatedProtocol.Json), DedicatedProtocol.Json);
    public static int Main()
    {
        string account = Guid.NewGuid().ToString("N"), player = Guid.NewGuid().ToString("N");
        var state = new AccountState { AccountId = account, StateRevision = 19, Money = 4123, Initialized = true,
            Inventory = JsonSerializer.SerializeToElement(Enumerable.Range(0, 1300).Select(i => new { Type = Guid.NewGuid().ToString("N"), LastUpdate = i, Items = new[] { new[] { i, 5, 900, 7 } } })) };
        var chunks = AccountStateChunks.Capture(state, player).ToArray();
        Check(chunks.Length > 2, "Fixture spans multiple chunks");
        var receiver = new AccountStateChunks(); AccountState result = null;
        foreach (var chunk in chunks)
        {
            Check(chunk.TargetPlayerId == player, "Every chunk is private");
            Check(WorldEventFlow.Cost(chunk) < WorldEventFlow.BulkWindowBytes, "Chunk respects flow bound");
            result = receiver.Accept(chunk, player, account);
        }
        Check(result != null && result.AccountId == account && result.Money == 4123 && result.StateRevision == 19 &&
            result.Inventory.GetRawText() == state.Inventory.GetRawText(), "Exact inventory instances and money survive transfer");
        Reject(() => receiver.Accept(chunks[0], Guid.NewGuid().ToString("N"), account), "Reject another connection's state");
        Reject(() => receiver.Accept(chunks[0], player, Guid.NewGuid().ToString("N")), "Reject another account's state");
        Reject(() => receiver.Accept(chunks[1], player, account), "Reject missing opening chunk");
        receiver.Reset(); receiver.Accept(chunks[0], player, account);
        Reject(() => receiver.Accept(chunks[0], player, account), "Reject duplicate opening chunk");
        receiver.Reset(); receiver.Accept(chunks[0], player, account);
        var foreign = AccountStateChunks.Capture(state, player).ToArray();
        Reject(() => receiver.Accept(foreign[1], player, account), "Reject interleaved transfer");
        receiver.Reset();
        var tampered = Clone(chunks[0]); var part = tampered.Data.Deserialize<AccountStateChunk>(DedicatedProtocol.Json);
        part.Bytes[0] ^= 1; tampered.Data = JsonSerializer.SerializeToElement(part, DedicatedProtocol.Json);
        Reject(() => { receiver.Accept(tampered, player, account); foreach (var chunk in chunks.Skip(1)) receiver.Accept(chunk, player, account); }, "Reject altered content by checksum");
        receiver.Reset();
        part = chunks[0].Data.Deserialize<AccountStateChunk>(DedicatedProtocol.Json); part.Length = int.MaxValue;
        tampered.Data = JsonSerializer.SerializeToElement(part, DedicatedProtocol.Json);
        Reject(() => receiver.Accept(tampered, player, account), "Reject oversized allocation");
        var queue = new WorldEventScheduler();
        foreach (var chunk in chunks) queue.Enqueue(chunk);
        queue.Enqueue(new WorldEvent { Kind = "interaction-result", Data = JsonSerializer.SerializeToElement(new { Success = true }) });
        for (int i = 0; i < chunks.Length; i++)
        {
            Check(queue.TryPeek(WorldEventFlow.BulkWindowBytes, WorldEventFlow.ControlWindowBytes, out var next) && next.Event.Kind == AccountStateChunks.EventKind,
                "Control success cannot overtake private account update"); queue.Remove(next);
        }
        Check(queue.TryPeek(WorldEventFlow.BulkWindowBytes, WorldEventFlow.ControlWindowBytes, out var final) && final.Event.Kind == "interaction-result", "Result follows committed account state");
        Console.WriteLine("Account chunk/privacy/ordering checks passed: " + checks); return 0;
    }
}
