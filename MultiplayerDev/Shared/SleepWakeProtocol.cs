using System;
using System.Collections.Generic;
using System.IO;

namespace NivalisTogether.Dedicated
{
    public sealed class SleepWakeMarker
    {
        public string Epoch { get; set; } = "";
        public string Token { get; set; } = "";
        public long Revision { get; set; }
        public int GameSeconds { get; set; }
    }
    public static class SleepWakeProtocol
    {
        public const string EventKind = "sleep-wake", ReadyKind = "group-sleep-ready";
        public static void Validate(SleepWakeMarker value)
        {
            if (value == null || !Guid.TryParseExact(value.Epoch, "N", out _) ||
                !Guid.TryParseExact(value.Token, "N", out _) || value.Revision < 1 || value.GameSeconds < 0)
                throw new InvalidDataException("Invalid morning synchronization marker.");
        }
        public static bool Matches(SleepWakeMarker left, SleepWakeMarker right)
        {
            Validate(left); Validate(right);
            return left.Epoch == right.Epoch && left.Token == right.Token &&
                left.Revision == right.Revision && left.GameSeconds == right.GameSeconds;
        }
    }
    // Fixed at unanimous native sleep; late joins import a fresh world after
    // completion, while disconnects remove only the departing participant.
    public sealed class SleepWakeBarrier
    {
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> cohort = new HashSet<string>(StringComparer.Ordinal);
        public string Epoch { get; private set; } = "";
        public SleepWakeMarker Marker { get; private set; }
        public int PendingCount => pending.Count;
        public bool Complete => Marker != null && pending.Count == 0;
        public void Begin(string epoch, IEnumerable<string> players)
        {
            if (!Guid.TryParseExact(epoch, "N", out _)) throw new InvalidDataException("Invalid wake epoch.");
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string player in players)
                if (!Guid.TryParseExact(player, "N", out _) || !selected.Add(player) || selected.Count > 32)
                    throw new InvalidDataException("Invalid wake cohort.");
            Reset(); Epoch = epoch;
            foreach (string player in selected) { cohort.Add(player); pending.Add(player); }
        }
        public void Arm(SleepWakeMarker marker)
        {
            SleepWakeProtocol.Validate(marker);
            if (Epoch != marker.Epoch || Marker != null) throw new InvalidOperationException("Wake barrier already armed or stale.");
            Marker = new SleepWakeMarker { Epoch=marker.Epoch, Token=marker.Token, Revision=marker.Revision, GameSeconds=marker.GameSeconds };
        }
        public bool Acknowledge(string player, SleepWakeMarker marker)
        {
            if (Marker == null || !cohort.Contains(player) || !SleepWakeProtocol.Matches(Marker, marker)) return false;
            pending.Remove(player); return true;
        }
        public void Remove(string player) { pending.Remove(player); cohort.Remove(player); }
        public void Reset() { Epoch = ""; Marker = null; pending.Clear(); cohort.Clear(); }
    }
}
