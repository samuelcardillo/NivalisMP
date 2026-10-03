using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using NivalisTogether.Dedicated;
using NivalisTogether.Server;

public static class MapCatchupProgressTests
{
    private const string Player="11111111111111111111111111111111", Epoch="22222222222222222222222222222222", Next="33333333333333333333333333333333";
    private static int checks;
    private static void Check(bool condition,string why){checks++;if(!condition)throw new Exception(why);}
    private static void Invalid(Action action,string why){checks++;try{action();}catch(InvalidDataException){return;}throw new Exception(why);}
    private static PlayerSceneContext Context(long generation,bool pending,string epoch=Epoch,string player=Player)=>new(){PlayerId=player,Generation=generation,TravelPending=pending,Epoch=pending?epoch:"",Scene=pending?"2_Meridian_Market":"1_Lowtown"};
    private static MapCatchupProgress Progress(long id,bool complete=false,string epoch=Epoch,long generation=10)=>new(){Epoch=epoch,Generation=generation,AppliedDeliveryId=id,Complete=complete};
    private static MapCatchupProgress Request(long id=0){var value=Progress(id);value.RequestBarrier=true;return value;}
    private static MapCatchupDeadline Ready(double settled=225)
    {
        var p=new MapCatchupDeadline();p.ObserveContext(Context(10,true),0,100);
        Check(!p.CanDeferDeliveryTimeout(200),"Native load has no catch-up allowance");
        p.ObserveContext(Context(11,false),settled,180);return p;
    }
    private static void PurePolicy()
    {
        var p=new MapCatchupDeadline();
        Invalid(()=>p.ObserveProgress(Progress(101),1,200),"Unsolicited report rejected");
        p.ObserveContext(Context(10,true),0,100);
        Invalid(()=>p.ObserveProgress(Progress(101),100,200),"Pending native phase cannot activate grace");
        p.ObserveContext(Context(11,false),225,180);
        Check(!p.CanDeferDeliveryTimeout(226),"Settled context requires real applied progress");
        Check(p.ObserveProgress(Progress(101),226,200),"225-second native phase still permits subsequent bounded catch-up");
        Check(p.CanDeferDeliveryTimeout(245.999)&&!p.CanDeferDeliveryTimeout(246),"Twenty-second progress stall remains exact");
        Check(!p.ObserveProgress(Progress(101),240,200),"Duplicate watermark is harmless without renewal");
        Invalid(()=>p.ObserveProgress(Progress(102),246,200),"Late progress cannot revive stalled grace");
        p=Ready();
        foreach(long invalid in new[]{0L,99L,100L,201L})Invalid(()=>p.ObserveProgress(Progress(invalid),226,200),"Only newly issued positive deliveries qualify");
        Invalid(()=>p.ObserveProgress(Progress(101,epoch:Next),226,200),"Foreign epoch rejected");
        Invalid(()=>p.ObserveProgress(Progress(101,generation:11),226,200),"Settled generation cannot replace original pending identity");
        Check(p.ObserveProgress(Progress(120),226,200),"Forward progress accepted");
        Invalid(()=>p.ObserveProgress(Progress(119),227,200),"Regressing applied delivery rejected");
        Invalid(()=>p.ObserveProgress(Progress(120,true),228,200),"Local completion without ordered marker cannot close grace");
        Check(!p.ObserveProgress(Request(120),228,200),"Barrier request at same watermark does not renew progress");
        var marker=p.BeginBarrierRound(228);p.BarrierIssued(marker,200);
        Check(p.ObserveProgress(Progress(200,true),229,200)&&p.CanDeferDeliveryTimeout(229),"Applied marker alone cannot close grace");
        Check(!p.TryConfirm(229,200,out _),"Marker actual receipt must be ACKed");
        Check(p.TryConfirm(229.5,0,out _)&&!p.CanDeferDeliveryTimeout(229.5),"Actual ordered prefix confirmation closes grace");
        Check(!p.ObserveProgress(Progress(201),230,210)&&!p.IsActive&&!p.CanDeferDeliveryTimeout(230),"Trailing confirmed progress is inert without reopening grace");
        p=Ready();
        p.ObserveContext(Context(11,false),300,200);
        for(int i=0;i<7;i++)Check(p.ObserveProgress(Progress(101+i),226+i*19,300),"Increasing progress inside absolute cap");
        Check(p.CanDeferDeliveryTimeout(344.999)&&!p.CanDeferDeliveryTimeout(345),"Repeated settlement cannot extend absolute120-second deadline");
        Invalid(()=>p.ObserveProgress(Progress(150),345,300),"Progress at absolute deadline rejected");
        p.Reset();Check(!p.CanDeferDeliveryTimeout(346),"World reset removes all grace");
        Invalid(()=>p.ObserveProgress(Progress(151),346,300),"Reset rejects old reports");
        p=Ready();p.ObserveProgress(Progress(101),226,200);
        p.ObserveContext(Context(12,true,Next),230,220);
        Check(!p.CanDeferDeliveryTimeout(231),"New native phase removes old grace immediately");
        p.ObserveContext(Context(13,false),400,230);
        Invalid(()=>p.ObserveProgress(Progress(120),401,300),"Earlier transition cannot refresh new phase");
        Check(p.ObserveProgress(Progress(231,epoch:Next,generation:12),401,300),"New worker-issued transition can qualify independently");
        Invalid(()=>p.ObserveContext(Context(14,true,Next),402,300),"Worker cannot reuse same epoch to reset deadline");
        p=Ready();p.ObserveContext(Context(12,false),227,200);
        Invalid(()=>p.ObserveProgress(Progress(101),228,200),"Unrelated settled generation closes old authorization");
        p=Ready();Invalid(()=>p.ObserveContext(Context(12,false,player:Next),230,200),"Other player context rejected");
        Invalid(()=>p.ObserveProgress(Progress(101),double.NaN,200),"Nonfinite clock rejected");
        Check(!p.CanDeferDeliveryTimeout(double.NaN)&&!p.CanDeferDeliveryTimeout(-1),"Invalid clocks cannot grant grace");
    }

