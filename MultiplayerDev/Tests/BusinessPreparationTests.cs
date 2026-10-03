using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisTogether.Dedicated
{
    internal static class BusinessPreparationTests
    {
        private static int checks;
        private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
        private static void Reject<T>(Action action,string message)where T:Exception
        {try{action();}catch(T){Check(true,message);return;}throw new Exception(message);}
        private static void Reset()
        {
            Time.OwnerThread=Environment.CurrentManagedThreadId;Time.Now=0;
            BusinessStateReplication.Reset();
            BusinessStateReplication.NativeCommits=BusinessStateReplication.ValidationCalls=BusinessStateReplication.Reports=0;
            BusinessStateReplication.ThrowNative=BusinessStateReplication.ResetDuringNative=false;BusinessStateReplication.OnValidate=null;
        }
        private static BusinessState State(long sequence=1)=>new BusinessState{Sequence=sequence,
            People=new[]{new BusinessPersonState{PersonId="person",VenueId="venue",Wage=100}},
            Venues=new[]{new BusinessVenueState{GhostId="venue-ghost",VenueId="venue"}}};
        private static WorldEvent[] Events(BusinessState state,int pieceSize=24000,long? envelopeSequence=null)
        {
            var encoded=BoundedSnapshotCodec.EncodeJson(state,16*1024*1024);
            return Enumerable.Range(0,(encoded.Bytes.Length+pieceSize-1)/pieceSize).Select(i=>new WorldEvent{
                Kind="business-state-chunk",Data=JsonSerializer.SerializeToElement(new BusinessStateChunk{
                    Sequence=envelopeSequence??state.Sequence,Hash=encoded.Hash,Encoding=encoded.Encoding,RawLength=encoded.RawLength,
                    Length=encoded.Bytes.Length,Offset=i*pieceSize,Bytes=encoded.Bytes.Skip(i*pieceSize).Take(pieceSize).ToArray()
                },DedicatedProtocol.Json)}).ToArray();
        }
        private static BusinessStateReplication.EncodedBusinessUpdate Detach(WorldEvent[] events)
        {
            BusinessStateReplication.EncodedBusinessUpdate result=null;
            for(int i=0;i<events.Length;i++){
                result=BusinessStateReplication.AcceptChunk(events[i]);
                Check((result!=null)==(i==events.Length-1),"Only the last chunk transfers complete buffer ownership");
            }
            return result;
        }
        private static BusinessStateReplication.PreparedBusinessUpdate Prepare(BusinessStateReplication.EncodedBusinessUpdate input)
            =>Task.Run(()=>BusinessStateReplication.PrepareManaged(input,CancellationToken.None)).GetAwaiter().GetResult();
        private static void OwnershipAndCompatibility()
        {
            Reset();var source=State();var input=Detach(Events(source,97));
            Check(BusinessStateReplication.NativeCommits==0&&BusinessStateReplication.ValidationCalls==0,"Chunk assembly performs no DTO validation or native commit");
            var later=Events(State(2),29);BusinessStateReplication.AcceptChunk(later[0]);
            var prepared=Prepare(input);source.People[0].Wage=999;Array.Fill(input.Bytes,(byte)0);
            Check(BusinessStateReplication.NativeCommits==0&&BusinessStateReplication.Reports==0,"Background preparation touches neither native state nor diagnostics");
            Check(BusinessStateReplication.ValidationCalls==1&&BusinessStateReplication.LastCaptureHash=="","Pure validation happens once and cannot publish native success");
            prepared.Commit();
            Check(BusinessStateReplication.LastState.Sequence==1&&BusinessStateReplication.LastState.People[0].Wage==100,"Prepared DTO owns its deserialized snapshot despite input and assembler changes");
            Check(BusinessStateReplication.NativeCommits==1&&BusinessStateReplication.ValidationCalls==1,"Commit retains one native transaction and skips duplicate pure validation");
            Check(BusinessStateReplication.CommitThread==Time.OwnerThread&&BusinessStateReplication.Reports==1,"Native transaction and diagnostics run on owning thread");
            Check(BusinessStateReplication.LastCaptureHash==input.Hash,"Only successful commit updates capture hash");
            Check(BusinessStateReplication.LastBackground&&BusinessStateReplication.LastPreparation>=BusinessStateReplication.LastDecode&&BusinessStateReplication.LastValidation>=0,"Background preparation has separate decode, validation and aggregate timings");
            Reject<InvalidOperationException>(prepared.Commit,"Prepared result is consumed exactly once");
            BusinessStateReplication.Apply(State(3));
            Check(BusinessStateReplication.ValidationCalls==2,"Public direct Apply(state) retains pure validation");
            var bad=State(4);bad.People[0].StartHour=float.NaN;
            Reject<ArgumentException>(()=>BusinessStateReplication.Apply(bad),"Direct apply rejects malformed floating-point fields");
            Reset();foreach(var part in Events(State(),97))BusinessStateReplication.Apply(part);
            Check(BusinessStateReplication.NativeCommits==1&&BusinessStateReplication.ValidationCalls==1,"Synchronous event compatibility path commits one validated result");
            Check(!BusinessStateReplication.LastBackground,"Synchronous preparation is distinguished in profile");
        }
        private static void CancellationAndEpochs()
        {
            Reset();var input=Detach(Events(State()));using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Reject<OperationCanceledException>(()=>BusinessStateReplication.PrepareManaged(input,cancellation.Token),"Pre-cancelled work never starts");
            Check(BusinessStateReplication.ValidationCalls==0,"Cancellation precedes decode/validation");
            using var midway=new CancellationTokenSource();BusinessStateReplication.OnValidate=midway.Cancel;
            Reject<OperationCanceledException>(()=>Task.Run(()=>BusinessStateReplication.PrepareManaged(input,midway.Token)).GetAwaiter().GetResult(),"Cancellation during managed validation cannot publish a prepared result");
            Check(BusinessStateReplication.NativeCommits==0&&BusinessStateReplication.Reports==0,"Cancellation never enters native transaction or diagnostic callbacks");
            BusinessStateReplication.OnValidate=null;var prepared=Prepare(input);BusinessStateReplication.Reset();
            Reject<InvalidOperationException>(prepared.Commit,"Reset invalidates already completed preparation");
            Reject<InvalidOperationException>(Prepare(input).Commit,"Input detached before reset cannot be revived by decoding afterward");
            Check(BusinessStateReplication.NativeCommits==0&&BusinessStateReplication.LastCaptureHash=="","Old generation cannot touch a reloaded world");
            Reset();prepared=Prepare(Detach(Events(State())));foreach(var part in Events(State(2)))BusinessStateReplication.Apply(part);
            var hash=BusinessStateReplication.LastCaptureHash;Reject<InvalidOperationException>(prepared.Commit,"Detached older sequence cannot commit after newer state");
            Check(BusinessStateReplication.LastCaptureHash==hash&&BusinessStateReplication.NativeCommits==1,"Stale sequence preserves newest committed state and hash");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessStateReplication.ThrowNative=true;
            Reject<InvalidOperationException>(prepared.Commit,"Native failure propagates to final ACK owner");
            Check(BusinessStateReplication.LastCaptureHash=="","Failed native transaction cannot publish successful checksum");
            BusinessStateReplication.ThrowNative=false;Reject<InvalidOperationException>(prepared.Commit,"Failed native result cannot be retried in place");
            Reset();prepared=Prepare(Detach(Events(State())));BusinessStateReplication.ResetDuringNative=true;
            Reject<InvalidOperationException>(prepared.Commit,"Reentrant reset during callback invalidates transaction success");
            Check(BusinessStateReplication.LastCaptureHash=="","Old checksum cannot be restored after callback reset");
        }
        private static void ValidationAndFraming()
        {
            foreach(string kind in new[]{"duplicatePerson","duplicateVenue","duplicateStaff","duplicateProfit","menu","itemMetadata","capacity"}){
                Reset();var bad=State();var venue=bad.Venues[0];
                if(kind=="duplicatePerson")bad.People=new[]{bad.People[0],bad.People[0]};
                if(kind=="duplicateVenue")bad.Venues=new[]{venue,venue};
                if(kind=="duplicateStaff")venue.Staff=new[]{"person","person"};
                if(kind=="duplicateProfit")venue.Profits=new[]{new[]{1,2},new[]{1,3}};
                if(kind=="menu")venue.Menu=new[]{new BusinessMenuItem{RecipeId="recipe",Price=-1}};
                if(kind=="itemMetadata")venue.Fridge.Items=new[]{new SharedItemStack{Type="item",Items=new[]{new[]{1,2,3}}}};
                if(kind=="capacity")venue.Fridge.MaxItems=-1;
                var input=Detach(Events(bad));Reject<ArgumentException>(()=>Prepare(input),"Pure background validation rejects "+kind);
                Check(BusinessStateReplication.NativeCommits==0&&BusinessStateReplication.Reports==0,"Malformed DTO cannot reach native state: "+kind);
            }
            Reset();var mismatch=Detach(Events(State(),24000,2));Reject<InvalidDataException>(()=>Prepare(mismatch),"Envelope and decoded sequences must match");
            Reset();var corrupt=Detach(Events(State()));corrupt.Bytes[0]^=1;Reject<InvalidDataException>(()=>Prepare(corrupt),"Encoded checksum is checked before commit");
            Reset();var parts=Events(State(),97);Check(parts.Length>2,"Fixture covers multi-chunk transfers");
            Check(BusinessStateReplication.AcceptChunk(parts[1])==null,"Initial late-join tail is ignored");
            BusinessStateReplication.AcceptChunk(parts[0]);Reject<InvalidDataException>(()=>BusinessStateReplication.AcceptChunk(parts[2]),"Missing chunk within an active batch fails closed");
            Reset();BusinessStateReplication.AcceptChunk(parts[0]);Time.Now=31;Reject<InvalidDataException>(()=>BusinessStateReplication.AcceptChunk(parts[1]),"Assembler retains thirty-second bound");
            Reset();Prepare(Detach(Events(State()))).Commit();Check(BusinessStateReplication.AcceptChunk(parts[0])==null,"Already applied sequence is still ignored");
        }
        public static int Main()
        {
            try{OwnershipAndCompatibility();CancellationAndEpochs();ValidationAndFraming();Console.WriteLine("PASS BusinessPreparationTests: "+checks+" assertions; production managed preparation, native-thread boundary, cancellation, epochs and wire validation.");return 0;}
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
