using System;
using System.Collections.Generic;
using NivalisTogether.Dedicated;

public static class CraftMealGhostIndexTests
{
    private static int checks;
    private static readonly IntPtr Manager=new IntPtr(101),Registry=new IntPtr(201),Foreign=new IntPtr(301);
    private static IntPtr Pointer(int value)=>new IntPtr(value);
    private static void Check(bool condition,string message)
    { checks++;if(!condition)throw new Exception(message); }
    private static bool Has(CraftMealGhostIndex index,string id,int pointer)
    {
        foreach(var candidate in index.Snapshot())if(candidate.Key==id&&candidate.Value==Pointer(pointer))return true;
        return false;
    }

    public static int Run()
    {
        checks=0;BoundaryTests();CallbackTests();SnapshotTests();CleanupDriverTests();return checks;
    }

    private static void BoundaryTests()
    {
        var index=new CraftMealGhostIndex();
        Check(index.Count==0&&index.Snapshot().Length==0,"new index has no native candidates");
        index.Observe(Manager,"before-authority",Pointer(1),true);
        Check(index.Count==0,"callbacks before a bound authority cannot create candidates");
        Check(!index.Begin(IntPtr.Zero,Registry,"city"),"absent manager never requests native bootstrap");
        Check(!index.Begin(Manager,IntPtr.Zero,"city"),"absent registry never requests native bootstrap");
        Check(index.Begin(Manager,Registry,"city"),"first valid boundary requests one bootstrap");
        index.Observe(Manager,"meal",Pointer(1),true);
        Check(!index.Begin(Manager,Registry,"city")&&Has(index,"meal",1),"same manager registry and scene retain observations without bootstrap");
        Check(index.Begin(Manager,Registry,"apartment")&&index.Count==0,"scene change discards previous scene candidates and requests bootstrap");
        index.Observe(Manager,"apartment-meal",Pointer(2),true);
        Check(!index.Begin(Manager,Registry,"apartment")&&index.Count==1,"same new scene bootstraps only once");
        Check(index.Begin(Manager,Pointer(202),"apartment")&&index.Count==0,"same-count registry replacement requires fresh bootstrap");
        index.Observe(Manager,"new-registry-meal",Pointer(3),true);
        Check(index.Begin(Foreign,Pointer(202),"apartment")&&index.Count==0,"world manager replacement invalidates an otherwise matching boundary");
        index.Observe(Foreign,"new-world-meal",Pointer(4),true);
        index.Invalidate(Manager);
        Check(Has(index,"new-world-meal",4)&&!index.Begin(Foreign,Pointer(202),"apartment"),"old world invalidation cannot clear the current world");
        index.Invalidate(Foreign);
        Check(index.Count==0&&index.Begin(Foreign,Pointer(202),"apartment"),"matching invalidation requires exactly a fresh bootstrap");
        index.Observe(Foreign,"meal",Pointer(5),true);index.Clear();
        Check(index.Count==0,"clear removes retained candidates");
        Check(index.Begin(Foreign,Pointer(202),"apartment"),"clear also invalidates the bootstrap boundary");
        index.Observe(Foreign,"meal",Pointer(6),true);
        Check(!index.Begin(IntPtr.Zero,Pointer(202),"apartment")&&index.Count==0,"lost manager clears previously live candidates");
        Check(index.Begin(Foreign,Pointer(202),"apartment"),"manager recovery bootstraps again");
        index.Observe(Foreign,"meal",Pointer(7),true);
        Check(!index.Begin(Foreign,IntPtr.Zero,"apartment")&&index.Count==0,"lost registry clears previously live candidates");
        Check(index.Begin(Foreign,Pointer(202),"apartment"),"registry recovery bootstraps again");
    }

