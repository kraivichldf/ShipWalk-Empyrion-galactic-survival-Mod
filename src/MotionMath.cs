using System;
using System.Numerics;

namespace ShipWalk
{
    // This kernel has no dependency on Unity and is exercised by the executable tests.
    internal static class MotionMath
    {
        public static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static Vector3 RelativeVelocity(Vector3 world, Vector3 previousSupport, Vector3 currentSupport, bool continuing)
        {
            // Transfer support acceleration once per fixed tick, not again at the late limiter.
            return world + (continuing ? currentSupport - previousSupport : Vector3.Zero) - currentSupport;
        }

        public static Vector3 WorldVelocity(Vector3 relative, Vector3 support) => relative + support;

        public static Vector3 RelativeDragAcceleration(Vector3 world, Vector3 frame, float drag, float seconds)
        {
            if (!Finite(world) || !Finite(frame) || !Finite(drag) || drag < 0f
                || !Finite(seconds) || seconds <= 0f || seconds > 0.25f)
                throw new ArgumentOutOfRangeException("Invalid relative-drag input.");
            // Apply damping before collision solving, about the departure frame.
            // Capping the coefficient prevents damping from reversing relative motion.
            return -(world - frame) * Math.Min(drag, 1f / seconds);
        }

        public static bool ContinuousPosition(Vector3 previous, Vector3 current, float seconds)
        {
            if (!Finite(previous) || !Finite(current) || !Finite(seconds) || seconds <= 0f || seconds > 0.25f) return false;
            float distance = 300f * seconds + 0.25f;
            return Vector3.DistanceSquared(previous, current) <= distance * distance;
        }

        public static bool TryPoseVelocity(Vector3 previousPosition, Quaternion previousRotation,
            Vector3 position, Quaternion rotation, Vector3 point, float seconds, out Vector3 velocity)
        {
            velocity = Vector3.Zero;
            if (seconds < 0.0001f || seconds > 0.25f || !Finite(seconds)) return false;
            Vector3 anchor = Vector3.Transform(point - position, Quaternion.Inverse(rotation));
            Vector3 previousPoint = previousPosition + Vector3.Transform(anchor, previousRotation);
            velocity = (point - previousPoint) / seconds;
            return Finite(velocity) && velocity.LengthSquared() <= 300f * 300f;
        }

        public static bool Continuous(Vector3 previousSupport, Vector3 support, float seconds)
        {
            return seconds > 0f && seconds <= 0.25f && Finite(support)
                && support.LengthSquared() <= 300f * 300f
                && (support - previousSupport).LengthSquared() <= 50f * 50f;
        }
    }
}
