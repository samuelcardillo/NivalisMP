using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;

namespace NivalisTogether.Server
{
    // One recipient's reliable history. A blocked peer cannot borrow another
    // peer's receipts or force the worker to stop producing authoritative state.
    internal sealed class PeerWorldEventQueue
    {
        internal const int MaxBytes = 64 * 1024 * 1024, MaxEvents = 8192;
        internal const double MaxAgeSeconds = 120;
        internal sealed class Entry
        {
            internal WorldEvent Event { get; }
            internal int Cost { get; }
            internal bool Control { get; }
            internal double QueuedAt { get; }
            internal Entry(WorldEvent value, int cost, bool control, double queuedAt)
            { Event = value; Cost = cost; Control = control; QueuedAt = queuedAt; }
        }
        private readonly Queue<Entry> entries = new Queue<Entry>();
        internal int Count => entries.Count;
        // Charge canonical payload plus the existing1024-byte transport reserve
        // (at least2048/event). Validated GUID envelope fields fit that reserve.
        internal int Bytes { get; private set; }

        internal void Enqueue(WorldEvent source, int cost, bool control, double now)
        {
            RequireTime(now);
            if (source == null || !Guid.TryParseExact(source.WorldId, "N", out _) || source.Revision < 0 ||
                source.TargetPlayerId == null || (source.TargetPlayerId.Length > 0 && !Guid.TryParseExact(source.TargetPlayerId, "N", out _)) ||
                !DedicatedProtocol.Text(source.Kind, 48) || source.Data.ValueKind == JsonValueKind.Undefined ||
                control != WorldEventFlow.IsControlKind(source.Kind))
                throw new InvalidDataException("Invalid per-player world event envelope.");
            int payloadBytes = CanonicalJson.ByteCount(source.Data);
            if (payloadBytes > 48000 || cost != Math.Max(2048, payloadBytes + 1024))
                throw new InvalidDataException("Invalid per-player world event charge.");
            if (Count >= MaxEvents || cost > MaxBytes - Bytes)
                throw new IOException("This player's reliable world history exceeded its bounded queue.");
            // JsonElement and strings are immutable. Copy the mutable envelope
            // before any recipient assigns its private delivery identity.
            var copy = new WorldEvent { WorldId = source.WorldId, Revision = source.Revision,
                TargetPlayerId = source.TargetPlayerId, Kind = source.Kind, Data = source.Data, DeliveryId = 0 };
            entries.Enqueue(new Entry(copy, cost, control, now)); Bytes += cost;
        }

        internal bool TryPeek(int bulkCredit, int controlCredit, out Entry entry)
        {
            if (bulkCredit < 0 || controlCredit < 0) throw new ArgumentOutOfRangeException("credit");
            entry = null;
            if (entries.Count == 0) return false;
            var head = entries.Peek();
            if (head.Cost > (head.Control ? controlCredit : bulkCredit)) return false;
            entry = head; return true; // Never bypass a blocked head, even for control.
        }

        internal void Remove(Entry entry)
        {
            if (entry == null || entries.Count == 0 || !ReferenceEquals(entries.Peek(), entry))
                throw new InvalidOperationException("Only this peer's exact FIFO head can be removed.");
            entries.Dequeue(); Bytes -= entry.Cost;
        }

        internal bool IsExpired(double now)
        {
            RequireTime(now);
            return entries.Count > 0 && now >= entries.Peek().QueuedAt && now - entries.Peek().QueuedAt >= MaxAgeSeconds;
        }
        internal void Clear() { entries.Clear(); Bytes = 0; }
        private static void RequireTime(double now)
        { if (!double.IsFinite(now) || now < 0) throw new InvalidDataException("Invalid per-player queue clock."); }
    }
}
