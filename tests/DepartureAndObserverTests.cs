using System;
using System.Numerics;
using ShipWalk;

internal static class DepartureAndObserverTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(Vector3 a, Vector3 b) => Check(Vector3.Distance(a, b) < .001f, "Expected " + b + "; got " + a);
    public static void TerrainBeforeBounds()
    {
        // Recorded release was followed by Planet_BelowTerrain. The old
        // envelope admitted a capsule below the ship, even through world soil.
        var volume = new LocalVolume(new Vector3(-20, 0, -30), new Vector3(22, 22, 30));
        Check(volume.Contains(new Vector3(.84f, -1.9f, -22.96f), 2), "Old premature terrain crossing did not reproduce.");
        Vector3 before = new Vector3(-300.08f, 85.2f, 273.58f), after = before - Vector3.UnitY * .3f;
        Vector3 safe = WorldDeparture.BeforeContact(before, after, .2f);
        Check(safe.Y > 85f && after.Y < 85f, "World collision did not stop on the near side of the terrain.");
        Near(WorldDeparture.BeforeContact(before, after, 0), before);
        Near(WorldDeparture.BeforeContact(before, before, 0), before);
        Near(WorldDeparture.BeforeContact(before, after, 100), after);
        // Fast inherited horizontal motion also stops before a real world wall.
        safe = WorldDeparture.BeforeContact(Vector3.Zero, new Vector3(2.5f, -.1f, 0), 1f);
        Check(safe.Length() < 1 && safe.Y < 0, "Swept handoff ignored diagonal travel.");
        var lease = new CharacterBodyLease(); lease.Capture(false, true, 1, false);
        Check(lease.Release() && !lease.Kinematic && lease.DetectCollisions && !lease.Trigger && lease.Interpolation == 1,
            "Solid native walking collision was not restored.");
    }
    public static void WorldSweepAndRelocation()
    {
        Vector3 local = new Vector3(2, 14, 30);
        var a = new LocalFramePose(new Vector3(10000, 80, 300), Quaternion.Identity);
        var b = new LocalFramePose(a.Position + new Vector3(2.5f, 0, 0), Quaternion.Identity);
        Near(WorldDeparture.SweepStart(a, b, local, .025f), a.ToWorldPoint(local));
        var relocated = new LocalFramePose(a.Position + new Vector3(120, -7, 387), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f));
        Near(WorldDeparture.SweepStart(a, relocated, local, .025f), relocated.ToWorldPoint(local));
        // Floating origin changes affect neither absolute sweep endpoints nor
        // local attachment. Convert to scene coordinates only at the query.
        Vector3 offset = new Vector3(10000, 0, 0);
        Near(WorldDeparture.SweepStart(a, b, local, .025f) - offset, a.ToWorldPoint(local) - offset);
    }
    private static FrameMessage State(uint sequence, uint generation, Vector3 local, PassengerMode mode = PassengerMode.Walking)
        => new FrameMessage { Sequence = sequence, Generation = generation, Ship = 1019, Position = local, Mode = mode };
    public static void RemoteRelativeAnimation()
    {
        var remote = new RemotePassenger(); Vector3 local = new Vector3(12.718f, 14.050f, 33.959f);
        remote.Accept(State(1, 1, local), 0);
        Check(remote.TakeLocomotion(0, Quaternion.Identity, out var delta), "Attached remote animation unavailable."); Near(delta, Vector3.Zero);
        // Ship coasting/turning cannot animate a stationary passenger.
        for (uint i = 2; i <= 20; i++)
        {
            remote.Accept(State(i, 1, local), i * .05);
            remote.TakeLocomotion(i * .05 + .025, Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * .1f), out delta);
            Near(delta, Vector3.Zero);
        }
        remote.Accept(State(21, 1, local + Vector3.UnitX * .2f), 1.05);
        Quaternion shipRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI / 2);
        remote.TakeLocomotion(1.075, shipRotation, out delta); Near(delta, new Vector3(0, 0, -.1f));
        remote.TakeLocomotion(1.075, shipRotation, out delta); Near(delta, Vector3.Zero);
        remote.TakeLocomotion(1.1, shipRotation, out delta); Near(delta, new Vector3(0, 0, -.1f));
    }
    public static void RemoteAnimationLifecycle()
    {
        var remote = new RemotePassenger(); remote.Accept(State(1, 1, Vector3.Zero), 0);
        remote.TakeLocomotion(0, Quaternion.Identity, out _);
        remote.Accept(State(2, 2, new Vector3(100, 0, 0)), .1);
        remote.TakeLocomotion(.2, Quaternion.Identity, out var delta); Near(delta, Vector3.Zero);
        remote.Accept(State(3, 2, new Vector3(102, 0, 0), PassengerMode.Jetpack), .2);
        remote.TakeLocomotion(.3, Quaternion.Identity, out delta); Near(delta, Vector3.Zero);
        Check(!remote.TakeLocomotion(2, Quaternion.Identity, out _), "Stale remote attachment animated forever.");
        remote.Accept(State(4, 2, new Vector3(103, 0, 0)), 2.1);
        remote.TakeLocomotion(2.2, Quaternion.Identity, out delta); Near(delta, Vector3.Zero);
        var departure = State(5, 3, Vector3.Zero, PassengerMode.World); departure.Ship = -1;
        remote.Accept(departure, 2.3);
        Check(!remote.TakeLocomotion(2.4, Quaternion.Identity, out _), "Departed player retained local animation override.");
    }
}
