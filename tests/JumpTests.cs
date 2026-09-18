using System;
using System.Collections.Generic;
using System.Numerics;
using ShipWalk;

internal static class JumpTests
{
    private const float Dt = .02f;

    internal static void CompleteJump()
    {
        // Reproduce the old failure mechanism independently: native drag damps world
        // motion during a jump even when the passenger has no relative horizontal input.
        float legacyForward = 50f;
        for (int i = 0; i < 60; i++) legacyForward *= 1f - .1f * Dt;
        Check(legacyForward < 45f, "Legacy world drag did not reproduce loss of carried motion.");

        foreach (float drag in new[] { 0f, .05f, .1f, 2f })
        {
            List<Vector3> stationary = SimulateJump(0f, drag);
            List<Vector3> moving = SimulateJump(50f, drag);
            Check(moving.Count == stationary.Count, "Ship speed changed jump airtime.");
            for (int i = 0; i < moving.Count; i++)
                Near(moving[i] - new Vector3(50f * Dt * (i + 1), 0, 0), stationary[i], .002f);
        }
    }

    private static List<Vector3> SimulateJump(float shipSpeed, float drag)
    {
        var frame = new MotionFrame();
        Vector3 ship = new Vector3(shipSpeed, 0, 0);
        Vector3 relative = frame.BeginGrounded(ship, ship);
        Vector3 world = MotionMath.WorldVelocity(relative + new Vector3(0, 6, 0), frame.Velocity);
        Check(frame.LeaveGround(0), "Takeoff failed.");
        Vector3 position = Vector3.Zero;
        var trajectory = new List<Vector3>();
        for (int step = 0; step < 250; step++)
        {
            Check(frame.CanRemainAirborne(step * Dt, true), "Jump tracking expired early.");
            // Character calculations are relative; the independent physics step is world-space.
            world = MotionMath.WorldVelocity(frame.Relative(world), frame.Velocity);
            var acceleration = MotionMath.RelativeDragAcceleration(world, frame.Velocity, drag, Dt);
            world += (new Vector3(0, -9.81f, 0) + acceleration) * Dt;
            position += world * Dt;
            trajectory.Add(position);
            if (position.Y > 0) continue;
            world.Y = 0; // A simple floor collision, before the after-physics limiter.
            relative = frame.LandingLimiter(world, ship);
            relative.X = Math.Max(-4, Math.Min(4, relative.X));
            world = MotionMath.WorldVelocity(relative, frame.Velocity);
            Near(world, ship);
            Near(frame.BeginGrounded(world, ship), Vector3.Zero);
            return trajectory;
        }
        throw new Exception("Simulated jump did not land.");
    }

    internal static void AccelerationAndLanding()
    {
        var frame = new MotionFrame();
        Vector3 departure = new Vector3(50, 0, 0);
        frame.BeginGrounded(departure, departure);
        frame.LeaveGround(1f);
        Vector3 world = new Vector3(53, 6, 0);
        Near(frame.Relative(world), new Vector3(3, 6, 0));
        // The ship can turn or brake; the airborne frame must not follow its new velocity.
        var landingShip = new Vector3(20, 0, 10);
        Near(frame.Velocity, departure);
        world.Y = 0;
        Near(frame.LandingLimiter(world, landingShip), new Vector3(33, 0, -10));
        Near(MotionMath.WorldVelocity(frame.Relative(world), frame.Velocity), world);
        Vector3 limitedWorld = MotionMath.WorldVelocity(new Vector3(4, 0, 0), frame.Velocity);
        Near(limitedWorld, new Vector3(24, 0, 10));
        Near(frame.BeginGrounded(limitedWorld, new Vector3(22, 0, 10)), new Vector3(4, 0, 0));
        // Re-entering the late limiter must not apply the acceleration a second time.
        Near(frame.LandingLimiter(new Vector3(26, 0, 10), new Vector3(22, 0, 10)), new Vector3(4, 0, 0));
    }

    internal static void CollisionAndRelease()
    {
        var frame = new MotionFrame();
        frame.BeginGrounded(new Vector3(50, 0, 0), new Vector3(50, 0, 0));
        frame.LeaveGround(0);
        // Use solver outputs for a wall impact and a ceiling impact. Frame conversion
        // must retain both impulses instead of restoring pre-collision velocity.
        foreach (Vector3 result in new[] { new Vector3(10, 2, 0), new Vector3(50, -3, 0) })
            Near(MotionMath.WorldVelocity(frame.Relative(result), frame.Velocity), result);
        Check(!frame.CanRemainAirborne(.2f, false), "Leaving the ship footprint retained tracking.");
        frame.LeaveGround(5.9f);
        Check(!frame.CanRemainAirborne(6.01f, true), "Repeated airborne ticks reset the timeout.");
        Check(!frame.CanRemainAirborne(-1f, true) && !frame.CanRemainAirborne(float.NaN, true), "Invalid clock accepted.");
        frame.Clear();
        Check(frame.Phase == PassengerPhase.Detached && !frame.LeaveGround(0), "Detached player acquired airborne support.");
        Near(frame.Velocity, Vector3.Zero);
        Near(frame.BeginGrounded(new Vector3(14, 0, 0), new Vector3(10, 0, 0)), new Vector3(4, 0, 0));
    }

    internal static void DragRestoration()
    {
        var lease = new DragOverride();
        Check(lease.Begin(.1f) == 0f && lease.Active, "Native drag was not suspended.");
        Check(lease.Restore(0f) == .1f && !lease.Active, "Original drag was not restored.");
        Check(lease.Restore(.4f) == .4f, "Repeated cleanup changed drag.");
        lease.Begin(.1f);
        Check(lease.Restore(.3f) == .3f, "A later component's drag change was overwritten.");
        var carrier = new Vector3(50, 0, 0);
        Near(MotionMath.RelativeDragAcceleration(carrier, carrier, 5f, Dt), Vector3.Zero);
        var relative = new Vector3(4, 6, -2);
        Vector3 acceleration = MotionMath.RelativeDragAcceleration(carrier + relative, carrier, 100f, Dt);
        Near(relative + acceleration * Dt, Vector3.Zero); // Saturation may stop, never reverse motion.
        bool rejected = false;
        try { MotionMath.RelativeDragAcceleration(carrier, carrier, float.NaN, Dt); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Invalid drag accepted.");
    }

    internal static void RepeatedJumpsAndDiscontinuities()
    {
        var frame = new MotionFrame();
        Vector3 ship = new Vector3(50, 0, 0), world = ship;
        for (int i = 0; i < 1000; i++)
        {
            Near(frame.BeginGrounded(world, ship), Vector3.Zero);
            frame.LeaveGround(i);
            world += new Vector3(0, 6, 0);
            Near(frame.Relative(world), new Vector3(0, 6, 0));
            world.Y = 0;
            world = MotionMath.WorldVelocity(frame.LandingLimiter(world, ship), frame.Velocity);
            Near(world, ship);
        }
        Check(MotionMath.ContinuousPosition(new Vector3(3000, 0, 0), new Vector3(3001, 0, 0), Dt), "Normal ship motion rejected.");
        Check(!MotionMath.ContinuousPosition(Vector3.Zero, new Vector3(500, 0, 0), Dt), "Teleport retained support.");
        Check(!MotionMath.ContinuousPosition(Vector3.Zero, Vector3.Zero, .5f), "Stale sample retained support.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(Vector3 actual, Vector3 expected, float tolerance = .0001f)
    {
        if (!MotionMath.Finite(actual) || Vector3.Distance(actual, expected) > tolerance)
            throw new Exception("Expected " + expected + ", got " + actual);
    }
}
