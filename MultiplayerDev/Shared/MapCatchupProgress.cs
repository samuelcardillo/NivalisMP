using System;
using System.IO;

namespace NivalisTogether.Dedicated
{
    // Progress is an authenticated liveness report only. It never acknowledges
    // deliveries, releases credit, assigns a map or extends a native transition.
    public sealed class MapCatchupProgress
    {
        public const string MessageKind = "mapCatchupProgress";
        public string Epoch { get; set; } = "";
        public long Generation { get; set; }
        public long AppliedDeliveryId { get; set; }
        public bool RequestBarrier { get; set; }
        public bool Complete { get; set; }
    }

    public sealed class MapCatchupBarrier
    {
        public const string EventKind = "player-map-catchup-barrier";
        public string Epoch { get; set; } = "";
        public long Generation { get; set; }
        public int Round { get; set; }
        public static void Validate(MapCatchupBarrier value)
        {
            if (value == null || !Guid.TryParseExact(value.Epoch, "N", out _) || value.Generation < 1 ||
                value.Round < 1 || value.Round > MapCatchupDeadline.MaximumBarrierRounds)
                throw new InvalidDataException("Invalid map catch-up barrier.");
        }
    }

    public sealed class MapCatchupConfirmation
    {
        public const string MessageKind = "mapCatchupConfirmed";
        public string Epoch { get; set; } = "";
        public long Generation { get; set; }
        public int Round { get; set; }
        public long BarrierDeliveryId { get; set; }
        public static void Validate(MapCatchupConfirmation value)
        {
            if (value == null || value.BarrierDeliveryId < 1)
                throw new InvalidDataException("Invalid map catch-up confirmation.");
            MapCatchupBarrier.Validate(new MapCatchupBarrier { Epoch = value.Epoch, Generation = value.Generation, Round = value.Round });
        }
    }

    public sealed class MapCatchupDeadline
    {
        public const double MaximumSeconds = 120, ProgressStallSeconds = 20;
        public const double MaximumBarrierAgeSeconds = 2;
        public const int MaximumBarrierRounds = 8;
        private readonly PlayerSceneMembership observed = new PlayerSceneMembership();
        private string epoch = "";
        private long generation, firstIssued, applied;
        private double settledAt, progressedAt;
        private bool authorized, settled, hasProgress, closed, confirmed;
        private bool barrierRequested, completionRequested, needsNextBarrier;
        private int barrierRound;
        private long barrierDeliveryId;
        private double barrierQueuedAt;

        public bool IsActive => authorized && !closed;
        public bool BarrierRequested => barrierRequested;
        public bool NeedsNextBarrier => needsNextBarrier;
        public long BarrierDeliveryId => barrierDeliveryId;
        public int BarrierRound => barrierRound;

        public void Reset()
        {
            observed.Reset(); epoch = ""; generation = firstIssued = applied = 0;
            settledAt = progressedAt = 0; authorized = settled = hasProgress = closed = false;
            ResetBarrier();
        }

        public void ObserveContext(PlayerSceneContext context, double now, long lastIssuedDeliveryId)
        {
            RequireClock(now);
            if (lastIssuedDeliveryId < 0) throw new InvalidDataException("Invalid issued delivery boundary.");
            if (!observed.Observe(context)) return; // Identical repeats cannot renew either clock.
            if (context.TravelPending)
            {
                if (context.Epoch == epoch) throw new InvalidDataException("Map catch-up epoch was reused.");
                epoch = context.Epoch; generation = context.Generation; firstIssued = lastIssuedDeliveryId;
                applied = 0; authorized = true; settled = hasProgress = closed = false;
                settledAt = progressedAt = 0;
                ResetBarrier();
                return;
            }
            if (!authorized || closed) { confirmed = false; return; }
            if (generation == long.MaxValue || context.Generation != generation + 1)
            {
                authorized = false; closed = true; return;
            }
            // A slow but valid cab/boat load can exceed120s. Its existing native
            // transition deadline applies until this worker-authored settlement.
            if (!settled) { settled = true; settledAt = now; }
        }

