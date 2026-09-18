using System;
using System.Numerics;

namespace ShipWalk
{
    internal static class LocalClimb
    {
        // Elevator and ladder blocks both set MenuOptions.ExitProject(). The
        // native controller uses direct, gravity-free velocity in this mode.
        public static Vector3 Velocity(Quaternion heading, Vector3 input, float speed, bool sprinting)
        {
            if (!MotionMath.Finite(input) || !MotionMath.Finite(speed) || speed < 0f
                || !new LocalFramePose(Vector3.Zero, heading).Valid)
                throw new ArgumentException("Invalid local elevator input or speed.");
            Vector3 velocity = input * (sprinting ? speed : speed / 1.5f);
            velocity.X /= 1.5f;
            velocity.Z /= 1.5f;
            return Vector3.Transform(velocity, heading);
        }
    }
}
