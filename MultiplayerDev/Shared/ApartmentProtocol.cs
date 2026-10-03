using System;
using System.Collections.Generic;
using System.IO;

namespace NivalisTogether.Dedicated
{
    public sealed class ApartmentIntent { public bool Enter { get; set; } }
    public sealed class ApartmentAcknowledgement
    {
        public bool Success { get; set; }
        public string Scene { get; set; } = "";
        public string PropertyId { get; set; } = "";
        public string Message { get; set; } = "";
    }
    public sealed class GroupApartmentEvent
    {
        public long Sequence { get; set; }
        public string Epoch { get; set; } = "";
        public string Phase { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string ApartmentId { get; set; } = "";
        public string Scene { get; set; } = "";
        public bool Enter { get; set; }
        public string Message { get; set; } = "";
        public string[] Participants { get; set; } = Array.Empty<string>();
        public string[] FailedPlayers { get; set; } = Array.Empty<string>();
        public string SourceProperty => Enter ? "" : ApartmentId;
        public string DestinationProperty => Enter ? ApartmentId : "";
    }
    // Pure identity/membership gates. No player-supplied coordinate or transform is
    // part of this protocol: every process resolves its own canonical native door.
    public static class ApartmentProtocol
    {
        public const int MaxParticipants = 32;
        public static bool Bounded(string value, int max) => value != null && value.Length <= max &&
            (value.Length == 0 || DedicatedProtocol.Text(value, max));
        public static bool SameRoute(GroupApartmentEvent a, GroupApartmentEvent b) => a != null && b != null &&
            a.Epoch == b.Epoch && a.ApartmentId == b.ApartmentId && a.Scene == b.Scene && a.Enter == b.Enter;
        public static void Validate(GroupApartmentEvent value)
        {
            if (value == null || value.Sequence < 1 || !Bounded(value.Epoch, 32) || !Bounded(value.PlayerId, 32) ||
                !Bounded(value.RequestId, 32) || !Bounded(value.ApartmentId, 128) || !Bounded(value.Scene, 128) ||
                !Bounded(value.Message, 240) || value.Participants == null || value.Participants.Length > MaxParticipants ||
                value.FailedPlayers == null || value.FailedPlayers.Length > MaxParticipants)
                throw new InvalidDataException("Malformed apartment transition.");
            bool routed = value.Phase == "prepare" || value.Phase == "execute" || value.Phase == "complete" || value.Phase == "cancel" || value.Phase == "heartbeat";
            if (!routed && value.Phase != "fatal" && value.Phase != "accepted" && value.Phase != "denied")
                throw new InvalidDataException("Unknown apartment transition phase.");
            if (value.PlayerId.Length != 0 && !Guid.TryParseExact(value.PlayerId, "N", out _)) throw new InvalidDataException("Invalid apartment recipient.");
            if (value.RequestId.Length != 0 && !Guid.TryParseExact(value.RequestId, "N", out _)) throw new InvalidDataException("Invalid apartment request.");
            if (routed && (!Guid.TryParseExact(value.Epoch, "N", out _) || !Guid.TryParse(value.ApartmentId, out _) || value.Scene.Length == 0))
                throw new InvalidDataException("Invalid apartment route.");
            if (routed && (!Guid.TryParseExact(value.PlayerId, "N", out _) || value.Participants.Length != 1 || value.Participants[0] != value.PlayerId))
                throw new InvalidDataException("Apartment transitions must address exactly their authenticated traveler.");
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in value.Participants)
                if (!Guid.TryParseExact(id, "N", out _) || !members.Add(id)) throw new InvalidDataException("Invalid apartment participant.");
            var failed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in value.FailedPlayers)
                if (!members.Contains(id) || !failed.Add(id)) throw new InvalidDataException("Invalid failed apartment participant.");
        }
    }
    public sealed class ApartmentBarrier
    {
        private readonly HashSet<string> members = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> prepared = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> arrived = new HashSet<string>(StringComparer.Ordinal);
        public string Epoch { get; }
        public string Phase { get; private set; } = "prepare";
        public double Deadline { get; private set; }
        public double FinalDeadline { get; }
        public ApartmentBarrier(string epoch, IEnumerable<string> participants, double now)
        {
            if (!Guid.TryParseExact(epoch, "N", out _) || !double.IsFinite(now)) throw new ArgumentException("Invalid apartment barrier.");
            Epoch = epoch; Deadline = now + 15; FinalDeadline = now + 60;
            foreach (string id in participants)
                if (!Guid.TryParseExact(id, "N", out _) || !members.Add(id) || members.Count > ApartmentProtocol.MaxParticipants)
                    throw new ArgumentException("Invalid apartment membership.");
            if (members.Count == 0) throw new ArgumentException("Apartment entry requires a loaded participant.");
        }
        public bool Contains(string id) => members.Contains(id);
        public void Remove(string id) { members.Remove(id); prepared.Remove(id); arrived.Remove(id); }
        public bool CanAcknowledge(string id, string epoch, bool arrival) => Epoch == epoch && members.Contains(id) &&
            (arrival ? Phase == "arrive" && prepared.Contains(id) : Phase == "prepare");
        public bool Acknowledge(string id, string epoch, bool arrival)
            => CanAcknowledge(id, epoch, arrival) && (arrival ? arrived.Add(id) : prepared.Add(id));
        public bool AllPrepared => members.Count == prepared.Count;
        public bool AllArrived => members.Count == arrived.Count;
        public string[] Participants { get { var a = new string[members.Count]; members.CopyTo(a); Array.Sort(a, StringComparer.Ordinal); return a; } }
        public string[] MissingArrivals { get { var a = new List<string>(); foreach (var id in members) if (!arrived.Contains(id)) a.Add(id); a.Sort(StringComparer.Ordinal); return a.ToArray(); } }
        public void BeginWorker(double now)
        {
            if (Phase != "prepare" || !AllPrepared || !double.IsFinite(now) || now > Deadline) throw new InvalidOperationException("Apartment preparation is incomplete.");
            Phase = "worker"; Deadline = Math.Min(now + 15, FinalDeadline);
        }
        public void BeginArrivals(double now)
        {
            if (Phase != "worker" || !double.IsFinite(now) || now > Deadline) throw new InvalidOperationException("Apartment worker phase expired.");
            Phase = "arrive"; Deadline = Math.Min(now + 30, FinalDeadline);
        }
    }
    public sealed class ApartmentNativePermit
    {
        private readonly string epoch, apartment;
        private readonly bool enter;
        public bool Consumed { get; private set; }
        public bool ContextObserved { get; private set; }
        public bool ArrivalObserved { get; private set; }
        public ApartmentNativePermit(string epoch, string apartment, bool enter)
        {
            if (!Guid.TryParseExact(epoch, "N", out _) || !Guid.TryParse(apartment, out _)) throw new ArgumentException("Invalid apartment invocation.");
            this.epoch = epoch; this.apartment = apartment; this.enter = enter;
        }
        public bool Consume(string epoch, string apartment, bool enter)
        {
            if (Consumed || this.epoch != epoch || this.apartment != apartment || this.enter != enter) return false;
            Consumed = true; return true;
        }
        public bool ObserveContext(string epoch, string apartment, bool enter)
        {
            if (!Consumed || ContextObserved || this.epoch != epoch || this.apartment != apartment || this.enter != enter) return false;
            ContextObserved = true; return true;
        }
        public bool ObserveArrival(bool nativeContextMatches, bool positionNearTarget)
        {
            if (!Consumed || !ContextObserved || !nativeContextMatches || !positionNearTarget) return false;
            ArrivalObserved = true; return true;
        }
        // Once arrival was observed, walking away must not invalidate it. Context
        // still has to match at party completion; a later unsupported exit fails.
        public bool IsComplete(bool nativeContextMatches) => Consumed && ContextObserved && ArrivalObserved && nativeContextMatches;
    }
}
