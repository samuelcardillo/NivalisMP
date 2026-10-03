using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;

internal static class WorldFlowTests
{
    private static int assertions;
    private static void Check(bool condition, string reason)
    { assertions++; if (!condition) throw new Exception(reason); }
    private static WorldEvent Event(string kind, int number, int padding = 0) => new WorldEvent {
        Kind = kind, Data = JsonSerializer.SerializeToElement(new { Number = number, Padding = new string('a', padding) }, DedicatedProtocol.Json) };
    private static WorldEvent Travel(string phase) => new WorldEvent { Kind = "group-travel", Data = JsonSerializer.SerializeToElement(new { Phase = phase }) };
    private static WorldEvent Sleep(string phase) => new WorldEvent { Kind = "group-sleep", Data = JsonSerializer.SerializeToElement(new { Phase = phase }) };
    private static WorldEventScheduler.Entry Take(WorldEventScheduler queue, int bulk = WorldEventFlow.BulkWindowBytes, int control = WorldEventFlow.ControlWindowBytes)
    { Check(queue.TryPeek(bulk, control, out var entry), "Expected schedulable event."); queue.Remove(entry); return entry; }
    private static void Reject(Action action, string reason)
    { bool rejected = false; try { action(); } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { rejected = true; } Check(rejected, reason); }
    public static int Main()
    {
        try
        {
            SerializationCostInvariant(); FairnessAndWholeBatches(); CreditsAndLargeBurst(); OrderingFences(); SleepFences(); BoatFences(); Bounds();
            Console.WriteLine("PASS WorldFlowTests: " + assertions + " assertions, 7 scenarios."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void SerializationCostInvariant()
    {
        // byte[] uses the JSON base64 writer, which may write literal '+' while
        // nesting the resulting JsonElement escapes it as six-byte \u002B.
        // Credit must account for the exact normalized data received by the peer.
        var bytes = new byte[3000];
        for (int i = 0; i < bytes.Length; i += 3) { bytes[i] = 251; bytes[i + 1] = 239; bytes[i + 2] = 190; }
        var original = new WorldEvent { Kind = "shared-economy-chunk", Data = JsonSerializer.SerializeToElement(new { Data = bytes }) };
        var envelope = new WireMessage { Type = "worldEvent", Payload = JsonSerializer.SerializeToElement(original, DedicatedProtocol.Json) };
        var received = JsonSerializer.Deserialize<WireMessage>(JsonSerializer.SerializeToUtf8Bytes(envelope, DedicatedProtocol.Json), DedicatedProtocol.Json).Read<WorldEvent>();
        Console.WriteLine("Serialization cost probe: sourceRaw=" + original.Data.GetRawText().Length + " receivedRaw=" + received.Data.GetRawText().Length +
            " workerCost=" + WorldEventFlow.Cost(original) + " serviceCost=" + WorldEventFlow.Cost(received));
        Check(original.Data.GetRawText().Length != received.Data.GetRawText().Length, "Regression input must exercise base64 normalization.");
        Check(WorldEventFlow.Cost(original) == WorldEventFlow.Cost(received), "Sender and receiver charged different credit for the same binary chunk.");
        var queue = new WorldEventScheduler(); queue.Enqueue(original);
        Check(queue.TryPeek(WorldEventFlow.Cost(received), 0, out var entry) && entry.Cost == WorldEventFlow.Cost(received), "Scheduler must reserve the complete on-wire event cost.");
        Check(!queue.TryPeek(WorldEventFlow.Cost(received) - 1, 0, out _), "Underfunded normalized event must remain queued.");
    }
    private static void FairnessAndWholeBatches()
    {
        var queue = new WorldEventScheduler();
        for (int i = 0; i < 100; i++) queue.Enqueue(Event("shared-economy-chunk", i));
        queue.Enqueue(Event("npcs", 0)); queue.Enqueue(Event("crowds", 0)); queue.Enqueue(Event("business-state-chunk", 0));
        for (int i = 100; i < 200; i++) queue.Enqueue(Event("shared-economy-chunk", i));
        queue.Enqueue(Event("interaction-result", 0));
        Check(Take(queue, 0).Event.Kind == "interaction-result", "Control must be deliverable with no bulk credit.");
        Check(Take(queue).Event.Kind == "shared-economy-chunk", "First bulk family must start.");
        Check(Take(queue).Event.Kind == "npcs", "NPC cannot wait behind the full economy batch.");
        Check(Take(queue).Event.Kind == "crowds", "Crowd must receive round-robin service.");
        Check(Take(queue).Event.Kind == "business-state-chunk", "Business must receive round-robin service.");
        for (int i = 1; i < 200; i++) Check(Take(queue).Event.Data.GetProperty("Number").GetInt32() == i, "Overlapping producers must preserve all economy chunks in FIFO order.");
        Check(queue.Count == 0 && queue.PayloadBytes == 0, "Queue accounting must drain exactly.");
    }
    private static void CreditsAndLargeBurst()
    {
        var queue = new WorldEventScheduler();
        for (int i = 0; i < 96; i++) queue.Enqueue(Event(i % 2 == 0 ? "npcs" : "shared-economy-chunk", i, 30000));
        Check(queue.PayloadBytes > DedicatedProtocol.MaxQueueBytes, "Burst must exceed TCP cap.");
        int available = WorldEventFlow.BulkWindowBytes, inFlight = 0, completed = 0, lastNpc = -2, lastEconomy = -1;
        var pending = new Queue<WorldEventScheduler.Entry>();
        while (queue.Count > 0 || pending.Count > 0)
        {
            while (queue.TryPeek(available, 0, out var entry))
            {
                queue.Remove(entry); available -= entry.Cost; inFlight += entry.Cost; pending.Enqueue(entry);
                Check(inFlight <= WorldEventFlow.BulkWindowBytes, "Outstanding delivery never exceeds credit.");
            }
            Check(pending.Count > 0, "A legal frame must fit the bounded window.");
            var applied = pending.Dequeue(); int number = applied.Event.Data.GetProperty("Number").GetInt32();
            if (applied.Event.Kind == "npcs") { Check(number == lastNpc + 2, "NPC chunk order changed."); lastNpc = number; }
            else { Check(number == lastEconomy + 2, "Economy chunk order changed."); lastEconomy = number; }
            available += applied.Cost; inFlight -= applied.Cost; completed++;
        }
        Check(completed == 96 && available == WorldEventFlow.BulkWindowBytes, "Every burst event must be delivered exactly once.");
    }
    private static void OrderingFences()
    {
        var queue = new WorldEventScheduler();
        queue.Enqueue(Event("shared-economy-chunk", 1)); queue.Enqueue(Event("shared-economy-chunk", 2));
        queue.Enqueue(Travel("prepare")); queue.Enqueue(Event("npcs", 3)); queue.Enqueue(Event("timeWeather", 4));
        Check(!queue.TryPeek(0, WorldEventFlow.ControlWindowBytes, out _), "Travel/control cannot overtake prior bulk with no credit.");
        Check(Take(queue).Event.Data.GetProperty("Number").GetInt32() == 1, "Fence changed pre-travel batch order.");
        Check(Take(queue).Event.Data.GetProperty("Number").GetInt32() == 2, "Fence split the pre-travel batch.");
        Check(Take(queue).Event.Data.GetProperty("Phase").GetString() == "prepare", "Prepare must precede every later event.");
        Check(Take(queue).Event.Kind == "timeWeather", "After fence, ordinary control priority resumes.");
        queue.Enqueue(Travel("complete")); queue.Enqueue(Event("business-state-chunk", 5));
        Check(Take(queue).Event.Kind == "npcs", "Complete cannot overtake an earlier world mutation.");
        Check(Take(queue).Event.Data.GetProperty("Phase").GetString() == "complete", "Post-travel capture must wait for completion fence.");
        Check(Take(queue).Event.Kind == "business-state-chunk", "Post-travel capture missing.");
    }
    private static void Bounds()
    {
        Check(WorldEventFlow.Cost(Event("npcs", 0)) == 2048, "Tiny events must reserve frame capacity.");
        Check((WorldEventFlow.BulkWindowBytes + WorldEventFlow.ControlWindowBytes) / 2048 < DedicatedProtocol.MaxQueueFrames, "Credit windows must leave TCP frame headroom.");
        Reject(() => WorldEventFlow.Cost(Event("npcs", 0, 48000)), "Oversized payload must fail before queueing.");
        var queue = new WorldEventScheduler();
        for (int i = 0; i < WorldEventScheduler.MaxEvents; i++) queue.Enqueue(Event("npcs", i));
        Reject(() => queue.Enqueue(Event("npcs", 4097)), "Frame bound missing.");
        queue.Clear();
        for (int i = 0; i < WorldEventScheduler.MaxKinds; i++) queue.Enqueue(Event("kind" + i, i));
        Reject(() => queue.Enqueue(Event("extraKind", 0)), "Kind bound missing.");
        queue.Clear();
        int count = 0;
        try { while (true) { queue.Enqueue(Event("npcs", count++, 47000)); } }
        catch (IOException) { }
        Check(queue.PayloadBytes <= WorldEventScheduler.MaxBytes && count < WorldEventScheduler.MaxEvents, "Byte bound missing.");
        queue.Clear(); Check(queue.Count == 0 && queue.PayloadBytes == 0 && !queue.Contains("npcs"), "Reset retains queue state.");
        queue.Enqueue(Event("new-kind", 1)); Check(Take(queue).Event.Kind == "new-kind", "Queue cannot be reused after reset.");
    }
    private static void SleepFences()
    {
        foreach(string phase in new[]{"summary","debt","ranking","newspaper","supply","sleeping","transition","complete"})
        {
            var queue=new WorldEventScheduler();queue.Enqueue(Event("shared-economy-chunk",1));queue.Enqueue(Sleep(phase));queue.Enqueue(Event("timeWeather",2));queue.Enqueue(Event("business-state-chunk",3));
            Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"Sleep phase cannot overtake pre-sleep native mutations.");
            Check(Take(queue).Event.Kind=="shared-economy-chunk","Sleep fence preserves previous economic state.");
            Check(Take(queue,0).Event.Data.GetProperty("Phase").GetString()==phase,"Sleep phase uses reserved control credit after preceding state drains.");
            Check(Take(queue,0).Event.Kind=="timeWeather","Clock updates remain deliverable during sleep.");
            Check(Take(queue).Event.Kind=="business-state-chunk","State after sleep phase follows its fence.");
        }
        foreach(string phase in new[]{"accepted","denied"})
        {
            var queue=new WorldEventScheduler();queue.Enqueue(Event("shared-economy-chunk",1));queue.Enqueue(Sleep(phase));
            Check(Take(queue,0).Event.Kind=="group-sleep","Sleep request result is a lightweight control, not a world transition fence.");
        }
        Check(!WorldEventFlow.IsOrderingBarrier(Event("group-sleep",0)),"Missing sleep phase is not a transition fence.");
        Reject(()=>WorldEventFlow.Cost(Event("group-sleep",0,24000)),"Sleep payload above24KB rejected on both worker/service credit paths.");
    }
    private static void BoatFences()
    {
        foreach(string operation in new[]{"recall","dock","refuel","board","disembark"})
        {
            var result=new WorldEvent{Kind=BoatProtocol.EventKind,Data=JsonSerializer.SerializeToElement(new BoatWorldState{Operation=operation,Success=true})};
            Check(WorldEventFlow.IsOrderingBarrier(result),"boat operation must preserve paired account update ordering: "+operation);
            var queue=new WorldEventScheduler();queue.Enqueue(Event("shared-economy-chunk",1));
            queue.Enqueue(Event("account-state-chunk",2));queue.Enqueue(result);queue.Enqueue(Event("timeWeather",3));
            Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"boat result and later clock cannot overtake preceding wallet update without bulk credit");
            Check(Take(queue).Event.Kind=="shared-economy-chunk","pre-transaction economy drains before private account update");
            Check(Take(queue).Event.Kind=="account-state-chunk","private account update precedes boat operation result");
            Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"later control remains behind unsent boat operation result");
            Check(Take(queue).Event.Data.GetProperty("Operation").GetString()==operation,"boat operation result follows its durable wallet update");
            Check(Take(queue,0).Event.Kind=="timeWeather","control priority resumes after boat operation fence");
            var failure=new WorldEvent{Kind=BoatProtocol.EventKind,Data=JsonSerializer.SerializeToElement(new BoatWorldState{Operation=operation,Success=false})};
            Check(WorldEventFlow.IsOrderingBarrier(failure),"boat denial remains ordered with surrounding state without implying a durable mutation");
        }
        foreach(bool success in new[]{true,false})
        {
            var snapshot=new WorldEvent{Kind=BoatProtocol.EventKind,Data=JsonSerializer.SerializeToElement(new BoatWorldState{Operation="snapshot",Success=success})};
            Check(!WorldEventFlow.IsOrderingBarrier(snapshot),"periodic boat snapshot must not fence unrelated scene delivery");
            var queue=new WorldEventScheduler();queue.Enqueue(Event("npcs",1));queue.Enqueue(snapshot);queue.Enqueue(Event("timeWeather",2));
            Check(Take(queue,0).Event.Kind=="timeWeather","clock control can pass ordinary boat snapshot traffic");
        }
        Check(!WorldEventFlow.IsOrderingBarrier(new WorldEvent{Kind=BoatProtocol.EventKind,Data=JsonSerializer.SerializeToElement(new{Success=true})}),
            "missing boat operation cannot create an ordering barrier");
    }
}
