using System;
using System.Numerics;
using ShipWalk;

internal static class MultiplayerFrameTests
{
    internal static void AuthorityBoundary()
    {
        Check(ClientFramePolicy.CanPrepare(false, true, true, true, false, true), "Remote kinematic vessel rejected on player client.");
        Check(ClientFramePolicy.CanPrepare(false, true, true, false, true, true), "Network ownership change lost client movement.");
        Check(!ClientFramePolicy.OwnsShipPhysics(false), "Multiplayer client took ship physics ownership.");
        Check(!ClientFramePolicy.CanPrepare(false, false, true, false, true, true), "Server with no local character admitted.");
        Check(!ClientFramePolicy.CanPrepare(false, true, false, true, false, true), "Non-vessel frame admitted.");
        Check(!ClientFramePolicy.CanPrepare(false, true, true, true, false, false), "Docked frame admitted before its adapter exists.");
        Check(ClientFramePolicy.SameSessionMode(true, false, true), "Live client frame rejected.");
        Check(!ClientFramePolicy.SameSessionMode(true, true, false), "Old network frame adopted single-player physics after a session change.");
        Check(!ClientFramePolicy.SameSessionMode(false, false, true), "Old single-player frame survived a multiplayer transition.");
        Check(!ClientFramePolicy.SameSessionMode(true, false, false), "Frame survived loss of the client role.");
    }

    internal static void SinglePlayerBaseline()
    {
        Check(ClientFramePolicy.CanPrepare(true, false, true, false, true, true), "Original local ship rejected.");
        Check(ClientFramePolicy.OwnsShipPhysics(true), "Single-player coast/selector ownership was removed.");
        Check(!ClientFramePolicy.CanPrepare(true, false, true, true, false, true), "Single-player accepted invalid remote ownership.");
        Check(!ClientFramePolicy.CanPrepare(true, false, true, false, false, true), "Invalid local body accepted.");
    }

    internal static void RepeatedDisplayPoses()
    {
        Vector3 speed = new Vector3(100.5f, 0, 0);
        var previous = new LocalFramePose(Vector3.Zero, Quaternion.Identity);
        Vector3 airborneWorld = speed + Vector3.UnitY * 6f;
        // Native history moves at 20 Hz, while this character steps at 50 Hz.
        foreach (float position in new[] { 0f, 5.025f, 5.025f, 10.05f, 10.05f })
        {
            var current = new LocalFramePose(new Vector3(position, 0, 0), Quaternion.Identity);
            Check(RemoteFrameMath.TryTransport(previous, current, Quaternion.Identity, .02f, speed, out var transport), "Ordinary history step rejected.");
            Near(transport, speed);
            Near(current.ToLocalVelocity(airborneWorld, transport), Vector3.UnitY * 6f);
            previous = current;
        }
        // Reproduce why displayed displacement alone cannot drive remote inertia.
        Check(LocalFrameMath.TryTransport(previous, previous, Quaternion.Identity, .02f, out var oldTransport), "Baseline sample failed.");
        Check(previous.ToLocalVelocity(airborneWorld, oldTransport).X > 100f, "Original duplicate-pose failure was not reproduced.");
    }

    internal static void RemoteAccelerationAndCollision()
    {
        var pose = new LocalFramePose(Vector3.Zero, Quaternion.Identity);
        Vector3 world = new Vector3(50, 6, 0);
        Check(RemoteFrameMath.TryTransport(pose, pose, Quaternion.Identity, .02f, new Vector3(80, 0, 0), out var faster), "Acceleration rejected.");
        Near(pose.ToLocalVelocity(world, faster), new Vector3(-30, 6, 0));
        Near(pose.ToWorldVelocity(pose.ToLocalVelocity(world, faster), faster), world);
        // A wall removes local horizontal motion. Publish the solver result,
        // without writing ship velocity or restoring the prior character speed.
        Vector3 accepted = new Vector3(0, -1, 0);
        world = pose.ToWorldVelocity(accepted, faster);
        Near(pose.ToLocalVelocity(world, faster), accepted);
        Check(RemoteFrameMath.TryTransport(pose, pose, Quaternion.Identity, .02f, Vector3.Zero, out var stopped), "Stopped ship rejected.");
        Near(pose.ToLocalVelocity(world, stopped), world);
    }

