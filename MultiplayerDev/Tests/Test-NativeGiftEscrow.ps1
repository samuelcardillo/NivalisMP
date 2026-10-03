$ErrorActionPreference='Stop'
$gameRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path=Join-Path $gameRoot 'MultiplayerDev/Source/PropertyOwnershipReplication.cs'
$tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path))
$nodes=$tree.GetRoot().DescendantNodes()
$names=@('OwnerOf','NativeOwnerOf','ReconcileNativeGifts','Resolve','Canonical','ApplyOwners')
$methods=foreach($name in $names){
 $found=@($nodes|Where-Object {$_.GetType().Name-eq'MethodDeclarationSyntax'-and$_.Identifier.ValueText-eq$name})
 if($found.Count-ne1){throw "Expected one actual method $name"};$found[0].ToFullString()
}
$prefix=@'
using System;
using System.IO;
using System.Collections.Generic;
namespace NativeGiftEscrowTests {
public class BaseProperty {
 public string Guid; public IntPtr Pointer;
 public bool Owned; public Action OnRead;
 public bool PlayerOwned {get{var action=OnRead;OnRead=null;action?.Invoke();return Owned;}}
 public T TryCast<T>() where T:class=>this as T;
}
public sealed class ApartmentAsset:BaseProperty {}
public sealed class Venue:BaseProperty {}
public sealed class Registry:Dictionary<string,BaseProperty> {public IntPtr Pointer=new IntPtr(17);}
public sealed class PropertyManager {
 public static PropertyManager Instance=new PropertyManager();
 public Registry _propertyIdLookup=new Registry();
 public static bool InstanceNotNull()=>Instance!=null;
 public BaseProperty TryProvide(string id)=>_propertyIdLookup.TryGetValue(id,out var value)?value:null;
}
public sealed class PreparedPropertyChange { public string AccountId; public bool NativeStarted; }
public sealed class PropertyOwner {public string Kind,PropertyId,OwnerAccountId;}
public sealed class AccountWorldState { public string StarterPropertyId; public PropertyOwner[] Owners=Array.Empty<PropertyOwner>(); }
public static class AccountProtocol { public const string LegacyOwnerId="legacy-escrow"; }
public static class PropertyOwnershipPolicy { public static bool AccountId(string id)=>Guid.TryParseExact(id,"N",out _); }
public static class Probe {
 private static bool worker=true,configured=true;
 private static readonly string starter="00000000-0000-0000-0000-000000000001";
 private static readonly string a=new string('a',32),b=new string('b',32);
 private static readonly Dictionary<string,string> Owners=new Dictionary<string,string>(StringComparer.Ordinal);
 private static readonly Dictionary<string,PreparedPropertyChange> Reservations=new Dictionary<string,PreparedPropertyChange>(StringComparer.Ordinal);
 private static int checks,next=2;
 private static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
 private static void Reject(Action action,string why){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}catch(InvalidDataException){rejected=true;}Check(rejected,why);}
 private static T Add<T>(bool owned=true) where T:BaseProperty,new(){var v=new T{Guid=new Guid(next++,0,0,new byte[8]).ToString(),Pointer=new IntPtr(next),Owned=owned};PropertyManager.Instance._propertyIdLookup.Add(v.Guid,v);return v;}
 private static PropertyOwner Row(BaseProperty p,string owner)=>new PropertyOwner{PropertyId=p.Guid,Kind=p is Venue?"business":"apartment",OwnerAccountId=owner};
 private static AccountWorldState Ledger(params PropertyOwner[] owners)=>new AccountWorldState{StarterPropertyId=starter,Owners=owners};
 public static int Run(){
  var venue=Add<Venue>();var apartment=Add<ApartmentAsset>();var rentable=Add<ApartmentAsset>(false);var unsupported=Add<BaseProperty>();
  var shared=new ApartmentAsset{Guid=starter,Pointer=new IntPtr(99),Owned=true};PropertyManager.Instance._propertyIdLookup.Add(starter,shared);
  Check(NativeOwnerOf(venue.Guid)==AccountProtocol.LegacyOwnerId,"late native business gift finances use escrow");
  Check(NativeOwnerOf(apartment.Guid)==AccountProtocol.LegacyOwnerId,"late native apartment gift finances use escrow");
  Check(OwnerOf(venue.Guid)==""&&Owners.Count==0,"finance fallback grants no public rights");
  Check(NativeOwnerOf(rentable.Guid)=="","available rental is not a gift");
  Check(NativeOwnerOf(starter)=="","starter never becomes a claimable escrow property");
  Check(NativeOwnerOf(null)=="","empty native identity remains unassigned");
  Reject(()=>NativeOwnerOf("bad-id"),"malformed identity cannot reach escrow");
  Reject(()=>NativeOwnerOf(Guid.NewGuid().ToString()),"unknown canonical identity rejected");
  Reject(()=>NativeOwnerOf(unsupported.Guid),"unsupported native property cannot use fallback");
  var personal=Add<Venue>();Owners[personal.Guid]=a;Check(NativeOwnerOf(personal.Guid)==a,"committed account retained");
  personal.Owned=false;Check(NativeOwnerOf(personal.Guid)==a,"fallback never rewrites committed ownership");personal.Owned=true;
  var priorLegacy=Add<ApartmentAsset>();Owners[priorLegacy.Guid]=AccountProtocol.LegacyOwnerId;
  Check(NativeOwnerOf(priorLegacy.Guid)==AccountProtocol.LegacyOwnerId,"existing escrow retained");
  var reservation=new PreparedPropertyChange{AccountId=b};Reservations[venue.Guid]=reservation;
  Check(NativeOwnerOf(venue.Guid)=="","even pre-native reservation suppresses gift fallback");
  reservation.NativeStarted=true;Check(NativeOwnerOf(venue.Guid)==b,"native-started rental/claim callbacks retain authenticated payer");
  Check(OwnerOf(venue.Guid)=="","pending finance never grants management rights");
  var ledger=Ledger(Row(personal,a),Row(priorLegacy,AccountProtocol.LegacyOwnerId));
  var alias=PropertyManager.Instance._propertyIdLookup;alias.Add("same-reference-alias",apartment);
  Check(ReconcileNativeGifts(ledger),"later native gift recorded in pending ledger");
  Check(ledger.Owners.Length==3,"only one unreserved supported nonstarter native gift added despite alias");
  Check(ledger.Owners[0].OwnerAccountId==a&&ledger.Owners[1].OwnerAccountId==AccountProtocol.LegacyOwnerId,"existing owners preserved");
  Check(ledger.Owners[2].PropertyId==apartment.Guid&&ledger.Owners[2].Kind=="apartment"&&ledger.Owners[2].OwnerAccountId==AccountProtocol.LegacyOwnerId,"gift kind and escrow identity exact");
  Check(OwnerOf(apartment.Guid)==""&&OwnerOf(venue.Guid)=="","capture does not promote pending owners before ACK");
  Check(!ReconcileNativeGifts(ledger)&&ledger.Owners.Length==3,"repeat capture idempotent");
  ApplyOwners(ledger.StarterPropertyId,ledger.Owners);
  Check(OwnerOf(apartment.Guid)==AccountProtocol.LegacyOwnerId,"existing ACK promotion publishes escrow only");
  Check(OwnerOf(venue.Guid)==""&&NativeOwnerOf(venue.Guid)==b,"uncommitted reservation unaffected by unrelated ACK");
  Reservations.Remove(venue.Guid);Check(ReconcileNativeGifts(ledger),"gift left unclaimed enters next paired ledger");
  Check(ledger.Owners[3].PropertyId==venue.Guid&&ledger.Owners[3].Kind=="business","venue kind is retained");
  Check(OwnerOf(venue.Guid)=="","second gift stays unpublished until its own ACK");
  ApplyOwners(ledger.StarterPropertyId,ledger.Owners);Check(OwnerOf(venue.Guid)==AccountProtocol.LegacyOwnerId,"second gift publishes on ACK");
  var ownedOnly=Add<Venue>();Owners[ownedOnly.Guid]=a;Check(!ReconcileNativeGifts(ledger),"never replaces existing committed owner absent from pending ledger");
  var claimedPending=Add<Venue>();var changed=new List<PropertyOwner>(ledger.Owners){Row(claimedPending,b)};ledger.Owners=changed.ToArray();
  Check(!ReconcileNativeGifts(ledger)&&ledger.Owners[4].OwnerAccountId==b,"preserve pending explicit claim even before public ownership");
  var fresh=Add<Venue>();var original=ledger.Owners;
  var invalid=Add<ApartmentAsset>();invalid.Guid=Guid.NewGuid().ToString();
  Reject(()=>ReconcileNativeGifts(ledger),"noncanonical asset fails closed");Check(ReferenceEquals(original,ledger.Owners),"failed reconciliation adds no partial gift rows");
  PropertyManager.Instance._propertyIdLookup.Remove(new Guid(next-1,0,0,new byte[8]).ToString());
  fresh.OnRead=()=>PropertyManager.Instance._propertyIdLookup.Add("mutation",new Venue{Guid=Guid.NewGuid().ToString(),Owned=false});
  Reject(()=>ReconcileNativeGifts(ledger),"native registry mutation during traversal rejected");Check(ReferenceEquals(original,ledger.Owners),"mutation failure leaves ledger untouched");
  var before=PropertyManager.Instance._propertyIdLookup;fresh.OnRead=()=>{var replacement=new Registry{Pointer=new IntPtr(999)};foreach(var pair in before)replacement.Add(pair.Key,pair.Value);PropertyManager.Instance._propertyIdLookup=replacement;};
  Reject(()=>ReconcileNativeGifts(ledger),"registry replacement rejected even with same count");Check(ReferenceEquals(original,ledger.Owners),"replacement failure leaves ledger untouched");
  var max=Ledger();max.Owners=new PropertyOwner[8192];for(int i=0;i<max.Owners.Length;i++)max.Owners[i]=new PropertyOwner{PropertyId=new Guid(i+10000,0,0,new byte[8]).ToString(),Kind="business",OwnerAccountId=a};
  var maxOriginal=max.Owners;Reject(()=>ReconcileNativeGifts(max),"gift cannot exceed owner bound");Check(ReferenceEquals(maxOriginal,max.Owners),"owner bound failure is atomic");
  Reject(()=>ReconcileNativeGifts(Ledger(Row(personal,a),Row(personal,a))),"duplicate pending owner rejected");
  Reject(()=>ReconcileNativeGifts(Ledger((PropertyOwner)null)),"null pending owner rejected");
  Reject(()=>ReconcileNativeGifts(new AccountWorldState{StarterPropertyId=Guid.NewGuid().ToString()}),"wrong starter ledger rejected");
  worker=false;Reject(()=>NativeOwnerOf(fresh.Guid),"client cannot resolve worker escrow attribution");Reject(()=>ReconcileNativeGifts(ledger),"client cannot append gifts");
  worker=true;configured=false;Reject(()=>ReconcileNativeGifts(ledger),"unconfigured worker cannot append gifts");
  return checks;
 }
'@
# The production ApplyOwners method assigns starter after validation.
$prefix=$prefix.Replace('private static readonly string starter=','private static string starter=')
Add-Type -TypeDefinition ($prefix+($methods-join"`n")+'}}')
"Native gift escrow PASS $([NativeGiftEscrowTests.Probe]::Run()) extracted-production assertions; native registry/pointer boundaries simulated, no game process."