    private static void DelayedConfirmation()
    {
        var p=Ready(100);p.ObserveProgress(Request(),101,200);
        var marker=p.BeginBarrierRound(101);p.BarrierIssued(marker,201);
        p.ObserveProgress(Progress(201,true),101.2,220);
        Check(p.TryConfirm(101.3,202,out var confirmation),"Service closes while confirmation is still in transit");
        // The client has not received confirmation and honestly keeps reporting
        // applied packets. The server has already sent those newer deliveries.
        for(int i=0;i<4;i++)
        {
            Check(!p.ObserveProgress(Progress(205+i),102+i,220),"In-flight actual progress is harmless after confirmation "+i);
            Check(!p.IsActive&&!p.CanDeferDeliveryTimeout(102+i),"Trailing progress grants no new grace "+i);
        }
        Check(!p.ObserveProgress(Progress(201,true),106,220),"Repeated exact completion is idempotent after confirmation");
        Check(p.BarrierRound==1&&p.BarrierDeliveryId==201&&!p.NeedsNextBarrier,"Trailing reports cannot mutate round or marker state");
        Check(!p.TryConfirm(107,0,out _),"Trailing reports cannot send another confirmation");
        Invalid(()=>p.ObserveProgress(Request(208),107,220),"Trailing request cannot reopen a barrier");
        Invalid(()=>p.ObserveProgress(Progress(208,true),107,220),"Trailing completion must identify actual confirmed marker");
        Invalid(()=>p.ObserveProgress(Progress(208,epoch:Next),107,220),"Foreign trailing epoch rejected");
        Invalid(()=>p.ObserveProgress(Progress(208,generation:11),107,220),"Foreign trailing generation rejected");
        Invalid(()=>p.ObserveProgress(Progress(221),107,220),"Unissued trailing delivery rejected");
        Invalid(()=>p.ObserveProgress(Progress(100),107,220),"Pre-transition trailing delivery rejected");
        Invalid(()=>p.ObserveProgress(Progress(0),107,220),"Empty trailing progress rejected");
        p.ObserveContext(Context(11,false),107,220);
        Check(!p.ObserveProgress(Progress(208),108,220),"Identical delivered context repeat does not revoke legitimate trailing allowance");
        p.ObserveContext(Context(12,false),109,220);
        Invalid(()=>p.ObserveProgress(Progress(208),110,220),"Newly observed unrelated context revokes old confirmed allowance");
        p=Ready(100);p.ObserveProgress(Request(),101,200);marker=p.BeginBarrierRound(101);p.BarrierIssued(marker,201);
        p.ObserveProgress(Progress(201,true),101.2,220);p.TryConfirm(101.3,0,out _);
        p.ObserveContext(Context(12,true,Next),102,220);
        Invalid(()=>p.ObserveProgress(Progress(208),103,230),"New transition rejects old trailing reports");
        p.Reset();Invalid(()=>p.ObserveProgress(Progress(208),104,230),"Reset rejects old trailing reports");
    }

