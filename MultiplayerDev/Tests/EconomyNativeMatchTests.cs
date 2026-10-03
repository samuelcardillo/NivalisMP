using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static partial class EconomyNativeMatchProbe
{
    private const int MaxStacks=16384,MaxInstances=200000;
    private sealed class Profile { public int FastMatchedContainers,FastMatchedInstances; }
    private static Profile activeProfile=new Profile();
    public sealed class SharedItemStack { public string Type;public int LastUpdate;public int[][] Items; }
    internal sealed class ItemType { public string Guid;public IntPtr Pointer=new IntPtr(0x4560); }
    internal sealed class ItemContainer { public ItemStack[] Stacks; }
    internal sealed class ItemStack {
        internal IntPtr Raw;internal static int PointerReads;private NativeList list;
        public IntPtr Pointer{get{PointerReads++;return Raw;}}
        public ItemType _type;
        public NativeList _instanceData {get{_=Pointer;return list;}set{list=value;Marshal.WriteIntPtr(Raw,16,value.Raw);}}
        public int _lastUpdateTime {get{_=Pointer;return Marshal.ReadInt32(Raw,24);}set{Marshal.WriteInt32(Raw,24,value);}}
    }
    internal sealed class NativeList {
        internal IntPtr Raw;internal static int PointerReads;private Il2CppArrayBase array;
        public IntPtr Pointer{get{PointerReads++;return Raw;}}
        public int _version {get{_=Pointer;return Marshal.ReadInt32(Raw,28);}set{Marshal.WriteInt32(Raw,28,value);}}
        public int _size {get{_=Pointer;return Marshal.ReadInt32(Raw,24);}set{Marshal.WriteInt32(Raw,24,value);}}
        public Il2CppArrayBase _items {get{_=Pointer;return array;}set{array=value;Marshal.WriteIntPtr(Raw,16,value.Raw);}}
    }
    internal sealed class Il2CppArrayBase {
        internal IntPtr Raw;internal int Size;internal static int PointerReads;
        public int Length {get{_=Pointer;return Size;}}
        public IntPtr Start=>IntPtr.Add(Pointer,32);
        public IntPtr Pointer{get{PointerReads++;return Raw;}}
    }
    private static readonly Dictionary<IntPtr,Il2CppArrayBase> rootedArrays=new();
    private static Action beforeArrayLength;
    private static class IL2CPP {internal static uint il2cpp_array_length(IntPtr pointer){var action=beforeArrayLength;beforeArrayLength=null;action?.Invoke();return (uint)rootedArrays[pointer].Size;}}
    private static ItemStack[] SnapshotStacks(ItemContainer container)=>container.Stacks;
    private static readonly List<IntPtr> allocations=new();
    private static IntPtr Allocate(int size){var p=Marshal.AllocHGlobal(size);Marshal.Copy(new byte[size],0,p,size);allocations.Add(p);return p;}
    private static SharedItemStack Row(string type,params int[][] rows)=>new SharedItemStack{Type=type,Items=rows};
    private static int[] Values(int value)=>new[]{value,10,-1,11};
    private static ItemContainer Native(params SharedItemStack[] rows)
    {
        var stacks=new List<ItemStack>();
        foreach(var row in rows)
        {
            var array=new Il2CppArrayBase{Size=row.Items.Length,Raw=Allocate(32+Math.Max(8,row.Items.Length*8))};
            Marshal.WriteIntPtr(array.Raw,new IntPtr(0x2200));rootedArrays.Add(array.Raw,array);
            for(int i=0;i<row.Items.Length;i++)
            {
                var item=Allocate(40);Marshal.WriteIntPtr(item,new IntPtr(0x1230));Marshal.WriteIntPtr(item,16,new IntPtr(0x4560));
                Marshal.Copy(row.Items[i],0,IntPtr.Add(item,24),4);Marshal.WriteIntPtr(array.Start,i*8,item);
            }
            var list=new NativeList{Raw=Allocate(40)};Marshal.WriteIntPtr(list.Raw,new IntPtr(0x2210));list._size=row.Items.Length;list._items=array;
            var stack=new ItemStack{Raw=Allocate(32)};Marshal.WriteIntPtr(stack.Raw,new IntPtr(0x2220));
            stack._type=new ItemType{Guid=row.Type};stack._instanceData=list;stack._lastUpdateTime=row.LastUpdate;stacks.Add(stack);
        }
        return new ItemContainer{Stacks=stacks.ToArray()};
    }
    private static partial class NativeItemReader
    {
        internal static bool CanMatch=true;
        internal static ItemReadLayout layout;
        internal static ItemListReadLayout listLayout;
        internal static bool Available=>layout!=null;
        private static int arrayStartOffset=32;
        private static bool arrayStartParity;
        private static void Initialize(){}
        private static Func<Il2CppArrayBase,IntPtr> arrayStart=value=>value.Start;
        internal static Il2CppArrayBase RootArray(IntPtr pointer)=>pointer==IntPtr.Zero?null:rootedArrays[pointer];
        internal static void UseArrayOffsetForTest(int offset){arrayStartOffset=offset;arrayStartParity=false;}
    }
    public static int Run()
    {
        int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
        try
        {
            Check(ItemReadLayout.TryCreate(new IntPtr(0x1230),40,16,24,28,32,36,out NativeItemReader.layout),"Exact complete inspected layout is accepted");
            Check(ItemListReadLayout.TryCreate(new IntPtr(0x2220),32,new IntPtr(0x2210),40,new IntPtr(0x2200),16,24,28,24,16,out NativeItemReader.listLayout),"Validated stack/list reference and scalar field layout is accepted");
            var parityItem=Allocate(40);Marshal.WriteIntPtr(parityItem,new IntPtr(0x1230));Marshal.WriteIntPtr(parityItem,16,new IntPtr(0x4560));
            var parityRows=new List<int[]> {
                new[]{0,0,0,0},new[]{-1,-1,-1,-1},new[]{int.MinValue,int.MaxValue,int.MinValue,int.MaxValue},
                new[]{int.MaxValue,int.MinValue,int.MaxValue,int.MinValue},new[]{-1,0,0,-1},new[]{0,-1,-1,0},
                new[]{unchecked((int)0x89abcdef),unchecked((int)0xfedcba98),0x01234567,0x76543210}
            };
            var random=new Random(9173);for(int i=0;i<32;i++)parityRows.Add(new[]{random.Next(int.MinValue,int.MaxValue),random.Next(int.MinValue,int.MaxValue),random.Next(int.MinValue,int.MaxValue),random.Next(int.MinValue,int.MaxValue)});
            foreach(var values in parityRows){
                Marshal.Copy(values,0,IntPtr.Add(parityItem,24),4);
                Check(NativeItemReader.layout.TryRead(parityItem,new IntPtr(0x4560),out var copied)&&copied[0]==values[0]&&copied[1]==values[1]&&copied[2]==values[2]&&copied[3]==values[3],"Packed matcher agrees with independently copied signed Int32 fields");
                Check(NativeItemReader.layout.TryMatches(parityItem,new IntPtr(0x4560),values),"All four exact signed bit patterns match");
                for(int field=0;field<4;field++){
                    var changed=(int[])values.Clone();changed[field]^=int.MinValue;
                    Check(!NativeItemReader.layout.TryMatches(parityItem,new IntPtr(0x4560),changed),"Each desired word's sign bit independently affects equality: "+field);
                    Marshal.WriteInt32(parityItem,24+field*4,changed[field]);
                    Check(!NativeItemReader.layout.TryMatches(parityItem,new IntPtr(0x4560),values),"Fresh native drift in either half of either pair is detected: "+field);
                    Marshal.WriteInt32(parityItem,24+field*4,values[field]);
                }
            }
            Check(!NativeItemReader.layout.TryMatches(parityItem,IntPtr.Zero,parityRows[0]),"Missing native item type never reaches wide reads");
            Check(!NativeItemReader.layout.TryMatches(IntPtr.Zero,new IntPtr(0x4560),parityRows[0]),"Null item never reaches wide reads");
            Check(!NativeItemReader.layout.TryMatches(parityItem,new IntPtr(0x4560),null),"Missing expected row never reaches wide reads");
            var desired=new[]{Row("type",Values(1),Values(1),Values(2))};var native=Native(desired);
            Check(TryMatchNativeItems(native,desired,out var original)&&ReferenceEquals(original,desired),"Exact freshly read contents reuse authoritative row arrays");
            Check(activeProfile.FastMatchedContainers==1&&activeProfile.FastMatchedInstances==3,"Counters record only fully matched native containers and actual compared instances");
            var first=Marshal.ReadIntPtr(native.Stacks[0]._instanceData._items.Start);
            for(int i=0;i<4;i++)
            {
                int offset=24+i*4,old=Marshal.ReadInt32(first,offset);Marshal.WriteInt32(first,offset,old+1);
                Check(!TryMatchNativeItems(native,desired,out _),"Fresh comparison catches native drift in integer field "+i);
                Marshal.WriteInt32(first,offset,old);
            }
            Check(TryMatchNativeItems(native,desired,out _),"Restored exact contents match without any remote hash change");
            ItemStack.PointerReads=NativeList.PointerReads=Il2CppArrayBase.PointerReads=0;
            Check(TryMatchNativeItems(native,desired,out _),"Scoped match still observes real unmanaged item contents");
            Check(ItemStack.PointerReads==2&&NativeList.PointerReads==1&&Il2CppArrayBase.PointerReads==0,"One scoped stack/list resolution replaces final getter/wrapper reads and repeated rooted-array resolution");
            NativeItemReader.UseArrayOffsetForTest(64);
            Check(TryMatchNativeItems(native,desired,out _),"Unexpected array accessor layout falls back to the installed accessor without disconnecting");
            Check(TryMatchNativeItems(native,desired,out _),"Disabled header shortcut remains a correct wrapper path on later reads");NativeItemReader.UseArrayOffsetForTest(32);
            var observedStack=native.Stacks[0];var observedList=observedStack._instanceData;var observedArray=observedList._items;
            var directSlots=NativeItemReader.CopyPointers(observedArray,desired[0].Items.Length,observedArray.Raw);
            var wrappedSlots=NativeItemReader.CopyPointers(observedArray,desired[0].Items.Length);
            Check(directSlots.Length==wrappedSlots.Length,"Scoped capture preserves pointer-slot count");
            for(int slot=0;slot<directSlots.Length;slot++)Check(directSlots[slot]==wrappedSlots[slot],"Scoped and accessor-based capture read the identical rooted item pointer");
            bool invalidCount=false;try{NativeItemReader.CopyPointers(observedArray,observedArray.Size+1,observedArray.Raw);}catch(InvalidOperationException){invalidCount=true;}
            Check(invalidCount,"Scoped capture still enforces the actual native array capacity");
            beforeArrayLength=()=>observedList._version++;
            Check(!TryMatchNativeItems(native,desired,out _),"Version changed during raw item reads rejects the snapshot");
            beforeArrayLength=()=>observedList._size--;
            Check(!TryMatchNativeItems(native,desired,out _),"Count changed during raw item reads rejects the snapshot");observedList._size=desired[0].Items.Length;
            var replacement=Native(desired).Stacks[0]._instanceData;
            beforeArrayLength=()=>observedList._items=replacement._items;
            Check(!TryMatchNativeItems(native,desired,out _),"Equal-content backing array replacement cannot pass final identity proof");observedList._items=observedArray;
            beforeArrayLength=()=>observedStack._instanceData=replacement;
            Check(!TryMatchNativeItems(native,desired,out _),"Equal-content list replacement cannot pass final identity proof");observedStack._instanceData=observedList;
            var originalLayout=NativeItemReader.listLayout;NativeItemReader.listLayout=null;
            Check(TryMatchNativeItems(native,desired,out _),"Unsupported list layout retains complete generated-wrapper fallback");NativeItemReader.listLayout=originalLayout;
            foreach(var bad in new[]{new[]{16,16,28,24,16},new[]{16,24,24,24,16},new[]{16,24,28,24,24},new[]{8,24,28,24,16},new[]{16,31,28,24,16}})
                Check(!ItemListReadLayout.TryCreate(new IntPtr(0x2220),32,new IntPtr(0x2210),40,new IntPtr(0x2200),bad[0],bad[1],bad[2],bad[3],bad[4],out _),"Overlapping/out-of-bounds/misaligned metadata never enables raw list reads");
            Marshal.WriteIntPtr(observedStack.Raw,new IntPtr(0x9990));
            Check(!originalLayout.TryObserve(observedStack.Raw,observedList.Raw,out _),"Unexpected native stack subclass falls back before reading field offsets");Marshal.WriteIntPtr(observedStack.Raw,new IntPtr(0x2220));
            Marshal.WriteIntPtr(observedList.Raw,new IntPtr(0x9990));
            Check(!originalLayout.TryObserve(observedStack.Raw,observedList.Raw,out _),"Unexpected list class cannot use direct offsets");Marshal.WriteIntPtr(observedList.Raw,new IntPtr(0x2210));
            Marshal.WriteIntPtr(observedArray.Raw,new IntPtr(0x9990));
            Check(!originalLayout.TryObserve(observedStack.Raw,observedList.Raw,out _),"Unexpected backing array class cannot use raw pointer slots");Marshal.WriteIntPtr(observedArray.Raw,new IntPtr(0x2200));
            native.Stacks[0]._lastUpdateTime=900;
            Check(TryMatchNativeItems(native,desired,out original)&&!ReferenceEquals(original,desired)&&original[0].LastUpdate==900&&ReferenceEquals(original[0].Items,desired[0].Items),"Native stack cache time is preserved for rollback with only shallow stack allocation");
            native.Stacks[0]._type.Guid="wrong";Check(!TryMatchNativeItems(native,desired,out _),"Native stack GUID mismatch falls back");native.Stacks[0]._type.Guid="type";
            Marshal.WriteIntPtr(first,16,new IntPtr(0x9990));Check(!TryMatchNativeItems(native,desired,out _),"Distinct native item type pointer uses existing GUID-aware wrapper fallback");Marshal.WriteIntPtr(first,16,new IntPtr(0x4560));
            Marshal.WriteIntPtr(first,new IntPtr(0x9990));Check(!TryMatchNativeItems(native,desired,out _),"Subclass or corrupt native class cannot use direct offsets");Marshal.WriteIntPtr(first,new IntPtr(0x1230));
            NativeItemReader.CanMatch=false;Check(!TryMatchNativeItems(native,desired,out _),"Unavailable ABI or absent wrapper parity forces the existing reader");NativeItemReader.CanMatch=true;
            foreach(var bad in new[]{new[]{39,16,24,28,32,36},new[]{40,16,24,32,28,36},new[]{40,8,24,28,32,36}})
                Check(!ItemReadLayout.TryCreate(new IntPtr(0x1230),bad[0],bad[1],bad[2],bad[3],bad[4],bad[5],out _),"Unexpected native size/offsets reject direct layout");
            Check(!TryMatchNativeItems(native,new[]{Row("type",Values(1),Values(2))},out _),"Duplicate multiplicity cannot be ignored");
            Check(!TryMatchNativeItems(native,new[]{Row("type",Values(2),Values(1),Values(1))},out _),"Reordered equivalent content falls back to existing canonical equality");
            Check(!TryMatchNativeItems(native,new[]{Row("type",Values(1)),Row("type",Values(1),Values(2))},out _),"Regrouped equivalent content falls back to canonical equality");
            var two=Native(Row("a",Values(1)),Row("b",Values(2)));
            Check(!TryMatchNativeItems(two,new[]{Row("b",Values(2)),Row("a",Values(1))},out _),"Reordered native stack identities fall back");
            Check(!TryMatchNativeItems(two,new[]{Row("a",Values(1))},out _),"Extra native stack is not accepted");
            Check(!TryMatchNativeItems(Native(Row("a",Values(1))),new[]{Row("a",Values(1)),Row("b",Values(2))},out _),"Missing native stack is not accepted");
            Check(!TryMatchNativeItems(native,new[]{Row("type",new[]{1,2,3},Values(1),Values(2))},out _),"Malformed desired record never reaches direct field comparison");
            native.Stacks[0]._instanceData._size=4;Check(!TryMatchNativeItems(native,desired,out _),"Native row count and backing capacity mismatch falls back");native.Stacks[0]._instanceData._size=3;
            Marshal.WriteIntPtr(native.Stacks[0]._instanceData._items.Start,IntPtr.Zero);Check(!TryMatchNativeItems(native,desired,out _),"Null native item slot falls back without dereferencing it");
            Check(TryMatchNativeItems(Native(Row("empty")),Array.Empty<SharedItemStack>(),out _),"Empty native stacks match CaptureItems omission semantics");
            return checks;
        }
        finally { foreach(var p in allocations)Marshal.FreeHGlobal(p);allocations.Clear();rootedArrays.Clear(); }
    }
}
