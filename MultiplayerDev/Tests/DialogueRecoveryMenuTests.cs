using System;
using NivalisTogether;
using NivalisTogether.Dedicated;

internal static class DialogueRecoveryMenuTests
{
    private static int checks;
    private const string Player = "11111111111111111111111111111111", Account = "22222222222222222222222222222222";
    private const string Conversation = "33333333333333333333333333333333", Request = "44444444444444444444444444444444";
    private static void Check(bool value, string text) { checks++; if (!value) throw new Exception("FAIL: " + text); }
    private static DialogueRecoveryState State(long seq, long step = 1, string phase = "state") => new DialogueRecoveryState {
        Sequence = seq, Conversation = Conversation, Step = step, Phase = phase, OwnerAccountId = Account,
        TargetPlayerId = Player, TargetId = "npc-authoritative", SpeakerName = "Speaker", Text = "Native dialogue text",
        RequestId = phase == "accepted" || phase == "denied" ? Request : "",
        Choices = phase == "state" ? new[] { new DialogueRecoveryChoice { Id = "choice-one", Text = "Yes", Enabled = true },
            new DialogueRecoveryChoice { Id = "choice-two", Text = "Unavailable", Enabled = false } } : Array.Empty<DialogueRecoveryChoice>() };
    public static int Main()
    {
        var model = new DialogueRecoveryPresentation();
        Check(!model.Apply(State(1), Account) && model.State == null, "presentation is private to exact targeted player");
        var offered = State(1); Check(model.Apply(offered, Player), "initial private state accepted");
        offered.Choices[0].Text = "mutated caller object"; offered.Text = "changed";
        Check(model.State.Text == "Native dialogue text" && model.State.Choices[0].Text == "Yes", "native display DTO detached from mutable caller");
        long generation = model.Generation;
        Check(!model.TrySubmit(generation, new DialogueRecoveryChoice { Id = "choice-two", Enabled = true }, out _), "caller cannot enable disabled native response");
        Check(!model.TrySubmit(generation, new DialogueRecoveryChoice { Continue = true }, out _), "no fabricated continue while native offers choices");
        Check(!model.TrySubmit(generation, new DialogueRecoveryChoice { Id = "unknown" }, out _), "unknown response rejected");
        Check(model.TrySubmit(generation, new DialogueRecoveryChoice { Id = "choice-one", Text = "fake" }, out var selected) && selected.Text == "Yes", "callback copies authoritative enabled choice");
        Check(!model.TrySubmit(generation, selected, out _), "one queued click per submission");
        model.SetPendingRequest(Request);
        Check(model.Apply(State(2), Player) && model.Submitted && model.PendingRequestId == Request, "same-step heartbeat retains pending submission");
        Check(!model.TrySubmit(generation, selected, out _), "old native click is inert after a newer event");
        var mismatch = State(3, phase: "denied"); mismatch.RequestId = Account;
        Check(!model.Apply(mismatch, Player) && model.Submitted, "unrelated denial cannot rearm");
        mismatch = State(3, phase: "denied"); mismatch.OwnerAccountId = Player;
        Check(!model.Apply(mismatch, Player), "wrong account receipt ignored");
        var denied = State(3, phase: "denied"); denied.Message = "Try the refreshed choices.";
        Check(model.Apply(denied, Player) && model.Submitted && model.Notice == denied.Message, "matching denial awaits fresh full state");
        Check(!model.Apply(State(2), Player) && model.Submitted, "older state cannot release rejected request");
        Check(model.Apply(State(4), Player) && !model.Submitted, "fresh full state after exact denial permits retry");
        Check(model.TrySubmit(model.Generation, selected, out _), "refreshed choice can be submitted"); model.SetPendingRequest(Request);
        Check(model.Apply(State(5, phase: "accepted"), Player) && model.Submitted, "acceptance remains disabled pending native progress");
        Check(model.Apply(State(6), Player) && model.Submitted, "accepted same-step heartbeat cannot reopen permit");
        model.SetNotice("Server busy"); Check(model.Submitted, "notice alone never re-enables a response");
        Check(model.Apply(State(7, 2), Player) && !model.Submitted, "changed native step opens fresh choices");
        var continuation = State(8, 3); continuation.Choices = Array.Empty<DialogueRecoveryChoice>(); continuation.CanContinue = true;
        Check(model.Apply(continuation, Player), "native continuation state accepted");
        Check(!model.TrySubmit(model.Generation, selected, out _), "visible-choice replay rejected in continuation state");
        Check(model.TrySubmit(model.Generation, new DialogueRecoveryChoice { Continue = true, Id = "ignored" }, out var resume) && resume.Continue && resume.Id == "", "continue callback only from authoritative continuation permission");
        var complete = State(9, 4, "complete");
        Check(model.Apply(complete, Player) && model.State == null, "matching conversation completes even when native terminal step advanced");
        Check(!model.Apply(continuation, Player), "completed presentation cannot reopen from old sequence");
        model.Clear(); Check(model.Apply(State(1), Player), "explicit session reset permits a new sequence domain");
        var bad = State(2); bad.CanContinue = true;
        bool rejected = false; try { model.Apply(bad, Player); } catch { rejected = true; }
        Check(rejected, "ambiguous native choices and continue rejected before rendering");
        Check(model.State.Sequence == 1 && !model.Submitted, "invalid update leaves current UI intact");
        foreach (bool retryable in new[] { true, false })
        {
            model.Clear(); model.Apply(State(20), Player);
            model.TrySubmit(model.Generation, selected, out _); model.SetPendingRequest(Request);
            model.Apply(State(21), Player);
            var rejection = new DialogueRecoveryRejection { Code = "dialogue_choice_rejected", Message = "Choices changed. Wait for refreshed choices.",
                RequestId = Request, Conversation = Conversation, Step = 1, Sequence = 20,
                OwnerAccountId = Account, TargetPlayerId = Player, Retryable = retryable };
            rejection.Sequence = 21;
            Check(!model.ApplyRejection(rejection) && model.Submitted, "rejection must match original submitted sequence, not latest heartbeat");
            rejection.Sequence = 20; rejection.RequestId = Account;
            Check(!model.ApplyRejection(rejection), "typed rejection matches exact pending request");
            rejection.RequestId = Request; rejection.Conversation = Account;
            Check(!model.ApplyRejection(rejection), "typed rejection matches conversation");
            rejection.Conversation = Conversation; rejection.Step = 2;
            Check(!model.ApplyRejection(rejection), "typed rejection matches native step");
            rejection.Step = 1; rejection.OwnerAccountId = Player;
            Check(!model.ApplyRejection(rejection), "typed rejection matches account owner");
            rejection.OwnerAccountId = Account; rejection.TargetPlayerId = Account;
            Check(!model.ApplyRejection(rejection), "typed rejection matches exact player connection");
            rejection.TargetPlayerId = Player;
            Check(model.ApplyRejection(rejection) && model.Submitted, "typed rejection never immediately re-enables rendered controls");
            Check(!model.Apply(State(21), Player) && model.Submitted, "already rendered heartbeat cannot be reused after rejection");
            model.SetNotice("Retry now"); Check(model.Submitted, "generic text cannot release typed pending state");
            Check(model.Apply(State(22), Player) && model.Submitted != retryable, "only retryable unconsumed rejection releases on subsequent full state");
            if (retryable)
                Check(!model.ApplyRejection(rejection), "consumed rejection receipt cannot affect later UI generation");
            else
            {
                Check(model.Apply(State(23, phase: "accepted"), Player) && model.Submitted, "nonretryable service rejection retains matching worker acceptance tracking");
                Check(!model.ApplyRejection(rejection), "typed rejection cannot rearm an already accepted action");
            }
        }
        model.Clear(); model.Apply(State(30), Player);
        long unsentGeneration = model.Generation;
        model.TrySubmit(unsentGeneration, selected, out _);
        Check(model.ReleaseUnsent(unsentGeneration) && !model.Submitted && model.Notice.Contains("not sent"), "synchronous callback failure before request ID permits retry");
        Check(!model.TrySubmit(unsentGeneration, selected, out _), "failed send invalidates old native queued clicks");
        Check(model.TrySubmit(model.Generation, selected, out _), "fresh rendered generation can retry locally unsent response");
        long sentGeneration = model.Generation; model.SetPendingRequest(Request);
        Check(!model.ReleaseUnsent(sentGeneration) && model.Submitted, "recorded request cannot be released as locally unsent");
        model.Clear(); model.Apply(State(40), Player);
        unsentGeneration = model.Generation; model.TrySubmit(unsentGeneration, selected, out _); model.Apply(State(41), Player);
        Check(!model.ReleaseUnsent(unsentGeneration) && model.Submitted, "intervening network state prevents ambiguous local retry");
        Console.WriteLine("PASS: " + checks + " production dialogue recovery presentation assertions.");
        return 0;
    }
}
