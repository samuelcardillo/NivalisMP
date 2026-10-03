using System;
using NivalisTogether.Dedicated;

public static class WorkerBoatResidencyTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
    private static PlayerMapLocation Location() => new PlayerMapLocation { HasPosition = true };
    public static int Run()
    {
        WorkerSceneResidency.ResetTest();
        BoatReplication.Attached = true;
        Check(WorkerSceneResidency.Ensure(Location(), 1), "matching position remains immediately usable");
        Check(!BoatReplication.Attached && BoatReplication.DetachCalls == 1, "matching-position fast path clears stale boat attachment first");
        Check(WorkerSceneResidency.BeginCalls == 0, "detaching a matching context does not trigger travel");
        Check(WorkerSceneResidency.Logs.Count == 1, "successful stale detachment is diagnosed");

        WorkerSceneResidency.ResetTest();
        WorkerSceneResidency.Completed = Location(); BoatReplication.Attached = true;
        Check(WorkerSceneResidency.Ensure(Location(), 1), "completed operation acknowledgement remains usable");
        Check(!BoatReplication.Attached && BoatReplication.DetachCalls == 1, "completed-context fast path cannot retain a boat constraint");
        Check(WorkerSceneResidency.Completed == null, "completed context remains consumed once");

        WorkerSceneResidency.ResetTest();
        WorkerSceneResidency.MatchPosition = false; BoatReplication.Attached = true;
        Check(!WorkerSceneResidency.Ensure(Location(), 1), "different position starts bounded native placement");
        Check(WorkerSceneResidency.IdleCalls == 1 && WorkerSceneResidency.BeginCalls == 1, "boat detachment occurs before native idle preflight and placement");

        WorkerSceneResidency.ResetTest(); WorkerSceneResidency.Pending = true; BoatReplication.Attached = true;
        Check(!WorkerSceneResidency.Ensure(Location(), 1), "ongoing operation remains pending");
        Check(BoatReplication.Attached && BoatReplication.DetachCalls == 0, "pending operation cannot be mutated by another queued actor");

        WorkerSceneResidency.ResetTest(); WorkerSceneResidency.IsWorker = false;
        Reject(() => WorkerSceneResidency.Ensure(Location(), 1), "client cannot invoke worker focus");
        Check(BoatReplication.DetachCalls == 0, "authority check precedes native detachment");

        WorkerSceneResidency.ResetTest();
        Reject(() => WorkerSceneResidency.Ensure(null, 1), "invalid location rejects");
        Check(BoatReplication.DetachCalls == 0, "validation precedes native detachment");

        WorkerSceneResidency.ResetTest(); BoatReplication.ThrowOnDetach = true;
        Reject(() => WorkerSceneResidency.Ensure(Location(), 1), "detachment failure cannot claim a ready context");
        Check(WorkerSceneResidency.BeginCalls == 0, "failed detachment cannot start a native map mutation");

        WorkerSceneResidency.ResetTest(); WorkerSceneResidency.IsWorker = false;
        WorkerSceneResidency.BeginClientRestore(Location(), Guid.NewGuid().ToString("N"), 1);
        Check(BoatReplication.DetachCalls == 0 && WorkerSceneResidency.BeginCalls == 1, "authenticated client restore never calls worker detachment");
        Check(WorkerSceneResidency.LastBeginClient, "client restoration retains its native client path");

        WorkerSceneResidency.ResetTest();
        Check(WorkerSceneResidency.Ensure(Location(), 1), "ordinary unbound worker context remains ready");
        Check(WorkerSceneResidency.Logs.Count == 0, "ordinary context does not produce false attachment diagnostics");
        return checks;
    }
}

namespace NivalisTogether.Dedicated
{
    public sealed class PlayerMapLocation
    {
        public bool HasPosition;
    }
    public static class BoatReplication
    {
        public static bool Attached, ThrowOnDetach;
        public static int DetachCalls;
        public static bool DetachWorkerBoatAttachment()
        {
            DetachCalls++;
            if (ThrowOnDetach) throw new InvalidOperationException("Native detachment rejected.");
            bool result = Attached; Attached = false; return result;
        }
    }
}
