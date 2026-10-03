using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using NivalisTogether.Dedicated;

public static class IndependentTransitionsProbe
{
    private static int checks;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            foreach (string dir in new[] { "BepInEx/interop", "BepInEx/core" }) {
                string file = Path.Combine(root, dir, name.Name + ".dll");
                if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
            }
            return null;
        };
        try { Run(); Console.WriteLine("PASS: " + checks + " independent transition assertions; no native game calls."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        string a = Id(), b = Id(), ea = Id(), eb = Id(), apartment = Id();
        var travelA = new GroupTravelBarrier(ea, new[] { a }, 10);
        var travelB = new GroupTravelBarrier(eb, new[] { b }, 10, true);
        Check(!travelA.Acknowledge(b, ea, false), "another player cannot prepare actor A's route");
        Check(!travelA.Acknowledge(a, eb, false), "another player's epoch cannot prepare actor A");
        Check(!travelA.Acknowledge(a, ea, true), "arrival cannot bypass preparation");
        Check(travelA.Acknowledge(a, ea, false), "actor A prepares independently");
        travelA.BeginWorker(11); travelA.BeginArrivals(11);
        Check(travelB.Phase == "prepare" && !travelB.AllPrepared, "actor A departure does not advance B");
        Check(travelA.Acknowledge(a, ea, true) && travelA.AllArrived, "actor A completes without B");
        Check(!travelA.Acknowledge(a, ea, true), "replayed actor arrival is idempotent");
        Check(travelB.Acknowledge(b, eb, false), "actor B retains its preparation");
        travelB.BeginWorker(12); travelB.BeginArrivals(12);
        Check(travelB.Deadline == 235, "cab deadline is bounded independently");
        Check(travelB.Acknowledge(b, eb, true) && travelB.AllArrived, "actor B completes after A");

        var doorA = new ApartmentBarrier(ea, new[] { a }, 0);
        var doorB = new ApartmentBarrier(eb, new[] { b }, 0);
        Check(!doorA.CanAcknowledge(b, ea, false), "another player cannot approve apartment entry");
        Check(doorA.Acknowledge(a, ea, false), "actor A prepares apartment");
        doorA.BeginWorker(1); doorA.BeginArrivals(1);
        Check(!doorB.AllPrepared && doorB.Phase == "prepare", "apartment A does not block or advance B");
        Check(doorA.Acknowledge(a, ea, true) && doorA.AllArrived, "apartment entry finishes independently");
        Reject(() => doorB.BeginWorker(16), "one player's expired apartment preparation is bounded");
        Check(doorA.AllArrived, "another player's timeout cannot roll back finished entry");

        var travel = new GroupTravelEvent { Sequence = 1, Epoch = ea, PlayerId = a, Participants = new[] { a },
            RequestId = Id(), PortalId = "native-portal", PortalName = "Meridian Market - Train station Eastern", TargetLocationId = "native-meridian-location",
            FromScene = "1_Lowtown", ToScene = "2_Meridian", IsCabTravel = true };
        var door = new GroupApartmentEvent { Sequence = 1, Epoch = ea, PlayerId = a, Participants = new[] { a },
            RequestId = Id(), ApartmentId = apartment, Scene = "1_Lowtown", Enter = true };
        foreach (string phase in new[] { "prepare", "depart", "complete", "cancel", "heartbeat" }) {
            travel.Phase = phase; TravelReplication.ValidateWire(travel); checks++;
            travel.PlayerId = ""; Reject(() => TravelReplication.ValidateWire(travel), "travel cannot broadcast " + phase); travel.PlayerId = a;
            travel.Participants = new[] { a, b }; Reject(() => TravelReplication.ValidateWire(travel), "travel cannot drag a second player " + phase);
            travel.Participants = new[] { b }; Reject(() => TravelReplication.ValidateWire(travel), "travel recipient must match participant " + phase); travel.Participants = new[] { a };
        }
        foreach (string phase in new[] { "prepare", "execute", "complete", "cancel", "heartbeat" }) {
            door.Phase = phase; ApartmentProtocol.Validate(door); checks++;
            door.PlayerId = ""; Reject(() => ApartmentProtocol.Validate(door), "apartment cannot broadcast " + phase); door.PlayerId = a;
            door.Participants = new[] { a, b }; Reject(() => ApartmentProtocol.Validate(door), "apartment cannot drag a second player " + phase);
            door.Participants = new[] { b }; Reject(() => ApartmentProtocol.Validate(door), "apartment recipient must match participant " + phase); door.Participants = new[] { a };
        }
        travel.FailedPlayers = new[] { b }; Reject(() => TravelReplication.ValidateWire(travel), "travel cannot mark nonparticipant failed"); travel.FailedPlayers = Array.Empty<string>();
        door.FailedPlayers = new[] { b }; Reject(() => ApartmentProtocol.Validate(door), "apartment cannot mark nonparticipant failed"); door.FailedPlayers = Array.Empty<string>();
        string portalName = travel.PortalName, locationId = travel.TargetLocationId;
        travel.PortalName = ""; Reject(() => TravelReplication.ValidateWire(travel), "GUID-only route cannot omit native asset name");
        travel.PortalName = new string('p', 129); Reject(() => TravelReplication.ValidateWire(travel), "portal asset names are bounded");
        travel.PortalName = "bad\nname"; Reject(() => TravelReplication.ValidateWire(travel), "portal asset control characters rejected"); travel.PortalName = portalName;
        travel.TargetLocationId = ""; Reject(() => TravelReplication.ValidateWire(travel), "route requires exact native location");
        travel.TargetLocationId = new string('l', 129); Reject(() => TravelReplication.ValidateWire(travel), "target location identity is bounded"); travel.TargetLocationId = locationId;
        var intent = new GroupTravelIntent { ToScene = travel.ToScene, PortalName = portalName, TargetLocationId = locationId };
        TravelReplication.ValidateIntent(intent); checks++;
        intent.PortalName = ""; Reject(() => TravelReplication.ValidateIntent(intent), "request cannot select first GUID match"); intent.PortalName = portalName;
        intent.ToScene = "bad\nscene"; Reject(() => TravelReplication.ValidateIntent(intent), "request destination control characters rejected"); intent.ToScene = travel.ToScene;
        intent.TargetLocationId = ""; Reject(() => TravelReplication.ValidateIntent(intent), "request must qualify native target location");
        var copiedTravel = (GroupTravelEvent)typeof(TravelReplication).GetMethod("Copy", Private).Invoke(null, new object[] { travel, "depart", a, "test" });
        Check(copiedTravel.PortalName == portalName && copiedTravel.TargetLocationId == locationId && GroupTravelNativeArrival.SameRoute(travel, copiedTravel), "all transition phases retain exact native asset identity");
        copiedTravel.PortalName = "Meridian Market - Train station";
        Check(!GroupTravelNativeArrival.SameRoute(travel, copiedTravel), "same GUID and district cannot change eastern train arrival to western arrival"); copiedTravel.PortalName = portalName;
        copiedTravel.TargetLocationId = "dev-room-location";
        Check(!GroupTravelNativeArrival.SameRoute(travel, copiedTravel), "same portal GUID cannot change native target location");

        var emitted = new List<WorldEvent>();
        TravelReplication.Configure(true, Id(), _ => {}, emitted.Add, _ => {}, _ => {});
        ApartmentReplication.Configure(true, Id(), _ => {}, emitted.Add, _ => {}, _ => {}, () => false);
        typeof(TravelReplication).GetMethod("PublishCopy", Private).Invoke(null, new object[] { travel });
        typeof(ApartmentReplication).GetMethod("Publish", Private).Invoke(null, new object[] { door });
        Check(emitted.Count == 2, "both production publishers emit exactly once");
        foreach (var item in emitted) {
            Check(item.TargetPlayerId == a, "production envelope is private before overlap filtering");
            Check(item.Data.GetProperty("PlayerId").GetString() == a, "payload and envelope identities agree");
            Check(item.Data.GetProperty("Participants").GetArrayLength() == 1, "production envelope keeps one participant");
        }
        CheckRegistryDisconnect(typeof(TravelReplication), a, b, ea, eb, travel, null);
        CheckRegistryDisconnect(typeof(ApartmentReplication), a, b, ea, eb, null, door);
        var copiedDoor = (GroupApartmentEvent)typeof(ApartmentReplication).GetMethod("Copy", Private).Invoke(null, new object[] { door, "complete", "done" });
        Check(copiedDoor.PlayerId == a && copiedDoor.Scene == door.Scene, "apartment copies retain actor and map identity");
        Check(copiedDoor.SourceProperty == "" && copiedDoor.DestinationProperty == apartment, "apartment property identity derives from retained door direction");

        var native = new GroupTravelNativeArrival(ea, "portal", "2_Meridian", "location", true);
        Check(!native.IsComplete(true, "2_Meridian", false, false), "scene loaded alone cannot finish travel");
        Check(!native.AuthorizeInitial(ea, "portal", "2_Meridian", false, false, false, true), "cab flag cannot be forged");
        Check(native.AuthorizeInitial(ea, "portal", "2_Meridian", true, false, false, true), "exact native request permitted once");
        Check(!native.AuthorizeInitial(ea, "portal", "2_Meridian", true, false, false, true), "native initial replay rejected");
        Check(!native.Observe(eb, "2_Meridian", "location"), "another player's epoch cannot satisfy native arrival");
        Check(native.Observe(ea, "2_Meridian", "location"), "matching native event observed");
        Check(!native.IsComplete(true, "2_Meridian", true, false), "native cinematic lock delays completion");
        Check(native.IsComplete(true, "2_Meridian", false, false), "native arrival completes after lock release");
        CheckAdditionalTravelModes(a, b, ea, travel);
        var apartmentPermit = new ApartmentNativePermit(ea, apartment, true);
        Check(!apartmentPermit.Consume(eb, apartment, true), "other player's apartment epoch rejected");
        Check(apartmentPermit.Consume(ea, apartment, true), "matching apartment invocation permitted");
        Check(apartmentPermit.ObserveContext(ea, apartment, true), "matching native apartment context observed");
        Check(!apartmentPermit.ObserveArrival(true, false), "door arrival needs actual placement");
        Check(apartmentPermit.ObserveArrival(true, true), "native apartment placement completes");

        // No active isolated authority: the changes leave solo native interaction intact.
        Check((bool)typeof(TravelReplication).GetMethod("LoadAreaPrefix", Private).Invoke(null, new object[] { "any", "scene", false, false }), "solo scene loads stay native");
        Check((bool)typeof(ApartmentReplication).GetMethod("DoorPrefix", Private).Invoke(null, new object[] { null }), "solo apartment doors stay native");
        TravelReplication.BeginPlayerTransition = (_, __) => {}; TravelReplication.CancelPlayerTransition = (_, __) => {};
        TravelReplication.CaptureBoatArrival = () => null;
        TravelReplication.FailPlayerTransition = (_, __) => {}; TravelReplication.CommitPlayerContext = (_, __, ___, ____) => {};
        ApartmentReplication.BeginPlayerTransition = (_, __) => {}; ApartmentReplication.CancelPlayerTransition = (_, __) => {};
        ApartmentReplication.FailPlayerTransition = (_, __) => {}; ApartmentReplication.CommitPlayerContext = (_, __, ___, ____) => {};
        TravelReplication.Stop(); ApartmentReplication.Stop();
        Check(TravelReplication.BeginPlayerTransition == null && TravelReplication.CancelPlayerTransition == null &&
            TravelReplication.FailPlayerTransition == null && TravelReplication.CommitPlayerContext == null && TravelReplication.CaptureBoatArrival == null, "travel session stop discards location callbacks");
        Check(ApartmentReplication.BeginPlayerTransition == null && ApartmentReplication.CancelPlayerTransition == null &&
            ApartmentReplication.FailPlayerTransition == null && ApartmentReplication.CommitPlayerContext == null, "apartment session stop discards location callbacks");
    }
    private static void CheckAdditionalTravelModes(string player, string other, string epoch, GroupTravelEvent original)
    {
        string boatId = Id(), accountId = Id();
        GroupTravelEvent Route() => JsonSerializer.Deserialize<GroupTravelEvent>(JsonSerializer.Serialize(original));
        var route = Route(); route.IsCabTravel = false; route.IsBoatTravel = true; route.BoatId = boatId;
        TravelReplication.ValidateWire(route); checks++;
        route.IsMapTravel = true; Reject(() => TravelReplication.ValidateWire(route), "boat mode cannot also request unrestricted map travel"); route.IsMapTravel = false;
        route.IsCabTravel = true; Reject(() => TravelReplication.ValidateWire(route), "boat mode cannot borrow cab routing"); route.IsCabTravel = false;
        route.BoatId = other + "x"; Reject(() => TravelReplication.ValidateWire(route), "boat identity must be exact account boat identifier"); route.BoatId = boatId;
        route.IsBoatTravel = false; Reject(() => TravelReplication.ValidateWire(route), "walking mode cannot smuggle a boat identity"); route.BoatId = "";
        route.IsMapTravel = true; TravelReplication.ValidateWire(route); checks++;
        route.IsCabTravel = true; Reject(() => TravelReplication.ValidateWire(route), "map mode cannot bypass cab flags"); route.IsCabTravel = false;
        var copy = (GroupTravelEvent)typeof(TravelReplication).GetMethod("Copy", Private).Invoke(null, new object[] { route, "depart", player, "test" });
        Check(copy.IsMapTravel && GroupTravelNativeArrival.SameRoute(route, copy), "map mode survives private phase copies");
        copy.IsMapTravel = false; Check(!GroupTravelNativeArrival.SameRoute(route, copy), "epoch cannot switch map request to terminal request");
        route.IsMapTravel = false; route.IsBoatTravel = true; route.BoatId = boatId;
        copy = (GroupTravelEvent)typeof(TravelReplication).GetMethod("Copy", Private).Invoke(null, new object[] { route, "depart", player, "test" });
        Check(copy.IsBoatTravel && copy.BoatId == boatId && GroupTravelNativeArrival.SameRoute(route, copy), "boat ownership survives private phase copies");
        copy.BoatId = Id(); Check(!GroupTravelNativeArrival.SameRoute(route, copy), "epoch cannot switch personal boats");
        var intent = new GroupTravelIntent { ToScene = route.ToScene, PortalName = route.PortalName, TargetLocationId = route.TargetLocationId, IsBoatTravel = true, BoatId = boatId };
        TravelReplication.ValidateIntent(intent); checks++;
        intent.IsMapTravel = true; Reject(() => TravelReplication.ValidateIntent(intent), "request cannot combine map and boat modes"); intent.IsMapTravel = false;
        intent.BoatId = ""; Reject(() => TravelReplication.ValidateIntent(intent), "boat request must identify its owned boat"); intent.BoatId = boatId;
        intent.IsBoatTravel = false; Reject(() => TravelReplication.ValidateIntent(intent), "ordinary terminal request cannot carry boat identifier");
        var barrier = new GroupTravelBarrier(epoch, new[] { player }, 0, false, true);
        Check(barrier.FinalDeadline == GroupTravelBarrier.CabTotalSeconds, "boat load and cockpit restoration have bounded native-sequence deadline");
        Reject(() => new GroupTravelBarrier(epoch, new[] { player }, 0, true, true), "barrier rejects conflicting native transport modes");
        var arrival = new GroupTravelNativeArrival(epoch, "p", "scene", "l", false, true);
        Check(!arrival.AuthorizeInitial(epoch, "p", "scene", false, false, false, true), "boat epoch cannot start as walking travel");
        Check(!arrival.AuthorizeInitial(epoch, "p", "scene", false, true, true, true), "boat epoch cannot force reload");
        Check(arrival.AuthorizeInitial(epoch, "p", "scene", false, false, true, true), "exact boat mode starts native sequence once");
        Check(!arrival.AuthorizeInitial(epoch, "p", "scene", false, false, true, true), "boat native start replay rejected");
        Check(arrival.AuthorizeContinuation(epoch, "p", "scene", false, false, true, true), "boat continuation consumes exact mode once");
        Check(!arrival.AuthorizeContinuation(epoch, "p", "scene", false, false, true, true), "boat native continuation replay rejected");
        IntPtr boatPointer = new IntPtr(100), portalPointer = new IntPtr(200), foreignPointer = new IntPtr(300);
        var nativeNullBoat = new GroupTravelNativeArrival(epoch, "p", "scene", "l", false, true);
        bool ExactNativeBoat(IntPtr argument, string argumentId, IntPtr sequenceBoat, string sequenceId, IntPtr sequencePortal) =>
            nativeNullBoat.MatchesNativeBoatArgument(boatPointer, boatId, argument, argumentId, sequenceBoat, sequenceId, portalPointer, sequencePortal);
        Check(ExactNativeBoat(IntPtr.Zero, "", boatPointer, boatId, portalPointer), "actual native null boat argument retains exact active boat sequence identity");
        Check(ExactNativeBoat(IntPtr.Zero, "", boatPointer, boatId, portalPointer) &&
            nativeNullBoat.AuthorizeInitial(epoch, "p", "scene", false, false, nativeNullBoat.IsBoatTravel, true), "native Start null-argument request reaches authorized initial departure");
        Check(!nativeNullBoat.AuthorizeInitial(epoch, "p", "scene", false, false, true, true), "null-argument boat start still consumes exactly one initial request");
        Check(ExactNativeBoat(IntPtr.Zero, "", boatPointer, boatId, portalPointer) &&
            nativeNullBoat.AuthorizeContinuation(epoch, "p", "scene", false, false, nativeNullBoat.IsBoatTravel, true), "native null-argument continuation retains authorized boat journey");
        Check(!nativeNullBoat.AuthorizeContinuation(epoch, "p", "scene", false, false, true, true), "null-argument continuation cannot replay");
        Check(!ExactNativeBoat(IntPtr.Zero, "", foreignPointer, boatId, portalPointer), "foreign native sequence cannot authorize a null boat argument");
        Check(!ExactNativeBoat(IntPtr.Zero, "", boatPointer, other, portalPointer), "sequence cannot swap authenticated boat identity");
        Check(!ExactNativeBoat(IntPtr.Zero, "", boatPointer, boatId, foreignPointer), "sequence cannot change native destination portal");
        Check(!ExactNativeBoat(IntPtr.Zero, "", IntPtr.Zero, "", portalPointer), "missing or cleared native sequence cannot authorize a null boat argument");
        Check(!ExactNativeBoat(foreignPointer, other, boatPointer, boatId, portalPointer), "foreign explicit boat argument remains rejected");
        Check(ExactNativeBoat(boatPointer, boatId, boatPointer, boatId, portalPointer), "matching explicit boat argument remains bound to exact native sequence");
        var nativeWalking = new GroupTravelNativeArrival(epoch, "p", "scene", "l", false);
        Check(!nativeWalking.MatchesNativeBoatArgument(IntPtr.Zero, "", boatPointer, boatId, boatPointer, boatId, portalPointer, portalPointer), "nonboat journey cannot carry a boat argument");
        Check(nativeWalking.MatchesNativeBoatArgument(IntPtr.Zero, "", IntPtr.Zero, "", boatPointer, boatId, portalPointer, portalPointer), "ordinary walking null argument remains independent of any unrelated boat sequence");
        Reject(() => new GroupTravelNativeArrival(epoch, "p", "scene", "l", true, true), "native arrival rejects cab/boat ambiguity");
        var request = new InteractionRequest { PlayerId = player, AccountId = accountId, ContextGeneration = 8 };
        var ack = new GroupTravelAcknowledgement { Success = true, Scene = route.ToScene, Boat = new BoatPose {
            PlayerId = player, AccountId = accountId, BoatId = boatId, ContextGeneration = 8, Sequence = 12,
            Scene = route.ToScene, Qw = 1, Fuel = 18, Driving = true, Boarded = true } };
        TravelReplication.ValidateArrivalBoat(route, request, ack); checks++;
        ack.Boat.PlayerId = other; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "boat ACK cannot move another player"); ack.Boat.PlayerId = player;
        ack.Boat.AccountId = other; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "boat ACK cannot move another account asset"); ack.Boat.AccountId = accountId;
        ack.Boat.BoatId = other; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "boat ACK cannot swap boat after acceptance"); ack.Boat.BoatId = boatId;
        ack.Boat.ContextGeneration--; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "stale source-generation boat ACK rejected"); ack.Boat.ContextGeneration++;
        ack.Boat.Scene = route.FromScene; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "boat ACK must reach authorized destination"); ack.Boat.Scene = route.ToScene;
        ack.Boat.Driving = false; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "boat ACK waits for native cockpit return"); ack.Boat.Driving = true;
        ack.Boat.Fuel = float.NaN; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "nonfinite arrival fuel rejected"); ack.Boat.Fuel = 18;
        route.IsBoatTravel = false; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "walking ACK cannot modify an account boat");
        ack.Boat = null; TravelReplication.ValidateArrivalBoat(route, request, ack); checks++;
        route.IsBoatTravel = true; Reject(() => TravelReplication.ValidateArrivalBoat(route, request, ack), "missing boat arrival cannot commit boat travel");
    }
    private static void CheckRegistryDisconnect(Type owner, string a, string b, string ea, string eb, GroupTravelEvent travel, GroupApartmentEvent apartment)
    {
        var operations = (IDictionary)owner.GetField("operations", Private).GetValue(null);
        var operationType = owner.GetNestedType("PlayerOperation", BindingFlags.NonPublic);
        object first = Activator.CreateInstance(operationType, true), second = Activator.CreateInstance(operationType, true);
        operations[a] = first; operations[b] = second;
        owner.GetMethod("SetPlayerPresent").Invoke(null, new object[] { a, false });
        Check(!operations.Contains(a), owner.Name + " discards only departing player's pending operation");
        Check(ReferenceEquals(operations[b], second), owner.Name + " leaves the other player's operation unchanged");
        operations.Clear();
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { checks++; try { action(); } catch { return; } throw new Exception("Expected rejection: " + message); }
}