    private static void CallbackTests()
    {
        var index=new CraftMealGhostIndex();index.Begin(Manager,Registry,"city");
        index.Observe(Manager,"meal",Pointer(10),true);
        Check(index.Count==1&&Has(index,"meal",10),"meal registration records identity and native pointer");
        index.Observe(Manager,"meal",Pointer(10),true);
        Check(index.Count==1,"duplicate registration is idempotent");
        index.Observe(Manager,"meal",Pointer(11),true);
        Check(index.Count==1&&Has(index,"meal",11)&&!Has(index,"meal",10),"pooled same-ID replacement updates pointer without a count change");
        index.Forget(Manager,"meal",Pointer(10));
        Check(Has(index,"meal",11),"stale deregistration cannot remove a newer pooled object");
        index.Forget(Foreign,"meal",Pointer(11));
        index.Observe(Foreign,"meal",Pointer(99),true);
        index.Observe(Foreign,"meal",Pointer(99),false);
        Check(index.Count==1&&Has(index,"meal",11),"foreign manager cannot replace or remove a current candidate");
        index.Forget(Manager,"missing",Pointer(11));index.Forget(Manager,null,Pointer(11));
        Check(Has(index,"meal",11),"unknown and null deregistrations leave current identities intact");
        index.Observe(Manager,"",Pointer(12),true);index.Observe(Manager,null,Pointer(12),true);
        index.Observe(Manager,"zero-pointer",IntPtr.Zero,true);
        Check(index.Count==1,"invalid IDs and zero native pointers never become candidates");
        index.Observe(Manager,"not-a-meal",Pointer(13),false);
        Check(index.Count==1,"ordinary nonmeal registration is ignored");
        index.Observe(Manager,"meal",Pointer(14),false);
        Check(index.Count==0,"same-ID nonmeal replacement removes the former meal candidate");
        index.Observe(Manager,"meal",Pointer(15),true);index.Observe(Manager,"Meal",Pointer(16),true);
        Check(index.Count==2&&Has(index,"meal",15)&&Has(index,"Meal",16),"ghost identity keys retain ordinal case sensitivity");
        index.Forget(Manager,"meal",Pointer(15));
        Check(index.Count==1&&Has(index,"Meal",16),"exact expected-pointer deregistration removes only its own identity");
        index.Forget(Manager,"Meal",Pointer(16));
        Check(index.Count==0,"all matching deregistrations empty the retained index");
        Check(!index.Begin(Manager,Registry,"city"),"empty candidate population does not trigger another whole-world bootstrap");
    }

    private static void SnapshotTests()
    {
        var index=new CraftMealGhostIndex();index.Begin(Manager,Registry,"city");
        index.Observe(Manager,"a",Pointer(1),true);index.Observe(Manager,"b",Pointer(2),true);
        var first=index.Snapshot();var second=index.Snapshot();
        Check(first.Length==2&&second.Length==2&&index.Count==2,"taking snapshots retains every candidate for future authority frames");
        Check(!ReferenceEquals(first,second),"each snapshot owns an independent array");
        int visited=0;
        foreach(var candidate in first){
            visited++;index.Forget(Manager,candidate.Key,candidate.Value);
            index.Observe(Manager,"new-"+candidate.Key,Pointer(10+visited),true);
        }
        Check(visited==2&&index.Count==2,"callbacks may alter the index while a fixed snapshot is enumerated");
        Check(first[0].Equals(second[0])&&first[1].Equals(second[1]),"callback changes leave older snapshots stable");
        Check(Has(index,"new-a",11)||Has(index,"new-a",12),"callback-added candidates survive for the next frame");
        Check(!Has(index,"a",1)&&!Has(index,"b",2),"callback-removed candidates are absent from the next snapshot");
        var writable=index.Snapshot();writable[0]=new KeyValuePair<string,IntPtr>("external-overwrite",Pointer(999));
        Check(index.Count==2&&!Has(index,"external-overwrite",999),"editing a returned snapshot cannot change internal identities");
    }

