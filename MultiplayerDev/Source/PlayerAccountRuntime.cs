using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.GhostSystem.Ai;
using Nivalis.InventorySystem;
using Nivalis.UI.EndOfDay;

namespace NivalisTogether.Dedicated
{
    /// <summary>Unity-thread account contexts on the existing native local inventory.</summary>
    public static class PlayerAccountRuntime
    {
        // Native currency uses hundredths: 500,000 units displays as 5,000 limes.
        // This grant applies only to a previously uninitialized server account.
        public const int StartingLimes = 5000;
        public const int StartingMoney = StartingLimes * 100;
        private static AccountWorldState ledger;
        private static readonly Dictionary<string, AccountState> accounts = new Dictionary<string, AccountState>(StringComparer.Ordinal);
        private static readonly Stack<Scope> scopes = new Stack<Scope>();
        private static readonly PersonalInventoryState Neutral = new PersonalInventoryState();
        private static long generation, committedGeneration, capturedGeneration, capturedRevision = -1;
        private static int mainThread;
        private static bool initialized;
        private static Harmony hooks;
        private static int decayDay = int.MinValue;
        private static readonly Queue<string> decayQueue = new Queue<string>();
        public static bool FailClosed { get; private set; }
        public static string FailureReason { get; private set; } = "";
        public static string CurrentAccountId => scopes.Count == 0 ? "" : scopes.Peek().AccountId;
        public static bool IsDirty => initialized && generation != committedGeneration;
        public static AccountInventoryState LegacyEscrow => ledger?.LegacyEscrow == null ? null : Clone(ledger.LegacyEscrow);
        public static int LegacyEscrowMoney => ledger?.LegacyEscrow?.Money ?? 0;

        public static void InitializeWorker(AccountWorldState state)
        {
            if (initialized) throw new InvalidOperationException("Account runtime is already initialized.");
            if (!DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady)
                throw new InvalidOperationException("Account authority requires the isolated worker world.");
            mainThread = Thread.CurrentThread.ManagedThreadId;
            AccountProtocol.Validate(state, state?.WorldId, state?.Revision ?? -1);
            ledger = state; accounts.Clear(); generation = committedGeneration = 0; capturedRevision = -1;
            foreach (var account in state.Accounts)
            {
                ValidateAccountItems(account); accounts.Add(account.AccountId, account);
            }
            // Only a missing escrow record permits the one-time legacy import.
            // Later checkpoints must contain a neutral native wallet/inventory.
            var native = SharedEconomyReplication.CapturePersonal();
            if (state.LegacyEscrow == null)
            {
                state.LegacyEscrow = Encode(native); generation++;
            }
            else
            {
                Decode(state.LegacyEscrow);
                if (native.Money != 0 || native.Inventory.Length != 0)
                    throw new InvalidDataException("An account checkpoint contains unattributed native personal assets.");
            }
            SharedEconomyReplication.ApplyPersonal(Neutral);
            initialized = true; FailClosed = false; FailureReason = "";
            try { InstallBackgroundHooks(); }
            catch { Fault("Native account attribution hooks could not be installed."); throw; }
        }

