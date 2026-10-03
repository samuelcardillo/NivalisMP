using System;
using System.Collections.Generic;
using System.IO;

namespace NivalisTogether.Dedicated
{
    // Service-side property context fence. Native route execution stays in the
    // worker; clients may import only a snapshot captured after its final phase.
    public sealed class ApartmentLoadFence
    {
        private GroupApartmentEvent route;
        private string phase = "";
        private long lastSequence;
        private HashSet<string> participants = new HashSet<string>(StringComparer.Ordinal);
        public long Generation { get; private set; }
        public long MinimumRevision { get; private set; }
        public bool TransitionActive => route != null;
        public bool AwaitingFreshCheckpoint { get; private set; }
        public bool BlocksSnapshot => TransitionActive || AwaitingFreshCheckpoint;
        public void Reset()
        { route = null; phase = ""; lastSequence = Generation = MinimumRevision = 0; AwaitingFreshCheckpoint = false; participants.Clear(); }
        // True only for a new prepare, so the caller invalidates earlier imports.
        public bool Observe(GroupApartmentEvent value, long committedRevision)
        {
            ApartmentProtocol.Validate(value);
            if (value.Sequence <= lastSequence || committedRevision < 1) throw new InvalidDataException("Replayed apartment authority state.");
            if (value.Phase == "accepted" || value.Phase == "denied" || value.Phase == "fatal")
            { lastSequence = value.Sequence; return false; }
            if (value.PlayerId.Length != 0) throw new InvalidDataException("Apartment phase must address the party.");
            if (value.Phase == "prepare")
            {
                if (route != null || committedRevision == long.MaxValue || Generation == long.MaxValue || value.Participants.Length == 0)
                    throw new InvalidDataException("Overlapping or invalid apartment preparation.");
                route = value; phase = "prepare"; Generation++;
                MinimumRevision = Math.Max(MinimumRevision, committedRevision + 1);
                AwaitingFreshCheckpoint = true;
                participants = new HashSet<string>(value.Participants, StringComparer.Ordinal);
                lastSequence = value.Sequence; return true;
            }
            if (!ApartmentProtocol.SameRoute(route, value)) throw new InvalidDataException("Apartment phase does not match its preparation.");
            var current = new HashSet<string>(value.Participants, StringComparer.Ordinal);
            if (!current.IsSubsetOf(participants)) throw new InvalidDataException("Apartment membership grew after preparation.");
            if (value.Phase == "execute")
            { if (phase != "prepare") throw new InvalidDataException("Unexpected apartment execution."); phase = "execute"; }
            else if (value.Phase == "complete" || value.Phase == "cancel")
            {
                if (value.Phase == "complete" ? phase != "execute" : phase != "prepare")
                    throw new InvalidDataException("Invalid apartment final phase.");
                route = null; phase = "";
            }
            else if (value.Phase != "heartbeat") throw new InvalidDataException("Invalid apartment authority phase.");
            participants = current; lastSequence = value.Sequence; return false;
        }
        public long BeginSnapshot(long revision)
        {
            if (TransitionActive) throw new InvalidDataException("A native apartment transition cannot publish a partial checkpoint.");
            if (revision < 1) throw new InvalidDataException("Invalid apartment checkpoint revision.");
            return Generation;
        }
        public void CommitSnapshot(long revision, long begunGeneration)
        {
            if (TransitionActive || begunGeneration != Generation)
                throw new InvalidDataException("Checkpoint began before the completed apartment transition.");
            if (revision >= MinimumRevision) AwaitingFreshCheckpoint = false;
        }
        public bool CanLoad(long revision) => !BlocksSnapshot && revision > 0 && revision >= MinimumRevision;
    }
}
