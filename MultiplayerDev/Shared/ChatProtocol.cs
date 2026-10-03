using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NivalisTogether.Dedicated
{
    public sealed class ChatRequest
    {
        public string RequestId { get; set; } = "";
        public string Channel { get; set; } = "local";
        public string TargetName { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class ChatMessage
    {
        public string MessageId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Channel { get; set; } = "local";
        public string SenderPlayerId { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string TargetPlayerId { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string Text { get; set; } = "";
        public long UnixTimeMilliseconds { get; set; }
    }

    public sealed class ChatRejection
    {
        public string RequestId { get; set; } = "";
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>Chat is an authenticated service channel, separate from world events and simulation.</summary>
    public static class ChatProtocol
    {
        public const string SendKind = "chatSend", MessageKind = "chatMessage", RejectedKind = "chatRejected";
        public const int MaxInputLength = 580, MaxMessageLength = 512, MaxTextLength = MaxMessageLength, MaxUtf8Bytes = 2048;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const string Usage = "Use /s message for local chat, /g message for global chat, or /w playername message to whisper.";

        public static bool TryParseInput(string raw, out ChatRequest request, out string error)
        {
            request = null;
            if (raw == null || raw.Length > MaxInputLength)
            { error = "Chat input must contain no more than 580 characters."; return false; }
            // Validate before trimming so a pasted control character is never silently hidden.
            if (!SafeUnicode(raw)) { error = "Chat cannot contain control or hidden formatting characters."; return false; }
            string text = raw.Trim(), channel = "local", target = "";
            if (text.StartsWith("/", StringComparison.Ordinal))
            {
                int split = WordEnd(text);
                string command = text.Substring(0, split).ToLowerInvariant();
                text = text.Substring(split).TrimStart();
                switch (command)
                {
                    case "/s": channel = "local"; break;
                    case "/g": channel = "global"; break;
                    case "/w":
                        channel = "whisper";
                        int nameEnd = WordEnd(text);
                        if (!TryUsername(text.Substring(0, nameEnd), out target))
                        { error = "Whisper to an account name of 3 to 24 letters, digits, underscores, or hyphens. " + Usage; return false; }
                        text = text.Substring(nameEnd).TrimStart();
                        break;
                    default: error = Usage; return false;
                }
            }
            var candidate = new ChatRequest { RequestId = Guid.NewGuid().ToString("N"), Channel = channel, TargetName = target, Text = text };
            if (!TryValidateRequest(candidate, out error)) return false;
            request = candidate; return true;
        }

        public static bool TryValidateRequest(ChatRequest request, out string error)
        {
            if (request == null || !IsId(request.RequestId))
            { error = "Invalid chat request identifier."; return false; }
            if (request.Channel != "local" && request.Channel != "global" && request.Channel != "whisper")
            { error = Usage; return false; }
            if (request.Channel == "whisper" ? !TryUsername(request.TargetName, out _) : request.TargetName != "")
            { error = "Invalid whisper recipient. " + Usage; return false; }
            if (request.Text == null || request.Text.Length == 0 || string.IsNullOrWhiteSpace(request.Text))
            { error = "Type a message. " + Usage; return false; }
            if (request.Text.Length > MaxMessageLength)
            { error = "Chat messages must contain no more than 512 characters."; return false; }
            if (!SafeUnicode(request.Text) || Utf8.GetByteCount(request.Text) > MaxUtf8Bytes)
            { error = "Chat cannot contain control or hidden formatting characters."; return false; }
            error = ""; return true;
        }

        public static void ValidateMessage(ChatMessage message)
        {
            if (message == null || !IsId(message.MessageId) || !IsId(message.SenderPlayerId) ||
                !TryUsername(message.SenderName, out string sender) || sender != message.SenderName ||
                message.UnixTimeMilliseconds < 0 || message.UnixTimeMilliseconds > 253402300799999L ||
                !TryValidateRequest(new ChatRequest { RequestId = message.RequestId, Channel = message.Channel,
                    TargetName = message.TargetName, Text = message.Text }, out _) ||
                (message.Channel == "whisper" ? !IsId(message.TargetPlayerId) ||
                    !TryUsername(message.TargetName, out string target) || target != message.TargetName : message.TargetPlayerId != ""))
                throw new InvalidDataException("Invalid chat message.");
        }

        public static void ValidateRejection(ChatRejection rejection)
        {
            if (rejection == null || (rejection.RequestId != "" && !IsId(rejection.RequestId)) ||
                !SafeBounded(rejection.Code, 48) || !SafeBounded(rejection.Message, MaxMessageLength))
                throw new InvalidDataException("Invalid chat rejection.");
            foreach (char c in rejection.Code)
                if (!((c >= 'a' && c <= 'z') || c == '_')) throw new InvalidDataException("Invalid chat rejection code.");
        }

        public static bool IsId(string value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);

        private static int WordEnd(string text)
        { int i = 0; while (i < text.Length && !char.IsWhiteSpace(text[i])) i++; return i; }
        private static bool TryUsername(string value, out string normalized)
        {
            try { normalized = AccountAuthentication.NormalizeUsername(value); return true; }
            catch (ArgumentException) { normalized = ""; return false; }
        }
        private static bool SafeBounded(string value, int limit) => value != null && value.Length > 0 && value.Length <= limit &&
            !string.IsNullOrWhiteSpace(value) && SafeUnicode(value);
        private static bool SafeUnicode(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    UnicodeCategory pairCategory = CharUnicodeInfo.GetUnicodeCategory(text, i);
                    if (pairCategory == UnicodeCategory.Format) return false;
                    i++; continue;
                }
                // ZWNJ and ZWJ are ordinary text in Persian and joined emoji.
                // Other format controls (especially bidi overrides/isolates)
                // cannot impersonate channel labels or hide message contents.
                if (char.IsLowSurrogate(c) || char.IsControl(c) || c == '\u2028' || c == '\u2029' ||
                    (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format && c != '\u200c' && c != '\u200d')) return false;
            }
            return true;
        }
    }
}
