using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NivalisTogether.Dedicated
{
    public sealed class AccountServerIdentity
    {
        public string ServerId { get; set; } = "";
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    }
    public sealed class AuthBegin
    {
        public string Mode { get; set; } = "login";
        public string Username { get; set; } = "";
        public byte[] ClientNonce { get; set; } = Array.Empty<byte>();
        public string RoomProof { get; set; } = "";
    }
    public sealed class AccountAuthChallenge
    {
        public int Version { get; set; } = DedicatedProtocol.Version;
        public string BuildId { get; set; } = DedicatedProtocol.BuildId;
        public string ServerId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string HelloNonce { get; set; } = "";
        public byte[] ClientNonce { get; set; } = Array.Empty<byte>();
        public byte[] ServerNonce { get; set; } = Array.Empty<byte>();
        public string Username { get; set; } = "";
        public string Mode { get; set; } = "";
        public string RoomProof { get; set; } = "";
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public int Iterations { get; set; }
        public byte[] Signature { get; set; } = Array.Empty<byte>();
    }
    public sealed class AccountAuthProof { public byte[] Proof { get; set; } = Array.Empty<byte>(); }
    public sealed class AccountRegistration
    {
        public byte[] WrappedKey { get; set; } = Array.Empty<byte>();
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
        public byte[] Tag { get; set; } = Array.Empty<byte>();
    }
    // This is a server-side persistence record. Never include it in a wire response.
    public sealed class AccountVerifier
    {
        public byte[] Salt { get; set; } = Array.Empty<byte>();
        public int Iterations { get; set; }
        public byte[] StoredKey { get; set; } = Array.Empty<byte>();
        public byte[] ServerKey { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// V5 pinned-identity account authentication. Uses the RFC 5802/7677 SCRAM-SHA-256
    /// key/proof math with an application-specific, length-prefixed transcript, not SASL.
    /// The caller owns identity persistence, first-use trust, one-use challenges, expiry,
    /// rate limits, and associating each challenge with the connection that requested it.
    /// </summary>
    public static class AccountAuthentication
    {
        public const int KdfIterations = 600000, SaltBytes = 32, NonceBytes = 32;
        public const int UsernameMin = 3, UsernameMax = 24, PasswordMin = 12, PasswordMax = 128;
        public const int MaxPasswordBytes = 512, MaxEnrollmentBytes = 4096, RsaBits = 3072;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const string ChallengeDomain = "NivalisTogether/account/v5/challenge";
        private const string TranscriptDomain = "NivalisTogether/account/v5/scram";
        private const string EnrollmentDomain = "NivalisTogether/account/v5/enrollment";
        private const string AadDomain = "NivalisTogether/account/v5/enrollment-aad";
        private const string TransportDomain = "NivalisTogether/account/v5/transport-key";

        public static AccountServerIdentity Identity(RSA rsa)
        {
            RequireRsa(rsa);
            var publicKey = rsa.ExportSubjectPublicKeyInfo();
            return new AccountServerIdentity { PublicKey = publicKey, ServerId = Convert.ToHexString(SHA256.HashData(publicKey)) };
        }

        public static string NormalizeUsername(string username)
        {
            if (username == null || username.Length < UsernameMin || username.Length > UsernameMax)
                throw new ArgumentException("Username must contain 3 to 24 ASCII letters, digits, underscores, or hyphens.");
            var chars = username.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c >= 'A' && c <= 'Z') c = (char)(c + ('a' - 'A'));
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    throw new ArgumentException("Username must contain only ASCII letters, digits, underscores, or hyphens.");
                chars[i] = c;
            }
            return new string(chars);
        }

        // Passwords are exact UTF-8, without trimming, case folding, or Unicode normalization.
        // Bounds count Unicode scalar values; spaces and combining marks are allowed.
        public static void ValidatePassword(string password)
        {
            if (password == null) throw new ArgumentException("Password is required.");
            ValidatePasswordChars(password.AsSpan());
        }

        private static void ValidatePasswordChars(ReadOnlySpan<char> chars)
        {
            int count = 0, index = 0;
            while (index < chars.Length)
            {
                if (Rune.DecodeFromUtf16(chars.Slice(index), out Rune rune, out int consumed) != OperationStatus.Done)
                    throw new ArgumentException("Password contains invalid Unicode.");
                var category = Rune.GetUnicodeCategory(rune);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.Surrogate || category == UnicodeCategory.OtherNotAssigned ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator)
                    throw new ArgumentException("Password must contain printable characters.");
                count++; index += consumed;
                if (count > PasswordMax) throw new ArgumentException("Password must contain 12 to 128 characters.");
            }
            if (count < PasswordMin || Utf8.GetByteCount(chars) > MaxPasswordBytes)
                throw new ArgumentException("Password must contain 12 to 128 characters and at most 512 UTF-8 bytes.");
        }

        public static AuthBegin CreateBegin(string mode, string username, string roomProof = "")
        {
            var begin = new AuthBegin { Mode = mode, Username = NormalizeUsername(username),
                ClientNonce = RandomNumberGenerator.GetBytes(NonceBytes), RoomProof = roomProof };
            ValidateBegin(begin); return begin;
        }

        public static void ValidateBegin(AuthBegin begin)
        {
            if (begin == null) throw new ArgumentException("Account authentication request is missing.");
            ValidateMode(begin.Mode);
            if (NormalizeUsername(begin.Username) != begin.Username) throw new ArgumentException("Username must be normalized.");
            RequireLength(begin.ClientNonce, NonceBytes, "Client nonce");
            BoundedText(begin.RoomProof, 256, true);
        }

        public static void ValidateVerifier(AccountVerifier verifier)
        {
            if (verifier == null || verifier.Iterations != KdfIterations) throw new ArgumentException("Account verifier is invalid.");
            RequireLength(verifier.Salt, SaltBytes, "Salt");
            RequireLength(verifier.StoredKey, 32, "Stored key");
            RequireLength(verifier.ServerKey, 32, "Server key");
        }

        public static AccountAuthChallenge CreateChallenge(RSA rsa, AccountServerIdentity identity,
            ServerHello hello, AuthBegin begin, AccountVerifier verifier = null)
        {
            ValidateHello(hello); ValidateBegin(begin); RequireIdentity(rsa, identity);
            if (begin.Mode == "login") ValidateVerifier(verifier);
            else if (verifier != null) throw new ArgumentException("Registration cannot reuse an existing account verifier.");
            var challenge = new AccountAuthChallenge {
                Version = hello.Version, BuildId = hello.BuildId, ServerId = identity.ServerId,
                SessionId = hello.SessionId, HelloNonce = hello.Nonce, ClientNonce = Copy(begin.ClientNonce),
                ServerNonce = RandomNumberGenerator.GetBytes(NonceBytes), Username = begin.Username, Mode = begin.Mode,
                RoomProof = begin.RoomProof, Salt = verifier == null ? RandomNumberGenerator.GetBytes(SaltBytes) : Copy(verifier.Salt),
                Iterations = KdfIterations };
            challenge.Signature = rsa.SignData(ChallengeBytes(challenge), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return challenge;
        }

        public static ClientAuth CreateClient(AccountServerIdentity identity, string pinnedServerId,
            ServerHello hello, AuthBegin begin, AccountAuthChallenge challenge, string password)
        {
            ValidateHello(hello); ValidateBegin(begin); ValidateChallenge(challenge);
            using var rsa = OpenPinnedIdentity(identity, pinnedServerId);
            if (challenge.ServerId != identity.ServerId || challenge.Version != hello.Version ||
                challenge.BuildId != hello.BuildId || challenge.SessionId != hello.SessionId || challenge.HelloNonce != hello.Nonce ||
                challenge.Username != begin.Username || challenge.Mode != begin.Mode || challenge.RoomProof != begin.RoomProof ||
                !Equal(challenge.ClientNonce, begin.ClientNonce) ||
                !rsa.VerifyData(ChallengeBytes(challenge), challenge.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("Account challenge identity or connection binding is invalid.");
            ValidatePassword(password);
            byte[] passwordBytes = Utf8.GetBytes(password);
            try
            {
                using var keys = DeriveKeys(passwordBytes, challenge.Salt, challenge.Iterations);
                var transcript = Transcript(challenge);
                var proof = new AccountAuthProof { Proof = ClientProof(keys.ClientKey, keys.StoredKey, transcript) };
                var serverProof = Mac(keys.ServerKey, transcript);
                var sessionKey = TransportKey(keys.ClientKey, keys.ServerKey, transcript);
                AccountRegistration registration = null;
                try
                {
                    if (challenge.Mode == "register") registration = SealRegistration(rsa, challenge, keys);
                    return new ClientAuth(registration, proof, sessionKey, serverProof);
                }
                catch { Clear(sessionKey); Clear(serverProof); Clear(proof.Proof); throw; }
            }
            finally { Clear(passwordBytes); }
        }

        public static AccountVerifier OpenRegistration(RSA rsa, AccountAuthChallenge challenge, AccountRegistration registration)
        {
            RequireRsa(rsa); ValidateChallenge(challenge);
            if (challenge.Mode != "register" || challenge.ServerId != Identity(rsa).ServerId ||
                !rsa.VerifyData(ChallengeBytes(challenge), challenge.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("Registration challenge is invalid.");
            if (registration == null || registration.Ciphertext == null || registration.Ciphertext.Length == 0 ||
                registration.Ciphertext.Length > MaxEnrollmentBytes) throw new CryptographicException("Registration envelope is invalid.");
            RequireLength(registration.WrappedKey, RsaBits / 8, "Wrapped registration key");
            RequireLength(registration.Nonce, 12, "Registration nonce");
            RequireLength(registration.Tag, 16, "Registration authentication tag");
            byte[] key = null, plain = new byte[registration.Ciphertext.Length];
            AccountVerifier verifier = null;
            bool succeeded = false;
            try
            {
                key = rsa.Decrypt(registration.WrappedKey, RSAEncryptionPadding.OaepSHA256);
                RequireLength(key, 32, "Registration key");
                using (var aes = new AesGcm(key))
                    aes.Decrypt(registration.Nonce, registration.Ciphertext, registration.Tag, plain, EnrollmentAad(challenge));
                int offset = 0;
                byte[] domain = ReadField(plain, ref offset, 128);
                if (!Equal(domain, Utf8.GetBytes(EnrollmentDomain))) throw new CryptographicException("Registration payload is invalid.");
                byte[] salt = ReadField(plain, ref offset, SaltBytes);
                byte[] iterations = ReadField(plain, ref offset, 4);
                if (iterations.Length != 4) throw new CryptographicException("Registration KDF is invalid.");
                verifier = new AccountVerifier { Salt = salt, Iterations = BinaryPrimitives.ReadInt32BigEndian(iterations) };
                verifier.StoredKey = ReadField(plain, ref offset, 32);
                verifier.ServerKey = ReadField(plain, ref offset, 32);
                if (offset != plain.Length) throw new CryptographicException("Registration payload has trailing data.");
                ValidateVerifier(verifier);
                if (verifier.Iterations != challenge.Iterations || !Equal(verifier.Salt, challenge.Salt))
                    throw new CryptographicException("Registration verifier does not match the signed challenge.");
                succeeded = true;
                return verifier;
            }
            finally
            {
                Clear(key); Clear(plain);
                if (!succeeded && verifier != null) { Clear(verifier.StoredKey); Clear(verifier.ServerKey); }
            }
        }

        public static bool VerifyProof(AccountAuthChallenge challenge, AccountVerifier verifier, AccountAuthProof proof,
            out byte[] serverProof, out byte[] sessionKey)
        {
            serverProof = Array.Empty<byte>(); sessionKey = Array.Empty<byte>();
            ValidateChallenge(challenge); ValidateVerifier(verifier);
            if (proof?.Proof == null || proof.Proof.Length != 32 || challenge.Iterations != verifier.Iterations ||
                !Equal(challenge.Salt, verifier.Salt)) return false;
            byte[] transcript = Transcript(challenge), signature = Mac(verifier.StoredKey, transcript), clientKey = new byte[32];
            byte[] storedKey = null;
            try
            {
                for (int i = 0; i < 32; i++) clientKey[i] = (byte)(proof.Proof[i] ^ signature[i]);
                storedKey = SHA256.HashData(clientKey);
                if (!Equal(storedKey, verifier.StoredKey)) return false;
                serverProof = Mac(verifier.ServerKey, transcript);
                sessionKey = TransportKey(clientKey, verifier.ServerKey, transcript);
                return true;
            }
            finally { Clear(signature); Clear(clientKey); Clear(storedKey); }
        }

        private static AccountRegistration SealRegistration(RSA rsa, AccountAuthChallenge challenge, ScramKeys keys)
        {
            // Enrollment sends only the verifier inside authenticated encryption.
            // The password never reaches the server, including in decrypted memory.
            byte[] key = RandomNumberGenerator.GetBytes(32), plain = Pack(Utf8.GetBytes(EnrollmentDomain),
                challenge.Salt, Int(challenge.Iterations), keys.StoredKey, keys.ServerKey);
            try
            {
                if (plain.Length > MaxEnrollmentBytes) throw new CryptographicException("Registration payload is too large.");
                var result = new AccountRegistration { WrappedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256),
                    Nonce = RandomNumberGenerator.GetBytes(12), Ciphertext = new byte[plain.Length], Tag = new byte[16] };
                using (var aes = new AesGcm(key)) aes.Encrypt(result.Nonce, plain, result.Ciphertext, result.Tag, EnrollmentAad(challenge));
                return result;
            }
            finally { Clear(key); Clear(plain); }
        }

        private static RSA OpenPinnedIdentity(AccountServerIdentity identity, string pinnedServerId)
        {
            if (identity == null || identity.PublicKey == null || identity.PublicKey.Length < 256 || identity.PublicKey.Length > 1024 ||
                !IsFingerprint(identity.ServerId) || !IsFingerprint(pinnedServerId))
                throw new CryptographicException("A pinned server identity is required.");
            string actual = Convert.ToHexString(SHA256.HashData(identity.PublicKey));
            if (!Equal(Utf8.GetBytes(actual), Utf8.GetBytes(identity.ServerId)) ||
                !Equal(Utf8.GetBytes(actual), Utf8.GetBytes(pinnedServerId)))
                throw new CryptographicException("Server identity does not match the trusted fingerprint.");
            var rsa = RSA.Create();
            try
            {
                rsa.ImportSubjectPublicKeyInfo(identity.PublicKey, out int consumed);
                if (consumed != identity.PublicKey.Length) throw new CryptographicException("Server identity has trailing data.");
                RequireRsa(rsa);
                // Pin one canonical representation of the key, not an alternate ASN.1 encoding.
                if (!Equal(rsa.ExportSubjectPublicKeyInfo(), identity.PublicKey)) throw new CryptographicException("Server identity is not canonical.");
                return rsa;
            }
            catch { rsa.Dispose(); throw; }
        }

        private static void RequireIdentity(RSA rsa, AccountServerIdentity identity)
        {
            var actual = Identity(rsa);
            if (identity == null || actual.ServerId != identity.ServerId || !Equal(actual.PublicKey, identity.PublicKey))
                throw new CryptographicException("Server signing key and identity differ.");
        }
        private static void RequireRsa(RSA rsa)
        { if (rsa == null || rsa.KeySize != RsaBits) throw new CryptographicException("Account identity requires a 3072-bit RSA key."); }
        private static bool IsFingerprint(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
        private static void ValidateHello(ServerHello hello)
        {
            if (hello == null || hello.Version != DedicatedProtocol.Version || hello.BuildId != DedicatedProtocol.BuildId)
                throw new CryptographicException("Server protocol or build is incompatible.");
            BoundedText(hello.SessionId, 128); BoundedText(hello.Nonce, 128);
        }
        private static void ValidateChallenge(AccountAuthChallenge challenge)
        {
            if (challenge == null || challenge.Version != DedicatedProtocol.Version || challenge.BuildId != DedicatedProtocol.BuildId ||
                !IsFingerprint(challenge.ServerId) || challenge.Iterations != KdfIterations)
                throw new CryptographicException("Account challenge parameters are invalid.");
            BoundedText(challenge.SessionId, 128); BoundedText(challenge.HelloNonce, 128);
            ValidateBegin(new AuthBegin { Mode = challenge.Mode, Username = challenge.Username,
                ClientNonce = challenge.ClientNonce, RoomProof = challenge.RoomProof });
            RequireLength(challenge.ServerNonce, NonceBytes, "Server nonce"); RequireLength(challenge.Salt, SaltBytes, "Salt");
            RequireLength(challenge.Signature, RsaBits / 8, "Challenge signature");
        }
        private static void ValidateMode(string mode)
        { if (mode != "login" && mode != "register") throw new ArgumentException("Account mode must be login or register."); }
        private static void BoundedText(string value, int maximum, bool empty = false)
        {
            if (value == null || (!empty && value.Length == 0) || value.Length > maximum || Utf8.GetByteCount(value) > maximum)
                throw new ArgumentException("Account binding text is invalid.");
            foreach (char c in value) if (char.IsControl(c)) throw new ArgumentException("Account binding text is invalid.");
        }
        private static void RequireLength(byte[] bytes, int length, string name)
        { if (bytes == null || bytes.Length != length) throw new ArgumentException(name + " has an invalid length."); }

        // Every field, including integers and the domain tag, has an unsigned 32-bit
        // big-endian byte-length prefix. Integers themselves use big-endian Int32.
        private static byte[] ChallengeBytes(AccountAuthChallenge c) => Pack(Utf8.GetBytes(ChallengeDomain), Int(c.Version),
            Utf8.GetBytes(c.BuildId), Utf8.GetBytes(c.ServerId), Utf8.GetBytes(c.SessionId), Utf8.GetBytes(c.HelloNonce),
            c.ClientNonce, c.ServerNonce, Utf8.GetBytes(c.Username), Utf8.GetBytes(c.Mode), Utf8.GetBytes(c.RoomProof), c.Salt, Int(c.Iterations));
        private static byte[] Transcript(AccountAuthChallenge c) => Pack(Utf8.GetBytes(TranscriptDomain), ChallengeBytes(c), c.Signature);
        private static byte[] EnrollmentAad(AccountAuthChallenge c) => Pack(Utf8.GetBytes(AadDomain), Transcript(c));
        private static byte[] Int(int value)
        { var result = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(result, value); return result; }
        private static byte[] Pack(params byte[][] fields)
        {
            int length = 0;
            foreach (var field in fields) length = checked(length + 4 + field.Length);
            var result = new byte[length]; int offset = 0;
            foreach (var field in fields)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), (uint)field.Length); offset += 4;
                field.CopyTo(result, offset); offset += field.Length;
            }
            return result;
        }
        private static byte[] ReadField(byte[] bytes, ref int offset, int maximum)
        {
            if (bytes.Length - offset < 4) throw new CryptographicException("Registration payload is truncated.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4;
            if (length > maximum || length > bytes.Length - offset) throw new CryptographicException("Registration payload field is invalid.");
            var result = bytes.AsSpan(offset, (int)length).ToArray(); offset += (int)length; return result;
        }
        private static byte[] Copy(byte[] bytes) => (byte[])bytes.Clone();
        internal static bool Equal(byte[] a, byte[] b) => a != null && b != null && CryptographicOperations.FixedTimeEquals(a, b);
        internal static void Clear(byte[] bytes) { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
        private static byte[] Mac(byte[] key, byte[] data)
        { using var hmac = new HMACSHA256(key); return hmac.ComputeHash(data); }

        // Internal primitives permit published RFC vectors without weakening public KDF policy.
        internal static ScramKeys DeriveKeys(byte[] password, byte[] salt, int iterations)
        {
            if (password == null || salt == null || salt.Length == 0 || iterations < 1) throw new ArgumentException("SCRAM parameters are invalid.");
            byte[] salted = null, client = null, stored = null, server = null;
            try
            {
                using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256)) salted = kdf.GetBytes(32);
                client = Mac(salted, Utf8.GetBytes("Client Key")); stored = SHA256.HashData(client); server = Mac(salted, Utf8.GetBytes("Server Key"));
                return new ScramKeys(client, stored, server);
            }
            catch { Clear(client); Clear(stored); Clear(server); throw; }
            finally { Clear(salted); }
        }
        internal static byte[] ClientProof(byte[] clientKey, byte[] storedKey, byte[] transcript)
        {
            byte[] signature = Mac(storedKey, transcript), proof = new byte[32];
            try { for (int i = 0; i < 32; i++) proof[i] = (byte)(clientKey[i] ^ signature[i]); return proof; }
            finally { Clear(signature); }
        }
        internal static byte[] ServerProof(byte[] serverKey, byte[] transcript) => Mac(serverKey, transcript);
        private static byte[] TransportKey(byte[] clientKey, byte[] serverKey, byte[] transcript)
        {
            // HKDF-SHA-256 extract/expand, one 32-byte output block. Both parties have
            // ClientKey only after the password/proof succeeds. No verifier is sent.
            byte[] material = Pack(clientKey, serverKey), salt = SHA256.HashData(transcript), prk = null;
            try
            {
                prk = Mac(salt, material);
                byte[] info = Pack(Utf8.GetBytes(TransportDomain), transcript), expand = new byte[info.Length + 1];
                info.CopyTo(expand, 0); expand[expand.Length - 1] = 1;
                return Mac(prk, expand);
            }
            finally { Clear(material); Clear(prk); }
        }
        internal sealed class ScramKeys : IDisposable
        {
            internal readonly byte[] ClientKey, StoredKey, ServerKey;
            internal ScramKeys(byte[] client, byte[] stored, byte[] server) { ClientKey = client; StoredKey = stored; ServerKey = server; }
            public void Dispose() { Clear(ClientKey); Clear(StoredKey); Clear(ServerKey); }
        }
    }

    public sealed class ClientAuth : IDisposable
    {
        private byte[] sessionKey, expectedServerProof;
        private bool verified, disposed;
        public AccountRegistration Registration { get; }
        public AccountAuthProof Proof { get; }
        // A caller cannot enable authenticated transport before verifying the server.
        // A fresh copy is returned; its eventual owner must clear it when disconnected.
        public byte[] SessionKey
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(ClientAuth));
                if (!verified) throw new InvalidOperationException("Verify the server proof before enabling account transport.");
                return (byte[])sessionKey.Clone();
            }
        }
        internal ClientAuth(AccountRegistration registration, AccountAuthProof proof, byte[] key, byte[] serverProof)
        { Registration = registration; Proof = proof; sessionKey = key; expectedServerProof = serverProof; }
        public bool VerifyServerProof(byte[] serverProof)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ClientAuth));
            if (!AccountAuthentication.Equal(expectedServerProof, serverProof)) { Dispose(); return false; }
            verified = true; return true;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; verified = false;
            AccountAuthentication.Clear(sessionKey); AccountAuthentication.Clear(expectedServerProof); AccountAuthentication.Clear(Proof.Proof);
            if (Registration != null)
            {
                AccountAuthentication.Clear(Registration.WrappedKey); AccountAuthentication.Clear(Registration.Nonce);
                AccountAuthentication.Clear(Registration.Ciphertext); AccountAuthentication.Clear(Registration.Tag);
            }
        }
    }
}
