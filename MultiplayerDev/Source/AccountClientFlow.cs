using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NivalisTogether.Dedicated;

namespace NivalisTogether
{
    public sealed class AccountLoginRequest
    {
        public string Username = "", Password = "", RoomPassword = "";
        public bool Register;
    }

    public interface IAccountClientView
    {
        void ShowIdentityTrust(string endpoint, string fingerprint, Action<bool> respond);
        void ShowAccountForm(string endpoint, string notice, Action<AccountLoginRequest> submit, Action cancel, string roomPassword = "");
        void ShowAccountProfile(AccountAuthenticated profile, Action<string> joinAppearance, Action cancel);
        void SetAccountBusy(bool value, string message);
        void ClearAccountFlow();
    }

    /// <summary>Main-thread account protocol controller. The session still owns connection
    /// pumping, joinAccepted, save isolation and world loading. Tick only polls managed KDF
    /// completion; the KDF task never touches Unity, the socket, or the view.</summary>
    public sealed class AccountClientFlow : IDisposable
    {
        private enum Stage { None, Trust, Form, Challenge, Deriving, Authentication, Appearance, Profile, Joining, Complete }
        private static int deriving;
        private readonly IAccountClientView view;
        private readonly AccountIdentityPins pins;
        private readonly Action<AccountProfile> ready;
        private readonly Action<string> failed;
        private readonly Action cancelled;
        private DedicatedConnection connection;
        private ServerHello hello;
        private AuthBegin begin;
        private ClientAuth authentication;
        private Task<ClientAuth> preparation;
        private AccountAuthenticated authenticated;
        private string endpoint = "", password = "", initialRoomPassword = "", pendingAppearance = "";
        private Stage stage;
        private long generation;
        private DateTime deadline;
        private bool disposed;
        public AccountProfile Profile { get; private set; }
        public bool ReadyToJoin => stage == Stage.Joining;
        public bool IsActive => stage != Stage.None && stage != Stage.Complete;

        public AccountClientFlow(IAccountClientView view, string pinsPath, Action<AccountProfile> readyToJoin,
            Action<string> failed, Action cancelled)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            pins = new AccountIdentityPins(pinsPath);
            ready = readyToJoin ?? throw new ArgumentNullException(nameof(readyToJoin));
            this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
            this.cancelled = cancelled ?? throw new ArgumentNullException(nameof(cancelled));
        }

