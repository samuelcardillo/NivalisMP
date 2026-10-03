using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    public sealed class DialogueRecoveryChoice
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Enabled { get; set; }
        // A UI callback marker. Authoritative visible choices never set this;
        // the explicit continuation control is represented by CanContinue.
        public bool Continue { get; set; }
    }
    public sealed class DialogueRecoveryState
    {
        public long Sequence { get; set; }
        public string Conversation { get; set; } = "";
        public long Step { get; set; }
        public string Phase { get; set; } = "state";
        public string OwnerAccountId { get; set; } = "";
        public string TargetPlayerId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string SpeakerName { get; set; } = "";
        public string Text { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Message { get; set; } = "";
        public bool CanContinue { get; set; }
        public DialogueRecoveryChoice[] Choices { get; set; } = Array.Empty<DialogueRecoveryChoice>();
    }
    public sealed class DialogueRecoveryIntent
    {
        public long Sequence { get; set; }
        public string Conversation { get; set; } = "";
        public long Step { get; set; }
        public string ChoiceId { get; set; } = "";
        public bool Continue { get; set; }
    }
    public sealed class DialogueRecoveryRejection
    {
        public string Code { get; set; } = "dialogue_choice_rejected";
        public string Message { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Conversation { get; set; } = "";
        public long Step { get; set; }
        public long Sequence { get; set; }
        public string OwnerAccountId { get; set; } = "";
        public string TargetPlayerId { get; set; } = "";
        public bool Retryable { get; set; }
    }
    public static class DialogueRecoveryProtocol
    {
        public const string EventKind = "dialogue-recovery", ActionKind = "dialogue-recovery-action";
        public const int MaxPayloadBytes = 24000;
        public static bool IsResponse(DialogueRecoveryState state) => state != null && (state.Phase == "accepted" || state.Phase == "denied");
        public static void ValidateWire(DialogueRecoveryState state)
        {
            if (state == null || state.Sequence < 1 || state.Step < 0 ||
                !Guid.TryParseExact(state.Conversation, "N", out _) || !Guid.TryParseExact(state.OwnerAccountId, "N", out _) ||
                !Guid.TryParseExact(state.TargetPlayerId, "N", out _) || !DedicatedProtocol.Text(state.TargetId, 128) ||
                !DedicatedProtocol.Text(state.SpeakerName, 160, true) || !PresentationText(state.Text, 16000) ||
                !PresentationText(state.Message, 512) || !DedicatedProtocol.Text(state.RequestId, 64, true) ||
                state.Choices == null || state.Choices.Length > 64 ||
                (state.Phase != "state" && state.Phase != "accepted" && state.Phase != "denied" && state.Phase != "complete"))
                throw new InvalidDataException("Invalid pending dialogue recovery state.");
            if (IsResponse(state) && !Guid.TryParseExact(state.RequestId, "N", out _))
                throw new InvalidDataException("Dialogue recovery response has no request identity.");
            if (state.Phase != "state" && (state.CanContinue || state.Choices.Length != 0))
                throw new InvalidDataException("Only current dialogue state can offer recovery controls.");
            if (state.CanContinue && state.Choices.Length != 0)
                throw new InvalidDataException("A dialogue continuation cannot also offer visible choices.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in state.Choices)
                if (choice == null || choice.Continue || !DedicatedProtocol.Text(choice.Id, 128) ||
                    !PresentationText(choice.Text, 2048) || !ids.Add(choice.Id))
                    throw new InvalidDataException("Invalid or ambiguous pending dialogue choice.");
            if (JsonSerializer.SerializeToUtf8Bytes(state, DedicatedProtocol.Json).Length > MaxPayloadBytes)
                throw new InvalidDataException("Pending dialogue presentation exceeds its bound.");
        }
        public static void ValidateIntent(DialogueRecoveryIntent intent)
        {
            if (intent == null || intent.Sequence < 1 || intent.Step < 0 || !Guid.TryParseExact(intent.Conversation, "N", out _) ||
                !DedicatedProtocol.Text(intent.ChoiceId, 128, true) || (intent.Continue ? intent.ChoiceId.Length != 0 : intent.ChoiceId.Length == 0))
                throw new InvalidDataException("Invalid pending dialogue intent.");
        }
        public static void ValidateRejection(DialogueRecoveryRejection value)
        {
            if (value == null || value.Code != "dialogue_choice_rejected" || !PresentationText(value.Message, 512) ||
                !Guid.TryParseExact(value.RequestId, "N", out _) || !Guid.TryParseExact(value.OwnerAccountId, "N", out _) ||
                !Guid.TryParseExact(value.TargetPlayerId, "N", out _) || value.Step < 0 || value.Sequence < 0 ||
                value.Conversation == null || (value.Conversation.Length != 0 && !Guid.TryParseExact(value.Conversation, "N", out _)) ||
                (value.Retryable && (value.Sequence == 0 || value.Conversation.Length == 0)))
                throw new InvalidDataException("Invalid pending dialogue rejection receipt.");
        }
        private static bool PresentationText(string value, int maximum)
        {
            if (value == null || value.Length > maximum) return false;
            foreach (char c in value) if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') return false;
            return true;
        }
        public static bool AllowsPendingAction(DialogueRecoveryState state, InteractionRequest request, string authenticatedPlayer, string authenticatedAccount)
        {
            try
            {
                if (state == null || state.Phase != "state" || request == null || request.Kind != ActionKind ||
                    state.TargetPlayerId != authenticatedPlayer || state.OwnerAccountId != authenticatedAccount ||
                    request.PlayerId != authenticatedPlayer || request.AccountId != authenticatedAccount ||
                    request.TargetId != state.TargetId || !Guid.TryParseExact(request.RequestId, "N", out _)) return false;
                var intent = request.Data.Deserialize<DialogueRecoveryIntent>(DedicatedProtocol.Json);
                ValidateIntent(intent);
                if (intent.Sequence != state.Sequence || intent.Conversation != state.Conversation || intent.Step != state.Step) return false;
                if (intent.Continue) return state.CanContinue;
                foreach (var choice in state.Choices) if (choice.Id == intent.ChoiceId && choice.Enabled) return true;
                return false;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is InvalidOperationException) { return false; }
        }
    }

    // A higher-sequence heartbeat for the same native step must not reopen an
    // already submitted choice. Only a changed step or its exact denial does.
    public sealed class DialogueRecoveryPermit
    {
        private readonly HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
        public DialogueRecoveryState State { get; private set; }
        public string PendingRequestId { get; private set; } = "";
        public void Observe(DialogueRecoveryState state)
        {
            DialogueRecoveryProtocol.ValidateWire(state);
            if (state.Phase == "state")
            {
                if (State == null || State.Conversation != state.Conversation || State.Step != state.Step ||
                    State.OwnerAccountId != state.OwnerAccountId || State.TargetPlayerId != state.TargetPlayerId)
                    PendingRequestId = "";
                State = state;
            }
            else if (State != null && state.Conversation == State.Conversation &&
                state.OwnerAccountId == State.OwnerAccountId && state.TargetPlayerId == State.TargetPlayerId)
            {
                if (state.Phase == "complete") Reset();
                else if (state.Step == State.Step && state.Phase == "denied" && state.RequestId == PendingRequestId) PendingRequestId = "";
            }
        }
        public bool TryConsume(InteractionRequest request, string playerId, string accountId)
        {
            if (PendingRequestId.Length != 0 || consumed.Count >= 4096 || request == null || consumed.Contains(request.RequestId ?? "") ||
                !DialogueRecoveryProtocol.AllowsPendingAction(State, request, playerId, accountId)) return false;
            consumed.Add(request.RequestId); PendingRequestId = request.RequestId; return true;
        }
        public DialogueRecoveryRejection Reject(InteractionRequest request, string playerId, string accountId, bool laneAvailable)
        {
            var result = new DialogueRecoveryRejection { RequestId = request.RequestId, OwnerAccountId = accountId, TargetPlayerId = playerId,
                Message = "That conversation step changed, was already submitted, or belongs to another account. Wait for its current choices." };
            try
            {
                var intent = request.Data.Deserialize<DialogueRecoveryIntent>(DedicatedProtocol.Json);
                DialogueRecoveryProtocol.ValidateIntent(intent);
                result.Conversation = intent.Conversation; result.Step = intent.Step; result.Sequence = intent.Sequence;
                result.Retryable = laneAvailable && PendingRequestId.Length == 0 && consumed.Count < 4096 && !consumed.Contains(request.RequestId) && State != null &&
                    State.Conversation == intent.Conversation && State.Step == intent.Step && intent.Sequence <= State.Sequence &&
                    State.TargetPlayerId == playerId && State.OwnerAccountId == accountId && request.TargetId == State.TargetId;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is InvalidOperationException) { }
            DialogueRecoveryProtocol.ValidateRejection(result); return result;
        }
        public void Reset() { State = null; PendingRequestId = ""; }
    }

    public sealed class DialogueRecoveryLoadFence
    {
        public bool Active { get; private set; }
        public bool AwaitingFresh { get; private set; }
        public long Generation { get; private set; }
        public long MinimumRevision { get; private set; }
        public void Begin(long committedRevision)
        {
            if (!Active) { Generation++; MinimumRevision = Math.Max(MinimumRevision, checked(committedRevision + 1)); }
            Active = AwaitingFresh = true;
        }
        public void Complete()
        {
            if (!Active) return;
            Active = false; AwaitingFresh = true; Generation++;
        }
        public long BeginSnapshot()
        {
            if (Active) throw new InvalidDataException("Dialogue recovery must finish before account/world capture.");
            return Generation;
        }
        public void CommitSnapshot(long revision, long generation)
        { if (!Active && generation == Generation && revision >= MinimumRevision) AwaitingFresh = false; }
        public bool CanLoad(long revision) => !Active && !AwaitingFresh && revision >= MinimumRevision;
        public void Reset() { Active = AwaitingFresh = false; Generation = MinimumRevision = 0; }
    }
}
