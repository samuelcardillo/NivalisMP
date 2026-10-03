using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using UnityEngine;
using WanderGhost = Nivalis.GhostSystem.WanderPoint.WanderPointGhost;

namespace NivalisTogether.Dedicated
{
    /// <summary>Keep native wander routes continuous in maps observed by multiplayer clients.</summary>
    public static class WorkerNpcSimulation
    {
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.worker-npc-routes");
        private static readonly Dictionary<IntPtr, SelectionEdge> Pending = new Dictionary<IntPtr, SelectionEdge>();
        private static readonly HashSet<int> ReportedScenes = new HashSet<int>();
        private static readonly NpcDiscontinuityLog Notices = new NpcDiscontinuityLog();
        private static readonly NpcDiscontinuityLog Transfers = new NpcDiscontinuityLog();
        private static readonly NpcDiscontinuityLog Moves = new NpcDiscontinuityLog();
        private static readonly NpcDiscontinuityLog SleepMoves = new NpcDiscontinuityLog();
        private static readonly Dictionary<IntPtr, ObservedMove> ObservedMoves = new Dictionary<IntPtr, ObservedMove>();
        private static readonly Dictionary<IntPtr, ResumedMove> ResumedMoves = new Dictionary<IntPtr, ResumedMove>();
        private static Func<int, bool> sceneOccupied;
        private static bool patched;
        private static SelectionScope selection;
        private static TickScope tick;
        private static MoveScope moveScope;
        private static SleepSelectionScope sleepSelection;
        public static Action<string> Diagnostic { get; set; }
        private sealed class SelectionScope
        {
            internal SelectionScope Previous;
            internal AgentGhost Agent;
            internal WanderGhost From;
            internal bool Protected;
        }
        private sealed class SelectionEdge
        {
            internal string AgentId;
            internal WanderGhost From, To;
            internal int Scene;
        }
        private sealed class TickScope
        {
            internal TickScope Previous;
            internal AgentGhost Agent;
            internal WanderAgentAction.State State;
            internal WanderGhost ReservedPoint;
            internal int ReservedTraffic;
            internal bool Restored;
        }
        private sealed class MoveScope
        {
            internal MoveScope Previous;
            internal SleepSelectionScope SuspendedSleep;
            internal AgentGhost Agent;
            internal ActionState State;
            internal string AgentId, TargetId, TargetType;
            internal int Scene;
            internal IntPtr OldCommand;
            internal bool Protected, Used;
        }
        private enum MoveTargetKind { Wander, WorldPoint, IdlePoint, PublicTask }
        private sealed class ObservedMove
        {
            internal string AgentId, TargetId;
            internal int Scene, SubActionIndex;
            internal IntPtr State, Command;
        }
        private sealed class ResumedMove
        {
            internal string AgentId;
            internal int Scene;
            internal IntPtr Ghost, Character, State, Command;
        }
        private sealed class SleepSelectionScope
        {
            internal SleepSelectionScope Previous;
            internal MoveScope SuspendedMove;
            internal IntPtr Agent;
            internal bool Protected;
        }
        private sealed class SleepTrace
        {
            internal AgentGhost Agent;
            internal SleepAgentAction.State State;
            internal Vector3 Position;
            internal int Scene;
            internal bool Viewless;
            internal string Stage;
        }
        private static bool Worker => sceneOccupied != null && DedicatedWorldRuntime.IsDedicatedServer && DedicatedWorldRuntime.IsIsolated;
        public static void Configure(Func<int, bool> isSceneOccupied)
        {
            Reset(); sceneOccupied = isSceneOccupied ?? throw new ArgumentNullException(nameof(isSceneOccupied));
            if (!patched) InstallPatches();
        }
        public static void Reset() { sceneOccupied = null; selection = null; tick = null; moveScope = null; sleepSelection = null; Pending.Clear(); ObservedMoves.Clear(); ResumedMoves.Clear(); ReportedScenes.Clear(); Notices.Reset(); Transfers.Reset(); Moves.Reset(); SleepMoves.Reset(); }
        private static void InstallPatches()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            // IL2CPP exposes inherited ActionState overloads alongside each
            // concrete subaction. Resolve the audited declaration and signature.
            var next = typeof(WanderAgentAction).GetMethod(nameof(WanderAgentAction.NextWaypoint), flags, null, new[] { typeof(AgentGhost), typeof(WanderGhost) }, null);
            var append = typeof(WanderGhost).GetMethod(nameof(WanderGhost.FillNextTeleportLocations), flags, null, new[] { typeof(Il2CppSystem.Collections.Generic.List<WanderGhost>), typeof(Vector3), typeof(float) }, null);
            var fill = typeof(WanderGhost).GetMethod(nameof(WanderGhost.FillNextLocations), flags, null, new[] { typeof(Il2CppSystem.Collections.Generic.List<WanderGhost>), typeof(Vector3), typeof(float), typeof(int) }, null);
            var give = typeof(WanderAgentAction.WanderToSubAction).GetMethod(nameof(WanderAgentAction.WanderToSubAction.GiveViewMoveCommand), flags, null, new[] { typeof(AgentGhost), typeof(WanderAgentAction.State), typeof(IWorldLocation), typeof(bool) }, null);
            var update = typeof(WanderAgentAction.WanderToSubAction).GetMethod(nameof(WanderAgentAction.WanderToSubAction.Tick), flags, null, new[] { typeof(AgentGhost), typeof(WanderAgentAction.State), typeof(float) }, null);
            var transfer = typeof(GhostManager).GetMethod(nameof(GhostManager.TransferGhostToScene), flags, null, new[] { typeof(Ghost), typeof(int), typeof(PositionRotation) }, null);
            var skip = typeof(AgentGhostSimulator).GetMethod(nameof(AgentGhostSimulator.SkipTime), flags, null, new[] { typeof(float), typeof(bool) }, null);
            var visit = typeof(VisitVenueAgentActionType.Teleport).GetMethod(nameof(VisitVenueAgentActionType.Teleport.Enter), flags, null, new[] { typeof(AgentGhost), typeof(VisitVenueAgentActionType.State) }, null);
            var delivery = typeof(DeliverIngredientsActionType.TeleportIfNotInScene).GetMethod(nameof(DeliverIngredientsActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(DeliverIngredientsActionType.State) }, null);
            var idle = typeof(IdleAgentAction.TeleportIfScheduleDemandsIt).GetMethod(nameof(IdleAgentAction.TeleportIfScheduleDemandsIt.Enter), flags, null, new[] { typeof(AgentGhost), typeof(IdleAgentAction.State) }, null);
            var workplace = typeof(IdleAtWorkplaceActionType.TeleportIfNotInScene).GetMethod(nameof(IdleAtWorkplaceActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(IdleAtWorkplaceActionType.State) }, null);
            var cleanupMeal = typeof(CleanupMealAgentActionType.TeleportIfNotInScene).GetMethod(nameof(CleanupMealAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(CleanupMealAgentActionType.State) }, null);
            var cleanupStorage = typeof(CleanupStorageAgentActionType.TeleportIfNotInScene).GetMethod(nameof(CleanupStorageAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(CleanupStorageAgentActionType.State) }, null);
            var cleanupTrash = typeof(CleanupTrashAgentActionType.TeleportIfNotInScene).GetMethod(nameof(CleanupTrashAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(CleanupTrashAgentActionType.State) }, null);
            var makeMeal = typeof(MakeMealAgentActionType.TeleportIfNotInScene).GetMethod(nameof(MakeMealAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(MakeMealAgentActionType.State) }, null);
            var manage = typeof(ManageAgentActionType.TeleportIfNotInScene).GetMethod(nameof(ManageAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(ManageAgentActionType.State) }, null);
            var manageComplaint = typeof(ManageComplaintAgentActionType.TeleportIfNotInScene).GetMethod(nameof(ManageComplaintAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(ManageComplaintAgentActionType.State) }, null);
            var processIngredient = typeof(ProcessIngredientAgentActionType.TeleportIfNotInScene).GetMethod(nameof(ProcessIngredientAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(ProcessIngredientAgentActionType.State) }, null);
            var serveMeal = typeof(ServeMealAgentActionType.TeleportIfNotInScene).GetMethod(nameof(ServeMealAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(ServeMealAgentActionType.State) }, null);
            var takeOrder = typeof(TakeOrderAgentActionType.TeleportIfNotInScene).GetMethod(nameof(TakeOrderAgentActionType.TeleportIfNotInScene.Enter), flags, null, new[] { typeof(AgentGhost), typeof(TakeOrderAgentActionType.State) }, null);
            var visible = typeof(AgentGhost).GetMethod("get_Visible", flags, null, Type.EmptyTypes, null);
            var actionUpdate = typeof(AgentGhostSimulator).GetMethod(nameof(AgentGhostSimulator.UpdateCurrentAgentAction), flags, null, new[] { typeof(AgentGhost), typeof(float) }, null);
            var sleepPick = typeof(SleepAgentAction).GetMethod(nameof(SleepAgentAction.TryGetValidInteractable), flags, null, new[] { typeof(AgentGhost), typeof(IAgentInteractable).MakeByRefType(), typeof(PersonSchedule) }, null);
            var sleepEnter = typeof(SleepAgentAction.SleepSubAction).GetMethod(nameof(SleepAgentAction.SleepSubAction.Enter), flags, null, new[] { typeof(AgentGhost), typeof(SleepAgentAction.State) }, null);
            var sleepExit = typeof(SleepAgentAction.SleepSubAction).GetMethod(nameof(SleepAgentAction.SleepSubAction.Exit), flags, null, new[] { typeof(AgentGhost), typeof(SleepAgentAction.State) }, null);
            var resume = typeof(Agent._SnapToGhostCoroutine_d__31).GetMethod(nameof(Agent._SnapToGhostCoroutine_d__31.MoveNext), flags, null, Type.EmptyTypes, null);
            var move = typeof(Character).GetMethod(nameof(Character.MoveToSpot), flags, null, new[] { typeof(MoveToSpotCommand) }, null);
            var disable = typeof(Agent).GetMethod(nameof(Agent.OnDisable), flags, null, Type.EmptyTypes, null);
            if (next == null || append == null || fill == null || give == null || update == null || transfer == null || skip == null ||
                cleanupMeal == null || cleanupStorage == null || cleanupTrash == null || makeMeal == null || manage == null || manageComplaint == null || processIngredient == null || serveMeal == null || takeOrder == null ||
                visit == null || delivery == null || idle == null || workplace == null || visible == null || actionUpdate == null || sleepPick == null || sleepEnter == null || sleepExit == null ||
                resume == null || move == null || disable == null) throw new MissingMethodException("Native NPC movement hooks are unavailable.");
            Patches.Patch(next, prefix: Hook(nameof(BeforeNextWaypoint)), postfix: Hook(nameof(AfterNextWaypoint)), finalizer: Hook(nameof(EndNextWaypoint)));
            Patches.Patch(append, prefix: Hook(nameof(AllowTeleportCandidates)));
            Patches.Patch(give, prefix: Hook(nameof(BeforeGiveMoveCommand)));
            Patches.Patch(update, prefix: Hook(nameof(BeforeWanderTick)), finalizer: Hook(nameof(EndWanderTick)));
            Patches.Patch(transfer, prefix: Hook(nameof(ObserveTransfer)));
            Patches.Patch(skip, prefix: Hook(nameof(ObserveSkipTime)));
            Patches.Patch(visit, prefix: Hook(nameof(BeforeVisitMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(delivery, prefix: Hook(nameof(BeforeDeliveryMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(idle, prefix: Hook(nameof(BeforeIdleMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(workplace, prefix: Hook(nameof(BeforeWorkplaceMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(cleanupMeal, prefix: Hook(nameof(BeforeCleanupMealMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(cleanupStorage, prefix: Hook(nameof(BeforeCleanupStorageMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(cleanupTrash, prefix: Hook(nameof(BeforeCleanupTrashMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(makeMeal, prefix: Hook(nameof(BeforeMakeMealMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(manage, prefix: Hook(nameof(BeforeManageMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(manageComplaint, prefix: Hook(nameof(BeforeManageComplaintMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(processIngredient, prefix: Hook(nameof(BeforeProcessIngredientMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(serveMeal, prefix: Hook(nameof(BeforeServeMealMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(takeOrder, prefix: Hook(nameof(BeforeTakeOrderMove)), postfix: Hook(nameof(AfterObservedMove)), finalizer: Hook(nameof(EndObservedMove)));
            Patches.Patch(visible, prefix: Hook(nameof(ObservedMoveVisibility)));
            Patches.Patch(actionUpdate, prefix: Hook(nameof(WaitForObservedMove)));
            Patches.Patch(sleepPick, prefix: Hook(nameof(BeforeSleepSelection)), finalizer: Hook(nameof(EndSleepSelection)));
            Patches.Patch(sleepEnter, prefix: Hook(nameof(BeforeSleepTransition)), postfix: Hook(nameof(AfterSleepTransition)));
            Patches.Patch(sleepExit, prefix: Hook(nameof(BeforeSleepTransition)), postfix: Hook(nameof(AfterSleepTransition)));
            Patches.Patch(resume, postfix: Hook(nameof(AfterViewResume)));
            Patches.Patch(move, prefix: Hook(nameof(BeforeResumedMove)));
            Patches.Patch(disable, prefix: Hook(nameof(ForgetResumedView)));
            patched = true;
        }
        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(WorkerNpcSimulation).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

        private static bool ResumeEligible(Agent view, Character character, MoveToSpotCommand command, out AgentGhost ghost)
        {
            ghost = view?.MyGhost;
            if (!Worker || ghost == null || !ghost.IsValid || ghost.IsViewLess || !sceneOccupied(ghost.SceneIndex) ||
                character == null || !character.Snap || character.Movement == null || view.Character?.Pointer != character.Pointer ||
                character.Agent?.Pointer != view.Pointer || !ghost.HasLoadedView || ghost.LoadedView?.Pointer != view.Pointer ||
                command == null || (int)command.Result != 0 || ghost.CharacterCommand?.Pointer != command.Pointer ||
                ghost.CurrentActionState?.CharacterCommand?.Pointer != command.Pointer || ghost.State.personSchedule?.teleport == true ||
                !float.IsFinite(command.PathProgress) || command.PathProgress <= 0 || command.PathProgress >= 1 ||
                !IsPublicMoveEndpoint(ghost, ghost.SceneIndex)) return false;
            var target = command.ghostReference?.Value;
            return target != null && IsPublicMoveEndpoint(target, ghost.SceneIndex);
        }
        private static void AfterViewResume(Agent._SnapToGhostCoroutine_d__31 __instance, bool __result)
        {
            if (!Worker || __result) return;
            try
            {
                var view = __instance.__4__this;
                if (view == null) return;
                ResumedMoves.Remove(view.Pointer);
                var character = view.Character;
                var command = view.MyGhost?.CharacterCommand?.TryCast<MoveToSpotCommand>();
                if (!ResumeEligible(view, character, command, out var ghost)) return;
                // The concrete native initializer has just copied the persistent
                // ghost's pose to this view and set Snap. Retain only identities:
                // a replacement action/command or pooled view must remain native.
                if (ResumedMoves.Count >= 30000) ResumedMoves.Clear();
                ResumedMoves[view.Pointer] = new ResumedMove { AgentId = ghost.Id, Scene = ghost.SceneIndex,
                    Ghost = ghost.Pointer, Character = character.Pointer, State = ghost.CurrentActionState.Pointer, Command = command.Pointer };
            }
            catch (Exception ex) { Notice("NPC_VIEW_RESUME observation failed: " + ex.Message); }
        }
        private static void BeforeResumedMove(Character __instance, MoveToSpotCommand __0)
        {
            if (!Worker || ResumedMoves.Count == 0 || __instance == null) return;
            try
            {
                var view = __instance.Agent;
                if (view == null || !ResumedMoves.TryGetValue(view.Pointer, out var resumed)) return;
                ResumedMoves.Remove(view.Pointer);
                if (!ResumeEligible(view, __instance, __0, out var ghost) ||
                    ghost.Id != resumed.AgentId || ghost.Pointer != resumed.Ghost || ghost.SceneIndex != resumed.Scene ||
                    __instance.Pointer != resumed.Character || ghost.CurrentActionState.Pointer != resumed.State || __0.Pointer != resumed.Command) return;
                var position = ghost.Position;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return;
                var old = __0.FromPosition; float progress = __0.PathProgress;
                // Native MoveToSpot uses FromPosition when Snap is true. Its old
                // origin/progress describe the whole offstage journey, so using
                // them on a newly loaded path rewinds the authoritative NPC.
                // Rebase the same command onto its remaining journey. Native
                // pathfinding, target/reservations and completion stay in charge.
                // Preserve the timer and vertical multiplier: resetting those
                // would apply the native speed multiplier a second time.
                __0.FromPosition = position;
                __0.PathProgress = 0;
                __instance.Movement.PathProgress = 0;
                Notice(FormattableString.Invariant($"NPC_VIEW_RESUME rebased id={ghost.Id} scene={ghost.SceneIndex} progress={progress:F4} old=({old.x:F3},{old.y:F3},{old.z:F3}) current=({position.x:F3},{position.y:F3},{position.z:F3})"));
            }
            catch (Exception ex) { Notice("NPC_VIEW_RESUME rebase failed: " + ex.Message); }
        }
        private static void ForgetResumedView(Agent __instance)
        { if (__instance != null) ResumedMoves.Remove(__instance.Pointer); }

        private static void BeforeVisitMove(AgentGhost __0, VisitVenueAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.Wander, out __state);
        private static void BeforeDeliveryMove(AgentGhost __0, DeliverIngredientsActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.WorldPoint, out __state);
        private static void BeforeIdleMove(AgentGhost __0, IdleAgentAction.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.IdlePoint, out __state);
        private static void BeforeWorkplaceMove(AgentGhost __0, IdleAtWorkplaceActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.IdlePoint, out __state);
        private static void BeforeCleanupMealMove(AgentGhost __0, CleanupMealAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeCleanupStorageMove(AgentGhost __0, CleanupStorageAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeCleanupTrashMove(AgentGhost __0, CleanupTrashAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeMakeMealMove(AgentGhost __0, MakeMealAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeManageMove(AgentGhost __0, ManageAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeManageComplaintMove(AgentGhost __0, ManageComplaintAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeProcessIngredientMove(AgentGhost __0, ProcessIngredientAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeServeMealMove(AgentGhost __0, ServeMealAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeforeTakeOrderMove(AgentGhost __0, TakeOrderAgentActionType.State __1, out MoveScope __state) =>
            BeginObservedMove(__0, __1, MoveTargetKind.PublicTask, out __state);
        private static void BeginObservedMove(AgentGhost agent, ActionState state, MoveTargetKind targetKind, out MoveScope scope)
        {
            scope = null;
            if (!Worker) return;
            try
            {
                // Concrete Enter methods retain their native schedule, target,
                // workplace and reservation checks before the shared Enter.
                // A nested unqualified call must never inherit outer visibility.
                if (moveScope != null || sleepSelection != null)
                {
                    scope = new MoveScope { Previous = moveScope, SuspendedSleep = sleepSelection };
                    moveScope = scope; sleepSelection = null;
                }
                if (agent == null || state == null || !agent.IsValid) return;
                ObservedMoves.Remove(agent.Pointer);
                int scene = agent.SceneIndex;
                if (scene < 0 || !sceneOccupied(scene) || agent.IsViewLess) return;
                if (agent.State.personSchedule?.teleport == true) return;
                var travel = state.TryCast<ITeleportingActionState>();
                if (travel == null || travel.TargetSceneIndex != scene) return;
                var targetRef = travel.Target;
                var target = ReferenceEquals(targetRef, null) ? null : targetRef.Value;
                // Every audited concrete wrapper consumes this pre-existing
                // Target; it does not assign one before the shared Enter call.
                // Missing targets remain entirely native. Public task targets
                // can be customers, stations or items, so validate their actual
                // scene and ancestry rather than inventing an IdlePoint cast.
                if (target == null || !target.IsValid || target.SceneIndex != scene ||
                    (targetKind == MoveTargetKind.Wander ? target.TryCast<WanderGhost>() == null :
                     targetKind == MoveTargetKind.WorldPoint ? target.TryCast<WorldPointGhost>() == null :
                     targetKind == MoveTargetKind.IdlePoint && target.TryCast<IdlePoint.IdlePointGhost>() == null)) return;
                if (!IsPublicMoveEndpoint(agent, scene) || !IsPublicMoveEndpoint(target, scene)) return;
                // ApartmentTransition is a cached comparison with the native
                // player's context, not proof that either endpoint is private.
                // Native Enter constructs the same-map MoveToSpot without that
                // flag; native Tick may discard it after worker focus changes.
                // Observe already-visible actors too, since their command needs
                // the same completion guard. Never rewrite the cached flag.
                if (scope == null) { scope = new MoveScope(); moveScope = scope; }
                scope.Agent = agent; scope.State = state; scope.Scene = scene;
                scope.AgentId = agent.Id; scope.TargetId = target.Id;
                scope.TargetType = NativeType(target);
                scope.OldCommand = state.CharacterCommand?.Pointer ?? IntPtr.Zero;
                scope.Protected = true;
            }
            catch (Exception ex) { Notice("NPC_OBSERVED_MOVE enter validation failed: " + ex.Message); }
        }
        private static bool IsPublicMoveEndpoint(Ghost ghost, int scene)
        {
            // Keep genuine apartment/attached private transitions native even
            // when their logical scene is the surrounding public district.
            // Unknown, cyclic or excessive ancestry cannot grant an override.
            for (int depth = 0; depth < 32; depth++)
            {
                if (ghost == null || !ghost.IsValid || ghost.SceneIndex != scene ||
                    ghost.TryCast<Nivalis.Apartment.ApartmentAreaGhost>() != null) return false;
                var parent = ghost.Parent;
                if (ReferenceEquals(parent, null) || string.IsNullOrEmpty(parent.ReferencedId)) return true;
                var next = parent.Value;
                if (next == null || next.Id != parent.ReferencedId || next.Pointer == ghost.Pointer) return false;
                ghost = next;
            }
            return false;
        }
        private static bool ObservedMoveVisibility(AgentGhost __instance, ref bool __result)
        {
            if (sleepSelection != null && sleepSelection.Protected && __instance != null && __instance.Pointer == sleepSelection.Agent)
            {
                __result = true; return false;
            }
            var scope = moveScope;
            if (scope == null || !scope.Protected || __instance == null || __instance.Pointer != scope.Agent.Pointer) return true;
            // Only this synchronous Enter sees visibility. The native movement
            // simulator continues to see the actual offstage ghost afterward.
            scope.Used = true; __result = true; return false;
        }
        private static void BeforeSleepSelection(AgentGhost __0, out SleepSelectionScope __state)
        {
            __state = null;
            if (!Worker) return;
            try
            {
                if (sleepSelection != null || moveScope != null)
                {
                    __state = new SleepSelectionScope { Previous = sleepSelection, SuspendedMove = moveScope };
                    sleepSelection = __state; moveScope = null;
                }
                if (__0 == null || !__0.IsValid || __0.SceneIndex < 0 || !sceneOccupied(__0.SceneIndex)) return;
                if (__state == null) { __state = new SleepSelectionScope(); sleepSelection = __state; }
                __state.Agent = __0.Pointer; __state.Protected = true;
                // Native TryGetValidInteractable consults visibility only after
                // its curfew branch. Keep that ordering and every native sleep
                // requirement/reservation; do not cancel an already loaded sleep.
            }
            catch (Exception ex) { Notice("NPC_SLEEP_SELECTION validation failed: " + ex.Message); }
        }
        private static Exception EndSleepSelection(Exception __exception, SleepSelectionScope __state)
        {
            if (__state != null && Worker && ReferenceEquals(sleepSelection, __state)) { sleepSelection = __state.Previous; moveScope = __state.SuspendedMove; }
            return __exception;
        }
        private static void BeforeSleepTransition(AgentGhost __0, SleepAgentAction.State __1, MethodBase __originalMethod, out SleepTrace __state)
        {
            __state = null;
            if (!Worker || Diagnostic == null) return;
            try
            {
                if (__0 == null || !__0.IsValid || !sceneOccupied(__0.SceneIndex)) return;
                __state = new SleepTrace { Agent = __0, State = __1, Scene = __0.SceneIndex, Position = __0.Position,
                    Viewless = __0.IsViewLess, Stage = __originalMethod.Name };
            }
            catch (Exception ex) { Notice("NPC_NATIVE_SLEEP observation failed: " + ex.Message); }
        }
        private static void AfterSleepTransition(SleepTrace __state)
        {
            if (!Worker || __state == null) return;
            try
            {
                var agent = __state.Agent; var position = agent.Position; var old = __state.Position;
                if ((position - old).sqrMagnitude <= 144f || !SleepMoves.TryTake(Time.realtimeSinceStartupAsDouble, out int suppressed)) return;
                var source = __state.State?.ActionSource;
                var target = ReferenceEquals(source, null) ? null : source.Value;
                Diagnostic?.Invoke(FormattableString.Invariant($"NPC_NATIVE_SLEEP stage={__state.Stage} id={agent.Id} scene={__state.Scene}->{agent.SceneIndex} old=({old.x:F3},{old.y:F3},{old.z:F3}) target=({position.x:F3},{position.y:F3},{position.z:F3}) viewless={__state.Viewless}->{agent.IsViewLess} result={__state.State?.Result} targetId={target?.Id ?? "null"} targetType={NativeType(target)} scheduleTeleport={agent.State.personSchedule?.teleport.ToString() ?? "none"} suppressed={suppressed}"));
            }
            catch (Exception ex) { Notice("NPC_NATIVE_SLEEP diagnostic failed: " + ex.Message); }
        }
        private static void AfterObservedMove(MoveScope __state)
        {
            if (!Worker || __state == null || !__state.Protected || !__state.Used) return;
            try
            {
                var state = __state.State;
                var command = state.CharacterCommand?.TryCast<MoveToSpotCommand>();
                var target = command?.ghostReference;
                if (command == null || command.Pointer == __state.OldCommand || (int)state.Result != 0 || (int)command.Result != 0 ||
                    ReferenceEquals(target, null) || target.ReferencedId != __state.TargetId || __state.Agent.SceneIndex != __state.Scene) return;
                if (ObservedMoves.Count >= 30000) ObservedMoves.Clear();
                ObservedMoves[__state.Agent.Pointer] = new ObservedMove { AgentId = __state.AgentId, TargetId = __state.TargetId,
                    Scene = __state.Scene, State = state.Pointer, Command = command.Pointer, SubActionIndex = state.SubActionIndex };
                MoveNotice("begin targetType=" + __state.TargetType, __state.AgentId, __state.Scene, __state.TargetId);
            }
            catch (Exception ex) { Notice("NPC_OBSERVED_MOVE command observation failed: " + ex.Message); }
        }
        private static Exception EndObservedMove(Exception __exception, MoveScope __state)
        {
            if (__state != null && Worker && ReferenceEquals(moveScope, __state)) { moveScope = __state.Previous; sleepSelection = __state.SuspendedSleep; }
            return __exception;
        }
        private static bool WaitForObservedMove(AgentGhost __0)
        {
            if (!Worker || ObservedMoves.Count == 0 || __0 == null || !ObservedMoves.TryGetValue(__0.Pointer, out var move)) return true;
            try
            {
                var state = __0.CurrentActionState;
                var command = state?.CharacterCommand;
                var travel = state?.TryCast<ITeleportingActionState>();
                var target = travel?.Target;
                // Resume the ordinary action immediately after departure,
                // replacement, interruption, or completion; never hold stale AI.
                if (!__0.IsValid || __0.Id != move.AgentId || __0.SceneIndex != move.Scene || !sceneOccupied(move.Scene) ||
                    state == null || state.Pointer != move.State || state.SubActionIndex != move.SubActionIndex || (int)state.Result != 0 ||
                    command == null || command.Pointer != move.Command || (int)command.Result != 0 ||
                    travel == null || travel.TargetSceneIndex != move.Scene || __0.IsViewLess ||
                    ReferenceEquals(target, null) || target.ReferencedId != move.TargetId || __0.State.personSchedule?.teleport == true)
                {
                    ObservedMoves.Remove(__0.Pointer);
                    MoveNotice("resume", move.AgentId, move.Scene, move.TargetId,
                        " commandResult=" + (command == null ? "null" : ((int)command.Result).ToString()) +
                        " commandSame=" + (command != null && command.Pointer == move.Command) +
                        " stateSame=" + (state != null && state.Pointer == move.State) +
                        " occupied=" + sceneOccupied(move.Scene));
                    return true;
                }
                if (!IsPublicMoveEndpoint(__0, move.Scene) || !IsPublicMoveEndpoint(target.Value, move.Scene))
                {
                    ObservedMoves.Remove(__0.Pointer); MoveNotice("resume private endpoint", move.AgentId, move.Scene, move.TargetId); return true;
                }
                var action = __0.CurrentAction;
                if (action == null || action.Type == null || action.IsExiting(__0) ||
                    (action.Type.CanBeInterrupted && !action.Type.MeetsRequirements(__0, __0.State.personSchedule)))
                {
                    ObservedMoves.Remove(__0.Pointer); return true;
                }
                // Native Teleport.Tick shortcuts when worker apartment/focus
                // differs. Await only its own pending MoveToSpotCommand here.
                // AgentViewSimulator still advances that command independently.
                return false;
            }
            catch (Exception ex)
            {
                ObservedMoves.Remove(__0.Pointer); Notice("NPC_OBSERVED_MOVE wait validation failed: " + ex.Message); return true;
            }
        }
        private static void MoveNotice(string stage, string id, int scene, string target, string detail = "")
        {
            try
            {
                if (Moves.TryTake(Time.realtimeSinceStartupAsDouble, out int suppressed))
                    Diagnostic?.Invoke("NPC_OBSERVED_MOVE " + stage + " id=" + id + " scene=" + scene + " target=" + target + detail + " suppressed=" + suppressed);
            }
            catch { }
        }

        private static void BeforeWanderTick(AgentGhost __0, WanderAgentAction.State __1, out TickScope __state)
        {
            __state = null;
            if (!Worker) return;
            try
            {
                // Tick releases the old destination before selecting the next
                // one. Enter also selects points but owns no such reservation.
                // Keep this scope distinct so a failed Tick cannot release twice.
                // Only nested calls need an immediate mask. Most NPC ticks
                // still await their command and allocate no selection scope.
                if (tick != null)
                {
                    __state = new TickScope { Previous = tick, Agent = __0, State = __1 };
                    tick = __state;
                }
                if (__0 == null || __1 == null) return;
                var command = __1.CharacterCommand;
                if (command != null && (int)command.Result != 1) return; // Native success.
                if (!__0.IsValid || !sceneOccupied(__0.SceneIndex)) return;
                if (__state == null) { __state = new TickScope { Agent = __0, State = __1 }; tick = __state; }
                var reference = __1.destination;
                var point = ReferenceEquals(reference, null) ? null : reference.Value;
                if (point == null || !point.IsValid || point.SceneIndex != __0.SceneIndex) return;
                __state.ReservedPoint = point;
                __state.ReservedTraffic = point.Traffic;
            }
            catch (Exception ex) { Notice("NPC_WANDER_GUARD tick observation failed: " + ex.Message); }
        }
        private static Exception EndWanderTick(Exception __exception, TickScope __state)
        {
            if (__state != null) tick = __state.Previous;
            return __exception;
        }
        private static void RestoreReleasedReservation(AgentGhost agent, WanderGhost from)
        {
            var owner = tick;
            if (owner == null || owner.Restored || agent == null || from == null || owner.Agent == null ||
                owner.Agent.Pointer != agent.Pointer || owner.ReservedPoint == null || owner.ReservedPoint.Pointer != from.Pointer) return;
            var destination = owner.State.destination;
            if (ReferenceEquals(destination, null) || destination.ReferencedId != from.Id ||
                (long)from.Traffic != (long)owner.ReservedTraffic - 1) return;
            // The native null-result path keeps state.destination. Its eventual
            // Exit still owns one release; undo only the release already made
            // by this Tick, without changing any other NPC's reservation.
            from.Traffic = owner.ReservedTraffic;
            owner.Restored = true;
        }

        private static bool BeforeNextWaypoint(AgentGhost __0, WanderGhost __1, ref WanderGhost __result, out SelectionScope __state)
        {
            __state = null;
            try
            {
                if (!Worker) return true;
                if (__0 != null) Pending.Remove(__0.Pointer);
                bool protect = __0 != null && __0.IsValid && __0.SceneIndex >= 0 && sceneOccupied(__0.SceneIndex);
                if (!protect && selection == null) return true;
                __state = new SelectionScope { Previous = selection, Agent = __0, From = __1, Protected = protect };
                selection = __state;
                return true;
            }
            catch (Exception ex)
            {
                // A null next point follows the game's existing failed-action
                // retry path; never partially mutate the authored route graph.
                __result = null;
                try { RestoreReleasedReservation(__0, __1); } catch { }
                Notice("NPC_WANDER_GUARD selection failed: " + ex.Message); return false;
            }
        }
        private static bool AllowTeleportCandidates(WanderGhost __instance)
        {
            // The native routine only appends teleport candidates. Its caller
            // has already filled normal neighbours, including its turn fallback.
            if (selection == null || !selection.Protected || selection.From == null ||
                __instance == null || selection.From.Pointer != __instance.Pointer) return true;
            int scene = selection.Agent.SceneIndex;
            if (ReportedScenes.Add(scene)) Notice("NPC_WANDER_GUARD protected scene=" + scene + " id=" + selection.Agent.Id);
            return false;
        }
        private static void AfterNextWaypoint(ref WanderGhost __result, SelectionScope __state)
        {
            if (__state == null || !__state.Protected || __state.From == null) return;
            try
            {
                var agent = __state.Agent;
                // Native selection requires a destination with at least two
                // connections, then falls back to an unrelated closest point.
                // Retry its own regular-neighbour traversal without that degree
                // preference; native validity and traffic limits still apply.
                if (__result == null || __result.SceneIndex != agent.SceneIndex || !HasNeighbour(__state.From, __result))
                {
                    var fallback = __result;
                    __result = RetryRegularNeighbour(__state.From, agent.SceneIndex);
                    Notice("NPC_WANDER_GUARD " + (__result == null ? "no regular route" : "regular retry") +
                        " id=" + agent.Id + " scene=" + agent.SceneIndex + " from=" + PointDetails(__state.From) +
                        " fallback=" + PointDetails(fallback) + " chosen=" + PointDetails(__result));
                    if (__result == null) { RestoreReleasedReservation(agent, __state.From); return; }
                }
                if (Pending.Count >= 30000) Pending.Clear();
                Pending[agent.Pointer] = new SelectionEdge { AgentId = agent.Id, From = __state.From, To = __result, Scene = agent.SceneIndex };
            }
            catch (Exception ex)
            {
                __result = null;
                try { RestoreReleasedReservation(__state.Agent, __state.From); } catch { }
                Notice("NPC_WANDER_GUARD route validation failed: " + ex.Message);
            }
        }
        private static WanderGhost RetryRegularNeighbour(WanderGhost from, int scene)
        {
            if (from == null || !from.IsValid || from.SceneIndex != scene || from.Neighbours == null || from.Neighbours.Count > 4096) return null;
            var candidates = new Il2CppSystem.Collections.Generic.List<WanderGhost>();
            from.FillNextLocations(candidates, Vector3.zero, -1f, 0);
            int count = candidates.Count;
            if (count == 0 || count > 4096) return null;
            int start = UnityEngine.Random.Range(0, count);
            for (int i = 0; i < count; i++)
            {
                var candidate = candidates[(start + i) % count];
                if (candidate != null && candidate.SceneIndex == scene && HasNeighbour(from, candidate)) return candidate;
            }
            return null;
        }
        private static string PointDetails(WanderGhost point) => point == null ? "null" :
            point.Id + ",regular:" + (point.Neighbours?.Count ?? -1) + ",teleport:" + (point.TeleportNeighbours?.Count ?? -1) +
            ",traffic:" + point.Traffic + "/" + point.TrafficLimit;
        private static Exception EndNextWaypoint(Exception __exception, SelectionScope __state)
        {
            if (__state != null) selection = __state.Previous;
            return __exception;
        }
        private static void BeforeGiveMoveCommand(AgentGhost __0, WanderAgentAction.State __1, IWorldLocation __2, ref bool __3)
        {
            if (!Worker || __0 == null || !Pending.TryGetValue(__0.Pointer, out var edge)) return;
            Pending.Remove(__0.Pointer);
            if (!__3) return;
            try
            {
                // Tick labels an edge teleporting if it appears in the old
                // point's TeleportNeighbours, even if it is also a normal edge.
                // Only this exact, freshly selected normal edge may change to
                // the game's ordinary MoveToSpotCommand construction branch.
                if (__1 == null || __2 == null || __0.Id != edge.AgentId || __0.SceneIndex != edge.Scene ||
                    __1.TargetSceneIndex != edge.Scene || __2.SceneIndex != edge.Scene || __2.Pointer != edge.To.Pointer ||
                    !sceneOccupied(edge.Scene) || !HasNeighbour(edge.From, edge.To)) return;
                __3 = false;
                Notice("NPC_WANDER_GUARD normal overlap id=" + edge.AgentId + " scene=" + edge.Scene + " from=" + edge.From.Id + " to=" + edge.To.Id);
            }
            catch (Exception ex)
            {
                // Leave the native command untouched when validation cannot
                // complete. Do not fabricate command state or completion.
                Notice("NPC_WANDER_GUARD command validation failed: " + ex.Message);
            }
        }
        private static bool HasNeighbour(WanderGhost from, WanderGhost to)
        {
            if (from == null || to == null || from.Pointer == to.Pointer || !from.IsValid || !to.IsValid || from.SceneIndex != to.SceneIndex) return false;
            var neighbours = from.Neighbours;
            if (neighbours == null || neighbours.Count > 4096) return false;
            string id = to.Id;
            for (int i = 0; i < neighbours.Count; i++)
            {
                // Reading boxed native values is supported. Never call generic
                // List.Contains/Add with GhostReference wrapper payloads.
                var reference = neighbours[i];
                if (!ReferenceEquals(reference, null) && reference.ReferencedId == id) return true;
            }
            return false;
        }
        private static void ObserveTransfer(Ghost __0, int __1, PositionRotation __2)
        {
            // Read-only and low frequency: identify the native subaction behind
            // large transfers before choosing any further movement intervention.
            if (!Worker || Diagnostic == null || __0 == null) return;
            try
            {
                var agent = __0.TryCast<AgentGhost>();
                if (agent == null || !agent.IsValid || (!sceneOccupied(agent.SceneIndex) && !sceneOccupied(__1))) return;
                var old = agent.Transform.Position;
                if ((old - __2.Position).sqrMagnitude <= 144f || !Transfers.TryTake(Time.realtimeSinceStartupAsDouble, out int suppressed)) return;
                var action = agent.CurrentAction;
                var state = agent.CurrentActionState;
                var command = agent.CharacterCommand;
                var sub = action != null && state != null && state.SubActionIndex >= 0 && state.SubActionIndex < action.TotalActionCount ? action.GetSubAction(state.SubActionIndex) : null;
                var teleport = state?.TryCast<Nivalis.GhostSystem.CustomerLoop.ITeleportingActionState>();
                var target = teleport?.Target;
                var targetGhost = ReferenceEquals(target, null) ? null : target.Value;
                Diagnostic("NPC_NATIVE_TRANSFER attempt id=" + agent.Id + " oldScene=" + agent.SceneIndex + " newScene=" + __1 +
                    " old=" + old + " target=" + __2.Position + " visible=" + agent.Visible +
                    " actionType=" + NativeType(action?.Type) + " state=" + NativeType(state) +
                    " subIndex=" + (state?.SubActionIndex ?? -1) + " groupedIndex=" + (state?.GroupedActionIndex ?? -1) + " sub=" + NativeType(sub) +
                    " command=" + NativeType(command) + " commandResult=" + (command == null ? "null" : command.Result.ToString()) +
                    " targetScene=" + (teleport?.TargetSceneIndex ?? -1) + " apartment=" + (teleport?.ApartmentTransition ?? false) +
                    " targetId=" + (ReferenceEquals(target, null) ? "null" : target.ReferencedId) + " targetType=" + NativeType(targetGhost) +
                    " scheduleTeleport=" + (agent.State.personSchedule?.teleport.ToString() ?? "none") + " suppressed=" + suppressed);
            }
            catch (Exception ex) { Notice("NPC_NATIVE_TRANSFER diagnostic failed: " + ex.Message); }
        }
        private static string NativeType(Il2CppSystem.Object value) => value == null ? "null" : value.GetIl2CppType()?.FullName ?? "unknown";
        private static void ObserveSkipTime(float __0, bool __1)
        {
            if (Worker) Notice("NPC_NATIVE_SKIP_TIME gameSeconds=" + __0 + " updateSimulators=" + __1);
        }
        private static void Notice(string message)
        {
            try
            {
                if (Notices.TryTake(UnityEngine.Time.realtimeSinceStartupAsDouble, out int suppressed))
                    Diagnostic?.Invoke(message + " suppressed=" + suppressed);
            }
            catch { }
        }
    }
}
