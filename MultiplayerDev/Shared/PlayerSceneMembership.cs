using System;
using System.IO;

namespace NivalisTogether.Dedicated
{
    /// <summary>Worker-authored position persisted beside the native world checkpoint.</summary>
    public sealed class PlayerMapLocation
    {
        public string Scene { get; set; } = "";
        public string PropertyId { get; set; } = "";
        public string PortalId { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public bool HasPosition { get; set; }

        public static void Validate(PlayerMapLocation value)
        {
            if (value == null || !DedicatedProtocol.Text(value.Scene, 128) ||
                !DedicatedProtocol.Text(value.PropertyId, 128, true) || !DedicatedProtocol.Text(value.PortalId, 128, true) ||
                !DedicatedProtocol.PoseValid(new PlayerPose { Scene = value.Scene, X = value.X, Y = value.Y, Z = value.Z, Yaw = value.Yaw }))
                throw new InvalidDataException("Invalid persistent player map location.");
        }
        public static PlayerMapLocation Copy(PlayerMapLocation value) => value == null ? null : new PlayerMapLocation
        { Scene = value.Scene, PropertyId = value.PropertyId, PortalId = value.PortalId, X = value.X, Y = value.Y, Z = value.Z, Yaw = value.Yaw, HasPosition = value.HasPosition };
    }

    /// <summary>Private, authenticated worker assignment. Client movement never grants map membership.</summary>
    public sealed class PlayerSceneContext
    {
        public const string EventKind = "player-scene-context";
        public string PlayerId { get; set; } = "";
        public string Scene { get; set; } = "";
        public string PropertyId { get; set; } = "";
        public long Generation { get; set; }
        public bool TravelPending { get; set; }
        public string Epoch { get; set; } = "";

        public static void Validate(PlayerSceneContext value)
        {
            if (value == null || !Guid.TryParseExact(value.PlayerId, "N", out _) || value.Generation < 1 ||
                !DedicatedProtocol.Text(value.Scene, 128) || !DedicatedProtocol.Text(value.PropertyId, 128, true) ||
                value.Epoch == null || (value.TravelPending ? !Guid.TryParseExact(value.Epoch, "N", out _) : value.Epoch.Length != 0))
                throw new InvalidDataException("Invalid player scene assignment.");
        }
        public static PlayerSceneContext Copy(PlayerSceneContext value) => value == null ? null : new PlayerSceneContext
        { PlayerId = value.PlayerId, Scene = value.Scene, PropertyId = value.PropertyId, Generation = value.Generation, TravelPending = value.TravelPending, Epoch = value.Epoch };
    }

    public sealed class PlayerMapRestore
    {
        public const string EventKind = "player-map-restore", AcknowledgementKind = "player-map-restored";
        public string PlayerId { get; set; } = "";
        public string Epoch { get; set; } = "";
        public PlayerMapLocation Location { get; set; }
        public static void Validate(PlayerMapRestore value)
        {
            if (value == null || !Guid.TryParseExact(value.PlayerId, "N", out _) || !Guid.TryParseExact(value.Epoch, "N", out _))
                throw new InvalidDataException("Invalid player map restoration.");
            PlayerMapLocation.Validate(value.Location);
        }
    }
    public sealed class PlayerMapRestoreAcknowledgement
    {
        public bool Success { get; set; }
        public string Scene { get; set; } = "";
        public string PropertyId { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>Pure service-side gate for one authenticated player. Reset on every world import.</summary>
    public sealed class PlayerSceneMembership
    {
        private PlayerSceneContext current;
        public PlayerSceneContext Current => PlayerSceneContext.Copy(current);
        public bool IsAssigned => current != null;
        public bool TravelPending => current?.TravelPending ?? false;
        public void Reset() => current = null;

        // An identical repeated assignment is harmless. A stale generation or a
        // changed payload under an existing generation is an authority error.
        public bool Observe(PlayerSceneContext value)
        {
            PlayerSceneContext.Validate(value);
            if (current != null)
            {
                if (value.PlayerId != current.PlayerId || value.Generation < current.Generation)
                    throw new InvalidDataException("Stale player scene assignment.");
                if (value.Generation == current.Generation)
                {
                    if (value.Scene != current.Scene || value.PropertyId != current.PropertyId ||
                        value.TravelPending != current.TravelPending || value.Epoch != current.Epoch)
                        throw new InvalidDataException("Player scene assignment changed within a generation.");
                    return false;
                }
            }
            current = PlayerSceneContext.Copy(value); return true;
        }
        public bool TryAcceptPose(PlayerPose pose) => current != null && !current.TravelPending &&
            DedicatedProtocol.PoseValid(pose) && pose.PlayerId == current.PlayerId && pose.ContextGeneration == current.Generation && pose.PropertyId == current.PropertyId &&
            (pose.Scene == current.Scene || (!pose.Active && pose.Scene.Length == 0));

        public bool AllowsInteraction(InteractionRequest request)
        {
            if (current == null || request == null || request.PlayerId != current.PlayerId || request.ContextGeneration != current.Generation) return false;
            if (!current.TravelPending) return true;
            // Only the already-issued route may progress while movement and
            // transactions are fenced. Worker still validates phase and result.
            return request.TargetId == current.Epoch && IsTransitionAcknowledgement(request.Kind);
        }
        public static bool IsTransitionAcknowledgement(string kind) => kind == "group-travel-prepared" || kind == "group-travel-arrived" ||
            kind == "group-apartment-prepared" || kind == "group-apartment-arrived" || kind == PlayerMapRestore.AcknowledgementKind;
    }
}
