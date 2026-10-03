using System;
using System.Collections.Generic;
using System.IO;

namespace NivalisTogether.Dedicated
{
    /// <summary>One worker-owned boat per account. An empty scene denotes a boat awaiting its first recall.</summary>
    public sealed class PlayerBoatState
    {
        public string BoatId { get; set; } = "";
        public string OwnerAccountId { get; set; } = "";
        public string PrefabId { get; set; } = "";
        public string Scene { get; set; } = "";
        public string DockId { get; set; } = "";
        public long Revision { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Qx { get; set; }
        public float Qy { get; set; }
        public float Qz { get; set; }
        public float Qw { get; set; } = 1;
        public float Fuel { get; set; }
        public bool Unlocked { get; set; } = true;
        public bool PlayerAboard { get; set; }
        public bool PlayerDriving { get; set; }
    }

    /// <summary>Movement only; a client pose cannot assign a dock or change its boat identity.</summary>
    public sealed class BoatPose
    {
        public string PlayerId { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string BoatId { get; set; } = "";
        public string Scene { get; set; } = "";
        public long ContextGeneration { get; set; }
        public long Sequence { get; set; }
        // Revision whose authoritative fuel has actually reached the native
        // client boat, not merely a received account/world-state snapshot.
        public long AppliedFuelRevision { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Qx { get; set; }
        public float Qy { get; set; }
        public float Qz { get; set; }
        public float Qw { get; set; } = 1;
        public float Fuel { get; set; }
        public bool Driving { get; set; }
        public bool Boarded { get; set; }
    }

    public sealed class BoatInteractionIntent
    {
        public string BoatId { get; set; } = "";
        public string DockId { get; set; } = "";
    }

    public sealed class BoatWorldState
    {
        public long Sequence { get; set; }
        public PlayerBoatState[] Boats { get; set; } = Array.Empty<PlayerBoatState>();
        public BoatPose[] RuntimePoses { get; set; } = Array.Empty<BoatPose>();
        public string TargetPlayerId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Operation { get; set; } = "snapshot";
        public string Message { get; set; } = "";
        public string BoatId { get; set; } = "";
        public bool Success { get; set; } = true;
    }

    public static class BoatProtocol
    {
        public const string EventKind = "boats", PoseKind = "boat-pose";
        public const int MaxBoats = 32, MaxIdLength = 128, MaxMessageLength = 1024;
        public const float MaxCoordinate = 1000000, MaxFuel = 1000000;
        // Unity quaternion round trips introduce small float error; zero/scaled quaternions remain invalid.
        public const double QuaternionNormTolerance = 0.02;

        public static void Validate(PlayerBoatState value)
        {
            if (value == null || !Id(value.BoatId) || !Id(value.OwnerAccountId) ||
                !DedicatedProtocol.Text(value.PrefabId, MaxIdLength) || !DedicatedProtocol.Text(value.Scene, MaxIdLength, true) ||
                !DedicatedProtocol.Text(value.DockId, MaxIdLength, true) || value.Revision < 0 ||
                !TransformValid(value.X, value.Y, value.Z, value.Qx, value.Qy, value.Qz, value.Qw) || !FuelValid(value.Fuel) ||
                (value.PlayerDriving && !value.PlayerAboard) ||
                (value.Scene.Length == 0 && (value.DockId.Length != 0 || value.X != 0 || value.Y != 0 || value.Z != 0 ||
                    value.Qx != 0 || value.Qy != 0 || value.Qz != 0 || value.Qw != 1 || value.PlayerAboard || value.PlayerDriving)))
                throw new InvalidDataException("Invalid persistent account boat.");
        }

        public static void Validate(BoatPose value)
        {
            if (value == null || !Id(value.PlayerId) || !Id(value.AccountId) || !Id(value.BoatId) ||
                !DedicatedProtocol.Text(value.Scene, MaxIdLength) || value.ContextGeneration < 1 || value.Sequence < 1 || value.AppliedFuelRevision < 0 ||
                !TransformValid(value.X, value.Y, value.Z, value.Qx, value.Qy, value.Qz, value.Qw) || !FuelValid(value.Fuel) ||
                (value.Driving && !value.Boarded))
                throw new InvalidDataException("Invalid boat movement.");
        }

        public static void Validate(BoatInteractionIntent value)
        {
            if (value == null || !Id(value.BoatId) || !DedicatedProtocol.Text(value.DockId, MaxIdLength, true))
                throw new InvalidDataException("Invalid boat interaction.");
        }

        public static void Validate(BoatWorldState value)
        {
            if (value == null || value.Sequence < 0 || value.Boats == null || value.RuntimePoses == null ||
                value.Boats.Length > MaxBoats || value.RuntimePoses.Length > MaxBoats ||
                !OptionalId(value.TargetPlayerId) || !OptionalId(value.RequestId) || !OptionalId(value.BoatId) ||
                !DedicatedProtocol.Text(value.Operation, 64) || !DedicatedProtocol.Text(value.Message, MaxMessageLength, true))
                throw new InvalidDataException("Invalid boat world state.");
            var boats = new Dictionary<string, PlayerBoatState>(StringComparer.OrdinalIgnoreCase);
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var boat in value.Boats)
            {
                Validate(boat);
                if (!boats.TryAdd(boat.BoatId, boat) || !owners.Add(boat.OwnerAccountId))
                    throw new InvalidDataException("Duplicate boat or boat owner.");
            }
            var moving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var players = new HashSet<string>(StringComparer.Ordinal);
            var movingOwners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in value.RuntimePoses)
            {
                Validate(pose);
                if (!moving.Add(pose.BoatId) || !players.Add(pose.PlayerId) || !movingOwners.Add(pose.AccountId) ||
                    (boats.TryGetValue(pose.BoatId, out var boat) && (boat.OwnerAccountId != pose.AccountId || boat.Scene != pose.Scene)))
                    throw new InvalidDataException("Invalid boat world movement ownership.");
            }
        }

