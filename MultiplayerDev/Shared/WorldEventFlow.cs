using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    // Credits account for both framing overhead and a minimum per-message cost.
    // This keeps byte and frame counts bounded without enlarging the TCP queue.
    public static class WorldEventFlow
    {
        public const int BulkWindowBytes = 256 * 1024, ControlWindowBytes = 64 * 1024;
        public const int StallSeconds = 20, MaxAcknowledgements = 128;
        public static int Cost(WorldEvent value)
        {
            if (value == null || !DedicatedProtocol.Text(value.Kind, 48) || value.Data.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Invalid world event.");
            // JsonElement raw text is not a stable wire size: byte[] is initially
            // written as base64 with literal '+', then nesting the element escapes
            // '+' as \u002B. Charge the canonical serialized element on both ends.
            int bytes = CanonicalJson.ByteCount(value.Data);
            if (bytes > 48000) throw new InvalidDataException("World event payload exceeds its bound.");
            if (value.Kind == "group-sleep" && bytes > 24000) throw new InvalidDataException("Shared sleep control exceeds its bound.");
            if (value.Kind == "group-apartment" && bytes > 24000) throw new InvalidDataException("Shared apartment control exceeds its bound.");
            if (value.Kind == DialogueRecoveryProtocol.EventKind && bytes > DialogueRecoveryProtocol.MaxPayloadBytes) throw new InvalidDataException("Pending dialogue control exceeds its bound.");
            return Math.Max(2048, bytes + 1024);
        }
        public static bool IsControlKind(string kind) => kind == "timeWeather" || kind == SleepWakeProtocol.EventKind || kind == "group-travel" || kind == "group-sleep" || kind == "group-apartment" || kind == "playerLeft" ||
            kind == "interaction-result" || kind == "business-result" || kind == "dialogue-result" || kind == "dialogue-state" || kind == "property-result" || kind == DialogueRecoveryProtocol.EventKind ||
            kind == PlayerSceneContext.EventKind || kind == PlayerMapRestore.EventKind || kind == "player-map-error";
        public static bool IsOrderingBarrier(WorldEvent value)
        {
            if (value.Kind == SleepWakeProtocol.EventKind) return true;
            // A durable transaction result must not overtake its private wallet
            // update through the control-priority lane.
            if (value.Kind == "account-state-chunk" || value.Kind == "property-state-chunk") return true;
            if (value.Kind == BoatProtocol.EventKind && value.Data.TryGetProperty("Operation", out var boatOperation) &&
                boatOperation.ValueKind == JsonValueKind.String && boatOperation.GetString() != "snapshot") return true;
            if (value.Kind == DialogueRecoveryProtocol.EventKind) return true;
            if (value.Kind == PlayerSceneContext.EventKind || value.Kind == PlayerMapRestore.EventKind || value.Kind == "player-map-error") return true;
            if (!value.Data.TryGetProperty("Phase", out var phase) || phase.ValueKind != JsonValueKind.String) return false;
            string state = phase.GetString();
            if (value.Kind == "group-sleep") return state != "accepted" && state != "denied";
            if (value.Kind == "group-apartment") return state == "prepare" || state == "execute" || state == "complete" || state == "cancel";
            return value.Kind == "group-travel" && (state == "prepare" || state == "depart" || state == "complete" || state == "cancel");
        }
    }

    /// <summary>Bounded main-thread event scheduler. A kind is always FIFO, including
    /// complete chunk batches from different producers. Distinct bulk kinds receive
    /// round-robin service, while small controls have their own credit reservation.
    /// Travel fences preserve the global before/after ordering of district changes.</summary>
    public sealed class WorldEventScheduler
    {
        public const int MaxBytes = 32 * 1024 * 1024, MaxEvents = 4096, MaxKinds = 64;
        public sealed class Entry
        {
            public WorldEvent Event { get; internal set; }
            public int Cost { get; internal set; }
            public int PayloadBytes { get; internal set; }
            internal long Order;
            internal bool Control, Barrier;
        }
        private readonly Dictionary<string, Queue<Entry>> queues = new Dictionary<string, Queue<Entry>>(StringComparer.Ordinal);
        private readonly List<string> kinds = new List<string>();
        private readonly Queue<long> barriers = new Queue<long>();
        private long sequence;
        private int nextKind;
        public int Count { get; private set; }
        public int PayloadBytes { get; private set; }
        public bool Contains(string kind) => queues.TryGetValue(kind, out var queue) && queue.Count > 0;
        public Entry Enqueue(WorldEvent value)
        {
            int cost = WorldEventFlow.Cost(value);
            int bytes = CanonicalJson.ByteCount(value);
            if (Count >= MaxEvents || bytes > MaxBytes - PayloadBytes)
                throw new IOException("Authoritative update backlog reached its bounded limit.");
            if (!queues.TryGetValue(value.Kind, out var queue))
            {
                if (queues.Count >= MaxKinds) throw new IOException("Too many authoritative update kinds.");
                queues.Add(value.Kind, queue = new Queue<Entry>()); kinds.Add(value.Kind);
            }
            var entry = new Entry { Event = value, Cost = cost, PayloadBytes = bytes, Order = ++sequence,
                Control = WorldEventFlow.IsControlKind(value.Kind), Barrier = WorldEventFlow.IsOrderingBarrier(value) };
            queue.Enqueue(entry); if (entry.Barrier) barriers.Enqueue(entry.Order);
            Count++; PayloadBytes += bytes;
            return entry;
        }
        public bool TryPeek(int bulkCredit, int controlCredit, out Entry selected)
        {
            selected = null;
            long fence = barriers.Count == 0 ? long.MaxValue : barriers.Peek(), oldest = long.MaxValue;
            foreach (var queue in queues.Values) if (queue.Count > 0) oldest = Math.Min(oldest, queue.Peek().Order);
            // Control priority never passes a travel fence or another event of its kind.
            foreach (var queue in queues.Values)
            {
                if (queue.Count == 0) continue;
                var item = queue.Peek();
                if (!item.Control || item.Cost > controlCredit || item.Order > fence || (item.Barrier && item.Order != oldest)) continue;
                if (selected == null || item.Order < selected.Order) selected = item;
            }
            if (selected != null) return true;
            for (int i = 0; i < kinds.Count; i++)
            {
                int index = (nextKind + i) % kinds.Count; var queue = queues[kinds[index]];
                if (queue.Count == 0) continue;
                var item = queue.Peek();
                if (item.Control || item.Cost > bulkCredit || item.Order > fence) continue;
                selected = item; return true;
            }
            return false;
        }
        public void Remove(Entry entry)
        {
            if (entry == null || !queues.TryGetValue(entry.Event.Kind, out var queue) || queue.Count == 0 || !ReferenceEquals(queue.Peek(), entry))
                throw new InvalidOperationException("Only the scheduled queue head may be removed.");
            queue.Dequeue(); Count--; PayloadBytes -= entry.PayloadBytes;
            if (entry.Barrier) { if (barriers.Dequeue() != entry.Order) throw new InvalidOperationException("Invalid travel fence order."); }
            if (!entry.Control) nextKind = (kinds.IndexOf(entry.Event.Kind) + 1) % kinds.Count;
        }
        public string Describe()
        {
            var result = new List<string>();
            foreach (var kind in kinds) if (queues[kind].Count > 0) result.Add(kind + ":" + queues[kind].Count);
            return string.Join(",", result);
        }
        public void Clear()
        { queues.Clear(); kinds.Clear(); barriers.Clear(); Count = PayloadBytes = nextKind = 0; sequence = 0; }
    }
}