    private static void BarrierPolicy()
    {
        var p=Ready(100);
        Invalid(()=>p.BeginBarrierRound(101),"Unrequested service barrier rejected");
        Invalid(()=>p.BarrierIssued(new MapCatchupBarrier{Epoch=Epoch,Generation=10,Round=1},200),"Unqueued marker cannot be issued");
        Check(!p.ObserveProgress(Request(),101,200)&&p.BarrierRequested&&!p.CanDeferDeliveryTimeout(101),"Empty backlog requests marker without invented progress/grace");
        Check(!p.ObserveProgress(Request(),101.1,200),"Duplicate empty request cannot refresh liveness");
        var marker=p.BeginBarrierRound(101.2);
        Check(marker.Epoch==Epoch&&marker.Generation==10&&marker.Round==1&&p.BarrierRound==1,"One exact worker-context marker issued");
        Invalid(()=>p.BeginBarrierRound(101.3),"Cannot queue unbounded duplicate rounds");
        Invalid(()=>p.BarrierIssued(new MapCatchupBarrier{Epoch=Next,Generation=10,Round=1},201),"Foreign barrier rejected");
        Invalid(()=>p.BarrierIssued(new MapCatchupBarrier{Epoch=Epoch,Generation=11,Round=1},201),"Foreign barrier generation rejected");
        Invalid(()=>p.BarrierIssued(new MapCatchupBarrier{Epoch=Epoch,Generation=10,Round=2},201),"Future barrier round rejected");
        Invalid(()=>p.BarrierIssued(marker,100),"Unissued marker boundary rejected");
        p.BarrierIssued(marker,201);p.BarrierIssued(marker,201);
        Check(p.BarrierDeliveryId==201,"Identical issuance has no clock side effect");
        Invalid(()=>p.BarrierIssued(marker,202),"Marker delivery identity cannot change");
        Invalid(()=>p.ObserveProgress(Progress(202),101.4,220),"Later progress cannot bypass marker completion report");
        Invalid(()=>p.ObserveProgress(Progress(200,true),101.4,220),"Completion must apply exact marker");
        var both=Request();both.Complete=true;Invalid(()=>p.ObserveProgress(both,101.4,220),"Request and completion are mutually exclusive");
        Check(p.ObserveProgress(Progress(201,true),101.5,220),"Exact marker is application progress");
        Check(p.ObserveProgress(Progress(215),101.8,220),"Later actual application keeps liveness while confirmation is pending");
        Check(!p.ObserveProgress(Progress(201,true),102,220),"Duplicate completion does not renew progress");
        Check(!p.TryConfirm(102,150,out _),"Older unACKed mutation blocks confirmation even if marker was ACKed");
        Check(!p.TryConfirm(102,201,out _),"UnACKed marker blocks confirmation");
        Check(p.TryConfirm(103.2,205,out var confirmation),"Exactly two-second prefix can confirm despite newer outstanding deliveries");
        Check(confirmation.Epoch==Epoch&&confirmation.Generation==10&&confirmation.Round==1&&confirmation.BarrierDeliveryId==201,"Confirmation binds exact marker");
        MapCatchupConfirmation.Validate(confirmation);Check(!p.IsActive,"Confirmed permit is closed");
        Check(!p.TryConfirm(104,0,out _),"Confirmation is one-use");
        Invalid(()=>p.BeginBarrierRound(104),"Confirmed phase cannot manufacture another round");

        p=Ready(100);p.ObserveProgress(Request(),101,200);marker=p.BeginBarrierRound(101);p.BarrierIssued(marker,201);
        p.ObserveProgress(Progress(201,true),103,220);
        Check(!p.TryConfirm(103.001,0,out _)&&p.NeedsNextBarrier&&p.IsActive,"Old drained prefix requests fresh ordered marker without becoming ready");
        var next=p.BeginBarrierRound(103.001);
        Check(next.Round==2&&!p.NeedsNextBarrier&&p.BarrierDeliveryId==0,"Next round resets only marker state");
        Invalid(()=>p.BarrierIssued(marker,220),"Prior-round marker replay rejected");
        p.BarrierIssued(next,230);p.ObserveProgress(Progress(230,true),104,250);
        Check(p.TryConfirm(104.2,231,out var fresh)&&fresh.Round==2,"Fresh newer prefix confirms without requiring globally empty stream");

        p=Ready(100);p.ObserveProgress(Request(),101,200);
        for(int round=1;round<=MapCatchupDeadline.MaximumBarrierRounds;round++)
        {
            double now=101+(round-1)*3;marker=p.BeginBarrierRound(now);p.BarrierIssued(marker,200+round);
            p.ObserveProgress(Progress(200+round,true),now+2.5,300);
            Check(!p.TryConfirm(now+2.5,0,out _)&&p.NeedsNextBarrier,"Stale round remains unconfirmed "+round);
        }
        Invalid(()=>p.BeginBarrierRound(125),"Round budget is finite even with real progress");
        p.Reset();Check(!p.IsActive&&!p.BarrierRequested&&!p.NeedsNextBarrier&&p.BarrierRound==0&&p.BarrierDeliveryId==0,"Reset clears every round field");
        Invalid(()=>p.BarrierIssued(marker,300),"Reset rejects late old marker");

        p=Ready(100);p.ObserveProgress(Request(),101,200);marker=p.BeginBarrierRound(101);p.BarrierIssued(marker,201);
        p.ObserveProgress(Progress(201,true),102,300);
        Invalid(()=>p.TryConfirm(122,0,out _),"Waiting for ACK cannot extend twenty-second progress stall");
        p=Ready(100);p.ObserveProgress(Request(),219,200);marker=p.BeginBarrierRound(219);p.BarrierIssued(marker,201);
        p.ObserveProgress(Progress(201,true),219.5,300);
        Invalid(()=>p.TryConfirm(220,0,out _),"Marker cannot extend absolute120-second settlement cap");
        Invalid(()=>MapCatchupBarrier.Validate(new MapCatchupBarrier{Epoch=Epoch,Generation=10,Round=9}),"Excessive wire round rejected");
        Invalid(()=>MapCatchupConfirmation.Validate(new MapCatchupConfirmation{Epoch=Epoch,Generation=10,Round=1,BarrierDeliveryId=0}),"Confirmation requires actual issued marker ID");
    }

