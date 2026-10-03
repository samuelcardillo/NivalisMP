using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NivalisTogether.Dedicated;

internal static class AccountAuthenticationTests
{
    private static int checks;
    private const string Password = "Twelve safe words! \u00e9\u2603";
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message)
    { checks++; try { action(); } catch { return; } throw new Exception(message); }
    private static bool Equal(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    private static ServerHello Hello() => new ServerHello { SessionId = Guid.NewGuid().ToString("N"), Nonce = Guid.NewGuid().ToString("N") };
    public static int Main()
    {
        PublishedVector(); InputBounds();
        using var rsa = RSA.Create(3072); using var otherRsa = RSA.Create(3072);
        var identity = AccountAuthentication.Identity(rsa); var otherIdentity = AccountAuthentication.Identity(otherRsa);
        Check(identity.PublicKey.Length > 384 && identity.ServerId.Length == 64, "canonical RSA identity");
        Check(Equal(identity.PublicKey, rsa.ExportSubjectPublicKeyInfo()), "SPKI is public identity");
        Check(identity.ServerId == Convert.ToHexString(SHA256.HashData(identity.PublicKey)), "fingerprint is SHA256(SPKI)");
        using (var smallRsa = RSA.Create(2048)) Throws(() => AccountAuthentication.Identity(smallRsa), "weak RSA identity rejected");
        RoundTrip(rsa, identity, otherRsa, otherIdentity);
        Console.WriteLine("Account authentication PASS: " + checks + " assertions (RFC 7677 vector and actual 600000-iteration account exchange).");
        return 0;
    }
    private static void PublishedVector()
    {
        // RFC 7677 section 3: https://www.rfc-editor.org/rfc/rfc7677#section-3
        // Production public APIs enforce 600000 iterations and a 32-byte salt.
        const string first = "n=user,r=rOprNGfwEbeRWgbNEkqO";
        const string nonce = "rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";
        string auth = first + ",r=" + nonce + ",s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,c=biws,r=" + nonce;
        using var keys = AccountAuthentication.DeriveKeys(Encoding.UTF8.GetBytes("pencil"), Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ=="), 4096);
        var proof = AccountAuthentication.ClientProof(keys.ClientKey, keys.StoredKey, Encoding.UTF8.GetBytes(auth));
        Check(Convert.ToBase64String(proof) == "dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=", "RFC 7677 client proof");
        Check(Convert.ToBase64String(AccountAuthentication.ServerProof(keys.ServerKey, Encoding.UTF8.GetBytes(auth))) ==
            "6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=", "RFC 7677 server proof");
        var client = keys.ClientKey; var stored = keys.StoredKey; var server = keys.ServerKey; keys.Dispose();
        Check(client.All(x => x == 0) && stored.All(x => x == 0) && server.All(x => x == 0), "SCRAM intermediate keys cleared");
    }
    private static void InputBounds()
    {
        Check(AccountAuthentication.NormalizeUsername("ABc_D-123") == "abc_d-123", "ASCII lowercase username");
        Check(AccountAuthentication.NormalizeUsername(new string('x', 24)).Length == 24, "24-character username");
        foreach (string invalid in new[] { null, "", "ab", new string('a', 25), " leading", "trailing ", "a.b", "u\u00e9ser", "a\nb", "a/b", "\u212aey" })
            Throws(() => AccountAuthentication.NormalizeUsername(invalid), "invalid username rejected");
        foreach (string valid in new[] { "twelve chars!", new string('x', 128), " leading space ", "passwordcaf\u00e9!", "passwordcafe\u0301!", string.Concat(Enumerable.Repeat("\ud83d\ude00", 128)) })
        { AccountAuthentication.ValidatePassword(valid); Check(true, "printable password accepted without normalization"); }
        foreach (string invalid in new[] { null, "", "short", new string('x', 129), "password\nabcdef", "password\0abcdef", "password\tabcdef",
            "password\u200babcdef", "password\u2028abcdef", "password\ud800abcdef" })
            Throws(() => AccountAuthentication.ValidatePassword(invalid), "invalid password rejected");
        var begin = AccountAuthentication.CreateBegin("login", "PLAYER");
        Check(begin.Username == "player" && begin.ClientNonce.Length == 32, "begin normalizes username and creates nonce");
        Check(!Equal(begin.ClientNonce, AccountAuthentication.CreateBegin("login", "PLAYER").ClientNonce), "begin nonce fresh");
        Throws(() => AccountAuthentication.CreateBegin("other", "player"), "unknown mode rejected");
        Throws(() => AccountAuthentication.CreateBegin("login", "player", new string('r', 257)), "room proof bounded");
        begin.Username = "PLAYER"; Throws(() => AccountAuthentication.ValidateBegin(begin), "wire username must already be normalized");
        begin.Username = "player"; begin.ClientNonce = new byte[31]; Throws(() => AccountAuthentication.ValidateBegin(begin), "nonce size exact");
        Throws(() => AccountAuthentication.ValidateVerifier(new AccountVerifier { Salt = new byte[32], StoredKey = new byte[32], ServerKey = new byte[32], Iterations = 4096 }), "weak verifier policy refused");
        Throws(() => AccountAuthentication.ValidateVerifier(new AccountVerifier { Salt = new byte[16], StoredKey = new byte[32], ServerKey = new byte[32], Iterations = 600000 }), "short salt refused");
    }
    private static void RoundTrip(RSA rsa, AccountServerIdentity identity, RSA otherRsa, AccountServerIdentity otherIdentity)
    {
        var hello = Hello(); var begin = AccountAuthentication.CreateBegin("register", "New_Player", "room-proof");
        var challenge = AccountAuthentication.CreateChallenge(rsa, identity, hello, begin);
        Check(challenge.Iterations == 600000 && challenge.Salt.Length == 32 && challenge.ServerNonce.Length == 32, "production KDF and nonce parameters");
        Check(challenge.Signature.Length == 384, "RSA3072 signature length");
        Throws(() => AccountAuthentication.CreateChallenge(otherRsa, identity, hello, begin), "mismatched signing identity rejected");
        var client = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, challenge, Password);
        Throws(() => { var _ = client.SessionKey; }, "session key unavailable before server proof");
        Check(client.Registration != null && client.Proof.Proof.Length == 32, "registration plus client proof");
        Check(client.Registration.WrappedKey.Length == 384 && client.Registration.Nonce.Length == 12 && client.Registration.Tag.Length == 16, "OAEP/AES-GCM envelope shape");
        var envelopeWire = JsonSerializer.Serialize(client.Registration);
        Check(!envelopeWire.Contains(Password) && !envelopeWire.Contains("StoredKey") && !envelopeWire.Contains("ServerKey"), "wire enrollment has neither plaintext nor exposed verifier");
        AccountVerifier verifier = AccountAuthentication.OpenRegistration(rsa, challenge, Clone(client.Registration));
        AccountAuthentication.ValidateVerifier(verifier);
        Check(verifier.Iterations == 600000 && verifier.StoredKey.Length == 32 && verifier.ServerKey.Length == 32, "server stores only SCRAM verifier");
        Check(!envelopeWire.Contains(Convert.ToBase64String(verifier.StoredKey)) && !envelopeWire.Contains(Convert.ToBase64String(verifier.ServerKey)), "verifier values encrypted on wire");
        Check(AccountAuthentication.VerifyProof(challenge, verifier, client.Proof, out byte[] serverProof, out byte[] serverKey), "registration client proof verifies");
        Check(client.VerifyServerProof(serverProof), "registration server proof verifies");
        Check(Equal(client.SessionKey, serverKey) && serverKey.Length == 32, "registration transport key matches");
        byte[] copy = client.SessionKey; copy[0] ^= 1; Check(Equal(client.SessionKey, serverKey), "session key getter cannot mutate owned key");
        byte[] before = client.Proof.Proof; client.Dispose();
        Check(before.All(x => x == 0) && client.Registration.Ciphertext.All(x => x == 0), "client disposal clears owned auth buffers");
        Throws(() => { var _ = client.SessionKey; }, "disposed key access refused");
        ChallengeFailures(rsa, identity, otherIdentity, hello, begin, challenge, verifier);
        RegistrationFailures(rsa, identity, otherRsa, hello, begin, challenge);
        DecryptedEnvelopeValidation(rsa, identity, hello, begin, challenge);
        Login(rsa, identity, verifier);
    }
    private static void ChallengeFailures(RSA rsa, AccountServerIdentity identity, AccountServerIdentity otherIdentity,
        ServerHello hello, AuthBegin begin, AccountAuthChallenge challenge, AccountVerifier verifier)
    {
        foreach (string pin in new[] { null, "", new string('A', 64), identity.ServerId.ToLowerInvariant(), otherIdentity.ServerId })
            Throws(() => AccountAuthentication.CreateClient(identity, pin, hello, begin, challenge, Password), "missing or wrong pin rejected before derivation");
        Throws(() => AccountAuthentication.CreateClient(otherIdentity, identity.ServerId, hello, begin, challenge, Password), "substituted server key rejected");
        var alteredIdentity = Clone(identity); alteredIdentity.PublicKey[15] ^= 1;
        Throws(() => AccountAuthentication.CreateClient(alteredIdentity, identity.ServerId, hello, begin, challenge, Password), "altered SPKI rejected");
        var wrongHello = Clone(hello); wrongHello.Version++;
        Throws(() => AccountAuthentication.CreateClient(identity, identity.ServerId, wrongHello, begin, challenge, Password), "incompatible hello rejected");
        foreach (Action<AccountAuthChallenge> mutate in new Action<AccountAuthChallenge>[] {
            c => c.Version++, c => c.BuildId = new string('B', 64), c => c.ServerId = otherIdentity.ServerId,
            c => c.SessionId += "x", c => c.HelloNonce += "x", c => c.ClientNonce[0] ^= 1, c => c.ServerNonce[0] ^= 1,
            c => c.Username = "another", c => c.Mode = "login", c => c.RoomProof += "x", c => c.Salt[0] ^= 1,
            c => c.Iterations = 4096, c => c.Signature[0] ^= 1, c => c.Signature = new byte[383] })
        {
            var changed = Clone(challenge); mutate(changed);
            Throws(() => AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, changed, Password), "tampered challenge field rejected");
        }
        Throws(() => AccountAuthentication.CreateClient(identity, identity.ServerId, Hello(), begin, challenge, Password), "signed challenge cannot replay onto another hello");
        var otherBegin = AccountAuthentication.CreateBegin("register", begin.Username, begin.RoomProof);
        Throws(() => AccountAuthentication.CreateClient(identity, identity.ServerId, hello, otherBegin, challenge, Password), "signed challenge cannot replay onto another begin");
        var fresh = AccountAuthentication.CreateChallenge(rsa, identity, hello, begin);
        Check(!Equal(fresh.ServerNonce, challenge.ServerNonce) && !Equal(fresh.Salt, challenge.Salt), "registration challenge nonce and salt are fresh");
        Throws(() => AccountAuthentication.CreateChallenge(rsa, identity, hello, begin, verifier), "registration cannot recycle verifier");
    }
    private static void RegistrationFailures(RSA rsa, AccountServerIdentity identity, RSA otherRsa,
        ServerHello hello, AuthBegin begin, AccountAuthChallenge challenge)
    {
        using var client = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, challenge, Password);
        Throws(() => AccountAuthentication.OpenRegistration(otherRsa, challenge, client.Registration), "wrong private key cannot decrypt registration");
        foreach (Action<AccountRegistration> mutate in new Action<AccountRegistration>[] {
            e => e.WrappedKey[0] ^= 1, e => e.Nonce[0] ^= 1, e => e.Ciphertext[0] ^= 1, e => e.Tag[0] ^= 1,
            e => e.WrappedKey = new byte[383], e => e.Nonce = new byte[11], e => e.Tag = new byte[15],
            e => e.Ciphertext = new byte[4097], e => e.Ciphertext = Array.Empty<byte>(), e => e.Ciphertext = null })
        {
            var envelope = Clone(client.Registration); mutate(envelope);
            Throws(() => AccountAuthentication.OpenRegistration(rsa, challenge, envelope), "tampered or oversized envelope rejected");
        }
        // Validly signed alternate challenges must still fail authenticated decryption.
        foreach (Action<ServerHello, AuthBegin> mutate in new Action<ServerHello, AuthBegin>[] {
            (h,b) => h.SessionId += "x", (h,b) => h.Nonce += "x", (h,b) => b.Username = "other_user",
            (h,b) => b.ClientNonce[0] ^= 1, (h,b) => b.RoomProof += "x" })
        {
            var nextHello = Clone(hello); var nextBegin = Clone(begin); mutate(nextHello, nextBegin);
            var anotherChallenge = AccountAuthentication.CreateChallenge(rsa, identity, nextHello, nextBegin);
            Throws(() => AccountAuthentication.OpenRegistration(rsa, anotherChallenge, client.Registration), "validly signed changed AAD cannot reuse registration");
        }
        var anotherFreshChallenge = AccountAuthentication.CreateChallenge(rsa, identity, hello, begin);
        Throws(() => AccountAuthentication.OpenRegistration(rsa, anotherFreshChallenge, client.Registration), "fresh signed challenge blocks envelope replay");
        var a = Clone(hello); a.SessionId = "a|b"; a.Nonce = "c";
        var bHello = Clone(hello); bHello.SessionId = "a"; bHello.Nonce = "b|c";
        var ca = AccountAuthentication.CreateChallenge(rsa, identity, a, begin);
        using var aClient = AccountAuthentication.CreateClient(identity, identity.ServerId, a, begin, ca, Password);
        var cb = AccountAuthentication.CreateChallenge(rsa, identity, bHello, begin);
        Throws(() => AccountAuthentication.OpenRegistration(rsa, cb, aClient.Registration), "length-prefixed binding cannot conflate delimiters");
    }
    private static void Login(RSA rsa, AccountServerIdentity identity, AccountVerifier verifier)
    {
        var hello = Hello(); var begin = AccountAuthentication.CreateBegin("login", "new_player");
        Throws(() => AccountAuthentication.CreateChallenge(rsa, identity, hello, begin), "login verifier required");
        var challenge = AccountAuthentication.CreateChallenge(rsa, identity, hello, begin, verifier);
        Check(Equal(challenge.Salt, verifier.Salt), "login retains account salt");
        using var client = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, challenge, Password);
        Check(client.Registration == null, "login never resends encrypted verifier");
        Throws(() => AccountAuthentication.OpenRegistration(rsa, challenge, new AccountRegistration()), "login challenge cannot enroll");
        Check(AccountAuthentication.VerifyProof(challenge, verifier, client.Proof, out byte[] serverProof, out byte[] serverKey), "login proof verifies existing account");
        Check(client.VerifyServerProof(serverProof) && Equal(client.SessionKey, serverKey), "login mutual proof and transport key match");
        var badProof = Clone(client.Proof); badProof.Proof[0] ^= 1;
        Check(!AccountAuthentication.VerifyProof(challenge, verifier, badProof, out byte[] failedProof, out byte[] failedKey) &&
            failedProof.Length == 0 && failedKey.Length == 0, "wrong proof reveals no server proof or session key");
        Check(!AccountAuthentication.VerifyProof(challenge, verifier, new AccountAuthProof { Proof = new byte[31] }, out _, out _), "short proof refused");
        Check(!AccountAuthentication.VerifyProof(challenge, verifier, null, out _, out _), "missing proof refused");
        using var wrongPassword = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, challenge, "A different password!");
        Check(!AccountAuthentication.VerifyProof(challenge, verifier, wrongPassword.Proof, out _, out _), "incorrect password cannot authenticate");
        Check(!wrongPassword.VerifyServerProof(serverProof), "incorrect-password client cannot accept real server proof");
        Throws(() => { var _ = wrongPassword.SessionKey; }, "failed mutual auth disposes transport key");
        using var wrongServerProof = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, challenge, Password);
        byte[] tamperedServerProof = (byte[])serverProof.Clone(); tamperedServerProof[0] ^= 1;
        Check(!wrongServerProof.VerifyServerProof(tamperedServerProof), "server proof tampering rejected");
        Throws(() => wrongServerProof.VerifyServerProof(serverProof), "failed server proof cannot be retried on disposed exchange");
        var secondChallenge = AccountAuthentication.CreateChallenge(rsa, identity, hello, begin, verifier);
        Check(!AccountAuthentication.VerifyProof(secondChallenge, verifier, client.Proof, out _, out _), "fresh challenge rejects prior proof");
        using var second = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, secondChallenge, Password);
        Check(AccountAuthentication.VerifyProof(secondChallenge, verifier, second.Proof, out byte[] secondProof, out byte[] secondKey), "fresh challenge authenticates independently");
        Check(second.VerifyServerProof(secondProof) && Equal(second.SessionKey, secondKey) && !Equal(serverKey, secondKey), "session keys differ across fresh challenges");
        foreach (Action<AccountAuthChallenge> mutate in new Action<AccountAuthChallenge>[] {
            c => c.SessionId += "x", c => c.HelloNonce += "x", c => c.ClientNonce[0] ^= 1, c => c.ServerNonce[0] ^= 1,
            c => c.Username = "another", c => c.Mode = "register", c => c.RoomProof = "different", c => c.Signature[0] ^= 1 })
        {
            var changed = Clone(challenge); mutate(changed);
            Check(!AccountAuthentication.VerifyProof(changed, verifier, client.Proof, out _, out _), "proof binds each signed transcript field");
        }
        var otherSalt = Clone(verifier); otherSalt.Salt[0] ^= 1;
        Check(!AccountAuthentication.VerifyProof(challenge, otherSalt, client.Proof, out _, out _), "verifier salt must match challenge");
        var storedWire = JsonSerializer.Serialize(verifier);
        Check(!storedWire.Contains(Password) && !storedWire.Contains("ClientKey") && !storedWire.Contains("SaltedPassword"), "persistence verifier excludes password and client key");
    }
    private static void DecryptedEnvelopeValidation(RSA rsa, AccountServerIdentity identity, ServerHello hello, AuthBegin begin, AccountAuthChallenge challenge)
    {
        using var client = AccountAuthentication.CreateClient(identity, identity.ServerId, hello, begin, challenge, Password);
        var original = client.Registration;
        byte[] key = rsa.Decrypt(original.WrappedKey, RSAEncryptionPadding.OaepSHA256);
        byte[] plaintext = new byte[original.Ciphertext.Length];
        byte[] aad = IndependentAad(challenge);
        try
        {
            using var aes = new AesGcm(key);
            aes.Decrypt(original.Nonce, original.Ciphertext, original.Tag, plaintext, aad);
            Check(!Encoding.UTF8.GetString(plaintext).Contains(Password), "even decrypted enrollment contains no password");
            var verifier = AccountAuthentication.OpenRegistration(rsa, challenge, original);
            byte[] expected = Pack(Text("NivalisTogether/account/v5/enrollment"), verifier.Salt, Int(verifier.Iterations), verifier.StoredKey, verifier.ServerKey);
            Check(Equal(plaintext, expected), "independent big-endian envelope and AAD encoding match");
            byte[] wrongSalt = (byte[])verifier.Salt.Clone(); wrongSalt[0] ^= 1;
            foreach (byte[] malformed in new[] {
                Pack(Text("wrong-domain"), verifier.Salt, Int(verifier.Iterations), verifier.StoredKey, verifier.ServerKey),
                Pack(Text("NivalisTogether/account/v5/enrollment"), wrongSalt, Int(verifier.Iterations), verifier.StoredKey, verifier.ServerKey),
                Pack(Text("NivalisTogether/account/v5/enrollment"), verifier.Salt, Int(4096), verifier.StoredKey, verifier.ServerKey),
                Pack(Text("NivalisTogether/account/v5/enrollment"), verifier.Salt, new byte[3], verifier.StoredKey, verifier.ServerKey),
                Pack(Text("NivalisTogether/account/v5/enrollment"), verifier.Salt, Int(verifier.Iterations), new byte[31], verifier.ServerKey),
                Pack(Text("NivalisTogether/account/v5/enrollment"), verifier.Salt, Int(verifier.Iterations), verifier.StoredKey, new byte[31]),
                expected.Concat(new byte[] { 0 }).ToArray(), expected.Take(expected.Length - 1).ToArray(),
                new byte[] { 255, 255, 255, 255 }, new byte[] { 0, 0, 0 } })
            {
                var envelope = Clone(original);
                envelope.Ciphertext = new byte[malformed.Length];
                envelope.Nonce = RandomNumberGenerator.GetBytes(12);
                aes.Encrypt(envelope.Nonce, malformed, envelope.Ciphertext, envelope.Tag, aad);
                Throws(() => AccountAuthentication.OpenRegistration(rsa, challenge, envelope), "authenticated malformed verifier payload rejected");
                CryptographicOperations.ZeroMemory(malformed);
            }
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(verifier.StoredKey); CryptographicOperations.ZeroMemory(verifier.ServerKey);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static byte[] IndependentAad(AccountAuthChallenge c)
    {
        byte[] challengeBytes = Pack(Text("NivalisTogether/account/v5/challenge"), Int(c.Version), Text(c.BuildId), Text(c.ServerId),
            Text(c.SessionId), Text(c.HelloNonce), c.ClientNonce, c.ServerNonce, Text(c.Username), Text(c.Mode), Text(c.RoomProof), c.Salt, Int(c.Iterations));
        byte[] transcript = Pack(Text("NivalisTogether/account/v5/scram"), challengeBytes, c.Signature);
        return Pack(Text("NivalisTogether/account/v5/enrollment-aad"), transcript);
    }
    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Int(int value)
    { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return bytes; }
    private static byte[] Pack(params byte[][] fields)
    {
        using var stream = new System.IO.MemoryStream();
        foreach (byte[] field in fields) { stream.Write(Int(field.Length)); stream.Write(field); }
        return stream.ToArray();
    }
}
