using System;
using System.Numerics;

namespace ShipWalk
{
    // No Unity dependency. Position is absolute, including the floating origin.
    internal readonly struct LocalFramePose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public LocalFramePose(Vector3 position, Quaternion rotation)
        { Position = position; Rotation = rotation; }
        public Vector3 ToWorldPoint(Vector3 local) => Position + Vector3.Transform(local, Rotation);
        public Vector3 ToLocalPoint(Vector3 world) => Vector3.Transform(world - Position, Quaternion.Inverse(Rotation));
        public Vector3 ToWorldVelocity(Vector3 local, Vector3 transport) => transport + Vector3.Transform(local, Rotation);
        public Vector3 ToLocalVelocity(Vector3 world, Vector3 transport) => Vector3.Transform(world - transport, Quaternion.Inverse(Rotation));
        public bool Valid => MotionMath.Finite(Position) && MotionMath.Finite(Rotation.X)
            && MotionMath.Finite(Rotation.Y) && MotionMath.Finite(Rotation.Z) && MotionMath.Finite(Rotation.W)
            && Math.Abs(Rotation.LengthSquared() - 1f) < .001f;
    }

    internal static class LocalFrameMath
    {
        public const float WalkSpeed = 4f, JumpSpeed = 6f, Gravity = 9.81f;
        public static bool IsVessel(string type) => type == "CV" || type == "SV" || type == "HV";

        // Shape orientation belongs to the character body, not world up. A
        // seated character and a CV in orbit can have any fixed world rotation.
        public static bool CapsuleAxisSupported(Vector3 axisInBody)
            => MotionMath.Finite(axisInBody) && Math.Abs(axisInBody.LengthSquared() - 1f) < .001f
                && Math.Abs(Vector3.Dot(axisInBody, Vector3.UnitY)) >= .999f;

        public static bool TryLocalHeading(Quaternion shipRotation, Quaternion worldFacing, out Quaternion heading)
        {
            heading = Quaternion.Identity;
            if (!new LocalFramePose(Vector3.Zero, shipRotation).Valid
                || !new LocalFramePose(Vector3.Zero, worldFacing).Valid) return false;
            Vector3 forward = Vector3.Transform(Vector3.Transform(Vector3.UnitZ, worldFacing), Quaternion.Inverse(shipRotation));
            forward.Y = 0f;
            // Looking exactly along deck-up has no projected forward. Preserve
            // the projected right axis in that case rather than normalizing zero.
            if (forward.LengthSquared() < .000001f)
            {
                Vector3 right = Vector3.Transform(Vector3.Transform(Vector3.UnitX, worldFacing), Quaternion.Inverse(shipRotation));
                right.Y = 0f;
                forward = Vector3.Cross(right, Vector3.UnitY);
            }
            if (!MotionMath.Finite(forward) || forward.LengthSquared() < .000001f) return false;
            heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.Atan2(forward.X, forward.Z));
            return true;
        }

        // The frame displacement over THIS interval must be subtracted for an
        // airborne passenger. Subtracting only the final instantaneous ship
        // velocity adds spurious acceleration when the frame changes speed.
        public static bool TryTransport(LocalFramePose previous, LocalFramePose current,
            Quaternion armedRotation, float seconds, out Vector3 transport)
        {
            transport = Vector3.Zero;
            if (!previous.Valid || !current.Valid || !MotionMath.Finite(seconds)
                || seconds < .0001f || seconds > .1f) return false;
            if (Math.Abs(Quaternion.Dot(armedRotation, current.Rotation)) < .99999f) return false;
            transport = (current.Position - previous.Position) / seconds;
            return MotionMath.Finite(transport) && transport.LengthSquared() <= 300f * 300f;
        }

        public static bool CanArm(int shapes, bool singlePlayer, bool vessel,
            bool localAuthority, bool undocked, float angularSpeed)
            => shapes > 0 && singlePlayer && vessel
                && localAuthority && undocked && MotionMath.Finite(angularSpeed) && angularSpeed <= .01f;
    }

    // A one-shot ownership record, independent of Unity and exercised by tests.
    internal sealed class CharacterBodyLease
    {
        public bool Held { get; private set; }
        public bool Kinematic { get; private set; }
        public bool DetectCollisions { get; private set; }
        public int Interpolation { get; private set; }
        public bool Trigger { get; private set; }
        public void Capture(bool kinematic, bool detectCollisions, int interpolation = 0, bool trigger = false)
        {
            if (Held) throw new InvalidOperationException("Character physics already leased.");
            Kinematic = kinematic; DetectCollisions = detectCollisions; Interpolation = interpolation; Trigger = trigger; Held = true;
        }
        public bool Release() { bool held = Held; Held = false; return held; }
    }
}
