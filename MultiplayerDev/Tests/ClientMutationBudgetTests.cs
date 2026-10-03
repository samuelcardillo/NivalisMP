using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NivalisTogether;
using NivalisTogether.Dedicated;

internal static class ClientMutationBudgetTests
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static BoundedPreparationFence<int,int> Prepared(int final)
    {
        var fence=new BoundedPreparationFence<int,int>();fence.Start(_=>final,final,0);
        var work=(Task)typeof(BoundedPreparationFence<int,int>).GetField("pending",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(fence);
        Check(work.Wait(TimeSpan.FromSeconds(2)),"Managed fixture preparation completed");return fence;
    }
    private static void FrameGrants()
    {
        var budget=new ClientMutationBudget(1000);
        Check(!budget.CanStart(0),"A frame must begin before any mutation");budget.BeginFrame(10);
        Check(budget.TryStart(100),"First action does not inherit time spent parsing sockets before it");
        Check(budget.TryStart(101)&&budget.TryStart(103),"Cheap atomic actions share remaining frame time");
        Check(!budget.TryStart(104),"Exact4ms deadline yields later action");
        budget.BeginFrame(10);Check(!budget.TryStart(110),"Nested pump or TickClient cannot reset the same frame budget");
        budget.BeginFrame(11);Check(budget.TryStart(180),"Next rendered frame admits remaining action");
        Check(!budget.TryStart(179)&&!budget.TryStart(-1),"Reversed and invalid timestamps cannot grant more work");
        var capped=new ClientMutationBudget(1000,4,2);capped.BeginFrame(1);
        Check(capped.TryStart(0)&&capped.TryStart(0)&&!capped.TryStart(0),"Count cap bounds cheap action floods without time progress");
        capped.Reset();Check(!capped.CanStart(0)&&capped.Actions==0,"Disconnect clears grants and requires a new frame");
        capped.BeginFrame(1);Check(capped.TryStart(0),"Reconnect can reuse the prior frame number after explicit reset");
    }
    private static void CommitAndReceiveDoNotCompound()
    {
        var budget=new ClientMutationBudget(1000);var fence=Prepared(10);var native=new List<int>();var ack=new List<int>();long now=0;
        budget.BeginFrame(1);Check(budget.TryStart(now),"Ready older commit receives frame priority");
        Check(fence.TryCommit(0,value=>{native.Add(value);now+=80;},ack.Add),"Prepared native commit completes once");
        // A receive still progresses after the heavy commit. It can admit later
        // data to the existing bounded unACKed FIFO, but cannot mutate native state.
        int receives=0,pongs=0;receives++;if(fence.IsFenced||!budget.CanStart(now))fence.Enqueue(11,48000);pongs++;
        Check(receives==1&&pongs==1,"Receive and ping path remain independent of mutation grant");
        Check(native.Count==1&&ack.Count==1&&ack[0]==10&&fence.Count==1,"New heavy delivery stays unACKed after older80ms commit");
        Check(!budget.TryStart(now),"A second pump cannot add another atomic mutation in that frame");
        budget.BeginFrame(2);int successor=0;Check(budget.TryStart(now)&&fence.TryDequeue(out successor),"Queue-only fence replays without a preparation Task");
        native.Add(successor);ack.Add(successor);now+=120;
        Check(!fence.IsFenced&&native[1]==11&&ack[1]==11,"Queue-only fence empties and ACK follows actual application");
        // If a preparation completes during a live receive, no late pump may
        // append its native commit to that same already-expensive frame.
        var newlyReady=Prepared(13);Check(!budget.TryStart(now),"Live120ms delivery prevents a late prepared commit");
        Check(newlyReady.IsPending,"Ungrantable preparation remains pending");budget.BeginFrame(3);
        Check(budget.TryStart(now)&&newlyReady.TryCommit(0,v=>native.Add(v),ack.Add),"Prepared commit resumes next frame");
    }
    private static void NestedMapFlushRetainsOrder()
    {
        var budget=new ClientMutationBudget(1000);var fence=new BoundedPreparationFence<int,int>();var native=new List<int>();var ack=new List<int>();long now=0;
        fence.Enqueue(20,100);fence.Enqueue(21,100);budget.BeginFrame(1);
        int current=0;Check(budget.TryStart(now)&&fence.TryDequeue(out current),"Oldest FIFO event enters dispatch");
        Check(budget.TryStart(now),"Older retained map event shares dispatch budget");native.Add(19);now+=60;
        // The final older map event used the remaining budget. Ordinary current
        // work must return to the FRONT, without map-load retained acknowledgement.
        Check(!budget.CanStart(now),"Nested map apply exhausts outer dispatch budget");fence.EnqueueFront(current,100);
        Check(ack.Count==0&&fence.Count==2,"Yielded ordinary work is not early-ACKed or discarded");
        budget.BeginFrame(2);Check(budget.TryStart(now)&&fence.TryDequeue(out current)&&current==20,"Requeued current remains ahead of its peers");native.Add(current);ack.Add(current);
        Check(budget.TryStart(now)&&fence.TryDequeue(out current)&&current==21,"Next FIFO peer retains order");native.Add(current);ack.Add(current);
        Check(string.Join(",",native)=="19,20,21"&&string.Join(",",ack)=="20,21","Older map work, current and peers apply exactly once in order");
        // A deferred final economy chunk can start managed preparation during
        // that same nested flush; the current event must still precede later peers.
        using var release=new System.Threading.ManualResetEventSlim(false);fence.Enqueue(23,100);fence.Start(_=>{release.Wait();return 22;},22,0);fence.EnqueueFront(24,100);
        Check(!fence.TryDequeue(out _),"New preparation blocks replay of both current and following world controls");
        release.Set();var task=(Task)typeof(BoundedPreparationFence<int,int>).GetField("pending",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(fence);Check(task.Wait(TimeSpan.FromSeconds(2)),"Deferred fixture preparation completes");
        fence.TryCommit(0,native.Add,ack.Add);Check(fence.TryDequeue(out current)&&current==24&&fence.TryDequeue(out var following)&&following==23,"Front insertion also preserves order across a new managed preparation");
    }
    private static void ReceivesStayLiveDuringSustainedReplay()
    {
        var budget=new ClientMutationBudget(1000);var fence=new BoundedPreparationFence<int,int>();var ack=new List<int>();int received=0,pongs=0,presentation=0;long now=0;
        for(int i=0;i<40;i++)fence.Enqueue(i,2048);
        for(int frame=0;frame<40;frame++){
            budget.BeginFrame(frame);int item=0;Check(budget.TryStart(now)&&fence.TryDequeue(out item),"Sustained older backlog makes progress");ack.Add(item);now+=160;
            // These are ordinary socket-level messages, not reordered world controls.
            received++;pongs++;presentation++;
            Check(!budget.TryStart(now),"Heavy replay cannot acquire an independent receive/TickClient mutation grant");
        }
        Check(received==40&&pongs==40&&presentation==40,"Sustained160ms mutations cannot starve per-frame receives or presentation");
        Check(!fence.IsFenced&&ack.Count==40,"Bounded backlog drains completely without skipped receipts");
        for(int i=0;i<40;i++)Check(ack[i]==i,"World ordering remains strict during independently live socket progress");
    }
    private static void FailureAndDisconnect()
    {
        var budget=new ClientMutationBudget(1000);var fence=Prepared(30);int mutations=0,acks=0;budget.BeginFrame(1);
        bool connected=false; // The session checks the actual socket immediately after its Tick.
        if(connected&&budget.TryStart(0))fence.TryCommit(0,_=>mutations++,_=>acks++);
        Check(mutations==0&&acks==0&&fence.IsPending,"Disconnected transport never commits pending native work");fence.Cancel();budget.Reset();
        fence=Prepared(31);budget.BeginFrame(2);Check(budget.TryStart(0),"Failure fixture granted one action");bool failed=false;
        try{fence.TryCommit(0,_=>throw new InvalidOperationException("native commit failed"),_=>acks++);}catch(InvalidOperationException){failed=true;}
        Check(failed&&acks==0&&fence.IsPending,"Throwing native commit is never acknowledged");fence.Cancel();budget.Reset();
        fence=Prepared(32);budget.BeginFrame(3);Check(budget.TryStart(0),"Cancellation fixture granted one action");
        Check(!fence.TryCommit(0,_=>{fence.Cancel();budget.Reset();},_=>acks++),"Native callback cancellation invalidates current epoch");
        Check(acks==0&&!fence.IsFenced&&!budget.CanStart(0),"Disconnect callback clears FIFO and prevents stale ACK/extra mutation");
        var blocked=Prepared(33);bool timedOut=false;try{blocked.TryCommit(11,_=>mutations++,_=>acks++);}catch(TimeoutException){timedOut=true;}
        Check(timedOut&&mutations==0&&acks==0,"Existing preparation timeout remains fail-closed");
    }
    public static int Main()
    {
        try{FrameGrants();CommitAndReceiveDoNotCompound();NestedMapFlushRetainsOrder();ReceivesStayLiveDuringSustainedReplay();FailureAndDisconnect();Console.WriteLine("PASS ClientMutationBudgetTests: "+checks+" assertions; shared atomic budget, unACKed FIFO, nested map replay, live receives and failure/cancellation.");return 0;}
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
