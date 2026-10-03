using System;
using NivalisTogether.Dedicated;

// This entry point exercises production managed sources without native game DLLs.
// It does not start the game, start a dedicated service, or read player saves.
internal static class ManagedTestRunner
{
    private static void RequireSuccess(int exitCode, string name)
    {
        if (exitCode != 0) throw new Exception(name + " failed with exit code " + exitCode);
    }

    public static int Main()
    {
        try
        {
            int chat = ChatProtocolTests.Run();
            Console.WriteLine("PASS ChatProtocolTests: " + chat + " assertions");
            int geometry = BoatTriggerGeometryTests.Run();
            Console.WriteLine("PASS BoatTriggerGeometryTests: " + geometry + " assertions");
            RequireSuccess(PlayerSceneMembershipTests.Main(), nameof(PlayerSceneMembershipTests));
            RequireSuccess(AccountAuthenticationTests.Main(), nameof(AccountAuthenticationTests));
            RequireSuccess(AccountStateChunksTests.Main(), nameof(AccountStateChunksTests));
            RequireSuccess(ApartmentTests.Main(), nameof(ApartmentTests));
            RequireSuccess(SleepConsensusTests.Main(), nameof(SleepConsensusTests));
            MapCatchupProgressTests.Run();
            PeerWorldEventQueueTests.Run();
            DedicatedBackpressureTests.Run();
            RequireSuccess(WorldFlowTests.Main(), nameof(WorldFlowTests));
            Console.WriteLine("PASS: all 11 portable managed suites completed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
