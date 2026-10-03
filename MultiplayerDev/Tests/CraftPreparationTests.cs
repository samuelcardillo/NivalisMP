using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisTogether.Dedicated
{
    internal static class CraftPreparationTests
    {
        private static int checks;
        private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
        private static void Reject<T>(Action action,string message) where T:Exception
        {try{action();}catch(T){Check(true,message);return;}throw new Exception(message);}
        private static void Reset()
        {
            Time.OwnerThread=Environment.CurrentManagedThreadId;Time.Now=0;
            BusinessCraftStateReplication.Reset();BusinessCraftStateReplication.SetAuthority(true);
            BusinessCraftStateReplication.NativeCommits=BusinessCraftStateReplication.ValidationCalls=0;
            BusinessCraftStateReplication.ThrowNative=BusinessCraftStateReplication.ResetDuringNative=false;
            BusinessCraftStateReplication.IncrementalUnits=BusinessCraftStateReplication.IncrementalAborts=0;BusinessCraftStateReplication.FailIncrementalPhase=-1;
        }
        private static WorldEvent[] Events(BusinessCraftState state,int pieceSize=97,long? envelopeSequence=null)
        {
            var encoded=BoundedSnapshotCodec.EncodeJson(state,16*1024*1024);
            return Enumerable.Range(0,(encoded.Bytes.Length+pieceSize-1)/pieceSize).Select(i=>new WorldEvent{
                Kind="business-craft-chunk",Data=JsonSerializer.SerializeToElement(new BusinessCraftChunk{
                    Sequence=envelopeSequence??state.Sequence,Hash=encoded.Hash,Encoding=encoded.Encoding,RawLength=encoded.RawLength,
                    Length=encoded.Bytes.Length,Offset=i*pieceSize,Bytes=encoded.Bytes.Skip(i*pieceSize).Take(pieceSize).ToArray()
                },DedicatedProtocol.Json)}).ToArray();
        }
        private static BusinessCraftState State(long sequence=1)=>new BusinessCraftState{Sequence=sequence,Venues=new[]{new BusinessVenueOrders{GhostId="venue-ghost",VenueId="venue",Orders=Array.Empty<BusinessOrderState>()}}};
        private static BusinessCraftStateReplication.EncodedCraftUpdate Detach(WorldEvent[] events)
        {
            BusinessCraftStateReplication.EncodedCraftUpdate result=null;
            for(int i=0;i<events.Length;i++){
                result=BusinessCraftStateReplication.AcceptChunk(events[i]);
                Check((result!=null)==(i==events.Length-1),"Only final chunk detaches a complete owned payload");
            }
            return result;
        }
        private static BusinessCraftStateReplication.PreparedCraftUpdate Prepare(BusinessCraftStateReplication.EncodedCraftUpdate encoded)
            =>Task.Run(()=>BusinessCraftStateReplication.PrepareManaged(encoded,CancellationToken.None)).GetAwaiter().GetResult();
        private static void DetachedAndOnceOnly()
        {
            Reset();var input=Detach(Events(State(),29));
            Check(BusinessCraftStateReplication.NativeCommits==0&&BusinessCraftStateReplication.ValidationCalls==0,"Assembly does not decode, validate DTOs or touch native state");
            var following=Events(State(2),13);BusinessCraftStateReplication.AcceptChunk(following[0]);
            var prepared=Prepare(input);
            Check(BusinessCraftStateReplication.NativeCommits==0&&BusinessCraftStateReplication.ValidationCalls==1,"Background work validates exactly once without native state");
            Check(BusinessCraftStateReplication.LastCaptureHash=="","Preparation cannot publish a native success hash");
            prepared.Commit();
            Check(BusinessCraftStateReplication.NativeCommits==1&&BusinessCraftStateReplication.LastState.Sequence==1,"Detached result retains its own sequence despite later assembler input");
            Check(BusinessCraftStateReplication.ValidationCalls==1,"Native commit skips duplicate pure validation");
            Check(BusinessCraftStateReplication.CommitThread==Time.OwnerThread,"Native commit runs on owner thread");
            Check(BusinessCraftStateReplication.LastCaptureHash==input.Hash,"Only successful native commit publishes hash");
            var profile=BusinessCraftStateReplication.LastProfile;
            Check(profile.BackgroundPrepared&&profile.Preparation>=profile.Decode&&profile.Validation>=0,"Preparation/decode/validation timing is separated from native commit");
            Reject<InvalidOperationException>(prepared.Commit,"Prepared update cannot commit twice");
            BusinessCraftStateReplication.Apply(State(3));
            Check(BusinessCraftStateReplication.ValidationCalls==2,"Public state apply retains pure validation");
            Reject<ArgumentException>(()=>BusinessCraftStateReplication.Apply(new BusinessCraftState{Sequence=4,Meals=null}),"Public state apply rejects invalid DTOs");
            Reset();foreach(var evt in Events(State()))BusinessCraftStateReplication.Apply(evt);
            Check(BusinessCraftStateReplication.NativeCommits==1&&BusinessCraftStateReplication.ValidationCalls==1,"Synchronous compatibility path still applies one validated snapshot");
            Check(!BusinessCraftStateReplication.LastProfile.BackgroundPrepared,"Synchronous profile is distinguished from background preparation");
        }
        private static void CancellationAndLifecycle()
        {
            Reset();var input=Detach(Events(State()));
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Reject<OperationCanceledException>(()=>BusinessCraftStateReplication.PrepareManaged(input,cancellation.Token),"Cancelled preparation never starts decode/validation");
            Check(BusinessCraftStateReplication.ValidationCalls==0,"Cancellation occurs before pure work");
            var prepared=Prepare(input);BusinessCraftStateReplication.Reset();
            Reject<InvalidOperationException>(prepared.Commit,"Reset invalidates completed preparation");
            Check(BusinessCraftStateReplication.NativeCommits==0&&BusinessCraftStateReplication.LastCaptureHash=="","Stale epoch cannot mutate native state or hash");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessCraftStateReplication.SetAuthority(false);
            Check(BusinessCraftStateReplication.AcceptChunk(Events(State(2))[0])==null,"Disabled client authority accepts no chunks");
            BusinessCraftStateReplication.SetAuthority(true);
            Reject<InvalidOperationException>(prepared.Commit,"Disable/re-enable invalidates previous epoch even when authority is active again");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessCraftStateReplication.Apply(State(2));
            Reject<InvalidOperationException>(prepared.Commit,"A detached older sequence cannot follow a newer committed native state");
            Check(BusinessCraftStateReplication.LastState.Sequence==2&&BusinessCraftStateReplication.LastCaptureHash=="","Outdated preparation cannot overwrite native state or success hash");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessCraftStateReplication.ThrowNative=true;
            Reject<InvalidOperationException>(prepared.Commit,"Native failure propagates to final ACK owner");
            Check(BusinessCraftStateReplication.LastCaptureHash=="","Native failure never publishes success hash");
            BusinessCraftStateReplication.ThrowNative=false;Reject<InvalidOperationException>(prepared.Commit,"Failed native commit is consumed and cannot retry");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessCraftStateReplication.ResetDuringNative=true;
            Reject<InvalidOperationException>(prepared.Commit,"Reentrant reset during native callback invalidates success");
            Check(BusinessCraftStateReplication.LastCaptureHash=="","Reentrant reset cannot republish stale hash");
        }
        private static void ValidationAndFraming()
        {
            Reset();var bad=State();bad.Processors=new[]{new BusinessProcessorState{Id="processor",PrefabId="prefab",Pose=new float[]{0,0,0,0,0,0,1},Bounds=new float[6],Meals=new[]{"missing-meal"}}};
            var input=Detach(Events(bad));Reject<ArgumentException>(()=>Prepare(input),"Background validator rejects missing referenced meal");
            Check(BusinessCraftStateReplication.NativeCommits==0,"Invalid DTO never reaches native state");
            Reset();input=Detach(Events(State(),97,2));Reject<InvalidDataException>(()=>Prepare(input),"Envelope and decoded state sequence must match");
            Reset();input=Detach(Events(State()));input.Bytes[0]^=1;
            Reject<InvalidDataException>(()=>Prepare(input),"Encoded checksum is validated before decoded state can commit");
            Reset();var parts=Events(State(),13);Check(parts.Length>2,"Fixture exercises multi-chunk assembly");
            Check(BusinessCraftStateReplication.AcceptChunk(parts[1])==null,"Late-join tail is ignored before a header");
            BusinessCraftStateReplication.AcceptChunk(parts[0]);Reject<InvalidDataException>(()=>BusinessCraftStateReplication.AcceptChunk(parts[2]),"Missing chunk within an active batch is rejected");
            Reset();BusinessCraftStateReplication.AcceptChunk(parts[0]);Time.Now=31;
            Reject<InvalidDataException>(()=>BusinessCraftStateReplication.AcceptChunk(parts[1]),"Native-thread assembler retains its30second bound");
            Reset();var prepared=Prepare(Detach(Events(State())));prepared.Commit();
            Check(BusinessCraftStateReplication.AcceptChunk(Events(State())[0])==null,"Already applied sequence remains ignored");
            Reset();var part=parts[0].Data.Deserialize<BusinessCraftChunk>(DedicatedProtocol.Json);part.Bytes=new byte[24*1024+1];part.Length=part.Bytes.Length;
            Reject<ArgumentException>(()=>BusinessCraftStateReplication.AcceptChunk(new WorldEvent{Kind="business-craft-chunk",Data=JsonSerializer.SerializeToElement(part,DedicatedProtocol.Json)}),"Chunk size bound remains enforced");
        }
        private static void IncrementalLifecycle()
        {
            Reset();var input=Detach(Events(State()));var prepared=Prepare(input);
            var budget=new ClientMutationBudget(System.Diagnostics.Stopwatch.Frequency,100000,1);budget.BeginFrame(1);
            Check(!prepared.Advance(budget)&&BusinessCraftStateReplication.IncrementalUnits==1,"Incremental preparation consumes one granted complete unit");
            Check(BusinessCraftStateReplication.LastCaptureHash==""&&BusinessCraftStateReplication.NativeCommits==0,"Partial application publishes no completion hash or final state");
            Check(!prepared.Advance(budget)&&BusinessCraftStateReplication.IncrementalUnits==1,"Repeated calls cannot obtain an extra same-frame grant");
            for(int frame=2;frame<=8;frame++){
                budget.BeginFrame(frame);bool done=prepared.Advance(budget);Check(done==(frame==8),"Completion occurs only after every ordered phase");
            }
            Check(BusinessCraftStateReplication.LastCaptureHash==input.Hash&&BusinessCraftStateReplication.NativeCommits==1,"Completion publishes exactly once after final phase");
            Reject<InvalidOperationException>(()=>prepared.Advance(budget),"Completed incremental update cannot advance again");
            Reset();prepared=Prepare(Detach(Events(State())));budget.BeginFrame(20);prepared.Advance(budget);prepared.Abort();
            Check(BusinessCraftStateReplication.IncrementalAborts==1&&BusinessCraftStateReplication.LastCaptureHash=="","Abort cancels active cursor without publishing success");
            prepared.Abort();Check(BusinessCraftStateReplication.IncrementalAborts==1,"Abort is idempotent");
            Reject<InvalidOperationException>(()=>prepared.Advance(budget),"Aborted incremental work cannot resume");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessCraftStateReplication.FailIncrementalPhase=1;
            budget.BeginFrame(30);prepared.Advance(budget);budget.BeginFrame(31);
            Reject<InvalidOperationException>(()=>prepared.Advance(budget),"Unit failure propagates to the ACK owner");
            Check(BusinessCraftStateReplication.IncrementalAborts==1&&BusinessCraftStateReplication.LastCaptureHash=="","Unit failure aborts cursor and never acknowledges publication");
            Reset();prepared=Prepare(Detach(Events(State())));budget.BeginFrame(40);prepared.Advance(budget);BusinessCraftStateReplication.Reset();budget.BeginFrame(41);
            Reject<InvalidOperationException>(()=>prepared.Advance(budget),"Reset between frames invalidates all subsequent native units");prepared.Abort();
        }
        public static int Main()
        {
            try{DetachedAndOnceOnly();CancellationAndLifecycle();ValidationAndFraming();IncrementalLifecycle();Console.WriteLine("PASS CraftPreparationTests: "+checks+" assertions; production preparation/advance lifecycle, framing, and native-commit facade.");return 0;}
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
