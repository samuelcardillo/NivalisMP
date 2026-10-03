using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;
using Nivalis;
using Nivalis.CraftingSystem;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using Nivalis.Locale;
using UnityEngine;

namespace NivalisTogether.Dedicated
{
    public sealed class BusinessContainerState
    {
        public bool Manual { get; set; }
        public bool Refrigerated { get; set; }
        public int? MaxItems { get; set; }
        public int? MaxSlots { get; set; }
        public string TagFilter { get; set; } = "";
        public bool AcceptsIngredients { get; set; }
        public SharedItemStack[] Items { get; set; } = Array.Empty<SharedItemStack>();
    }
    public sealed class BusinessMenuItem
    {
        public string RecipeId { get; set; } = "";
        public int Price { get; set; }
    }
    public sealed class BusinessPersonState
    {
        public string PersonId { get; set; } = "";
        public string VenueId { get; set; } = "";
        public bool HasWork { get; set; }
        public bool Hireable { get; set; }
        public int Wage { get; set; }
        public int BaseWorkingHours { get; set; }
        public float StartHour { get; set; }
        public float EndHour { get; set; }
        public int Tasks { get; set; }
        public float LastPaidWage { get; set; }
        public float WorkQuit { get; set; }
        public int Happiness { get; set; }
        public int WageInfluence { get; set; }
        public int SkillInfluence { get; set; }
        public float HappinessValue { get; set; }
    }
    public sealed class BusinessVenueState
    {
        public string GhostId { get; set; } = "";
        public string VenueId { get; set; } = "";
        public bool Open { get; set; }
        public int Ownership { get; set; }
        public int Level { get; set; }
        public int MealsServed { get; set; }
        public int Visits { get; set; }
        public bool Visited { get; set; }
        public int? Debt { get; set; }
        public int? FridgeCapacity { get; set; }
        public int? CupboardCapacity { get; set; }
        public BusinessMenuItem[] Menu { get; set; } = Array.Empty<BusinessMenuItem>();
        public string[] Staff { get; set; } = Array.Empty<string>();
        public BusinessContainerState Fridge { get; set; } = new BusinessContainerState();
        public BusinessContainerState Cupboard { get; set; } = new BusinessContainerState();
        public BusinessContainerState Furniture { get; set; } = new BusinessContainerState();
        // day, cached profit. Full receipt/review records remain native-checkpoint state.
        public int[][] Profits { get; set; } = Array.Empty<int[]>();
    }
    public sealed class BusinessState
    {
        public long Sequence { get; set; }
        public BusinessVenueState[] Venues { get; set; } = Array.Empty<BusinessVenueState>();
        public BusinessPersonState[] People { get; set; } = Array.Empty<BusinessPersonState>();
    }
    public sealed class BusinessStateChunk
    {
        public long Sequence { get; set; }
        public string Hash { get; set; } = "";
        public string Encoding { get; set; } = BoundedSnapshotCodec.JsonEncoding;
        public int RawLength { get; set; }
        public int Length { get; set; }
        public int Offset { get; set; }
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Raw client mirror for loaded native venues. Does not replay hire/fire,
    /// ownership, payroll, menus, inventory events or rewards. Native world import
    /// must finish first; gameplay intent validation belongs to BusinessReplication.
    /// Inventory and staffing UI refresh is the caller's responsibility.
    /// </summary>
    public static class BusinessStateReplication
    {
        private const int MaxVenues = 2048, MaxPeople = 20000, MaxBytes = 16 * 1024 * 1024, ChunkBytes = 24 * 1024;
        private static readonly Dictionary<string, Person> TrackedPeople = new Dictionary<string, Person>(StringComparer.Ordinal);
        private static long sequence, applied, incomingSequence;
        private static byte[] incoming;
        private static int incomingOffset, incomingRawLength;
        private static string incomingHash, incomingEncoding;
        private static float incomingStarted;
        private static int preparationGeneration;
        private static long nextProfileReport;
        private sealed class ApplyProfile
        {
            internal long Started, DecodeJson, Validation, Observation, Comparison, Preparation, Mutation, Verify, Ui, Rollback;
            internal long VenuePhase, PeoplePhase, VenueAllocated, PeopleAllocated;
            internal long ManagedPreparation;
            internal long IncrementalMainThread;
            internal int Slices;
            internal Action CheckClientContext;
            internal int Venues, People, Containers, Items, ChangedVenues, ChangedPeople, ChangedContainers;
            internal bool NoOp, Failed, WireValidated, BackgroundPrepared, Incremental;
            internal bool InspectDifferences;
            internal long Sequence, DifferenceCost;
            internal readonly int[] DifferenceCounts = new int[6];
            internal int ItemOnlyDifferences;
            internal readonly List<ContainerProbe> Probes = new List<ContainerProbe>();
            internal readonly List<(BusinessContainerState Expected, ItemContainer Target)> InventoryChecks = new();
            internal readonly HashSet<IntPtr> ChangedInventoryPointers = new();
        }
        private sealed class ContainerProbe
        {
            internal string Kind, Label, Before, AfterWrite = "not-run", AfterUi = "not-run";
            internal ItemContainer Target;
        }
        public static bool IsApplying { get; private set; }
        /// <summary>Optional visual refresh; invoked while outbound UI intents must be suppressed.</summary>
        public static Action RefreshUi { get; set; }
        public static Action<string> Diagnostic { get; set; }
        public static string LastRefreshError { get; private set; } = "";
        /// <summary>SHA256 of the last complete encoded transport payload, not a semantic business-state digest.</summary>
        public static string LastCaptureHash { get; private set; } = "";
        public static void Reset()
        {
            preparationGeneration++;
            sequence = applied = incomingSequence = 0; incoming = null; incomingOffset = 0;
            incomingHash = incomingEncoding = null; incomingRawLength = 0;
            TrackedPeople.Clear(); LastCaptureHash = ""; LastRefreshError = ""; IsApplying = false;
            nextProfileReport = 0;
        }
        /// <summary>Call before a native staff operation, especially before firing/removing a bot.</summary>
        public static void TrackPerson(Person person)
        {
            if (person == null || person.RuntimeData == null) return;
            string id=person.Guid;
            if (!DedicatedProtocol.Text(id, 128)) return;
            if (TrackedPeople.Count >= MaxPeople && !TrackedPeople.ContainsKey(id))
                throw new InvalidOperationException("Business person roster exceeds supported size.");
            TrackedPeople[id] = person;
        }
        public static BusinessState Capture()
        {
            var manager = Ready();
            var venues = new List<BusinessVenueState>();
            // Concrete native ValueCollection iterator avoids boxed generic IEnumerator methods.
            var iterator = manager.Ghosts.GetEnumerator();
            while (iterator.MoveNext())
            {
                var ghost = iterator.Current?.TryCast<VenueAreaGhost>();
                if (ghost == null || !ghost.IsValid || ghost.Venue == null) continue;
                venues.Add(CaptureVenue(ghost));
                if (venues.Count > MaxVenues) throw new InvalidOperationException("Too many business venues.");
            }
            foreach (var agent in manager._agentGhosts) if (agent != null) TrackPerson(agent.Person);
            var people = new List<BusinessPersonState>();
            foreach (var entry in TrackedPeople)
                if (entry.Value != null && entry.Value.RuntimeData != null) people.Add(CapturePerson(entry.Value));
            venues.Sort((a,b) => string.CompareOrdinal(a.GhostId,b.GhostId));
            people.Sort((a,b) => string.CompareOrdinal(a.PersonId,b.PersonId));
            return new BusinessState {Sequence=++sequence, Venues=venues.ToArray(), People=people.ToArray()};
        }
        private static BusinessVenueState CaptureVenue(VenueAreaGhost ghost) => CaptureVenue(ghost,null);
        private static BusinessVenueState CaptureVenue(VenueAreaGhost ghost,BusinessVenueState expected)
        {
            // These wrappers belong only to this synchronous observation. Re-read
            // them next capture; repeated generated getters also re-enter the
            // native pointer cache even when the reference is unchanged.
            var open=ghost._isOpen;var joint=ghost.JointInventory;var nativeMenu=ghost._menu;var nativeStaff=ghost.staff;
            if (open == null || joint == null || nativeMenu == null || nativeStaff == null)
                throw new InvalidOperationException("Venue business state is not initialized.");
            var venue=ghost.Venue;var debt=venue.currentDebt;
            var menu = new List<BusinessMenuItem>();
            int menuCount=nativeMenu.Count;
            for (int i=0; i<menuCount; i++) {
                var item=nativeMenu[i];var recipe=item?.recipe;
                if (recipe == null) throw new InvalidOperationException("Venue menu has an unresolved recipe.");
                menu.Add(new BusinessMenuItem {RecipeId=recipe.Guid,Price=item.price});
            }
            var staff = new string[nativeStaff.Count];
            for(int i=0;i<staff.Length;i++) { var person=nativeStaff[i]; TrackPerson(person); staff[i]=person.Guid; }
            Array.Sort(staff,StringComparer.Ordinal);
            var profits=new List<int[]>();
            var nativeProfits=ghost._finances?._profitsPerDay;
            if(nativeProfits != null) {
                var days=nativeProfits.Keys.GetEnumerator();
                while(days.MoveNext()) {int day=days.Current;profits.Add(new[]{day,nativeProfits[day]});}
            }
            profits.Sort((a,b)=>a[0].CompareTo(b[0]));
            return new BusinessVenueState {
                GhostId=ghost.Id,VenueId=venue.Guid,Open=open.Value,
                Ownership=(int)ghost.OwnershipType,Level=ghost.currentLevel,MealsServed=ghost.mealsServed,
                Visits=ghost.totalVisitsCount,Visited=ghost.wasVisitedByPlayer,
                Debt=debt==null?(int?)null:debt.Value,
                FridgeCapacity=NativeCapacity(CapacityRestriction(joint._RefridgeratedInventory_k__BackingField).MaxItems),
                CupboardCapacity=NativeCapacity(CapacityRestriction(joint._NormalInventory_k__BackingField).MaxItems),
                Menu=menu.ToArray(),Staff=staff,Profits=profits.ToArray(),
                Fridge=CaptureContainerState(ghost.FridgeInventory,expected?.Fridge?.Items),Cupboard=CaptureContainerState(ghost.CupboardInventory,expected?.Cupboard?.Items),
                Furniture=CaptureContainerState(ghost.FurnitureInventory,expected?.Furniture?.Items)
            };
        }
        private static BusinessPersonState CapturePerson(Person person)
        {
            return new PersonObservation(person.Guid,person._RuntimeData_k__BackingField).ToState();
        }
        // Fresh value observation, never cached across snapshots. This is a
        // managed value struct, so the usual unchanged-person path allocates no
        // temporary wire DTO while still reading every native work field.
        private readonly struct PersonObservation
        {
            internal readonly string PersonId, VenueId;
            internal readonly Venue Work;
            internal readonly bool HasWork, Hireable;
            internal readonly int Wage, BaseWorkingHours, Tasks, Happiness, WageInfluence, SkillInfluence;
            internal readonly float StartHour, EndHour, LastPaidWage, WorkQuit, HappinessValue;
            internal PersonObservation(string id, RuntimePersonData data)
            {
                if(data==null)throw new InvalidOperationException("Native staff runtime state is missing.");
                PersonId=id;Work=data._worksAt;VenueId=Work==null?"":Work.Guid;
                var hours=data.WorkingHours;var happiness=data.WorkSatisfaction;
                HasWork=data.HasWork;Hireable=data.Hireable;Wage=data.Wage;BaseWorkingHours=data.BaseWorkingHours;
                StartHour=hours.x;EndHour=hours.y;Tasks=(int)data.Tasks;LastPaidWage=data.LastPaidWage;WorkQuit=data.WorkQuit;
                Happiness=(int)happiness.Happiness;WageInfluence=happiness.WageInfluence;
                SkillInfluence=happiness.SkillInfluence;HappinessValue=happiness.Value;
            }
            internal bool Matches(BusinessPersonState row) => row!=null && PersonId==row.PersonId && VenueId==row.VenueId &&
                HasWork==row.HasWork && Hireable==row.Hireable && Wage==row.Wage && BaseWorkingHours==row.BaseWorkingHours &&
                StartHour==row.StartHour && EndHour==row.EndHour && Tasks==row.Tasks && LastPaidWage==row.LastPaidWage &&
                WorkQuit==row.WorkQuit && Happiness==row.Happiness && WageInfluence==row.WageInfluence &&
                SkillInfluence==row.SkillInfluence && HappinessValue==row.HappinessValue;
            internal BusinessPersonState ToState() => new BusinessPersonState {PersonId=PersonId,VenueId=VenueId,HasWork=HasWork,
                Hireable=Hireable,Wage=Wage,BaseWorkingHours=BaseWorkingHours,StartHour=StartHour,EndHour=EndHour,Tasks=Tasks,
                LastPaidWage=LastPaidWage,WorkQuit=WorkQuit,Happiness=Happiness,WageInfluence=WageInfluence,
                SkillInfluence=SkillInfluence,HappinessValue=HappinessValue};
        }
        public static SharedItemStack[] CaptureContainer(ItemContainer container) => CaptureContainerState(container).Items;
        public static string ContentHash(SharedItemStack[] items) => SharedEconomyReplication.ContentHash(new SharedEconomyState {Inventory=items});
        public static BusinessContainerState CaptureContainerState(ItemContainer container) => CaptureContainerState(container,null);
        private static BusinessContainerState CaptureContainerState(ItemContainer container,SharedItemStack[] expectedItems)
        {
            if(container == null) throw new InvalidOperationException("Venue inventory is unavailable.");
            var restriction=container.Restriction;
            // Same verified main-thread native reader as the economy mirror;
            // retain business-specific restrictions and flags separately.
            var rows=expectedItems!=null&&SharedEconomyReplication.TryMatchNativeItems(container,expectedItems,out _)
                ? expectedItems : SharedEconomyReplication.CaptureNativeItems(container);
            var maxItems=restriction.MaxItems;var maxSlots=restriction.MaxSlots;var tag=restriction.TagFilter;
            return new BusinessContainerState {Manual=container.IsManuallyUpdated,Refrigerated=container.IsRefridgerated,
                MaxItems=maxItems != null && maxItems.HasValue ? maxItems.Value : (int?)null,
                MaxSlots=maxSlots != null && maxSlots.HasValue ? maxSlots.Value : (int?)null,
                TagFilter=tag == null ? "" : tag.Guid,AcceptsIngredients=restriction.AcceptsIngredients,
                Items=rows};
        }
        public static void Apply(BusinessState state)
            => ApplyProfiled(state,new ApplyProfile {Started=Stopwatch.GetTimestamp()});
        private static void ApplyProfiled(BusinessState state,ApplyProfile profile)
        {
            try {ApplyState(state,profile);}
            catch {profile.Failed=true;throw;}
            finally {ReportProfile(profile);}
        }
        private static void ApplyState(BusinessState state,ApplyProfile profile)
        {
            if(state == null || state.Sequence<=0 || state.Venues == null || state.Venues.Length>MaxVenues ||
               state.People == null || state.People.Length>MaxPeople) throw new ArgumentException("Invalid business state.");
            if(state.Sequence<=applied) return;
            profile.Sequence=state.Sequence;
            profile.InspectDifferences=Stopwatch.GetTimestamp()>=nextProfileReport;
            long stage=Stopwatch.GetTimestamp();
            if(!profile.WireValidated) {
                ValidateWire(state);
                profile.Validation=Stopwatch.GetTimestamp()-stage;
            }
            profile.Venues=state.Venues.Length;profile.People=state.People.Length;
            var manager=Ready(); var updates=new List<(Action Apply,Action Undo)>(); var venueIds=new HashSet<string>(); var personIds=new HashSet<string>();
            long phaseStarted=Stopwatch.GetTimestamp(),phaseAllocated=GC.GetAllocatedBytesForCurrentThread();
            try {
            foreach(var row in state.Venues) {
                if(row==null || !DedicatedProtocol.Text(row.GhostId,256) || !DedicatedProtocol.Text(row.VenueId,128) ||
                    !venueIds.Add(row.GhostId) || row.Menu == null || row.Menu.Length>1024 || row.Staff == null || row.Staff.Length>512 ||
                    row.Profits == null || row.Profits.Length>100000 || row.Ownership<0 || row.Ownership>2)
                    throw new ArgumentException("Invalid venue state.");
                stage=Stopwatch.GetTimestamp();
                var ghost=manager.GetGhostById(row.GhostId)?.TryCast<VenueAreaGhost>();
                if(ghost==null || ghost.Venue==null || ghost.Venue.Guid!=row.VenueId) throw new InvalidOperationException("Venue is missing from the imported native world.");
                var observed=CaptureVenue(ghost,row);
                profile.Observation+=Stopwatch.GetTimestamp()-stage;
                PrepareVenue(ghost,row,observed,updates,profile);
            }
            } finally {profile.VenuePhase=Stopwatch.GetTimestamp()-phaseStarted;profile.VenueAllocated=GC.GetAllocatedBytesForCurrentThread()-phaseAllocated;}
            phaseStarted=Stopwatch.GetTimestamp();phaseAllocated=GC.GetAllocatedBytesForCurrentThread();
            try {
            foreach(var row in state.People) {
                if(row==null || !DedicatedProtocol.Text(row.PersonId,128) || !personIds.Add(row.PersonId) ||
                    !float.IsFinite(row.StartHour) || !float.IsFinite(row.EndHour) || !float.IsFinite(row.LastPaidWage) ||
                    !float.IsFinite(row.WorkQuit) || !float.IsFinite(row.HappinessValue) || (row.VenueId?.Length ?? 999)>128)
                    throw new ArgumentException("Invalid staff state.");
                StagePerson(row,updates,profile);
            }
            } finally {profile.PeoplePhase=Stopwatch.GetTimestamp()-phaseStarted;profile.PeopleAllocated=GC.GetAllocatedBytesForCurrentThread()-phaseAllocated;}
            // No incoming-hash cache: every target was observed above in this
            // Apply. Local drift therefore reaches the ordinary mutation path.
            profile.NoOp=updates.Count==0;
            if(profile.NoOp){applied=state.Sequence;return;}
            int next=0; IsApplying=true;
            try {
                stage=Stopwatch.GetTimestamp();
                for(;next<updates.Count;next++) updates[next].Apply();
                profile.Mutation=Stopwatch.GetTimestamp()-stage;
                ObserveDifferencePhase(profile,false);
                stage=Stopwatch.GetTimestamp();
                VerifyChangedInventories(profile);
                profile.Verify=Stopwatch.GetTimestamp()-stage;
                applied=state.Sequence;
                // Presentation failures must never roll back a valid authoritative snapshot.
                stage=Stopwatch.GetTimestamp();
                try {RefreshUi?.Invoke();LastRefreshError="";}
                catch(Exception ex) {
                    LastRefreshError=ex.ToString();
                    try {Diagnostic?.Invoke("Business state applied, but its UI refresh failed: "+ex.Message);} catch {}
                }
                finally {profile.Ui=Stopwatch.GetTimestamp()-stage;}
                ObserveDifferencePhase(profile,true);
            }
            catch {
                stage=Stopwatch.GetTimestamp();
                try {for(int i=Math.Min(next,updates.Count-1);i>=0;i--) updates[i].Undo();}
                finally {profile.Rollback=Stopwatch.GetTimestamp()-stage;}
                throw;
            }
            finally {IsApplying=false;}
        }
        private static void StagePerson(BusinessPersonState row,List<(Action Apply,Action Undo)> updates,ApplyProfile profile)
        {
            long stage=Stopwatch.GetTimestamp();
            var person=ResolvePerson(row.PersonId,out var data);
            // Preserve validation of the requested native workplace even when
            // all observed scalar values already match the incoming record.
            var work=string.IsNullOrEmpty(row.VenueId)?null:ResolveVenue(row.VenueId);
            var observed=new PersonObservation(row.PersonId,data);
            profile.Observation+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();
            bool same=observed.Matches(row);
            profile.Comparison+=Stopwatch.GetTimestamp()-stage;
            if(same)return;
            profile.ChangedPeople++;
            AddPersonUpdate(person,data,row,work,observed,updates);
        }
        // The client mirror may advance by independent units while later world
        // messages and player intents remain fenced. A late failure discards that
        // isolated client world; the server transaction is never split this way.
        private sealed class BusinessClientCursor
        {
            private readonly BusinessState state;
            private readonly ApplyProfile profile;
            private readonly int generation;
            private readonly Dictionary<IntPtr,BusinessContainerState> aliases=new();
            private int venueIndex,personIndex;
            private IntPtr managerIdentity;
            private bool published;
            internal BusinessClientCursor(BusinessState state,ApplyProfile profile,int generation)
            {this.state=state;this.profile=profile;this.generation=generation;profile.CheckClientContext=CheckCurrent;}
            internal bool Advance(ClientMutationBudget budget)
            {
                CheckCurrent();
                while(budget.TryStart(Stopwatch.GetTimestamp())){
                    CheckCurrent();
                    var manager=Ready();var identity=manager.Pointer;
                    if(managerIdentity==IntPtr.Zero)managerIdentity=identity;
                    else if(managerIdentity!=identity)throw new InvalidOperationException("Business world identity changed between client units.");
                    profile.InventoryChecks.Clear();profile.ChangedInventoryPointers.Clear();
                    if(venueIndex<state.Venues.Length){
                        long started=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
                        try{ApplyClientVenueUnit(manager,state.Venues[venueIndex],profile,aliases);venueIndex++;}
                        finally{profile.VenuePhase+=Stopwatch.GetTimestamp()-started;profile.VenueAllocated+=GC.GetAllocatedBytesForCurrentThread()-allocated;}
                    }
                    else if(personIndex<state.People.Length){
                        // Tiny staff records share one bounded group. One action
                        // per person would hold a 1,589-person roster for 50 frames.
                        long started=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
                        int end=Math.Min(state.People.Length,personIndex+32);
                        try{
                            ApplyClientPeopleUnit(state.People,personIndex,end,profile);personIndex=end;
                        }
                        finally{profile.PeoplePhase+=Stopwatch.GetTimestamp()-started;profile.PeopleAllocated+=GC.GetAllocatedBytesForCurrentThread()-allocated;}
                    }
                    else{
                        profile.NoOp=profile.ChangedVenues==0&&profile.ChangedPeople==0&&profile.ChangedContainers==0;
                        if(!profile.NoOp){
                            long started=Stopwatch.GetTimestamp();IsApplying=true;
                            try{RefreshUi?.Invoke();LastRefreshError="";}
                            catch(Exception ex){LastRefreshError=ex.ToString();try{Diagnostic?.Invoke("Business state applied, but its UI refresh failed: "+ex.Message);}catch{}}
                            finally{profile.Ui+=Stopwatch.GetTimestamp()-started;IsApplying=false;}
                        }
                        CheckCurrent();applied=state.Sequence;published=true;return true;
                    }
                    CheckCurrent();
                }
                return false;
            }
            private void CheckCurrent()
            {
                if(published||generation!=preparationGeneration||state.Sequence<=applied)
                    throw new InvalidOperationException("Discarded business client cursor cannot advance.");
                if(managerIdentity!=IntPtr.Zero&&Ready().Pointer!=managerIdentity)
                    throw new InvalidOperationException("Business world identity changed during a client unit.");
            }
        }
        private static void RememberClientAlias(Dictionary<IntPtr,BusinessContainerState> aliases,ItemContainer target,BusinessContainerState expected)
        {
            if(target==null)throw new InvalidOperationException("Venue inventory is unavailable.");
            IntPtr pointer=target.Pointer;
            if(aliases.TryGetValue(pointer,out var previous)){
                if(!SameContainer(previous,expected))throw new InvalidOperationException("Aliased venue inventories have conflicting authoritative state.");
            }
            else aliases.Add(pointer,expected);
        }
        private static void ApplyClientVenueUnit(GhostManager manager,BusinessVenueState row,ApplyProfile profile,Dictionary<IntPtr,BusinessContainerState> aliases)
        {
            long started=Stopwatch.GetTimestamp();
            var ghost=manager.GetGhostById(row.GhostId)?.TryCast<VenueAreaGhost>();
            if(ghost==null||ghost.Venue==null||ghost.Venue.Guid!=row.VenueId)
                throw new InvalidOperationException("Venue is missing from the imported native world.");
            var observed=CaptureVenue(ghost,row);
            profile.Observation+=Stopwatch.GetTimestamp()-started;
            // Expectations persist across units so a later alias cannot silently
            // overwrite an inventory already mirrored for another venue.
            RememberClientAlias(aliases,ghost.FridgeInventory,row.Fridge);
            RememberClientAlias(aliases,ghost.CupboardInventory,row.Cupboard);
            RememberClientAlias(aliases,ghost.FurnitureInventory,row.Furniture);
            var updates=new List<(Action Apply,Action Undo)>();
            PrepareVenue(ghost,row,observed,updates,profile);
            CommitClientUnit(updates,profile);
        }
        private static void ApplyClientPeopleUnit(BusinessPersonState[] people,int start,int end,ApplyProfile profile)
        {
            var updates=new List<(Action Apply,Action Undo)>();
            var changed=new List<BusinessPersonState>();
            for(int i=start;i<end;i++){
                int before=updates.Count;StagePerson(people[i],updates,profile);
                if(updates.Count!=before)changed.Add(people[i]);
            }
            if(changed.Count!=0)updates.Add((()=>{
                foreach(var row in changed)VerifyClientPerson(row);
            },()=>{}));
            CommitClientUnit(updates,profile);
        }
        private static void VerifyClientPerson(BusinessPersonState row)
        {
            ResolvePerson(row.PersonId,out var data);
            if(!new PersonObservation(row.PersonId,data).Matches(row))
                throw new InvalidOperationException("Native staff state differs after client apply.");
        }
        private static void CommitClientUnit(List<(Action Apply,Action Undo)> updates,ApplyProfile profile)
        {
            if(updates.Count==0)return;
            int next=0;long started=Stopwatch.GetTimestamp();IsApplying=true;
            try{
                for(;next<updates.Count;next++){
                    profile.CheckClientContext?.Invoke();updates[next].Apply();profile.CheckClientContext?.Invoke();
                }
                profile.Mutation+=Stopwatch.GetTimestamp()-started;started=Stopwatch.GetTimestamp();
                VerifyChangedInventories(profile);profile.CheckClientContext?.Invoke();profile.Verify+=Stopwatch.GetTimestamp()-started;
            }
            catch{
                started=Stopwatch.GetTimestamp();
                // A reset discards this entire client world. Never run an old
                // rollback delegate after a native callback replaces its world.
                try{for(int i=Math.Min(next,updates.Count-1);i>=0;i--){profile.CheckClientContext?.Invoke();updates[i].Undo();}}
                finally{profile.Rollback+=Stopwatch.GetTimestamp()-started;}
                throw;
            }
            finally{IsApplying=false;}
        }
        private static void AddPersonUpdate(Person person,RuntimePersonData data,BusinessPersonState row,Venue work,
            PersonObservation observed,List<(Action Apply,Action Undo)> updates)
        {
            // Keep closures in the changed-only helper: a captured variable in
            // StagePerson would allocate a display class even before its no-op
            // return, defeating the unchanged-person allocation fast path.
            var previous=observed.ToState();var oldWork=observed.Work;
            updates.Add((()=>{
                // All updates are staged before the atomic raw commit. Refuse
                // a replaced runtime record, and restore the exact observed
                // object during rollback rather than mutating its replacement.
                if(person._RuntimeData_k__BackingField?.Pointer!=data.Pointer)
                    throw new InvalidOperationException("Native staff runtime identity changed during preparation.");
                SetPersonRaw(data,row,work);
            },()=>SetPersonRaw(data,previous,oldWork)));
        }
        private static void PrepareVenue(VenueAreaGhost ghost,BusinessVenueState row,BusinessVenueState observed,List<(Action Apply,Action Undo)> updates,ApplyProfile profile)
        {
            if(ghost._isOpen==null || ghost._menu==null || ghost.staff==null || ghost._finances==null || ghost.JointInventory==null ||
               (ghost.Venue.currentDebt==null)!=(!row.Debt.HasValue))
                throw new InvalidOperationException("Native venue is not initialized.");
            int priorUpdates=updates.Count;
            StageContainer(ghost.FridgeInventory,row.Fridge,observed.Fridge,updates,profile,row.VenueId,"fridge",row.FridgeCapacity,observed.FridgeCapacity);
            StageContainer(ghost.CupboardInventory,row.Cupboard,observed.Cupboard,updates,profile,row.VenueId,"cupboard",row.CupboardCapacity,observed.CupboardCapacity);
            StageContainer(ghost.FurnitureInventory,row.Furniture,observed.Furniture,updates,profile,row.VenueId,"furniture",null,null);
            long stage=Stopwatch.GetTimestamp();
            bool same=SameVenueMetadata(row,observed);
            profile.Comparison+=Stopwatch.GetTimestamp()-stage;
            // Joint capacity references can alias a replaced container's
            // restriction. Preserve the original final capacity write ordering.
            if(same&&updates.Count==priorUpdates)return;
            profile.ChangedVenues++;stage=Stopwatch.GetTimestamp();
            var menu=new Il2CppSystem.Collections.Generic.List<MealMenuItem>();
            foreach(var item in row.Menu) {
                if(item==null || !DedicatedProtocol.Text(item.RecipeId,128) || item.Price<0) throw new ArgumentException("Invalid venue menu.");
                var recipe=ScriptableObjectDatabase.Instance.Provide<MealRecipeDefinition>(item.RecipeId);
                if(recipe==null || recipe.Guid!=item.RecipeId) throw new ArgumentException("Unknown menu recipe.");
                // Raw fields avoid Price setter or menu collection callbacks.
                menu.Add(new MealMenuItem {recipe=recipe,price=item.Price,_recipe=null});
            }
            var staff=new UniqueList<Person>(); var seen=new HashSet<string>();
            foreach(string id in row.Staff) {if(!DedicatedProtocol.Text(id,128)||!seen.Add(id)) throw new ArgumentException("Invalid staff roster.");staff.Add(ResolvePerson(id));}
            var profits=new Il2CppSystem.Collections.Generic.Dictionary<int,int>();
            foreach(var item in row.Profits) {if(item==null||item.Length!=2||profits.ContainsKey(item[0]))throw new ArgumentException("Invalid finance cache.");profits.Add(item[0],item[1]);}
            bool oldOpen=ghost._isOpen._runtimeValue,oldOpenInit=ghost._isOpen._isRuntimeValueInitialized,oldMenuInit=ghost._menu._isRuntimeValueInitialized;
            int oldLevel=ghost.currentLevel,oldMeals=ghost.mealsServed,oldVisits=ghost.totalVisitsCount,oldDebt=ghost.Venue.currentDebt?._runtimeValue??0;
            bool oldVisited=ghost.wasVisitedByPlayer,oldDebtInit=ghost.Venue.currentDebt?._isRuntimeValueInitialized??false;
            var fridgeCapacity=PrepareCapacity(row.FridgeCapacity);var cupboardCapacity=PrepareCapacity(row.CupboardCapacity);
            var fridge=ghost.JointInventory._RefridgeratedInventory_k__BackingField;
            var cupboard=ghost.JointInventory._NormalInventory_k__BackingField;
            var oldFridgeCapacity=CapacityRestriction(fridge).MaxItems;var oldCupboardCapacity=CapacityRestriction(cupboard).MaxItems;
            var oldOwner=ghost.OwnershipType;var oldMenu=ghost._menu._backingList;var oldStaff=ghost.staff;var oldProfits=ghost._finances._profitsPerDay;
            updates.Add((()=>{
                ghost._isOpen._runtimeValue=row.Open;ghost._isOpen._isRuntimeValueInitialized=true;
                ghost.OwnershipType=(OwnershipType)row.Ownership;ghost.currentLevel=row.Level;ghost.mealsServed=row.MealsServed;
                ghost.totalVisitsCount=row.Visits;ghost.wasVisitedByPlayer=row.Visited;
                if(row.Debt.HasValue) {ghost.Venue.currentDebt._runtimeValue=row.Debt.Value;ghost.Venue.currentDebt._isRuntimeValueInitialized=true;}
                CapacityRestriction(fridge).MaxItems=fridgeCapacity;CapacityRestriction(cupboard).MaxItems=cupboardCapacity;
                ghost._menu._backingList=menu;ghost._menu._isRuntimeValueInitialized=true;ghost.staff=staff;ghost._finances._profitsPerDay=profits;
            },()=>{
                ghost._isOpen._runtimeValue=oldOpen;ghost._isOpen._isRuntimeValueInitialized=oldOpenInit;
                ghost.OwnershipType=oldOwner;ghost.currentLevel=oldLevel;ghost.mealsServed=oldMeals;
                ghost.totalVisitsCount=oldVisits;ghost.wasVisitedByPlayer=oldVisited;
                if(row.Debt.HasValue) {ghost.Venue.currentDebt._runtimeValue=oldDebt;ghost.Venue.currentDebt._isRuntimeValueInitialized=oldDebtInit;}
                CapacityRestriction(fridge).MaxItems=oldFridgeCapacity;CapacityRestriction(cupboard).MaxItems=oldCupboardCapacity;
                ghost._menu._backingList=oldMenu;ghost._menu._isRuntimeValueInitialized=oldMenuInit;ghost.staff=oldStaff;ghost._finances._profitsPerDay=oldProfits;
            }));
            profile.Preparation+=Stopwatch.GetTimestamp()-stage;
        }
        // Native VenueInventory capacity accessors only translate this OptionalInt
        // to/from Nullable<int>. Its generated nullable-return wrapper is unsafe:
        // runtime_invoke boxes no value as null and a present value as Int32, not
        // as a Nullable object. Optional<int>._value's generated generic FIELD
        // accessor also returned pointer-like garbage in native live probes.
        // HasValue and Value invoke the primitive bool/int native methods and
        // are the same safe accessors used by CaptureContainerState. Mirror the
        // native setter's OptionalInt reference replacement on write.
        private static int? NativeCapacity(OptionalInt value)
        {
            if(ReferenceEquals(value,null))throw new InvalidOperationException("Native venue capacity is not initialized.");
            return value.HasValue ? value.Value : (int?)null;
        }
        private static ItemContainerRestriction CapacityRestriction(ItemContainer container)
        {
            var restriction=ReferenceEquals(container,null)?null:container._restriction;
            if(ReferenceEquals(restriction,null) || ReferenceEquals(restriction.MaxItems,null))
                throw new InvalidOperationException("Native venue capacity restriction is not initialized.");
            return restriction;
        }
        private static OptionalInt PrepareCapacity(int? value)
        {
            // Native setters preserve the complete Int32 domain, including
            // negative values emitted by authoritative joint inventories.
            return new OptionalInt(value.GetValueOrDefault(),value.HasValue);
        }
        private static void SetPersonRaw(RuntimePersonData person,BusinessPersonState row,Venue work)
        {
            person._worksAt=work;person.HasWork=row.HasWork;person.Hireable=row.Hireable;person.Wage=row.Wage;
            person.BaseWorkingHours=row.BaseWorkingHours;person.WorkingHours=new Vector2(row.StartHour,row.EndHour);
            person.Tasks=(VenueTasks)row.Tasks;person.LastPaidWage=row.LastPaidWage;person.WorkQuit=row.WorkQuit;
            person.WorkSatisfaction=new HappinessBreakdown {Happiness=(Happiness)row.Happiness,WageInfluence=row.WageInfluence,
                SkillInfluence=row.SkillInfluence,Value=row.HappinessValue};
        }
        private static void StageContainer(ItemContainer target,BusinessContainerState row,BusinessContainerState observed,List<(Action Apply,Action Undo)> updates,ApplyProfile profile,string venueId,string kind,int? jointDesired,int? jointObserved)
        {
            long stage=Stopwatch.GetTimestamp();
            profile.InventoryChecks.Add((row,target));
            profile.Containers++;
            foreach(var stack in row.Items)profile.Items+=stack.Items.Length;
            bool same=SameContainer(row,observed);
            profile.Comparison+=Stopwatch.GetTimestamp()-stage;
            if(same)return;
            profile.ChangedInventoryPointers.Add(target.Pointer);
            RecordDifference(profile,target,row,observed,venueId,kind,jointDesired,jointObserved);
            profile.ChangedContainers++;stage=Stopwatch.GetTimestamp();
            var prepared=PrepareContainer(row);
            var oldItems=target._items;var oldMap=target._typeMap;var oldRestriction=target._restriction;
            bool oldManual=target._IsManuallyUpdated_k__BackingField,oldRefrigerated=target._IsRefridgerated_k__BackingField;
            updates.Add((()=>{
                target._items=prepared._items;target._typeMap=prepared._typeMap;target._restriction=prepared._restriction;
                target._IsManuallyUpdated_k__BackingField=row.Manual;target._IsRefridgerated_k__BackingField=row.Refrigerated;
            },()=>{
                target._items=oldItems;target._typeMap=oldMap;target._restriction=oldRestriction;
                target._IsManuallyUpdated_k__BackingField=oldManual;target._IsRefridgerated_k__BackingField=oldRefrigerated;
            }));
            profile.Preparation+=Stopwatch.GetTimestamp()-stage;
        }
        private static void VerifyChangedInventories(ApplyProfile profile)
        {
            // Every container was freshly compared before this synchronous raw
            // commit. Only these pointers had their item lists changed; metadata
            // writes and capacity updates emit no inventory callbacks. Re-read
            // changed inventories, including ALL row aliases of each pointer, so
            // conflicting alias expectations still fail and roll back the commit.
            foreach(var check in profile.InventoryChecks)
                if(profile.ChangedInventoryPointers.Contains(check.Target.Pointer))
                    AssertInventory(check.Expected,check.Target);
        }
        /// <summary>Construct a detached inventory for validation or rollback, with the template's exact restrictions.</summary>
        public static ItemContainer PrepareContainer(SharedItemStack[] items,ItemContainer template)
        {
            var state=CaptureContainerState(template);state.Items=items;
            return PrepareContainer(state);
        }
        /// <summary>Swap inventory data without emitting native gameplay callbacks; retains the target's subscribed event object.</summary>
        public static void ReplaceContainerRaw(ItemContainer target,ItemContainer prepared)
        {
            if(target==null || prepared==null)throw new ArgumentNullException("Inventory is unavailable.");
            target._items=prepared._items;target._typeMap=prepared._typeMap;target._restriction=prepared._restriction;
            target._IsManuallyUpdated_k__BackingField=prepared._IsManuallyUpdated_k__BackingField;
            target._IsRefridgerated_k__BackingField=prepared._IsRefridgerated_k__BackingField;
        }
        private static ItemContainer PrepareContainer(BusinessContainerState row)
        {
            if(row==null || row.Items==null || row.Items.Length>16384 || row.MaxItems<0 || row.MaxSlots<0 ||
                (row.TagFilter?.Length ?? 999)>128) throw new ArgumentException("Invalid venue container.");
            var restriction=new ItemContainerRestriction {MaxItems=new OptionalInt(row.MaxItems??0,row.MaxItems.HasValue),
                MaxSlots=new OptionalInt(row.MaxSlots??0,row.MaxSlots.HasValue),AcceptsIngredients=row.AcceptsIngredients};
            if(!string.IsNullOrEmpty(row.TagFilter)) {
                restriction.TagFilter=ScriptableObjectDatabase.Instance.Provide<ObjectTag>(row.TagFilter);
                if(restriction.TagFilter==null || restriction.TagFilter.Guid!=row.TagFilter)throw new ArgumentException("Unknown inventory filter.");
            }
            var prepared=new ItemContainer(row.Manual,row.Refrigerated,restriction);int count=0;
            foreach(var stack in row.Items) {
                if(stack==null||!DedicatedProtocol.Text(stack.Type,128)||stack.Items==null||stack.Items.Length>200000-count)
                    throw new ArgumentException("Invalid venue item stack.");
                count+=stack.Items.Length;if(stack.Items.Length==0)continue;
                var type=ScriptableObjectDatabase.Instance.Provide<ItemType>(stack.Type);
                if(type==null || type.Guid!=stack.Type)throw new ArgumentException("Unknown venue item.");
                var native=ItemStack.SafeCreateEmpty(type,stack.Items.Length);
                foreach(var values in stack.Items) {
                    if(values==null||values.Length!=4)throw new ArgumentException("Invalid venue item metadata.");
                    native.AddInstance(new ItemInstanceData {type=type,value=values[0],creationDay=values[1],remainingDecayTime=values[2],lastUpdateDay=values[3]});
                }
                native._lastUpdateTime=stack.LastUpdate;
                if(prepared.TryAdd(native)!=ItemCollectionOperationResult.Succeeded)throw new InvalidOperationException("Venue inventory exceeds its restriction.");
            }
            return prepared;
        }
        private static void AssertInventory(BusinessContainerState expected,ItemContainer actual)
        {
            string desired=ContentHash(expected.Items);
            string observed=ContentHash(CaptureContainer(actual));
            if(desired!=observed)throw new InvalidOperationException("Venue inventory metadata differs after apply.");
        }
        // Pure validation/comparison boundaries: safe for detached tests and
        // future managed preparation, but every native observation stays above.
        internal static void ValidateWire(BusinessState state)
        {
            if(state==null||state.Sequence<=0||state.Venues==null||state.Venues.Length>MaxVenues||
                state.People==null||state.People.Length>MaxPeople)throw new ArgumentException("Invalid business state.");
            var venues=new HashSet<string>(StringComparer.Ordinal);var people=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in state.Venues){
                if(row==null||!DedicatedProtocol.Text(row.GhostId,256)||!DedicatedProtocol.Text(row.VenueId,128)||
                    !venues.Add(row.GhostId)||row.Menu==null||row.Menu.Length>1024||row.Staff==null||row.Staff.Length>512||
                    row.Profits==null||row.Profits.Length>100000||row.Ownership<0||row.Ownership>2)throw new ArgumentException("Invalid venue state.");
                foreach(var item in row.Menu)if(item==null||!DedicatedProtocol.Text(item.RecipeId,128)||item.Price<0)throw new ArgumentException("Invalid venue menu.");
                var staff=new HashSet<string>(StringComparer.Ordinal);
                foreach(string id in row.Staff)if(!DedicatedProtocol.Text(id,128)||!staff.Add(id))throw new ArgumentException("Invalid staff roster.");
                var profits=new HashSet<int>();
                foreach(var item in row.Profits)if(item==null||item.Length!=2||!profits.Add(item[0]))throw new ArgumentException("Invalid finance cache.");
                ValidateContainerWire(row.Fridge);ValidateContainerWire(row.Cupboard);ValidateContainerWire(row.Furniture);
            }
            foreach(var row in state.People)if(row==null||!DedicatedProtocol.Text(row.PersonId,128)||!people.Add(row.PersonId)||
                !float.IsFinite(row.StartHour)||!float.IsFinite(row.EndHour)||!float.IsFinite(row.LastPaidWage)||
                !float.IsFinite(row.WorkQuit)||!float.IsFinite(row.HappinessValue)||(row.VenueId?.Length??999)>128)
                throw new ArgumentException("Invalid staff state.");
        }
        private static void ValidateContainerWire(BusinessContainerState row)
        {
            if(row==null||row.Items==null||row.Items.Length>16384||row.MaxItems<0||row.MaxSlots<0||
                (row.TagFilter?.Length??999)>128)throw new ArgumentException("Invalid venue container.");
            int count=0;
            foreach(var stack in row.Items){
                if(stack==null||!DedicatedProtocol.Text(stack.Type,128)||stack.Items==null||stack.Items.Length>200000-count)
                    throw new ArgumentException("Invalid venue item stack.");
                count+=stack.Items.Length;
                foreach(var item in stack.Items)if(item==null||item.Length!=4)throw new ArgumentException("Invalid venue item metadata.");
            }
        }
        internal static bool SameContainer(BusinessContainerState desired,BusinessContainerState observed)
        {
            return desired!=null&&observed!=null&&desired.Manual==observed.Manual&&desired.Refrigerated==observed.Refrigerated&&
                desired.MaxItems==observed.MaxItems&&desired.MaxSlots==observed.MaxSlots&&desired.TagFilter==observed.TagFilter&&
                desired.AcceptsIngredients==observed.AcceptsIngredients&&
                (ReferenceEquals(desired.Items,observed.Items)||ContentHash(desired.Items)==ContentHash(observed.Items));
        }
        // Diagnostic comparison only. The ordinary drift check above remains
        // unchanged, including a fresh native observation on every Apply.
        internal static int ContainerMetadataDifferences(BusinessContainerState desired,BusinessContainerState observed)
        {
            if(desired==null||observed==null)throw new ArgumentNullException("Container diagnostic input");
            return (desired.Manual!=observed.Manual?1:0)|(desired.Refrigerated!=observed.Refrigerated?2:0)|
                (desired.MaxItems!=observed.MaxItems?4:0)|(desired.MaxSlots!=observed.MaxSlots?8:0)|
                (desired.TagFilter!=observed.TagFilter?16:0)|(desired.AcceptsIngredients!=observed.AcceptsIngredients?32:0);
        }
        private static string OptionalText(int? value)=>value.HasValue?value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture):"absent";
        private static string ContainerMetadataText(BusinessContainerState row)=>
            "manual="+row.Manual+",cold="+row.Refrigerated+",maxItems="+OptionalText(row.MaxItems)+
            ",maxSlots="+OptionalText(row.MaxSlots)+",tag="+row.TagFilter+",ingredients="+row.AcceptsIngredients;
        private static string NativeProbeText(ItemContainer target)
        {
            var restriction=target._restriction;
            var maxItems=restriction.MaxItems;var maxSlots=restriction.MaxSlots;
            return "container="+target.Pointer.ToInt64().ToString("X")+",restriction="+restriction.Pointer.ToInt64().ToString("X")+
                ",manual="+target._IsManuallyUpdated_k__BackingField+",cold="+target._IsRefridgerated_k__BackingField+
                ",itemsCapacity="+OptionalText(NativeCapacity(maxItems))+",slotsCapacity="+OptionalText(NativeCapacity(maxSlots))+
                ",tag="+(restriction.TagFilter==null?"":restriction.TagFilter.Guid)+",ingredients="+restriction.AcceptsIngredients;
        }
        private static void RecordDifference(ApplyProfile profile,ItemContainer target,BusinessContainerState desired,BusinessContainerState observed,string venueId,string kind,int? jointDesired,int? jointObserved)
        {
            if(!profile.InspectDifferences)return;
            long start=Stopwatch.GetTimestamp();
            try {
                int bits=ContainerMetadataDifferences(desired,observed);
                for(int i=0;i<profile.DifferenceCounts.Length;i++)if((bits&(1<<i))!=0)profile.DifferenceCounts[i]++;
                if(bits==0)profile.ItemOnlyDifferences++;
                // One example per container role, at most three, every five
                // seconds. No item contents or unbounded identifiers are logged.
                if(profile.Probes.Count>=3||profile.Probes.Exists(p=>p.Kind==kind))return;
                bool itemsEqual=ContentHash(desired.Items)==ContentHash(observed.Items);
                profile.Probes.Add(new ContainerProbe {Kind=kind,Label=venueId+"/"+kind,Target=target,
                    Before="desired{"+ContainerMetadataText(desired)+"} observed{"+ContainerMetadataText(observed)+
                        "} jointDesired="+OptionalText(jointDesired)+" jointObserved="+OptionalText(jointObserved)+
                        " itemsEqual="+itemsEqual+" native{"+NativeProbeText(target)+"}"});
            } catch(Exception ex) {
                if(profile.Probes.Count<3&&!profile.Probes.Exists(p=>p.Kind==kind))
                    profile.Probes.Add(new ContainerProbe {Kind=kind,Label=venueId+"/"+kind,Target=target,Before="unavailable:"+ex.GetType().Name});
            }
            finally {profile.DifferenceCost+=Stopwatch.GetTimestamp()-start;}
        }
        private static void ObserveDifferencePhase(ApplyProfile profile,bool afterUi)
        {
            if(!profile.InspectDifferences)return;
            long start=Stopwatch.GetTimestamp();
            foreach(var probe in profile.Probes) {
                string text;
                try {text=NativeProbeText(probe.Target);}catch(Exception ex){text="unavailable:"+ex.GetType().Name;}
                if(afterUi)probe.AfterUi=text;else probe.AfterWrite=text;
            }
            profile.DifferenceCost+=Stopwatch.GetTimestamp()-start;
        }
        internal static bool SamePerson(BusinessPersonState a,BusinessPersonState b)
        {
            return a!=null&&b!=null&&a.PersonId==b.PersonId&&a.VenueId==b.VenueId&&a.HasWork==b.HasWork&&a.Hireable==b.Hireable&&
                a.Wage==b.Wage&&a.BaseWorkingHours==b.BaseWorkingHours&&a.StartHour==b.StartHour&&a.EndHour==b.EndHour&&
                a.Tasks==b.Tasks&&a.LastPaidWage==b.LastPaidWage&&a.WorkQuit==b.WorkQuit&&a.Happiness==b.Happiness&&
                a.WageInfluence==b.WageInfluence&&a.SkillInfluence==b.SkillInfluence&&a.HappinessValue==b.HappinessValue;
        }
        internal static bool SameVenueMetadata(BusinessVenueState a,BusinessVenueState b)
        {
            if(a==null||b==null||a.GhostId!=b.GhostId||a.VenueId!=b.VenueId||a.Open!=b.Open||a.Ownership!=b.Ownership||
                a.Level!=b.Level||a.MealsServed!=b.MealsServed||a.Visits!=b.Visits||a.Visited!=b.Visited||a.Debt!=b.Debt||
                a.FridgeCapacity!=b.FridgeCapacity||a.CupboardCapacity!=b.CupboardCapacity||
                a.Menu==null||b.Menu==null||a.Menu.Length!=b.Menu.Length||a.Staff==null||b.Staff==null||
                a.Staff.Length!=b.Staff.Length||a.Profits==null||b.Profits==null||a.Profits.Length!=b.Profits.Length)return false;
            // Menu order is user-visible. Capture already canonicalizes staff
            // membership and the finance dictionary, whose order is immaterial.
            for(int i=0;i<a.Menu.Length;i++)if(a.Menu[i]==null||b.Menu[i]==null||a.Menu[i].RecipeId!=b.Menu[i].RecipeId||a.Menu[i].Price!=b.Menu[i].Price)return false;
            var staff=new HashSet<string>(a.Staff,StringComparer.Ordinal);
            if(staff.Count!=a.Staff.Length||!staff.SetEquals(b.Staff))return false;
            var profits=new Dictionary<int,int>();
            foreach(var row in a.Profits)if(row==null||row.Length!=2||!profits.TryAdd(row[0],row[1]))return false;
            var seen=new HashSet<int>();
            foreach(var row in b.Profits)if(row==null||row.Length!=2||!seen.Add(row[0])||!profits.TryGetValue(row[0],out int value)||value!=row[1])return false;
            return true;
        }
        private static void ReportProfile(ApplyProfile profile)
        {
            long now=Stopwatch.GetTimestamp();
            if(!profile.Failed && now<nextProfileReport)return;
            nextProfileReport=now+5*Stopwatch.Frequency;
            string Ms(long value)=>(value*1000d/Stopwatch.Frequency).ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
            long mainThread=profile.Incremental?profile.IncrementalMainThread:now-profile.Started;
            try {Diagnostic?.Invoke("BUSINESS_PROFILE venues="+profile.Venues+" people="+profile.People+" containers="+profile.Containers+
                " items="+profile.Items+" changedVenues="+profile.ChangedVenues+" changedPeople="+profile.ChangedPeople+
                " changedContainers="+profile.ChangedContainers+" noOp="+profile.NoOp+" failed="+profile.Failed+
                " totalMs="+Ms(mainThread)+" mainThreadMs="+Ms(mainThread)+" slices="+profile.Slices+" decodeJsonMs="+Ms(profile.DecodeJson)+
                " backgroundPrepared="+profile.BackgroundPrepared+" managedPreparationMs="+Ms(profile.ManagedPreparation)+
                " validationMs="+Ms(profile.Validation)+" nativeObservationMs="+Ms(profile.Observation)+
                " venuePhaseMs="+Ms(profile.VenuePhase)+" peoplePhaseMs="+Ms(profile.PeoplePhase)+
                " venueAllocatedBytes="+profile.VenueAllocated+" peopleAllocatedBytes="+profile.PeopleAllocated+
                " comparisonMs="+Ms(profile.Comparison)+" preparationMs="+Ms(profile.Preparation)+" mutationMs="+Ms(profile.Mutation)+
                " verifyMs="+Ms(profile.Verify)+" uiMs="+Ms(profile.Ui)+" rollbackMs="+Ms(profile.Rollback));}catch{}
            if(profile.InspectDifferences&&profile.ChangedContainers>0)try {
                Diagnostic?.Invoke("BUSINESS_DIFF sequence="+profile.Sequence+" manual="+profile.DifferenceCounts[0]+
                    " cold="+profile.DifferenceCounts[1]+" maxItems="+profile.DifferenceCounts[2]+" maxSlots="+profile.DifferenceCounts[3]+
                    " tag="+profile.DifferenceCounts[4]+" ingredients="+profile.DifferenceCounts[5]+" itemOnly="+profile.ItemOnlyDifferences+
                    " diagnosticMs="+Ms(profile.DifferenceCost));
                foreach(var probe in profile.Probes)Diagnostic?.Invoke("BUSINESS_DIFF_SAMPLE "+probe.Label+" before{"+probe.Before+
                    "} afterWrite{"+probe.AfterWrite+"} afterUi{"+probe.AfterUi+"}");
            }catch{}
        }
        private static GhostManager Ready()
        {
            var manager=GhostManager.HasInstance?GhostManager.Instance:null;
            if(manager==null||!manager.IsSystemInitialized)throw new InvalidOperationException("Native business world is not initialized.");
            return manager;
        }
        private static Person ResolvePerson(string id) => ResolvePerson(id,out _);
        private static Person ResolvePerson(string id,out RuntimePersonData data)
        {
            var person=ScriptableObjectDatabase.Instance.Provide<Person>(id);
            data=null;
            if(person==null||person.Guid!=id)throw new ArgumentException("Unknown staff person.");
            // Audited native Person.RuntimeData is exactly its backing-field
            // load (0x643560). Avoid a redundant runtime_invoke for this read.
            data=person._RuntimeData_k__BackingField;
            if(data==null)throw new ArgumentException("Unknown staff person.");
            return person;
        }
        private static Venue ResolveVenue(string id)
        {
            var venue=ScriptableObjectDatabase.Instance.Provide<Venue>(id);
            if(venue==null||venue.Guid!=id)throw new ArgumentException("Unknown staff workplace.");
            return venue;
        }
        public static WorldEvent[] CaptureEvents(BusinessState state)
        {
            if(state==null || state.Sequence<=0 || state.Venues==null || state.Venues.Length>MaxVenues || state.People==null || state.People.Length>MaxPeople)
                throw new ArgumentException("Invalid business state.");
            var encoded=BoundedSnapshotCodec.EncodeJson(state,MaxBytes);var bytes=encoded.Bytes;
            string hash=encoded.Hash;LastCaptureHash=hash;
            var events=new List<WorldEvent>();
            for(int offset=0;offset<bytes.Length;offset+=ChunkBytes) {
                var part=new byte[Math.Min(ChunkBytes,bytes.Length-offset)];Buffer.BlockCopy(bytes,offset,part,0,part.Length);
                var evt=new WorldEvent {Kind="business-state-chunk",Data=JsonSerializer.SerializeToElement(new BusinessStateChunk {
                    Sequence=state.Sequence,Hash=hash,Encoding=encoded.Encoding,RawLength=encoded.RawLength,
                    Length=bytes.Length,Offset=offset,Bytes=part},DedicatedProtocol.Json)};
                if(CanonicalJson.ByteCount(evt)>48000)throw new InvalidOperationException("Business event is too large.");
                events.Add(evt);
            }
            return events.ToArray();
        }
        // A completed buffer is transferred from the main-thread assembler;
        // preparation never reads its mutable fields or any native object.
        internal sealed class EncodedBusinessUpdate
        {
            internal readonly byte[] Bytes;
            internal readonly string Encoding,Hash;
            internal readonly int RawLength,Generation;
            internal readonly long Sequence;
            internal EncodedBusinessUpdate(byte[] bytes,string encoding,string hash,int rawLength,int generation,long sequence)
            {Bytes=bytes;Encoding=encoding;Hash=hash;RawLength=rawLength;Generation=generation;Sequence=sequence;}
        }
        internal sealed class PreparedBusinessUpdate:IIncrementalPreparedWorldUpdate
        {
            private readonly BusinessState state;
            private readonly string hash;
            private readonly int generation;
            private readonly long decode,validation,preparation;
            private readonly bool background;
            private bool consumed;
            private BusinessClientCursor cursor;
            private ApplyProfile incrementalProfile;
            internal PreparedBusinessUpdate(BusinessState state,string hash,int generation,long decode,long validation,long preparation,bool background)
            {this.state=state;this.hash=hash;this.generation=generation;this.decode=decode;this.validation=validation;this.preparation=preparation;this.background=background;}
            public void Commit()
            {
                if(consumed||cursor!=null||generation!=preparationGeneration||state.Sequence<=applied)
                    throw new InvalidOperationException("Discarded business preparation cannot be applied.");
                consumed=true; // A failed native transaction is never retried in place.
                var profile=new ApplyProfile {Started=Stopwatch.GetTimestamp(),DecodeJson=decode,Validation=validation,
                    ManagedPreparation=preparation,BackgroundPrepared=background,WireValidated=true};
                ApplyProfiled(state,profile);
                if(generation!=preparationGeneration)
                    throw new InvalidOperationException("Business world changed during native commit.");
                LastCaptureHash=hash;
            }
            public bool Advance(ClientMutationBudget budget)
            {
                if(budget==null)throw new ArgumentNullException(nameof(budget));
                if(consumed||generation!=preparationGeneration||state.Sequence<=applied)
                    throw new InvalidOperationException("Discarded business preparation cannot advance.");
                if(!budget.CanStart(Stopwatch.GetTimestamp()))return false;
                if(cursor==null){
                    incrementalProfile=new ApplyProfile {Started=Stopwatch.GetTimestamp(),Sequence=state.Sequence,DecodeJson=decode,Validation=validation,
                        ManagedPreparation=preparation,BackgroundPrepared=background,WireValidated=true,Incremental=true,
                        Venues=state.Venues.Length,People=state.People.Length,InspectDifferences=Stopwatch.GetTimestamp()>=nextProfileReport};
                    cursor=new BusinessClientCursor(state,incrementalProfile,generation);
                }
                var profile=incrementalProfile;long started=Stopwatch.GetTimestamp();bool complete=false;
                try{
                    complete=cursor.Advance(budget);
                    if(generation!=preparationGeneration)throw new InvalidOperationException("Business world changed during client update.");
                    if(complete){consumed=true;LastCaptureHash=hash;cursor=null;incrementalProfile=null;}
                    return complete;
                }
                catch{consumed=true;profile.Failed=true;cursor=null;incrementalProfile=null;throw;}
                finally{
                    profile.IncrementalMainThread+=Stopwatch.GetTimestamp()-started;profile.Slices++;
                    if(complete||profile.Failed)ReportProfile(profile);
                }
            }
            public void Abort(){consumed=true;cursor=null;incrementalProfile=null;}
        }
        public static void Apply(WorldEvent evt)
        {
            var encoded=AcceptChunk(evt);
            if(encoded!=null)PrepareManaged(encoded,CancellationToken.None,false).Commit();
        }
        internal static EncodedBusinessUpdate AcceptChunk(WorldEvent evt)
        {
            if(evt==null||evt.Kind!="business-state-chunk")return null;
            var part=evt.Data.Deserialize<BusinessStateChunk>(DedicatedProtocol.Json);
            if(part==null||part.Sequence<=0||part.Length<=0||part.Length>MaxBytes||part.Bytes==null||
                part.Bytes.Length==0||part.Bytes.Length>ChunkBytes||part.Offset<0||part.Offset>part.Length-part.Bytes.Length||
                part.Hash==null||part.Hash.Length!=64)throw new ArgumentException("Invalid business chunk.");
            // RawLength was absent in legacy json-v1 snapshots. Compressed
            // payloads always require their independently bounded raw length.
            int rawLength=part.RawLength==0&&part.Encoding==BoundedSnapshotCodec.JsonEncoding?part.Length:part.RawLength;
            BoundedSnapshotCodec.ValidateHeader(part.Encoding,rawLength,part.Length,MaxBytes);
            if(part.Sequence<=applied)return null;
            // The relay can admit a late join between chunks of an older batch.
            // Ignore that tail until a header arrives; never skip within a batch.
            if(incoming==null&&part.Offset!=0)return null;
            if(part.Offset==0) {incoming=new byte[part.Length];incomingOffset=0;incomingSequence=part.Sequence;incomingHash=part.Hash;
                incomingEncoding=part.Encoding;incomingRawLength=rawLength;incomingStarted=Time.realtimeSinceStartup;}
            if(incoming==null||incomingSequence!=part.Sequence||incomingHash!=part.Hash||incoming.Length!=part.Length||
                incomingEncoding!=part.Encoding||incomingRawLength!=rawLength||
                incomingOffset!=part.Offset||Time.realtimeSinceStartup-incomingStarted>30) {incoming=null;throw new InvalidDataException("Incomplete business state; reconnect to refresh.");}
            Buffer.BlockCopy(part.Bytes,0,incoming,incomingOffset,part.Bytes.Length);incomingOffset+=part.Bytes.Length;
            if(incomingOffset!=incoming.Length)return null;
            var bytes=incoming;incoming=null;
            return new EncodedBusinessUpdate(bytes,incomingEncoding,incomingHash,incomingRawLength,preparationGeneration,incomingSequence);
        }
        // Checksum, bounded decode and wire validation are pure managed work.
        // Native existence, identity, fresh observation and rollback stay in Commit.
        internal static PreparedBusinessUpdate PrepareManaged(EncodedBusinessUpdate input,CancellationToken cancellation,bool background=true)
        {
            cancellation.ThrowIfCancellationRequested();
            if(input==null||input.Bytes==null||input.Bytes.Length<1||input.Bytes.Length>MaxBytes||input.Sequence<=0)
                throw new InvalidDataException("Invalid business preparation.");
            long started=Stopwatch.GetTimestamp(),stage=started;
            var state=BoundedSnapshotCodec.DecodeJson<BusinessState>(input.Encoding,input.RawLength,input.Hash,input.Bytes,MaxBytes);
            long decode=Stopwatch.GetTimestamp()-stage;
            cancellation.ThrowIfCancellationRequested();
            if(state==null||state.Sequence!=input.Sequence)throw new InvalidDataException("Business state sequence mismatch.");
            stage=Stopwatch.GetTimestamp();ValidateWire(state);long validation=Stopwatch.GetTimestamp()-stage;
            cancellation.ThrowIfCancellationRequested();
            return new PreparedBusinessUpdate(state,input.Hash,input.Generation,decode,validation,Stopwatch.GetTimestamp()-started,background);
        }
    }
}
