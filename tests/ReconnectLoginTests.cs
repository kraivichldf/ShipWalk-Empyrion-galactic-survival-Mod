using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ShipWalk;

internal static class ReconnectLoginTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void LookupNeverOwnsMovement()
    {
        var login = new ReconnectLogin();
        Check(!login.Restoring && !login.Started, "Login acquired movement before the handshake.");
        Check(login.Begin(100) && !login.Begin(101), "Repeated handshake renewed the lookup.");
        for (double now = 100; now < 108; now += .25)
            Check(!login.Restoring && !login.Expired(now), "Normal login lookup took movement/camera ownership.");
        Check(login.Expired(108) && !login.Restoring, "Unanswered lookup failed to end without a hold.");
        Check(!login.AcceptOffer(108), "Offer arriving after the lookup deadline stole normal movement.");
        login.Cancel();
        Check(login.CancelPending && login.Finished && !login.Restoring, "Timeout kept character ownership.");
        Check(!login.AcceptOffer(145.78), "Recorded 45-second delay restarted the reconnect hold.");
    }

    public static void ConfirmedRestoreIsBounded()
    {
        var login = new ReconnectLogin(); login.Begin(10);
        Check(login.AcceptOffer(17) && login.Restoring, "Authenticated restoration did not acquire its hold.");
        Check(!login.Expired(18) && !login.Expired(106.99), "Streaming inherited the short lookup deadline.");
        Check(login.AcceptOffer(60) && login.Expired(107), "Repeated offer renewed the restoration timeout.");
        Check(!login.AcceptOffer(107), "Expired restoration could be revived by a duplicate offer.");
        login.Finish();
        Check(!login.Restoring && !login.CancelPending && !login.AcceptOffer(108), "Completion rewound active movement.");
    }

    public static void CancellationRetriesAndReset()
    {
        var login = new ReconnectLogin(); login.Begin(0); login.Cancel();
        for (int retry = 0; retry < 8; retry++)
            Check(login.CancelPending && !login.Restoring && !login.AcceptOffer(10 + retry),
                "Dropped cancellation or late offer reacquired character control.");
        login.Finish();
        Check(!login.CancelPending && !login.AcceptOffer(20), "Acknowledgement did not end cancellation retries.");
        login.Reset(true);
        Check(!login.Begin(30) && !login.AcceptOffer(30), "Opted-out session started restoring.");
        login.Reset(false);
        Check(login.Begin(40) && login.Age(42) == 2 && login.AcceptOffer(42), "New login inherited a cancelled attempt.");
        login.Reset(false);
        Check(!login.Restoring && !login.Started && login.Age(90) == 0, "Disconnect leaked a restoration hold.");
    }

    public static void ControllerAndIdentityIntegration()
    {
        MethodInfo[] Calls(Type type, string method) => PatchProcessor.GetOriginalInstructions(
            type.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(i => i.operand).OfType<MethodInfo>().ToArray();
        var blocking = Calls(typeof(ReconnectCoordinator), "get_Blocking");
        Check(blocking.Any(m => m.DeclaringType == typeof(ReconnectLogin) && m.Name == "get_Restoring")
            && !blocking.Any(m => m.Name == "get_Finished"), "Auto-boarding still waits for the initial lookup.");
        var worker = Calls(typeof(ReconnectCoordinator), "FromClient");
        Check(!worker.Any(m => m.Name == "get_SteamId"), "Worker reads process-wide identity for a remote actor.");
        Check(worker.Any(m => m.Name == "Authenticate") && worker.Any(m => m.Name == "Authenticated"),
            "Resolving identity at the manager removed native connection/session authentication.");
        var receive = Calls(typeof(ReconnectCoordinator), "FromServer");
        Check(receive.Any(m => m.Name == "AcceptReconnect") && receive.Any(m => m.DeclaringType == typeof(ReconnectLogin) && m.Name == "AcceptOffer"),
            "Restoration bypasses the authenticated offer and deadline checks.");
        var update = Calls(typeof(ReconnectCoordinator), "UpdateClient");
        Check(update.Any(m => m.DeclaringType == typeof(ReconnectLogin) && m.Name == "get_Restoring")
            && update.Any(m => m.Name == "SendCancellation"), "Controller update lost offer ownership or cancellation retries.");
    }
}
