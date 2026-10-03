using System;
using System.IO;
using NivalisTogether.Dedicated;

internal static class SleepConsensusTests
{
    private static int assertions;
    private static string A=Guid.NewGuid().ToString("N"),B=Guid.NewGuid().ToString("N"),C=Guid.NewGuid().ToString("N");
    private static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
    private static void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
    public static int Main()
    {
        try{
            var policy=new SleepConsensus();
            Check(!policy.CanStart,"An empty server must never start sleep.");
            policy.SetConnected(A,true);policy.SetLoaded(A,true);policy.SetConnected(B,true);policy.SetLoaded(B,true);
            policy.SetReady(A,true);Check(!policy.CanStart&&policy.ReadyCount==1&&policy.ParticipantCount==2,"One of two players cannot skip the night.");
            policy.SetReady(A,true);Check(policy.ReadyCount==1&&!policy.CanStart,"Repeated bed intent is idempotent, not an extra vote.");
            policy.SetReady(A,false);Check(policy.ReadyCount==0&&!policy.CanStart,"Cancellation removes consent.");
            policy.SetReady(A,true);policy.SetReady(B,true);Check(policy.CanStart,"Every loaded connected player can authorize sleep.");
            policy.SetConnected(C,true);Check(!policy.CanStart,"A newly authenticated participant blocks sleep while their snapshot loads.");
            Reject(()=>policy.SetReady(C,true),"A pending client cannot contribute a readiness vote.");
            // Snapshot transfer removes recovery presence, which never removes
            // authenticated membership or grants a loaded player body.
            Check(!policy.CanStart&&policy.ParticipantCount==3,"Snapshot transfer leaves pending participant in the consensus.");
            policy.SetLoaded(C,true);Check(!policy.CanStart,"Finishing load does not imply sleep consent.");
            policy.SetReady(C,true);Check(policy.CanStart,"New arrival must explicitly choose sleep.");
            var cohort=policy.Participants;policy.SetConnected(C,false);Check(cohort.Length==3&&policy.ParticipantCount==2&&policy.CanStart,"Captured cohort is independent from live membership changes.");
            policy.SetLoaded(A,false);Check(!policy.CanStart&&!policy.IsReady(A),"Unloading invalidates prior readiness.");
            policy.SetLoaded(A,true);Check(!policy.CanStart,"Reloading cannot restore an old readiness vote.");
            policy.SetReady(A,true);policy.SetReady(B,false);policy.SetConnected(B,false);Check(policy.CanStart,"Leaving nonready player no longer blocks remaining ready player.");
            policy.SetConnected(A,false);Check(!policy.CanStart&&policy.ReadyCount==0&&policy.ParticipantCount==0,"All-player departure cancels waiting without advancing night.");
            policy.SetConnected(A,true);policy.SetLoaded(A,true);Check(!policy.CanStart,"Reconnection cannot inherit departed readiness.");
            policy.SetReady(A,true);policy.ClearReadiness();Check(!policy.CanStart&&policy.ParticipantCount==1,"Finishing or cancelling sleep clears votes, preserves connection.");
            Reject(()=>policy.SetConnected("not-a-guid",true),"Malformed membership is rejected.");
            Reject(()=>policy.SetLoaded(B,true),"Unconnected body cannot be marked loaded.");
            Reject(()=>policy.SetReady(B,true),"Unconnected body cannot be marked ready.");
            policy.Reset();for(int i=0;i<32;i++)policy.SetConnected(Guid.NewGuid().ToString("N"),true);
            Reject(()=>policy.SetConnected(Guid.NewGuid().ToString("N"),true),"Membership is bounded at 32.");
            policy.Reset();Check(policy.ParticipantCount==0&&policy.ReadyCount==0&&!policy.CanStart,"Session reset discards every prior participant.");

            var consent=new SleepBedReadiness{AccountId=C,Scene="1_Lowtown",PropertyId="starter-apartment",Generation=5,X=10,Y=2,Z=20};
            var context=new PlayerSceneContext{PlayerId=A,Scene=consent.Scene,PropertyId=consent.PropertyId,Generation=5};
            var pose=new PlayerPose{PlayerId=A,AccountId=C,Scene=consent.Scene,PropertyId=consent.PropertyId,ContextGeneration=5,Active=true,X=11,Y=2,Z=20};
            Check(consent.Matches(pose,context,0,false),"A fresh authenticated player at their bed stays ready independent of worker map residency.");
            Check(!consent.Matches(pose,context,0,true),"The sleeper's own map operation invalidates readiness.");
            Check(!consent.Matches(pose,context,3.01,false),"Stale position cannot authorize sleep.");
            double previousTick=20,receivedPose=20.01,currentFocusCheck=20.02;
            Check(!consent.Matches(pose,context,previousTick-receivedPose,false),"A previous tick timestamp incorrectly treats a newly received position as future-dated.");
            Check(consent.Matches(pose,context,currentFocusCheck-receivedPose,false),"Focus checks use the current session timestamp so new receive-before-tick poses remain ready.");
            Check(!consent.Matches(pose,context,double.NaN,false),"Nonfinite pose age cannot authorize sleep.");
            pose.Active=false;Check(!consent.Matches(pose,context,0,false),"Inactive sleeper cannot retain readiness.");pose.Active=true;
            pose.AccountId=B;Check(!consent.Matches(pose,context,0,false),"Consent is bound to the authenticated account.");pose.AccountId=C;
            context.Generation++;Check(!consent.Matches(pose,context,0,false),"A changed assigned map generation invalidates old bed consent.");
            pose.ContextGeneration=context.Generation;Check(!consent.Matches(pose,context,0,false),"A fresh new-generation pose cannot reuse old-generation consent.");context.Generation=pose.ContextGeneration=5;
            context.TravelPending=true;Check(!consent.Matches(pose,context,0,false),"Pending travel invalidates consent before the native client leaves.");context.TravelPending=false;
            context.Scene=pose.Scene="2_Midtown";Check(!consent.Matches(pose,context,0,false),"Other district invalidates consent even at numerically identical coordinates.");context.Scene=pose.Scene=consent.Scene;
            context.PropertyId=pose.PropertyId="another-apartment";Check(!consent.Matches(pose,context,0,false),"Other apartment invalidates consent even at identical coordinates.");context.PropertyId=pose.PropertyId=consent.PropertyId;
            pose.X=14;Check(consent.Matches(pose,context,3,false),"Exact four-metre distance and three-second age remain valid.");
            pose.X=14.01f;Check(!consent.Matches(pose,context,0,false),"Walking away from the consenting bed invalidates readiness.");pose.X=11;
            consent.X=float.PositiveInfinity;Check(!consent.Matches(pose,context,0,false),"Invalid bed coordinates cannot authorize sleep.");consent.X=10;
            context.PlayerId=B;Check(!consent.Matches(pose,context,0,false),"Another player's scene assignment cannot validate consent.");context.PlayerId=A;
            Check(consent.Matches(pose,context,0,false),"Valid consent remains reusable while the worker focuses another player's map.");

            var state=new GroupSleepEvent{Sequence=1,Epoch=Guid.NewGuid().ToString("N"),Step=1,Phase="waiting",Participants=new[]{A,B},ReadyPlayers=new[]{A}};
            SleepRecoveryProtocol.ValidateWire(state);Check(true,"Valid partial waiting state is accepted.");
            state.ReadyPlayers=new[]{A,A};Reject(()=>SleepRecoveryProtocol.ValidateWire(state),"Duplicate ready identifiers cannot manufacture unanimous consent.");
            state.ReadyPlayers=new[]{C};Reject(()=>SleepRecoveryProtocol.ValidateWire(state),"Ready identifier must belong to connected cohort.");
            state.ReadyPlayers=Array.Empty<string>();Reject(()=>SleepRecoveryProtocol.ValidateWire(state),"Zero-ready waiting state must use cancellation.");
            state.Phase="cancelled";SleepRecoveryProtocol.ValidateWire(state);Check(true,"Cancellation accepts no ready players.");
            state.Phase="synchronizing";state.NightTransition=true;state.ReadyPlayers=new[]{A,B};SleepRecoveryProtocol.ValidateWire(state);Check(true,"Morning synchronization retains the unanimous cohort.");
            state.Phase="complete";SleepRecoveryProtocol.ValidateWire(state);Check(true,"Completed state passes the same bounded cohort validation.");
            state.Participants=new[]{A,A};Reject(()=>SleepRecoveryProtocol.ValidateWire(state),"Duplicate participants are rejected in completion too.");
            Console.WriteLine("PASS: "+assertions+" sleep consensus and wire assertions; pure policy only, no native process.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