        public void Begin(DedicatedConnection connection, ServerHello hello, string endpoint, string roomSecret = "")
        {
            if (disposed) throw new ObjectDisposedException(nameof(AccountClientFlow));
            Reset();
            try
            {
                if (connection == null || !connection.Connected || hello == null || hello.SessionId != connection.SessionId ||
                    hello.Version != DedicatedProtocol.Version || hello.BuildId != DedicatedProtocol.BuildId)
                    throw new InvalidDataException("The server uses a different multiplayer build.");
                AccountIdentityPins.ValidateIdentity(hello.AccountIdentity);
                this.connection = connection; this.hello = hello;
                this.endpoint = AccountIdentityPins.NormalizeEndpoint(endpoint);
                initialRoomPassword = roomSecret ?? "";
                string previous = pins.Find(this.endpoint);
                if (previous != null && previous != hello.AccountIdentity.ServerId)
                    throw new CryptographicException("The saved server fingerprint has changed. Connection refused. Verify the server identity with its owner before changing the saved pin.");
                if (previous != null) { ShowForm(); return; }
                stage = Stage.Trust;
                long token = generation;
                view.ShowIdentityTrust(this.endpoint, hello.AccountIdentity.ServerId, accepted =>
                {
                    if (!Current(token, Stage.Trust)) return;
                    if (!accepted) { Cancel(); return; }
                    try { pins.Trust(this.endpoint, hello.AccountIdentity.ServerId); ShowForm(); }
                    catch (Exception ex) { Fail(ex.Message); }
                });
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        private bool Current(long token, Stage expected) => !disposed && generation == token && stage == expected && connection != null && connection.Connected;
        private void ShowForm(string notice = "")
        {
            stage = Stage.Form;
            long token = generation;
            view.ShowAccountForm(endpoint, notice, request =>
            {
                if (!Current(token, Stage.Form)) return;
                try
                {
                    if (request == null) throw new ArgumentException("Enter your account details.");
                    AccountAuthentication.ValidatePassword(request.Password);
                    begin = AccountAuthentication.CreateBegin(request.Register ? "register" : "login", request.Username);
                    begin.RoomProof = AccountProtocol.RoomProof(hello, begin, request.RoomPassword);
                    initialRoomPassword = "";
                    password = request.Password;
                    stage = Stage.Challenge; deadline = DateTime.UtcNow.AddSeconds(15);
                    view.SetAccountBusy(true, "Requesting a secure account challenge...");
                    Send("accountBegin", begin);
                }
                catch (ArgumentException ex) { password = initialRoomPassword = ""; ShowForm(ex.Message); }
                catch (Exception ex) { Fail(ex.Message); }
                finally { if (request != null) request.Password = request.RoomPassword = ""; }
            }, () => { if (generation == token && IsActive) Cancel(); }, initialRoomPassword);
            initialRoomPassword = "";
        }

        public bool Receive(WireMessage message)
        {
            if (message == null) return false;
            if (message.Type != "accountChallenge" && message.Type != "accountAuthenticated" && message.Type != "accountProfileAccepted" &&
                message.Type != "appearanceCatalog" && message.Type != "accountJoinRejected" && message.Type != "error") return false;
            if (!IsActive) return false;
            try
            {
                switch (message.Type)
                {
                    case "accountChallenge":
                        if (stage != Stage.Challenge) throw new InvalidDataException("Unexpected account challenge.");
                        var challenge = message.Read<AccountAuthChallenge>();
                        if (Interlocked.CompareExchange(ref deriving, 1, 0) != 0)
                            throw new InvalidOperationException("A previous account request is still finishing. Please reconnect in a moment.");
                        var identity = hello.AccountIdentity; var capturedHello = hello; var capturedBegin = begin;
                        string capturedPassword = password; password = "";
                        stage = Stage.Deriving; deadline = DateTime.UtcNow.AddSeconds(15);
                        try
                        {
                            preparation = Task.Run(() =>
                            {
                                try { return AccountAuthentication.CreateClient(identity, identity.ServerId, capturedHello, capturedBegin, challenge, capturedPassword); }
                                finally { capturedPassword = ""; Interlocked.Exchange(ref deriving, 0); }
                            });
                        }
                        catch { Interlocked.Exchange(ref deriving, 0); throw; }
                        view.SetAccountBusy(true, "Verifying the server and securing your account...");
                        break;
                    case "accountAuthenticated":
                        if (stage != Stage.Authentication || authentication == null)
                            throw new InvalidDataException("Unexpected account authentication response.");
                        authenticated = message.Read<AccountAuthenticated>();
                        ValidateAuthenticated(authenticated, begin.Username);
                        authentication.Dispose(); authentication = null;
                        Profile = new AccountProfile { AccountId = authenticated.AccountId, Username = authenticated.Username, AppearanceId = authenticated.AppearanceId };
                        ShowAppearances();
                        break;
                    case "appearanceCatalog":
                        if (authenticated == null || (stage != Stage.Appearance && stage != Stage.Profile && stage != Stage.Joining))
                            throw new InvalidDataException("Unexpected appearance catalog.");
                        var options = message.Read<AccountAppearanceCatalog>()?.Options;
                        ValidateOptions(options);
                        authenticated.AppearanceOptions = options;
                        if (stage == Stage.Appearance) ShowAppearances();
                        break;
                    case "accountProfileAccepted":
                        var profile = message.Read<AccountProfile>();
                        if (stage != Stage.Profile || profile == null || profile.AccountId != Profile.AccountId || profile.Username != Profile.Username ||
                            profile.AppearanceId != pendingAppearance || !authenticated.AppearanceOptions.Any(o => o.Id == profile.AppearanceId))
                            throw new InvalidDataException("Server character selection did not match this account.");
                        Profile = profile; authenticated.AppearanceId = profile.AppearanceId;
                        stage = Stage.Joining; deadline = DateTime.UtcNow.AddSeconds(30);
                        view.SetAccountBusy(true, "Joining the shared world...");
                        ready(Profile);
                        break;
                    case "accountJoinRejected":
                        if (stage != Stage.Joining) throw new InvalidDataException("Unexpected world join response.");
                        var rejection = message.Read<ProtocolError>();
                        ShowAppearances(); view.SetAccountBusy(false, SafeMessage(rejection?.Message)); break;
                    case "error":
                        Fail(SafeMessage(message.Read<ProtocolError>()?.Message)); break;
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
            return true;
        }

        public void Tick()
        {
            if (!IsActive || disposed) return;
            if (connection == null || !connection.Connected)
            {
                string rejection = connection?.TerminalServerErrorMessage;
                Fail(!string.IsNullOrEmpty(rejection) ? rejection : stage == Stage.Trust || stage == Stage.Form
                    ? "The sign-in connection closed. Select JOIN SERVER to reconnect. The server allows 3 minutes to sign in; passwords have been cleared."
                    : "The account connection closed. Select JOIN SERVER to reconnect; passwords have been cleared.");
                return;
            }
            if (stage == Stage.Deriving && preparation != null && preparation.IsCompleted)
            {
                var completed = preparation; preparation = null;
                try
                {
                    authentication = completed.GetAwaiter().GetResult();
                    var expected = authentication;
                    long token = generation;
                    stage = Stage.Authentication; deadline = DateTime.UtcNow.AddSeconds(15);
                    // This callback runs inside transport parsing, before the same
                    // frame's MAC is checked and before any payload can be consumed.
                    connection.AuthenticateAccountFrame = frame =>
                    {
                        if (!Current(token, Stage.Authentication) || authentication != expected)
                            throw new CryptographicException("Stale account proof.");
                        var reply = frame.Read<AccountAuthenticated>();
                        if (reply == null || !expected.VerifyServerProof(reply.ServerProof))
                            throw new CryptographicException("Account server proof was invalid.");
                        return expected.SessionKey;
                    };
                    if (begin.Mode == "register") Send("accountRegister", new AccountRegistrationProof { Registration = authentication.Registration, Proof = authentication.Proof });
                    else Send("accountProof", authentication.Proof);
                    view.SetAccountBusy(true, "Authenticating your account...");
                }
                catch (Exception ex) { Fail(ex.Message); }
            }
            if ((stage == Stage.Challenge || stage == Stage.Deriving || stage == Stage.Authentication || stage == Stage.Profile || stage == Stage.Joining) && DateTime.UtcNow > deadline)
                Fail("The account request timed out. Reconnect to try again.");
        }

        private void ShowAppearances()
        {
            stage = Stage.Appearance;
            long token = generation;
            view.ShowAccountProfile(authenticated, id =>
            {
                if (!Current(token, Stage.Appearance)) return;
                try
                {
                    if (!authenticated.AppearanceOptions.Any(o => o.Id == id)) throw new InvalidDataException("Choose a character from the server's list.");
                    pendingAppearance = id; stage = Stage.Profile; deadline = DateTime.UtcNow.AddSeconds(15);
                    view.SetAccountBusy(true, "Saving your character selection...");
                    Send("accountProfile", new AccountProfileRequest { AppearanceId = id });
                }
                catch (Exception ex) { Fail(ex.Message); }
            }, () => { if (generation == token && IsActive) Cancel(); });
        }

        // Call on joinAccepted before the normal native-world load path hides the menu.
        public void MarkJoined()
        {
            if (stage != Stage.Joining) throw new InvalidOperationException("World join arrived before account profile confirmation.");
            stage = Stage.Complete; view.ClearAccountFlow();
        }
        private void Send<T>(string type, T value)
        { if (connection == null || !connection.Send(type, value)) throw new IOException("The account connection could not send a request."); }
        private static string SafeMessage(string value) => DedicatedProtocol.Text(value, 256) ? value : "Account authentication failed. Reconnect to try again.";
        private void Fail(string message) { Reset(); failed(SafeMessage(message)); }
        private void Cancel() { Reset(); cancelled(); }
        public void Reset()
        {
            generation++; stage = Stage.None;
            if (connection != null)
            { connection.AuthenticateAccountFrame = null; connection.ClearTerminalServerError(); }
            connection = null; hello = null; begin = null; Profile = null; authenticated = null;
            password = initialRoomPassword = pendingAppearance = endpoint = "";
            authentication?.Dispose(); authentication = null;
            var abandoned = preparation; preparation = null;
            if (abandoned != null)
                _ = abandoned.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
            view.ClearAccountFlow();
        }
        public void Dispose() { if (disposed) return; disposed = true; Reset(); }
        internal static void ValidateOptions(AppearanceOption[] options)
        {
            if (options == null || options.Length > 512) throw new InvalidDataException("Invalid server character catalog.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in options)
                if (item == null || !DedicatedProtocol.Text(item.Id, 128) || !DedicatedProtocol.Text(item.Name, 128) || !ids.Add(item.Id))
                    throw new InvalidDataException("Invalid server character option.");
        }
        internal static void ValidateAuthenticated(AccountAuthenticated profile, string username)
        {
            if (profile == null || !Guid.TryParseExact(profile.AccountId, "N", out _) || profile.Username != username ||
                !DedicatedProtocol.Text(profile.AppearanceId, 128, true)) throw new InvalidDataException("Invalid authenticated account profile.");
            ValidateOptions(profile.AppearanceOptions);
        }
    }

    internal sealed class AccountIdentityPins
    {
        private readonly string path;
        internal AccountIdentityPins(string path) { this.path = Path.GetFullPath(path); }
        internal string Find(string endpoint) { var pins = Read(); return pins.TryGetValue(endpoint, out string value) ? value : null; }
        internal void Trust(string endpoint, string fingerprint)
        {
            endpoint = NormalizeEndpoint(endpoint);
            if (!Fingerprint(fingerprint)) throw new InvalidDataException("Invalid server fingerprint.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // An exclusive sidecar prevents two local game clients from dropping
            // each other's newly trusted endpoints. There is no silent replacement.
            using var claim = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var pins = Read();
            if (pins.TryGetValue(endpoint, out string prior) && prior != fingerprint) throw new CryptographicException("The server fingerprint changed while confirming trust.");
            if (!pins.ContainsKey(endpoint) && pins.Count >= 1024) throw new InvalidDataException("The trusted server list is full.");
            pins[endpoint] = fingerprint;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, pins); file.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private Dictionary<string, string> Read()
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("The trusted server file is too large.");
            var source = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (source == null || source.Count > 1024) throw new InvalidDataException("Invalid trusted server file.");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in source)
                if (!Fingerprint(item.Value) || !result.TryAdd(NormalizeEndpoint(item.Key), item.Value)) throw new InvalidDataException("Invalid trusted server record.");
            return result;
        }
        internal static string NormalizeEndpoint(string endpoint)
        {
            if (!DedicatedProtocol.Text(endpoint, 300) || !Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri) ||
                uri.Port < 1 || uri.Port > 65535 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new ArgumentException("A host and port are required for server identity verification.");
            string host = uri.IdnHost.Trim('[', ']');
            if (IPAddress.TryParse(host, out var address))
            { if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4(); host = address.ToString(); }
            return (host.Contains(":") ? "[" + host + "]" : host.ToLowerInvariant()) + ":" + uri.Port;
        }
        private static bool Fingerprint(string value) => value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'));
        internal static void ValidateIdentity(AccountServerIdentity identity)
        {
            if (identity?.PublicKey == null || identity.PublicKey.Length < 256 || identity.PublicKey.Length > 1024 || !Fingerprint(identity.ServerId) ||
                Convert.ToHexString(SHA256.HashData(identity.PublicKey)) != identity.ServerId) throw new CryptographicException("The server identity fingerprint is invalid.");
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(identity.PublicKey, out int length);
            if (length != identity.PublicKey.Length || rsa.KeySize != AccountAuthentication.RsaBits || !rsa.ExportSubjectPublicKeyInfo().SequenceEqual(identity.PublicKey))
                throw new CryptographicException("The server identity key is invalid.");
        }
    }
}
