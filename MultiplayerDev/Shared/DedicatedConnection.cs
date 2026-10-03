using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisTogether.Dedicated
{
    /// <summary>Single-thread Tick-driven, length-framed TCP channel. ConnectAsync is safe to await off the Unity thread.</summary>
    public sealed class DedicatedConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly Socket _socket;
        private readonly Queue<byte[]> _outgoing = new Queue<byte[]>();
        private readonly Queue<(WireMessage Message, int Bytes)> _incoming = new Queue<(WireMessage, int)>();
        private const int ReceiveHighWaterFrames = 32, ReceiveHighWaterBytes = 256 * 1024;
        private readonly byte[] _buffer = new byte[DedicatedProtocol.MaxFrameBytes + 4];
        private int _received, _wanted = 4, _sendOffset, _incomingBytes;
        private ulong _sendSequence, _receiveSequence;
        private bool _adoptServerSession, _disposed, _authenticationBoundary;
        private readonly bool _serverSide;
        private byte[] _key;
        public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
        public bool Connected => !_disposed;
        public string Status { get; private set; } = "Connected";
        // Display-only terminal diagnostic. Before authentication this is an
        // untrusted server-supplied error, never proof or permission to dispatch.
        public string TerminalServerErrorMessage { get; private set; } = "";
        public void ClearTerminalServerError() => TerminalServerErrorMessage = "";
        public int PendingSendBytes { get; private set; }
        public DateTime LastReceiveUtc { get; private set; } = DateTime.UtcNow;
        // The client must verify the challenge-bound server proof inside this
        // callback before returning a key. This frame is then MAC-checked below
        // before any payload is queued; following coalesced frames use that key.
        public Func<WireMessage,byte[]> AuthenticateAccountFrame { get; set; }
        public IPEndPoint RemoteEndpoint => (IPEndPoint)_socket.RemoteEndPoint;

        private DedicatedConnection(TcpClient client, bool adoptServerSession)
        { _client = client; _socket = client.Client; _socket.Blocking = false; _socket.NoDelay = true; _adoptServerSession = adoptServerSession; _serverSide = !adoptServerSession; }
        public static DedicatedConnection FromAccepted(TcpClient client) => new DedicatedConnection(client, false);
        public static async Task<DedicatedConnection> ConnectAsync(string host, int port, double timeoutSeconds = 8, CancellationToken cancellationToken = default)
        {
            if (!DedicatedProtocol.Text(host, 253) || port < 1 || port > 65535 || timeoutSeconds < .1 || timeoutSeconds > 60)
                throw new ArgumentException("Invalid host, port or connection timeout.");
            var client = new TcpClient();
            try
            {
                using var cancellation = cancellationToken.Register(client.Dispose);
                await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested(); return new DedicatedConnection(client, true);
            }
            catch { client.Dispose(); throw; }
        }
        public void SetAuthenticationKey(byte[] key)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("Session key must be 32 bytes.");
            if (_key != null) throw new InvalidOperationException("Session authentication is already established.");
            _key = (byte[])key.Clone();
        }
        public bool Send<T>(string type, T payload, string requestId = "")
        {
            if (_disposed || !DedicatedProtocol.Text(type, 48) || !DedicatedProtocol.Text(requestId, 64, true)) return false;
            if (_outgoing.Count >= DedicatedProtocol.MaxQueueFrames) return false;
            var message = new WireMessage { Type = type, RequestId = requestId, SessionId = SessionId, Sequence = _sendSequence + 1,
                Payload = JsonSerializer.SerializeToElement(payload, DedicatedProtocol.Json) };
            if (_key != null) message.Mac = Sign(message, true);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json);
            if (json.Length > DedicatedProtocol.MaxFrameBytes || PendingSendBytes + json.Length + 4 > DedicatedProtocol.MaxQueueBytes) return false;
            byte[] frame = new byte[json.Length + 4];
            frame[0] = (byte)(json.Length >> 24); frame[1] = (byte)(json.Length >> 16); frame[2] = (byte)(json.Length >> 8); frame[3] = (byte)json.Length;
            Buffer.BlockCopy(json, 0, frame, 4, json.Length);
            _outgoing.Enqueue(frame); PendingSendBytes += frame.Length; _sendSequence++; return true;
        }
        public bool TryReceive(out WireMessage message)
        {
            if (_incoming.Count == 0) { message = null; return false; }
            var item = _incoming.Dequeue(); _incomingBytes -= item.Bytes; message = item.Message; return true;
        }
        public void Tick()
        {
            if (_disposed) return;
            string operation = "send";
            try
            {
                int budget = 256 * 1024;
                while (_outgoing.Count > 0 && budget > 0)
                {
                    byte[] frame = _outgoing.Peek(); int sent;
                    try { sent = _socket.Send(frame, _sendOffset, Math.Min(frame.Length - _sendOffset, budget), SocketFlags.None); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                    if (sent == 0) { Fail("Remote closed the TCP stream."); return; }
                    _sendOffset += sent; PendingSendBytes -= sent; budget -= sent;
                    if (_sendOffset == frame.Length) { _outgoing.Dequeue(); _sendOffset = 0; }
                }
                operation = "receive";
                budget = 256 * 1024; int frames = 0;
                while (budget > 0 && frames < 64)
                {
                    // A bounded main-thread consumer may need several ticks to
                    // apply the previous messages. Leave further bytes in TCP
                    // instead of filling the hard queue cap and disconnecting.
                    // Finish an already-started frame; outgoing ACKs above keep
                    // flowing, and the authentication boundary below is unchanged.
                    if (_received == 0 && (_incoming.Count >= ReceiveHighWaterFrames || _incomingBytes >= ReceiveHighWaterBytes)) break;
                    int count;
                    try { count = _socket.Receive(_buffer, _received, Math.Min(_wanted - _received, budget), SocketFlags.None); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                    if (count == 0) { Fail("Remote closed the TCP stream."); return; }
                    _received += count; budget -= count;
                    if (_received != _wanted) continue;
                    if (_wanted == 4)
                    {
                        long length = ((long)_buffer[0] << 24) | ((long)_buffer[1] << 16) | ((long)_buffer[2] << 8) | _buffer[3];
                        if (length < 2 || length > DedicatedProtocol.MaxFrameBytes) { Fail("Invalid TCP frame length."); return; }
                        _wanted = (int)length + 4; continue;
                    }
                    if (!AcceptFrame(_wanted - 4)) return;
                    _received = 0; _wanted = 4; frames++;
                    // Dispatch the terminal auth message before decoding its
                    // coalesced successor. Unsigned prequeued frames must never
                    // inherit a role established by the preceding auth frame.
                    if(_authenticationBoundary){_authenticationBoundary=false;return;}
                }
            }
            catch (SocketException ex)
            { Fail("TCP socket " + operation + " failed: " + ex.SocketErrorCode + " (native " + ex.NativeErrorCode + ")."); }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is JsonException || ex is FormatException || ex is ArgumentException)
            { Fail("TCP protocol error: " + ex.GetType().Name); }
        }
        private bool AcceptFrame(int length)
        {
            WireMessage message = JsonSerializer.Deserialize<WireMessage>(new ReadOnlySpan<byte>(_buffer, 4, length), DedicatedProtocol.Json);
            if (message == null || !DedicatedProtocol.Text(message.Type, 48) || !DedicatedProtocol.Text(message.RequestId, 64, true) ||
                !Guid.TryParseExact(message.SessionId, "N", out _) || message.Sequence != _receiveSequence + 1 || message.Payload.ValueKind == JsonValueKind.Undefined)
            { Fail("Invalid TCP envelope or sequence."); return false; }
            if (_adoptServerSession)
            {
                if (message.Type != "serverHello" || message.Sequence != 1) { Fail("Missing server handshake."); return false; }
                SessionId = message.SessionId; _adoptServerSession = false;
            }
            if (message.SessionId != SessionId) { Fail("Stale or mismatched session."); return false; }
            if(_key==null&&!_serverSide&&message.Type=="accountAuthenticated"&&AuthenticateAccountFrame!=null){
                var authenticate=AuthenticateAccountFrame;AuthenticateAccountFrame=null;byte[] key=null;
                try{key=authenticate(message);SetAuthenticationKey(key);}
                catch(Exception ex) when(ex is CryptographicException||ex is InvalidDataException||ex is InvalidOperationException||ex is ArgumentException)
                {Fail("Account server proof verification failed.");return false;}
                finally{if(key!=null)CryptographicOperations.ZeroMemory(key);}
            }
            if (_key != null)
            {
                string receivedMac = message.Mac; message.Mac = "";
                string expectedMac = Sign(message, false); message.Mac = receivedMac;
                if (!DedicatedProtocol.EqualHex(receivedMac, expectedMac)) { Fail("Session authentication failed."); return false; }
            }
            else if (!string.IsNullOrEmpty(message.Mac)) { Fail("Unexpected authenticated frame before handshake."); return false; }
            if (_incoming.Count >= DedicatedProtocol.MaxQueueFrames || _incomingBytes + length > DedicatedProtocol.MaxQueueBytes)
            { Fail("Inbound queue limit reached."); return false; }
            message.ReceivedFrameBytes = length + 4;
            _incoming.Enqueue((message, length)); _incomingBytes += length; _receiveSequence = message.Sequence; LastReceiveUtc = DateTime.UtcNow;
            if(_serverSide&&_key==null&&(message.Type=="join"||message.Type=="accountProof"||message.Type=="accountRegister"))_authenticationBoundary=true;
            return true;
        }
        private string Sign(WireMessage message, bool outgoing)
        {
            // Separate directions: a relayed request cannot be reflected back as an authoritative worker reply.
            bool serverToClient = outgoing ? _serverSide : !_serverSide;
            return CanonicalJson.HmacHex(message, _key, serverToClient);
        }
        private void Fail(string status)
        {
            // A rejection and FIN/reset may arrive in one Tick. Retain only its
            // bounded diagnostic before discarding queued data. Pre-auth errors
            // do not establish a session key or authorize queued world messages.
            string rejection = "", rejectionMessage = "";
            if (_key != null || !_serverSide)
                foreach (var item in _incoming)
                    if (item.Message.Type == "error")
                        try
                        {
                            var error = item.Message.Read<ProtocolError>();
                            if (error != null && DedicatedProtocol.Text(error.Code, 64) && DedicatedProtocol.Text(error.Message, 256))
                            {
                                rejection = "Remote protocol error [" + error.Code + "]: " + error.Message + " ";
                                rejectionMessage = error.Message;
                            }
                        }
                        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { }
            Dispose(); Status = rejection + status;
            if (!_serverSide) TerminalServerErrorMessage = rejectionMessage;
        }
        public void Dispose()
        {
            ClearTerminalServerError();
            if (_disposed) return;
            _disposed = true; _client.Dispose(); _outgoing.Clear(); _incoming.Clear(); AuthenticateAccountFrame=null; PendingSendBytes = _incomingBytes = 0;
            if (_key != null) CryptographicOperations.ZeroMemory(_key);
            Status = "Disconnected";
        }
    }
}
