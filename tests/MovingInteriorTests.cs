using System;
using System.Collections.Generic;
using System.Numerics;
using ShipWalk;

internal static class MovingInteriorTests
{
    public static void RecordedExitRegression()
    {
        // 0.1.13 CSV 3627..3635, CV 1016: these solver impulses explain
        // the first loss, independently of the character's controller code.
        var inherited = new Vector3(99.76904f, 9.55983f, 7.41606f);
        var impulses = new Vector3(-9.74726f, -.84168f, 1.17703f);
        Near(inherited + impulses / .1f, new Vector3(2.29643f, 1.14300f, 19.18630f), .00015f);
        var start = new Vector3(102.78240f, 20.35202f, 10.18824f);
        Check(InteriorStep.TryPlan(start, Quaternion.Identity, start, Quaternion.Identity, start,
            inherited, Vector3.Zero, .025f, out var step), "recorded motion accepted");
        Vector3 cockpit = start + new Vector3(0, 1, 37);
        Near(inherited - step.PointVelocity(cockpit), Vector3.Zero, .0005f);
        Check(inherited.Length() > 100f, "stationary cockpit produces the recorded full-speed contact");
        Vector3 walk = new Vector3(2, 0, -3);
        Near(inherited + walk - step.PointVelocity(cockpit), walk, .0005f);
        // Every copied hull point shares this translation, not a one-frame pose teleport.
        Near(step.Target - step.Start, inherited * .025f, .00003f);
    }

