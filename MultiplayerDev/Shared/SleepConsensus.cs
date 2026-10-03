using System;
using System.Collections.Generic;
using System.IO;

namespace NivalisTogether.Dedicated
{
    // Pure membership/readiness policy. A pending snapshot is still connected:
    // only the authenticated transport's departure removes that participant.
    public sealed class SleepConsensus
    {
        private readonly SortedSet<string> connected = new SortedSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> loaded = new HashSet<string>(StringComparer.Ordinal);
        private readonly SortedSet<string> ready = new SortedSet<string>(StringComparer.Ordinal);
        public string[] Participants => Copy(connected);
        public string[] ReadyPlayers => Copy(ready);
        public int ParticipantCount => connected.Count;
        public int ReadyCount => ready.Count;
        public bool CanStart => connected.Count > 0 && ready.Count == connected.Count;
        public bool IsConnected(string id) => connected.Contains(id);
        public bool IsReady(string id) => ready.Contains(id);
        public void SetConnected(string id, bool value)
        {
            Validate(id);
            if (value)
            {
                if (connected.Count >= 32 && !connected.Contains(id)) throw new InvalidDataException("Too many sleep participants.");
                connected.Add(id);
            }
            else { connected.Remove(id); loaded.Remove(id); ready.Remove(id); }
        }
        public void SetLoaded(string id, bool value)
        {
            Validate(id);
            if (value)
            {
                if (!connected.Contains(id)) throw new InvalidOperationException("A loaded sleeper must still be connected.");
                loaded.Add(id);
            }
            else { loaded.Remove(id); ready.Remove(id); }
        }
        public void SetReady(string id, bool value)
        {
            Validate(id);
            if (value)
            {
                if (!connected.Contains(id) || !loaded.Contains(id)) throw new InvalidOperationException("Wait for this player to finish loading before choosing sleep.");
                ready.Add(id);
            }
            else ready.Remove(id);
        }
        public void ClearReadiness() => ready.Clear();
        public void Reset() { connected.Clear(); loaded.Clear(); ready.Clear(); }
        private static string[] Copy(SortedSet<string> values) { var result = new string[values.Count]; values.CopyTo(result); return result; }
        private static void Validate(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid sleep participant."); }
    }
}
