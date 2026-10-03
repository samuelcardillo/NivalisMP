using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NivalisTogether.Dedicated;

internal static class AccountStoreTests
{
    private static int checks;
    private const string Password = "Store test password only!";
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Throws(Action action, string message)
    { checks++; try { action(); } catch { return; } throw new Exception(message); }
    private static bool Equal(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
    public static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("A unique isolated test directory is required.");
        string root = Path.GetFullPath(args[0]);
        string allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new InvalidOperationException("Test output must be new and inside this test assembly's isolated build directory.");
        Directory.CreateDirectory(root);
        string main = Path.Combine(root, "main");
        AccountProfile first, second;
        AccountVerifier fakeOne, fakeTwo;
        byte[] originalKey, publicKey;
        string serverId;
        var salt = RandomNumberGenerator.GetBytes(32);
        using var keys = AccountAuthentication.DeriveKeys(Encoding.UTF8.GetBytes(Password), salt, AccountAuthentication.KdfIterations);
        var verifier = new AccountVerifier { Salt = salt, Iterations = AccountAuthentication.KdfIterations,
            StoredKey = (byte[])keys.StoredKey.Clone(), ServerKey = (byte[])keys.ServerKey.Clone() };
        using (var store = new AccountStore(main))
        {
            serverId = store.Identity.ServerId; publicKey = store.Identity.PublicKey;
            Check(File.Exists(Path.Combine(main, "account-identity.pkcs8")), "identity file created");
            Check(File.Exists(Path.Combine(main, "accounts.json")), "empty credential store created");
            Check(!store.TryGet("unknown", out var absentProfile, out var absentVerifier) && absentProfile == null && absentVerifier == null, "absent account exposes no credential");
            fakeOne = store.FakeVerifier("missing_user"); fakeTwo = store.FakeVerifier("another_user");
            AccountAuthentication.ValidateVerifier(fakeOne);
            Check(Equal(fakeOne.Salt, store.FakeVerifier("MISSING_USER").Salt), "dummy salt uses normalized username");
            Check(!Equal(fakeOne.Salt, fakeTwo.Salt) && !Equal(fakeOne.StoredKey, fakeTwo.StoredKey) && !Equal(fakeOne.ServerKey, fakeTwo.ServerKey), "dummy account material is account specific");
            Check(!Equal(fakeOne.Salt, fakeOne.StoredKey) && !Equal(fakeOne.Salt, fakeOne.ServerKey), "dummy salt and key domains differ");
            first = store.Register("FIRST_PLAYER", verifier);
            Check(first.Username == "first_player" && Guid.TryParseExact(first.AccountId, "N", out _), "registration stores normalized username and stable opaque ID");
            Check(store.ContainsId(first.AccountId), "registered account ID found");
            Throws(() => store.Register("First_Player", verifier), "normalized duplicate username denied");
            Throws(() => store.Register("invalid name", verifier), "invalid username denied");
            Throws(() => store.Register("new_user", new AccountVerifier()), "invalid verifier denied");
            Check(store.TryGet("FIRST_PLAYER", out var found, out var stored) && found.AccountId == first.AccountId, "case-insensitive user input resolves canonical account");
            Check(Equal(stored.StoredKey, verifier.StoredKey) && Equal(stored.ServerKey, verifier.ServerKey), "credential keys preserved");
            found.AppearanceId = "mutated-returned-profile";
            Check(store.TryGet("first_player", out found, out _) && found.AppearanceId == "", "lookup returns profile copy");
            first = store.SetAppearance(first.AccountId, "avatar:citizen_07");
            Check(first.AppearanceId == "avatar:citizen_07", "appearance assigned");
            Throws(() => store.SetAppearance(first.AccountId, ""), "empty appearance rejected");
            Throws(() => store.SetAppearance(first.AccountId, new string('x', 129)), "oversized appearance rejected");
            Throws(() => store.SetAppearance(Guid.NewGuid().ToString("N"), "avatar:any"), "unknown account appearance refused");
            second = store.Register("second_player", store.FakeVerifier("second_player"));
            Check(second.AccountId != first.AccountId, "account IDs distinct");
            store.ValidateLedgerAccounts(new AccountWorldState { Accounts = new[] { new AccountState { AccountId = first.AccountId }, new AccountState { AccountId = second.AccountId } } });
            Check(true, "registered ledger accounts accepted");
            Throws(() => store.ValidateLedgerAccounts(new AccountWorldState { Accounts = new[] { new AccountState { AccountId = Guid.NewGuid().ToString("N") } } }), "unregistered ledger identity refused");
            originalKey = File.ReadAllBytes(Path.Combine(main, "account-identity.pkcs8"));
        }
        string file = File.ReadAllText(Path.Combine(main, "accounts.json"));
        using (var json = JsonDocument.Parse(file))
        {
            var records = json.RootElement.GetProperty("Accounts").EnumerateArray().ToArray();
            Check(records.Length == 2 && records[0].GetProperty("Profile").GetProperty("Username").GetString() == "first_player", "deterministic persisted account ordering");
            string[] fields = records[0].GetProperty("Verifier").EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
            Check(fields.SequenceEqual(new[] { "Iterations", "Salt", "ServerKey", "StoredKey" }), "only verifier fields persisted");
            Check(records[0].GetProperty("Verifier").GetProperty("Iterations").GetInt32() == 600000, "strong KDF policy persisted");
        }
        Check(!file.Contains(Password) && !file.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password))) &&
            !file.Contains("Password") && !file.Contains("ClientKey") && !file.Contains("PrivateKey"), "credential JSON contains no password, client key, or identity private key");
        using (var store = new AccountStore(main))
        {
            Check(store.Identity.ServerId == serverId && Equal(store.Identity.PublicKey, publicKey), "server identity and pin stable after restart");
            Check(Equal(originalKey, File.ReadAllBytes(Path.Combine(main, "account-identity.pkcs8"))), "private key file is reused unchanged");
            Check(store.TryGet("FIRST_PLAYER", out var restored, out var restoredVerifier) &&
                restored.AccountId == first.AccountId && restored.Username == first.Username && restored.AppearanceId == "avatar:citizen_07", "account ID, normalized username and appearance survive restart");
            Check(Equal(restoredVerifier.Salt, verifier.Salt) && Equal(restoredVerifier.StoredKey, verifier.StoredKey) && Equal(restoredVerifier.ServerKey, verifier.ServerKey), "verifier survives restart exactly");
            var fakeAgain = store.FakeVerifier("missing_user");
            Check(Equal(fakeAgain.Salt, fakeOne.Salt) && Equal(fakeAgain.StoredKey, fakeOne.StoredKey) && Equal(fakeAgain.ServerKey, fakeOne.ServerKey), "dummy account material stable across restart");
            Throws(() => store.Register("FIRST_PLAYER", verifier), "duplicate account denied after restart");
            var hello = new ServerHello { SessionId = Guid.NewGuid().ToString("N"), Nonce = Guid.NewGuid().ToString("N") };
            var begin = AccountAuthentication.CreateBegin("login", "FIRST_PLAYER");
            var challenge = AccountAuthentication.CreateChallenge(store.IdentityKey, store.Identity, hello, begin, restoredVerifier);
            using var client = AccountAuthentication.CreateClient(store.Identity, serverId, hello, begin, challenge, Password);
            Check(AccountAuthentication.VerifyProof(challenge, restoredVerifier, client.Proof, out var proof, out var transport) &&
                client.VerifyServerProof(proof) && Equal(client.SessionKey, transport), "persisted verifier supports mutual login after restart");
            CryptographicOperations.ZeroMemory(transport);
        }
        using (var independent = new AccountStore(Path.Combine(root, "independent")))
        {
            Check(independent.Identity.ServerId != serverId, "independent server has distinct pin");
            Check(!Equal(independent.FakeVerifier("missing_user").Salt, fakeOne.Salt), "dummy salt cannot correlate accounts across servers");
        }
        string missingIdentity = Path.Combine(root, "missing-identity");
        Directory.CreateDirectory(missingIdentity);
        File.Copy(Path.Combine(main, "accounts.json"), Path.Combine(missingIdentity, "accounts.json"));
        Throws(() => { using var ignored = new AccountStore(missingIdentity); }, "credentials without identity fail closed");
        Check(!File.Exists(Path.Combine(missingIdentity, "account-identity.pkcs8")), "missing identity failure cannot replace a trusted pin");
        string malformedIdentity = Fixture(main, root, "invalid-identity");
        File.WriteAllBytes(Path.Combine(malformedIdentity, "account-identity.pkcs8"), new byte[] { 1, 2, 3 });
        Throws(() => { using var ignored = new AccountStore(malformedIdentity); }, "corrupt identity rejected");
        foreach (Action<JsonObject> mutate in new Action<JsonObject>[] {
            j => j["SchemaVersion"] = 99, j => j["ServerId"] = new string('A', 64), j => j["Accounts"] = null,
            j => j["Accounts"][0]["Profile"]["Username"] = "FIRST_PLAYER",
            j => j["Accounts"][1]["Profile"]["Username"] = "first_player",
            j => j["Accounts"][1]["Profile"]["AccountId"] = j["Accounts"][0]["Profile"]["AccountId"].GetValue<string>(),
            j => j["Accounts"][0]["Verifier"]["Iterations"] = 4096,
            j => j["Accounts"][0]["Verifier"]["StoredKey"] = Convert.ToBase64String(new byte[31]) })
        {
            string invalid = Fixture(main, root, "bad-store-" + checks);
            var json = JsonNode.Parse(file).AsObject(); mutate(json);
            File.WriteAllText(Path.Combine(invalid, "accounts.json"), json.ToJsonString());
            Throws(() => { using var ignored = new AccountStore(invalid); }, "malformed credential store rejected without replacement");
            Check(File.ReadAllText(Path.Combine(invalid, "accounts.json")) == json.ToJsonString(), "malformed credentials not overwritten");
        }
        Check(Directory.GetFiles(main, "*.tmp").Length == 0, "successful persistence leaves no temp files");
        CryptographicOperations.ZeroMemory(originalKey); CryptographicOperations.ZeroMemory(verifier.StoredKey); CryptographicOperations.ZeroMemory(verifier.ServerKey);
        Console.WriteLine("Account store PASS: " + checks + " assertions. Synthetic test files only: " + root);
        return 0;
    }
    private static string Fixture(string source, string root, string name)
    {
        string destination = Path.Combine(root, name); Directory.CreateDirectory(destination);
        File.Copy(Path.Combine(source, "account-identity.pkcs8"), Path.Combine(destination, "account-identity.pkcs8"));
        File.Copy(Path.Combine(source, "accounts.json"), Path.Combine(destination, "accounts.json"));
        return destination;
    }
}