    private static void CleanupDriverTests()
    {
        // This facade tests the lifecycle index's consumer contract. Canonical
        // lookup and native deletion themselves remain adapter/live concerns.
        var world=new NativeWorld();
        world.Register("pre-authority-meal",1);world.Register("chair",2,false);
        Check(world.Index.Count==0,"registrations that predate authority are not assumed to have been tracked");
        world.Apply(Set("pre-authority-meal"));
        Check(world.Bootstraps==0&&world.Deletes==0,"inactive authority never scans or deletes native objects");
        world.Authority=true;world.Apply(Set("pre-authority-meal"));
        Check(world.Bootstraps==1&&world.Index.Count==1,"first authority frame bootstraps existing meals and excludes nonmeals");
        Check(world.Deletes==0&&world.Canonical.ContainsKey("pre-authority-meal"),"currently authoritative meal is retained without deletion");
        world.Apply(Set("pre-authority-meal"));
        Check(world.Bootstraps==1&&world.Index.Count==1,"unchanged authority frames retain candidates without rescanning the native world");
        world.Apply(Set());
        Check(world.Deletes==1&&!world.Canonical.ContainsKey("pre-authority-meal")&&world.Index.Count==0,"previously authoritative meal remains discoverable when it later becomes extra");
        Check(world.Canonical.ContainsKey("chair"),"cleanup never targets unrelated nonmeal objects");

        world.Register("pooled",3);int count=world.Canonical.Count;
        world.Register("pooled",4);world.Index.Forget(Manager,"pooled",Pointer(3));
        Check(world.Canonical.Count==count&&Has(world.Index,"pooled",4),"same-count native replacement is observed without roster-size invalidation");
        world.Apply(Set("pooled"));
        Check(world.Bootstraps==1&&world.Deletes==1,"replacement preservation does not require another world scan");
        world.Apply(Set());
        Check(world.Deletes==2&&!world.Canonical.ContainsKey("pooled"),"new pooled pointer can later be removed while stale callback was ignored");

        world.Register("missing",5);world.Register("replaced-without-callback",6);
        world.Register("changed-type",7);world.Register("invalid",8);
        world.Canonical.Remove("missing");
        world.Canonical["replaced-without-callback"]=new NativeGhost(9,true,true);
        world.Canonical["changed-type"]=new NativeGhost(7,false,true);
        world.Canonical["invalid"]=new NativeGhost(8,true,false);
        int deletesBefore=world.Deletes;world.Apply(Set());
        Check(world.Deletes==deletesBefore,"canonical missing replaced nonmeal and invalid objects all skip deletion");
        Check(world.Canonical["replaced-without-callback"].Pointer==Pointer(9),"stale candidate cannot delete an unobserved replacement sharing its ID");
        Check(world.Canonical.ContainsKey("changed-type")&&world.Canonical.ContainsKey("invalid"),"type or validity mismatch leaves canonical native objects untouched");

        world.Register("race",10);bool replaced=false;
        world.Apply(Set(),candidate=>{if(candidate.Key=="race"){
            replaced=true;world.Register("race",11);
        }});
        Check(replaced&&world.Deletes==deletesBefore&&Has(world.Index,"race",11),"replacement after snapshot creation survives canonical pointer recheck");
        world.Apply(Set("race"));
        Check(world.Deletes==deletesBefore&&Has(world.Index,"race",11),"new replacement remains indexed through authoritative frames");
        world.Apply(Set());
        Check(world.Deletes==deletesBefore+1&&!world.Canonical.ContainsKey("race"),"retained replacement is cleaned up only after later becoming extra");
    }

    private static HashSet<string> Set(params string[] ids)=>new HashSet<string>(ids,StringComparer.Ordinal);
    private sealed class NativeGhost
    {
        internal readonly IntPtr Pointer;internal readonly bool IsMeal,Valid;
        internal NativeGhost(int pointer,bool isMeal,bool valid){Pointer=CraftMealGhostIndexTests.Pointer(pointer);IsMeal=isMeal;Valid=valid;}
    }
    private sealed class NativeWorld
    {
        internal readonly CraftMealGhostIndex Index=new CraftMealGhostIndex();
        internal readonly Dictionary<string,NativeGhost> Canonical=new Dictionary<string,NativeGhost>(StringComparer.Ordinal);
        internal bool Authority;internal int Bootstraps,Deletes;
        internal void Register(string id,int pointer,bool isMeal=true)
        { Canonical[id]=new NativeGhost(pointer,isMeal,true);Index.Observe(Manager,id,Pointer(pointer),isMeal); }
        internal void Apply(HashSet<string> authoritative,Action<KeyValuePair<string,IntPtr>> beforeResolve=null)
        {
            if(!Authority)return;
            if(Index.Begin(Manager,Registry,"city")){
                Bootstraps++;foreach(var item in Canonical)Index.Observe(Manager,item.Key,item.Value.Pointer,item.Value.IsMeal);
            }
            foreach(var candidate in Index.Snapshot()){
                if(authoritative.Contains(candidate.Key))continue;
                beforeResolve?.Invoke(candidate);
                if(!Canonical.TryGetValue(candidate.Key,out var ghost)||ghost.Pointer!=candidate.Value||!ghost.IsMeal||!ghost.Valid)continue;
                Deletes++;Canonical.Remove(candidate.Key);Index.Forget(Manager,candidate.Key,candidate.Value);
            }
        }
    }
}