        public bool ObserveProgress(MapCatchupProgress value, double now, long lastIssuedDeliveryId)
        {
            RequireClock(now);
            if (closed && confirmed)
            {
                // Confirmation crosses the opposite TCP stream. A report sent
                // before the client receives it may arrive after service closure.
                // Validate that old identity/range, then ignore it completely.
                if (value == null || !Guid.TryParseExact(value.Epoch, "N", out _) ||
                    value.Epoch != epoch || value.Generation != generation || value.RequestBarrier ||
                    value.AppliedDeliveryId <= firstIssued || value.AppliedDeliveryId > lastIssuedDeliveryId ||
                    (value.Complete && value.AppliedDeliveryId != barrierDeliveryId))
                    throw new InvalidDataException("Trailing map catch-up progress is outside its confirmed context.");
                return false;
            }
            bool emptyBarrierRequest = value != null && value.RequestBarrier && value.AppliedDeliveryId == 0;
            bool exactCompletion = value != null && value.Complete && barrierRequested && barrierDeliveryId > 0 &&
                value.AppliedDeliveryId == barrierDeliveryId;
            if (value == null || !Guid.TryParseExact(value.Epoch, "N", out _) || value.Generation < 1 ||
                (!emptyBarrierRequest && value.AppliedDeliveryId < 1) || !authorized || !settled || closed ||
                (value.RequestBarrier && value.Complete) ||
                value.Epoch != epoch || value.Generation != generation ||
                (!emptyBarrierRequest && (value.AppliedDeliveryId <= firstIssued || value.AppliedDeliveryId > lastIssuedDeliveryId ||
                    (value.AppliedDeliveryId < applied && !exactCompletion))) || (emptyBarrierRequest && applied != 0) ||
                (barrierDeliveryId != 0 && value.AppliedDeliveryId > barrierDeliveryId && !completionRequested))
                throw new InvalidDataException("Map catch-up progress is outside its issued context.");
            RequireLivePhase(now);
            if (value.Complete)
            {
                if (!barrierRequested || barrierDeliveryId == 0 || value.AppliedDeliveryId != barrierDeliveryId)
                    throw new InvalidDataException("Map catch-up completion did not apply its ordered barrier.");
                completionRequested = true;
            }
            if (value.RequestBarrier) barrierRequested = true;
            // An empty local backlog may request the ordered marker, but that
            // request itself proves no application progress and grants no grace.
            if (emptyBarrierRequest) return false;
            // A marker may be reported after later actual applications. Record
            // its completion separately without regressing or renewing progress.
            if (value.AppliedDeliveryId <= applied) return false;
            applied = value.AppliedDeliveryId; progressedAt = now; hasProgress = true;
            return true;
        }

        public MapCatchupBarrier BeginBarrierRound(double now)
        {
            RequireClock(now); RequireLivePhase(now);
            if (!barrierRequested || (barrierRound != 0 && !needsNextBarrier))
                throw new InvalidDataException("Map catch-up barrier was not requested or is already queued.");
            if (barrierRound >= MaximumBarrierRounds)
                throw new InvalidDataException("Map catch-up could not reach a fresh bounded barrier.");
            barrierRound++; barrierQueuedAt = now; barrierDeliveryId = 0;
            completionRequested = needsNextBarrier = false;
            return new MapCatchupBarrier { Epoch = epoch, Generation = generation, Round = barrierRound };
        }

        public void BarrierIssued(MapCatchupBarrier marker, long deliveryId)
        {
            MapCatchupBarrier.Validate(marker);
            if (!authorized || !settled || closed || !barrierRequested || marker.Epoch != epoch ||
                marker.Generation != generation || marker.Round != barrierRound || deliveryId <= firstIssued ||
                (barrierDeliveryId != 0 && barrierDeliveryId != deliveryId) ||
                (barrierDeliveryId == 0 && deliveryId <= applied))
                throw new InvalidDataException("Map catch-up barrier is outside its issued context.");
            barrierDeliveryId = deliveryId;
        }

        // The service supplies its smallest actually outstanding receipt ID;
        // zero means no receipts remain. Progress reports never remove receipts.
        public bool TryConfirm(double now, long oldestUnacknowledgedDeliveryId, out MapCatchupConfirmation confirmation)
        {
            confirmation = null; RequireClock(now);
            if (oldestUnacknowledgedDeliveryId < 0)
                throw new InvalidDataException("Invalid outstanding delivery boundary.");
            if (!authorized || !settled || closed || !completionRequested || barrierDeliveryId == 0) return false;
            RequireLivePhase(now);
            if (oldestUnacknowledgedDeliveryId != 0 && oldestUnacknowledgedDeliveryId <= barrierDeliveryId) return false;
            if (now < barrierQueuedAt) throw new InvalidDataException("Map catch-up barrier clock regressed.");
            if (now - barrierQueuedAt > MaximumBarrierAgeSeconds)
            {
                // The client drained an old prefix, but is not yet near the live
                // stream. Probe one newer prefix without resetting either cap.
                needsNextBarrier = true; return false;
            }
            confirmation = new MapCatchupConfirmation { Epoch = epoch, Generation = generation,
                Round = barrierRound, BarrierDeliveryId = barrierDeliveryId };
            closed = confirmed = true; return true;
        }

        private void ResetBarrier()
        {
            barrierRequested = completionRequested = needsNextBarrier = confirmed = false;
            barrierRound = 0; barrierDeliveryId = 0; barrierQueuedAt = 0;
        }

        private void RequireLivePhase(double now)
        {
            if (!authorized || !settled || closed || now < settledAt || now - settledAt >= MaximumSeconds ||
                (hasProgress && (now < progressedAt || now - progressedAt >= ProgressStallSeconds)))
                throw new InvalidDataException("Map catch-up progress exceeded its bounded phase.");
        }

        public bool CanDeferDeliveryTimeout(double now) =>
            double.IsFinite(now) && authorized && settled && !closed && hasProgress &&
            now >= settledAt && now - settledAt < MaximumSeconds &&
            now >= progressedAt && now - progressedAt < ProgressStallSeconds;

        private static void RequireClock(double now)
        {
            if (!double.IsFinite(now) || now < 0) throw new InvalidDataException("Invalid catch-up clock.");
        }
    }
}
