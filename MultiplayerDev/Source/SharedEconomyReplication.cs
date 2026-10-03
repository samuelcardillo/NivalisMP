using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.Economy;
using Nivalis.InventorySystem;
using Nivalis.Locale.UI;
using Nivalis.Player;
using Nivalis.SkillSystem;
using UnityEngine;
using NativeItems = Il2CppSystem.Collections.Generic.List<Nivalis.InventorySystem.ItemInstanceData>;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace NivalisTogether.Dedicated
{
    public sealed class SharedItemStack
    {
        public string Type { get; set; } = "";
        public int LastUpdate { get; set; }
        // Each row is value, creationDay, remainingDecayTime, lastUpdateDay.
        public int[][] Items { get; set; } = Array.Empty<int[]>();
    }
    public sealed class SharedVendorStock
    {
        public string VendorId { get; set; } = "";
        public float Relationship { get; set; }
        public int LastRelationshipDay { get; set; }
        public SharedItemStack[] Stacks { get; set; } = Array.Empty<SharedItemStack>();
    }
    public sealed class SharedEconomyState
    {
        public long Sequence { get; set; }
        public int Money { get; set; }
        public SharedItemStack[] Inventory { get; set; } = Array.Empty<SharedItemStack>();
        public SharedVendorStock[] Vendors { get; set; } = Array.Empty<SharedVendorStock>();
    }
    public sealed class PersonalInventoryState
    {
        public int Money { get; set; }
        public SharedItemStack[] Inventory { get; set; } = Array.Empty<SharedItemStack>();
    }
    public sealed class SharedTradeIntent
    {
        public string ItemId { get; set; } = "";
        public int Freshness { get; set; }
        public int Amount { get; set; }
    }
    public sealed class SharedEconomyChunk
    {
        public string Encoding { get; set; } = BoundedSnapshotCodec.JsonEncoding;
        public int RawLength { get; set; }
        public string Hash { get; set; } = "";
        public long Sequence { get; set; }
        public int Length { get; set; }
        public int Offset { get; set; }
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Main-thread native vendor transactions. Personal balances travel privately.
    /// Caller authenticates/deduplicates requests and broadcasts every CaptureEvents result.
    /// Initial native save must be loaded before receiving live state.
    /// </summary>
    public static class SharedEconomyReplication
    {
        private const int MaxStacks = 16384, MaxInstances = 200000, MaxStateBytes = 16 * 1024 * 1024;
        private const int ChunkBytes = 24 * 1024;
        private static long sequence, lastApplied;
        private static byte[] incoming;
        private static int incomingOffset;
        private static long incomingSequence;
        private static string incomingHash;
        private static string incomingEncoding;
        private static int incomingRawLength;
        private static float incomingStarted;
        private static long incomingChunkJsonTicks, nextPerformanceReport;
        private static EconomyProfile activeProfile;
        private static int preparationGeneration;
        // Managed snapshots only: never retain native wrappers or dereference an
        // old pointer. Every reuse compares all current native contents first.
        private static Dictionary<IntPtr, SharedItemStack[]> capturedVendorStocks = new Dictionary<IntPtr, SharedItemStack[]>();
        private const int MaxCachedVendorContainers = 4096, MaxCachedVendorInstances = 1000000;
        private const long MaxCachedVendorBytes = 64L * 1024 * 1024;
        internal sealed class EconomyProfile
        {
            internal long Started, Sequence, ChunkJson, Decode, StateJson, Validation, Resolve, DesiredHashes,
                Capture, StackWalk, ItemReads, Compare, Prepare, Mutate, Verify, Rollback, Ui;
            internal int Vendors, Containers, Replacements, Captures, Stacks, Items, FastItems, FallbackItems,
                FastMatchedContainers, FastMatchedInstances;
            internal long PreparationWall, PreparationTicks, MainThreadTicks;
            internal bool NoOp, Failed, BackgroundPrepared, Incremental;
        }
        public static bool IsApplying { get; private set; }
        public static bool Faulted { get; private set; }
        public static string LastCaptureHash { get; private set; } = "";
        public static string LastRefreshError { get; private set; } = "";
        public static int LastComparedContainers { get; private set; }
        public static int LastReplacedContainers { get; private set; }
        public static bool LastApplyWasNoOp { get; private set; }
        public static Action<string> Diagnostic { get; set; }
        // Mandatory. Caller must use authenticated, fresh actor pose and compare
        // district and distance to the worker-resolved native vendor location.
        public static Action<InteractionRequest, int, Vector3> RequireNearby { get; set; }

        public static void Reset()
        {
            sequence = lastApplied = 0; incoming = null; incomingOffset = 0;
            incomingHash = incomingEncoding = null; incomingRawLength = 0;
            Faulted = false; LastCaptureHash = ""; LastRefreshError = ""; IsApplying = false;
            LastComparedContainers = LastReplacedContainers = 0; LastApplyWasNoOp = false;
            incomingChunkJsonTicks = nextPerformanceReport = 0; activeProfile = null;
            preparationGeneration++;
            capturedVendorStocks.Clear();
        }

        public static InteractionRequest BuildRequest(ShopItemDetailsDisplay ui, int amount, bool selling)
        {
            if (ui == null || ui.Vendor == null || ui._stack == null || ui._stack.Type == null)
                throw new InvalidOperationException("Select an item and vendor first.");
            var type = ui._stack.Type;
            var inventory = PlayerInventory();
            // A personal destination is the first supported route. The shared
            // business containers require separate permissions/capacity rules.
            if (type.IsFurniture || !type.IsPlayerStorable)
                throw new InvalidOperationException("Only items in your personal inventory can be traded.");
            if (ui._inventoryData != null)
            {
                var destination = ui._inventoryData.GetDestinationContainerForItemType(type);
                if (destination != null && destination.Pointer != inventory.Items.Pointer)
                    throw new InvalidOperationException("Select your personal inventory as the destination.");
            }
            ValidateAmount(amount);
            return new InteractionRequest {
                RequestId = Guid.NewGuid().ToString("N"),
                Kind = selling ? "shop-sell" : "shop-buy", TargetId = VendorKey(ui.Vendor),
                Data = JsonSerializer.SerializeToElement(new SharedTradeIntent {
                    ItemId = type.Guid, Amount = amount, Freshness = (int)ui._freshness
                }, DedicatedProtocol.Json)
            };
        }

        public static bool TryResolveVendorPosition(string vendorId, out int sceneIndex, out Vector3 position)
        {
            sceneIndex = -1; position = default;
            var vendor = ResolveVendor(vendorId);
            foreach (var view in Object.FindObjectsOfType<VendorInteraction>(true))
            {
                if (view == null || !view.IsActive || !view.gameObject.activeInHierarchy ||
                    !view.gameObject.scene.IsValid() || !view.gameObject.scene.isLoaded ||
                    view.Definition == null || view.Definition.Pointer != vendor.Pointer) continue;
                sceneIndex = view.gameObject.scene.buildIndex; position = view.transform.position; return true;
            }
            return false;
        }

        public static SharedEconomyState Handle(InteractionRequest request)
        {
            if (IsApplying || Faulted) throw new InvalidOperationException("Trading requires a fresh authoritative world load.");
            if (request == null || (request.Kind != "shop-buy" && request.Kind != "shop-sell") ||
                !DedicatedProtocol.Text(request.PlayerId, 128) || !DedicatedProtocol.Text(request.RequestId, 64) ||
                !DedicatedProtocol.Text(request.TargetId, 128))
                throw new ArgumentException("Invalid shop request.");
            if (RequireNearby == null) throw new InvalidOperationException("Shop actor validation is unavailable.");
            var intent = request.Data.Deserialize<SharedTradeIntent>(DedicatedProtocol.Json);
            if (intent == null || !DedicatedProtocol.Text(intent.ItemId, 128) || (intent.Freshness != (int)FoodFreshness.Fresh && intent.Freshness != (int)FoodFreshness.Aging && intent.Freshness != (int)FoodFreshness.Spoiled))
                throw new ArgumentException("Invalid item request.");
            ValidateAmount(intent.Amount);
            var inventory = PlayerInventory();
            var player = PlayerManager.Instance.LocalPlayer;
            var vendor = ResolveVendor(request.TargetId);
            if (!TryResolveVendorPosition(request.TargetId, out int scene, out var position))
                throw new InvalidOperationException("This vendor's district is not loaded on the worker.");
            RequireNearby(request, scene, position);
            if (!EconomyManager.Instance.IsVendorTierUnlocked(vendor.Tier))
                throw new InvalidOperationException("This vendor is not unlocked in the shared world.");
            var type = ResolveItem(intent.ItemId);
            bool selling = request.Kind == "shop-sell";
            if (type.IsFurniture || !type.IsPlayerStorable || (selling ? !type.AllowSelling : !type.AllowBuying))
                throw new InvalidOperationException("This item cannot be traded through your personal inventory.");
            if (!selling && !vendor.IsOffered(type))
                throw new InvalidOperationException("This vendor does not offer the requested item.");
            var freshness = (FoodFreshness)intent.Freshness;
            var source = selling ? inventory.Items : vendor.Container;
            var destination = selling ? vendor.Container : inventory.Items;
            source.Refresh(); destination.Refresh();
            var selected = SelectInstances(source, type, freshness, intent.Amount);
            if (selected.Count != intent.Amount) throw new InvalidOperationException("The requested quantity or freshness is no longer available.");
            int price = selling ? vendor.GetItemSellCost(type, freshness) : vendor.GetItemBuyCost(type, freshness);
            long totalLong = checked((long)price * intent.Amount);
            if (price < 0 || totalLong < 0 || totalLong > int.MaxValue)
                throw new InvalidOperationException("The native trade price is out of range.");
            int total = (int)totalLong;
            long expectedMoney = (long)inventory.Money + (selling ? total : -total);
            if (expectedMoney < 0 || expectedMoney > int.MaxValue)
                throw new InvalidOperationException(selling ? "Your wallet cannot hold this amount." : "Your wallet has insufficient funds.");
            // Vendors discard sold items they do not offer; this is native behavior.
            if ((!selling || vendor.IsOffered(type)) && !destination.Restriction.CanAddItem(destination, type, intent.Amount))
                throw new InvalidOperationException("The destination does not have enough inventory capacity.");

            var backup = CreateRollback(inventory, vendor);
            int beforeCount = inventory.Items.GetItemCount(type);
            bool enteredNative = false;
            try
            {
                // Native UI removes its selected source items before calling
                // Vendor.BuyItem/SellItem. The list overload is NOT an out parameter.
                var items = new NativeItems();
                foreach (var selectedItem in selected)
                {
                    if (!source.TryTake(selectedItem.Stack, selectedItem.Item))
                        throw new InvalidOperationException("The item stock changed before the transaction.");
                    items.Add(selectedItem.Item);
                }
                var nativeRequest = new ShopTradeRequest(player.Cast<IShopUser>(),
                    inventory.Items.Cast<IItemContainer>(), vendor.Cast<IItemShop>(), type, freshness, intent.Amount);
                enteredNative = true;
                if (selling) vendor.SellItem(ref nativeRequest, items);
                else vendor.BuyItem(ref nativeRequest, items);
                inventory.Items.Refresh(); vendor.Container.Refresh();
                int expectedCount = beforeCount + (selling ? -intent.Amount : intent.Amount);
                if (inventory.Money != expectedMoney || inventory.Items.GetItemCount(type) != expectedCount)
                    throw new InvalidOperationException("The native trade was rejected or did not complete in full.");
                var result = CaptureVendor(vendor);
                // Serialize before committing so oversize/capture failures use
                // the same rollback path as native failures.
                CheckCaptureSize(result);
                backup.Commit();
                return result;
            }
            catch
            {
                try { backup.Restore(); }
                finally
                {
                    // Native notifications, achievements, and arbitrary event
                    // subscribers cannot be generically undone. Stop subsequent
                    // trades after entering native code until the world reloads.
                    if (enteredNative) Faulted = true;
                }
                throw;
            }
        }

        public static SharedEconomyState Capture(bool allVendors = true)
        {
            var inventory = PlayerInventory();
            var vendors = new List<SharedVendorStock>();
            bool cacheWorkerStocks = allVendors && DedicatedWorldRuntime.IsDedicatedServer;
            Dictionary<IntPtr, SharedItemStack[]> stockByContainer = null;
            if (allVendors)
            {
                stockByContainer = new Dictionary<IntPtr, SharedItemStack[]>();
                var roster = EconomyManager.Instance.Vendors;
                if (roster == null || roster.Length > 4096) throw new InvalidOperationException("Invalid native vendor roster.");
                for (int nativeIndex = 0; nativeIndex < roster.Length; nativeIndex++)
                {
                    var vendor = roster[nativeIndex];
                    if (vendor == null || vendor.Container == null) continue;
                    var stock = CaptureStock(vendor, nativeIndex, stockByContainer, cacheWorkerStocks ? capturedVendorStocks : null);
                    ValidateVendor(stock, nativeIndex, null);
                    vendors.Add(stock);
                }
            }
            vendors.Sort((a, b) => string.CompareOrdinal(a.VendorId, b.VendorId));
            var result = new SharedEconomyState { Sequence = ++sequence, Money = 0,
                Inventory = Array.Empty<SharedItemStack>(), Vendors = vendors.ToArray() };
            // Publish only a complete successful roster. Replacing this map also
            // evicts vanished containers; partial/transaction captures leave it alone.
            if (cacheWorkerStocks) capturedVendorStocks = BoundCapturedVendorStocks(stockByContainer);
            return result;
        }
        private static SharedEconomyState CaptureVendor(Vendor vendor)
        {
            var result = Capture(false); result.Vendors = new[] { CaptureStock(vendor) }; return result;
        }
        private static SharedVendorStock CaptureStock(Vendor vendor, int index = -1, Dictionary<IntPtr, SharedItemStack[]> cache = null,
            Dictionary<IntPtr, SharedItemStack[]> previous = null)
        {
            var container = vendor.Container;
            if (container.Pointer == PlayerInventory().Items.Pointer)
                throw new InvalidOperationException("A shared vendor cannot expose personal inventory.");
            if (cache == null || !cache.TryGetValue(container.Pointer, out var stacks))
            {
                if (previous == null || !previous.TryGetValue(container.Pointer, out var candidate) ||
                    !TryMatchNativeItems(container, candidate, out stacks))
                    stacks = CaptureItems(container);
                if (cache != null) cache.Add(container.Pointer, stacks);
            }
            return new SharedVendorStock {
                VendorId = index < 0 ? VendorKey(vendor) : VendorKey(index, vendor), Relationship = vendor.relationshipLevelProgress,
                LastRelationshipDay = vendor.lastRelationshipLevelUpDay, Stacks = stacks
            };
        }

        private static Dictionary<IntPtr, SharedItemStack[]> BoundCapturedVendorStocks(Dictionary<IntPtr, SharedItemStack[]> current)
        {
            var retained = new Dictionary<IntPtr, SharedItemStack[]>();
            long bytes = 0; int instances = 0;
            foreach (var pair in current)
            {
                if (retained.Count >= MaxCachedVendorContainers) break;
                long entryBytes = 64 + 24 + (long)pair.Value.Length * 8;
                long entryInstances = 0;
                foreach (var stack in pair.Value)
                {
                    // Conservative x64 estimate: stack, UTF-16 type string,
                    // jagged reference array, and a 40-byte int[4] per row.
                    entryInstances += stack.Items.Length;
                    long typeBytes = (24 + 2L * stack.Type.Length + 7) & ~7L;
                    entryBytes += 40 + typeBytes + 24 + 48L * stack.Items.Length;
                }
                // Caching is optional. Oversized valid worlds still capture and
                // publish normally; uncached containers use fresh capture next time.
                if (entryInstances > MaxCachedVendorInstances - instances || entryBytes > MaxCachedVendorBytes - bytes) continue;
                retained.Add(pair.Key, pair.Value); instances += (int)entryInstances; bytes += entryBytes;
            }
            return retained;
        }

        // Main-thread only. Other inventory replication modules can reuse the
        // same validated native reader without duplicating its layout contract.
        internal static SharedItemStack[] CaptureNativeItems(ItemContainer container) => CaptureItems(container);

        // A sufficient equality proof, not a replacement for canonical equality:
        // different ordering/grouping still falls back to capture + content hash.
        // Observe every native item on every call; no last-received hash cache.
        internal static bool TryMatchNativeItems(ItemContainer container, SharedItemStack[] desired, out SharedItemStack[] observed)
        {
            observed = null;
            if (desired == null || desired.Length > MaxStacks || !NativeItemReader.CanMatch) return false;
            var stacks = SnapshotStacks(container);
            int rowIndex = 0, total = 0;
            SharedItemStack[] baseline = null;
            foreach (var stack in stacks)
            {
                var type = stack?._type;
                if (stack == null || type == null) return false;
                var instances = stack._instanceData;
                if (instances == null) return false;
                bool scoped = NativeItemReader.TryObserveList(stack, instances, out var native);
                int version = scoped ? native.Version : instances._version, count = scoped ? native.Count : instances._size;
                if (count < 0 || count > MaxInstances - total) return false;
                if (count == 0) continue; // CaptureItems also omits empty native stacks.
                if (rowIndex >= desired.Length) return false;
                var expected = desired[rowIndex];
                if (expected == null || expected.Items == null || expected.Items.Length != count) return false;
                var array = scoped ? NativeItemReader.RootArray(native.Array) : instances._items;
                try
                {
                    if (array == null || type.Guid != expected.Type || !NativeItemReader.TryMatches(array, count, type.Pointer, expected.Items, scoped ? native.Array : IntPtr.Zero)) return false;
                    if (scoped ? !native.IsCurrent() : instances._version != version || instances._size != count ||
                        instances._items?.Pointer != array.Pointer || stack._instanceData?.Pointer != instances.Pointer) return false;
                    int lastUpdate = scoped ? native.LastUpdate : stack._lastUpdateTime;
                    if (lastUpdate != expected.LastUpdate)
                    {
                        // Stack cache time is deliberately absent from canonical
                        // equality, but preserve the native baseline for rollback.
                        baseline ??= (SharedItemStack[])desired.Clone();
                        baseline[rowIndex] = new SharedItemStack { Type = expected.Type, LastUpdate = lastUpdate, Items = expected.Items };
                    }
                    total += count; rowIndex++;
                }
                finally { GC.KeepAlive(array); GC.KeepAlive(instances); GC.KeepAlive(type); GC.KeepAlive(stack); }
            }
            GC.KeepAlive(container);
            if (rowIndex != desired.Length) return false;
            observed = baseline ?? desired;
            if (activeProfile != null)
            {
                activeProfile.FastMatchedContainers++; activeProfile.FastMatchedInstances += total;
            }
            return true;
        }

        private static SharedItemStack[] CaptureItems(ItemContainer container)
        {
            var result = new List<SharedItemStack>(); int total = 0;
            long profileStarted = Stopwatch.GetTimestamp();
            var stackSnapshot = SnapshotStacks(container);
            if (activeProfile != null)
            {
                activeProfile.StackWalk += Stopwatch.GetTimestamp() - profileStarted;
                activeProfile.Captures++; activeProfile.Stacks += stackSnapshot.Length;
            }
            long itemStarted = Stopwatch.GetTimestamp();
            foreach (var stack in stackSnapshot)
            {
                var type = stack?._type;
                if (stack == null || type == null) throw new InvalidOperationException("Native inventory contains an invalid stack.");
                if (total > MaxInstances || result.Count >= MaxStacks) throw new InvalidOperationException("Shared inventory exceeds supported limits.");
                var instances = stack._instanceData;
                if (instances == null) throw new InvalidOperationException("Native inventory contains an invalid item list.");
                // ItemInstanceData is a reference class. The generated backing-array
                // wrapper reads object references directly; List<T>[i] invokes the
                // native method and reconstructs its wrapper on every iteration.
                bool scoped = NativeItemReader.TryObserveList(stack, instances, out var native);
                int version = scoped ? native.Version : instances._version, count = scoped ? native.Count : instances._size;
                if (count < 0 || count > MaxInstances - total)
                    throw new InvalidOperationException("Native inventory contains an invalid item list size.");
                if (count == 0) continue;
                var array = scoped ? NativeItemReader.RootArray(native.Array) : instances._items;
                try
                {
                    if (array == null || (!scoped && count > array.Length))
                        throw new InvalidOperationException("Native inventory contains an invalid item list size.");
                    string typeId = type.Guid; IntPtr typePointer = type.Pointer;
                    var rows = new int[count][];
                    // The rooted reference array owns every item during this
                    // main-thread read. Copy slots once, without creating an
                    // interop wrapper/GC handle for each of hundreds of thousands
                    // of items. Unsupported layouts retain the wrapper path.
                    var pointers = NativeItemReader.CopyPointers(array, count, scoped ? native.Array : IntPtr.Zero);
                    for (int i = 0; i < count; i++)
                    {
                        if (pointers != null && pointers[i] == IntPtr.Zero)
                            throw new InvalidOperationException("Native inventory contains an invalid item.");
                        if (pointers != null && NativeItemReader.TryRead(pointers[i], typePointer, out var row))
                        {
                            if (!NativeItemReader.ParityChecked)
                            {
                                var sample = array[i];
                                NativeItemReader.CheckParity(row, ReadItem(sample, typePointer, typeId));
                                GC.KeepAlive(sample);
                            }
                            if (NativeItemReader.Available)
                            {
                                rows[i] = row;
                                if (activeProfile != null) activeProfile.FastItems++;
                                continue;
                            }
                        }
                        var item = array[i];
                        rows[i] = ReadItem(item, typePointer, typeId);
                        if (activeProfile != null) activeProfile.FallbackItems++;
                        GC.KeepAlive(item);
                    }
                    // Reads are main-thread only and invoke no mutation callbacks.
                    // Still reject a replaced or structurally changed list rather
                    // than accepting a mixture of snapshots.
                    if (scoped ? !native.IsCurrent() : instances._version != version || instances._size != count ||
                        instances._items?.Pointer != array.Pointer || stack._instanceData?.Pointer != instances.Pointer)
                        throw new InvalidOperationException("Native inventory changed during item capture.");
                    total += count;
                    result.Add(new SharedItemStack { Type = typeId, LastUpdate = scoped ? native.LastUpdate : stack._lastUpdateTime, Items = rows });
                }
                finally { GC.KeepAlive(array); GC.KeepAlive(instances); GC.KeepAlive(type); GC.KeepAlive(stack); }
            }
            if (activeProfile != null) { activeProfile.ItemReads += Stopwatch.GetTimestamp() - itemStarted; activeProfile.Items += total; }
            GC.KeepAlive(container);
            return result.ToArray();
        }

        private static int[] ReadItem(ItemInstanceData item, IntPtr typePointer, string typeId)
        {
            if (item == null) throw new InvalidOperationException("Native inventory contains an invalid item.");
            var itemType = item.type;
            // Preserve GUID equivalence for distinct native type objects and
            // Unity's destroyed-object null check on the fallback path.
            if (ReferenceEquals(itemType, null) ||
                (itemType.Pointer != typePointer && (itemType == null || itemType.Guid != typeId)))
                throw new InvalidOperationException("Native inventory contains an invalid item.");
            var row = new[] { item.value, item.creationDay, item.remainingDecayTime, item.lastUpdateDay };
            GC.KeepAlive(itemType); GC.KeepAlive(item);
            return row;
        }

        // Only accepts the inspected, complete reference-class layout. This
        // helper has no native API dependency so bounds/fallback/read parity can
        // also be tested against owned unmanaged buffers outside Unity.
        internal sealed class ItemReadLayout
        {
            private readonly IntPtr itemClass;
            private readonly int typeOffset, valueOffset;
            private ItemReadLayout(IntPtr itemClass, int typeOffset, int valueOffset)
            { this.itemClass = itemClass; this.typeOffset = typeOffset; this.valueOffset = valueOffset; }
            internal static bool TryCreate(IntPtr itemClass, int size, int typeOffset, int valueOffset,
                int creationOffset, int decayOffset, int dayOffset, out ItemReadLayout layout)
            {
                layout = null;
                if (IntPtr.Size != 8 || !BitConverter.IsLittleEndian || itemClass == IntPtr.Zero || size != 40 || typeOffset != 16 ||
                    valueOffset != 24 || creationOffset != 28 || decayOffset != 32 || dayOffset != 36) return false;
                layout = new ItemReadLayout(itemClass, typeOffset, valueOffset); return true;
            }
            internal bool TryRead(IntPtr item, IntPtr expectedType, out int[] row)
            {
                row = null;
                // Slots came from a rooted native reference array. Check exact
                // class before applying offsets; subclasses use native wrappers.
                if (item == IntPtr.Zero || expectedType == IntPtr.Zero || Marshal.ReadIntPtr(item) != itemClass ||
                    Marshal.ReadIntPtr(item, typeOffset) != expectedType) return false;
                row = new int[4];
                Marshal.Copy(IntPtr.Add(item, valueOffset), row, 0, row.Length);
                return true;
            }
            internal bool TryMatches(IntPtr item, IntPtr expectedType, int[] expected)
            {
                if (expected == null || expected.Length != 4 || item == IntPtr.Zero || expectedType == IntPtr.Zero ||
                    Marshal.ReadIntPtr(item) != itemClass || Marshal.ReadIntPtr(item, typeOffset) != expectedType) return false;
                // The validated little-endian layout stores four adjacent Int32
                // fields. Pack unsigned halves so negative low words cannot
                // sign-extend over their neighbor; observe all four native fields.
                long first = unchecked((long)((ulong)(uint)expected[0] | ((ulong)(uint)expected[1] << 32)));
                long second = unchecked((long)((ulong)(uint)expected[2] | ((ulong)(uint)expected[3] << 32)));
                return Marshal.ReadInt64(item, valueOffset) == first && Marshal.ReadInt64(item, valueOffset + 8) == second;
            }
        }

        // Runtime metadata validates these fields before any scoped raw read.
        // A snapshot lives only inside one main-thread read whose caller keeps
        // the stack, list and array wrappers rooted through the final check.
        internal sealed class ItemListReadLayout
        {
            private readonly IntPtr stackClass,listClass,arrayClass;
            private readonly int stackList,stackUpdate,listVersion,listCount,listArray;
            private ItemListReadLayout(IntPtr stackClass,IntPtr listClass,IntPtr arrayClass,int stackList,int stackUpdate,int listVersion,int listCount,int listArray)
            {this.stackClass=stackClass;this.listClass=listClass;this.arrayClass=arrayClass;this.stackList=stackList;this.stackUpdate=stackUpdate;this.listVersion=listVersion;this.listCount=listCount;this.listArray=listArray;}
            internal static bool TryCreate(IntPtr stackClass,int stackSize,IntPtr listClass,int listSize,IntPtr arrayClass,
                int stackList,int stackUpdate,int listVersion,int listCount,int listArray,out ItemListReadLayout layout)
            {
                layout=null;
                bool Field(int offset,int bytes,int size)=>offset>=2*IntPtr.Size&&offset<=size-bytes&&offset%bytes==0;
                bool Separate(int a,int sizeA,int b,int sizeB)=>a+sizeA<=b||b+sizeB<=a;
                if(IntPtr.Size!=8||stackClass==IntPtr.Zero||listClass==IntPtr.Zero||arrayClass==IntPtr.Zero||
                    !Field(stackList,IntPtr.Size,stackSize)||!Field(stackUpdate,4,stackSize)||!Separate(stackList,IntPtr.Size,stackUpdate,4)||
                    !Field(listVersion,4,listSize)||!Field(listCount,4,listSize)||!Field(listArray,IntPtr.Size,listSize)||
                    !Separate(listVersion,4,listCount,4)||!Separate(listVersion,4,listArray,IntPtr.Size)||!Separate(listCount,4,listArray,IntPtr.Size))return false;
                layout=new ItemListReadLayout(stackClass,listClass,arrayClass,stackList,stackUpdate,listVersion,listCount,listArray);return true;
            }
            internal bool TryObserve(IntPtr stack,IntPtr list,out ItemListSnapshot snapshot)
            {
                snapshot=default;
                if(stack==IntPtr.Zero||list==IntPtr.Zero||Marshal.ReadIntPtr(stack)!=stackClass||Marshal.ReadIntPtr(list)!=listClass||
                    Marshal.ReadIntPtr(stack,stackList)!=list)return false;
                var array=Marshal.ReadIntPtr(list,listArray);
                if(array!=IntPtr.Zero&&Marshal.ReadIntPtr(array)!=arrayClass)return false;
                snapshot=new ItemListSnapshot(this,stack,list,array,Marshal.ReadInt32(list,listVersion),Marshal.ReadInt32(list,listCount));return true;
            }
            private bool IsCurrent(ItemListSnapshot value)=>Marshal.ReadIntPtr(value.Stack)==stackClass&&Marshal.ReadIntPtr(value.List)==listClass&&
                Marshal.ReadIntPtr(value.Stack,stackList)==value.List&&Marshal.ReadIntPtr(value.List,listArray)==value.Array&&
                Marshal.ReadInt32(value.List,listVersion)==value.Version&&Marshal.ReadInt32(value.List,listCount)==value.Count;
            internal readonly struct ItemListSnapshot
            {
                private readonly ItemListReadLayout layout;
                internal readonly IntPtr Stack,List,Array;
                internal readonly int Version,Count;
                internal ItemListSnapshot(ItemListReadLayout layout,IntPtr stack,IntPtr list,IntPtr array,int version,int count)
                {this.layout=layout;Stack=stack;List=list;Array=array;Version=version;Count=count;}
                internal bool IsCurrent()=>layout!=null&&layout.IsCurrent(this);
                internal int LastUpdate=>Marshal.ReadInt32(Stack,layout.stackUpdate);
            }
        }

        private static class NativeItemReader
        {
            private static bool initialized, reported;
            private static ItemReadLayout layout;
            private static ItemListReadLayout listLayout;
            private static Func<Il2CppArrayBase, IntPtr> arrayStart;
            private static int arrayStartOffset;
            private static bool arrayStartParity;
            internal static bool ParityChecked { get; private set; }
            internal static bool Available => layout != null;
            internal static bool CanMatch { get { Initialize(); return Available && ParityChecked; } }
            internal static bool TryObserveList(ItemStack stack,NativeItems instances,out ItemListReadLayout.ItemListSnapshot snapshot)
            {
                Initialize();snapshot=default;
                if(!Available||listLayout==null||stack==null||instances==null)return false;
                // Resolve each held wrapper once for this scoped observation.
                // Raw final checks do not manufacture replacement wrappers.
                try{return listLayout.TryObserve(stack.Pointer,instances.Pointer,out snapshot);}
                finally{GC.KeepAlive(instances);GC.KeepAlive(stack);}
            }
            internal static Il2CppReferenceArray<ItemInstanceData> RootArray(IntPtr pointer)=>pointer==IntPtr.Zero?null:
                Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<Il2CppReferenceArray<ItemInstanceData>>(pointer);
            private static IntPtr ArrayStart(Il2CppArrayBase array,IntPtr pointer)
            {
                if(pointer==IntPtr.Zero||arrayStartOffset<0)return arrayStart(array);
                var direct=IntPtr.Add(pointer,arrayStartOffset);
                if(!arrayStartParity)
                {
                    // Confirm the installed interop accessor on a real rooted
                    // array before reusing its runtime-derived header size.
                    var actual=arrayStart(array);
                    if(actual!=direct){arrayStartOffset=-1;return actual;}
                    arrayStartParity=true;
                }
                return direct;
            }
            private static long ArrayLength(Il2CppArrayBase array,IntPtr pointer)=>pointer==IntPtr.Zero?array.Length:IL2CPP.il2cpp_array_length(pointer);
            internal static bool TryMatches(Il2CppArrayBase array, int count, IntPtr type, int[][] expected,IntPtr pointer=default)
            {
                if (!CanMatch || array == null || expected == null || count < 0 || count > MaxInstances ||
                    count > ArrayLength(array,pointer) || count != expected.Length) return false;
                try
                {
                    var start = ArrayStart(array,pointer);
                    if (start == IntPtr.Zero && count != 0) return false;
                    for (int i = 0; i < count; i++)
                        if (!layout.TryMatches(Marshal.ReadIntPtr(start, checked(i * IntPtr.Size)), type, expected[i])) return false;
                    return true;
                }
                finally { GC.KeepAlive(array); }
            }
            internal static IntPtr[] CopyPointers(Il2CppArrayBase array, int count,IntPtr pointer=default)
            {
                Initialize();
                if (!Available) return null;
                // ArrayStartPointer is the installed interop runtime's accessor;
                // no duplicate assumptions about the native array header.
                if (count < 0 || count > MaxInstances || count > ArrayLength(array,pointer))
                    throw new InvalidOperationException("Native inventory contains an invalid item list size.");
                var pointers = new IntPtr[count];
                try { Marshal.Copy(ArrayStart(array,pointer), pointers, 0, count); }
                finally { GC.KeepAlive(array); }
                return pointers;
            }
            internal static bool TryRead(IntPtr item, IntPtr type, out int[] row)
            {
                row = null;
                return layout != null && layout.TryRead(item, type, out row);
            }
            internal static void CheckParity(int[] direct, int[] wrapped)
            {
                for (int i = 0; i < 4; i++) if (direct[i] != wrapped[i])
                { layout = null; Report("disabled reason=wrapper-parity"); return; }
                ParityChecked = true;
                Report("enabled objectBytes=40 typeOffset=16 valueOffset=24 fields=4 wrapperParity=True scopedList="+(listLayout!=null));
            }
            private static void Initialize()
            {
                if (initialized) return;
                initialized = true;
                try
                {
                    IntPtr klass = Il2CppClassPointerStore<ItemInstanceData>.NativeClassPtr;
                    IntPtr typeClass = Il2CppClassPointerStore<ItemType>.NativeClassPtr;
                    if (klass == IntPtr.Zero || typeClass == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(klass))
                        throw new InvalidOperationException("class-kind");
                    int Offset(string name, int code, IntPtr fieldClass)
                    {
                        IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
                        if (field == IntPtr.Zero || IL2CPP.il2cpp_field_get_parent(field) != klass ||
                            (IL2CPP.il2cpp_field_get_flags(field) & 0x50) != 0)
                            throw new InvalidOperationException("field-owner");
                        IntPtr type = IL2CPP.il2cpp_field_get_type(field);
                        if (type == IntPtr.Zero || IL2CPP.il2cpp_type_get_type(type) != code ||
                            (fieldClass != IntPtr.Zero && IL2CPP.il2cpp_class_from_type(type) != fieldClass))
                            throw new InvalidOperationException("field-type");
                        return checked((int)IL2CPP.il2cpp_field_get_offset(field));
                    }
                    if (!ItemReadLayout.TryCreate(klass, IL2CPP.il2cpp_class_instance_size(klass),
                        Offset("type", 0x12, typeClass), Offset("value", 0x08, IntPtr.Zero),
                        Offset("creationDay", 0x08, IntPtr.Zero), Offset("remainingDecayTime", 0x08, IntPtr.Zero),
                        Offset("lastUpdateDay", 0x08, IntPtr.Zero), out var candidate))
                        throw new InvalidOperationException("field-layout");
                    var getter = typeof(Il2CppArrayBase).GetProperty("ArrayStartPointer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod;
                    if (getter == null || getter.ReturnType != typeof(IntPtr)) throw new InvalidOperationException("array-accessor");
                    arrayStart = (Func<Il2CppArrayBase, IntPtr>)getter.CreateDelegate(typeof(Func<Il2CppArrayBase, IntPtr>));
                    try{
                        var objectHeader=typeof(Il2CppArrayBase).Assembly.GetType("Il2CppInterop.Runtime.Runtime.Il2CppObject",true);
                        arrayStartOffset=checked(Marshal.SizeOf(objectHeader)+2*IntPtr.Size);
                    }catch{arrayStartOffset=-1;}
                    layout = candidate;
                    InitializeListLayout();
                }
                catch (Exception ex)
                {
                    layout = null;
                    // Restrict diagnostics to fixed validation labels/type names.
                    string reason = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
                    Report("disabled reason=" + reason);
                }
            }
            private static void InitializeListLayout()
            {
                // Optional optimization. Unsupported stack/list layouts retain
                // the normal generated-property capture and validation path.
                try
                {
                    IntPtr stackClass=Il2CppClassPointerStore<ItemStack>.NativeClassPtr,listClass=Il2CppClassPointerStore<NativeItems>.NativeClassPtr;
                    if(stackClass==IntPtr.Zero||listClass==IntPtr.Zero||IL2CPP.il2cpp_class_is_valuetype(stackClass)||IL2CPP.il2cpp_class_is_valuetype(listClass))return;
                    int Field(IntPtr owner,string name,int code,IntPtr fieldClass,out IntPtr actualClass)
                    {
                        IntPtr field=IL2CPP.il2cpp_class_get_field_from_name(owner,name);actualClass=IntPtr.Zero;
                        if(field==IntPtr.Zero||IL2CPP.il2cpp_field_get_parent(field)!=owner||(IL2CPP.il2cpp_field_get_flags(field)&0x50)!=0)
                            throw new InvalidOperationException("list-field-owner");
                        IntPtr type=IL2CPP.il2cpp_field_get_type(field);
                        if(type==IntPtr.Zero||IL2CPP.il2cpp_type_get_type(type)!=code)throw new InvalidOperationException("list-field-type");
                        actualClass=IL2CPP.il2cpp_class_from_type(type);
                        if(fieldClass!=IntPtr.Zero&&actualClass!=fieldClass)throw new InvalidOperationException("list-field-class");
                        return checked((int)IL2CPP.il2cpp_field_get_offset(field));
                    }
                    int stackList=Field(stackClass,"_instanceData",0x15,listClass,out _);
                    int stackUpdate=Field(stackClass,"_lastUpdateTime",0x08,IntPtr.Zero,out _);
                    int version=Field(listClass,"_version",0x08,IntPtr.Zero,out _),count=Field(listClass,"_size",0x08,IntPtr.Zero,out _);
                    int array=Field(listClass,"_items",0x1d,IntPtr.Zero,out var arrayClass);
                    if(arrayClass==IntPtr.Zero||IL2CPP.il2cpp_class_get_element_class(arrayClass)!=Il2CppClassPointerStore<ItemInstanceData>.NativeClassPtr)return;
                    ItemListReadLayout.TryCreate(stackClass,IL2CPP.il2cpp_class_instance_size(stackClass),listClass,IL2CPP.il2cpp_class_instance_size(listClass),
                        arrayClass,stackList,stackUpdate,version,count,array,out listLayout);
                }
                catch{listLayout=null;}
            }
            private static void Report(string detail)
            {
                if (reported) return;
                reported = true;
                try { string text = "ECONOMY_NATIVE_READ " + detail; if (Diagnostic != null) Diagnostic(text); else Debug.Log(text); } catch { }
            }
        }
        /// <summary>Main-thread snapshot without the native boxed LinkedList enumerator.</summary>
        public static ItemStack[] SnapshotStacks(ItemContainer container)
        {
            var list = container?._items ?? throw new InvalidOperationException("Native inventory is unavailable.");
            var result = new List<ItemStack>();
            var seen = new HashSet<IntPtr>();
            // Copy node references before reading any stack data. GetEnumerator's
            // versioned boxed interface failed at runtime on vendor containers.
            // LinkedList accessors and raw fields do not advance item decay or
            // invoke inventory callbacks; no gameplay mutation occurs here.
            for (var node = list.First; node != null; node = node.Next)
            {
                if (result.Count >= MaxStacks || !seen.Add(node.Pointer)) throw new InvalidOperationException("Invalid native inventory node chain.");
                result.Add(node.Value);
            }
            if (result.Count != list.Count) throw new InvalidOperationException("Native inventory changed while copying its nodes.");
            return result.ToArray();
        }

        public static WorldEvent[] CaptureEvents(SharedEconomyState state)
        {
            ValidateSharedOnly(state);
            string contentHash = ContentHash(state);
            var encoded = BoundedSnapshotCodec.EncodeJson(state, MaxStateBytes);
            LastCaptureHash = contentHash;
            var bytes = encoded.Bytes;
            var result = new List<WorldEvent>();
            for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
            {
                int count = Math.Min(ChunkBytes, bytes.Length - offset); var part = new byte[count];
                Buffer.BlockCopy(bytes, offset, part, 0, count);
                var evt = new WorldEvent { Kind = "shared-economy-chunk",
                    Data = JsonSerializer.SerializeToElement(new SharedEconomyChunk {
                        Encoding = encoded.Encoding, RawLength = encoded.RawLength, Hash = encoded.Hash,
                        Sequence = state.Sequence, Length = bytes.Length, Offset = offset, Bytes = part
                    }, DedicatedProtocol.Json) };
                if (CanonicalJson.ByteCount(evt) > 48 * 1024)
                    throw new InvalidOperationException("Shared economy event exceeds transport limit.");
                result.Add(evt);
            }
            return result.ToArray();
        }
        private static void CheckCaptureSize(SharedEconomyState state)
        {
            ValidateSharedOnly(state);
            string contentHash = ContentHash(state);
            if (CanonicalJson.ByteCount(state) > MaxStateBytes) throw new InvalidOperationException("Shared economy state exceeds supported size.");
            LastCaptureHash = contentHash;
        }

        /// <summary>Stable gameplay-content hash. Ignores sequence, stack grouping,
        /// array order and native stack cache timestamps; preserves multiplicity,
        /// item identity, all four per-instance integers, wallet and vendor state.
        /// This is distinct from the chunk SHA-256, which authenticates exact bytes.</summary>
        public static string ContentHash(SharedEconomyState state)
            => ContentHash(state, null);

        private static string ContentHash(SharedEconomyState state, Dictionary<SharedItemStack[], string> itemHashes)
            => ContentHash(state, itemHashes, CancellationToken.None);

        private static string ContentHash(SharedEconomyState state, Dictionary<SharedItemStack[], string> itemHashes, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (state == null || state.Vendors == null || state.Vendors.Length > 4096)
                throw new ArgumentException("Invalid shared economy state.");
            using var stream = new CanonicalHashStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            // Hash the exact canonical bytes as they are written. A container
            // sink also forwards those bytes into the world sink, so neither
            // path retains a potentially multi-megabyte canonical byte buffer.
            void WriteItems(SharedItemStack[] stacks)
            {
                if (itemHashes == null) { WriteCanonicalItems(writer, stacks, cancellation); return; }
                using var itemStream = new CanonicalHashStream(stream);
                using var itemWriter = new BinaryWriter(itemStream, System.Text.Encoding.UTF8, true);
                WriteCanonicalItems(itemWriter, stacks, cancellation); itemWriter.Flush();
                itemHashes[stacks] = itemStream.Finish();
            }
            writer.Write("NivalisTogether.SharedEconomy.v1"); writer.Write(state.Money);
            WriteItems(state.Inventory);
            var vendors = new List<SharedVendorStock>(state.Vendors);
            vendors.Sort((a, b) => string.CompareOrdinal(a?.VendorId, b?.VendorId));
            writer.Write(vendors.Count); var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < vendors.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var vendor = vendors[i]; ValidateVendor(vendor, i, ids);
                writer.Write(vendor.VendorId);
                writer.Write(vendor.Relationship == 0 ? 0f : vendor.Relationship); writer.Write(vendor.LastRelationshipDay);
                WriteItems(vendor.Stacks);
            }
            writer.Flush(); return stream.Finish();
        }
        private static void ValidateVendor(SharedVendorStock stock, int index, HashSet<string> ids)
        {
            string field = stock == null ? "record=null" :
                !DedicatedProtocol.Text(stock.VendorId, 128) ? "VendorId=invalid" :
                ids != null && !ids.Add(stock.VendorId) ? "VendorId=duplicate" :
                !float.IsFinite(stock.Relationship) ? "Relationship=nonfinite" : null;
            if (field != null) throw new ArgumentException("Invalid vendor state (" + field + "): " + VendorDetails(stock, index) + ".");
        }
        private static string VendorDetails(SharedVendorStock stock, int index)
        {
            if (stock == null) return "index=" + index + "; record=null";
            string id = stock.VendorId;
            return "index=" + index + "; id=" + JsonSerializer.Serialize(id == null ? null : id.Substring(0, Math.Min(id.Length, 64))) +
                "; idLength=" + (id == null ? -1 : id.Length) + "; relationship=" + stock.Relationship.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; lastRelationshipDay=" + stock.LastRelationshipDay + "; stacks=" + (stock.Stacks == null ? -1 : stock.Stacks.Length);
        }
        private static void WriteCanonicalItems(BinaryWriter writer, SharedItemStack[] stacks)
            => WriteCanonicalItems(writer, stacks, CancellationToken.None);

        private static void WriteCanonicalItems(BinaryWriter writer, SharedItemStack[] stacks, CancellationToken cancellation)
        {
            if (stacks == null || stacks.Length > MaxStacks) throw new ArgumentException("Invalid inventory state.");
            var counts = new Dictionary<(string Type, int Value, int Creation, int Decay, int Last), int>();
            int total = 0;
            foreach (var stack in stacks)
            {
                cancellation.ThrowIfCancellationRequested();
                if (stack == null || !DedicatedProtocol.Text(stack.Type, 128) || stack.Items == null ||
                    stack.Items.Length > MaxInstances - total) throw new ArgumentException("Invalid item stack.");
                foreach (var item in stack.Items)
                {
                    if (item == null || item.Length != 4) throw new ArgumentException("Invalid item instance.");
                    var row = (stack.Type, item[0], item[1], item[2], item[3]);
                    counts.TryGetValue(row, out int count); counts[row] = count + 1;
                }
                total += stack.Items.Length;
            }
            var rows = new List<KeyValuePair<(string Type, int Value, int Creation, int Decay, int Last), int>>(counts);
            rows.Sort((a, b) => {
                int value = string.CompareOrdinal(a.Key.Type, b.Key.Type); if (value != 0) return value;
                value = a.Key.Value.CompareTo(b.Key.Value); if (value != 0) return value;
                value = a.Key.Creation.CompareTo(b.Key.Creation); if (value != 0) return value;
                value = a.Key.Decay.CompareTo(b.Key.Decay); return value != 0 ? value : a.Key.Last.CompareTo(b.Key.Last);
            });
            writer.Write(total);
            using var record = new MemoryStream(256);
            using var recordWriter = new BinaryWriter(record, System.Text.Encoding.UTF8, true);
            byte[] repeated = null;
            foreach (var entry in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                // Equal rows had adjacent identical bytes in the original sort.
                // Encode each once and repeat its exact bytes; signed ordering,
                // UTF-8 string length encoding and multiplicity stay unchanged.
                var item = entry.Key;
                record.Position = 0; record.SetLength(0);
                recordWriter.Write(item.Type); recordWriter.Write(item.Value); recordWriter.Write(item.Creation);
                recordWriter.Write(item.Decay); recordWriter.Write(item.Last); recordWriter.Flush();
                int length = checked((int)record.Length), remaining = entry.Value;
                if (remaining == 1) { writer.Write(record.GetBuffer(), 0, length); continue; }
                repeated ??= new byte[8192];
                int copies = Math.Min(remaining, repeated.Length / length);
                if (copies == 0) throw new InvalidOperationException("Canonical item record exceeds supported length.");
                for (int i = 0; i < copies; i++) Buffer.BlockCopy(record.GetBuffer(), 0, repeated, i * length, length);
                while (remaining > 0)
                {
                    int count = Math.Min(remaining, copies);
                    writer.Write(repeated, 0, count * length); remaining -= count;
                }
            }
        }

        internal sealed class EncodedEconomyUpdate
        {
            internal readonly byte[] Bytes;
            internal readonly string Encoding, Hash;
            internal readonly int RawLength, Generation;
            internal readonly long Sequence, ChunkStarted, ChunkJson;
            internal EncodedEconomyUpdate(byte[] bytes, string encoding, string hash, int rawLength,
                int generation, long sequence, long chunkStarted, long chunkJson)
            { Bytes = bytes; Encoding = encoding; Hash = hash; RawLength = rawLength; Generation = generation;
                Sequence = sequence; ChunkStarted = chunkStarted; ChunkJson = chunkJson; }
        }
        internal sealed class PreparedEconomyUpdate : IIncrementalPreparedWorldUpdate
        {
            internal readonly SharedEconomyState State;
            internal readonly Dictionary<SharedItemStack[], string> ItemHashes;
            internal readonly string ContentHash;
            internal readonly int Generation;
            private readonly EconomyProfile profile;
            private bool consumed;
            private IncrementalEconomyPlan plan;
            internal PreparedEconomyUpdate(SharedEconomyState state, Dictionary<SharedItemStack[], string> hashes,
                string contentHash, int generation, EconomyProfile profile)
            { State = state; ItemHashes = hashes; ContentHash = contentHash; Generation = generation; this.profile = profile; }
            public void Commit()
            {
                if (consumed || plan != null || Generation != preparationGeneration)
                    throw new InvalidOperationException("Discarded shared economy preparation cannot be applied.");
                consumed = true;
                profile.PreparationWall = Stopwatch.GetTimestamp() - profile.Started;
                profile.Started = Stopwatch.GetTimestamp();
                ApplyProfiled(State, profile, this);
            }
            internal void RequireCurrent()
            {
                if (consumed || Generation != preparationGeneration || Faulted)
                    throw new InvalidOperationException("Discarded shared economy preparation cannot be applied.");
            }
            public bool Advance(ClientMutationBudget budget)
            {
                if (budget == null) throw new ArgumentNullException(nameof(budget));
                RequireCurrent();
                long started = Stopwatch.GetTimestamp();
                var previous = activeProfile; activeProfile = profile;
                bool complete = false;
                try
                {
                    if (plan == null)
                    {
                        profile.PreparationWall = started - profile.Started;
                        profile.Started = started; profile.Incremental = true;
                        plan = new IncrementalEconomyPlan(this, profile);
                    }
                    complete = plan.Advance(budget);
                    RequireCurrent();
                    if (complete) { consumed = true; plan = null; }
                    return complete;
                }
                catch
                {
                    consumed = true; plan = null; profile.Failed = true;
                    if (Generation == preparationGeneration) Faulted = true;
                    throw;
                }
                finally
                {
                    profile.MainThreadTicks += Stopwatch.GetTimestamp() - started;
                    activeProfile = previous;
                    if (complete || profile.Failed) ReportPerformance(profile);
                }
            }
            public void Abort() { consumed = true; plan = null; }
        }

        // Client-only, disposable mirror reconciliation. Resolve ALL aliases
        // before the first mutation, then finish one physical container at a
        // time. The session keeps its FIFO/ACK fence across every yield. Public
        // Apply and the worker's synchronous transaction retain their rollback.
        private sealed class IncrementalEconomyPlan
        {
            private sealed class VendorTarget
            {
                internal Vendor Vendor;
                internal SharedVendorStock State;
                internal IntPtr Container;
            }
            private sealed class ContainerTarget
            {
                internal ItemApplyTarget Items;
                internal readonly List<VendorTarget> Vendors = new List<VendorTarget>();
                internal readonly List<(string Id, IntPtr Vendor)> Aliases = new List<(string, IntPtr)>();
            }
            private readonly PreparedEconomyUpdate prepared;
            private readonly EconomyProfile profile;
            private readonly Dictionary<IntPtr, ItemApplyTarget> itemTargets = new Dictionary<IntPtr, ItemApplyTarget>();
            private readonly Dictionary<IntPtr, ContainerTarget> containers = new Dictionary<IntPtr, ContainerTarget>();
            private readonly Dictionary<IntPtr, VendorTarget> vendors = new Dictionary<IntPtr, VendorTarget>();
            private readonly List<ContainerTarget> ordered = new List<ContainerTarget>();
            private readonly HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            private int resolving, applying, verifying;
            private bool changed, finished;
            private string personalHash;
            internal IncrementalEconomyPlan(PreparedEconomyUpdate prepared, EconomyProfile profile)
            {
                this.prepared = prepared; this.profile = profile;
                var state = prepared.State;
                if (state == null || state.Sequence <= 0 || state.Vendors == null || state.Vendors.Length > 4096)
                    throw new ArgumentException("Invalid shared economy state.");
                ValidateSharedOnly(state);
                profile.Sequence = state.Sequence; profile.Vendors = state.Vendors.Length;
                if (state.Sequence <= lastApplied) finished = true;
            }
            internal bool Advance(ClientMutationBudget budget)
            {
                if (finished) return true;
                while (resolving < prepared.State.Vendors.Length)
                {
                    if (!budget.TryStart(Stopwatch.GetTimestamp())) return false;
                    prepared.RequireCurrent(); long started = Stopwatch.GetTimestamp();
                    Resolve(prepared.State.Vendors[resolving], resolving);
                    resolving++; profile.Resolve += Stopwatch.GetTimestamp() - started;
                }
                while (applying < ordered.Count)
                {
                    if (!budget.TryStart(Stopwatch.GetTimestamp())) return false;
                    prepared.RequireCurrent(); ApplyContainer(ordered[applying]); applying++;
                }
                // A native callback in a later group can affect an earlier one.
                // Recheck all final groups after the last mutation, without
                // exposing a success sequence or ACK between verification steps.
                while (changed && verifying < ordered.Count)
                {
                    if (!budget.TryStart(Stopwatch.GetTimestamp())) return false;
                    prepared.RequireCurrent(); long started = Stopwatch.GetTimestamp();
                    VerifyContainer(ordered[verifying]); verifying++;
                    profile.Verify += Stopwatch.GetTimestamp() - started;
                }
                if (!budget.TryStart(Stopwatch.GetTimestamp())) return false;
                prepared.RequireCurrent();
                if (changed && PersonalHash(CapturePersonal()) != personalHash)
                    throw new InvalidOperationException("A vendor callback changed private account inventory.");
                LastComparedContainers = ordered.Count; LastReplacedContainers = profile.Replacements;
                LastApplyWasNoOp = profile.NoOp = !changed; profile.Containers = ordered.Count;
                if (changed)
                {
                    long started = Stopwatch.GetTimestamp(); RefreshVisibleShops(); profile.Ui += Stopwatch.GetTimestamp() - started;
                    prepared.RequireCurrent();
                }
                LastCaptureHash = prepared.ContentHash; lastApplied = prepared.State.Sequence;
                finished = true; ReportApply(prepared.State.Sequence, ordered.Count, profile.Replacements, !changed);
                return true;
            }
            private void Resolve(SharedVendorStock stock, int index)
            {
                ValidateVendor(stock, index, ids);
                var vendor = ResolveVendor(stock.VendorId);
                var container = vendor.Container;
                IntPtr containerPointer = container.Pointer, vendorPointer = vendor.Pointer;
                if (containerPointer == PlayerInventory().Items.Pointer)
                    throw new InvalidOperationException("A shared vendor cannot overwrite personal inventory.");
                AddItemTarget(itemTargets, container, stock.Stacks, prepared.ItemHashes[stock.Stacks]);
                if (!containers.TryGetValue(containerPointer, out var group))
                {
                    group = new ContainerTarget { Items = itemTargets[containerPointer] };
                    containers.Add(containerPointer, group); ordered.Add(group);
                }
                group.Aliases.Add((stock.VendorId, vendorPointer));
                if (vendors.TryGetValue(vendorPointer, out var previous))
                {
                    if (previous.Container != containerPointer || previous.State.Relationship != stock.Relationship ||
                        previous.State.LastRelationshipDay != stock.LastRelationshipDay)
                        throw new ArgumentException("Conflicting authoritative relationship for one native vendor instance.");
                }
                else
                {
                    var target = new VendorTarget { Vendor = vendor, State = stock, Container = containerPointer };
                    vendors.Add(vendorPointer, target); group.Vendors.Add(target);
                }
            }
            private void CheckIdentity(ContainerTarget group)
            {
                IntPtr pointer = group.Items.Target.Pointer;
                if (pointer == PlayerInventory().Items.Pointer)
                    throw new InvalidOperationException("A shared vendor cannot overwrite personal inventory.");
                foreach (var alias in group.Aliases)
                {
                    var current = ResolveVendor(alias.Id);
                    if (current.Pointer != alias.Vendor || current.Container.Pointer != pointer)
                        throw new InvalidOperationException("Native vendor identity changed during shared economy reconciliation.");
                }
            }
            private void ApplyContainer(ContainerTarget group)
            {
                CheckIdentity(group);
                var target = group.Items;
                long started = Stopwatch.GetTimestamp();
                bool exact = TryMatchNativeItems(target.Target, target.Desired, out var original);
                target.Original = exact ? original : CaptureItems(target.Target);
                profile.Capture += Stopwatch.GetTimestamp() - started; started = Stopwatch.GetTimestamp();
                bool replace = !exact && ItemContentHash(target.Original) != target.ExpectedHash;
                profile.Compare += Stopwatch.GetTimestamp() - started;
                bool relationships = false;
                foreach (var entry in group.Vendors)
                    if (entry.Vendor.relationshipLevelProgress != entry.State.Relationship || entry.Vendor.lastRelationshipLevelUpDay != entry.State.LastRelationshipDay)
                        relationships = true;
                if (!replace && !relationships) return;
                // Capture private state immediately before the first mutation;
                // the enclosing session prevents account writes while fenced.
                personalHash ??= PersonalHash(CapturePersonal());
                if (replace)
                {
                    started = Stopwatch.GetTimestamp(); target.Prepared = Prepare(target.Desired, target.Target);
                    profile.Prepare += Stopwatch.GetTimestamp() - started;
                }
                prepared.RequireCurrent();
                bool previousApplying = IsApplying; IsApplying = true;
                try
                {
                    started = Stopwatch.GetTimestamp();
                    if (replace) { Replace(target.Target, target.Prepared); prepared.RequireCurrent(); profile.Replacements++; }
                    foreach (var entry in group.Vendors)
                    {
                        if (entry.Vendor.relationshipLevelProgress != entry.State.Relationship) entry.Vendor.relationshipLevelProgress = entry.State.Relationship;
                        prepared.RequireCurrent();
                        if (entry.Vendor.lastRelationshipLevelUpDay != entry.State.LastRelationshipDay) entry.Vendor.lastRelationshipLevelUpDay = entry.State.LastRelationshipDay;
                        prepared.RequireCurrent();
                    }
                    profile.Mutate += Stopwatch.GetTimestamp() - started; started = Stopwatch.GetTimestamp();
                    VerifyContainer(group);
                    if (PersonalHash(CapturePersonal()) != personalHash)
                        throw new InvalidOperationException("A vendor callback changed private account inventory.");
                    prepared.RequireCurrent(); changed = true;
                    profile.Verify += Stopwatch.GetTimestamp() - started;
                }
                finally { IsApplying = previousApplying; target.Prepared = null; target.Original = null; }
            }
            private void VerifyContainer(ContainerTarget group)
            {
                CheckIdentity(group);
                var target = group.Items;
                if (!TryMatchNativeItems(target.Target, target.Desired, out _) && ItemContentHash(CaptureItems(target.Target)) != target.ExpectedHash)
                    throw new InvalidOperationException("The applied native inventory differs from the authoritative state.");
                foreach (var entry in group.Vendors)
                    if (entry.Vendor.relationshipLevelProgress != entry.State.Relationship || entry.Vendor.lastRelationshipLevelUpDay != entry.State.LastRelationshipDay)
                        throw new InvalidOperationException("The applied native vendor relationship differs from the authoritative state.");
            }
        }

        // Compatibility path for synchronous callers. Session uses the explicit
        // detach/prepare/commit contract and owns the final delivery ACK.
        public static void Apply(WorldEvent evt)
        {
            var complete = AcceptChunk(evt);
            if (complete != null) PrepareManaged(complete, CancellationToken.None, false).Commit();
        }

        // Unity-thread assembler. Once detached, every field belongs exclusively
        // to the managed preparation; no static assembler state is read by it.
        internal static EncodedEconomyUpdate AcceptChunk(WorldEvent evt)
        {
            if (evt == null || evt.Kind != "shared-economy-chunk") return null;
            long chunkStarted = Stopwatch.GetTimestamp();
            var chunk = evt.Data.Deserialize<SharedEconomyChunk>(DedicatedProtocol.Json);
            long chunkJsonTicks = Stopwatch.GetTimestamp() - chunkStarted;
            if (chunk == null || chunk.Sequence <= 0 || chunk.Length <= 0 || chunk.Length > MaxStateBytes ||
                chunk.Bytes == null || chunk.Bytes.Length == 0 || chunk.Bytes.Length > ChunkBytes ||
                chunk.Offset < 0 || chunk.Offset > chunk.Length - chunk.Bytes.Length ||
                chunk.Hash == null || chunk.Hash.Length != 64)
                throw new ArgumentException("Invalid shared economy chunk.");
            // Missing fields from the original uncompressed format remain readable.
            int rawLength = chunk.RawLength == 0 && chunk.Encoding == BoundedSnapshotCodec.JsonEncoding ? chunk.Length : chunk.RawLength;
            BoundedSnapshotCodec.ValidateHeader(chunk.Encoding, rawLength, chunk.Length, MaxStateBytes);
            if (chunk.Sequence <= lastApplied) return null;
            // A newly loaded client can enter the relay while an older snapshot
            // batch is already in flight. Wait for an offset-zero header before
            // assembling; once started, sequence/offset/hash checks stay strict.
            if (incoming == null && chunk.Offset != 0) return null;
            if (chunk.Offset == 0)
            {
                incoming = new byte[chunk.Length]; incomingOffset = 0; incomingSequence = chunk.Sequence;
                incomingHash = chunk.Hash; incomingEncoding = chunk.Encoding; incomingRawLength = rawLength;
                incomingStarted = Time.realtimeSinceStartup;
                incomingChunkJsonTicks = 0;
            }
            if (incoming == null || chunk.Sequence != incomingSequence || chunk.Hash != incomingHash ||
                chunk.Encoding != incomingEncoding || rawLength != incomingRawLength ||
                chunk.Length != incoming.Length || chunk.Offset != incomingOffset || Time.realtimeSinceStartup - incomingStarted > 30)
            { incoming = null; throw new InvalidDataException("Incomplete shared economy update; reconnect to refresh."); }
            Buffer.BlockCopy(chunk.Bytes, 0, incoming, incomingOffset, chunk.Bytes.Length); incomingOffset += chunk.Bytes.Length;
            incomingChunkJsonTicks += chunkJsonTicks;
            if (incomingOffset != incoming.Length) return null;
            var encoded = incoming; incoming = null;
            return new EncodedEconomyUpdate(encoded, incomingEncoding, incomingHash, incomingRawLength,
                preparationGeneration, incomingSequence, chunkStarted, incomingChunkJsonTicks);
        }

        // Fully managed, bounded work only: no Unity time, wrappers, diagnostics,
        // native resolution, assembler fields or session callbacks on this thread.
        internal static PreparedEconomyUpdate PrepareManaged(EncodedEconomyUpdate input, CancellationToken cancellation, bool background = true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (input == null || input.Bytes == null || input.Bytes.Length < 1 || input.Bytes.Length > MaxStateBytes || input.Sequence <= 0)
                throw new InvalidDataException("Invalid shared economy preparation.");
            var profile = new EconomyProfile { Started = input.ChunkStarted, ChunkJson = input.ChunkJson, BackgroundPrepared = background };
            long prepStarted = Stopwatch.GetTimestamp(), stageStarted = prepStarted;
            var state = BoundedSnapshotCodec.DecodeJson<SharedEconomyState>(input.Encoding, input.RawLength, input.Hash, input.Bytes, MaxStateBytes);
            // Streaming decompression and JSON parsing share one bounded pass;
            // StateJson now includes checksum/decode time, with Decode left zero.
            profile.Decode = 0;
            profile.StateJson = Stopwatch.GetTimestamp() - stageStarted;
            cancellation.ThrowIfCancellationRequested();
            if (state == null || state.Sequence != input.Sequence) throw new InvalidDataException("Invalid shared economy update.");
            ValidateSharedOnly(state);
            stageStarted = Stopwatch.GetTimestamp();
            var hashes = new Dictionary<SharedItemStack[], string>();
            string contentHash = ContentHash(state, hashes, cancellation);
            profile.Validation = Stopwatch.GetTimestamp() - stageStarted;
            profile.PreparationTicks = Stopwatch.GetTimestamp() - prepStarted;
            cancellation.ThrowIfCancellationRequested();
            return new PreparedEconomyUpdate(state, hashes, contentHash, input.Generation, profile);
        }

        public static void Apply(SharedEconomyState state)
            => ApplyProfiled(state, new EconomyProfile { Started = Stopwatch.GetTimestamp() });

        private static void ApplyProfiled(SharedEconomyState state, EconomyProfile profile, PreparedEconomyUpdate prepared = null)
        {
            profile.Sequence = state?.Sequence ?? 0; profile.Vendors = state?.Vendors?.Length ?? 0;
            var previous = activeProfile; activeProfile = profile;
            try { ApplyState(state, profile, prepared); }
            catch { profile.Failed = true; throw; }
            finally { activeProfile = previous; ReportPerformance(profile); }
        }

        private static void ApplyState(SharedEconomyState state, EconomyProfile profile, PreparedEconomyUpdate prepared)
        {
            if (state == null || state.Sequence <= 0 || state.Vendors == null || state.Vendors.Length > 4096)
                throw new ArgumentException("Invalid shared economy state.");
            ValidateSharedOnly(state);
            if (state.Sequence <= lastApplied) return;
            if (Faulted) throw new InvalidOperationException("Shared economy requires a fresh authoritative world load.");
            long stageStarted = Stopwatch.GetTimestamp();
            var authoritativeHashes = prepared?.ItemHashes ?? new Dictionary<SharedItemStack[], string>();
            string expectedHash = prepared?.ContentHash ?? ContentHash(state, authoritativeHashes);
            if (prepared == null) profile.Validation = Stopwatch.GetTimestamp() - stageStarted;
            stageStarted = Stopwatch.GetTimestamp();
            var inventory = PlayerInventory();
            var targets = new Dictionary<IntPtr, ItemApplyTarget>();
            var vendorTargets = new List<(Vendor Vendor, SharedVendorStock State, float OldRelationship, int OldDay)>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var relationshipTargets = new Dictionary<IntPtr, (float Relationship, int Day)>();
            foreach (var stock in state.Vendors)
            {
                ValidateVendor(stock, vendorTargets.Count, ids);
                var vendor = ResolveVendor(stock.VendorId);
                if (vendor.Container.Pointer == inventory.Items.Pointer)
                    throw new InvalidOperationException("A shared vendor cannot overwrite personal inventory.");
                AddItemTarget(targets, vendor.Container, stock.Stacks, authoritativeHashes[stock.Stacks]);
                if (relationshipTargets.TryGetValue(vendor.Pointer, out var existingRelationship))
                {
                    if (existingRelationship.Relationship != stock.Relationship || existingRelationship.Day != stock.LastRelationshipDay)
                        throw new ArgumentException("Conflicting authoritative relationship for one native vendor instance.");
                }
                else
                {
                    relationshipTargets.Add(vendor.Pointer, (stock.Relationship, stock.LastRelationshipDay));
                    vendorTargets.Add((vendor, stock, vendor.relationshipLevelProgress, vendor.lastRelationshipLevelUpDay));
                }
            }
            profile.Resolve = Stopwatch.GetTimestamp() - stageStarted - profile.DesiredHashes;
            // Inspect the CURRENT native contents on every update. A cached remote
            // hash alone would incorrectly hide local drift. Aliased vendor stock
            // shares one read and comparison, and an unchanged target allocates no
            // native replacement or backup inventories.
            int oldMoney = inventory.Money, replacements = 0;
            bool changed = false;
            foreach (var target in targets.Values)
            {
                stageStarted = Stopwatch.GetTimestamp();
                bool exactMatch = TryMatchNativeItems(target.Target, target.Desired, out var original);
                target.Original = exactMatch ? original : CaptureItems(target.Target);
                profile.Capture += Stopwatch.GetTimestamp() - stageStarted; stageStarted = Stopwatch.GetTimestamp();
                // AddItemTarget has already validated/hashed every authoritative
                // alias in this synchronous apply. Reuse that result, while
                // always hashing the newly read local contents for drift.
                target.Comparison = new InventoryComparison {
                    ExpectedHash = target.ExpectedHash, ObservedHash = exactMatch ? target.ExpectedHash : ItemContentHash(target.Original)
                };
                profile.Compare += Stopwatch.GetTimestamp() - stageStarted;
                if (!target.Comparison.RequiresReplacement) continue;
                stageStarted = Stopwatch.GetTimestamp();
                target.Prepared = Prepare(target.Desired, target.Target);
                target.Backup = Prepare(target.Original, target.Target);
                profile.Prepare += Stopwatch.GetTimestamp() - stageStarted;
                replacements++; changed = true;
            }
            foreach (var entry in vendorTargets)
                if (entry.OldRelationship != entry.State.Relationship || entry.OldDay != entry.State.LastRelationshipDay) changed = true;
            LastComparedContainers = targets.Count; LastReplacedContainers = replacements; LastApplyWasNoOp = !changed;
            profile.Containers = targets.Count; profile.Replacements = replacements; profile.NoOp = !changed;
            if (!changed)
            {
                LastCaptureHash = expectedHash; lastApplied = state.Sequence;
                ReportApply(state.Sequence, targets.Count, 0, true); return;
            }
            var personalBaseline = CapturePersonal();
            var personalHash = PersonalHash(personalBaseline);
            IsApplying = true;
            try
            {
                stageStarted = Stopwatch.GetTimestamp();
                foreach (var entry in targets.Values) if (entry.Prepared != null) Replace(entry.Target, entry.Prepared);
                foreach (var entry in vendorTargets)
                {
                    if (entry.OldRelationship != entry.State.Relationship) entry.Vendor.relationshipLevelProgress = entry.State.Relationship;
                    if (entry.OldDay != entry.State.LastRelationshipDay) entry.Vendor.lastRelationshipLevelUpDay = entry.State.LastRelationshipDay;
                }
                profile.Mutate = Stopwatch.GetTimestamp() - stageStarted; stageStarted = Stopwatch.GetTimestamp();
                // Native container/money setters can invoke arbitrary callbacks.
                // Verify EVERY unique target after any mutation, including targets
                // that originally matched. Never recapture stock for every alias.
                foreach (var entry in targets.Values)
                    if (ItemContentHash(CaptureItems(entry.Target)) != entry.Comparison.ExpectedHash)
                        throw new InvalidOperationException("The applied native inventory differs from the authoritative state.");
                foreach (var entry in vendorTargets)
                    if (entry.Vendor.relationshipLevelProgress != entry.State.Relationship || entry.Vendor.lastRelationshipLevelUpDay != entry.State.LastRelationshipDay)
                        throw new InvalidOperationException("The applied native vendor relationship differs from the authoritative state.");
                if (PersonalHash(CapturePersonal()) != personalHash)
                    throw new InvalidOperationException("A vendor callback changed private account inventory.");
                profile.Verify = Stopwatch.GetTimestamp() - stageStarted;
                LastCaptureHash = expectedHash;
                lastApplied = state.Sequence;
            }
            catch
            {
                long rollbackStarted = Stopwatch.GetTimestamp();
                // Keep the managed baseline for all unique targets. A callback may
                // have mutated an initially unchanged container; lazily construct
                // its rollback inventory only on this exceptional path.
                Faulted = true;
                foreach (var entry in targets.Values)
                    if (ItemContentHash(CaptureItems(entry.Target)) != entry.Comparison.ObservedHash)
                        Replace(entry.Target, entry.Backup ?? Prepare(entry.Original, entry.Target));
                if (PersonalHash(CapturePersonal()) != personalHash) ApplyPersonal(personalBaseline);
                foreach (var entry in vendorTargets)
                {
                    if (entry.Vendor.relationshipLevelProgress != entry.OldRelationship) entry.Vendor.relationshipLevelProgress = entry.OldRelationship;
                    if (entry.Vendor.lastRelationshipLevelUpDay != entry.OldDay) entry.Vendor.lastRelationshipLevelUpDay = entry.OldDay;
                }
                profile.Rollback = Stopwatch.GetTimestamp() - rollbackStarted;
                throw;
            }
            finally { IsApplying = false; }
            // State and content hash are committed above. UIPanel hides through a
            // CanvasGroup, so activeInHierarchy alone includes unbound hidden shops.
            stageStarted = Stopwatch.GetTimestamp(); RefreshVisibleShops(); profile.Ui = Stopwatch.GetTimestamp() - stageStarted;
            ReportApply(state.Sequence, targets.Count, replacements, false);
        }

        private sealed class ItemApplyTarget
        {
            internal ItemContainer Target, Prepared, Backup;
            internal SharedItemStack[] Desired, Original;
            internal string ExpectedHash;
            internal InventoryComparison Comparison;
        }
        internal sealed class InventoryComparison
        {
            internal string ExpectedHash, ObservedHash;
            internal bool RequiresReplacement => !string.Equals(ExpectedHash, ObservedHash, StringComparison.Ordinal);
        }
        // Pure semantic decision used by the native reconciliation path and tests.
        internal static InventoryComparison CompareInventory(SharedItemStack[] expected, SharedItemStack[] observed) =>
            new InventoryComparison { ExpectedHash = ItemContentHash(expected), ObservedHash = ItemContentHash(observed) };
        private static void AddItemTarget(Dictionary<IntPtr, ItemApplyTarget> targets, ItemContainer target, SharedItemStack[] desired, string hash)
        {
            long started = Stopwatch.GetTimestamp();
            if (activeProfile != null) activeProfile.DesiredHashes += Stopwatch.GetTimestamp() - started;
            if (targets.TryGetValue(target.Pointer, out var existing))
            {
                if (existing.ExpectedHash != hash) throw new ArgumentException("Conflicting authoritative stock for a shared native container.");
                return;
            }
            targets.Add(target.Pointer, new ItemApplyTarget { Target = target, Desired = desired, ExpectedHash = hash });
        }
        private static void ReportApply(long revision, int compared, int replaced, bool noOp)
        {
            string message = "Shared economy reconciliation sequence=" + revision + " uniqueContainers=" + compared +
                " replacedContainers=" + replaced + " noOp=" + noOp;
            try { if (Diagnostic != null) Diagnostic(message); else Debug.Log(message); } catch { }
        }
        private static void ReportPerformance(EconomyProfile profile)
        {
            // Numeric, aggregate diagnostics only; no payloads, identities or
            // per-item logging. At most one report per five seconds per process.
            long now = Stopwatch.GetTimestamp(), elapsed = now - profile.Started;
            long total = profile.Incremental ? profile.MainThreadTicks : elapsed;
            if (total < Stopwatch.Frequency / 20 && !profile.Failed && !profile.BackgroundPrepared) return;
            if (now < nextPerformanceReport) return;
            nextPerformanceReport = now + Stopwatch.Frequency * 5;
            string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string message = "ECONOMY_PROFILE sequence=" + profile.Sequence + " vendors=" + profile.Vendors +
                " containers=" + profile.Containers + " replaced=" + profile.Replacements + " noOp=" + profile.NoOp + " failed=" + profile.Failed +
                " captures=" + profile.Captures + " stacks=" + profile.Stacks + " items=" + profile.Items +
                " fastItems=" + profile.FastItems + " fallbackItems=" + profile.FallbackItems +
                " fastMatchedContainers=" + profile.FastMatchedContainers + " fastMatchedInstances=" + profile.FastMatchedInstances +
                " backgroundPrepared=" + profile.BackgroundPrepared + " incremental=" + profile.Incremental +
                " mainThreadMs=" + Ms(total) + " backgroundPrepMs=" + Ms(profile.BackgroundPrepared ? profile.PreparationTicks : 0) +
                " wallLatencyMs=" + Ms(profile.PreparationWall + elapsed) + " decodeMs=" + Ms(profile.Decode) + " stateJsonMs=" + Ms(profile.StateJson) +
                " validationHashMs=" + Ms(profile.Validation) + " resolveMs=" + Ms(profile.Resolve) + " desiredAliasHashMs=" + Ms(profile.DesiredHashes) +
                " captureMs=" + Ms(profile.Capture) + " compareMs=" + Ms(profile.Compare) + " prepareMs=" + Ms(profile.Prepare) +
                " mutateMs=" + Ms(profile.Mutate) + " verifyMs=" + Ms(profile.Verify) + " rollbackMs=" + Ms(profile.Rollback) + " uiMs=" + Ms(profile.Ui) +
                " nestedStackWalkMs=" + Ms(profile.StackWalk) + " nestedItemReadMs=" + Ms(profile.ItemReads) + " priorAndFinalChunkJsonMs=" + Ms(profile.ChunkJson);
            try { if (Diagnostic != null) Diagnostic(message); else Debug.Log(message); } catch { }
        }

        private static void RefreshVisibleShops()
        {
            foreach (var ui in ReplicatedUiRegistry.Snapshot<Nivalis.UI.ShopUINew>())
            {
                try
                {
                    if (ui == null || !ui.isActiveAndEnabled || !ui.gameObject.activeInHierarchy || !ui.IsVisible || !ui.IsOpen) continue;
                    var vendor = ui._targetVendor;
                    if (vendor == null || vendor.Container == null || ui.vendorInfoPanelUI == null || ui.playerMoneyText == null ||
                        ui.vendorInfoPanelUI._targetVendor == null || ui.vendorInfoPanelUI._targetVendor.Pointer != vendor.Pointer ||
                        ui.shoppingList == null || !ShopPanelReady(ui.playerItems, vendor) || !ShopPanelReady(ui.vendorItems, vendor)) continue;
                    // RefreshInventory already updates the money header internally.
                    ui.RefreshInventory(); LastRefreshError = "";
                }
                catch (Exception ex) when (IsShopPresentationException(ex))
                {
                    // A panel may be closing/destroyed during native UI callbacks.
                    // Only missing UI references are cosmetic; validation, inventory
                    // parity, native transaction and all other failures still escape.
                    string message = ex.Message ?? ex.GetType().Name;
                    if (message.Length > 600) message = message.Substring(0, 600);
                    if (LastRefreshError == message) continue;
                    LastRefreshError = message;
                    string diagnostic = "Shared economy applied; shop display refresh deferred: " + message;
                    try { if (Diagnostic != null) Diagnostic(diagnostic); else Debug.LogWarning(diagnostic); } catch { }
                }
            }
        }
        private static bool ShopPanelReady(Nivalis.UI.ShopUiVendorPanel panel, Vendor vendor) =>
            panel != null && panel._vendor != null && panel._vendor.Pointer == vendor.Pointer &&
            panel.itemsList != null && panel.itemFiltering != null && panel.shopUIFiltering != null;
        private static bool IsShopPresentationException(Exception ex)
        {
            if (ex is NullReferenceException) return true;
            if (ex is not Il2CppInterop.Runtime.Il2CppException) return false;
            string message = ex.Message ?? "";
            return (message.StartsWith("System.NullReferenceException:", StringComparison.Ordinal) ||
                    message.StartsWith("UnityEngine.MissingReferenceException:", StringComparison.Ordinal)) &&
                (message.Contains("Nivalis.UI.ShopUINew.", StringComparison.Ordinal) ||
                 message.Contains("Nivalis.UI.ShopUiVendorPanel.", StringComparison.Ordinal));
        }

        internal static void ValidateSharedOnly(SharedEconomyState state)
        {
            if (state == null || state.Money != 0 || state.Inventory == null || state.Inventory.Length != 0)
                throw new InvalidDataException("Shared vendor messages cannot contain a private wallet or inventory.");
        }

        public static PersonalInventoryState CapturePersonal() => new PersonalInventoryState {
            Money = PlayerInventory()._money, Inventory = CaptureItems(PlayerInventory().Items)
        };
        internal static string PersonalHash(PersonalInventoryState state) => ContentHash(new SharedEconomyState {
            Money = state.Money, Inventory = state.Inventory
        });

        // Retain the real container identity and its restriction/event registrations.
        // The prepared linked list and index are a matched pair. Concrete reference
        // setters use il2cpp_gc_wbarrier_set_field; no boxed generic value copies.
        // Context switches deliberately emit no gameplay money/contents callbacks.
        internal static void ApplyPersonal(PersonalInventoryState state, bool refreshUi = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var expected = PersonalHash(state);
            var inventory = PlayerInventory(); var target = inventory.Items;
            var prepared = Prepare(state.Inventory, target);
            var oldItems = target._items; var oldMap = target._typeMap; int oldMoney = inventory._money;
            bool previousApplying = IsApplying; IsApplying = true;
            try
            {
                target._items = prepared._items; target._typeMap = prepared._typeMap; inventory._money = state.Money;
                if (PersonalHash(CapturePersonal()) != expected)
                    throw new InvalidOperationException("Private native inventory did not match its validated account.");
            }
            catch
            {
                target._items = oldItems; target._typeMap = oldMap; inventory._money = oldMoney;
                throw;
            }
            finally
            {
                IsApplying = previousApplying;
                GC.KeepAlive(oldItems); GC.KeepAlive(oldMap); GC.KeepAlive(prepared); GC.KeepAlive(target); GC.KeepAlive(inventory);
            }
            if (refreshUi) RefreshPersonalDisplay(oldMoney, state.Money);
        }

        internal static PersonalInventoryState CreateStartingInventory(PlayerManagerConfiguration configuration)
        {
            if (configuration == null || configuration.StartingItems == null)
                throw new InvalidOperationException("Native starting inventory configuration is unavailable.");
            var target = PlayerInventory().Items;
            var fresh = new ItemContainer(target.IsManuallyUpdated, target.IsRefridgerated, target.Restriction);
            var initial = configuration.StartingItems; int total = 0, count = initial.Count;
            if (count < 0 || count > MaxStacks) throw new InvalidDataException("Invalid native starting item count.");
            for (int i = 0; i < count; i++)
            {
                var entry = initial[i];
                if (entry == null || entry.type == null || entry.amount < 0 || entry.amount > MaxInstances - total)
                    throw new InvalidDataException("Invalid native starting item definition.");
                if (entry.amount == 0) continue;
                total += entry.amount;
                // Native PlayerManager fresh initialization passes pricePerItem=0.
                // SafeCreate returns a reference; avoid its BasicTemp out overload.
                var stack = ItemStack.SafeCreate(entry.type, entry.amount, 0);
                if (stack == null || fresh.TryAdd(stack) != ItemCollectionOperationResult.Succeeded)
                    throw new InvalidOperationException("Native starting inventory does not fit.");
            }
            return new PersonalInventoryState { Money = configuration.Money, Inventory = CaptureItems(fresh) };
        }

        private static void RefreshPersonalDisplay(int oldMoney, int newMoney)
        {
            RefreshVisibleShops();
            foreach (var ui in ReplicatedUiRegistry.Snapshot<Nivalis.UI.PlayerInventoryUI>())
                try {
                    if (ui != null && ui.isActiveAndEnabled && ui.gameObject.activeInHierarchy && ui.IsVisible &&
                        ui.inventoryItems != null && ui._playerItems != null) ui.RefreshInventoryDisplay();
                } catch (Exception ex) when (IsShopPresentationException(ex)) { LastRefreshError = "Private inventory display refresh deferred."; }
            foreach (var ui in ReplicatedUiRegistry.Snapshot<WorldStateDisplayUI>())
                try {
                    // P_TopNavigation uses ValueDisplayUi, independently of the
                    // secondary PlayerMoneyDisplayUI. Its native handler only
                    // forwards the new balance to the currency presentation.
                    if (ui == null || !ui.isActiveAndEnabled || !ui.gameObject.activeInHierarchy) continue;
                    var display = ui.moneyDisplay;
                    if (display != null && display.isActiveAndEnabled && display.gameObject.activeInHierarchy &&
                        display.valueText != null && display.colorChangingGraphics != null)
                        ui.OnPlayerMoneyChanged(newMoney, newMoney);
                } catch (Exception ex) when (IsShopPresentationException(ex)) { LastRefreshError = "Private navigation balance refresh deferred."; }
            foreach (var ui in ReplicatedUiRegistry.Snapshot<PlayerMoneyDisplayUI>())
                try {
                    // Native UpdateMoney writes the balance before its zero-delta
                    // early return. Account synchronization is not a transaction:
                    // suppress the receipt animation and its optional UI objects.
                    if (ui != null && ui.isActiveAndEnabled && ui.gameObject.activeInHierarchy && ui.moneyTMP != null)
                        ui.UpdateMoney(newMoney, newMoney);
                } catch (Exception ex) when (IsShopPresentationException(ex)) { LastRefreshError = "Private balance display refresh deferred."; }
        }

        private static ItemContainer Prepare(SharedItemStack[] data, ItemContainer target)
        {
            if (data == null || data.Length > MaxStacks) throw new ArgumentException("Invalid inventory state.");
            var prepared = new ItemContainer(target.IsManuallyUpdated, target.IsRefridgerated, target.Restriction); int count = 0;
            foreach (var row in data)
            {
                if (row == null || !DedicatedProtocol.Text(row.Type, 128) || row.Items == null || row.Items.Length == 0 ||
                    row.Items.Length > MaxInstances - count) throw new ArgumentException("Invalid inventory stack.");
                count += row.Items.Length;
                var type = ResolveItem(row.Type);
                var stack = ItemStack.SafeCreateEmpty(type, row.Items.Length);
                foreach (var item in row.Items)
                {
                    if (item == null || item.Length != 4) throw new ArgumentException("Invalid item instance.");
                    stack.AddInstance(new ItemInstanceData { type = type, value = item[0], creationDay = item[1],
                        remainingDecayTime = item[2], lastUpdateDay = item[3] });
                }
                stack._lastUpdateTime = row.LastUpdate;
                if (prepared.TryAdd(stack) != ItemCollectionOperationResult.Succeeded)
                    throw new InvalidOperationException("The authoritative inventory does not fit its native container.");
            }
            return prepared;
        }
        private static void Replace(ItemContainer target, ItemContainer prepared)
        {
            // Native InventorySave compresses instances and reconstructs creationDay
            // from lastUpdateDay. Transfer exact objects instead of using that lossy path.
            var stacks = new List<ItemStack>();
            stacks.AddRange(SnapshotStacks(prepared));
            int expected = prepared.ItemCount;
            target.Clear();
            foreach (var stack in stacks)
                if (target.TryAdd(stack) != ItemCollectionOperationResult.Succeeded)
                    throw new InvalidOperationException("The authoritative inventory could not be applied in full.");
            target.Refresh();
            if (target.ItemCount != expected) throw new InvalidOperationException("The authoritative inventory count changed during apply.");
        }
        private static List<(ItemStack Stack, ItemInstanceData Item)> SelectInstances(ItemContainer container, ItemType type, FoodFreshness freshness, int amount)
        {
            var result = new List<(ItemStack Stack, ItemInstanceData Item)>();
            foreach (var stack in SnapshotStacks(container))
            {
                    if (stack == null || stack.Type == null || stack.Type.Guid != type.Guid) continue;
                    // Native containers merge types regardless of freshness; the
                    // shop's displayed freshness groups are filtered copies.
                    for (int i = stack._instanceData.Count - 1; i >= 0; i--)
                    {
                        var item = stack._instanceData[i];
                        if (item == null || item.Freshness != freshness) continue;
                        result.Add((stack, item));
                        if (result.Count == amount) return result;
                    }
            }
            return result;
        }
        private static PlayerInventory PlayerInventory()
        {
            var player = PlayerManager._instance != null ? PlayerManager._instance.LocalPlayer : null;
            if (player == null || player.Inventory == null || player.Inventory.Items == null || EconomyManager._instance == null)
                throw new InvalidOperationException("The shared economy is not ready.");
            return player.Inventory;
        }
        private static ItemType ResolveItem(string guid)
        {
            var type = ScriptableObjectDatabase.Instance.Provide<ItemType>(guid);
            if (type == null || type.Guid != guid) throw new ArgumentException("Unknown inventory item.");
            return type;
        }
        private static Vendor ResolveVendor(string id)
        {
            var roster = EconomyManager.Instance.Vendors;
            int separator = id == null ? -1 : id.IndexOf(':', 7);
            if (id == null || !id.StartsWith("vendor:", StringComparison.Ordinal) || separator < 8 ||
                !int.TryParse(id.Substring(7, separator - 7), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index) ||
                roster == null || index < 0 || index >= roster.Length)
                throw new ArgumentException("Invalid native vendor roster key.");
            var vendor = roster[index];
            if (vendor == null || vendor.Container == null || VendorKey(index, vendor) != id)
                throw new ArgumentException("The native vendor roster does not match the authoritative world.");
            return vendor;
        }
        // Native EconomyManager save/load serializes relationships by this array
        // index. Resources can contain distinct Vendor objects with one GUID and
        // one inventory, so GUID alone cannot address the relationship owner.
        private static string VendorKey(Vendor vendor)
        {
            var roster = EconomyManager.Instance.Vendors;
            if (vendor != null && roster != null)
                for (int i = 0; i < roster.Length; i++)
                    if (roster[i] != null && roster[i].Pointer == vendor.Pointer) return VendorKey(i, vendor);
            throw new InvalidOperationException("The selected vendor is not in the native world roster.");
        }
        private static string VendorKey(int index, Vendor vendor)
        {
            string id = vendor.Id;
            if (!DedicatedProtocol.Text(id, 112)) throw new InvalidOperationException("Invalid native vendor GUID at roster index " + index + ".");
            return "vendor:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + id;
        }
        private static string ItemContentHash(SharedItemStack[] stacks)
        {
            using var stream = new CanonicalHashStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            WriteCanonicalItems(writer, stacks); writer.Flush();
            return stream.Finish();
        }

        private sealed class CanonicalHashStream : Stream
        {
            private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private readonly Stream forward;
            private bool finished;
            internal CanonicalHashStream(Stream forward = null) { this.forward = forward; }
            internal string Finish()
            {
                if (finished) throw new InvalidOperationException("Canonical hash was already finalized.");
                finished = true;
                Span<byte> digest = stackalloc byte[32];
                if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
                    throw new CryptographicException("Invalid canonical hash digest.");
                return Convert.ToHexString(digest);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !finished;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (finished) throw new InvalidOperationException("Canonical hash was already finalized.");
                hash.AppendData(buffer); forward?.Write(buffer);
            }
            public override void WriteByte(byte value)
            {
                Span<byte> single = stackalloc byte[1]; single[0] = value; Write(single);
            }
            protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
        private static void ValidateAmount(int amount)
        { if (amount < 1 || amount > 1000) throw new ArgumentException("Trade quantity must be between 1 and 1000."); }

        private sealed class Rollback
        {
            internal Action Restore, Commit;
        }
        private static Rollback CreateRollback(PlayerInventory inventory, Vendor vendor)
        {
            int money = inventory.Money;
            var playerItems = Prepare(CaptureItems(inventory.Items), inventory.Items);
            var stock = Prepare(CaptureItems(vendor.Container), vendor.Container);
            var receipts = PlayerManager.Instance.LocalPlayer.PersonalReceipts;
            var copies = new Il2CppSystem.Collections.Generic.List<ReceiptBase>();
            var originals = receipts.GetReceipts();
            for (int i = 0; i < originals.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<ReceiptBase>>().Count; i++) copies.Add(originals[i].Clone());
            var economy = EconomyManager.Instance;
            float relationship = vendor.relationshipLevelProgress, gained = economy.barterExperienceGainedToday;
            int relationshipDay = vendor.lastRelationshipLevelUpDay, levelDay = economy.lastBarterSkillLevelUpDay,
                experienceDay = economy.barterExperienceDay;
            var barter = economy.barterLevel;
            var skillMap = SkillLevelController._instance != null ? SkillLevelController.Instance._playerData?.PerSkillExperience : null;
            var skillValues = SnapshotSkillExperience(skillMap);
            return new Rollback {
                Commit = () => { for (int i = 0; i < copies.Count; i++) copies[i].Dispose(); copies.Clear(); },
                Restore = () => {
                    Replace(inventory.Items, playerItems); Replace(vendor.Container, stock); inventory.Money = money;
                    receipts.Clear(true);
                    for (int i = 0; i < copies.Count; i++) { var receipt = copies[i]; receipts.Add(ref receipt, false); }
                    copies.Clear();
                    vendor.relationshipLevelProgress = relationship; vendor.lastRelationshipLevelUpDay = relationshipDay;
                    economy.barterLevel = barter; economy.lastBarterSkillLevelUpDay = levelDay;
                    economy.barterExperienceDay = experienceDay; economy.barterExperienceGainedToday = gained;
                    if (skillMap != null) { skillMap.Clear(); foreach (var entry in skillValues) skillMap.Add(entry.Key, entry.Value); }
                }
            };
        }
        private static List<KeyValuePair<Il2CppSystem.Type, SkillLevelController.PlayerSkillExperience>> SnapshotSkillExperience(
            Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, SkillLevelController.PlayerSkillExperience> map)
        {
            var result = new List<KeyValuePair<Il2CppSystem.Type, SkillLevelController.PlayerSkillExperience>>();
            if (map == null) return result;
            int count = map.Count, version = map.version;
            if (count < 0 || count > 4096) throw new InvalidOperationException("Invalid native skill map size.");
            // The generated IEnumerator interface invokes a boxed value-type
            // dictionary enumerator without unboxing its receiver. Copy only
            // reference keys, then use the safe CLR value-type out parameter.
            var keys = new Il2CppReferenceArray<Il2CppSystem.Type>(count);
            try
            {
                map.Keys.CopyTo(keys, 0);
                var seen = new HashSet<IntPtr>();
                for (int i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    if (key == null || !seen.Add(key.Pointer) || !map.TryGetValue(key, out var value))
                        throw new InvalidOperationException("Native skill map changed while copying its values.");
                    result.Add(new KeyValuePair<Il2CppSystem.Type, SkillLevelController.PlayerSkillExperience>(key, value));
                }
                if (map.Count != count || map.version != version)
                    throw new InvalidOperationException("Native skill map changed while copying its values.");
                return result;
            }
            finally { GC.KeepAlive(keys); GC.KeepAlive(map); }
        }
    }

    /// <summary>One managed preparation plus a bounded, strictly ordered inbox.
    /// Call every member except the work delegate on the owning main thread.</summary>
    internal sealed class BoundedPreparationFence<TState, TMessage>
    {
        // Shared by replacement sessions of the same closed type. Cancellation
        // does not release this lease until the actual delegate exits: JSON
        // deserialization cannot be forcibly interrupted midway through a call.
        private static int activeWorkers;
        private readonly LinkedList<(TMessage Message, int Bytes)> queued = new LinkedList<(TMessage, int)>();
        private readonly int maxMessages, maxBytes;
        private readonly double deadlineSeconds;
        private Task<TState> pending;
        private CancellationTokenSource cancellation;
        private TMessage finalMessage;
        private double started;
        private int epoch;
        internal BoundedPreparationFence(int maxMessages = 256, int maxBytes = 2 * 1024 * 1024, double deadlineSeconds = 10)
        {
            if (maxMessages < 1 || maxBytes < 1 || !double.IsFinite(deadlineSeconds) || deadlineSeconds <= 0)
                throw new ArgumentException("Invalid managed preparation bounds.");
            this.maxMessages = maxMessages; this.maxBytes = maxBytes; this.deadlineSeconds = deadlineSeconds;
        }
        internal bool IsPending => pending != null;
        internal bool IsFenced => IsPending || queued.Count > 0;
        internal int Count => queued.Count;
        internal int Bytes { get; private set; }
        internal void Start(Func<CancellationToken, TState> work, TMessage final, double now)
        {
            if (work == null || !double.IsFinite(now)) throw new ArgumentException("Invalid managed preparation.");
            if (pending != null || Interlocked.CompareExchange(ref activeWorkers, 1, 0) != 0)
                throw new InvalidOperationException("A previous managed preparation is still running.");
            cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            finalMessage = final; started = now;
            try
            {
                // Do not pass token to Task.Run: a pre-start cancellation must
                // still enter finally and release the actual-work lease.
                pending = Task.Run(() => {
                    try { token.ThrowIfCancellationRequested(); var value = work(token); token.ThrowIfCancellationRequested(); return value; }
                    finally { Interlocked.Exchange(ref activeWorkers, 0); }
                });
            }
            catch { cancellation.Dispose(); cancellation = null; finalMessage = default; Interlocked.Exchange(ref activeWorkers, 0); throw; }
        }
        internal void Enqueue(TMessage message, int bytes)
        {
            if (bytes < 0 || queued.Count >= maxMessages || bytes > maxBytes - Bytes)
                throw new InvalidDataException("Too many world updates arrived during managed preparation.");
            queued.AddLast((message, bytes)); Bytes += bytes;
        }
        internal void EnqueueFront(TMessage message, int bytes)
        {
            if (bytes < 0 || queued.Count >= maxMessages || bytes > maxBytes - Bytes)
                throw new InvalidDataException("Too many world updates arrived during managed preparation.");
            queued.AddFirst((message, bytes)); Bytes += bytes;
        }
        internal bool TryDequeue(out TMessage message)
        {
            message = default;
            if (pending != null || queued.Count == 0) return false;
            var entry = queued.First.Value; queued.RemoveFirst(); Bytes -= entry.Bytes; message = entry.Message; return true;
        }
        internal bool TryCommit(double now, Action<TState> apply, Action<TMessage> acknowledge)
            => TryProgress(now, result => { apply(result); return true; }, acknowledge);
        internal bool TryProgress(double now, Func<TState, bool> advance, Action<TMessage> acknowledge)
        {
            if (pending == null) return false;
            if (!double.IsFinite(now) || now - started > deadlineSeconds)
            { Cancel(); throw new TimeoutException("Shared economy preparation exceeded its deadline."); }
            if (!pending.IsCompleted) return false;
            int currentEpoch = epoch;
            var message = finalMessage;
            var result = pending.GetAwaiter().GetResult();
            bool complete = advance(result); // False or failure retains the pending task and final ACK.
            if (currentEpoch != epoch) return false; // Native callback stopped the session.
            if (!complete) return false;
            pending = null; finalMessage = default; cancellation.Dispose(); cancellation = null;
            acknowledge(message);
            return true;
        }
        internal void Cancel()
        {
            epoch++; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            var abandoned = pending; pending = null; finalMessage = default;
            queued.Clear(); Bytes = 0;
            if (abandoned != null)
                _ = abandoned.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