    internal static void LiveHullPresentation()
    {
        var presentation = new LocalPresentation();
        Vector3 local = new Vector3(2, 5, -7);
        presentation.Reset(local);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .75f);
        for (int frame = 0; frame < 240; frame++)
        {
            var hull = new LocalFramePose(new Vector3(frame * 100.5f / 60f, 20, 30), rotation);
            Vector3 display = presentation.WorldPoint(hull, .6f);
            Near(hull.ToLocalPoint(display), local);
            Near(presentation.TakeLocomotion(rotation), Vector3.Zero);
        }
        presentation.Advance(local + Vector3.UnitX);
        var laterHull = new LocalFramePose(new Vector3(410, 20, 30), rotation);
        Near(laterHull.ToLocalPoint(presentation.WorldPoint(laterHull, 1f)), local + Vector3.UnitX);
        Near(presentation.TakeLocomotion(rotation), Vector3.Transform(Vector3.UnitX, rotation));
    }

    internal static void RemoteOriginAndTilt()
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);
        Vector3 origin = new Vector3(8000, 0, -8000), relativeShip = new Vector3(10, 20, 30);
        Vector3 absolute = relativeShip + origin, velocity = new Vector3(0, 0, 100.5f);
        var before = new LocalFramePose(absolute, rotation);
        origin += new Vector3(1000, 0, 0); relativeShip -= new Vector3(1000, 0, 0);
        var after = new LocalFramePose(relativeShip + origin + velocity * .02f, rotation);
        Check(RemoteFrameMath.TryTransport(before, after, rotation, .02f, velocity, out var frame), "Floating-origin shift rejected.");
        Vector3 local = new Vector3(3, 8, 12);
        Near(after.ToLocalVelocity(after.ToWorldVelocity(local, frame), frame), local);
    }

    internal static void InvalidRemoteState()
    {
        var pose = new LocalFramePose(Vector3.Zero, Quaternion.Identity);
        foreach (Vector3 velocity in new[] { new Vector3(float.NaN, 0, 0), new Vector3(1000, 0, 0) })
            Check(!RemoteFrameMath.TryTransport(pose, pose, Quaternion.Identity, .02f, velocity, out _), "Invalid remote velocity accepted.");
        foreach (float dt in new[] { 0f, -.02f, float.NaN, .2f })
            Check(!RemoteFrameMath.TryTransport(pose, pose, Quaternion.Identity, dt, Vector3.Zero, out _), "Invalid remote timestep accepted.");
        Check(!RemoteFrameMath.TryTransport(pose, new LocalFramePose(new Vector3(500, 0, 0), Quaternion.Identity), Quaternion.Identity, .02f, Vector3.Zero, out _), "Teleport accepted as ordinary ship motion.");
        Check(!RemoteFrameMath.TryTransport(pose, new LocalFramePose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .2f)), Quaternion.Identity, .02f, Vector3.Zero, out _), "Unsupported turning frame accepted.");
    }

    internal static void ObserverTimingEvidence()
    {
        // Native world-position replication can have different display times.
        // This first slice measures that gap; it must not pretend that a local
        // movement test proves observers already share a synchronized frame.
        Vector3 local = new Vector3(2, 5, 8), speed = new Vector3(100.5f, 0, 0);
        var passengerHull = new LocalFramePose(speed * 1f, Quaternion.Identity);
        Vector3 replicatedWorld = passengerHull.ToWorldPoint(local);
        var observerHull = new LocalFramePose(speed * 1.1f, Quaternion.Identity);
        Vector3 observedLocal = observerHull.ToLocalPoint(replicatedWorld);
        Check(Vector3.Distance(observedLocal, local) > 10f, "Different replication clocks did not expose expected observer offset.");
        Near(observerHull.ToLocalPoint(observerHull.ToWorldPoint(local)), local);
    }

    private static void Near(Vector3 actual, Vector3 expected)
        => Check(Vector3.Distance(actual, expected) < .003f, "Expected " + expected + "; actual " + actual);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
