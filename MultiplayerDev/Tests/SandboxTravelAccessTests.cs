using System;
using System.Collections.Generic;

namespace Nivalis
{
    public sealed class WorldLocation
    {
        public IntPtr Pointer;
        public string Guid = "world", ScenePath = "Assets/_Scenes/public.unity";
        public int SceneIndex = 1;
        public PortalKey DefaultLocation;
    }
    public sealed class PortalKey
    {
        public string Guid = "portal";
        public object Apartment;
        public WorldLocation TargetLocation;
        public int SceneIndex = 1;
    }
    public sealed class TravelUnlockManager { public static TravelUnlockManager Instance; public IntPtr Pointer; }
    public sealed class SceneTransitionInteractable { public PortalKey keyTo; }
    public sealed class MapTile { public PortalKey PortalKey; }
    public sealed class PlayerManager
    {
        public static PlayerManager Instance;
        public Player LocalPlayer;
        public sealed class Player { public bool IsHoldingObject; }
    }
}

namespace NivalisTogether.Dedicated
{
    public static partial class MultiplayerSandboxRuntime
    {
        private static int checks;
        private static void Check(bool pass, string message) { checks++; if (!pass) throw new Exception(message); }
        public static int Run()
        {
            var location = new Nivalis.WorldLocation { Pointer = (IntPtr)11 };
            var key = new Nivalis.PortalKey { TargetLocation = location };
            location.DefaultLocation = key;
            var terminal = new Nivalis.SceneTransitionInteractable { keyTo = key };
            var tile = new Nivalis.MapTile { PortalKey = key };
            Nivalis.TravelUnlockManager.Instance = new Nivalis.TravelUnlockManager { Pointer = (IntPtr)21 };
            Nivalis.PlayerManager.Instance = new Nivalis.PlayerManager { LocalPlayer = new Nivalis.PlayerManager.Player() };
            accessManager = (IntPtr)21;
            PublicLocations.Add(location.Pointer);
            Check(IsPublicLocationDefinition(location), "canonical public district accepted");
            Check(!IsPublicLocationDefinition(null), "null world excluded");
            location.SceneIndex = -1; Check(!IsPublicLocationDefinition(location), "developer/unloadable map excluded"); location.SceneIndex = 1;
            location.Guid = " "; Check(!IsPublicLocationDefinition(location), "unnamed location excluded"); location.Guid = "world";
            location.ScenePath = ""; Check(!IsPublicLocationDefinition(location), "missing scene excluded"); location.ScenePath = "public";
            location.DefaultLocation = null; Check(!IsPublicLocationDefinition(location), "no canonical arrival excluded"); location.DefaultLocation = key;
            key.Apartment = new object(); Check(!IsPublicLocationDefinition(location), "private default apartment excluded"); key.Apartment = null;
            key.TargetLocation = null; Check(!IsPublicLocationDefinition(location), "missing destination excluded"); key.TargetLocation = location;
            key.TargetLocation = new Nivalis.WorldLocation { Pointer = (IntPtr)12 }; Check(!IsPublicLocationDefinition(location), "default key pointing elsewhere excluded"); key.TargetLocation = location;
            key.SceneIndex = 2; Check(!IsPublicLocationDefinition(location), "wrong arrival scene excluded"); key.SceneIndex = 1;

            Active = false;
            bool result = false;
            Check(!IsPublicTravelDestination(key), "solo destination policy untouched");
            Check(PublicTerminalAccess(terminal, ref result) && !result, "solo terminal falls through unchanged");
            Check(PublicMapAccess(tile, ref result) && !result, "solo first-visit map policy unchanged");
            Active = true;
            Check(IsPublicTravelDestination(key), "isolated public route allowed");
            Check(!PublicTerminalAccess(terminal, ref result) && result, "public access flag bypassed");
            result = false; Check(!PublicMapAccess(tile, ref result) && result, "public unvisited map is available");
            Nivalis.PlayerManager.Instance.LocalPlayer.IsHoldingObject = true;
            Check(!PublicTerminalAccess(terminal, ref result) && !result, "held-object transition guard preserved");
            Nivalis.PlayerManager.Instance.LocalPlayer = null;
            Check(!PublicTerminalAccess(terminal, ref result) && !result, "missing player cannot enter terminal");
            Nivalis.PlayerManager.Instance = null;
            Check(!PublicTerminalAccess(terminal, ref result) && !result, "missing manager cannot enter terminal");
            Check(PublicTerminalAccess(null, ref result) && !result, "missing terminal follows native path");
            Check(PublicMapAccess(null, ref result) && !result, "missing map tile follows native path");
            key.Apartment = new object();
            Check(!IsPublicTravelDestination(key), "apartment access excluded");
            Check(PublicTerminalAccess(terminal, ref result) && !result, "private terminal keeps native access");
            Check(PublicMapAccess(tile, ref result) && !result, "private map keeps native access");
            key.Apartment = null;
            key.TargetLocation = new Nivalis.WorldLocation { Pointer = (IntPtr)12, Guid = location.Guid };
            Check(!IsPublicTravelDestination(key), "duplicate location GUID cannot claim canonical reference");
            key.TargetLocation = location;
            key.Guid = ""; Check(!IsPublicTravelDestination(key), "empty portal identifier excluded"); key.Guid = "portal";
            key.SceneIndex = 2; Check(!IsPublicTravelDestination(key), "key cannot redirect public target to another scene"); key.SceneIndex = 1;
            Check(!IsPublicTravelDestination(null), "null key excluded");
            key.TargetLocation = null; Check(!IsPublicTravelDestination(key), "null target excluded"); key.TargetLocation = location;
            location.SceneIndex = key.SceneIndex = -1;
            Check(!IsPublicTravelDestination(key), "unloadable key rejected even if stale canonical reference remains");
            location.SceneIndex = key.SceneIndex = 1;
            Nivalis.TravelUnlockManager.Instance.Pointer = (IntPtr)22;
            Check(!IsPublicTravelDestination(key), "new world manager cannot reuse old membership");
            Nivalis.TravelUnlockManager.Instance = null;
            Check(!IsPublicTravelDestination(key), "no manager cannot reuse access");
            Nivalis.TravelUnlockManager.Instance = new Nivalis.TravelUnlockManager { Pointer = (IntPtr)21 };
            PublicLocations.Clear(); Check(!IsPublicTravelDestination(key), "reset membership revokes policy");
            PublicLocations.Add(location.Pointer); accessManager = IntPtr.Zero;
            Check(!IsPublicTravelDestination(key), "before normalization no access override");
            accessManager = (IntPtr)21; Active = false;
            result = true;
            Check(PublicMapAccess(tile, ref result) && result, "disconnect restores native execution without overwriting original result");
            return checks;
        }
    }
}
