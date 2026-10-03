using System;

namespace NivalisTogether.Dedicated
{
    /// <summary>A consent records its authenticated map generation and native bed
    /// location. Worker map residency is intentionally absent from this policy.</summary>
    public sealed class SleepBedReadiness
    {
        public string AccountId { get; set; }="";
        public string Scene { get; set; }="";
        public string PropertyId { get; set; }="";
        public long Generation { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public bool Matches(PlayerPose pose,PlayerSceneContext context,double poseAge,bool playerMapBusy)
        {
            if(pose==null||context==null||playerMapBusy||context.TravelPending||!pose.Active||
                !double.IsFinite(poseAge)||poseAge<0||poseAge>3||!DedicatedProtocol.PoseValid(pose)||
                !Guid.TryParseExact(AccountId,"N",out _)||pose.AccountId!=AccountId||context.PlayerId!=pose.PlayerId||
                Generation<1||context.Generation!=Generation||pose.ContextGeneration!=Generation||
                context.Scene!=Scene||pose.Scene!=Scene||context.PropertyId!=PropertyId||pose.PropertyId!=PropertyId||
                !float.IsFinite(X)||!float.IsFinite(Y)||!float.IsFinite(Z))return false;
            double x=(double)pose.X-X,y=(double)pose.Y-Y,z=(double)pose.Z-Z;
            return x*x+y*y+z*z<=16;
        }
    }
}
