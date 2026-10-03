using System;

namespace NivalisTogether.Dedicated
{
    /// <summary>One cooperative budget for atomic client world mutations in a
    /// rendered frame. An atomic operation may exceed the deadline; later work
    /// yields. Socket parsing, pings and presentation do not require a grant.</summary>
    public sealed class ClientMutationBudget
    {
        private readonly long duration;
        private readonly int maximumActions;
        private int frame;
        private bool hasFrame, started;
        private long firstStarted;
        public int Actions { get; private set; }
        public ClientMutationBudget(long timestampFrequency, double milliseconds = 4, int maximumActions = 32)
        {
            if (timestampFrequency < 1 || !double.IsFinite(milliseconds) || milliseconds <= 0 || maximumActions < 1)
                throw new ArgumentException("Invalid client mutation budget.");
            double ticks = timestampFrequency * (milliseconds / 1000d);
            if (ticks > long.MaxValue) throw new ArgumentException("Client mutation budget is too large.");
            duration = Math.Max(1, (long)Math.Ceiling(ticks)); this.maximumActions = maximumActions;
        }
        public void BeginFrame(int frameNumber)
        {
            if (hasFrame && frame == frameNumber) return;
            frame = frameNumber; hasFrame = true; started = false; Actions = 0; firstStarted = 0;
        }
        public bool CanStart(long timestamp) => hasFrame && timestamp >= 0 && Actions < maximumActions &&
            (!started || (timestamp >= firstStarted && timestamp - firstStarted < duration));
        public bool TryStart(long timestamp)
        {
            if (!CanStart(timestamp)) return false;
            if (!started) { started = true; firstStarted = timestamp; }
            Actions++; return true;
        }
        public void Reset() { hasFrame = started = false; Actions = 0; firstStarted = 0; }
    }
}
