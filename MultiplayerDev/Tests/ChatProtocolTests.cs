using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    // Exercises the real command parser and wire validator; no chat behavior is
    // reproduced in the fixture. Routing/authentication have separate service tests.
    public static class ChatProtocolTests
    {
        private static int checks;
        private static void Check(bool condition, string message)
        { checks++; if (!condition) throw new Exception("Chat protocol: " + message); }

        private static ChatRequest Parse(string raw, string channel, string text, string target = "")
        {
            Check(ChatProtocol.TryParseInput(raw, out var request, out var error), "accept input " + Describe(raw) + ": " + error);
            Check(request != null && request.Channel == channel && request.Text == text && request.TargetName == target,
                "parsed channel, body and recipient match for " + Describe(raw));
            Check(Guid.TryParseExact(request.RequestId, "N", out var id) && id != Guid.Empty,
                "parser creates a nonempty N-format request identity");
            Check(ChatProtocol.TryValidateRequest(request, out error), "parsed request passes wire validation: " + error);
            return request;
        }

        private static string Describe(string text) => text == null ? "<null>" :
            JsonSerializer.Serialize(text.Length > 60 ? text.Substring(0, 60) + "..." : text);

        private static void RejectInput(string raw, string reason)
        {
            Check(!ChatProtocol.TryParseInput(raw, out _, out var error), reason);
            Check(!string.IsNullOrWhiteSpace(error), reason + " supplies an error to display");
        }

        private static ChatRequest Valid() => new ChatRequest
        { RequestId = Guid.NewGuid().ToString("N"), Channel = "local", TargetName = "", Text = "hello" };

        private static void RejectRequest(Action<ChatRequest> mutate, string reason)
        {
            var request = Valid(); mutate(request);
            Check(!ChatProtocol.TryValidateRequest(request, out var error), reason);
            Check(!string.IsNullOrWhiteSpace(error), reason + " supplies an error to display");
        }

        private static void RejectPayload(Action validate, string reason)
        {
            checks++;
            try { validate(); }
            catch (InvalidDataException) { return; }
            throw new Exception("Chat protocol: " + reason);
        }

        private static ChatMessage Message() => new ChatMessage
        {
            MessageId = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"),
            SenderPlayerId = Guid.NewGuid().ToString("N"), SenderName = "player_one", Channel = "local",
            TargetPlayerId = "", TargetName = "", Text = "hello", UnixTimeMilliseconds = 1234
        };

        private static void RejectMessage(Action<ChatMessage> mutate, string reason)
        { var value = Message(); mutate(value); RejectPayload(() => ChatProtocol.ValidateMessage(value), reason); }

        private static ChatRejection Rejection() => new ChatRejection
        { RequestId = Guid.NewGuid().ToString("N"), Code = "rate_limit", Message = "Please wait before sending another message." };

        private static void RejectRejection(Action<ChatRejection> mutate, string reason)
        { var value = Rejection(); mutate(value); RejectPayload(() => ChatProtocol.ValidateRejection(value), reason); }

        private static void RejectCharacter(string character, string description)
        {
            // Edges matter: trimming before validation must not erase forbidden
            // tabs/newlines and silently turn a malformed input into a valid one.
            RejectInput("hello" + character + "world", description + " rejected inside plain input");
            RejectInput(character + "hello", description + " rejected before text");
            RejectInput("hello" + character, description + " rejected after text");
            RejectInput("/g hello" + character + "world", description + " rejected in global command");
            RejectInput("/w player hello" + character + "world", description + " rejected in whisper body");
            RejectRequest(r => r.Text = "hello" + character + "world", description + " rejected on wire");
        }

        public static int Run()
        {
            checks = 0;
            Check(ChatProtocol.SendKind == "chatSend" && ChatProtocol.MessageKind == "chatMessage" &&
                ChatProtocol.RejectedKind == "chatRejected", "wire message kinds are stable");
            Check(ChatProtocol.MaxTextLength == 512 && ChatProtocol.MaxInputLength == 580 &&
                ChatProtocol.MaxUtf8Bytes == 2048, "public text/input/UTF-8 limits match the contract");

            var first = Parse("hello", "local", "hello");
            var second = Parse("hello", "local", "hello");
            Check(first.RequestId != second.RequestId, "separate submissions receive separate request identities");
            Parse("  hello  world  ", "local", "hello  world");
            Parse("/s hello", "local", "hello");
            Parse("  /s   hello  world  ", "local", "hello  world");
            Parse("/g hello all maps", "global", "hello all maps");
            Parse("/g   hello  world  ", "global", "hello  world");
            Parse("/w PLAYER_Name-7 hello privately", "whisper", "hello privately", "player_name-7");
            Parse("  /w   AbC   hello  world  ", "whisper", "hello  world", "abc");
            Parse("/w " + new string('A', 24) + " hello", "whisper", "hello", new string('a', 24));
            Parse("/s /g is literal body text", "local", "/g is literal body text");
            Parse("/g /w player is literal body text", "global", "/w player is literal body text");
            Parse("/w player /s literal body", "whisper", "/s literal body", "player");
            const string markup = "<b>hello</b> <color=#ff0000>red?</color> & [local]";
            Parse(markup, "local", markup);
            Parse("/g " + markup, "global", markup);
            const string unicode = "caf\u00e9 cafe\u0301 \u4f60\u597d \u0645\u0631\u062d\u0628\u0627 \ud83d\ude00";
            Parse(unicode, "local", unicode);
            const string familyEmoji = "Family \ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc66";
            const string persian = "\u0645\u06cc\u200c\u0631\u0648\u0645";
            Parse(familyEmoji, "local", familyEmoji);
            Parse("/g " + persian, "global", persian);
            Parse("/w player " + familyEmoji + " " + persian, "whisper", familyEmoji + " " + persian, "player");

            foreach (string input in new[] { null, "", " ", "    ", "/", "/s", "/s ", "/g", "/g   ",
                "/w", "/w ", "/w player", "/w player   ", "/unknown hello", "/global hello", "/greeting hello", "/whisper player hello" })
                RejectInput(input, "empty or malformed command is rejected: " + Describe(input));
            foreach (string name in new[] { "ab", new string('a', 25), "user.name", "us\u00e9r", "user/name", "\"player\"", "\u212aey" })
                RejectInput("/w " + name + " hello", "whisper uses account username grammar: " + Describe(name));

            string maximum = new string('x', 512);
            Parse(maximum, "local", maximum);
            Parse("/s " + maximum, "local", maximum);
            Parse("/g " + maximum, "global", maximum);
            Parse("/w " + new string('a', 24) + " " + maximum, "whisper", maximum, new string('a', 24));
            Parse(new string(' ', 68) + maximum, "local", maximum); // Exact 580 raw units.
            RejectInput(new string(' ', 69) + maximum, "raw input bound is enforced before trimming");
            RejectInput(maximum + "x", "513-unit default body is rejected");
            RejectInput("/s " + maximum + "x", "513-unit local body is rejected");
            RejectInput("/g " + maximum + "x", "513-unit global body is rejected");
            RejectInput("/w player " + maximum + "x", "513-unit whisper body is rejected");
            string cjk = new string('\u4f60', 512);
            Parse(cjk, "local", cjk);
            Check(Encoding.UTF8.GetByteCount(cjk) == 1536, "maximum BMP body tests multibyte UTF-8 without ASCII assumptions");
            var emojiBuilder = new StringBuilder();
            for (int i = 0; i < 256; i++) emojiBuilder.Append("\ud83d\ude00");
            string emojis = emojiBuilder.ToString();
            Parse(emojis, "local", emojis);
            RejectInput(emojis + "a", "supplementary characters count as two UTF-16 units");
            // 512 well-formed UTF-16 units use at most 1536 UTF-8 bytes. The
            // independent 2048-byte safety cap cannot bind before the text cap.

            for (int value = 0; value <= 0x9f; value++)
                if (char.IsControl((char)value)) RejectCharacter(((char)value).ToString(), "control U+" + value.ToString("X4"));
            foreach (string forbidden in new[] { "\u00ad", "\u061c", "\u200b", "\u200e", "\u200f",
                "\u202a", "\u202b", "\u202c", "\u202d", "\u202e", "\u2060", "\u2066", "\u2067", "\u2068", "\u2069",
                "\ufeff", "\u2028", "\u2029", "\udb40\udc01", "\ud800", "\udfff", "\ud800x", "\udfff\ud800" })
                RejectCharacter(forbidden, "format/separator/malformed Unicode " + Describe(forbidden));

            Check(!ChatProtocol.TryValidateRequest(null, out var missingError) && !string.IsNullOrWhiteSpace(missingError),
                "missing wire request is a recoverable validation failure");
            foreach (string id in new[] { null, "", "not-a-guid", new string('z', 32), Guid.NewGuid().ToString("D"), new string('a', 33) })
                RejectRequest(r => r.RequestId = id, "wire request identity requires GUID N: " + Describe(id));
            foreach (string channel in new[] { null, "", "LOCAL", "all", "s", " global", "global\n" })
                RejectRequest(r => r.Channel = channel, "wire channel must be an exact supported value");
            RejectRequest(r => r.TargetName = "player", "local chat cannot carry a whisper target");
            RejectRequest(r => { r.Channel = "global"; r.TargetName = "player"; }, "global chat cannot carry a whisper target");
            foreach (string name in new[] { null, "", "ab", new string('a', 25), "user.name", "us\u00e9r", " player", "player " })
                RejectRequest(r => { r.Channel = "whisper"; r.TargetName = name; }, "wire whisper needs a valid account username");
            var uppercase = Valid(); uppercase.Channel = "whisper"; uppercase.TargetName = "PLAYER_Name-7";
            Check(ChatProtocol.TryValidateRequest(uppercase, out _), "valid uppercase wire whisper recipient may be normalized by the service");
            foreach (string text in new[] { null, "", "   ", new string('x', 513), new string('\u4f60', 683) })
                RejectRequest(r => r.Text = text, "wire text independently rejects empty or over-limit payloads");
            var literalWire = Valid(); literalWire.Text = markup;
            Check(ChatProtocol.TryValidateRequest(literalWire, out _), "literal markup is data; UI must disable rich text");
            var roundTrip = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(uppercase));
            Check(roundTrip != null && roundTrip.RequestId == uppercase.RequestId && roundTrip.Channel == "whisper" &&
                roundTrip.TargetName == "PLAYER_Name-7" && roundTrip.Text == "hello" && ChatProtocol.TryValidateRequest(roundTrip, out _),
                "wire serialization preserves all request fields and remains valid");

            ValidateServerPayloads(markup, familyEmoji, persian);
            return checks;
        }

        private static void ValidateServerPayloads(string markup, string familyEmoji, string persian)
        {
            foreach (string channel in new[] { "local", "global", "whisper" })
            {
                var value = Message(); value.Channel = channel;
                if (channel == "whisper") { value.TargetName = "player_two"; value.TargetPlayerId = Guid.NewGuid().ToString("N"); }
                ChatProtocol.ValidateMessage(value);
                var copy = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(value));
                ChatProtocol.ValidateMessage(copy);
                Check(copy.MessageId == value.MessageId && copy.RequestId == value.RequestId && copy.Channel == channel &&
                    copy.SenderPlayerId == value.SenderPlayerId && copy.SenderName == value.SenderName &&
                    copy.TargetPlayerId == value.TargetPlayerId && copy.TargetName == value.TargetName &&
                    copy.Text == value.Text && copy.UnixTimeMilliseconds == value.UnixTimeMilliseconds,
                    channel + " server payload validates and round-trips all identity/body fields");
            }
            foreach (string body in new[] { markup, familyEmoji, persian, new string('x', 512) })
            { var value = Message(); value.Text = body; ChatProtocol.ValidateMessage(value); Check(true, "valid message payload body accepted"); }
            foreach (long timestamp in new[] { 0L, 253402300799999L })
            { var value = Message(); value.UnixTimeMilliseconds = timestamp; ChatProtocol.ValidateMessage(value); Check(true, "Unix timestamp boundary accepted"); }
            RejectPayload(() => ChatProtocol.ValidateMessage(null), "null message is invalid");
            RejectMessage(v => v.MessageId = "", "message identity is required");
            RejectMessage(v => v.MessageId = Guid.NewGuid().ToString("D"), "message identity must have N format");
            RejectMessage(v => v.RequestId = "", "message must refer to a request");
            RejectMessage(v => v.SenderPlayerId = "player", "sender identity must have N format");
            RejectMessage(v => v.SenderName = "PLAYER", "server sender name must be canonical");
            RejectMessage(v => v.SenderName = "player.name", "server sender name follows account username grammar");
            RejectMessage(v => v.Channel = "LOCAL", "server channel must be canonical");
            RejectMessage(v => v.TargetPlayerId = Guid.NewGuid().ToString("N"), "local payload cannot claim a whisper player");
            RejectMessage(v => { v.Channel = "global"; v.TargetName = "player_two"; }, "global payload cannot claim a whisper account");
            RejectMessage(v => v.Channel = "whisper", "whisper payload must include recipient identity and name");
            RejectMessage(v => { v.Channel = "whisper"; v.TargetName = "player_two"; v.TargetPlayerId = "bad"; }, "whisper recipient identity must have N format");
            RejectMessage(v => { v.Channel = "whisper"; v.TargetName = "PLAYER_TWO"; v.TargetPlayerId = Guid.NewGuid().ToString("N"); }, "server whisper name must be canonical");
            RejectMessage(v => v.Text = "", "message text cannot be empty");
            RejectMessage(v => v.Text = new string('x', 513), "message text bound checked on receipt");
            RejectMessage(v => v.Text = "hello\u202eworld", "received messages reject bidi formatting");
            RejectMessage(v => v.Text = "hello\nworld", "received messages reject line controls");
            RejectMessage(v => v.Text = "hello\ud800", "received messages reject invalid UTF-16");
            RejectMessage(v => v.UnixTimeMilliseconds = -1, "negative message timestamps rejected");
            RejectMessage(v => v.UnixTimeMilliseconds = 253402300800000L, "out-of-range message timestamps rejected");

            foreach (string requestId in new[] { "", Guid.NewGuid().ToString("N") })
            {
                var value = Rejection(); value.RequestId = requestId;
                ChatProtocol.ValidateRejection(value);
                var copy = JsonSerializer.Deserialize<ChatRejection>(JsonSerializer.Serialize(value));
                ChatProtocol.ValidateRejection(copy);
                Check(copy.RequestId == requestId && copy.Code == value.Code && copy.Message == value.Message,
                    "rejection supports either known request or malformed request without leaking input");
            }
            var bounded = Rejection(); bounded.Code = new string('a', 48); bounded.Message = new string('x', 512);
            ChatProtocol.ValidateRejection(bounded); Check(true, "rejection exact field limits accepted");
            var literal = Rejection(); literal.Message = markup + " " + familyEmoji + " " + persian;
            ChatProtocol.ValidateRejection(literal); Check(true, "rejection text permits printable literal content and joiners");
            RejectPayload(() => ChatProtocol.ValidateRejection(null), "missing rejection invalid");
            foreach (string id in new[] { null, "bad", Guid.NewGuid().ToString("D") })
                RejectRejection(v => v.RequestId = id, "rejection request identity must be empty or N format");
            foreach (string code in new[] { null, "", " ", "RATE_LIMIT", "rate-limit", "rate1", "rate\nlimit", new string('a', 49) })
                RejectRejection(v => v.Code = code, "rejection code must be bounded lowercase ASCII words and underscores");
            foreach (string body in new[] { null, "", " ", new string('x', 513), "bad\trequest", "bad\u200brequest", "bad\ud800" })
                RejectRejection(v => v.Message = body, "rejection message must be bounded printable text");
        }
    }
}