    public static void RotationAndOffsetCenter()
    {
        var ship = new Vector3(2, 0, 3);
        var center = new Vector3(5, 1, 4);
        var velocity = new Vector3(40, 1, 3);
        var omega = new Vector3(0, .8f, 0);
        Check(InteriorStep.TryPlan(ship, Quaternion.Identity, ship, Quaternion.Identity, center,
            velocity, omega, .025f, out var step), "rotating plan");
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f * .025f);
        Near(step.Target + Vector3.Transform(center - ship, turn), center + velocity * .025f);
        Near(step.Angular, omega);
        Vector3 port = ship + new Vector3(-10, 0, 0), starboard = ship + new Vector3(10, 0, 0);
        Near(step.PointVelocity(starboard) - step.PointVelocity(port), Vector3.Cross(omega, starboard - port));
        Near(step.PointVelocity(starboard + step.Target - step.Start, step.Target), step.PointVelocity(starboard));
        Check(Vector3.Distance(step.PointVelocity(starboard), step.PointVelocity(port)) > 15f,
            "turning surfaces must not all get one identical linear velocity");
    }

    public static void ExitPoseAndFloatingOrigin()
    {
        var visual = new Vector3(104.20710f, 20.48853f, 10.29414f);
        var physical = new Vector3(102.78240f, 20.35202f, 10.18824f);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f);
        var physicalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .45f);
        var local = new Vector3(2, 1, 37);
        var point = visual + Vector3.Transform(local, rotation);
        var mapped = InteriorStep.MapPoint(point, visual, rotation, physical, physicalRotation);
        Near(Vector3.Transform(mapped - physical, Quaternion.Inverse(physicalRotation)), local);
        var origin = new Vector3(3000, 0, 1000);
        Near(InteriorStep.MapPoint(point - origin, visual - origin, rotation,
            physical - origin, physicalRotation) + origin, mapped, .001f);
        // Origin changes convert the stored absolute proxy pose, not a physical sweep.
        Vector3 absolute = physical + new Vector3(1000, 0, 0);
        Vector3 rebased = absolute - origin;
        Near(rebased + origin, absolute, .001f);
    }

    public static void TrackingAndAirborne()
    {
        Vector3 start = Vector3.Zero, ship = Vector3.Zero, world = new Vector3(100, 0, 0);
        var frame = new MotionFrame();
        frame.BeginSeatExit(world, 0f);
        for (int i = 0; i < 1000; i++)
        {
            // Include a small physical integration error; the proxy corrects it
            // through one target and the character uses that exact trajectory.
            Vector3 velocity = new Vector3(100 + (float)Math.Sin(i * .01), 0, 0);
            Check(InteriorStep.TryPlan(start, Quaternion.Identity, ship, Quaternion.Identity, ship,
                velocity, Vector3.Zero, .025f, out var step), "continuous target accepted");
            Vector3 support = step.PointVelocity(start + Vector3.UnitY);
            Vector3 relative = frame.BeginGrounded(world, support);
            world = relative + support;
            Near(world - support, Vector3.Zero, .0002f);
            start = step.Target;
            ship += velocity * .025f;
            if (i % 10 == 0) ship.X += .001f;
        }
        frame.LeaveGround(25f);
        Vector3 takeoff = frame.Velocity + Vector3.UnitY * 6f;
        Near(frame.Relative(takeoff), Vector3.UnitY * 6f);
        Near(frame.Relative(takeoff) + frame.Velocity, takeoff);
        // Later vessel acceleration does not reattach or add velocity mid-jump.
        var departure = frame.Velocity;
        Check(InteriorStep.TryPlan(start, Quaternion.Identity, ship, Quaternion.Identity, ship,
            new Vector3(110, 0, 0), Vector3.Zero, .025f, out _), "accelerated hull remains independent");
        Near(frame.Velocity, departure);
    }

    public static void RejectDiscontinuity()
    {
        Check(!InteriorStep.TryPlan(Vector3.Zero, Quaternion.Identity, new Vector3(1000, 0, 0),
            Quaternion.Identity, new Vector3(1000, 0, 0), Vector3.Zero, Vector3.Zero, .025f, out _), "teleport rejected");
        Check(!InteriorStep.TryPlan(Vector3.Zero, Quaternion.Identity, Vector3.Zero,
            Quaternion.Identity, Vector3.Zero, Vector3.One, Vector3.Zero, 0f, out _), "zero dt rejected");
        Check(!InteriorStep.TryPlan(Vector3.Zero, Quaternion.Identity, Vector3.Zero,
            Quaternion.Identity, Vector3.Zero, new Vector3(float.NaN, 0, 0), Vector3.Zero, .025f, out _), "NaN rejected");
        Check(!InteriorStep.TryPlan(Vector3.Zero, default, Vector3.Zero,
            Quaternion.Identity, Vector3.Zero, Vector3.Zero, Vector3.Zero, .025f, out _), "invalid rotation rejected");
        Check(!InteriorStep.TryPlan(Vector3.Zero, Quaternion.Identity, Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1), Vector3.Zero, Vector3.Zero, Vector3.Zero, .025f, out _), "rotation discontinuity rejected");
        Check(InteriorStep.TryPlan(Vector3.Zero, Quaternion.Identity, Vector3.Zero,
            new Quaternion(0, 0, 0, -1), Vector3.Zero, Vector3.Zero, Vector3.Zero, .025f, out var stationary), "equivalent quaternion accepted");
        Near(stationary.Angular, Vector3.Zero);
    }

    public static void PlayerOnlyFilterAndLease()
    {
        var allowed = new HashSet<ulong> { InteriorContactFilter.Pair(-1000, -50) };
        var filter = new InteriorContactFilter(100, 5, allowed);
        allowed.Clear();
        Check(!filter.Reject(100, 5, -1000, -50) && !filter.Reject(5, 100, -50, -1000), "eligible player shape retained in either order and snapshot is immutable");
        Check(filter.Reject(100, 5, -1000, -51) && filter.Reject(5, 100, -51, -1000), "same-body camera/model shape rejected");
        Check(filter.Reject(100, 5, -1001, -50), "unknown copy fails closed");
        foreach (int other in new[] { 0, 6, 40, 100 })
            Check(filter.Reject(100, other, -1000, -50) && filter.Reject(other, 100, -50, -1000), "terrain/remote/body/self contact excluded");
        Check(!filter.Reject(5, 40, -50, -1000) && !filter.Reject(0, 6, 1, 2), "unrelated native contacts untouched");
        var player = new object(); var wall = new object(); var external = new object(); var added = new object();
        var ignored = new HashSet<Tuple<object, object>> { Tuple.Create(player, external) };
        var pairs = new CollisionPairs<object>(_ => true, _ => true,
            (a,b) => ignored.Contains(Tuple.Create(a,b)),
            (a,b,value) => { if (value) ignored.Add(Tuple.Create(a,b)); else ignored.Remove(Tuple.Create(a,b)); });
        Check(!pairs.OriginalIgnore(player, wall).HasValue, "uncaptured pairs remain distinguishable");
        pairs.Refresh(new[] { player }, new[] { wall, external });
        Check(pairs.OriginalIgnore(player, wall) == false && pairs.OriginalIgnore(player, external) == true,
            "replacement contacts distinguish our ignore from an external ignore");
        pairs.Refresh(new[] { player }, new[] { added, external });
        Check(!ignored.Contains(Tuple.Create(player, wall)), "removed geometry restores native pair");
        Check(pairs.OriginalIgnore(player, added) == false && pairs.OriginalIgnore(player, external) == true,
            "rebuild preserves original exclusions");
        pairs.Reset();
        Check(ignored.Count == 1 && ignored.Contains(Tuple.Create(player, external)), "cleanup restores ownership");
    }

    private static void Near(Vector3 actual, Vector3 expected, float tolerance = .0001f)
    { if (Vector3.Distance(actual, expected) > tolerance) throw new Exception(actual + " != " + expected); }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
