using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    public static class AccountProtocol
    {
        public const string Migration = "legacy-escrow-fresh-accounts-v1", LegacyOwnerId = "legacy-escrow";
        public const int MaxAccounts = 1024, MaxLedgerBytes = 64 * 1024 * 1024, MaxInventoryBytes = 4 * 1024 * 1024;
        public static string RoomProof(ServerHello hello,AuthBegin begin,string roomPassword)=>DedicatedProtocol.Mac(roomPassword??"",
            "account-room|"+hello.SessionId+"|"+hello.Nonce+"|"+begin.Mode+"|"+begin.Username+"|"+Convert.ToBase64String(begin.ClientNonce)+"|"+DedicatedProtocol.Version+"|"+DedicatedProtocol.BuildId);
        public static void Validate(AccountWorldState ledger, string worldId, long revision)
        {
            if(ledger==null||ledger.SchemaVersion!=1||ledger.WorldId!=worldId||ledger.Revision!=revision||revision<0||
                ledger.Migration!=Migration||ledger.Accounts==null||ledger.Accounts.Length>MaxAccounts||ledger.Owners==null||ledger.Owners.Length>8192||
                !DedicatedProtocol.Text(ledger.StarterPropertyId,128,true))throw new InvalidDataException("Invalid account ledger.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            var boatIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var account in ledger.Accounts){
                Validate(account);
                if(!ids.Add(account.AccountId))throw new InvalidDataException("Duplicate account state.");
                if(account.Boat!=null&&!boatIds.Add(account.Boat.BoatId))throw new InvalidDataException("Duplicate account boat.");
            }
            if(ledger.DialogueOwnerAccountId==null||(ledger.DialogueOwnerAccountId.Length>0&&!ids.Contains(ledger.DialogueOwnerAccountId)))
                throw new InvalidDataException("Invalid persistent dialogue owner.");
            if (ledger.SpawnLocation != null) PlayerMapLocation.Validate(ledger.SpawnLocation);
            if(ledger.LegacyEscrow!=null&&(ledger.LegacyEscrow.Inventory.ValueKind!=JsonValueKind.Array||
                Encoding.UTF8.GetByteCount(ledger.LegacyEscrow.Inventory.GetRawText())>MaxInventoryBytes))throw new InvalidDataException("Invalid legacy escrow inventory.");
            var properties=new HashSet<string>(StringComparer.Ordinal);
            foreach(var owner in ledger.Owners)
                if(owner==null||(owner.Kind!="apartment"&&owner.Kind!="business")||!DedicatedProtocol.Text(owner.PropertyId,128)||
                    !properties.Add(owner.PropertyId)||owner.PropertyId==ledger.StarterPropertyId||
                    (owner.OwnerAccountId!=LegacyOwnerId&&!ids.Contains(owner.OwnerAccountId)))throw new InvalidDataException("Invalid property ownership.");
        }
        public static void Validate(AccountState state)
        {
            if(state==null||!Guid.TryParseExact(state.AccountId,"N",out _)||state.StateRevision<0||
                !DedicatedProtocol.Text(state.AppearanceId,128,true)||state.Inventory.ValueKind!=JsonValueKind.Array||
                Encoding.UTF8.GetByteCount(state.Inventory.GetRawText())>MaxInventoryBytes)
                throw new InvalidDataException("Invalid account state.");
            if (state.Location != null) PlayerMapLocation.Validate(state.Location);
            if (state.Boat != null)
            {
                BoatProtocol.Validate(state.Boat);
                if (state.Boat.OwnerAccountId != state.AccountId) throw new InvalidDataException("Invalid account boat owner.");
            }
        }
    }
    public sealed class AppearanceOption { public string Id { get; set; }=""; public string Name { get; set; }=""; }
    public sealed class AccountAppearanceCatalog { public AppearanceOption[] Options { get; set; }=Array.Empty<AppearanceOption>(); }
    public sealed class AccountState
    {
        // Older ledgers omit this field. The worker initializes only location;
        // account assets and their original starting grant remain untouched.
        public PlayerMapLocation Location { get; set; }
        // Missing in older ledgers. Boat authority grants only this field once;
        // money, inventory, appearance and the legacy native boat stay separate.
        public PlayerBoatState Boat { get; set; }
        public string AccountId { get; set; }="";
        public long StateRevision { get; set; }
        public int Money { get; set; }
        // Exact SharedItemStack[] JSON: Type, LastUpdate, Items[int[4]]. The
        // worker owns semantic native-item validation; the service bounds bytes.
        public JsonElement Inventory { get; set; }=JsonSerializer.SerializeToElement(Array.Empty<object>());
        public string AppearanceId { get; set; }="";
        public bool Initialized { get; set; }
    }
    public sealed class AccountInventoryState
    {
        public int Money { get; set; }
        public JsonElement Inventory { get; set; }=JsonSerializer.SerializeToElement(Array.Empty<object>());
    }
    public sealed class PropertyOwner
    {
        public string Kind { get; set; }="";
        public string PropertyId { get; set; }="";
        public string OwnerAccountId { get; set; }="";
    }
    public sealed class AccountWorldState
    {
        // Stable initial location; worker residency moves must not move new accounts.
        public PlayerMapLocation SpawnLocation { get; set; }
        public int SchemaVersion { get; set; }=1;
        public string WorldId { get; set; }="";
        public long Revision { get; set; }
        public string StarterPropertyId { get; set; }="";
        public string Migration { get; set; }=AccountProtocol.Migration;
        public string DialogueOwnerAccountId { get; set; }="";
        public AccountInventoryState LegacyEscrow { get; set; }
        public AccountState[] Accounts { get; set; }=Array.Empty<AccountState>();
        public PropertyOwner[] Owners { get; set; }=Array.Empty<PropertyOwner>();
    }
    public sealed class AccountProfile
    {
        public string AccountId { get; set; }="";
        public string Username { get; set; }="";
        public string AppearanceId { get; set; }="";
    }
    public sealed class AccountAuthenticated
    {
        public string AccountId { get; set; }="";
        public string Username { get; set; }="";
        public string AppearanceId { get; set; }="";
        public AppearanceOption[] AppearanceOptions { get; set; }=Array.Empty<AppearanceOption>();
        public byte[] ServerProof { get; set; }=Array.Empty<byte>();
    }
    public sealed class AccountProfileRequest { public string AppearanceId { get; set; }=""; }
    public sealed class AccountRegistrationProof
    {
        public AccountRegistration Registration { get; set; }
        public AccountAuthProof Proof { get; set; }
    }
    public sealed class AccountPreparation
    {
        public string PlayerId { get; set; }="";
        public string AccountId { get; set; }="";
        public string Username { get; set; }="";
        public string AppearanceId { get; set; }="";
    }
    public sealed class AccountDataBegin
    {
        public string TransferId { get; set; }="";
        public string WorldId { get; set; }="";
        public long Revision { get; set; }
        public int Length { get; set; }
        public string Sha256 { get; set; }="";
        // "ledger" is worker-only; "account" carries one client's own state.
        public string Kind { get; set; }="ledger";
    }
}
