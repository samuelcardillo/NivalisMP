using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NivalisTogether.Dedicated;

namespace NivalisTogether.Dedicated
{
    public sealed class ItemContainer
    {
        private static long next;
        public IntPtr Pointer { get; } = new IntPtr(++next);
        public SharedItemStack[] Items = Array.Empty<SharedItemStack>();
        public Action Replaced;
    }
    public sealed class Vendor
    {
        private static long next = 10000;
        public IntPtr Pointer { get; } = new IntPtr(++next);
        public ItemContainer Container;
        public float relationshipLevelProgress;
        public int lastRelationshipLevelUpDay;
    }
    internal interface IPreparedWorldUpdate { void Commit(); }
    internal interface IIncrementalPreparedWorldUpdate : IPreparedWorldUpdate { bool Advance(ClientMutationBudget budget); void Abort(); }
    public static partial class SharedEconomyReplication
    {
        private static int preparationGeneration;
        private static EconomyProfile activeProfile;
        public static bool Faulted, IsApplying, LastApplyWasNoOp;
        public static string LastCaptureHash = "";
        private static long lastApplied;
        public static int LastComparedContainers, LastReplacedContainers;
        internal static int Replacements, Publications, UiRefreshes;
        internal static string Personal = "private-original";
        internal static readonly Dictionary<string, Vendor> Registry = new Dictionary<string, Vendor>();
        internal sealed class Inventory { public ItemContainer Items = new ItemContainer(); }
        private static Inventory inventory = new Inventory();
        private static Inventory PlayerInventory() => inventory;
        private static Vendor ResolveVendor(string id) => Registry[id];
        private static void ValidateSharedOnly(SharedEconomyState state)
        { if (state.Money != 0 || state.Inventory == null || state.Inventory.Length != 0) throw new ArgumentException(); }
        private static void ValidateVendor(SharedVendorStock value, int index, HashSet<string> ids)
        { if (value == null || value.VendorId == null || !ids.Add(value.VendorId) || !float.IsFinite(value.Relationship)) throw new ArgumentException(); }
        private static string ItemContentHash(SharedItemStack[] stacks) => string.Join("|", stacks.Select(x => x.Type + ":" + string.Join(";", x.Items.Select(r => string.Join(",", r)))));
        private static bool TryMatchNativeItems(ItemContainer target, SharedItemStack[] desired, out SharedItemStack[] original)
        { original = target.Items; return ItemContentHash(target.Items) == ItemContentHash(desired); }
        private static SharedItemStack[] CaptureItems(ItemContainer target) => target.Items;
        private static ItemContainer Prepare(SharedItemStack[] desired, ItemContainer target) => new ItemContainer { Items = desired };
        private static void Replace(ItemContainer target, ItemContainer prepared)
        { Replacements++; target.Items = prepared.Items; target.Replaced?.Invoke(); }
        private static string CapturePersonal() => Personal;
        private static string PersonalHash(string value) => value;
        private static void RefreshVisibleShops() => UiRefreshes++;
        private static void ReportApply(long sequence, int compared, int replaced, bool noop) => Publications++;
        private static void ReportPerformance(EconomyProfile value) { }
        private static void ApplyProfiled(SharedEconomyState state, EconomyProfile profile, PreparedEconomyUpdate prepared)
        { throw new NotSupportedException("Synchronous implementation remains in production and is not used by this extracted incremental test."); }
        internal static void ResetFixture()
        {
            preparationGeneration++; Faulted = IsApplying = false; lastApplied = 0; LastCaptureHash = "";
            LastComparedContainers = LastReplacedContainers = Replacements = Publications = UiRefreshes = 0;
            Registry.Clear(); Personal = "private-original"; inventory = new Inventory();
        }
        internal static SharedItemStack[] Items(int number) => new[] { new SharedItemStack { Type = "type", Items = new[] { new[] { number, 2, 3, 4 } } } };
        internal static Vendor Add(string id, int value, ItemContainer alias = null)
        { var vendor = new Vendor { Container = alias ?? new ItemContainer { Items = Items(value) } }; Registry.Add(id, vendor); return vendor; }
        internal static SharedVendorStock Desired(string id, int number, float relationship = 0) => new SharedVendorStock { VendorId = id, Stacks = Items(number), Relationship = relationship };
        internal static PreparedEconomyUpdate PrepareFixture(params SharedVendorStock[] stocks)
        {
            var state = new SharedEconomyState { Sequence = 1, Vendors = stocks };
            var hashes = new Dictionary<SharedItemStack[], string>(); foreach (var stock in stocks) hashes[stock.Stacks] = ItemContentHash(stock.Stacks);
            return new PreparedEconomyUpdate(state, hashes, "snapshot-committed", preparationGeneration, new EconomyProfile { Started = Stopwatch.GetTimestamp() });
        }
        internal static bool HasValue(Vendor vendor, int value) => vendor.Container.Items[0].Items[0][0] == value;
    }
}

