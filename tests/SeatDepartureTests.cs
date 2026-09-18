using System;
using System.Numerics;
using ShipWalk;

internal static class SeatDepartureTests
{
    public static void MissingInheritanceRegression()
    {
        // Live 0.1.11 CSV at fixed_time 106.2250: native CopyWindow halves
        // the relative velocity of a character still carrying the old seat velocity.
        var ship = new Vector3(-77.02692f, -64.51829f, 2.12021f);
        var oldSeatVelocity = new Vector3(-.31191f, -.15722f, -.33994f);
        var frame = new MotionFrame();
        Vector3 relative = frame.BeginGrounded(oldSeatVelocity, ship);
        Vector3 after = MotionMath.WorldVelocity(relative * .5f, frame.Velocity);
        Near(after, new Vector3(-38.66941f, -32.33775f, .89014f));
        Check(Vector3.Distance(after, ship) > 49f, "baseline leaves a roughly 50 m/s velocity deficit");
        Console.WriteLine("REPRO seat exit: retained stale character velocity + native relative damping gives half ship speed.");
    }

    public static void TranslatingExit()
    {
        // Replay departure, initial ungrounded ticks, landing and input with the
        // production velocity kernels. Compare ship-relative travel to a parked CV.
        foreach (Vector3 ship in new[] { Vector3.Zero, new Vector3(100, 0, 0),
            new Vector3(-77.02692f, -64.51829f, 2.12021f) })
        {
            var transfer = new SeatExitInertia(ship, Vector3.Zero, Vector3.Zero, 10f);
            transfer.Confirm();
            Check(transfer.TryApply(10f, Vector3.UnitY, true, true, false, out Vector3 world), "departure applies");
            Check(transfer.TrySeed(10f, out Vector3 inherited), "first controller can consume departure");
            var frame = new MotionFrame();
            frame.BeginSeatExit(inherited, 10f);
            Vector3 playerPosition = Vector3.Zero, shipPosition = Vector3.Zero;
            Vector3 referencePosition = Vector3.Zero, referenceVelocity = Vector3.Zero;
            for (int tick = 0; tick < 120; tick++)
            {
                Vector3 relative = tick < 2 ? frame.Relative(world) : frame.BeginGrounded(world, ship);
                Vector3 desired = tick < 80 ? Vector3.Zero : new Vector3(4, 0, 0);
                // Native damping/input runs in local velocity, then physics gets world velocity.
                relative = Vector3.Lerp(relative, desired, .5f);
                referenceVelocity = Vector3.Lerp(referenceVelocity, desired, .5f);
                world = MotionMath.WorldVelocity(relative, frame.Velocity);
                Near(world - ship, referenceVelocity);
                if (tick < 80) Near(world, ship); // Two seconds with no walking input.
                playerPosition += world * .025f;
                shipPosition += ship * .025f;
                referencePosition += referenceVelocity * .025f;
                Near(playerPosition - shipPosition, referencePosition, .005f);
                Check(!transfer.TryApply(10f + tick * .025f, Vector3.UnitY, true, true, false, out _), "no continuous velocity overwrite");
            }
        }
    }

    public static void RotatingExitAndOrigin()
    {
        Vector3 linear = new Vector3(80, 0, 0), angular = new Vector3(0, 2, 0);
        // The origin changes between capture and exit; the absolute lever arm is still 5 m.
        Vector3 capturedCenter = new Vector3(2000, 0, 0) + new Vector3(1000, 0, 0);
        Vector3 exitPoint = new Vector3(5, 1, 0) + new Vector3(3000, 0, 0);
        var transfer = new SeatExitInertia(linear, angular, capturedCenter, 2f);
        transfer.Confirm();
        Check(transfer.TryApply(2.025f, exitPoint, true, true, false, out Vector3 velocity), "rotating exit applies");
        Near(velocity, new Vector3(80, 0, -10));
        Check(transfer.TrySeed(2.025f, out Vector3 frame), "rotating departure seeds");
        Near(frame, velocity);
        var motion = new MotionFrame(); motion.BeginSeatExit(frame, 2.025f);
        Near(motion.Relative(velocity), Vector3.Zero);
    }

