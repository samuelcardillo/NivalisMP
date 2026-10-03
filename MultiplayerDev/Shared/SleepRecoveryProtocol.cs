using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    public sealed class GroupSleepIntent
    {
        public string Epoch { get; set; }="";
        public long Step { get; set; }
        public string Action { get; set; }="";
        public float Percentage { get; set; }
    }
    public sealed class GroupSleepChoice
    {
        public string Action { get; set; }="";
        public string Label { get; set; }="";
        public float Percentage { get; set; }
    }
    public sealed class GroupSleepEvent
    {
        public long Sequence { get; set; }
        public string Epoch { get; set; }="";
        public long Step { get; set; }
        public string Phase { get; set; }="";
        public string Owner { get; set; }="";
        public bool RecoveryOwner { get; set; }
        public string TargetPlayer { get; set; }="";
        public string RequestId { get; set; }="";
        public string Scene { get; set; }="";
        public string Title { get; set; }="";
        public string Text { get; set; }="";
        public int GameSeconds { get; set; }
        public bool Curfew { get; set; }
        public bool NightTransition { get; set; }
        public string[] Participants { get; set; }=Array.Empty<string>();
        public string[] ReadyPlayers { get; set; }=Array.Empty<string>();
        public GroupSleepChoice[] Choices { get; set; }=Array.Empty<GroupSleepChoice>();
    }

    public static class SleepRecoveryProtocol
    {
        public const string EventKind="group-sleep", ActionKind="group-sleep-action";
        public static bool IsChoicePhase(string phase)=>phase=="summary"||phase=="debt"||phase=="ranking";
        public static bool IsResponse(GroupSleepEvent value)=>value.Phase=="accepted"||value.Phase=="denied";
        public static string SelectOwner(string previous,IEnumerable<string> freshLoaded,IEnumerable<string> pending)
        {
            var loaded=new SortedSet<string>(freshLoaded,StringComparer.Ordinal);
            var recovery=new SortedSet<string>(pending,StringComparer.Ordinal);
            if(loaded.Count>32||recovery.Count>32)throw new InvalidDataException("Too many shared day participants.");
            foreach(string id in loaded)if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Invalid loaded shared day participant.");
            foreach(string id in recovery)if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Invalid pending shared day participant.");
            if(loaded.Contains(previous))return previous;
            if(loaded.Count>0)return loaded.Min;
            if(recovery.Contains(previous))return previous;
            return recovery.Count>0?recovery.Min:"";
        }
        public static bool AllowsPendingAction(GroupSleepEvent issued,InteractionRequest request,string authenticatedPlayer)
        {
            if(issued==null||request==null||request.Kind!=ActionKind||!issued.RecoveryOwner||!IsChoicePhase(issued.Phase)||
                issued.Owner!=authenticatedPlayer||request.TargetId!=issued.Epoch)return false;
            var intent=request.Data.Deserialize<GroupSleepIntent>(DedicatedProtocol.Json);
            try{ValidateChoice(issued,intent,authenticatedPlayer,request.TargetId);return true;}
            catch(InvalidOperationException){return false;}
            catch(InvalidDataException){return false;}
        }
        public static bool AllowsShutdownContinuation(GroupSleepEvent issued,InteractionRequest request)
        {
            if(issued==null||request==null||request.Kind!=ActionKind||!IsChoicePhase(issued.Phase))return false;
            try{
                var intent=request.Data.Deserialize<GroupSleepIntent>(DedicatedProtocol.Json);
                ValidateChoice(issued,intent,request.PlayerId,request.TargetId);return true;
            }
            catch(InvalidOperationException){return false;}
            catch(InvalidDataException){return false;}
            catch(JsonException){return false;}
        }
        public static void ValidateIntent(GroupSleepIntent value)
        {
            if(value==null||!Bounded(value.Epoch,32)||value.Step<0||!Bounded(value.Action,16)||!float.IsFinite(value.Percentage)||value.Percentage<0||value.Percentage>100)
                throw new InvalidDataException("Malformed sleep intent.");
        }
        public static void ValidateChoice(GroupSleepEvent current,GroupSleepIntent intent,string playerId,string targetId)
        {
            ValidateIntent(intent);
            if(current==null||current.Epoch.Length==0||intent.Epoch!=current.Epoch||intent.Step!=current.Step||targetId!=current.Epoch)
                throw new InvalidOperationException("The shared day transition changed. Wait for its current choices.");
            if(current.Owner!=playerId)throw new InvalidOperationException("Another player is handling the shared day transition.");
            foreach(var choice in current.Choices)
                if(choice.Action==intent.Action&&choice.Percentage==intent.Percentage)return;
            throw new InvalidOperationException("That day transition choice was not offered by the worker.");
        }
        public static void ValidateWire(GroupSleepEvent value)
        {
            if(value==null||value.Sequence<1||value.Step<0||!Bounded(value.Epoch,32)||!Bounded(value.Owner,32)||!Bounded(value.TargetPlayer,32)||!Bounded(value.RequestId,64)||
                !Bounded(value.Scene,128)||!Bounded(value.Title,160)||!Bounded(value.Text,3000)||value.GameSeconds<0||value.Choices==null||value.Choices.Length>16)
                throw new InvalidDataException("Malformed shared sleep state.");
            if(value.Epoch.Length>0&&!Guid.TryParseExact(value.Epoch,"N",out _))throw new InvalidDataException("Invalid sleep epoch.");
            if(value.Owner.Length>0&&!Guid.TryParseExact(value.Owner,"N",out _)||value.TargetPlayer.Length>0&&!Guid.TryParseExact(value.TargetPlayer,"N",out _))throw new InvalidDataException("Invalid sleep participant.");
            if(value.RecoveryOwner&&value.Owner.Length==0)throw new InvalidDataException("Recovery choice has no authenticated owner.");
            if(value.Phase!="accepted"&&value.Phase!="denied"&&value.Phase!="summary"&&value.Phase!="debt"&&value.Phase!="ranking"&&value.Phase!="newspaper"&&value.Phase!="supply"&&
                value.Phase!="sleeping"&&value.Phase!="transition"&&value.Phase!="curfew"&&value.Phase!="idle"&&value.Phase!="complete"&&
                value.Phase!="waiting"&&value.Phase!="cancelled"&&value.Phase!="synchronizing")throw new InvalidDataException("Invalid sleep phase.");
            if(!IsResponse(value)&&(value.Epoch.Length==0||value.TargetPlayer.Length!=0))throw new InvalidDataException("Invalid broadcast sleep epoch.");
            var participants=ValidateParticipants(value.Participants,"sleep participants");
            var ready=ValidateParticipants(value.ReadyPlayers,"ready sleepers");
            foreach(string id in ready)if(!participants.Contains(id))throw new InvalidDataException("A ready sleeper is outside the connected cohort.");
            if(value.Phase=="waiting"&&(participants.Count==0||ready.Count==0))throw new InvalidDataException("A waiting sleep needs a connected ready participant.");
            var choices=new HashSet<string>(StringComparer.Ordinal);
            foreach(var choice in value.Choices){
                if(choice==null||!Bounded(choice.Label,32)||!float.IsFinite(choice.Percentage)||choice.Percentage<0||choice.Percentage>100||
                    (choice.Action!="next"&&choice.Action!="ranking"&&choice.Action!="debt"))throw new InvalidDataException("Invalid shared sleep choice.");
                if(choice.Action=="next"&&value.Phase!="summary"||choice.Action=="ranking"&&value.Phase!="ranking"||choice.Action=="debt"&&value.Phase!="debt"||
                    choice.Action!="debt"&&choice.Percentage!=0||!choices.Add(choice.Action+":"+choice.Percentage.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))
                    throw new InvalidDataException("Invalid choice for this shared day phase.");
            }
        }
        private static HashSet<string> ValidateParticipants(string[] values,string name)
        {
            if(values==null||values.Length>32)throw new InvalidDataException("Invalid "+name+".");
            var result=new HashSet<string>(StringComparer.Ordinal);
            foreach(string id in values)if(!Guid.TryParseExact(id,"N",out _)||!result.Add(id))throw new InvalidDataException("Invalid "+name+".");
            return result;
        }
        private static bool Bounded(string value,int maximum)=>value!=null&&value.Length<=maximum;
    }
}