internal static class EconomyIncrementalTests
{
    private static int checks;
    private static void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    private static void Reject(Action action, string why)
    { bool failed = false; try { action(); } catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is TimeoutException) { failed = true; } Check(failed, why); }
    private static ClientMutationBudget Budget() => new ClientMutationBudget(Stopwatch.Frequency, 100000, 1);
    private static bool Step(SharedEconomyReplication.PreparedEconomyUpdate value, ClientMutationBudget budget, int frame)
    { budget.BeginFrame(frame); return value.Advance(budget); }
    private static void UnitsAndAliases()
    {
        SharedEconomyReplication.ResetFixture();
        var a = SharedEconomyReplication.Add("a", 0); var alias = SharedEconomyReplication.Add("alias", 0, a.Container); var b = SharedEconomyReplication.Add("b", 0);
        var p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7), SharedEconomyReplication.Desired("alias", 7), SharedEconomyReplication.Desired("b", 9));
        var budget = Budget();
        for (int frame = 1; frame <= 3; frame++) { Check(!Step(p, budget, frame), "Alias preflight yields"); Check(SharedEconomyReplication.Replacements == 0, "No mutation before all aliases validated"); }
        Check(!Step(p, budget, 4) && SharedEconomyReplication.HasValue(a, 7) && SharedEconomyReplication.HasValue(alias, 7), "One complete shared-container unit updates aliases atomically");
        Check(SharedEconomyReplication.HasValue(b, 0) && SharedEconomyReplication.Replacements == 1, "Later container waits");
        Check(!Step(p, budget, 5) && SharedEconomyReplication.HasValue(b, 9), "Second group applies separately");
        Check(SharedEconomyReplication.LastCaptureHash == "" && SharedEconomyReplication.Publications == 0, "Partial mirror never publishes completed snapshot");
        Check(!Step(p, budget, 6) && !Step(p, budget, 7), "Final verification remains sliced");
        Check(Step(p, budget, 8) && SharedEconomyReplication.LastCaptureHash == "snapshot-committed", "Publish only after all groups verified");
        Check(SharedEconomyReplication.Replacements == 2 && SharedEconomyReplication.LastComparedContainers == 2 && SharedEconomyReplication.UiRefreshes == 1, "Aliases count once; presentation refresh once");
        Reject(() => Step(p, budget, 9), "Completed cursor cannot be reused");
    }
    private static void DenialsAndDrift()
    {
        SharedEconomyReplication.ResetFixture(); var a = SharedEconomyReplication.Add("a", 0); SharedEconomyReplication.Add("alias", 0, a.Container);
        var p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7), SharedEconomyReplication.Desired("alias", 8)); var budget = Budget();
        Check(!Step(p, budget, 1), "First alias may stage"); Reject(() => Step(p, budget, 2), "Conflicting shared stock rejected before writes");
        Check(SharedEconomyReplication.Replacements == 0 && SharedEconomyReplication.Faulted, "Conflict leaves native inventory untouched and faults mirror");
        SharedEconomyReplication.ResetFixture(); a = SharedEconomyReplication.Add("a", 0); SharedEconomyReplication.Registry.Add("alias", a);
        p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7, 1), SharedEconomyReplication.Desired("alias", 7, 2)); budget = Budget();
        Step(p, budget, 1); Reject(() => Step(p, budget, 2), "Conflicting native vendor relationship rejected before writes"); Check(SharedEconomyReplication.Replacements == 0, "Relationship alias rejection is preflight");
        SharedEconomyReplication.ResetFixture(); a = SharedEconomyReplication.Add("a", 0); var b = SharedEconomyReplication.Add("b", 0);
        b.Container.Replaced = () => a.Container.Items = SharedEconomyReplication.Items(123);
        p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7), SharedEconomyReplication.Desired("b", 9)); budget = Budget();
        for (int frame = 1; frame <= 4; frame++) Step(p, budget, frame);
        Reject(() => Step(p, budget, 5), "Final pass catches later callback drift of earlier completed group");
        Check(SharedEconomyReplication.Publications == 0 && SharedEconomyReplication.LastCaptureHash == "" && !SharedEconomyReplication.IsApplying, "Failure does not publish or leak applying flag");
        SharedEconomyReplication.ResetFixture(); a = SharedEconomyReplication.Add("a", 0); a.Container.Replaced = () => SharedEconomyReplication.Personal = "changed";
        p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7)); budget = Budget(); Step(p, budget, 1);
        Reject(() => Step(p, budget, 2), "Native callback private-inventory mutation fails same group"); Check(SharedEconomyReplication.Publications == 0, "Private-state failure has no success");
        SharedEconomyReplication.ResetFixture(); a = SharedEconomyReplication.Add("a", 0); p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7)); budget = Budget(); Step(p, budget, 1);
        SharedEconomyReplication.Registry["a"] = new Vendor { Container = new ItemContainer() };
        Reject(() => Step(p, budget, 2), "Rebound vendor between slices is rejected"); Check(SharedEconomyReplication.Replacements == 0, "Identity failure precedes mutation");
    }
    private static void BudgetsAndReset()
    {
        SharedEconomyReplication.ResetFixture(); SharedEconomyReplication.Add("a", 7);
        var p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7)); var budget = Budget(); budget.BeginFrame(1); budget.TryStart(Stopwatch.GetTimestamp());
        Check(!p.Advance(budget) && SharedEconomyReplication.Publications == 0, "Shared exhausted frame cannot start unit");
        Check(!Step(p, budget, 2) && !Step(p, budget, 3), "No-op still observes current native state in its own unit");
        Check(Step(p, budget, 4) && SharedEconomyReplication.LastApplyWasNoOp && SharedEconomyReplication.Replacements == 0, "No-op publishes only at end without replacements");
        SharedEconomyReplication.ResetFixture(); SharedEconomyReplication.Add("a", 0); p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7)); budget = Budget(); Step(p, budget, 1); p.Abort();
        Reject(() => Step(p, budget, 2), "Aborted plan cannot resume"); Check(SharedEconomyReplication.Replacements == 0, "Abort during preflight has no native write");
        SharedEconomyReplication.ResetFixture(); SharedEconomyReplication.Add("a", 0); p = SharedEconomyReplication.PrepareFixture(SharedEconomyReplication.Desired("a", 7)); budget = Budget(); Step(p, budget, 1); SharedEconomyReplication.ResetFixture();
        Reject(() => Step(p, budget, 2), "New session generation rejects old cursor"); Check(!SharedEconomyReplication.Faulted, "Old cursor does not poison new session fault flag");
    }
    private static Task Pending(BoundedPreparationFence<object, int> fence) => (Task)fence.GetType().GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fence);
    private static void FenceProgress()
    {
        var fence = new BoundedPreparationFence<object, int>(); var state = new object(); int calls = 0, ack = 0;
        fence.Start(_ => state, 1, 0); Pending(fence).Wait(); fence.Enqueue(2, 10);
        Check(!fence.TryProgress(1, value => { Check(ReferenceEquals(state, value), "Same detached state survives yields"); calls++; return false; }, _ => ack++), "Incomplete native cursor yields");
        Check(fence.IsPending && fence.IsFenced && !fence.TryDequeue(out _) && ack == 0, "No early ACK or overtaking after completed worker task");
        Check(fence.TryProgress(2, value => { Check(ReferenceEquals(state, value), "Yielded state retained"); calls++; return true; }, _ => ack++), "Final native unit releases original ACK");
        Check(calls == 2 && ack == 1 && !fence.IsPending && fence.TryDequeue(out int next) && next == 2, "FIFO advances only after success");
        fence.Start(_ => new object(), 3, 0); Pending(fence).Wait(); fence.TryProgress(1, _ => false, _ => ack++);
        Reject(() => fence.TryProgress(11, _ => true, _ => ack++), "Deadline applies across native yields"); Check(ack == 1 && !fence.IsFenced, "Timed out cursor never acknowledged");
        fence.Start(_ => new object(), 4, 0); Pending(fence).Wait();
        Check(!fence.TryProgress(1, _ => { fence.Cancel(); return true; }, _ => ack++), "Reentrant cancellation suppresses completion"); Check(ack == 1, "Cancelled epoch cannot ACK");
    }
    public static int Main()
    {
        try { UnitsAndAliases(); DenialsAndDrift(); BudgetsAndReset(); FenceProgress(); Console.WriteLine("PASS EconomyIncrementalTests: " + checks + " assertions; extracted production cursor/fence, alias preflight, budgets, callback drift, cancellation and final ACK."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