        public static AccountState EnsureAccount(AccountPreparation preparation)
        {
            RequireWorker(); RequireNeutral();
            if (preparation == null || !Guid.TryParseExact(preparation.AccountId, "N", out _) ||
                !PlayerAppearance.CanResolve(preparation.AppearanceId)) throw new InvalidDataException("Invalid approved account profile.");
            if (!accounts.TryGetValue(preparation.AccountId, out var account))
            {
                if (accounts.Count >= AccountProtocol.MaxAccounts) throw new InvalidOperationException("Account limit reached.");
                account = new AccountState { AccountId = preparation.AccountId };
                accounts.Add(account.AccountId, account);
                var all = new List<AccountState>(ledger.Accounts) { account }; ledger.Accounts = all.ToArray();
            }
            if (!account.Initialized)
            {
                var manager = GameSceneManager._instance;
                var map = manager?.bootManager?.configurationMap;
                if (map == null || !map.TryGetValue(Il2CppType.Of<PlayerManagerConfiguration>(), out var raw))
                    throw new InvalidOperationException("Effective native player configuration is unavailable.");
                var configuration = raw?.TryCast<PlayerManagerConfiguration>();
                var start = SharedEconomyReplication.CreateStartingInventory(configuration);
                start.Money = StartingMoney;
                account.Money = start.Money; account.Inventory = Encode(start).Inventory;
                account.Initialized = true; account.StateRevision++; generation++;
            }
            if (account.AppearanceId != preparation.AppearanceId)
            {
                account.AppearanceId = preparation.AppearanceId; account.StateRevision++; generation++;
            }
            AccountProtocol.Validate(account);
            // Offline time is caught up by the native decay algorithm before a
            // reconnecting client receives its private inventory.
            using (WithAccount(account.AccountId)) { }
            return Clone(account);
        }

        public static AccountState GetAccount(string accountId)
        {
            RequireWorker();
            if (!accounts.TryGetValue(accountId ?? "", out var account) || !account.Initialized)
                throw new InvalidOperationException("The account is not initialized.");
            return Clone(account);
        }

