using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using NivalisTogether;
using NivalisTogether.Dedicated;

internal static class AccountClientFlowTests
{
    private static int assertions;
    private const string Password = "secure account password 123";
    private const string AccountId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AppearanceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static void Check(bool condition, string name) { assertions++; if (!condition) throw new Exception("FAIL: " + name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
    private sealed class View : IAccountClientView
    {
        public Action<bool> Trust;
        public Action<AccountLoginRequest> Form;
        public Action<string> Choose;
        public Action Cancel;
        public int TrustCount, FormCount, ProfileCount, ClearCount;
        public bool Busy;
        public string InitialRoomPassword;
        public AccountAuthenticated Profile;
        public void ShowIdentityTrust(string endpoint, string fingerprint, Action<bool> respond) { Trust = respond; TrustCount++; }
        public void ShowAccountForm(string endpoint, string notice, Action<AccountLoginRequest> submit, Action cancel, string roomPassword = "") { Form = submit; Cancel = cancel; FormCount++; InitialRoomPassword = roomPassword; }
        public void ShowAccountProfile(AccountAuthenticated profile, Action<string> choose, Action cancel) { Profile = profile; Choose = choose; Cancel = cancel; ProfileCount++; }
        public void SetAccountBusy(bool value, string notice) { Busy = value; }
        public void ClearAccountFlow() { Trust = null; Form = null; Choose = null; Cancel = null; Profile = null; ClearCount++; }
    }
    private sealed class Pair : IDisposable
    {
        internal DedicatedConnection Client, Server;
        internal ServerHello Hello;
        internal Queue<WireMessage> ServerMessages = new Queue<WireMessage>();
        internal string Endpoint;
        internal Pair(AccountServerIdentity identity)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Endpoint = "127.0.0.1:" + port;
            var pending = DedicatedConnection.ConnectAsync("127.0.0.1", port);
            var accepted = listener.AcceptTcpClient(); listener.Stop();
            Client = pending.GetAwaiter().GetResult(); Server = DedicatedConnection.FromAccepted(accepted);
            Hello = new ServerHello { AccountIdentity = identity, SessionId = Server.SessionId, Nonce = Guid.NewGuid().ToString("N") };
            Server.Send("serverHello", Hello);
            var stop = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < stop)
            {
                Server.Tick(); Client.Tick();
                if (Client.TryReceive(out var message)) { Check(message.Type == "serverHello", "actual transport hello"); return; }
                Thread.Sleep(2);
            }
            throw new Exception("Hello timeout");
        }
        internal void Pump(AccountClientFlow flow)
        {
            Server.Tick(); Client.Tick();
            while (Client.TryReceive(out var message)) flow.Receive(message);
            flow.Tick(); Client.Tick(); Server.Tick();
            while (Server.TryReceive(out var message)) ServerMessages.Enqueue(message);
        }
        internal WireMessage Until(AccountClientFlow flow, string type)
        {
            var stop = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < stop)
            {
                Pump(flow);
                while (ServerMessages.Count > 0) { var message = ServerMessages.Dequeue(); if (message.Type == type) return message; }
                if (!Client.Connected || !Server.Connected) throw new Exception("Connection failed: " + Client.Status + "/" + Server.Status);
                Thread.Sleep(2);
            }
            throw new Exception("Timed out waiting for " + type);
        }
        internal void Until(AccountClientFlow flow, Func<bool> condition)
        {
            var stop = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < stop) { Pump(flow); if (condition()) return; Thread.Sleep(2); }
            throw new Exception("Condition timeout");
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }
    public static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]);
        string pinPath = Path.Combine(args[0], "server-identities.json");
        using var rsa = RSA.Create(AccountAuthentication.RsaBits);
        var identity = AccountAuthentication.Identity(rsa);
        AccountIdentityPins.ValidateIdentity(identity);
        Check(identity.ServerId.Length == 64, "valid RSA identity accepted");
        Reject(() => AccountIdentityPins.ValidateIdentity(new AccountServerIdentity { ServerId = identity.ServerId, PublicKey = new byte[400] }), "invalid key rejected before trust UI");
        Check(AccountIdentityPins.NormalizeEndpoint("LOCALHOST:47778") == "localhost:47778", "canonical endpoint case");
        Check(AccountIdentityPins.NormalizeEndpoint("[::ffff:127.0.0.1]:47778") == "127.0.0.1:47778", "canonical IPv4 mapping");
        Reject(() => AccountIdentityPins.NormalizeEndpoint("user@localhost:47778"), "endpoint userinfo rejected");
        Reject(() => AccountIdentityPins.NormalizeEndpoint("localhost:47778/path"), "endpoint path rejected");
        Reject(() => AccountClientFlow.ValidateOptions(new[] { new AppearanceOption { Id = AppearanceId, Name = "A" }, new AppearanceOption { Id = AppearanceId, Name = "B" } }), "duplicate appearance ids rejected");
        Reject(() => AccountClientFlow.ValidateOptions(new AppearanceOption[513]), "catalog bounded");
        Reject(() => AccountClientFlow.ValidateAuthenticated(new AccountAuthenticated { AccountId = AccountId, Username = "other" }, "explorer"), "account username binding checked");

        string endpoint;
        AccountVerifier registered;
        using (var pair = new Pair(identity))
        {
            endpoint = pair.Endpoint;
            var view = new View(); var failures = new List<string>(); int ready = 0, cancelled = 0;
            using var flow = new AccountClientFlow(view, pinPath, _ => { ready++; pair.Client.Send("accountJoin", new { }); }, failures.Add, () => cancelled++);
            flow.Begin(pair.Client, pair.Hello, endpoint, "room only");
            Check(view.TrustCount == 1 && view.FormCount == 0, "unknown pin blocks credentials until explicit trust");
            Check(!File.Exists(pinPath), "showing trust does not persist acceptance");
            var staleTrust = view.Trust; view.Trust(true);
            Check(view.FormCount == 1 && new AccountIdentityPins(pinPath).Find(endpoint) == identity.ServerId, "explicit trust persists exact fingerprint and opens form");
            staleTrust(false);
            Check(cancelled == 0 && flow.IsActive, "stale trust callback cannot cancel newer form");
            Check(view.InitialRoomPassword == "room only", "initial room secret is editable form content, not a hidden fallback");
            var request = new AccountLoginRequest { Username = "Explorer", Password = Password, RoomPassword = view.InitialRoomPassword, Register = true };
            view.Form(request);
            Check(request.Password == "" && request.RoomPassword == "", "submission DTO secrets released immediately");
            var begin = pair.Until(flow, "accountBegin").Read<AuthBegin>();
            Check(begin.Username == "explorer" && begin.Mode == "register", "normalized create request");
            Check(begin.RoomProof == AccountProtocol.RoomProof(pair.Hello, begin, "room only"), "room password remains separate and only proof is sent");
            var challenge = AccountAuthentication.CreateChallenge(rsa, identity, pair.Hello, begin);
            pair.Server.Send("accountChallenge", challenge);
            var registration = pair.Until(flow, "accountRegister").Read<AccountRegistrationProof>();
            registered = AccountAuthentication.OpenRegistration(rsa, challenge, registration.Registration);
            Check(AccountAuthentication.VerifyProof(challenge, registered, registration.Proof, out var serverProof, out var key), "actual client registration proof verifies");
            pair.Server.SetAuthenticationKey(key); CryptographicOperations.ZeroMemory(key);
            pair.Server.Send("accountAuthenticated", new AccountAuthenticated { AccountId = AccountId, Username = begin.Username,
                AppearanceId = "", AppearanceOptions = new[] { new AppearanceOption { Id = AppearanceId, Name = "Native NPC" } }, ServerProof = serverProof });
            pair.Until(flow, () => view.ProfileCount == 1);
            Check(failures.Count == 0 && pair.Client.Connected, "server-proof callback and same-frame MAC accepted");
            Check(!flow.ReadyToJoin && ready == 0, "authentication alone cannot join world");
            Check(pair.Client.AuthenticateAccountFrame == null, "proof callback is one-use");
            view.Choose(AppearanceId);
            var selection = pair.Until(flow, "accountProfile").Read<AccountProfileRequest>();
            Check(selection.AppearanceId == AppearanceId && ready == 0, "appearance request waits for server confirmation");
            pair.Server.Send("accountProfileAccepted", new AccountProfile { AccountId = AccountId, Username = begin.Username, AppearanceId = AppearanceId });
            pair.Until(flow, "accountJoin");
            Check(ready == 1 && flow.ReadyToJoin && flow.Profile.AccountId == AccountId, "matching profile releases root join callback once");
            flow.MarkJoined(); Check(!flow.IsActive && !flow.ReadyToJoin && flow.Profile.AppearanceId == AppearanceId, "join acceptance clears account UI and retains verified profile");
            string persisted = File.ReadAllText(pinPath);
            Check(!persisted.Contains(Password) && !persisted.Contains("room only") && !persisted.Contains("explorer"), "pin file contains no password or username");
            flow.Reset(); Check(flow.Profile == null && pair.Client.AuthenticateAccountFrame == null, "reset clears account and transport hook");
        }
        using (var pair = new Pair(identity))
        {
            var view = new View(); var failures = new List<string>(); int cancelled = 0;
            using var flow = new AccountClientFlow(view, pinPath, _ => {}, failures.Add, () => cancelled++);
            // Reuse the trusted logical address across a fresh connection. The
            // root provides the original hostname, never an unstable source port.
            flow.Begin(pair.Client, pair.Hello, endpoint, "incorrect default");
            Check(view.TrustCount == 0 && view.FormCount == 1, "matching pin permits form on fresh connection");
            var oldSubmit = view.Form;
            view.Form(new AccountLoginRequest { Username = "explorer", Password = Password });
            var begin = pair.Until(flow, "accountBegin").Read<AuthBegin>();
            Check(begin.Mode == "login", "login is separate from account registration");
            Check(begin.RoomProof == AccountProtocol.RoomProof(pair.Hello, begin, ""), "user can erase an initial room password instead of silently reusing it");
            view.Cancel();
            Check(cancelled == 1 && !flow.IsActive, "cancel remains available while awaiting challenge");
            oldSubmit(new AccountLoginRequest { Username = "explorer", Password = Password });
            Check(!flow.IsActive && failures.Count == 0, "cancelled generation rejects stale credential callbacks");
        }
        using (var otherRsa = RSA.Create(AccountAuthentication.RsaBits))
        using (var pair = new Pair(AccountAuthentication.Identity(otherRsa)))
        {
            var view = new View(); var failures = new List<string>();
            using var flow = new AccountClientFlow(view, pinPath, _ => {}, failures.Add, () => {});
            flow.Begin(pair.Client, pair.Hello, endpoint);
            Check(failures.Count == 1 && failures[0].Contains("changed"), "changed pin fails closed");
            Check(view.TrustCount == 0 && view.FormCount == 0, "changed pin has no trust override or password form");
            Check(new AccountIdentityPins(pinPath).Find(endpoint) == identity.ServerId, "changed pin never overwrites trust store");
        }
        foreach (bool badProof in new[] { true, false })
        using (var pair = new Pair(identity))
        {
            var view = new View(); var failures = new List<string>(); int ready = 0;
            using var flow = new AccountClientFlow(view, pinPath, _ => ready++, failures.Add, () => {});
            flow.Begin(pair.Client, pair.Hello, endpoint);
            view.Form(new AccountLoginRequest { Username = "explorer", Password = Password });
            var begin = pair.Until(flow, "accountBegin").Read<AuthBegin>();
            var challenge = AccountAuthentication.CreateChallenge(rsa, identity, pair.Hello, begin, registered);
            pair.Server.Send("accountChallenge", challenge);
            var proof = pair.Until(flow, "accountProof").Read<AccountAuthProof>();
            Check(AccountAuthentication.VerifyProof(challenge, registered, proof, out var serverProof, out var key), "actual login proof verifies");
            // Exercise both gates independently: a bad server proof with a valid
            // MAC, then a valid server proof with a MAC from the wrong key.
            pair.Server.SetAuthenticationKey(badProof ? key : RandomNumberGenerator.GetBytes(32));
            CryptographicOperations.ZeroMemory(key);
            if (badProof) serverProof[0] ^= 1;
            pair.Server.Send("accountAuthenticated", new AccountAuthenticated { AccountId = AccountId, Username = begin.Username,
                AppearanceOptions = new[] { new AppearanceOption { Id = AppearanceId, Name = "Native NPC" } }, ServerProof = serverProof });
            pair.Until(flow, () => failures.Count > 0);
            Check(!pair.Client.Connected, badProof ? "bad server proof closes transport" : "bad same-frame MAC closes transport after valid proof");
            Check(view.ProfileCount == 0 && ready == 0 && flow.Profile == null, "invalid authentication frame never reaches appearance or world join");
        }
        using (var pair = new Pair(identity))
        {
            var view = new View(); var failures = new List<string>(); int cancelled = 0;
            using var flow = new AccountClientFlow(view, pinPath, _ => {}, failures.Add, () => cancelled++);
            flow.Begin(pair.Client, pair.Hello, endpoint);
            view.Form(new AccountLoginRequest { Username = "explorer", Password = Password });
            var begin = pair.Until(flow, "accountBegin").Read<AuthBegin>();
            pair.Server.Send("accountChallenge", AccountAuthentication.CreateChallenge(rsa, identity, pair.Hello, begin, registered));
            var end = DateTime.UtcNow.AddSeconds(5); bool received = false;
            while (!received && DateTime.UtcNow < end)
            {
                pair.Server.Tick(); pair.Client.Tick();
                while (pair.Client.TryReceive(out var message)) { flow.Receive(message); received |= message.Type == "accountChallenge"; }
                if (!received) Thread.Sleep(2);
            }
            Check(received, "KDF cancellation fixture received challenge");
            view.Cancel();
            Check(cancelled == 1 && !flow.IsActive && pair.Client.AuthenticateAccountFrame == null, "cancel during managed KDF clears generation and transport callback");
            for (int i = 0; i < 100; i++) { pair.Pump(flow); Thread.Sleep(2); }
            Check(!pair.ServerMessages.Any(m => m.Type == "accountProof") && failures.Count == 0, "abandoned KDF result never sends proof or reopens UI");
        }
        foreach (bool trusted in new[] { false, true })
        using (var pair = new Pair(identity))
        {
            var view = new View(); var failures = new List<string>(); int cancelled = 0;
            using var flow = new AccountClientFlow(view, pinPath, _ => {}, failures.Add, () => cancelled++);
            flow.Begin(pair.Client, pair.Hello, trusted ? endpoint : pair.Endpoint);
            Check(trusted ? view.Form != null : view.Trust != null, "peer-close fixture waits for human input");
            var oldTrust = view.Trust; var oldForm = view.Form;
            pair.Server.Dispose();
            pair.Until(flow, () => failures.Count > 0);
            Check(failures.Count == 1 && failures[0].Contains("JOIN SERVER") && failures[0].Contains("3 minutes"), "human sign-in disconnect gives bounded retry guidance");
            Check(cancelled == 0 && !flow.IsActive && view.Trust == null && view.Form == null, "peer closure is failure, not a user cancel, and clears credential controls");
            oldTrust?.Invoke(true); oldForm?.Invoke(new AccountLoginRequest { Username = "explorer", Password = Password });
            Check(failures.Count == 1 && view.Form == null, "stale human input cannot revive the closed account connection");
        }
        TestTerminalRejections(rsa, identity, registered, pinPath, endpoint);
        var pins = new AccountIdentityPins(pinPath);
        pins.Trust("localhost:47778", identity.ServerId);
        Check(pins.Find(endpoint) == identity.ServerId && pins.Find("localhost:47778") == identity.ServerId, "atomic pin update preserves other endpoint aliases");
        Reject(() => pins.Trust(endpoint, new string('A', 64)), "pin writer independently rejects replacement");
        Check(!Directory.EnumerateFiles(args[0], "*.tmp").Any(), "atomic pin temporary files cleaned");
        Console.WriteLine("PASS: " + assertions + " client flow/pin assertions including actual TCP encrypted registration, signed server proof, appearance barrier and cancellation.");
        return 0;
    }

    private static void TestTerminalRejections(RSA rsa, AccountServerIdentity identity, AccountVerifier registered, string pinPath, string endpoint)
    {
        const string duplicate = "That account cannot be created. Choose another username or log in.";
        var cases = new[] {
            (Code:"account_unavailable", Message:duplicate, Expected:duplicate, Signed:false, BadMac:false, Authenticate:true, Register:true),
            (Code:"authentication_failed", Message:"Account authentication failed.", Expected:"Account authentication failed.", Signed:false, BadMac:false, Authenticate:true, Register:false),
            (Code:"bounded", Message:new string('x',256), Expected:new string('x',256), Signed:false, BadMac:false, Authenticate:false, Register:false),
            (Code:"oversized", Message:new string('x',257), Expected:"", Signed:false, BadMac:false, Authenticate:false, Register:false),
            (Code:"controls", Message:"Invalid\u0001diagnostic", Expected:"", Signed:false, BadMac:false, Authenticate:false, Register:false),
            (Code:"", Message:"Missing error code", Expected:"", Signed:false, BadMac:false, Authenticate:false, Register:false),
            (Code:"signed", Message:"Signed rejection before FIN.", Expected:"Signed rejection before FIN.", Signed:true, BadMac:false, Authenticate:false, Register:false),
            (Code:"forged", Message:"Forged rejection must not be displayed.", Expected:"", Signed:true, BadMac:true, Authenticate:false, Register:false)
        };
        foreach (var test in cases)
        using (var pair = new Pair(identity))
        {
            var view = new View(); var failures = new List<string>(); int ready = 0, cancelled = 0;
            using var flow = new AccountClientFlow(view, pinPath, _ => ready++, failures.Add, () => cancelled++);
            flow.Begin(pair.Client, pair.Hello, endpoint);
            Check(pair.Client.TerminalServerErrorMessage == "", "new account connection has no old terminal rejection");
            if (test.Authenticate)
            {
                view.Form(new AccountLoginRequest { Username = "explorer", Password = Password, Register = test.Register });
                var begin = pair.Until(flow, "accountBegin").Read<AuthBegin>();
                pair.Server.Send("accountChallenge", AccountAuthentication.CreateChallenge(rsa, identity, pair.Hello, begin, test.Register ? null : registered));
                pair.Until(flow, test.Register ? "accountRegister" : "accountProof");
                Check(pair.Client.AuthenticateAccountFrame != null, "rejection fixture is awaiting real server authentication proof");
            }
            if (test.Signed)
            {
                byte[] key = RandomNumberGenerator.GetBytes(32);
                pair.Client.SetAuthenticationKey(key);
                pair.Server.SetAuthenticationKey(test.BadMac ? RandomNumberGenerator.GetBytes(32) : key);
                CryptographicOperations.ZeroMemory(key);
            }
            // Do not poll the client between the framed error and remote FIN.
            // Ordinary queued data must still be discarded after transport loss.
            if (!test.BadMac) Check(pair.Server.Send("worldEvent", new { MustNotApply = true }), "ordinary test frame queued");
            Check(pair.Server.Send("error", new ProtocolError { Code = test.Code, Message = test.Message }), "terminal error frame queued");
            var end = DateTime.UtcNow.AddSeconds(3);
            while (pair.Server.PendingSendBytes > 0 && DateTime.UtcNow < end) { pair.Server.Tick(); Thread.Sleep(2); }
            Check(pair.Server.PendingSendBytes == 0, "complete error frame sent before FIN");
            pair.Server.Dispose();
            end = DateTime.UtcNow.AddSeconds(3);
            while (pair.Client.Connected && DateTime.UtcNow < end) { pair.Client.Tick(); Thread.Sleep(2); }
            Check(!pair.Client.Connected, "actual TCP observes terminal FIN or invalid MAC");
            Check(!pair.Client.TryReceive(out _), "closed transport never releases queued gameplay or rejection work");
            Check(pair.Client.TerminalServerErrorMessage == test.Expected, "only bounded accepted-envelope error survives as diagnostic");
            if (test.BadMac) Check(pair.Client.Status.Contains("authentication failed", StringComparison.OrdinalIgnoreCase), "forged error preserves MAC failure");
            flow.Tick();
            Check(failures.Count == 1 && (test.Expected.Length != 0 ? failures[0] == test.Expected : failures[0].Contains("connection closed")), "flow surfaces explicit rejection or generic closure, never arbitrary transport status");
            Check(!flow.IsActive && flow.Profile == null && ready == 0 && cancelled == 0 && view.ProfileCount == 0, "diagnostic cannot authenticate, select appearance, join, or cancel as user");
            Check(pair.Client.TerminalServerErrorMessage == "", "flow reset clears consumed terminal diagnostic");
            flow.Tick(); Check(failures.Count == 1, "terminal callback is delivered once");
        }
        using (var pair = new Pair(identity))
        {
            pair.Server.Send("error", new ProtocolError { Code = "reset", Message = "Reset-only diagnostic" });
            pair.Server.Tick(); pair.Server.Dispose();
            var end = DateTime.UtcNow.AddSeconds(3);
            while (pair.Client.Connected && DateTime.UtcNow < end) { pair.Client.Tick(); Thread.Sleep(2); }
            Check(pair.Client.TerminalServerErrorMessage == "Reset-only diagnostic", "explicit dispose fixture retains received reason until reset");
            pair.Client.Dispose(); Check(pair.Client.TerminalServerErrorMessage == "", "repeated Dispose clears terminal diagnostic on an already closed transport");
        }
    }
}
