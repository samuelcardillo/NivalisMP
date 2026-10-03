using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace CanonicalHashRegression
{
    internal static class Program
    {
        private static int checks;
        private static readonly List<string> cases = new();
        private static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
        private static int[] Row(int a,int b=0,int c=0,int d=0)=>new[]{a,b,c,d};
        private static SharedItemStack Stack(string type,params int[][] items)=>new(){Type=type,Items=items};
        private static SharedVendorStock Vendor(string id,SharedItemStack[] stacks,float relationship=0,int day=0)=>new(){VendorId=id,Stacks=stacks,Relationship=relationship,LastRelationshipDay=day};
        private static SharedEconomyState State(params SharedItemStack[] items)=>new(){Money=1000,Inventory=items};
        private static SharedEconomyState Clone(SharedEconomyState value)=>new(){Money=value.Money,Sequence=value.Sequence,Inventory=CloneItems(value.Inventory),Vendors=value.Vendors.Select(v=>Vendor(v.VendorId,CloneItems(v.Stacks),v.Relationship,v.LastRelationshipDay)).ToArray()};
        private static SharedItemStack[] CloneItems(SharedItemStack[] value)=>value.Select(s=>new SharedItemStack{Type=s.Type,LastUpdate=s.LastUpdate,Items=s.Items.Select(i=>(int[])i.Clone()).ToArray()}).ToArray();
        private static void Parity(SharedEconomyState state,string label)
        {
            var old=OldHash.Evaluate(state,true);var current=NewHash.Evaluate(state,true);
            Check(old.Hash==current.Hash,label+" full hash with per-container hashes");
            Check(OldHash.ContentHash(state)==current.Hash&&NewHash.ContentHash(state)==current.Hash,label+" map-free full hash");
            Check(old.ItemHashes.Count==current.ItemHashes.Count,label+" reference-key count");
            foreach(var pair in old.ItemHashes){Check(current.ItemHashes.TryGetValue(pair.Key,out var hash)&&pair.Value==hash,label+" container hash");Check(OldHash.EvaluateItem(pair.Key)==NewHash.EvaluateItem(pair.Key)&&hash==NewHash.EvaluateItem(pair.Key),label+" standalone container parity");}
            cases.Add(label);
        }
        private static Exception Failure(Action action){try{action();return null;}catch(Exception ex){return ex;}}
        private static void Reject(SharedEconomyState state,string label)
        {
            foreach(bool map in new[]{false,true}){
                var old=Failure(()=>OldHash.Evaluate(state,map));var current=Failure(()=>NewHash.Evaluate(state,map));
                Check(old!=null&&current!=null,label+" rejects");Check(old.GetType()==current.GetType()&&old.Message==current.Message,label+" rejection type/message parity");
            }
            cases.Add(label);
        }
        private static void SemanticCases()
        {
            Parity(State(),"empty world");
            var signed=State(Stack("\u00e9\u6f22\ud83d\ude00",Row(int.MinValue,int.MaxValue,-1,0),Row(0,-1,int.MinValue,int.MaxValue),Row(-1,0,int.MaxValue,int.MinValue)));
            signed.Money=int.MinValue;signed.Vendors=new[]{Vendor("vendor:\u6f22\ud83d\ude00",CloneItems(signed.Inventory),-123.75f,int.MinValue)};Parity(signed,"signed integers and UTF8 IDs");
            var boundaries=State(Stack(new string('\u6f22',128),Row(1)),Stack("\ud800",Row(2)),Stack("\udc00",Row(3)));Parity(boundaries,"UTF8 multi-byte length prefix and unpaired surrogate fallback");
            var original=State(Stack("b",Row(2),Row(-1),Row(2)),Stack("a",Row(5,6,7,8)));
            original.Vendors=new[]{Vendor("z",CloneItems(original.Inventory),.25f,17),Vendor("a",new[]{Stack("x",Row(9))},-0f,-1)};Parity(original,"multiset fixture");
            var reordered=Clone(original);reordered.Sequence=long.MaxValue;Array.Reverse(reordered.Vendors);Array.Reverse(reordered.Inventory);foreach(var stack in reordered.Inventory){stack.LastUpdate=int.MaxValue;Array.Reverse(stack.Items);}Parity(reordered,"order sequence cache timestamp ignored");Check(NewHash.ContentHash(original)==NewHash.ContentHash(reordered),"semantic reordered hash unchanged");
            var regrouped=Clone(original);regrouped.Inventory=new[]{Stack("b",Row(2)),Stack("a",Row(5,6,7,8)),Stack("b",Row(-1),Row(2)),Stack("empty")};Parity(regrouped,"stack splitting grouping and empty stack");Check(NewHash.ContentHash(original)==NewHash.ContentHash(regrouped),"grouping unchanged");
            var aliased=Clone(original);aliased.Inventory=original.Inventory;aliased.Vendors=new[]{Vendor("a",original.Inventory),Vendor("b",original.Inventory),Vendor("c",original.Vendors[0].Stacks)};Parity(aliased,"inventory and vendor reference aliases");Check(NewHash.Evaluate(aliased,true).ItemHashes.Count==2,"aliased arrays retain reference-key dictionary semantics");
            var zero=Clone(original);zero.Vendors[0].Relationship=0f;var negativeZero=Clone(zero);negativeZero.Vendors[0].Relationship=BitConverter.Int32BitsToSingle(int.MinValue);Parity(zero,"relationship positive zero");Parity(negativeZero,"relationship negative zero");Check(NewHash.ContentHash(zero)==NewHash.ContentHash(negativeZero),"relationship signed-zero normalization");
            for(int word=0;word<4;word++){var changed=Clone(original);changed.Inventory[0].Items[0][word]^=int.MinValue;Parity(changed,"changed signed word "+word);Check(NewHash.ContentHash(original)!=NewHash.ContentHash(changed),"all four integer fields affect hash");}
            var duplicate=Clone(original);duplicate.Inventory[0].Items=duplicate.Inventory[0].Items.Concat(new[]{Row(2)}).ToArray();Parity(duplicate,"duplicate multiplicity");Check(NewHash.ContentHash(original)!=NewHash.ContentHash(duplicate),"multiplicity affects hash");
            foreach(var change in new Action<SharedEconomyState>[]{s=>s.Money++,s=>s.Vendors[0].VendorId+="changed",s=>s.Vendors[0].Relationship=.5f,s=>s.Vendors[0].LastRelationshipDay++,s=>s.Inventory[0].Type="other"}){var changed=Clone(original);change(changed);Parity(changed,"gameplay field mutation");Check(NewHash.ContentHash(original)!=NewHash.ContentHash(changed),"gameplay field remains represented");}
            var random=new Random(19271);
            for(int trial=0;trial<64;trial++){
                var items=Enumerable.Range(0,random.Next(1,8)).Select(i=>Stack("type"+(i%3),Enumerable.Range(0,random.Next(0,20)).Select(j=>Row(random.Next(-5,5),random.Next(-5,5),random.Next(-5,5),random.Next(-5,5))).ToArray())).ToArray();
                var state=State(items);state.Vendors=new[]{Vendor("vendorB",items,trial/16f,-trial),Vendor("vendorA",CloneItems(items),-trial/16f,trial)};Parity(state,"deterministic multiset fuzz "+trial);
            }
        }
        private static void MalformedCases()
        {
            Reject(null,"null state");Reject(new SharedEconomyState{Vendors=null},"null vendors");Reject(new SharedEconomyState{Vendors=new SharedVendorStock[4097]},"vendor count bound");
            foreach(var inventory in new[]{(SharedItemStack[])null,new SharedItemStack[16385],new SharedItemStack[]{null},new[]{Stack("")},new[]{Stack("bad\n")},new[]{Stack(new string('x',129))},new[]{new SharedItemStack{Type="x",Items=null}},new[]{Stack("x",(int[])null)},new[]{Stack("x",new int[3])},new[]{Stack("x",new int[5])},new[]{Stack("x",new int[200001][])}}){Reject(new SharedEconomyState{Inventory=inventory},"malformed inventory");var old=Failure(()=>OldHash.EvaluateItem(inventory));var current=Failure(()=>NewHash.EvaluateItem(inventory));Check(old!=null&&current!=null&&old.GetType()==current.GetType()&&old.Message==current.Message,"standalone malformed item parity");}
            foreach(var vendor in new[]{(SharedVendorStock)null,Vendor("",Array.Empty<SharedItemStack>()),Vendor("bad\n",Array.Empty<SharedItemStack>()),Vendor(new string('x',129),Array.Empty<SharedItemStack>()),Vendor("x",null),Vendor("x",Array.Empty<SharedItemStack>(),float.NaN),Vendor("x",Array.Empty<SharedItemStack>(),float.PositiveInfinity),Vendor("x",Array.Empty<SharedItemStack>(),float.NegativeInfinity)})Reject(new SharedEconomyState{Vendors=new[]{vendor}},"malformed vendor");
            Reject(new SharedEconomyState{Vendors=new[]{Vendor("x",Array.Empty<SharedItemStack>()),Vendor("x",Array.Empty<SharedItemStack>())}},"duplicate vendor ID");
            Parity(State(Stack("recovered",Row(1))),"hash independence after failures");
        }
        private static SharedEconomyState Large(bool aliases)
        {
            const int total=319372,containers=71;var arrays=new SharedItemStack[containers][];
            for(int c=0;c<containers;c++){
                int count=total/containers+(c<total%containers?1:0);var rows=new int[count][];
                for(int i=0;i<count;i++)rows[i]=Row(i%16,i%7,-1,i%11);
                arrays[c]=new[]{Stack("c2ab9137-1a14-45d1-993c-03082667ad99",rows)};
            }
            return new SharedEconomyState{Money=1000,Vendors=Enumerable.Range(0,aliases?141:71).Select(i=>Vendor("vendor:"+i.ToString("D3")+":\u00e9",arrays[i%containers],i%4/4f,i%8)).ToArray()};
        }
        private static object Benchmark(SharedEconomyState state,bool map,int iterations)
        {
            string expected=OldHash.Evaluate(state,map).Hash;Check(NewHash.Evaluate(state,map).Hash==expected,"benchmark warmup parity");
            object Run(bool candidate){var elapsed=new List<double>();var allocated=new List<long>();for(int i=0;i<iterations;i++){long before=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();var result=candidate?NewHash.Evaluate(state,map):OldHash.Evaluate(state,map);clock.Stop();long bytes=GC.GetAllocatedBytesForCurrentThread()-before;Check(result.Hash==expected,"benchmark repeat exact hash");elapsed.Add(clock.Elapsed.TotalMilliseconds);allocated.Add(bytes);}return new{ElapsedMs=elapsed,AllocatedBytes=allocated,MeanElapsedMs=elapsed.Average(),MeanAllocatedBytes=allocated.Average()};}
            int unique=state.Vendors.Select(v=>v.Stacks).Distinct().Sum(a=>a.Sum(s=>s.Items.Length));int occurrences=state.Vendors.Sum(v=>v.Stacks.Sum(s=>s.Items.Length));
            return new{Vendors=state.Vendors.Length,UniqueItemInstances=unique,CanonicalItemOccurrences=occurrences,PerContainerHashes=map,Iterations=iterations,Old=Run(false),New=Run(true),Hash=expected};
        }
        private static void CancellationCases(SharedEconomyState state)
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            foreach(bool map in new[]{false,true}){var old=Failure(()=>OldHash.Evaluate(state,map,cancelled.Token));var current=Failure(()=>NewHash.Evaluate(state,map,cancelled.Token));Check(old is OperationCanceledException&&current is OperationCanceledException,"already-cancelled parity");}
            foreach(bool candidate in new[]{false,true}){using var during=new CancellationTokenSource();during.CancelAfter(1);var failure=Failure(()=>{if(candidate)NewHash.Evaluate(state,true,during.Token);else OldHash.Evaluate(state,true,during.Token);});Check(failure is OperationCanceledException,"in-progress cancellation observed "+candidate);Check(((OperationCanceledException)failure).CancellationToken==during.Token,"cancellation token retained "+candidate);}
            Parity(State(Stack("after cancellation",Row(2))),"fresh hash after cancellation");
        }
        public static int Main(string[] args)
        {
            try{SemanticCases();MalformedCases();var large=Large(false);CancellationCases(large);int iterations=int.Parse(args[1]);var benchmarks=new[]{Benchmark(large,false,iterations),Benchmark(large,true,iterations),Benchmark(Large(true),true,iterations)};var report=new{Pass=true,Checks=checks,Cases=cases,Framework=Environment.Version.ToString(),Benchmarks=benchmarks,Limitations=new[]{"Actual selected production method bodies extracted with Roslyn; no game/native runtime executed.","Benchmark fixture construction excluded; elapsed timings are standalone managed observations, not native gameplay FPS.","Same canonical hashes and per-container dictionaries are required; cancellation timing is nondeterministic but must terminate with the supplied token.","Installed .NET8 runtime with bundled .NET6 compile references; native .NET6 execution remains a separate validation."}};File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS: "+checks+" actual canonical hash parity checks");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        }
    }
}
