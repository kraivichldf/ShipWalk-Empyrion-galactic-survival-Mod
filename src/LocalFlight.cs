using System;
using System.Numerics;

namespace ShipWalk
{
    // Flight control is relative to the cabin, so a jetpack toggle cannot
    // discard vessel velocity or switch the ship's collider representation.
    internal static class LocalFlight
    {
        public const float Speed = 6f, Acceleration = 12f;
        public static Vector3 Step(Vector3 relative, Quaternion heading, Vector3 input, float seconds)
        {
            if (!MotionMath.Finite(relative) || !MotionMath.Finite(input)
                || !new LocalFramePose(Vector3.Zero, heading).Valid
                || !MotionMath.Finite(seconds) || seconds <= 0f || seconds > .1f)
                throw new ArgumentException("Invalid local jetpack input or timestep.");
            float length = input.Length();
            if (length > 1f) input /= length;
            Vector3 desired = Vector3.Transform(input * Speed, heading);
            Vector3 delta = desired - relative;
            float distance = delta.Length(), step = Acceleration * seconds;
            return distance <= step ? desired : relative + delta * (step / distance);
        }
    }
}
