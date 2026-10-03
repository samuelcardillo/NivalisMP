using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NivalisTogether.Dedicated;

internal static class WorldPreparationFenceTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static Task Work(BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent> fence) =>
        (Task)fence.GetType().GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fence);
    private static void Ready(BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent> fence) =>
        Check(Work(fence).Wait(TimeSpan.FromSeconds(3)), "Managed preparation completes without native work");
    private static WorldEvent Event(long id, string kind) => new WorldEvent { DeliveryId = id, Kind = kind };

    private static void MixedFamilyOrder()
    {
        var fence = new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>();
        var applied = new List<long>(); var acknowledgements = new List<long>();
        int owner = Environment.CurrentManagedThreadId, background = owner;
        using var release = new ManualResetEventSlim();
        var economyFinal = Event(2, "shared-economy-chunk");
        acknowledgements.Add(1); // An accepted intermediate chunk grants transport credit.
        fence.Start(_ => { background = Environment.CurrentManagedThreadId; release.Wait(); return new PreparedWorldUpdate(() => {
            Check(Environment.CurrentManagedThreadId == owner, "Native commit runs on the owning main thread"); applied.Add(2);
        }); }, economyFinal, 0);
        fence.Enqueue(Event(3, "business-state-chunk"), 100);
        fence.Enqueue(Event(4, "business-craft-chunk"), 100);
        fence.Enqueue(Event(5, "business-result"), 100);
        fence.Enqueue(Event(6, "group-travel"), 100);
        fence.Enqueue(Event(7, "sleep-wake-marker"), 100);
        Check(fence.IsPending && fence.IsFenced && !fence.TryDequeue(out _), "All later snapshot/result/transition controls wait behind one fence");
        Check(acknowledgements.Count == 1 && applied.Count == 0, "Final economy chunk is not acknowledged while preparing");
        release.Set(); Ready(fence);
        Check(background != owner, "Managed preparation runs away from main thread");
        Check(fence.TryCommit(1, p => p.Commit(), e => acknowledgements.Add(e.DeliveryId)), "First family commits and acknowledges once");
        Check(fence.IsFenced && !fence.IsPending, "A completed task with queued work still blocks map restore and sleep readiness");
        foreach (long id in new long[] { 3, 4 })
        {
            Check(fence.TryDequeue(out var evt) && evt.DeliveryId == id, "Different snapshot families keep delivery order");
            long sequence = id;
            fence.Start(_ => new PreparedWorldUpdate(() => { Check(Environment.CurrentManagedThreadId == owner, "Business/craft commit stays main-thread"); applied.Add(sequence); }), evt, 1);
            Check(!fence.TryDequeue(out _), "Every family uses the same pending barrier");
            Ready(fence); fence.TryCommit(2, p => p.Commit(), e => acknowledgements.Add(e.DeliveryId));
        }
        while (fence.TryDequeue(out var next)) { applied.Add(next.DeliveryId); acknowledgements.Add(next.DeliveryId); }
        Check(string.Join(",", applied) == "2,3,4,5,6,7", "All families/results/travel/wake apply in exact FIFO order");
        Check(string.Join(",", acknowledgements) == "1,2,3,4,5,6,7" && !fence.IsFenced, "Credit progresses without acknowledging future native state");
    }

    private static void MapReplay()
    {
        var fence = new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>();
        var applied = new List<long>(); var ack = new List<long>();
        // Dispatch popped 11, then flushing older map-retained work reaches a
        // completed craft snapshot 10. It must not run 11 before that commit.
        fence.Enqueue(Event(12, "group-apartment"), 30);
        fence.Start(_ => new PreparedWorldUpdate(() => applied.Add(10)), Event(10, "business-craft-chunk"), 0);
        fence.EnqueueFront(Event(11, "business-result"), 30);
        Check(!fence.TryDequeue(out _), "Older map replay's new family preparation blocks current replay");
        Ready(fence); fence.TryCommit(1, p => p.Commit(), e => ack.Add(e.DeliveryId));
        Check(fence.IsFenced, "Map attachment remains fenced after commit until replay drains");
        while (fence.TryDequeue(out var evt)) { applied.Add(evt.DeliveryId); ack.Add(evt.DeliveryId); }
        Check(string.Join(",", applied) == "10,11,12" && string.Join(",", ack) == "10,11,12", "Front replay retains exact order across family preparations");
    }

    private static void CancellationFailureAndBounds()
    {
        int commits = 0, ack = 0;
        var fence = new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>();
        using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        fence.Start(_ => { entered.Set(); release.Wait(); return new PreparedWorldUpdate(() => commits++); }, Event(1, "business-state-chunk"), 0);
        Check(entered.Wait(TimeSpan.FromSeconds(3)), "Cancelled preparation fixture entered its actual worker");
        var abandoned = Work(fence); fence.Cancel();
        var replacement = new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>(); bool rejected = false;
        try { replacement.Start(_ => new PreparedWorldUpdate(() => commits++), Event(2, "business-craft-chunk"), 0); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Replacement session/different family cannot start while cancelled delegate still owns shared lease");
        release.Set(); try { abandoned.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        Check(!fence.TryCommit(1, p => p.Commit(), _ => ack++) && commits == 0 && ack == 0, "Cancelled result never commits or grants stale credit");
        replacement.Start(_ => new PreparedWorldUpdate(() => { commits++; throw new InvalidOperationException("native failure"); }), Event(3, "business-craft-chunk"), 0);
        Ready(replacement); rejected = false;
        try { replacement.TryCommit(1, p => p.Commit(), _ => ack++); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && commits == 1 && ack == 0, "Native commit failure cannot acknowledge final chunk");
        rejected = false; try { replacement.TryCommit(1, p => p.Commit(), _ => ack++); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && commits == 1, "Consumed failing commit cannot repeat native side effects"); replacement.Cancel();
        replacement.Start(_ => new PreparedWorldUpdate(() => replacement.Cancel()), Event(4, "business-state-chunk"), 0);
        Ready(replacement); Check(!replacement.TryCommit(1, p => p.Commit(), _ => ack++) && ack == 0, "Native callback disconnect suppresses post-commit ACK");
        replacement.Start(_ => new PreparedWorldUpdate(() => commits++), Event(5, "business-craft-chunk"), 0);
        Ready(replacement); rejected = false; try { replacement.TryCommit(11, p => p.Commit(), _ => ack++); } catch (TimeoutException) { rejected = true; }
        Check(rejected && commits == 1 && ack == 0 && !replacement.IsFenced, "Preparation deadline discards state before native mutation");
        var countFence = new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>();
        for (int i = 0; i < 256; i++) countFence.Enqueue(Event(i, "business-result"), 1);
        rejected = false; try { countFence.Enqueue(Event(256, "group-travel"), 1); } catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected && countFence.Count == 256 && countFence.Bytes == 256, "All families share existing 256-event cap"); countFence.Cancel();
        var byteFence = new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>(); byteFence.Enqueue(Event(1, "business-state-chunk"), 2 * 1024 * 1024);
        rejected = false; try { byteFence.EnqueueFront(Event(2, "business-craft-chunk"), 1); } catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected && byteFence.Count == 1 && byteFence.Bytes == 2 * 1024 * 1024, "Front replay also respects shared 2MiB cap"); byteFence.Cancel();
    }
    public static int Main()
    {
        try { MixedFamilyOrder(); MapReplay(); CancellationFailureAndBounds(); Console.WriteLine("PASS WorldPreparationFenceTests: " + checks + " assertions; all snapshot families share ordering, credit, cancellation and bounds."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
