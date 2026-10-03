using System;
using NivalisTogether.Dedicated;

public static class PlayerChatOverlayTests
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new Exception("Chat UI regression: " + message);
    }
    public static int Run()
    {
        var input = new ChatInputLifecycle();
        Check(!input.IsTyping && !input.HoldsClosingGuard, "initial state releases all typing intent");
        Check(input.Open(10), "Enter opens input");
        long first = input.Generation;
        Check(input.IsTyping && !input.Open(10), "duplicate Update cannot open a second input");
        Check(!input.Submit("", 10, first), "opening Enter's late TMP callback cannot submit");
        Check(!input.Submit("hello", 11, first - 1), "stale input callback cannot submit");
        Check(input.Submit("hello", 12, first), "next Enter queues message");
        Check(!input.Submit("hello", 12, first), "TMP plus polling queues only once");
        Check(input.TakeSubmission() == "hello", "queued text is exact");
        Check(input.TakeSubmission() == null, "queued message is consumed once");
        Check(!input.Submit("hello", 12, first), "late same-frame TMP callback cannot resubmit after Update consumes it");
        input.Close(12, true);
        Check(!input.IsTyping && input.HoldsClosingGuard, "send closes field but shields the closing key");
        Check(!input.Open(12), "late Enter cannot reopen closed input");
        Check(!input.ReleaseClosingGuard(12, false, false), "menu guard survives rest of send frame");
        Check(!input.ReleaseClosingGuard(13, true, false), "held Enter keeps menu guard");
        Check(!input.ReleaseClosingGuard(14, false, true), "held Escape keeps menu guard");
        Check(input.ReleaseClosingGuard(15, false, false), "later released-key frame releases guard");
        Check(!input.HoldsClosingGuard, "no guard after key release");
        Check(input.Open(16), "next distinct Enter can reopen");
        Check(!input.Submit("late old text", 17, first), "old generation cannot send after reopen");
        long current = input.Generation;
        Check(input.Submit("/g hello", 17, current), "global command reaches parser intact");
        Check(input.TakeSubmission() == "/g hello", "no channel remembered implicitly");
        Check(input.IsTyping, "parser error can retain editable field");
        Check(input.Submit("corrected", 20, current), "corrected draft accepts next submit");
        input.Close(20, true);
        Check(!input.HasSubmission && input.TakeSubmission() == null, "Escape invalidates a queued submit before processing");
        Check(!input.Submit("late", 21, current), "Escape prevents delayed native submit");
        input.Close(21, false);
        Check(!input.HoldsClosingGuard && !input.IsTyping, "loading/focus loss releases pending menu guard");
        Check(input.Open(22), "chat usable after loading ends");
        long beforeDisconnect = input.Generation;
        input.Submit("private draft", 23, beforeDisconnect);
        input.Stop();
        Check(!input.IsTyping && !input.HoldsClosingGuard && !input.HasSubmission, "disconnect clears typing, queued submission and guard");
        Check(input.TakeSubmission() == null, "draft cannot carry into another session");
        Check(!input.Submit("old account text", 24, beforeDisconnect), "late callback after disconnect inert");
        Check(input.Open(25), "new session can open");
        Check(!input.Submit("old session text", 26, beforeDisconnect), "old callback remains inert after new session opens");
        input.Stop(); input.Stop();
        Check(!input.IsTyping && !input.HoldsClosingGuard, "repeated teardown is safe");

        string me = Guid.NewGuid().ToString("N"), other = Guid.NewGuid().ToString("N"), third = Guid.NewGuid().ToString("N");
        var message = new ChatMessage { Channel = "whisper", SenderPlayerId = me, TargetPlayerId = other };
        Check(ChatPresentationProbe.VisibleToLocal(message, me), "sender sees own whisper echo");
        Check(ChatPresentationProbe.VisibleToLocal(message, other), "recipient sees whisper");
        Check(!ChatPresentationProbe.VisibleToLocal(message, third), "third player cannot see a misrouted whisper");
        Check(!ChatPresentationProbe.VisibleToLocal(message, ""), "unconfigured client sees no message");
        message.Channel = "global";
        Check(ChatPresentationProbe.VisibleToLocal(message, third), "global channel visible to an active player");
        message.Channel = "local";
        Check(ChatPresentationProbe.VisibleToLocal(message, third), "local delivery trusts authoritative map routing");
        Check(!ChatPresentationProbe.VisibleToLocal(null, me), "null message rejected");
        Check(ChatPresentationProbe.Literal("<size=999>Hello</size>", 512) == "<size=999>Hello</size>", "literal formatting tags are not interpreted or altered");
        Check(ChatPresentationProbe.Literal("a\nb\rc\td", 512) == "a b c d", "server diagnostic cannot create forged history lines");
        Check(ChatPresentationProbe.Literal(new string('x', 513), 512).Length == 512, "entry text is bounded");
        Check(ChatPresentationProbe.Literal(null, 512) == "", "null notices remain safe");

        ChatRequest request; string error;
        Check(ChatProtocol.TryParseInput("hello nearby", out request, out error) && request.Channel == "local", "plain chat defaults to local");
        Check(ChatProtocol.TryParseInput("/s hello", out request, out error) && request.Channel == "local", "explicit /s is local");
        Check(ChatProtocol.TryParseInput("/g hello", out request, out error) && request.Channel == "global", "explicit /g is global");
        Check(ChatProtocol.TryParseInput("/w tester hello", out request, out error) && request.Channel == "whisper" && request.TargetName == "tester", "whisper target and text survive UI submission");
        Check(!ChatProtocol.TryParseInput("/w tester", out request, out error), "invalid command stays editable with useful parse error");
        return assertions;
    }
}
