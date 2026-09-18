using System;
using ShipWalk;

internal static class LocalFrameControlTests
{
    internal static void EnableAnywhereAndBoarding()
    {
        var control = new LocalFrameControl();
        Check(!control.ObserveContext(1021, false, false), "Disabled mod began preparation.");
        control.Enable();
        for (int i = 0; i < 300; i++)
            Check(!control.ObserveContext(null, false, false) && control.Enabled, "Enabling outside required a seat or lost intent.");
        Check(control.ObserveContext(1021, false, false), "Walking aboard did not begin preparation.");
        for (int i = 0; i < 300; i++)
            Check(!control.ObserveContext(1021, false, true), "Existing preparation or movement was restarted.");
        Check(!control.ObserveContext(null, false, false), "Leaving the ship began a local frame.");
        Check(control.Enabled && control.ObserveContext(1021, false, false), "Reboarding required another on command.");
        control.Disable();
        Check(!control.ObserveContext(1022, false, false), "Off did not stop automatic boarding.");
    }

    internal static void SeatTransitionsAndFailedAttempts()
    {
        var control = new LocalFrameControl(); control.Enable();
        Check(control.ObserveContext(1021, true, false), "Seated preparation did not start.");
        for (int i = 0; i < 300; i++)
            Check(!control.ObserveContext(1021, true, false), "Failed context was retried every frame.");
        Check(control.ObserveContext(1021, false, false), "Leaving a refused seat blocked subsequent on-foot boarding.");
        Check(!control.ObserveContext(1021, true, true), "Reseating stole an active frame.");
        Check(control.ObserveContext(1021, true, false), "Reseating did not allow a new session after release.");
        control.Enable();
        Check(control.ObserveContext(1021, true, false), "Explicit on did not retry a failed preparation.");
    }

    internal static void InactiveCapsuleRegression()
    {
        // Recorded 0.2.1 refusal: controller body=-7010, live physics body=0.
        // Native IL establishes that the capsule and body are read from the
        // same Physics object. Registration disappears during seated disable.
        const int body = -7010;
        int[] physicsAssociation = { body, 0, 0, 0, body, body, 0, body };
        foreach (int registered in physicsAssociation)
            Check(NativeCapsuleBinding.BelongsTo(body, body, registered), "Seating or unseating lost valid component ownership.");
        Check(physicsAssociation[1] != body, "Fixture did not reproduce the old equality refusal.");
        Check(!NativeCapsuleBinding.BelongsTo(body, -8120, 0), "A foreign inactive hierarchy was accepted.");
        Check(!NativeCapsuleBinding.BelongsTo(body, body, -8120), "Conflicting live physics ownership was accepted.");
        Check(!NativeCapsuleBinding.BelongsTo(body, 0, 0), "A missing hierarchy was accepted.");
        Check(!NativeCapsuleBinding.BelongsTo(0, 0, 0), "A missing native body was accepted.");
    }

    internal static void AutomaticHandshakeAndBoarding()
    {
        var control = new LocalFrameControl();
        Check(!control.TryEnableAutomatically(false) && !control.Enabled, "Missing server enabled local movement.");
        Check(!control.ObserveContext(1021, false, false), "Boarding started before acknowledgement.");
        Check(control.TryEnableAutomatically(true) && control.Enabled, "Acknowledged server still required a command.");
        Check(control.ObserveContext(1021, false, false), "Already aboard player was not prepared on automatic enable.");
        for (int i = 0; i < 300; i++)
        {
            Check(!control.TryEnableAutomatically(true), "Repeated welcome restarted activation.");
            Check(!control.ObserveContext(1021, false, true), "Repeated welcome rebuilt the active interior.");
        }
        Check(!control.ObserveContext(null, false, false), "World movement created an interior.");
        Check(control.ObserveContext(1022, true, false), "Boarding another ship required a command.");
    }

    internal static void AutomaticRespectsOff()
    {
        var control = new LocalFrameControl(); control.TryEnableAutomatically(true); control.Disable();
        for (int i = 0; i < 10; i++)
        {
            control.ResetSession(true);
            Check(!control.TryEnableAutomatically(true) && !control.Enabled, "Handshake/reset overrode explicit off.");
            Check(!control.ObserveContext(1021, false, false), "Off still allowed boarding.");
        }
        control.Enable();
        Check(control.ObserveContext(1021, false, false), "Manual on could not override the explicit off.");
        control.ResetSession(true);
        Check(control.TryEnableAutomatically(true), "Manual re-enable left automatic startup suppressed.");
        Check(new LocalFrameControl().TryEnableAutomatically(true), "New game lifetime retained an old off override.");
    }

    internal static void AutomaticSessionIsolation()
    {
        var control = new LocalFrameControl(); control.TryEnableAutomatically(true);
        control.ObserveContext(1021, false, false);
        control.ResetSession(true);
        Check(!control.Enabled && !control.TryEnableAutomatically(false), "Old server acknowledgement enabled a new server.");
        Check(!control.ObserveContext(1021, false, false), "Same entity ID in a new world bypassed the handshake.");
        Check(control.TryEnableAutomatically(true) && control.ObserveContext(1021, false, false), "New acknowledged playfield did not initialize.");
        control.ResetSession(false);
        Check(!control.Enabled, "Automatic multiplayer startup leaked into single-player.");
        control.Enable(); control.ObserveContext(1021, true, false); control.ResetSession(false);
        Check(control.Enabled && control.ObserveContext(1021, true, false), "Manual single-player activation stopped surviving world changes.");
    }

    internal static void AutomaticDoesNotRetryFailures()
    {
        var control = new LocalFrameControl(); control.TryEnableAutomatically(true);
        Check(control.ObserveContext(1021, true, false), "Initial automatic preparation missing.");
        for (int i = 0; i < 300; i++)
        {
            control.TryEnableAutomatically(true);
            Check(!control.ObserveContext(1021, true, false), "Automatic startup retried failed preparation every frame.");
        }
        control.Enable();
        Check(control.ObserveContext(1021, true, false), "Explicit retry stopped working.");
        // Runtime failure calls Disable; ready notifications must not revive it.
        control.Disable();
        Check(!control.TryEnableAutomatically(true), "Automatic startup revived a failed runtime.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
