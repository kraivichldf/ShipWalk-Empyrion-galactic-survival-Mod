using System;
using System.Numerics;

namespace ShipWalk
{
    internal static class RemoteFrameMath
    {
        public static bool ValidVelocity(Vector3 velocity)
            => MotionMath.Finite(velocity) && velocity.LengthSquared() <= 300f * 300f;

        public static bool TryTransport(LocalFramePose previous, LocalFramePose current,
            Quaternion armedRotation, float seconds, Vector3 nativeVelocity, out Vector3 transport)
        {
            transport = Vector3.Zero;
            if (!previous.Valid || !current.Valid || !MotionMath.Finite(seconds)
                || seconds < .0001f || seconds > .1f || !ValidVelocity(nativeVelocity)
                || !new LocalFramePose(Vector3.Zero, armedRotation).Valid
                || Math.Abs(Quaternion.Dot(armedRotation, current.Rotation)) < .99999f) return false;
            // Native entity history advances at 0.05 s. Several fixed steps can
            // share one displayed pose, then see a whole history interval move.
            // That display displacement is not a fresh passenger acceleration.
            float maxDisplacement = 300f * Math.Max(seconds, .05f) + .25f;
            if (Vector3.DistanceSquared(previous.Position, current.Position) > maxDisplacement * maxDisplacement) return false;
            transport = nativeVelocity;
            return true;
        }
    }
}
