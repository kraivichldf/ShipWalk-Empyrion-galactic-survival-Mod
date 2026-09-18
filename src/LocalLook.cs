using System;
using System.Numerics;

namespace ShipWalk
{
    internal static class LocalLook
    {
        // PrintAddin has already applied the user's look settings and encoded
        // free-flight mouse/roll input as dt * (15, 4, 20). Consume that sample
        // directly because the suspended world FixedUpdate cannot turn us.
        public static bool TryRotation(Vector3 sample, float seconds, out Quaternion delta)
        {
            delta = Quaternion.Identity;
            if (!MotionMath.Finite(sample) || !MotionMath.Finite(seconds) || seconds <= 0f) return false;
            Vector3 degrees = new Vector3(sample.X / (seconds * 15f),
                sample.Y / (seconds * 4f), sample.Z / (seconds * 20f));
            if (!MotionMath.Finite(degrees)) return false;
            float radians = (float)Math.PI / 180f;
            delta = Quaternion.CreateFromYawPitchRoll(degrees.Y * radians, degrees.X * radians, degrees.Z * radians);
            return new LocalFramePose(Vector3.Zero, delta).Valid;
        }
    }
}
