using System;
using System.Numerics;
using ShipWalk;

internal static class LocalFrameTests
{
    internal static void RoundTripsAndOrigin()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
        var frame = new LocalFramePose(new Vector3(10000, 100, -7000), q);
        var point = new Vector3(3, 2, -6);
        var localVelocity = new Vector3(4, 6, 1);
        var transport = new Vector3(70, 0, -20);
        Near(frame.ToLocalPoint(frame.ToWorldPoint(point)), point, .002f);
        Near(frame.ToLocalVelocity(frame.ToWorldVelocity(localVelocity, transport), transport), localVelocity);
        Vector3 oldOrigin = new Vector3(10000, 0, -7000), newOrigin = new Vector3(11000, 0, -6000);
        Vector3 oldWorld = frame.ToWorldPoint(point) - oldOrigin;
        Vector3 newWorld = frame.ToWorldPoint(point) - newOrigin;
        Near(oldWorld + oldOrigin, newWorld + newOrigin, .002f);
        var shifted = new LocalFramePose((frame.Position - newOrigin) + newOrigin, q);
        Check(LocalFrameMath.TryTransport(frame, shifted, q, .025f, out Vector3 velocity), "Origin-only sample rejected.");
        Near(velocity, Vector3.Zero);
    }

    internal static void AcceleratingJump()
    {
        const float dt = .025f;
        var previous = new LocalFramePose(Vector3.Zero, Quaternion.Identity);
        Vector3 local = new Vector3(0, 2, 0), world = local;
        Vector3 inertia = new Vector3(100, 6, 0);
        for (int i = 0; i < 80; i++)
        {
            // Accelerating, braking and reversing ship, but no air control.
            float speed = i < 25 ? 100 + i : i < 50 ? 30 : -10;
            var current = new LocalFramePose(previous.Position + new Vector3(speed * dt, 0, 0), Quaternion.Identity);
            Check(LocalFrameMath.TryTransport(previous, current, Quaternion.Identity, dt, out Vector3 transport), "Valid transport rejected.");
            Vector3 relative = current.ToLocalVelocity(inertia, transport);
            relative.Y -= LocalFrameMath.Gravity * dt;
            local += relative * dt;
            inertia.Y -= LocalFrameMath.Gravity * dt;
            world += inertia * dt;
            Near(current.ToWorldPoint(local), world, .002f);
            Near(current.ToWorldVelocity(relative, transport), inertia);
            previous = current;
        }
        // Disproof case: final instantaneous speed differs from interval speed.
        float displacement = 2.625f, finalSpeed = 110, worldSpeed = 100;
        float wrongWorldStep = displacement + (worldSpeed - finalSpeed) * dt;
        Check(Math.Abs(wrongWorldStep - worldSpeed * dt) > .1f, "Fixture did not expose wrong interval transport.");
    }

    internal static void GroundAndCollisionTransfer()
    {
        foreach (float speed in new[] { 0f, 30f, 100f, -30f })
        {
            var frame = new LocalFramePose(new Vector3(speed, 0, 0), Quaternion.Identity);
            Vector3 transport = new Vector3(speed, 0, 0);
            Vector3 walking = new Vector3(0, 0, 4);
            Near(frame.ToWorldVelocity(walking, transport), new Vector3(speed, 0, 4));
            // Post-solver velocity is the next airborne inertial state. Do not
            // overwrite a collision result with the pre-contact frame velocity.
            Vector3 collisionResult = new Vector3(-2, 1, 0);
            Vector3 inherited = frame.ToWorldVelocity(collisionResult, transport);
            Near(frame.ToLocalVelocity(inherited, transport), collisionResult);
            Near(frame.ToWorldVelocity(collisionResult, transport), inherited);
        }
    }

    internal static void GuardUnsupportedFrames()
    {
        foreach (string type in new[] { "CV", "SV", "HV" })
            Check(LocalFrameMath.IsVessel(type), "A previously supported moving-seat vessel type was excluded.");
        foreach (string type in new[] { "BA", "Player", "Unknown", null })
            Check(!LocalFrameMath.IsVessel(type), "World/base context was treated as a moving vessel.");
        var frame = new LocalFramePose(Vector3.Zero, Quaternion.Identity);
        Check(LocalFrameMath.CanArm(512, true, true, true, true, 0), "Small CV rejected.");
        Check(LocalFrameMath.CanArm(7417, true, true, true, true, 0), "Recorded large CV was rejected.");
        Check(LocalFrameMath.CanArm(50000, true, true, true, true, 0), "A hidden shape cap remains.");
        Check(!LocalFrameMath.CanArm(0, true, true, true, true, 0), "Empty geometry accepted.");
        for (int i = 0; i < 4; i++)
        {
            bool[] flags = { true, true, true, true }; flags[i] = false;
            Check(!LocalFrameMath.CanArm(1, flags[0], flags[1], flags[2], flags[3], 0), "Scope guard missing.");
        }
        Check(!LocalFrameMath.CanArm(1, true, true, true, true, float.NaN), "Invalid spin accepted.");
        Check(!LocalFrameMath.CanArm(1, true, true, true, true, .02f), "Turning ship accepted.");
        foreach (float dt in new[] { 0, -.1f, .2f, float.NaN })
            Check(!LocalFrameMath.TryTransport(frame, frame, Quaternion.Identity, dt, out _), "Invalid step accepted.");
        Check(!LocalFrameMath.TryTransport(frame, new LocalFramePose(new Vector3(100, 0, 0), Quaternion.Identity),
            Quaternion.Identity, .025f, out _), "Teleport accepted.");
        Check(!LocalFrameMath.TryTransport(frame, new LocalFramePose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .1f)),
            Quaternion.Identity, .025f, out _), "Rotating frame accepted.");
        Check(!LocalFrameMath.TryTransport(frame, new LocalFramePose(Vector3.Zero, new Quaternion(0, 0, 0, 0)),
            Quaternion.Identity, .025f, out _), "Invalid rotation accepted.");
        var equivalent = new LocalFramePose(Vector3.Zero, new Quaternion(0, 0, 0, -1));
        Check(LocalFrameMath.TryTransport(frame, equivalent, Quaternion.Identity, .025f, out _), "Equivalent quaternion sign rejected.");
    }

    internal static void RestoreLeaseOnce()
    {
        foreach (bool kinematic in new[] { false, true })
            foreach (bool collisions in new[] { false, true })
            {
                var lease = new CharacterBodyLease();
                lease.Capture(kinematic, collisions);
                bool duplicateRejected = false;
                try { lease.Capture(!kinematic, !collisions); } catch (InvalidOperationException) { duplicateRejected = true; }
                Check(duplicateRejected, "Second owner overwrote original body flags.");
                Check(lease.Release() && !lease.Held, "Release failed.");
                Check(lease.Kinematic == kinematic && lease.DetectCollisions == collisions, "Original flags lost.");
                Check(!lease.Release(), "Repeated cleanup claimed ownership twice.");
                lease.Capture(!kinematic, !collisions);
                Check(lease.Release(), "Next session could not acquire/release.");
            }
    }

    internal static void TiltedFrameRegression()
    {
        // Recorded CV 1021 orientation from the prior client diagnostic capture.
        // The current 0.2.0 refusal combined several conditions and did not log
        // its exact orientation, so this is a historical regression fixture.
        Quaternion recorded = Quaternion.Normalize(new Quaternion(.30007f, .64519f, -.69153f, -.12441f));
        Check(Vector3.Transform(Vector3.UnitY, recorded).Y < 0f, "Fixture no longer exposes the world-up refusal.");
        foreach (Quaternion q in new[] { recorded, Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Math.PI / 2),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.PI / 2),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.PI) })
        {
            foreach (float yaw in new[] { 0f, .5f, 1.5f, -2f })
            {
                Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
                Check(LocalFrameMath.TryLocalHeading(q, q * facing, out Quaternion actual), "Valid deck heading rejected.");
                Near(Vector3.Transform(Vector3.UnitZ, actual), Vector3.Transform(Vector3.UnitZ, facing));
                Near(Vector3.Transform(Vector3.UnitY, actual), Vector3.UnitY);
            }
            Quaternion verticalFacing = q * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Math.PI / 2);
            Check(LocalFrameMath.TryLocalHeading(q, verticalFacing, out Quaternion verticalHeading), "Vertical facing lost its right-axis heading.");
            Near(Vector3.Transform(Vector3.UnitZ, verticalHeading), Vector3.UnitZ);
            var previous = new LocalFramePose(Vector3.Zero, q);
            Vector3 local = new Vector3(1, 2, 3), world = previous.ToWorldPoint(local);
            Vector3 inertia = new Vector3(100, 0, 0) + Vector3.Transform(new Vector3(0, 6, 0), q);
            for (int tick = 0; tick < 40; tick++)
            {
                const float dt = .025f;
                var current = new LocalFramePose(previous.Position + new Vector3((100 + tick) * dt, 0, 0), q);
                Check(LocalFrameMath.TryTransport(previous, current, q, dt, out Vector3 transport), "A constant tilted pose was treated as turning.");
                Vector3 relative = current.ToLocalVelocity(inertia, transport);
                relative.Y -= LocalFrameMath.Gravity * dt;
                local += relative * dt;
                inertia += Vector3.Transform(new Vector3(0, -LocalFrameMath.Gravity * dt, 0), q);
                world += inertia * dt;
                Near(current.ToWorldPoint(local), world, .002f);
                Near(current.ToWorldVelocity(relative, transport), inertia, .002f);
                previous = current;
            }
        }
        Check(LocalFrameMath.CapsuleAxisSupported(Vector3.UnitY) && LocalFrameMath.CapsuleAxisSupported(-Vector3.UnitY), "Body-local capsule alignment rejected.");
        foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.Zero, new Vector3(float.NaN, 1, 0) })
            Check(!LocalFrameMath.CapsuleAxisSupported(axis), "Invalid or sideways native capsule accepted.");
        Check(!LocalFrameMath.TryLocalHeading(default(Quaternion), Quaternion.Identity, out _), "Invalid ship orientation accepted.");
    }
    private static void Near(Vector3 actual, Vector3 expected, float tolerance = .0001f)
    { Check(Vector3.Distance(actual, expected) <= tolerance, "Expected " + expected + "; actual " + actual); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
