using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    // Service-owned credentials. Never serialize this store into a wire message
    // or native world save. No password or reversible password key is retained.
    public sealed class AccountStore : IDisposable
    {
        private sealed class StoredAccount
        {
            public AccountProfile Profile { get; set; }=new AccountProfile();
            public AccountVerifier Verifier { get; set; }
        }
        private sealed class StoreFile
        {
            public int SchemaVersion { get; set; }=1;
            public string ServerId { get; set; }="";
            public StoredAccount[] Accounts { get; set; }=Array.Empty<StoredAccount>();
        }
        private readonly string path;
        private byte[] dummyKey;
        private readonly Dictionary<string,StoredAccount> accounts=new Dictionary<string,StoredAccount>(StringComparer.Ordinal);
        public RSA IdentityKey { get; }
        public AccountServerIdentity Identity { get; }
        public AccountStore(string root)
        {
            root=Path.GetFullPath(root);Directory.CreateDirectory(root);
            path=Path.Combine(root,"accounts.json");string keyPath=Path.Combine(root,"account-identity.pkcs8");
            IdentityKey=RSA.Create();
            if(File.Exists(keyPath)){
                byte[] bytes=ReadBounded(keyPath,8192);
                try{IdentityKey.ImportPkcs8PrivateKey(bytes,out int read);if(read!=bytes.Length||IdentityKey.KeySize!=3072)throw new InvalidDataException("Invalid account server identity.");dummyKey=SHA256.HashData(bytes);}
                finally{CryptographicOperations.ZeroMemory(bytes);}
            } else {
                if(File.Exists(path))throw new InvalidDataException("Account identity key is missing; refusing to replace pinned identity.");
                IdentityKey.KeySize=3072;byte[] bytes=IdentityKey.ExportPkcs8PrivateKey();
                try{dummyKey=SHA256.HashData(bytes);WriteAtomic(keyPath,bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
            }
            Identity=AccountAuthentication.Identity(IdentityKey);
            if(!File.Exists(path)){Persist();return;}
            var file=JsonSerializer.Deserialize<StoreFile>(ReadBounded(path,4*1024*1024),DedicatedProtocol.Json);
            if(file==null||file.SchemaVersion!=1||file.ServerId!=Identity.ServerId||file.Accounts==null||file.Accounts.Length>AccountProtocol.MaxAccounts)
                throw new InvalidDataException("Invalid account credential store.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in file.Accounts){
                if(item?.Profile==null||!Guid.TryParseExact(item.Profile.AccountId,"N",out _)||!ids.Add(item.Profile.AccountId)||
                    AccountAuthentication.NormalizeUsername(item.Profile.Username)!=item.Profile.Username||
                    !DedicatedProtocol.Text(item.Profile.AppearanceId,128,true)||accounts.ContainsKey(item.Profile.Username))throw new InvalidDataException("Invalid account record.");
                AccountAuthentication.ValidateVerifier(item.Verifier);accounts.Add(item.Profile.Username,item);
            }
        }
        public bool TryGet(string username,out AccountProfile profile,out AccountVerifier verifier)
        {
            if(accounts.TryGetValue(AccountAuthentication.NormalizeUsername(username),out var record))
            {profile=Copy(record.Profile);verifier=record.Verifier;return true;}
            profile=null;verifier=null;return false;
        }
        public bool ContainsId(string id)=>accounts.Values.Any(v=>v.Profile.AccountId==id);
        public AccountVerifier FakeVerifier(string username)
        {
            username=AccountAuthentication.NormalizeUsername(username);
            using var mac=new HMACSHA256(dummyKey);
            byte[] Derive(string kind)=>mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(kind+":"+username));
            return new AccountVerifier {Salt=Derive("salt"),Iterations=AccountAuthentication.KdfIterations,StoredKey=Derive("stored"),ServerKey=Derive("server")};
        }
        public AccountProfile Register(string username,AccountVerifier verifier)
        {
            username=AccountAuthentication.NormalizeUsername(username);AccountAuthentication.ValidateVerifier(verifier);
            if(accounts.Count>=AccountProtocol.MaxAccounts||accounts.ContainsKey(username))throw new InvalidOperationException("Account cannot be created.");
            var record=new StoredAccount {Profile=new AccountProfile {AccountId=Guid.NewGuid().ToString("N"),Username=username},Verifier=verifier};
            accounts.Add(username,record);
            try{Persist();}catch{accounts.Remove(username);throw;}
            return Copy(record.Profile);
        }
        public AccountProfile SetAppearance(string accountId,string appearanceId)
        {
            if(!DedicatedProtocol.Text(appearanceId,128))throw new ArgumentException("Invalid appearance.");
            var record=accounts.Values.SingleOrDefault(v=>v.Profile.AccountId==accountId)??throw new InvalidOperationException("Unknown account.");
            string old=record.Profile.AppearanceId;record.Profile.AppearanceId=appearanceId;
            try{Persist();}catch{record.Profile.AppearanceId=old;throw;}
            return Copy(record.Profile);
        }
        public void ValidateLedgerAccounts(AccountWorldState ledger)
        {foreach(var state in ledger.Accounts)if(!ContainsId(state.AccountId))throw new InvalidDataException("Ledger contains an unregistered account.");}
        private static AccountProfile Copy(AccountProfile value)=>new AccountProfile {AccountId=value.AccountId,Username=value.Username,AppearanceId=value.AppearanceId};
        private void Persist()=>WriteAtomic(path,JsonSerializer.SerializeToUtf8Bytes(new StoreFile {ServerId=Identity.ServerId,Accounts=accounts.Values.OrderBy(x=>x.Profile.Username,StringComparer.Ordinal).ToArray()},DedicatedProtocol.Json));
        public static byte[] ReadBounded(string path,int max)
        {var info=new FileInfo(path);if(!info.Exists||info.Length<1||info.Length>max||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid account persistence file.");return File.ReadAllBytes(path);}
        public static void WriteAtomic(string path,byte[] bytes)
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}File.Move(temporary,path,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void Dispose(){IdentityKey.Dispose();if(dummyKey!=null)CryptographicOperations.ZeroMemory(dummyKey);}
    }
}
