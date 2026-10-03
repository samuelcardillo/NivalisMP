using System;
using System.Collections.Generic;
using System.Reflection;
using Articy.Nivalis.GlobalVariables;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.Dialogue;
using Nivalis.Economy;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.UI.EndOfDay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    // Campaign suppression belongs only to the isolated multiplayer world.
    // Native commerce, property ownership, rent, salaries and account balances
    // retain their normal handlers; no campaign completion rewards are invoked.
    public static class MultiplayerSandboxRuntime
    {
        private static readonly HarmonyLib.Harmony Harmony = new HarmonyLib.Harmony("local.nivalis.together.sandbox");
        private static readonly List<IntegerVariable> Debts = new List<IntegerVariable>();
        private static readonly HashSet<IntPtr> PublicLocations = new HashSet<IntPtr>();
        private static bool installed, normalized;
        private static IntPtr accessManager;
        private static int debtScene = -1;
        public static bool Active => DedicatedWorldRuntime.IsIsolated;
        public static bool IsNormalized => !Active || normalized;

        public static void Install()
        {
            if (installed) return;
            Hook(typeof(QuestManager), "InitializeInternal", nameof(QuestLoad), typeof(ManagerConfiguration), typeof(ISavePacket));
            Hook(typeof(QuestManager), "StartQuest", nameof(QuestStart), typeof(Quest));
            Hook(typeof(QuestManager), "ProgressUpdate", nameof(AllowCampaign), typeof(bool));
            foreach (string name in new[] { "ForceCompleteQuest", "ForceCompleteQuestObjectives", "FailQuest", "CompleteQuestImmediate" })
                Hook(typeof(QuestManager), name, nameof(AllowCampaign), typeof(Quest));
            foreach (string name in new[] { "StartQuest", "ForceComplete", "ForceCompleteCurrentObjectives", "ActivateCachedSubquestsAndTrackers", "Fail", "CompleteImmediate" })
                Hook(typeof(RuntimeQuest), name, nameof(AllowCampaign));
            Hook(typeof(RuntimeQuest), "UpdateIfDirty", nameof(AllowCampaign), typeof(bool));
            Hook(typeof(StoryArcDialogueAssigner), "Tick", nameof(AllowCampaign));
            foreach (string name in new[] { "CheckTransition", "SetActiveDialogue" })
                Hook(typeof(StoryArcDialogueAssigner), name, nameof(AllowCampaign), typeof(ConvTransition));
            Hook(typeof(StoryArcDialogueAssigner), "ForceActivateTransition", nameof(AllowCampaign), typeof(IConversation), typeof(IConversation));
            foreach (string name in new[] { "AddTreeToQueue", "ForceAddTreeToQueue" })
                Hook(typeof(StartDialoguePhone), name, nameof(AllowCampaign), typeof(IDialogueTree), typeof(Person), typeof(string));
            Hook(typeof(StartDialoguePhone), "Update", nameof(AllowCampaign));
            Hook(typeof(DialogueTreeProgressManager), "InitializeInternal", nameof(DialogueLoad), typeof(ManagerConfiguration), typeof(ISavePacket));
            Hook(typeof(DialogueTreeProgressManager), "Update", nameof(AllowCampaign));
            Hook(typeof(DialogueTreeProgressManager), "HandleCurrentDialogueNode", nameof(AllowCampaign));
            Hook(typeof(EconomyManager), "IsVendorTierUnlocked", nameof(SupplierTier), typeof(Vendor.VendorTier));
            Hook(typeof(SceneTransitionInteractable), "CanInteract", nameof(PublicTerminalAccess));
            Hook(typeof(MapTile), "get_IsFastTravelAllowed", nameof(PublicMapAccess));
            DialogueReplication.InstallSandboxPolicy();
            installed = true;
        }

        public static void PrepareForLoad() { Install(); Reset(); }
        public static void Reset()
        {
            normalized = false; accessManager = IntPtr.Zero; debtScene = -1; Debts.Clear(); PublicLocations.Clear();
        }

        public static void Tick()
        {
            if (!Active || !DedicatedWorldRuntime.IsWorldReady) return;
            var quests = QuestManager.Instance;
            var globals = ArticyGlobalVariables.Default;
            var travel = TravelUnlockManager.Instance;
            if (quests == null || globals == null || travel == null || travel.worldLocations == null)
                throw new InvalidOperationException("Multiplayer sandbox managers are not initialized.");

            ClearQuests(quests);
            if (StartDialoguePhone.Instance != null) StartDialoguePhone.Instance.callsQueue?.Clear();
            if (DialogueTreeProgressManager.Instance != null) DialogueTreeProgressManager.Instance._phoneDialogueQueue?.Clear();
            DialogueReplication.ApplySandboxPolicy();

            // Set only explicit access/debt variables. Never execute a quest's
            // start/end actions or fabricate completion flags for every quest.
            if (!globals.GlobalPlayerKnowledge.IntroComplete) globals.GlobalPlayerKnowledge.IntroComplete = true;
            if (!globals.GameState.VendorsActivated) globals.GameState.VendorsActivated = true;
            if (!globals.GameState.CabTerminalMeridian_IsInteractable) globals.GameState.CabTerminalMeridian_IsInteractable = true;
            if (globals.GameState.Debt != 0) globals.GameState.Debt = 0;
            if (globals.GameState.NoodleBar_Debt != 0) globals.GameState.NoodleBar_Debt = 0;

            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            if (debtScene != scene)
            {
                Debts.Clear();
                foreach (var section in Object.FindObjectsOfType<EndOfDayDebtSection>(true))
                    if (section != null) { AddDebt(section.debtVariable); AddDebt(section.additionalDebtVariable); }
                // These are the two exact story-debt assets used by the native
                // end-of-day section. Do not assume every Venue.currentDebt is
                // that obligation or overwrite unrelated business accounting.
                debtScene = scene;
            }
            foreach (var debt in Debts) if (debt != null && debt.Value != 0) debt.Value = 0;

            if (accessManager != travel.Pointer)
            {
                PublicLocations.Clear();
                var locations = travel.worldLocations;
                for (int i = 0; i < locations.Length; i++)
                {
                    var location = locations[i];
                    if (!IsPublicLocationDefinition(location)) continue;
                    if (!travel.IsLocationUnlocked(location)) travel.UnlockLocation(location);
                    if (!travel.IsLocationUnlocked(location)) throw new InvalidOperationException("Sandbox travel access could not be initialized.");
                    PublicLocations.Add(location.Pointer);
                }
                if (PublicLocations.Count == 0) throw new InvalidOperationException("Sandbox has no canonical public travel destinations.");
                accessManager = travel.Pointer;
            }
            if (!normalized)
            {
                normalized = true;
                Debug.Log("[Nivalis Together] Multiplayer sandbox ready: no campaign quests, phone story or narrative debt; " +
                    PublicLocations.Count + " public travel destinations open without first-visit requirements; native commerce and account assets preserved.");
            }
        }

        // Match actual native assets by reference, not by GUID alone: shipped
        // portal GUIDs are reused by some train platforms and the developer map.
        // Never turn an apartment key or an unregistered/debug scene into a
        // public destination, and never grant property or business ownership.
        private static bool IsPublicLocationDefinition(WorldLocation location)
        {
            if (location == null || location.SceneIndex < 0 || string.IsNullOrWhiteSpace(location.Guid) ||
                string.IsNullOrWhiteSpace(location.ScenePath)) return false;
            var key = location.DefaultLocation;
            return key != null && key.Apartment == null && key.TargetLocation != null &&
                key.TargetLocation.Pointer == location.Pointer && key.SceneIndex == location.SceneIndex;
        }

        public static bool IsPublicTravelDestination(PortalKey key)
        {
            if (!Active || accessManager == IntPtr.Zero || key == null || key.Apartment != null ||
                string.IsNullOrWhiteSpace(key.Guid)) return false;
            var manager = TravelUnlockManager.Instance;
            var location = key.TargetLocation;
            return manager != null && manager.Pointer == accessManager && location != null &&
                PublicLocations.Contains(location.Pointer) && key.SceneIndex == location.SceneIndex &&
                key.SceneIndex >= 0;
        }

        private static bool PublicTerminalAccess(SceneTransitionInteractable __instance, ref bool __result)
        {
            if (__instance == null || !IsPublicTravelDestination(__instance.keyTo)) return true;
            // Native CanInteract is: optional access BooleanVariable, valid key,
            // and !LocalPlayer.IsHoldingObject. Remove only the campaign/access
            // flag. Do not mutate the ScriptableObject or its saved solo value.
            var player = PlayerManager.Instance?.LocalPlayer;
            __result = player != null && !player.IsHoldingObject;
            return false;
        }

        private static bool PublicMapAccess(MapTile __instance, ref bool __result)
        {
            if (__instance == null || !IsPublicTravelDestination(__instance.PortalKey)) return true;
            // Native IsFastTravelAllowed adds a prior-visit check. This getter
            // supplies only access policy: MapTile.CanTravel and the server
            // still enforce loading, sleep, held-object and actor state gates.
            __result = true;
            return false;
        }

        private static void AddDebt(IntegerVariable value)
        {
            if (value == null) return;
            foreach (var existing in Debts) if (existing.Pointer == value.Pointer) return;
            if (Debts.Count >= 4096) throw new InvalidOperationException("Too many native narrative debt variables.");
            Debts.Add(value);
        }

        private static void ClearQuests(QuestManager manager)
        {
            bool changed = ClearQuestMap(manager._activeQuests) | ClearQuestMap(manager._completedQuests);
            if (QuestManager.questsToMarkAsCompleted != null && QuestManager.questsToMarkAsCompleted.Count != 0)
            { QuestManager.questsToMarkAsCompleted.Clear(); changed = true; }
            if (manager._nextQuestNumber != 0) { manager._nextQuestNumber = 0; changed = true; }
            if (changed) { manager.OnQuestsUpdated?.Invoke(); manager.OnQuestsPinnedChanged?.Invoke(); }
        }

        private static bool ClearQuestMap(Il2CppSystem.Collections.Generic.Dictionary<string, RuntimeQuest> map)
        {
            if (map == null || map.Count == 0) return false;
            int count = map.Count, version = map.version;
            if (count < 0 || count > 8192) throw new InvalidOperationException("Invalid native quest registry size.");
            var rows = new Il2CppReferenceArray<RuntimeQuest>(count);
            map.Values.CopyTo(rows, 0);
            if (map.Count != count || map.version != version) throw new InvalidOperationException("Native quest registry changed during sandbox cleanup.");
            try
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    var quest = rows[i];
                    if (quest == null) continue;
                    quest.DeactivateTrackingPointsForQuest();
                    quest.StopTracking();
                }
                if (map.Count != count || map.version != version) throw new InvalidOperationException("Native quest registry changed while stopping its trackers.");
                map.Clear();
                return true;
            }
            finally { GC.KeepAlive(rows); GC.KeepAlive(map); }
        }

        private static bool AllowCampaign() => !Active;
        private static bool QuestStart(Quest __0, ref RuntimeQuest __result)
        {
            if (!Active) return true;
            // Preserve callers' reference-return contract without registering or
            // starting a quest. The constructor creates inert tracker lists.
            __result = __0 == null ? null : new RuntimeQuest(__0);
            return false;
        }
        private static void QuestLoad(ISavePacket __1)
        {
            if (!Active || __1 == null) return;
            var packet = __1.TryCast<QuestManagerSave>();
            if (packet == null) throw new InvalidOperationException("Unexpected native quest save packet.");
            packet.ActiveQuests?.Clear(); packet.CompletedQuests?.Clear(); packet.nextQuestNumber = 0;
        }
        private static void DialogueLoad(ISavePacket __1)
        {
            if (!Active || __1 == null) return;
            var packet = __1.TryCast<DialogueTreeProgressManager.DialogueTreeProgressManagerSave>();
            if (packet == null) throw new InvalidOperationException("Unexpected native dialogue save packet.");
            packet.SavedPhoneCallQueue?.Clear();
        }
        private static bool SupplierTier(Vendor.VendorTier __0, ref bool __result)
        {
            if (!Active) return true;
            if (!Enum.IsDefined(typeof(Vendor.VendorTier), __0)) return true;
            __result = true; return false;
        }

        private static void Hook(Type type, string name, string prefix, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
            foreach (var parameter in method.GetParameters())
            {
                Type input = parameter.ParameterType;
                if (input.IsByRef) input = input.GetElementType();
                if (typeof(Il2CppSystem.ValueType).IsAssignableFrom(input))
                    throw new InvalidOperationException("Unsafe boxed native argument on sandbox hook " + method.Name);
            }
            Harmony.Patch(method, prefix: new HarmonyMethod(typeof(MultiplayerSandboxRuntime).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
        }
    }
}