    public static void OneShotAndCancellation()
    {
        var transfer = NewTransfer();
        Check(!transfer.TrySeed(10f, out _), "unapplied departure cannot seed a frame");
        Check(!transfer.TryApply(10f, Vector3.Zero, true, true, false, out _), "native detach must complete first");
        transfer.Confirm();
        Check(transfer.TryApply(10f, Vector3.Zero, true, true, false, out Vector3 player), "confirmed detach inherits");
        Check(transfer.TrySeed(10f, out _), "seed once");
        Check(!transfer.TrySeed(10.025f, out _), "later ticks cannot restart airborne departure");
        player = new Vector3(-12, 3, 1); // Solver collision after the exit handoff.
        if (transfer.TryApply(10.025f, Vector3.Zero, true, true, false, out Vector3 reapplied)) player = reapplied;
        Near(player, new Vector3(-12, 3, 1));
        foreach (bool cancelBeforeConfirmation in new[] { true, false })
        {
            var cancelled = NewTransfer();
            if (cancelBeforeConfirmation) cancelled.Cancel();
            cancelled.Confirm();
            if (!cancelBeforeConfirmation) cancelled.Cancel(); // Re-entered seat/reset/docking.
            Check(!cancelled.TryApply(10f, Vector3.Zero, true, true, false, out _), "cancellation cannot be revived");
            Check(!cancelled.TrySeed(10f, out _), "cancelled transfer has no frame");
        }
        var remote = NewTransfer(); remote.Confirm();
        Check(!remote.TryApply(10f, Vector3.Zero, false, true, false, out _), "remote player refused");
        Check(!remote.TryApply(10.025f, Vector3.Zero, true, true, false, out _), "lost character ownership cancels transfer");
        var aborted = NewTransfer(); aborted.Confirm();
        Check(!aborted.TryApply(10f, Vector3.Zero, true, true, true, out _), "failed detach refused");
    }

    public static void ReadinessAndInvalidData()
    {
        var waiting = NewTransfer(); waiting.Confirm();
        Check(!waiting.TryApply(10f, Vector3.Zero, true, false, false, out _), "inactive/kinematic character waits");
        Check(waiting.TryApply(10.1f, Vector3.Zero, true, true, false, out _), "first dynamic tick can inherit");
        foreach (float time in new[] { 10.251f, 9.9f, float.NaN, float.PositiveInfinity })
        {
            var invalidTime = NewTransfer(); invalidTime.Confirm();
            Check(!invalidTime.TryApply(time, Vector3.Zero, true, true, false, out _), "expired/reset/invalid time refused");
            Check(!invalidTime.TryApply(10f, Vector3.Zero, true, true, false, out _), "invalid transfer stays cancelled");
        }
        foreach (Vector3 point in new[] { new Vector3(float.NaN, 0, 0), new Vector3(float.PositiveInfinity, 0, 0), new Vector3(1000, 0, 0) })
        {
            var invalidPoint = new SeatExitInertia(new Vector3(100, 0, 0), Vector3.UnitY, Vector3.Zero, 10f);
            invalidPoint.Confirm();
            Check(!invalidPoint.TryApply(10f, point, true, true, false, out _), "invalid/excessive point speed refused");
            Check(!invalidPoint.TrySeed(10f, out _), "rejected inertia cannot seed");
        }
        var staleSeed = NewTransfer(); staleSeed.Confirm();
        Check(staleSeed.TryApply(10f, Vector3.Zero, true, true, false, out _), "valid initial handoff");
        Check(!staleSeed.TrySeed(10.251f, out _), "controller must consume within bounded handoff");
    }

    public static void InitialAirborneWindow()
    {
        var frame = new MotionFrame(); frame.BeginSeatExit(new Vector3(100, 0, 0), 10f);
        Check(frame.Phase == PassengerPhase.Airborne, "seat release does not pretend to have a floor");
        Check(frame.CanRemainAirborne(10.1f, SeatExitInertia.WithinGrace(10.1f, 10f)), "brief collider/contact handoff is tolerated");
        Check(!frame.CanRemainAirborne(10.251f, SeatExitInertia.WithinGrace(10.251f, 10f)), "grace cannot retain unsupported character indefinitely");
        Check(frame.CanRemainAirborne(10.251f, true), "same-ship floor can sustain the existing jump frame");
        Check(!frame.CanRemainAirborne(16.1f, true), "normal airborne timeout still applies");
        Check(!SeatExitInertia.WithinGrace(9.9f, 10f) && !SeatExitInertia.WithinGrace(10f, float.NegativeInfinity), "clock reset and unseeded frame refused");
        // Ship acceleration after leaving the floor must not accelerate a free character.
        Near(MotionMath.WorldVelocity(frame.Relative(new Vector3(100, 6, 0)), frame.Velocity), new Vector3(100, 6, 0));
        frame.Clear();
        Check(!frame.CanRemainAirborne(10.1f, true), "reset removes departure frame");
    }

    private static SeatExitInertia NewTransfer() => new SeatExitInertia(new Vector3(100, 0, 0), Vector3.Zero, Vector3.Zero, 10f);
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    private static void Near(Vector3 actual, Vector3 expected, float tolerance = .001f)
        => Check(MotionMath.Finite(actual) && Vector3.Distance(actual, expected) < tolerance, "expected " + expected + ", got " + actual);
}
