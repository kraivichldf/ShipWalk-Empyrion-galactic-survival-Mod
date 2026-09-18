using System;
using System.Numerics;
using ShipWalk;

internal static class LocalTransitionTests
{
    internal static void PilotHandoff()
    {
        var state = new PhysicsActivity(true);
        Check(state.Request(false), "Unpiloted simulation should stay active.");
        Check(!state.Requested, "Native selector cache must retain its own request.");
        // Previously Cancel restored Requested=false as a pilot entered.
        Check(state.Release(true), "Pilot takeover deactivated the moving ship.");
        Check(!state.Release(false), "Real shutdown must restore the native selection.");
        Check(state.Request(true) && state.Release(false), "Native moving-body selection lost.");
    }

    internal static void TriggerLease()
    {
        var state = new CharacterBodyLease();
        for (int i = 0; i < 100; i++)
        {
            state.Capture(false, true, 1, false);
            Check(state.Held && !state.Kinematic && state.DetectCollisions && !state.Trigger,
                "Walking shape's original state lost.");
            Check(state.Release() && !state.Release(), "Seat/disable release was not one-shot.");
            Check(state.Interpolation == 1 && !state.Trigger, "Seat release must restore solid interpolated character.");
        }
        state.Capture(true, false, 2, true);
        state.Release();
        Check(state.Kinematic && !state.DetectCollisions && state.Trigger && state.Interpolation == 2,
            "Preexisting body flags were not preserved.");
    }

    internal static void FlightTransport()
    {
        Vector3 idle = Vector3.Zero, ship = new Vector3(0, 0, 55.566f);
        var pose = new LocalFramePose(Vector3.Zero, Quaternion.Identity);
        for (int i = 0; i < 100; i++)
        {
            idle = LocalFlight.Step(idle, Quaternion.Identity, Vector3.Zero, .02f);
            Near(pose.ToWorldVelocity(idle, ship), ship);
        }
        Vector3 thrust = LocalFlight.Step(idle, Quaternion.Identity, Vector3.UnitY, .02f);
        Check(thrust.Y > 0 && thrust.Y <= LocalFlight.Acceleration * .02f + .0001f, "No controlled ascent.");
        Near(pose.ToWorldVelocity(thrust, ship), ship + thrust);
        // Toggle off -> normal airborne inertia, not zero world velocity.
        Vector3 velocity = pose.ToWorldVelocity(thrust, ship);
        var nextPose = new LocalFramePose(ship * .02f, Quaternion.Identity);
        Check(LocalFrameMath.TryTransport(pose, nextPose, Quaternion.Identity, .02f, out var transport), "Bad frame.");
        Near(nextPose.ToWorldVelocity(nextPose.ToLocalVelocity(velocity, transport), transport), velocity);
    }

    internal static void FlightControlsAndCollision()
    {
        Vector3 velocity = Vector3.Zero;
        Quaternion heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI / 2);
        for (int i = 0; i < 60; i++) velocity = LocalFlight.Step(velocity, heading, Vector3.UnitZ, .02f);
        Near(velocity, new Vector3(LocalFlight.Speed, 0, 0));
        // The collision solver may remove this component. The next input-free
        // step consumes its result, never a cached pre-impact flight velocity.
        velocity = Vector3.Zero;
        Near(LocalFlight.Step(velocity, heading, Vector3.Zero, .02f), Vector3.Zero);
        for (int i = 0; i < 100; i++) velocity = LocalFlight.Step(velocity, heading, new Vector3(1, 1, 1), .02f);
        Check(Math.Abs(velocity.Length() - LocalFlight.Speed) < .0001f, "Diagonal flight exceeds configured speed.");
        for (int i = 0; i < 100; i++) velocity = LocalFlight.Step(velocity, heading, -Vector3.UnitY, .02f);
        Near(velocity, -Vector3.UnitY * LocalFlight.Speed);
    }

    internal static void FlightRejectsInvalidData()
    {
        foreach (float dt in new[] { 0f, -.02f, .5f, float.NaN })
        {
            bool rejected = false;
            try { LocalFlight.Step(Vector3.Zero, Quaternion.Identity, Vector3.Zero, dt); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid flight timestep accepted.");
        }
    }
    private static void Near(Vector3 a, Vector3 b) => Check(Vector3.Distance(a, b) < .001f, "Expected " + b + "; actual " + a);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