    private const BindingFlags Fields=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Set(object o,string field,object v)=>o.GetType().GetField(field,Fields).SetValue(o,v);
    private static T Get<T>(object o,string field)=>(T)o.GetType().GetField(field,Fields).GetValue(o);
    private static object Invoke(string name,object service,params object[] args)
    {
        try{return typeof(DedicatedService).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Invoke(service,args);}
        catch(TargetInvocationException e) when(e.InnerException!=null){throw e.InnerException;}
    }
    private static void ServiceCreditIsolation()
    {
        // Compile and call the actual service handler/ACK/reset methods without
        // constructing a listener, launching a service or opening a game.
        var type=typeof(DedicatedService);var peerType=type.GetNestedType("Peer",BindingFlags.NonPublic);
        var service=(DedicatedService)FormatterServices.GetUninitializedObject(type);
        var peer=Activator.CreateInstance(peerType,true);var worker=Activator.CreateInstance(peerType,true);
        Set(peer,"Connection",(DedicatedConnection)FormatterServices.GetUninitializedObject(typeof(DedicatedConnection)));
        Set(service,"_ready",true);Set(service,"_worker",worker);
        Set(peer,"Role","player");Set(peer,"Loaded",true);Set(peer,"LastDeliveryId",200L);
        Set(peer,"BulkInFlight",4096);Set(peer,"ControlInFlight",2048);
        var receipts=Get<IDictionary>(peer,"Deliveries");
        var delivery=Activator.CreateInstance(type.GetNestedType("Delivery",BindingFlags.NonPublic),true);
        Set(delivery,"Cost",4096);Set(delivery,"Control",false);receipts.Add(200L,delivery);
        var policy=Get<MapCatchupDeadline>(peer,"MapCatchup");policy.ObserveContext(Context(10,true),0,100);policy.ObserveContext(Context(11,false),1000,180);
        var msg=new WireMessage{Type=MapCatchupProgress.MessageKind,Payload=JsonSerializer.SerializeToElement(Progress(150),DedicatedProtocol.Json)};
        Invoke("Handle",service,peer,msg,DateTime.UnixEpoch.AddSeconds(1001));
        Check(policy.CanDeferDeliveryTimeout(1002),"Actual loaded-player service handler forwards valid bound progress");
        Check(receipts.Count==1&&Get<int>(peer,"BulkInFlight")==4096&&Get<int>(peer,"ControlInFlight")==2048,"Progress grants no delivery credit and removes no receipt");
        Invoke("AcknowledgeWorldEvents",null,peer,new WorldEventAcknowledgement{DeliveryIds=new[]{150L}});
        Check(receipts.Count==1&&Get<int>(peer,"BulkInFlight")==4096,"Old retained ACK remains idempotent and grants no credit");
        Check(!policy.CanDeferDeliveryTimeout(1021),"Repeated ACK does not renew catch-up liveness");
        Invoke("AcknowledgeWorldEvents",null,peer,new WorldEventAcknowledgement{DeliveryIds=new[]{200L}});
        Check(receipts.Count==0&&Get<int>(peer,"BulkInFlight")==0&&Get<int>(peer,"ControlInFlight")==2048,"Ordinary ACK releases only its actual issued credit");
        var other=Activator.CreateInstance(peerType,true);Set(other,"Role","player");Set(other,"Loaded",true);Set(other,"LastDeliveryId",200L);
        Invalid(()=>Invoke("Handle",service,other,msg,DateTime.UnixEpoch.AddSeconds(1002)),"Other authenticated player cannot borrow transition permit");
        Set(worker,"Role","worker");Invalid(()=>Invoke("Handle",service,worker,msg,DateTime.UnixEpoch.AddSeconds(1002)),"Worker role cannot submit client catch-up reports");
        Invoke("ClearDeliveries",null,peer);Check(!policy.CanDeferDeliveryTimeout(1002),"Actual import/disconnect receipt reset clears catch-up permit");
    }
    public static int Run(){PurePolicy();BarrierPolicy();DelayedConfirmation();ServiceCreditIsolation();Console.WriteLine("PASS MapCatchupProgressTests: "+checks+" assertions; policy plus production service handler/credit isolation.");return checks;}
}
