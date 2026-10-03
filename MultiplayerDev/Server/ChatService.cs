using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using NivalisTogether.Dedicated;

namespace NivalisTogether.Server
{
    internal sealed partial class DedicatedService
    {
        private const int ChatRecentLimit = 32;
        private readonly ConditionalWeakTable<Peer, ChatPeerState> _chatPeers = new ConditionalWeakTable<Peer, ChatPeerState>();

        private sealed class ChatPeerState
        {
            public double Tokens = 5;
            public DateTime Refilled;
            public readonly Dictionary<string, ChatResult> Recent = new Dictionary<string, ChatResult>(StringComparer.Ordinal);
            public readonly Queue<string> Order = new Queue<string>();
            public bool Take(DateTime now)
            {
                if (Refilled == default) Refilled = now;
                // A backwards system-clock adjustment cannot mint tokens.
                if (now > Refilled) { Tokens = Math.Min(5, Tokens + (now - Refilled).TotalSeconds / 2); Refilled = now; }
                if (Tokens < 1) return false;
                Tokens -= 1; return true;
            }
            public void Remember(ChatRequest request, ChatMessage message, ChatRejection rejection)
            {
                if (!ChatProtocol.IsId(request?.RequestId)) return;
                while (Order.Count >= ChatRecentLimit) Recent.Remove(Order.Dequeue());
                Order.Enqueue(request.RequestId);
                Recent.Add(request.RequestId, new ChatResult { Fingerprint = ChatResult.Digest(request), Message = message, Rejection = rejection });
            }
        }

        private sealed class ChatResult
        {
            // Even a malformed 64 KiB transport frame retains only a fixed-size
            // digest. Chat bodies are never saved or sent to the native worker.
            public string Fingerprint;
            public ChatMessage Message;
            public ChatRejection Rejection;
            public bool Matches(ChatRequest value) => Fingerprint == Digest(value);
            public static string Digest(ChatRequest value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { value.Channel, TargetName = value.TargetName?.ToUpperInvariant(), value.Text }, DedicatedProtocol.Json)));
        }

        // Called before the world-load barrier. Chat is never forwarded to the
        // Unity worker or journalled as a world event, and never trusts a client map/name.
        private bool TryHandleChat(Peer peer, WireMessage wire, DateTime now)
        {
            if (wire.Type != ChatProtocol.SendKind) return false;
            ChatRequest request;
            try { request = wire.Read<ChatRequest>(); }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is NotSupportedException)
            { SendChatRejection(peer, "", "malformed", "Invalid chat request."); return true; }
            string requestId = ChatProtocol.IsId(request?.RequestId) ? request.RequestId : "";
            if (!IsChatPlayer(peer) || !peer.Loaded)
            { SendChatRejection(peer, requestId, "not_ready", "Join and finish loading the world before sending chat."); return true; }

            var state = _chatPeers.GetValue(peer, _ => new ChatPeerState());
            if (requestId.Length > 0 && state.Recent.TryGetValue(requestId, out var previous))
            {
                if (!previous.Matches(request))
                    SendChatRejection(peer, requestId, "duplicate_request", "That chat request identifier was already used.");
                else if (previous.Message != null) Send(peer, ChatProtocol.MessageKind, previous.Message);
                else Send(peer, ChatProtocol.RejectedKind, previous.Rejection);
                return true;
            }
            if (!state.Take(now))
            { RejectChat(peer, state, request, "rate_limit", "Please wait a moment before sending another chat message."); return true; }
            if (!ChatProtocol.TryValidateRequest(request, out string error))
            { RejectChat(peer, state, request, "malformed", error); return true; }

            Peer target = null;
            Peer[] recipients;
            if (request.Channel == "local")
            {
                var context = peer.SceneMembership.Current;
                if (context == null || context.TravelPending)
                { RejectChat(peer, state, request, "map_not_ready", "Wait until you arrive before speaking locally."); return true; }
                recipients = _peers.Where(p => IsChatPlayer(p) && p.Loaded && HasChatScene(p, context.Scene)).ToArray();
            }
            else if (request.Channel == "global") recipients = _peers.Where(IsChatPlayer).ToArray();
            else
            {
                string name = AccountAuthentication.NormalizeUsername(request.TargetName);
                target = _peers.FirstOrDefault(p => IsChatPlayer(p) && string.Equals(p.Account.Username, name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                { RejectChat(peer, state, request, "player_not_found", "That player is not connected. Use their account name."); return true; }
                recipients = ReferenceEquals(target, peer) ? new[] { peer } : new[] { peer, target };
            }
            var message = new ChatMessage { MessageId = Guid.NewGuid().ToString("N"), RequestId = request.RequestId,
                Channel = request.Channel, SenderPlayerId = peer.Id, SenderName = peer.Account.Username,
                TargetPlayerId = target?.Id ?? "", TargetName = target?.Account.Username ?? "", Text = request.Text,
                UnixTimeMilliseconds = new DateTimeOffset(now.ToUniversalTime()).ToUnixTimeMilliseconds() };
            state.Remember(request, message, null);
            foreach (var recipient in recipients) Send(recipient, ChatProtocol.MessageKind, message);
            return true;
        }

        private static bool IsChatPlayer(Peer peer) => peer.Role == "player" && peer.Account != null && peer.Connection.Connected;
        private static bool HasChatScene(Peer peer, string scene)
        { var context = peer.SceneMembership.Current; return context != null && !context.TravelPending && context.Scene == scene; }
        private static void SendChatRejection(Peer peer, string requestId, string code, string message)
            => Send(peer, ChatProtocol.RejectedKind, new ChatRejection { RequestId = requestId, Code = code, Message = message });
        private static void RejectChat(Peer peer, ChatPeerState state, ChatRequest request, string code, string message)
        {
            var rejection = new ChatRejection { RequestId = ChatProtocol.IsId(request?.RequestId) ? request.RequestId : "", Code = code, Message = message };
            state.Remember(request, null, rejection);
            Send(peer, ChatProtocol.RejectedKind, rejection);
        }
    }
}
