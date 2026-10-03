$ErrorActionPreference='Stop'
$gameRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourcePath=Join-Path $gameRoot 'MultiplayerDev/Source/SharedEconomyReplication.cs'
$root=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath)).GetRoot()
$nodes=@($root.DescendantNodes())
$owner=@($nodes|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'SharedEconomyReplication'})[0]
$methods=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]})
$names=@('Capture','CaptureVendor','CaptureStock','CaptureNativeItems','TryMatchNativeItems','BoundCapturedVendorStocks')
$production=@(foreach($name in $names){$found=@($methods|Where-Object{$_.Identifier.ValueText -eq $name});if($found.Count -ne 1){throw "Production method missing: $name"};$found[0].ToFullString()}) -join "`n"
$fields=@($owner.Members|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax] -and ($_.ToString().Contains('capturedVendorStocks') -or $_.ToString().Contains('MaxCachedVendor') -or $_.ToString().Contains('MaxStacks ='))}|ForEach-Object ToFullString) -join "`n"
$dtos=@(foreach($name in @('SharedItemStack','SharedVendorStock','SharedEconomyState')){@($nodes|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq $name})[0].ToFullString()}) -join "`n"
$reset=@($methods|Where-Object{$_.Identifier.ValueText -eq 'Reset'})[0]
$clear=@($reset.DescendantNodes()|Where-Object{$_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax] -and $_.ToString() -eq 'capturedVendorStocks.Clear();'})
if($clear.Count -ne 1){throw 'Production Reset does not clear the capture cache.'}
$probe=@'
using System;
using System.Collections.Generic;
using System.Linq;
namespace NivalisTogether.Dedicated {
/*DTO*/
internal static class DedicatedWorldRuntime {internal static bool IsDedicatedServer=true;}
internal sealed class ItemType {internal string Guid="type";internal IntPtr Pointer=new IntPtr(700);}
internal sealed class NativeArray {internal int[][] Rows;internal int Length=>Rows.Length;internal IntPtr Pointer=new IntPtr(600);}
internal sealed class NativeList {internal int _version=1,_size;internal NativeArray _items;internal IntPtr Pointer=new IntPtr(500);}
internal sealed class ItemStack {internal ItemType _type=new ItemType();internal NativeList _instanceData;internal int _lastUpdateTime=10;}
internal sealed class ItemContainer {internal IntPtr Pointer;internal ItemStack[] Stacks;}
internal sealed class Vendor {internal ItemContainer Container;internal float relationshipLevelProgress;internal int lastRelationshipLevelUpDay;}
internal sealed class Inventory {internal ItemContainer Items=new ItemContainer{Pointer=new IntPtr(999)};}
internal sealed class EconomyManager {internal static EconomyManager Instance=new EconomyManager();internal Vendor[] Vendors=Array.Empty<Vendor>();}
internal static class SharedEconomyReplication {
/*FIELDS*/
private static long sequence;
private sealed class EconomyProfile {internal int FastMatchedContainers,FastMatchedInstances;}
private static EconomyProfile activeProfile;
private static readonly Inventory inventory=new Inventory();
private static int captures,checks,failIndex=-1;
private static Inventory PlayerInventory()=>inventory;
private static string VendorKey(Vendor vendor)=>"single";
private static string VendorKey(int index,Vendor vendor)=>"vendor-"+index;
private static void ValidateVendor(SharedVendorStock stock,int index,object ids){if(index==failIndex)throw new InvalidOperationException("Injected failed full capture");}
private static ItemStack[] SnapshotStacks(ItemContainer value)=>value.Stacks;
private static SharedItemStack[] CaptureItems(ItemContainer value){captures++;return value.Stacks.Where(s=>s._instanceData._size>0).Select(s=>new SharedItemStack{Type=s._type.Guid,LastUpdate=s._lastUpdateTime,Items=s._instanceData._items.Rows.Select(row=>(int[])row.Clone()).ToArray()}).ToArray();}
private static class NativeItemReader {
 internal static bool CanMatch=true;
 internal static int Calls,FieldsRead;
 internal struct Snapshot {internal int Version,Count,LastUpdate;internal IntPtr Array;internal bool IsCurrent()=>true;}
 internal static bool TryObserveList(ItemStack stack,NativeList list,out Snapshot value){value=default;return false;}
 internal static NativeArray RootArray(IntPtr pointer)=>throw new Exception("Fallback fixture does not use scoped arrays");
 internal static bool TryMatches(NativeArray array,int count,IntPtr type,int[][] expected,IntPtr pointer=default){
  Calls++;if(count!=array.Length||expected.Length!=count)return false;
  for(int i=0;i<count;i++)for(int field=0;field<4;field++){FieldsRead++;if(array.Rows[i][field]!=expected[i][field])return false;}
  return true;
 }
}
/*METHODS*/
private static void ResetCache(){/*RESET*/}
private static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
private static ItemContainer Container(int pointer,int seed)=>new ItemContainer{Pointer=new IntPtr(pointer),Stacks=new[]{new ItemStack{_instanceData=new NativeList{_size=2,_items=new NativeArray{Rows=new[]{new[]{seed,2,3,4},new[]{5,6,7,8}}}}}}};
private static Vendor VendorFor(ItemContainer container)=>new Vendor{Container=container};
private static void Roster(params Vendor[] vendors){EconomyManager.Instance.Vendors=vendors;}
private static void ReuseAndDrift(){
 ResetCache();captures=NativeItemReader.Calls=NativeItemReader.FieldsRead=0;
 var a=Container(1,1);Roster(VendorFor(a),VendorFor(a));var first=Capture();
 Check(captures==1&&NativeItemReader.Calls==0,"Initial aliases require one fresh capture");
 Check(ReferenceEquals(first.Vendors[0].Stacks,first.Vendors[1].Stacks),"Aliased vendors share only one managed stock capture");
 var second=Capture();Check(captures==1&&NativeItemReader.Calls==1&&NativeItemReader.FieldsRead==8,"Every current field is freshly observed once per aliased container");
 Check(ReferenceEquals(first.Vendors[0].Stacks,second.Vendors[0].Stacks),"Exact match avoids allocating replacement managed rows");
 Check(!ReferenceEquals(first.Vendors[0].Stacks[0].Items[0],a.Stacks[0]._instanceData._items.Rows[0]),"Cached rows own managed copies of source data");
 a.Stacks[0]._lastUpdateTime=123;var stamp=Capture();
 Check(stamp.Vendors[0].Stacks[0].LastUpdate==123&&first.Vendors[0].Stacks[0].LastUpdate==10,"Observed LastUpdate is current without mutating older snapshot");
 Check(ReferenceEquals(stamp.Vendors[0].Stacks[0].Items,first.Vendors[0].Stacks[0].Items),"LastUpdate-only drift reuses immutable item rows");
 Check(!ReferenceEquals(stamp.Vendors[0].Stacks,first.Vendors[0].Stacks),"LastUpdate baseline uses copy-on-write headers");
 for(int field=0;field<4;field++){
  int before=captures;a.Stacks[0]._instanceData._items.Rows[0][field]^=int.MinValue;var changed=Capture();
  Check(captures==before+1,"Drift in native item field triggers fresh capture: "+field);
  Check(changed.Vendors[0].Stacks[0].Items[0][field]==a.Stacks[0]._instanceData._items.Rows[0][field],"Changed field is published: "+field);
 }
 var unsupportedBefore=captures;NativeItemReader.CanMatch=false;Capture();Check(captures==unsupportedBefore+1,"Unsupported native layout falls back to fresh capture");NativeItemReader.CanMatch=true;
 var stale=first.Vendors[0].Stacks[0].Items[0];Check(stale[0]==1&&stale[1]==2&&stale[2]==3&&stale[3]==4,"Later changes never mutate an older published snapshot");
}
private static void ScopeAndLifetime(){
 ResetCache();var a=Container(11,1);var b=Container(12,2);Roster(VendorFor(a),VendorFor(b));Capture();
 var prior=capturedVendorStocks;int before=captures;Capture(false);Check(ReferenceEquals(prior,capturedVendorStocks)&&captures==before,"Capture(false) neither consults nor clears full-roster cache");
 CaptureVendor(VendorFor(a));CaptureNativeItems(a);Check(captures==before+2&&ReferenceEquals(prior,capturedVendorStocks),"Transaction and native inventory captures stay uncached");
 DedicatedWorldRuntime.IsDedicatedServer=false;Capture();Check(captures==before+4&&ReferenceEquals(prior,capturedVendorStocks),"Client full capture remains fresh and does not publish worker cache");DedicatedWorldRuntime.IsDedicatedServer=true;
 Roster(VendorFor(a));Capture();Check(capturedVendorStocks.Count==1&&!capturedVendorStocks.ContainsKey(b.Pointer),"Successful next roster evicts unseen containers");
 before=captures;Roster(VendorFor(b));Capture();Check(captures==before+1,"Returning evicted pointer receives fresh capture");
 var recycled=Container(12,999);before=captures;Roster(VendorFor(recycled));var replacement=Capture();
 Check(captures==before+1&&replacement.Vendors[0].Stacks[0].Items[0][0]==999,"Recycled pointer cannot reuse different native contents");
 var sameContent=Container(12,999);before=captures;int matched=NativeItemReader.Calls;Roster(VendorFor(sameContent));Capture();
 Check(captures==before&&NativeItemReader.Calls==matched+1,"Recycled pointer with equal contents is still freshly verified");
 prior=capturedVendorStocks;var remembered=prior[recycled.Pointer];sameContent.Stacks[0]._instanceData._items.Rows[0][0]=777;
 failIndex=1;Roster(VendorFor(sameContent),VendorFor(a));bool failed=false;try{Capture();}catch(InvalidOperationException){failed=true;}failIndex=-1;
 Check(failed&&ReferenceEquals(prior,capturedVendorStocks)&&ReferenceEquals(remembered,capturedVendorStocks[recycled.Pointer]),"Failed roster does not publish partial new cache");
 Check(remembered[0].Items[0][0]==999,"Failed capture leaves prior snapshots untouched");
 Roster(VendorFor(sameContent));before=captures;Capture();Check(captures==before+1,"Retry after failure reobserves changed native contents");
 ResetCache();Check(capturedVendorStocks.Count==0,"Actual Reset clear statement releases cache");before=captures;Capture();Check(captures==before+1,"First capture after reset is fresh");
}
private static void Bounds(){
 var current=new Dictionary<IntPtr,SharedItemStack[]>();for(int i=0;i<MaxCachedVendorContainers+1;i++)current[new IntPtr(i+1)]=Array.Empty<SharedItemStack>();
 Check(BoundCapturedVendorStocks(current).Count==MaxCachedVendorContainers,"Container-count retention bound is enforced without rejecting data");
 var items=new int[200000][];Array.Fill(items,new int[4]);var large=new[]{new SharedItemStack{Type="type",Items=items}};
 current.Clear();for(int i=0;i<6;i++)current[new IntPtr(i+1)]=large;
 Check(BoundCapturedVendorStocks(current).Count==5,"Aggregate retained item bound skips overflow containers");
 var dense=new SharedItemStack[16384];Array.Fill(dense,new SharedItemStack{Type=new string('x',128),Items=new[]{new int[4]}});
 current.Clear();for(int i=0;i<11;i++)current[new IntPtr(i+1)]=dense;
 var bounded=BoundCapturedVendorStocks(current);Check(bounded.Count<current.Count&&bounded.Count>0,"Estimated managed byte bound applies independently of item count");
 current[new IntPtr(100)]=Array.Empty<SharedItemStack>();bounded=BoundCapturedVendorStocks(current);
 Check(bounded.ContainsKey(new IntPtr(100)),"Oversized candidate does not prevent later small entries from being cached");
 Check(current.Count==12,"Retention bounds never truncate the captured state supplied for publication");
}
public static int Main(){try{ReuseAndDrift();ScopeAndLifetime();Bounds();Console.WriteLine("PASS VendorCaptureCacheProbe: "+checks+" assertions; production capture/match/cache orchestration, native-field spy, lifecycle and managed retention bounds.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
}
'@
$probe=$probe.Replace('/*DTO*/',$dtos).Replace('/*FIELDS*/',$fields).Replace('/*METHODS*/',$production).Replace('/*RESET*/',$clear[0].ToFullString())
$outDir=Join-Path $PSScriptRoot ('vendor-capture-cache-results-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'));[void][IO.Directory]::CreateDirectory($outDir)
$generated=Join-Path $outDir 'VendorCaptureCacheProbe.generated.cs';[IO.File]::WriteAllText($generated,$probe)
$refs=[Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $gameRoot 'dotnet') -Filter '*.dll'){try{[void][Reflection.AssemblyName]::GetAssemblyName($file.FullName)}catch{continue};$refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))}
$options=[Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$compile=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('VendorCaptureCacheProbe').WithOptions($options).AddReferences($refs.ToArray())
$compile=$compile.AddSyntaxTrees([Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($probe,$null,$generated)))
$path=Join-Path $outDir 'VendorCaptureCacheProbe.dll';$stream=[IO.File]::Create($path);try{$result=$compile.Emit($stream)}finally{$stream.Dispose()}
if(!$result.Success){$result.Diagnostics|Where-Object Severity -eq Error|ForEach-Object ToString;throw 'Vendor cache production extraction compilation failed.'}
'{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"Major"}}'|Set-Content -LiteralPath (Join-Path $outDir 'VendorCaptureCacheProbe.runtimeconfig.json')
Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outDir 'source-hashes.json')
& dotnet $path
if($LASTEXITCODE -ne 0){throw 'Vendor cache regression failed.'}
"Evidence: $outDir"
