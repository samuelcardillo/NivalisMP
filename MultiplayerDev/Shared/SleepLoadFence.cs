using System;
using System.IO;

namespace NivalisTogether.Dedicated
{
    // Keep new native imports outside an in-progress night. Checkpoints may
    // still be committed for the worker's wake synchronization, but a waiting
    // player receives only a capture begun after the final complete phase.
    public sealed class SleepLoadFence
    {
        private string epoch = "";
        public bool TransitionActive { get; private set; }
        public bool AwaitingFreshCheckpoint { get; private set; }
        public long Generation { get; private set; }
        public long MinimumRevision { get; private set; }
        public void Reset()
        { epoch = ""; TransitionActive = AwaitingFreshCheckpoint = false; Generation = MinimumRevision = 0; }
        public bool Observe(GroupSleepEvent value, long committedRevision)
        {
            SleepRecoveryProtocol.ValidateWire(value);
            if (committedRevision < 1) throw new InvalidDataException("Invalid sleep world revision.");
            if (SleepRecoveryProtocol.IsResponse(value)) return false;
            if (value.Phase == "waiting")
            {
                if (TransitionActive) throw new InvalidDataException("Sleep readiness cannot replace an active night.");
                return false;
            }
            bool final = value.Phase == "complete" || value.Phase == "cancelled" || value.Phase == "idle";
            if (final)
            {
                if (!TransitionActive) return false;
                if (value.Epoch != epoch) throw new InvalidDataException("Sleep completion does not match the active night.");
                Advance(committedRevision);
                TransitionActive = false;
                return false;
            }
            if (TransitionActive)
            {
                if (value.Epoch != epoch) throw new InvalidDataException("Overlapping shared night transitions.");
                return false;
            }
            Advance(committedRevision);
            epoch = value.Epoch;
            TransitionActive = true;
            return true;
        }
        private void Advance(long committedRevision)
        {
            if (Generation == long.MaxValue || committedRevision == long.MaxValue) throw new InvalidDataException("Sleep checkpoint generation overflow.");
            Generation++;
            MinimumRevision = Math.Max(MinimumRevision, committedRevision + 1);
            AwaitingFreshCheckpoint = true;
        }
        public long BeginSnapshot() => Generation;
        public void CommitSnapshot(long revision, long begunGeneration)
        {
            if (revision < 1 || begunGeneration < 0 || begunGeneration > Generation) throw new InvalidDataException("Invalid sleep checkpoint generation.");
            if (!TransitionActive && begunGeneration == Generation && revision >= MinimumRevision)
                AwaitingFreshCheckpoint = false;
        }
        public bool CanLoad(long revision) => !TransitionActive && !AwaitingFreshCheckpoint && revision > 0 && revision >= MinimumRevision;
    }
}