        public static PlayerMapLocation GetSpawnLocation()
        {
            RequireWorker();
            return PlayerMapLocation.Copy(ledger.SpawnLocation);
        }
        public static PlayerBoatState GetBoat(string accountId)
        {
            RequireWorker();
            return BoatProtocol.Copy(BoatAccount(accountId).Boat);
        }
        public static PlayerBoatState[] ListBoats()
        {
            RequireWorker();
            var result = new List<PlayerBoatState>();
            foreach (var account in ledger.Accounts)
                if (account.Initialized && account.Boat != null) result.Add(BoatProtocol.Copy(account.Boat));
            return result.ToArray();
        }
        /// <summary>One-time boat grant for old and new accounts; never repeats the personal starting grant.</summary>
        public static PlayerBoatState EnsureBoat(string accountId, PlayerBoatState starting)
        {
            RequireWorker(); RequireBoatScope(accountId);
            var account = BoatAccount(accountId);
            if (account.Boat != null) return BoatProtocol.Copy(account.Boat);
            BoatProtocol.Validate(starting);
            if (starting.OwnerAccountId != accountId) throw new InvalidDataException("Boat grant owner does not match the account.");
            RequireUniqueBoat(accountId, starting.BoatId);
            long revision = checked(account.StateRevision + 1), nextGeneration = checked(generation + 1);
            var value = BoatProtocol.Copy(starting); value.Revision = 1;
            account.Boat = value; account.StateRevision = revision; generation = nextGeneration;
            return BoatProtocol.Copy(value);
        }
        /// <summary>Persist the owner's boat beside its wallet, including synchronous refueling transactions.</summary>
        public static PlayerBoatState SetBoat(string accountId, PlayerBoatState value)
        {
            RequireWorker(); RequireBoatScope(accountId); BoatProtocol.Validate(value);
            var account = BoatAccount(accountId);
            if (account.Boat == null) throw new InvalidOperationException("The account has no granted boat.");
            if (value.OwnerAccountId != accountId || value.BoatId != account.Boat.BoatId || value.PrefabId != account.Boat.PrefabId)
                throw new InvalidDataException("Boat update changed its persistent owner, identity or prefab.");
            RequireUniqueBoat(accountId, value.BoatId);
            long boatRevision = checked(account.Boat.Revision + 1), revision = checked(account.StateRevision + 1),
                nextGeneration = checked(generation + 1);
            var replacement = BoatProtocol.Copy(value); replacement.Revision = boatRevision;
            account.Boat = replacement; account.StateRevision = revision; generation = nextGeneration;
            return BoatProtocol.Copy(replacement);
        }
        private static AccountState BoatAccount(string accountId)
        {
            if (!accounts.TryGetValue(accountId ?? "", out var account) || !account.Initialized)
                throw new InvalidOperationException("The account is not initialized.");
            return account;
        }
        private static void RequireBoatScope(string accountId)
        {
            if (scopes.Count == 0) RequireNeutral();
            else if (CurrentAccountId != accountId)
                throw new InvalidOperationException("Boat persistence belongs to a different active account transaction.");
        }
        private static void RequireUniqueBoat(string accountId, string boatId)
        {
            foreach (var account in ledger.Accounts)
                if (account.AccountId != accountId && string.Equals(account.Boat?.BoatId, boatId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Boat identity already belongs to another account.");
        }
        /// <summary>Migrate the stable new-account spawn once, before worker focus can move.</summary>
        public static PlayerMapLocation EnsureSpawnLocation(PlayerMapLocation location)
        {
            RequireWorker(); PlayerMapLocation.Validate(location);
            if (scopes.Count != 0) throw new InvalidOperationException("Map persistence cannot overlap a native account transaction.");
            if (ledger.SpawnLocation == null)
            {
                long nextGeneration = checked(generation + 1);
                ledger.SpawnLocation = PlayerMapLocation.Copy(location); generation = nextGeneration;
            }
            return PlayerMapLocation.Copy(ledger.SpawnLocation);
        }
        public static bool SetMapLocation(string accountId, PlayerMapLocation location)
        {
            RequireWorker(); PlayerMapLocation.Validate(location);
            if (scopes.Count != 0) throw new InvalidOperationException("Map persistence cannot overlap a native account transaction.");
            if (!accounts.TryGetValue(accountId ?? "", out var account) || !account.Initialized)
                throw new InvalidOperationException("The account is not initialized.");
            var previous = account.Location;
            if (previous != null && previous.Scene == location.Scene && previous.PropertyId == location.PropertyId &&
                previous.PortalId == location.PortalId && previous.HasPosition == location.HasPosition && previous.X == location.X &&
                previous.Y == location.Y && previous.Z == location.Z && previous.Yaw == location.Yaw) return false;
            long revision = checked(account.StateRevision + 1), nextGeneration = checked(generation + 1);
            account.Location = PlayerMapLocation.Copy(location); account.StateRevision = revision; generation = nextGeneration;
            return true;
        }

        public static IDisposable WithAccount(string accountId)
        {
            RequireWorker();
            if (accountId != AccountProtocol.LegacyOwnerId && (!accounts.TryGetValue(accountId ?? "", out var account) || !account.Initialized))
                throw new InvalidOperationException("The authenticated account is not initialized.");
            var scope = new Scope { AccountId = accountId, Parent = CurrentAccountId };
            if (scope.Parent == accountId)
            {
                scope.Nested = true; scopes.Push(scope); return scope;
            }
            if (scopes.Count == 0) RequireNeutral();
            scope.Baseline = SharedEconomyReplication.CapturePersonal();
            if (scopes.Count != 0) Store(scope.Parent, scope.Baseline);
            var desired = accountId == AccountProtocol.LegacyOwnerId ? Decode(ledger.LegacyEscrow) : Decode(accounts[accountId]);
            try { SharedEconomyReplication.ApplyPersonal(desired); }
            catch { Fault("Could not activate private native inventory."); throw; }
            scopes.Push(scope);
            try { PlayerManager._instance.LocalPlayer.Inventory.Items.UpdateItemDecay(true, TimeOfDayManager.GameplayGameDay); }
            catch
            {
                Fault("Native private inventory decay failed."); scope.Dispose(); throw;
            }
            return scope;
        }

        private sealed class Scope : IDisposable
        {
            internal string AccountId, Parent;
            internal PersonalInventoryState Baseline;
            internal bool Nested, Disposed;
            public void Dispose()
            {
                if (Disposed) return;
                AssertThread();
                if (scopes.Count == 0 || !ReferenceEquals(scopes.Peek(), this))
                { Fault("Account scopes were disposed out of order."); throw new InvalidOperationException(FailureReason); }
                Disposed = true;
                try
                {
                    if (!Nested && !FailClosed) Store(AccountId, SharedEconomyReplication.CapturePersonal());
                }
                catch { Fault("Could not capture a native account transaction."); throw; }
                finally
                {
                    try
                    {
                        // A -> B -> A may update A while B is active. Restore the
                        // latest ledger state, never A's stale pre-B snapshot.
                        if (!Nested) SharedEconomyReplication.ApplyPersonal(Parent.Length == 0 ? Baseline :
                            Parent == AccountProtocol.LegacyOwnerId ? Decode(ledger.LegacyEscrow) : Decode(accounts[Parent]));
                    }
                    catch { Fault("Could not restore native account isolation."); throw; }
                    finally { scopes.Pop(); }
                }
            }
        }

        private static void Store(string accountId, PersonalInventoryState observed)
        {
            var original = accountId == AccountProtocol.LegacyOwnerId ? Decode(ledger.LegacyEscrow) : Decode(accounts[accountId]);
            if (SharedEconomyReplication.PersonalHash(original) == SharedEconomyReplication.PersonalHash(observed)) return;
            var encoded = Encode(observed);
            if (accountId == AccountProtocol.LegacyOwnerId) ledger.LegacyEscrow = encoded;
            else
            {
                var account = accounts[accountId]; account.Money = encoded.Money; account.Inventory = encoded.Inventory;
                account.StateRevision++;
            }
            generation++;
        }

        public static void Tick()
        {
            if (!initialized) return;
            RequireWorker();
            if (scopes.Count != 0) { Fault("A native account scope escaped its synchronous operation."); throw new InvalidOperationException(FailureReason); }
            RequireNeutral();
            int day = TimeOfDayManager.GameplayGameDay;
            if (day != decayDay)
            {
                decayDay = day; decayQueue.Clear();
                decayQueue.Enqueue(AccountProtocol.LegacyOwnerId);
                foreach (var account in ledger.Accounts) if (account.Initialized) decayQueue.Enqueue(account.AccountId);
            }
            // Bound routine day-change work; snapshots retain native timestamps,
            // so accounts awaiting this queue also catch up on their next use.
            for (int i = 0; i < 4 && decayQueue.Count != 0; i++)
                using (WithAccount(decayQueue.Dequeue())) { }
        }
        private static void RequireNeutral()
        {
            var player = PlayerManager._instance?.LocalPlayer;
            if (player?.Inventory == null || player.Inventory._money != 0 || player.Inventory.Items.ItemCount != 0)
            { Fault("Native money or items changed without an authenticated account owner."); throw new InvalidOperationException(FailureReason); }
        }

        public static AccountWorldState CaptureLedger(long revision)
        {
            RequireWorker();
            if (scopes.Count != 0) throw new InvalidOperationException("Cannot snapshot an active account transaction.");
            // Serialization calls this AFTER writing the matching native world.
            // Never execute decay or native callbacks across that commit boundary.
            RequireNeutral();
            if (revision <= ledger.Revision) throw new InvalidOperationException("Account checkpoint revision did not advance.");
            if (PropertyOwnershipReplication.ReconcileNativeGifts(ledger)) generation++;
            var snapshot = Clone(ledger); snapshot.Revision = revision;
            AccountProtocol.Validate(snapshot, snapshot.WorldId, revision);
            capturedRevision = revision; capturedGeneration = generation;
            return snapshot;
        }
        public static void MarkCommitted(long revision)
        {
            RequireWorker();
            if (revision != capturedRevision) throw new InvalidOperationException("Account checkpoint acknowledgement does not match capture.");
            ledger.Revision = revision; committedGeneration = capturedGeneration; capturedRevision = -1;
        }

        public static void ApplyClientAccount(AccountState state)
        {
            if (DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsIsolated || !DedicatedWorldRuntime.IsWorldReady)
                throw new InvalidOperationException("Private account apply requires an isolated client world.");
            if (mainThread == 0) mainThread = Thread.CurrentThread.ManagedThreadId;
            AssertThread(); ValidateAccountItems(state);
            if (!state.Initialized) throw new InvalidDataException("The server account has no starting inventory.");
            try { SharedEconomyReplication.ApplyPersonal(Decode(state), true); }
            catch { Fault("Private account state could not be applied."); throw; }
        }

        public static void RequireBusinessOwner(string accountId, VenueAreaGhost runtime)
        {
            RequireWorker();
            if (runtime?.Venue == null || !accounts.ContainsKey(accountId ?? "") ||
                PropertyOwnershipReplication.OwnerOf(runtime.Venue.Guid) != accountId)
                throw new InvalidOperationException("This business belongs to another account.");
        }

        public static void Fault(string reason)
        {
            FailClosed = true;
            if (FailureReason.Length == 0) FailureReason = string.IsNullOrEmpty(reason) ? "Account authority failed." : reason;
        }
        private static void RequireWorker()
        {
            AssertThread();
            if (!initialized || !DedicatedWorldRuntime.IsDedicatedServer || FailClosed)
                throw new InvalidOperationException(FailClosed ? FailureReason : "Account authority is not initialized.");
        }
        private static void AssertThread()
        {
            if (mainThread == 0 || Thread.CurrentThread.ManagedThreadId != mainThread)
                throw new InvalidOperationException("Native account inventory must stay on the Unity thread.");
        }
        public static void Reset()
        {
            if (scopes.Count != 0) Fault("Account reset interrupted a native scope.");
            hooks?.UnpatchSelf(); hooks = null;
            scopes.Clear(); accounts.Clear(); ledger = null; initialized = false;
            generation = committedGeneration = capturedGeneration = 0; capturedRevision = -1;
            decayQueue.Clear(); decayDay = int.MinValue;
            FailClosed = false; FailureReason = ""; mainThread = 0;
        }

        private static AccountInventoryState Encode(PersonalInventoryState state)
        {
            SharedEconomyReplication.PersonalHash(state);
            var encoded = new AccountInventoryState { Money = state.Money, Inventory = JsonSerializer.SerializeToElement(state.Inventory, DedicatedProtocol.Json) };
            ValidateInventory(encoded); return encoded;
        }
        private static PersonalInventoryState Decode(AccountInventoryState state)
        {
            ValidateInventory(state);
            var decoded = new PersonalInventoryState { Money = state.Money,
                Inventory = state.Inventory.Deserialize<SharedItemStack[]>(DedicatedProtocol.Json) };
            SharedEconomyReplication.PersonalHash(decoded); return decoded;
        }
        private static PersonalInventoryState Decode(AccountState state) => Decode(new AccountInventoryState { Money = state.Money, Inventory = state.Inventory });
        private static void ValidateInventory(AccountInventoryState state)
        {
            if (state == null || state.Inventory.ValueKind != JsonValueKind.Array ||
                Encoding.UTF8.GetByteCount(state.Inventory.GetRawText()) > AccountProtocol.MaxInventoryBytes)
                throw new InvalidDataException("Invalid private inventory encoding.");
        }
        private static void ValidateAccountItems(AccountState state) { AccountProtocol.Validate(state); Decode(state); }
        private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, DedicatedProtocol.Json), DedicatedProtocol.Json);

