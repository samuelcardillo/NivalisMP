using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

public static partial class EconomyNativeMatchBenchmark
{
    private const int Count=319000;
    private static readonly IntPtr Class=new IntPtr(0x1230), Type=new IntPtr(0x4560);
    private static IntPtr slots;
    private static int[][] expected;
    private static ItemReadLayout baseline;
    private static PairReadLayout candidate;
    private static long Baseline()
    {
        long matched=0;
        for(int i=0;i<Count;i++)if(baseline.TryMatches(Marshal.ReadIntPtr(slots,i*IntPtr.Size),Type,expected[i]))matched++;
        return matched;
    }
    private static long Candidate()
    {
        long matched=0;
        for(int i=0;i<Count;i++)if(candidate.TryMatches(Marshal.ReadIntPtr(slots,i*IntPtr.Size),Type,expected[i]))matched++;
        return matched;
    }
    private static double Measure(Func<long> scan)
    {
        var timer=Stopwatch.StartNew();long value=scan();timer.Stop();
        if(value!=Count)throw new Exception("An exact native record did not match.");
        return timer.Elapsed.TotalMilliseconds;
    }
    public static int Main(string[] args)
    {
        var objects=new IntPtr[Count];
        try{
            if(!ItemReadLayout.TryCreate(Class,40,16,24,28,32,36,out baseline)||!PairReadLayout.TryCreate(Class,40,16,24,28,32,36,out candidate))throw new Exception("Benchmark layout rejected.");
            slots=Marshal.AllocHGlobal(Count*IntPtr.Size);expected=new int[Count][];
            var rows=new int[Count][];var random=new Random(46131);
            for(int i=0;i<Count;i++){
                objects[i]=Marshal.AllocHGlobal(40);
                Marshal.WriteIntPtr(objects[i],Class);Marshal.WriteIntPtr(objects[i],8,IntPtr.Zero);Marshal.WriteIntPtr(objects[i],16,Type);
                rows[i]=new[]{random.Next(int.MinValue,int.MaxValue),random.Next(int.MinValue,int.MaxValue),random.Next(int.MinValue,int.MaxValue),random.Next(int.MinValue,int.MaxValue)};
                Marshal.Copy(rows[i],0,IntPtr.Add(objects[i],24),4);
            }
            var order=Enumerable.Range(0,Count).ToArray();
            for(int i=Count-1;i>0;i--){int j=random.Next(i+1);(order[i],order[j])=(order[j],order[i]);}
            for(int i=0;i<Count;i++){Marshal.WriteIntPtr(slots,i*IntPtr.Size,objects[order[i]]);expected[i]=rows[order[i]];}
            for(int i=0;i<8;i++){Measure(Baseline);Measure(Candidate);}
            var oldTimes=new double[15];var pairTimes=new double[15];
            for(int i=0;i<15;i++){
                if((i&1)==0){oldTimes[i]=Measure(Baseline);pairTimes[i]=Measure(Candidate);}
                else{pairTimes[i]=Measure(Candidate);oldTimes[i]=Measure(Baseline);}
            }
            double Median(double[] values)=>values.OrderBy(x=>x).ElementAt(values.Length/2);
            var report=new{utc=DateTime.UtcNow,framework=RuntimeInformation.FrameworkDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),BitConverter.IsLittleEndian,
                Items=Count,ObjectBytes=40,IndividuallyAllocated=true,ShuffledPointerOrder=true,RootedPointerSlots=true,WarmupPairs=8,Samples=15,
                BaselineMilliseconds=oldTimes,CandidateMilliseconds=pairTimes,BaselineMedian=Median(oldTimes),CandidateMedian=Median(pairTimes),
                MedianReductionPercent=100*(1-Median(pairTimes)/Median(oldTimes)),SyntheticOnly=true};
            string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(args[0],json);Console.WriteLine(json);return 0;
        }finally{foreach(var item in objects)if(item!=IntPtr.Zero)Marshal.FreeHGlobal(item);if(slots!=IntPtr.Zero)Marshal.FreeHGlobal(slots);GC.KeepAlive(expected);}
    }
}