        public static bool PoseValid(BoatPose value)
        {
            try { Validate(value); return true; }
            catch (InvalidDataException) { return false; }
        }
        public static bool FuelValid(float value) => float.IsFinite(value) && value >= 0 && value <= MaxFuel;
        public static bool TransformValid(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || Math.Abs(x) > MaxCoordinate ||
                Math.Abs(y) > MaxCoordinate || Math.Abs(z) > MaxCoordinate || !float.IsFinite(qx) || !float.IsFinite(qy) ||
                !float.IsFinite(qz) || !float.IsFinite(qw)) return false;
            double lengthSquared = (double)qx * qx + (double)qy * qy + (double)qz * qz + (double)qw * qw;
            return Math.Abs(lengthSquared - 1) <= QuaternionNormTolerance;
        }
        private static bool Id(string value) => Guid.TryParseExact(value, "N", out _);
        private static bool OptionalId(string value) => value != null && (value.Length == 0 || Id(value));

        public static PlayerBoatState Copy(PlayerBoatState value) => value == null ? null : new PlayerBoatState
        {
            BoatId = value.BoatId, OwnerAccountId = value.OwnerAccountId, PrefabId = value.PrefabId, Scene = value.Scene,
            DockId = value.DockId, Revision = value.Revision, X = value.X, Y = value.Y, Z = value.Z,
            Qx = value.Qx, Qy = value.Qy, Qz = value.Qz, Qw = value.Qw, Fuel = value.Fuel, Unlocked = value.Unlocked,
            PlayerAboard = value.PlayerAboard, PlayerDriving = value.PlayerDriving
        };
        public static BoatPose Copy(BoatPose value) => value == null ? null : new BoatPose
        {
            PlayerId = value.PlayerId, AccountId = value.AccountId, BoatId = value.BoatId, Scene = value.Scene,
            ContextGeneration = value.ContextGeneration, Sequence = value.Sequence, AppliedFuelRevision = value.AppliedFuelRevision, X = value.X, Y = value.Y, Z = value.Z,
            Qx = value.Qx, Qy = value.Qy, Qz = value.Qz, Qw = value.Qw, Fuel = value.Fuel, Driving = value.Driving, Boarded = value.Boarded
        };
        public static BoatInteractionIntent Copy(BoatInteractionIntent value) => value == null ? null : new BoatInteractionIntent
        { BoatId = value.BoatId, DockId = value.DockId };
        public static BoatWorldState Copy(BoatWorldState value)
        {
            if (value == null) return null;
            var copy = new BoatWorldState { Sequence = value.Sequence, TargetPlayerId = value.TargetPlayerId, RequestId = value.RequestId,
                Operation = value.Operation, Message = value.Message, BoatId = value.BoatId, Success = value.Success,
                Boats = value.Boats == null ? null : new PlayerBoatState[value.Boats.Length],
                RuntimePoses = value.RuntimePoses == null ? null : new BoatPose[value.RuntimePoses.Length] };
            if (copy.Boats != null) for (int i = 0; i < copy.Boats.Length; i++) copy.Boats[i] = Copy(value.Boats[i]);
            if (copy.RuntimePoses != null) for (int i = 0; i < copy.RuntimePoses.Length; i++) copy.RuntimePoses[i] = Copy(value.RuntimePoses[i]);
            return copy;
        }
    }
}