        private static void InstallBackgroundHooks()
        {
            if (hooks != null) return;
            hooks = new Harmony("NivalisTogether.AccountBackground");
            Patch(typeof(VenueAreaGhost), nameof(VenueAreaGhost.PayStaff), new[] { typeof(Person) }, nameof(VenuePrefix));
            Patch(typeof(RentManager), "CollectReceipt", new[] { typeof(RentManager.Rent) }, nameof(RentPrefix));
            Patch(typeof(VisitVenueAgentActionType.LeaveVenueSubAction), "LeaveReview",
                new[] { typeof(AgentGhost), typeof(VisitVenueAgentActionType.State), typeof(Order) }, nameof(RevenuePrefix));
            Patch(typeof(EndOfDayDebtSection), "Show", new[] { typeof(int) }, nameof(DebtShowPrefix), false);
            Patch(typeof(EndOfDayDebtSection), "ChoicePicked", new[] { typeof(float) }, nameof(DebtChoicePrefix));
        }
        private static void Patch(Type type, string method, Type[] arguments, string prefix, bool finalizer = true)
        {
            var target = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, arguments, null);
            if (target == null || target.ReturnType != typeof(void)) throw new MissingMethodException(type.FullName, method);
            hooks.Patch(target, prefix: new HarmonyMethod(typeof(PlayerAccountRuntime), prefix),
                finalizer: finalizer ? new HarmonyMethod(typeof(PlayerAccountRuntime), nameof(BackgroundFinalizer)) : null);
        }
        private static bool VenuePrefix(VenueAreaGhost __instance, out IDisposable __state)
        {
            __state = null;
            if (!initialized) return true;
            try
            {
                if (!__instance.Venue.PlayerOwned) return true;
                __state = WithProperty(__instance.Venue.Guid); return true;
            }
            catch { Fault("Native staff payment has no valid account context."); return false; }
        }
        private static bool RentPrefix(RentManager.Rent __0, out IDisposable __state)
        {
            __state = null;
            if (!initialized || !DedicatedWorldRuntime.IsDedicatedServer || __0 == null) return true;
            try
            {
                var player = PlayerManager._instance?.LocalPlayer;
                if (player == null || __0.rentier == null || __0.rentier.Pointer != player.Pointer) return true;
                // The starter apartment is shared multiplayer infrastructure,
                // not a personal lease. Its native rent must not drain the
                // separately preserved legacy wallet when the day changes.
                string starter = PropertyOwnershipReplication.StarterPropertyId;
                if (!string.IsNullOrEmpty(starter) && __0.rentableGuid == starter) return false;
                __state = WithProperty(__0.rentableGuid); return true;
            }
            catch { Fault("Native rent payment has no valid account context."); return false; }
        }
        private static bool RevenuePrefix(Order __2, out IDisposable __state)
        {
            __state = null;
            if (!initialized) return true;
            try
            {
                if (__2?.Venue == null || !__2.Venue.PlayerOwned) return true;
                __state = WithProperty(__2.Venue.Guid); return true;
            }
            catch { Fault("Native customer revenue has no valid account context."); return false; }
        }
        private static void DebtShowPrefix(ref int __0)
        {
            if (initialized) __0 = LegacyEscrowMoney;
        }
        private static bool DebtChoicePrefix(out IDisposable __state)
        {
            __state = null;
            if (!initialized) return true;
            try { __state = WithAccount(AccountProtocol.LegacyOwnerId); return true; }
            catch { Fault("Legacy debt payment has no valid escrow context."); return false; }
        }
        private static IDisposable WithProperty(string property)
        {
            var owner = property == PropertyOwnershipReplication.StarterPropertyId ? AccountProtocol.LegacyOwnerId :
                PropertyOwnershipReplication.NativeOwnerOf(property);
            if (string.IsNullOrEmpty(owner)) { Fault("A native property charge has no account owner."); throw new InvalidOperationException(FailureReason); }
            return WithAccount(owner);
        }
        private static Exception BackgroundFinalizer(Exception __exception, IDisposable __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) Fault("A native account background operation failed.");
            try { __state.Dispose(); }
            catch (Exception ex) { Fault("A native account background scope failed."); return __exception ?? ex; }
            return __exception;
        }
    }
}
