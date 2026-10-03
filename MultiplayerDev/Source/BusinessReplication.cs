using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Nivalis;
using Nivalis.CraftingSystem;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using Nivalis.Locale;
using Nivalis.Locale.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using TransferUi = _Work_Lukasz.Scripts.UI.TransferMenuUI;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    public sealed class BusinessIntent
    {
        public string PersonId { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string RecipeId { get; set; } = "";
        public string Storage { get; set; } = "";
        public string ProcessorId { get; set; } = "";
        public bool Enabled { get; set; }
        public bool ToVenue { get; set; }
        public int Amount { get; set; }
        public int Freshness { get; set; }
        public int Delta { get; set; }
        public int Task { get; set; }
        public float Start { get; set; }
        public float End { get; set; }
    }
    public sealed class BusinessResult
    {
        public string PlayerId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public bool Success { get; set; }
        // Starting a timed craft only reserves an existing native task. Its final
        // completion is a separate result whose mutations require durable save.
        public bool NeedsCheckpoint { get; set; } = true;
        public string Message { get; set; } = "";
    }

    /// <summary>Unity main-thread business management of the co-op group's native venues.</summary>
    public static class BusinessReplication
    {
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.business");
        private static readonly Dictionary<string, (PlayerPose Pose, float Time)> Players = new();
        private static readonly Dictionary<string, WorldEvent> Results = new();
        private static readonly Queue<string> ResultOrder = new();
        private static readonly Dictionary<string, CraftJob> CraftJobs = new();
        private sealed class CraftJob
        {
            internal string PlayerId, RequestId, AccountId;
            internal Ghost Processor;
            internal VenueAreaGhost Venue;
            internal StaffTaskQueue.StaffTask Task;
            internal float FinishTime;
            internal bool Committing;
        }
        private static bool patched, clientAuthority, refreshing, faulted;
        private static float nextIntent, nextNotice;
        public static Action<InteractionRequest> OutboundInteraction { get; set; }
        public static Action<WorldEvent> OutboundWorldEvent { get; set; }
        public static Action<string> Notice { get; set; }
        public static string LocalPlayerId { get; set; } = "";
        public static void SetClientAuthority(bool value)
        {
            if (!patched) InstallPatches();
            if (value) ReplicatedUiRegistry.Initialize();
            clientAuthority = value;
            BusinessStateReplication.RefreshUi = RefreshUi;
            BusinessCraftStateReplication.SetClientAuthority(value);
        }
        public static void Reset()
        {
            Players.Clear(); Results.Clear(); ResultOrder.Clear(); nextIntent = nextNotice = 0;
            foreach (var job in CraftJobs.Values) CancelJob(job);
            CraftJobs.Clear();
            refreshing = faulted = false; BusinessStateReplication.Reset();
            BusinessCraftStateReplication.Reset();
        }
        public static void UpdatePlayerPose(PlayerPose pose)
        {
            if (pose != null && DedicatedProtocol.PoseValid(pose) && DedicatedProtocol.Text(pose.PlayerId, 128))
                Players[pose.PlayerId] = (pose, Time.realtimeSinceStartup);
        }
        public static void RemovePlayer(string id)
        {
            if (id == null) return;
            Players.Remove(id);
            if (CraftJobs.TryGetValue(id, out var job)) { CancelJob(job); CraftJobs.Remove(id); }
        }
        /// <summary>Call on worker immediately before exporting a native checkpoint.</summary>
        public static void PrepareForCheckpoint()
        {
            foreach (var job in new List<CraftJob>(CraftJobs.Values))
            {
                CancelJob(job); CraftJobs.Remove(job.PlayerId);
                try { EmitCraftResult(job, false, "Preparation returned to the venue queue while the shared world saves."); }
                catch (Exception ex) { Notice?.Invoke("Craft checkpoint notification failed: " + ex.Message); }
            }
        }
        private static void InstallPatches()
        {
            Patch(typeof(VenueOverviewTabGeneralSection), "SnapValue", nameof(OpenPrefix));
            Patch(typeof(HireStaffWindow), "HireSelectedStaff", nameof(HirePrefix));
            Patch(typeof(VenueStaffOverviewTab), "FirePerson", nameof(FirePrefix));
            Patch(typeof(LocaleStaffDetailsDisplay), "FireStaff", nameof(FireDetailsPrefix));
            Patch(typeof(LocaleStaffDetailsDisplay), "IncreaseWage", nameof(DetailWageUpPrefix));
            Patch(typeof(LocaleStaffDetailsDisplay), "DecreaseWage", nameof(DetailWageDownPrefix));
            Patch(typeof(VenueStaffListItem), "WageIncreaseListener", nameof(WageUpPrefix));
            Patch(typeof(VenueStaffListItem), "WageDecreaseListener", nameof(WageDownPrefix));
            Patch(typeof(VenueStaffListItem), "OnTimeSlotValueChanged", nameof(HoursPrefix));
            Patch(typeof(VenueStaffListItem), "CleanToggleListener", nameof(CleanPrefix));
            Patch(typeof(VenueStaffListItem), "ServeToggleListener", nameof(ServePrefix));
            Patch(typeof(VenueStaffListItem), "CookToggleListener", nameof(CookPrefix));
            Patch(typeof(VenueStaffListItem), "ManageToggleListener", nameof(ManagePrefix));
            Patch(typeof(MenuModificationWindow), "AddRecipeToMenu", nameof(AddRecipePrefix));
            Patch(typeof(MenuModificationWindow), "MenuItemRemoveListener", nameof(RemoveRecipePrefix));
            Patch(typeof(MenuItemDisplayUI), "ChangePrice", nameof(PricePrefix));
            Patch(typeof(MenuItemDisplayUI), "OnCustomizeRecipe", nameof(CustomizePrefix));
            Patch(typeof(TransferUi), "ActionButtonClicked", nameof(TransferPrefix));
            Patch(typeof(TransferUi), "RestockButtonClicked", nameof(RestockPrefix));
            Patch(typeof(TransferUi), "RestockEvenlyButtonClicked", nameof(EvenRestockPrefix));
            patched = true;
        }
        private static void Patch(Type type, string name, string prefix)
        {
            MethodInfo found = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.Name == name) { if (found != null) throw new AmbiguousMatchException(type.FullName + "." + name); found = method; }
            if (found == null) throw new MissingMethodException(type.FullName, name);
            Patches.Patch(found, prefix: new HarmonyMethod(typeof(BusinessReplication).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
        }
        private static bool Send(Venue venue, string kind, BusinessIntent intent)
        {
            if (!clientAuthority) return true;
            if (refreshing || BusinessStateReplication.IsApplying) return false;
            try
            {
                if (venue == null || venue.RuntimeData == null || !venue.RuntimeData.PlayerOwned) throw new InvalidOperationException("This venue is not owned by the shared group.");
                if (kind == "business-open" && venue.RuntimeData.IsOpen.Value == intent.Enabled) return false;
                if (kind == "business-task" || kind == "business-hours")
                {
                    var person = ResolvePerson(intent.PersonId);
                    if (kind == "business-task" && (((int)person.RuntimeData.Tasks & intent.Task) != 0) == intent.Enabled) return false;
                    if (kind == "business-hours" && Mathf.Approximately(person.RuntimeData.WorkingHours.x, intent.Start) && Mathf.Approximately(person.RuntimeData.WorkingHours.y, intent.End)) return false;
                }
                if (Time.realtimeSinceStartup < nextIntent) return false;
                if (OutboundInteraction == null) throw new InvalidOperationException("The shared world connection is not ready.");
                nextIntent = Time.realtimeSinceStartup + .15f;
                OutboundInteraction(new InteractionRequest { RequestId = Guid.NewGuid().ToString("N"), Kind = kind, TargetId = venue.RuntimeData.Id,
                    Data = JsonSerializer.SerializeToElement(intent, DedicatedProtocol.Json) });
            }
            catch (Exception ex) { ShowNotice(ex.Message); }
            return false;
        }
        private static bool SendPerson(Person person, string kind, BusinessIntent intent)
        { intent.PersonId = person == null ? "" : person.Guid; return Send(person?.RuntimeData?._worksAt, kind, intent); }
        private static bool OpenPrefix(VenueOverviewTabGeneralSection __instance) => !clientAuthority || Send(__instance._venue, "business-open", new BusinessIntent { Enabled = __instance.venueEnabledToggle.value > .5f });
        private static bool HirePrefix(HireStaffWindow __instance) => !clientAuthority || Send(__instance._forVenue, "business-hire", new BusinessIntent { PersonId = __instance._selectedStaff?.Guid ?? "", Task = (int)__instance._currentTabOpened });
        private static bool FirePrefix(VenueStaffOverviewTab __instance, Person __0) => !clientAuthority || Send(__instance._forVenue, "business-fire", new BusinessIntent { PersonId = __0?.Guid ?? "" });
        private static bool FireDetailsPrefix(LocaleStaffDetailsDisplay __instance) => !clientAuthority || SendPerson(__instance._person, "business-fire", new BusinessIntent());
        private static bool DetailWageUpPrefix(LocaleStaffDetailsDisplay __instance) => !clientAuthority || SendPerson(__instance._person, "business-wage", new BusinessIntent { Delta = 1 });
        private static bool DetailWageDownPrefix(LocaleStaffDetailsDisplay __instance) => !clientAuthority || SendPerson(__instance._person, "business-wage", new BusinessIntent { Delta = -1 });
        private static bool WageUpPrefix(VenueStaffListItem __instance) => !clientAuthority || SendPerson(__instance._person, "business-wage", new BusinessIntent { Delta = 1 });
        private static bool WageDownPrefix(VenueStaffListItem __instance) => !clientAuthority || SendPerson(__instance._person, "business-wage", new BusinessIntent { Delta = -1 });
        // Native slider is normalized: OnTimeSlotValueChanged maps [0,1] to [8,26].
        private static bool HoursPrefix(VenueStaffListItem __instance, Vector2 __0) => !clientAuthority || SendPerson(__instance._person, "business-hours", new BusinessIntent { Start = __0.x * 18 + 8, End = __0.y * 18 + 8 });
        private static bool CleanPrefix(VenueStaffListItem __instance, bool __0) => TaskPrefix(__instance, __0, VenueTasks.Cleaning);
        private static bool ServePrefix(VenueStaffListItem __instance, bool __0) => TaskPrefix(__instance, __0, VenueTasks.Serving);
        private static bool CookPrefix(VenueStaffListItem __instance, bool __0) => TaskPrefix(__instance, __0, VenueTasks.Cooking);
        private static bool ManagePrefix(VenueStaffListItem __instance, bool __0) => TaskPrefix(__instance, __0, VenueTasks.Managing);
        private static bool TaskPrefix(VenueStaffListItem ui, bool enabled, VenueTasks task) => !clientAuthority || SendPerson(ui._person, "business-task", new BusinessIntent { Task = (int)task, Enabled = enabled });
        private static bool AddRecipePrefix(MenuModificationWindow __instance, IRecipe __0) => !clientAuthority || Send(__instance._venue, "business-menu-add", new BusinessIntent { RecipeId = __0?.BaseDefinition?.Guid ?? "" });
        private static bool RemoveRecipePrefix(MenuModificationWindow __instance, MealMenuItem __0) => !clientAuthority || Send(__instance._venue, "business-menu-remove", new BusinessIntent { RecipeId = __0?.RecipeDefinition?.Guid ?? "" });
        private static bool PricePrefix(MenuItemDisplayUI __instance, int __0) => !clientAuthority || Send(__instance._venue, "business-price", new BusinessIntent { RecipeId = __instance._displayedItem?.RecipeDefinition?.Guid ?? "", Delta = __0 });
        private static bool CustomizePrefix()
        {
            if (!clientAuthority) return true;
            ShowNotice("Shared recipe ingredient customization is not available yet."); return false;
        }
        private static bool TransferPrefix(TransferUi __instance)
        {
            if (!clientAuthority) return true;
            try
            {
                var runtime = __instance._venue?.RuntimeData;
                var storage = runtime == null || __instance._currentContainer == null ? "" :
                    __instance._currentContainer.Pointer == runtime.FridgeInventory.Pointer ? "fridge" :
                    __instance._currentContainer.Pointer == runtime.CupboardInventory.Pointer ? "cupboard" : "";
                return Send(__instance._venue, "business-transfer", new BusinessIntent { Storage = storage, ItemId = __instance._selectedStack?.Type?.Guid ?? "",
                    Freshness = (int)__instance._currentFreshness, Amount = __instance._currentAmount, ToVenue = __instance._transferringToContainer });
            }
            catch (Exception ex) { ShowNotice(ex.Message); return false; }
        }
        private static bool RestockPrefix(TransferUi __instance) => !clientAuthority || Send(__instance._venue, "business-restock", new BusinessIntent());
        private static bool EvenRestockPrefix(TransferUi __instance) => !clientAuthority || Send(__instance._venue, "business-restock", new BusinessIntent { Enabled = true });

        /// <summary>Chain after dialogue. null = unhandled; true = safe native UI opening.</summary>
        public static bool? TryRouteClientInteraction(IInteractable focus)
        {
            if (!clientAuthority || focus == null) return null;
            var sign = focus.TryCast<VenueSignInteraction>();
            if (sign != null && sign.venue != null && sign.venue.RuntimeData != null && sign.venue.RuntimeData.PlayerOwned) return true;
            // Storage opening itself is personal UI. All transfer and restock
            // commit controls are intercepted above; worker enforces ownership.
            if (focus.TryCast<StorageInteractable>() != null) return true;
            var ingredient = focus.TryCast<IngredientProcessorView>();
            if (ingredient?.MyGhost != null) return Send(ingredient.MyGhost.Venue.Value?.Venue, "business-craft", new BusinessIntent { ProcessorId = ingredient.MyGhost.Id });
            var food = focus.TryCast<FoodProcessor>();
            if (food?.MyGhost != null) return Send(food.MyGhost.Venue.Value?.Venue, "business-craft", new BusinessIntent { ProcessorId = food.MyGhost.Id });
            return null;
        }
        public static WorldEvent HandleInteraction(InteractionRequest request)
        {
            if (!DedicatedWorldRuntime.IsDedicatedServer || clientAuthority) throw new InvalidOperationException("Business actions require the dedicated worker.");
            if (request == null || !DedicatedProtocol.Text(request.PlayerId, 128) || !DedicatedProtocol.Text(request.RequestId, 64) || !DedicatedProtocol.Text(request.TargetId, 128)) throw new ArgumentException("Invalid business request.");
            string key = request.PlayerId + ":" + request.RequestId;
            if (Results.TryGetValue(key, out var prior)) return prior;
            var result = new BusinessResult { PlayerId = request.PlayerId, RequestId = request.RequestId };
            bool changed = false;
            try
            {
                if (!DedicatedWorldRuntime.IsWorldReady || faulted) throw new InvalidOperationException("Business management requires a ready shared world.");
                var actor = RequireActor(request.PlayerId);
                RequireActorAccount(actor, request.AccountId);
                var runtime = GhostManager.Instance.GetGhostById(request.TargetId)?.TryCast<VenueAreaGhost>();
                var player = PlayerManager.Instance.LocalPlayer;
                if (runtime == null || runtime.Venue == null || !runtime.PlayerOwned || !player.DoesOwnProperty(runtime.Venue)) throw new InvalidOperationException("This venue is not owned by the shared group.");
                PlayerAccountRuntime.RequireBusinessOwner(request.AccountId, runtime);
                var intent = request.Data.Deserialize<BusinessIntent>(DedicatedProtocol.Json) ?? throw new ArgumentException("Missing business action.");
                var venue = runtime.Venue;
                switch (request.Kind)
                {
                    case "business-open":
                        // Native management UI permits opening before staffing;
                        // readiness checks remain native customer simulation.
                        runtime.IsOpen.Value = intent.Enabled; changed = true; break;
                    case "business-hire":
                    {
                        var person = ResolvePerson(intent.PersonId);
                        BusinessStateReplication.TrackPerson(person);
                        var tasks = ValidateTasks(intent.Task, person, runtime);
                        if (person.RuntimeData == null || !person.RuntimeData.Hireable || person.RuntimeData.HasWork || runtime.staff.Count >= runtime.StaffLimit)
                            throw new InvalidOperationException("This person is unavailable or the venue has no staff slots.");
                        VenueManager.Instance.HireStaff(venue, person);
                        if (!person.RuntimeData.HasWork || person.RuntimeData._worksAt != venue) throw new InvalidOperationException("The worker rejected this hire.");
                        VenueManager.Instance.ChangeStaffTasks(venue, person.RuntimeData, tasks); changed = true; break;
                    }
                    case "business-fire":
                    { var person = RequireStaff(runtime, intent.PersonId); BusinessStateReplication.TrackPerson(person); VenueManager.Instance.FireStaff(venue, person); changed = true; break; }
                    case "business-wage":
                    {
                        if (intent.Delta != -1 && intent.Delta != 1) throw new ArgumentException("Invalid wage adjustment.");
                        var person = RequireStaff(runtime, intent.PersonId); var range = person.RuntimeData.CalculateBaseWage();
                        person.RuntimeData.Wage = Mathf.Clamp(person.RuntimeData.Wage + intent.Delta, range.x, range.y); changed = true; break;
                    }
                    case "business-hours":
                    {
                        if (!float.IsFinite(intent.Start) || !float.IsFinite(intent.End) || intent.Start < 8 || intent.End > 26 || intent.Start >= intent.End)
                            throw new ArgumentException("Invalid staff shift.");
                        var person = RequireStaff(runtime, intent.PersonId);
                        person.RuntimeData.WorkingHours = new Vector2(intent.Start, intent.End); runtime.ReportStaffWorkingHoursChanged(); changed = true; break;
                    }
                    case "business-task":
                    {
                        var person = RequireStaff(runtime, intent.PersonId);
                        if (intent.Task != (int)VenueTasks.Serving && intent.Task != (int)VenueTasks.Cooking && intent.Task != (int)VenueTasks.Cleaning && intent.Task != (int)VenueTasks.Managing) throw new ArgumentException("Invalid staff task.");
                        int value = intent.Enabled ? (int)person.RuntimeData.Tasks | intent.Task : (int)person.RuntimeData.Tasks & ~intent.Task;
                        VenueManager.Instance.ChangeStaffTasks(venue, person.RuntimeData, ValidateTasks(value, person, runtime)); changed = true; break;
                    }
                    case "business-menu-add":
                    {
                        var recipe = ResolveKnownRecipe(intent.RecipeId);
                        if (runtime.Menu.Count >= runtime.MenuLimit || runtime.Menu.ContainsMeal(recipe)) throw new InvalidOperationException("This recipe is already listed or the menu is full.");
                        bool validType = false; foreach (var type in venue.RecipeType) if (type == recipe.BaseDefinition.RecipeType) validType = true;
                        if (!validType || recipe.BaseDefinition.Disabled || recipe.BaseDefinition.PlayerOnly) throw new InvalidOperationException("This venue cannot serve that recipe.");
                        var entry = new MealMenuItem { recipe = recipe.BaseDefinition, _recipe = recipe, price = Math.Max(recipe.Output.type.MinMealPrice, recipe.Output.type.MaxMealPrice) };
                        runtime.Menu.Add(entry); VenueManager.Instance.TriggerMenuUpdatedEvent(); changed = true; break;
                    }
                    case "business-menu-remove":
                    { runtime.Menu.Remove(FindMenu(runtime, intent.RecipeId)); VenueManager.Instance.TriggerMenuUpdatedEvent(); changed = true; break; }
                    case "business-price":
                    {
                        if (intent.Delta != -100 && intent.Delta != 100) throw new ArgumentException("Invalid menu price adjustment.");
                        var entry = FindMenu(runtime, intent.RecipeId); int minimum = entry.Meal.MinMealPrice;
                        int maximum = entry.Meal.MaxMealPrice <= 0 ? int.MaxValue : (int)(entry.Meal.MaxMealPrice * venue.GetPriceMultiplier() / 100f) * 100;
                        entry.Price = (int)Math.Clamp((long)entry.Price + intent.Delta, minimum, Math.Max(minimum, maximum)); changed = true; break;
                    }
                    case "business-transfer":
                        RequireNearby(actor, runtime); Transfer(runtime, intent); changed = true; break;
                    case "business-restock":
                        RequireNearby(actor, runtime); Restock(runtime, intent.Enabled); changed = true; break;
                    case "business-craft":
                        BeginCraft(request, actor, runtime, intent.ProcessorId); result.NeedsCheckpoint = false; changed = true; break;
                    default: throw new InvalidOperationException("Unknown business action.");
                }
                result.Success = true; result.Message = request.Kind == "business-craft" ? "Preparing the shared order. Stay near this workstation." : "Shared venue updated.";
            }
            catch (Exception ex) { result.Message = ex.Message; }
            var evt = new WorldEvent { Kind = "business-result", TargetPlayerId = request.PlayerId, Data = JsonSerializer.SerializeToElement(result, DedicatedProtocol.Json) };
            Results[key] = evt; ResultOrder.Enqueue(key);
            while (ResultOrder.Count > 4096) Results.Remove(ResultOrder.Dequeue());
            if (changed)
                try { foreach (var update in CaptureEvents()) OutboundWorldEvent?.Invoke(update); }
                catch (Exception ex) { Notice?.Invoke("Business action committed; refresh failed: " + ex.Message); }
            return evt;
        }
        private static PlayerPose RequireActor(string id)
        {
            if (!Players.TryGetValue(id, out var actor) || !actor.Pose.Active || Time.realtimeSinceStartup - actor.Time > 3)
                throw new InvalidOperationException("Your character position is unavailable.");
            return actor.Pose;
        }
        private static void RequireActorAccount(PlayerPose actor, string accountId)
        {
            if (!Guid.TryParseExact(accountId, "N", out _) || actor == null || actor.AccountId != accountId)
                throw new InvalidOperationException("The character is not bound to this account.");
        }
        private static void RequireNearby(PlayerPose actor, VenueAreaGhost venue)
        {
            string scene = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(venue.SceneIndex));
            if (!string.Equals(scene, actor.Scene, StringComparison.Ordinal)) throw new InvalidOperationException("Travel to this venue to move stock.");
            // Venue ghosts represent the whole floor plan, not a single storage
            // handle. Native property membership is evaluated at actor position.
            var point = new Vector3(actor.X, actor.Y, actor.Z);
            if ((point - venue.Position).sqrMagnitude > 1600) throw new InvalidOperationException("Move inside this venue to transfer stock.");
        }
        private static Person ResolvePerson(string id)
        {
            if (!DedicatedProtocol.Text(id, 128)) throw new ArgumentException("Invalid staff member.");
            var person = ScriptableObjectDatabase.Instance.Provide<Person>(id);
            return person != null && person.RuntimeData != null ? person : throw new ArgumentException("Unknown staff member.");
        }

        private static void RequireNearProcessor(PlayerPose actor, Ghost processor)
        {
            string scene = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(processor.SceneIndex));
            if (!processor.IsActive || !processor.IsValid || !string.Equals(scene, actor.Scene, StringComparison.Ordinal) ||
                (new Vector3(actor.X, actor.Y, actor.Z) - processor.Position).sqrMagnitude > 64)
                throw new InvalidOperationException("Stay near this workstation to prepare the order.");
        }
        private static void BeginCraft(InteractionRequest request, PlayerPose actor, VenueAreaGhost venue, string processorId)
        {
            if (!DedicatedProtocol.Text(processorId, 128)) throw new ArgumentException("Invalid workstation.");
            if (CraftJobs.ContainsKey(request.PlayerId)) throw new InvalidOperationException("Finish your current workstation task first.");
            var processor = GhostManager.Instance.GetGhostById(processorId);
            if (processor == null) throw new InvalidOperationException("This workstation is unavailable.");
            RequireNearProcessor(actor, processor);
            var ingredient = processor.TryCast<IngredientProcessorGhost>();
            var food = processor.TryCast<FoodProcessorGhost>();
            StaffTaskQueue.StaffTask task = null;
            float duration;
            if (ingredient != null)
            {
                if (ingredient.Venue.Value?.Id != venue.Id || ingredient.IsInUse || ingredient.ReservedBy.HasValue || ingredient.Type == null)
                    throw new InvalidOperationException("This workstation is busy or belongs to a different venue.");
                duration = ingredient.GetCookingTime();
                task = venue.TaskQueue.DequeueProcessingTask(ingredient.Type.AvailableProcessingTypes);
            }
            else if (food != null)
            {
                if (food.Venue.Value?.Id != venue.Id || food.IsInUse || food.ReservedBy.HasValue)
                    throw new InvalidOperationException("This workstation is busy or belongs to a different venue.");
                // Native drink-vs-food selection is defined by the loaded view;
                // reject absent views instead of guessing serialized prefab data.
                FoodProcessor view = null;
                foreach (var candidate in Object.FindObjectsOfType<FoodProcessor>(true)) if (candidate?.MyGhost?.Id == processor.Id) { view = candidate; break; }
                if (view == null) throw new InvalidOperationException("The workstation's district is not loaded on the worker.");
                duration = food.GetCookingTime();
                task = venue.TaskQueue.DequeueMakeMealTask(view.IsDrinksMachine);
            }
            else throw new InvalidOperationException("This object is not a cooking workstation.");
            if (task == null) throw new InvalidOperationException("There is no ready order task for this workstation.");
            var job = new CraftJob { PlayerId = request.PlayerId, RequestId = request.RequestId, AccountId = request.AccountId, Processor = processor, Venue = venue, Task = task };
            try
            {
                if (task.IsCompleted || task.IsObsolete || !float.IsFinite(duration)) throw new InvalidOperationException("This order is no longer available.");
                var processing = task.TryCast<StaffTaskQueue.ProcessIngredientTask>();
                if (processing != null) duration *= processing.ProcessingTimeMultiplier;
                if (!float.IsFinite(duration) || duration <= 0) throw new InvalidOperationException("The workstation has invalid preparation timing.");
                job.FinishTime = Time.realtimeSinceStartup + Mathf.Clamp(duration, .1f, 180f);
                if (ingredient != null) { ingredient.IsInUse = true; ingredient.ReservedBy = new GhostReference<Ghost>(processor); }
                else { food.IsInUse = true; food.ReservedBy = new GhostReference<Ghost>(processor); }
                CraftJobs.Add(request.PlayerId, job);
            }
            catch { CancelJob(job); throw; }
        }
        /// <summary>Call every worker Unity Update. Each actor owns at most one timed task.</summary>
        public static void Tick()
        {
            if (clientAuthority || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady || CraftJobs.Count == 0) return;
            foreach (var id in new List<string>(CraftJobs.Keys))
            {
                var job = CraftJobs[id];
                try
                {
                    var actor = RequireActor(id); RequireActorAccount(actor, job.AccountId); RequireNearProcessor(actor, job.Processor);
                    PlayerAccountRuntime.RequireBusinessOwner(job.AccountId, job.Venue);
                    if (!job.Venue.PlayerOwned || job.Task.IsObsolete) throw new InvalidOperationException("The order is no longer available.");
                    if (Time.realtimeSinceStartup < job.FinishTime) continue;
                    using (PlayerAccountRuntime.WithAccount(job.AccountId))
                    {
                        try { CompleteJob(job); }
                        catch { if (job.Committing) PlayerAccountRuntime.Fault("A native account craft failed after mutation began."); throw; }
                    }
                    ReleaseProcessor(job); CraftJobs.Remove(id);
                    // Sending an update cannot turn a committed native craft
                    // into a gameplay failure or cause its task to replay.
                    try { EmitCraftResult(job, true, "Shared order preparation completed."); foreach (var evt in CaptureEvents()) OutboundWorldEvent?.Invoke(evt); }
                    catch (Exception ex) { Notice?.Invoke("Shared craft committed; refresh failed: " + ex.Message); }
                }
                catch (Exception ex)
                {
                    if (job.Committing) { faulted = true; PlayerAccountRuntime.Fault("A native account craft could not finish safely."); }
                    else CancelJob(job);
                    ReleaseProcessor(job); CraftJobs.Remove(id);
                    EmitCraftResult(job, false, ex.Message);
                }
            }
        }
        private static void CompleteJob(CraftJob job)
        {
            var ingredientTask = job.Task.TryCast<StaffTaskQueue.ProcessIngredientTask>();
            if (ingredientTask != null)
            {
                job.Committing = true; ingredientTask.IsCompleted = true;
                job.Venue.TaskQueue.OnProcessIngredientTaskCompleted(ingredientTask);
                PlayerManager.Instance.LocalPlayer.AddExperience(CommonReferences.Instance.CookingSkill, 1);
                return;
            }
            var mealTask = job.Task.TryCast<StaffTaskQueue.MakeMealTask>();
            var processor = job.Processor.TryCast<FoodProcessorGhost>();
            if (mealTask == null || processor == null || mealTask.TaskInventory == null || mealTask.Order == null || !mealTask.Order.IsValid ||
                mealTask.ProcessingTasksCompleted < mealTask.ProcessingTasksRequired) throw new InvalidOperationException("The meal is not ready to prepare.");
            var recipe = MealDatabase.Instance.GetMealRecipeFromItemType(mealTask.Meal);
            if (recipe == null || mealTask.TaskInventory.ItemCount == 0 || !recipe.CheckInputs(mealTask.TaskInventory.Cast<IItemContainer>())) throw new InvalidOperationException("The order has insufficient reserved ingredients.");
            var cooking = PlayerManager.Instance.LocalPlayer.GetSkill<Nivalis.SkillSystem.CookingLevel>(CommonReferences.Instance.CookingSkill.Cast<Nivalis.SkillSystem.CookingSkillLevels>());
            job.Committing = true;
            var meal = processor.CraftRecipe(recipe, mealTask.OrderReference);
            if (meal == null) throw new InvalidOperationException("The native processor did not create a meal.");
            meal.ProcessorMade = new GhostReference<FoodProcessorGhost>(processor);
            meal.Quality = cooking.PreparationQuality; meal.Freshness = mealTask.Freshness;
            processor.AddMeal(new GhostReference<MealGhost>(meal));
            mealTask.TaskInventory.Clear(); mealTask.IsCompleted = true;
            mealTask.Order.PreparedMealTypes.Add(meal.Type);
            job.Venue.TaskQueue.EnqueueServeTask(mealTask.Order, meal);
            GhostManager.Instance.FlushGhostOperationQueues();
            PlayerManager.Instance.LocalPlayer.AddExperience(CommonReferences.Instance.CookingSkill, 1);
        }
        private static void CancelJob(CraftJob job)
        {
            if (job == null) return;
            try { if (!job.Committing && job.Task != null && !job.Task.IsCompleted && !job.Task.IsObsolete && job.Venue?.TaskQueue != null) job.Venue.TaskQueue.ReEnqueueTask(job.Task); }
            finally { ReleaseProcessor(job); }
        }
        private static void ReleaseProcessor(CraftJob job)
        {
            if (job?.Processor == null) return;
            var ingredient = job.Processor.TryCast<IngredientProcessorGhost>(); var food = job.Processor.TryCast<FoodProcessorGhost>();
            if (ingredient != null && ingredient.ReservedBy.ReferencedId == job.Processor.Id) { ingredient.ReservedBy = GhostReference<Ghost>.Null; ingredient.IsInUse = false; }
            if (food != null && food.ReservedBy.ReferencedId == job.Processor.Id) { food.ReservedBy = GhostReference<Ghost>.Null; food.IsInUse = false; }
        }
        private static void EmitCraftResult(CraftJob job, bool success, string message)
        {
            OutboundWorldEvent?.Invoke(new WorldEvent { Kind = "business-result", TargetPlayerId = job.PlayerId, Data = JsonSerializer.SerializeToElement(new BusinessResult {
                PlayerId = job.PlayerId, RequestId = job.RequestId, Success = success, Message = message
            }, DedicatedProtocol.Json) });
        }
        private static Person RequireStaff(VenueAreaGhost venue, string id)
        {
            var person = ResolvePerson(id);
            if (!person.RuntimeData.HasWork || person.RuntimeData._worksAt != venue.Venue || !venue.staff.Contains(person)) throw new InvalidOperationException("This person is not employed here.");
            return person;
        }
        private static VenueTasks ValidateTasks(int value, Person person, VenueAreaGhost venue)
        {
            var all = VenueTasks.Serving | VenueTasks.Cooking | VenueTasks.Cleaning | VenueTasks.Managing;
            var tasks = (VenueTasks)value;
            if (value == 0 || (tasks & ~all) != 0 || !person.HasSkills(tasks) || (tasks & VenueTasks.Managing) != 0 && venue.ManagerLocked)
                throw new InvalidOperationException("This staff assignment is unavailable.");
            return tasks;
        }
        private static IRecipe ResolveKnownRecipe(string id)
        {
            var database = MealDatabase.Instance;
            if (!DedicatedProtocol.Text(id, 128) || database == null) throw new ArgumentException("Unknown recipe.");
            foreach (var definition in database.knownRecipes) if (definition != null && definition.Guid == id) return database.GetRuntimeRecipe(definition);
            throw new InvalidOperationException("That recipe is not unlocked in the shared world.");
        }
        private static MealMenuItem FindMenu(VenueAreaGhost venue, string id)
        {
            if (!DedicatedProtocol.Text(id, 128)) throw new ArgumentException("Invalid menu item.");
            for (int i = 0; i < venue.Menu.Count; i++) if (venue.Menu[i].RecipeDefinition?.Guid == id) return venue.Menu[i];
            throw new InvalidOperationException("This recipe is no longer on the menu.");
        }

        // Staged snapshots and exact-instance transfer code live below. A native
        // rejection rolls back both containers and faults further transfers.
        private static void Transfer(VenueAreaGhost venue, BusinessIntent intent)
        {
            if (intent.Amount < 1 || intent.Amount > 1000 || !DedicatedProtocol.Text(intent.ItemId, 128) ||
                (intent.Freshness != (int)FoodFreshness.Fresh && intent.Freshness != (int)FoodFreshness.Aging && intent.Freshness != (int)FoodFreshness.Spoiled)) throw new ArgumentException("Invalid transfer quantity or freshness.");
            var storage = intent.Storage == "fridge" ? venue.FridgeInventory : intent.Storage == "cupboard" ? venue.CupboardInventory : null;
            if (storage == null) throw new ArgumentException("Invalid venue storage.");
            var player = PlayerManager.Instance.LocalPlayer.Inventory.Items;
            var source = intent.ToVenue ? player : storage; var destination = intent.ToVenue ? storage : player;
            var type = ScriptableObjectDatabase.Instance.Provide<ItemType>(intent.ItemId);
            if (type == null || type.IsFurniture || !type.IsPlayerStorable) throw new InvalidOperationException("This item cannot be transferred here.");
            MoveItems(source, destination, type, (FoodFreshness)intent.Freshness, intent.Amount);
        }
        private static void MoveItems(ItemContainer source, ItemContainer destination, ItemType type, FoodFreshness freshness, int amount)
        {
            if (!destination.Restriction.CanAddItem(destination, type, amount)) throw new InvalidOperationException("The destination storage is full.");
            var selected = new List<(ItemStack Stack, ItemInstanceData Item)>();
            foreach (var stack in SharedEconomyReplication.SnapshotStacks(source)) {
                if (stack?.Type == null || stack.Type.Guid != type.Guid) continue;
                for (int i = stack._instanceData.Count - 1; i >= 0 && selected.Count < amount; i--) if (stack._instanceData[i].Freshness == freshness) selected.Add((stack, stack._instanceData[i]));
                if (selected.Count == amount) break;
            }
            if (selected.Count != amount) throw new InvalidOperationException("The requested items are no longer available.");
            var sourceBackup = BusinessStateReplication.PrepareContainer(BusinessStateReplication.CaptureContainer(source), source);
            var destinationBackup = BusinessStateReplication.PrepareContainer(BusinessStateReplication.CaptureContainer(destination), destination);
            int sourceCount = source.ItemCount, destinationCount = destination.ItemCount;
            try
            {
                var stack = ItemStack.SafeCreateEmpty(type, amount);
                foreach (var row in selected) { if (!source.TryTake(row.Stack, row.Item)) throw new InvalidOperationException("Stock changed during transfer."); stack.AddInstance(row.Item); }
                if (destination.TryAdd(stack) != ItemCollectionOperationResult.Succeeded || source.ItemCount != sourceCount - amount || destination.ItemCount != destinationCount + amount)
                    throw new InvalidOperationException("The native transfer did not complete.");
                source.Refresh(); destination.Refresh();
            }
            catch
            {
                BusinessStateReplication.ReplaceContainerRaw(source, sourceBackup); BusinessStateReplication.ReplaceContainerRaw(destination, destinationBackup);
                faulted = true; throw;
            }
        }
        private static void Restock(VenueAreaGhost venue, bool evenly)
        {
            var player = PlayerManager.Instance.LocalPlayer.Inventory.Items;
            Il2CppSystem.Collections.Generic.HashSet<ItemType> used;
            var handle = venue.Venue.GetItemsUsedInVenue(out used);
            var types = new List<ItemType>();
            var enumerator = used.GetEnumerator(); try { while (enumerator.MoveNext()) types.Add(enumerator.Current); } finally { enumerator.Dispose(); handle.Dispose(); }
            types.Sort((a,b) => string.CompareOrdinal(a.Guid,b.Guid));
            int moved = 0;
            // One-at-a-time native transfers guarantee capacity validation and
            // give equal-share restock a fair round-robin across menu ingredients.
            bool any;
            do
            {
                any = false;
                foreach (var type in types)
                {
                    var destination = type.RequiresRefridgeration ? venue.FridgeInventory : venue.CupboardInventory;
                    foreach (var freshness in new[] { FoodFreshness.Aging, FoodFreshness.Fresh })
                    {
                        int count = CountFresh(player, type, freshness);
                        if (count == 0 || !destination.Restriction.CanAddItem(destination, type, 1)) continue;
                        int amount = evenly ? 1 : Math.Min(count, 1000 - moved);
                        while (amount > 1 && !destination.Restriction.CanAddItem(destination, type, amount)) amount--;
                        MoveItems(player, destination, type, freshness, amount); moved += amount; any = true;
                        if (moved >= 1000) return;
                    }
                }
            } while (evenly && any);
            if (moved == 0) throw new InvalidOperationException("No menu ingredients fit in this venue's storage.");
        }
        private static int CountFresh(ItemContainer source, ItemType type, FoodFreshness freshness)
        {
            int count = 0;
            foreach (var stack in SharedEconomyReplication.SnapshotStacks(source)) {
                if (stack?.Type == null || stack.Type.Guid != type.Guid) continue;
                foreach (var item in stack._instanceData) if (item.Freshness == freshness) count++;
            }
            return count;
        }
        public static WorldEvent[] CaptureEvents()
        {
            var events = new List<WorldEvent>();
            events.AddRange(BusinessStateReplication.CaptureEvents(BusinessStateReplication.Capture()));
            events.AddRange(BusinessCraftStateReplication.CaptureEvents());
            // Personal inventory/wallet updates are captured by the account runtime
            // and delivered only to that account after the paired checkpoint.
            return events.ToArray();
        }
        public static void Apply(WorldEvent evt)
        {
            if (!clientAuthority || evt == null) return;
            if (evt.Kind == "business-state-chunk") { BusinessStateReplication.Apply(evt); return; }
            if (evt.Kind == "business-craft-chunk") { BusinessCraftStateReplication.Apply(evt); return; }
            if (evt.Kind != "business-result") return;
            var result = evt.Data.Deserialize<BusinessResult>(DedicatedProtocol.Json);
            if (result != null && result.PlayerId == LocalPlayerId) ShowNotice(result.Message);
        }
        private static void RefreshUi()
        {
            refreshing = true;
            try
            {
                foreach (var ui in ReplicatedUiRegistry.Snapshot<TransferUi>())
                    if (VisibleBusinessUi(ui) && ui.IsOpen && ui._venue?.RuntimeData != null && ui._currentContainer != null && ui.inventoryList != null && ui.containerList != null)
                    { ui.RefreshLists(); ui.RefreshWarnings(); ui.RefreshMiddlePanel(); }
                foreach (var ui in ReplicatedUiRegistry.Snapshot<VenueOverviewTabGeneralSection>())
                    if (VisibleBusinessUi(ui) && ui._venue?.RuntimeData != null) ui.Show(ui._venue);
                foreach (var ui in ReplicatedUiRegistry.Snapshot<VenueStaffOverviewTab>())
                    if (VisibleBusinessUi(ui) && ui._forVenue?.RuntimeData != null && ui.staffList != null) { ui.RefreshStaffList(ui._forVenue); ui.RefreshWorkingHours(); }
                foreach (var ui in ReplicatedUiRegistry.Snapshot<HireStaffWindow>())
                    if (VisibleBusinessUi(ui) && ui._forVenue?.RuntimeData != null && ui.staffList != null) ui.RefreshStaffList();
                foreach (var ui in ReplicatedUiRegistry.Snapshot<MenuModificationWindow>())
                    if (VisibleBusinessUi(ui) && ui._venue?.RuntimeData != null && ui.menuItemList != null && ui.recipeItemList != null)
                    { ui.RefreshMenuList(); ui.RefreshRecipeList(); ui.RefreshSatisfaction(); }
            }
            finally { refreshing = false; }
        }
        private static bool VisibleBusinessUi(MonoBehaviour component)
        {
            if (component == null || !component.isActiveAndEnabled || !component.gameObject.activeInHierarchy) return false;
            // Native menus often stay active while hidden by their panel/window.
            // Refreshing one before Open binds its containers dereferences null
            // inside TransferMenuUI.SpawnList. Check the owning panel hierarchy,
            // including closed parent windows of still-visible child tabs.
            bool hasPanel = false;
            foreach (var panel in component.GetComponentsInParent<UIPanel>(true))
            {
                if (panel == null) continue;
                hasPanel = true;
                if (!panel.IsVisible) return false;
                var window = panel.TryCast<UIWindow>();
                if (window != null && !window.IsOpen) return false;
            }
            return hasPanel;
        }
        private static void ShowNotice(string message)
        {
            if (Time.realtimeSinceStartup < nextNotice) return; nextNotice = Time.realtimeSinceStartup + 1;
            Notice?.Invoke(message);
            try { if (clientAuthority && NotificationManager.InstanceNotNull()) NotificationManager.Instance.CreateMessage("Shared business", message, null); } catch { }
        }
    }
}
