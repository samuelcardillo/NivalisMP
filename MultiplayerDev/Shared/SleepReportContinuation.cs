namespace NivalisTogether.Dedicated
{
    // Informational reports can finish on an empty dedicated server without
    // inventing a player vote or starting sleep. A pending authenticated join
    // still counts, even before its native body or recovery UI is available.
    public static class SleepReportContinuation
    {
        public static bool CanContinue(string phase, bool authorizedSleep, int connectedPlayers, int loadedPlayers, int recoveryPlayers)
        {
            if (phase != "summary" && phase != "ranking") return false;
            if (connectedPlayers < 0 || loadedPlayers < 0 || recoveryPlayers < 0) return false;
            return authorizedSleep || (connectedPlayers == 0 && loadedPlayers == 0 && recoveryPlayers == 0);
        }
    }
}
