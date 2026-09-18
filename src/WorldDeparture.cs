using System;
using System.Numerics;

namespace ShipWalk
{
    internal static class WorldDeparture
    {
        public const float Clearance = .04f;
        public static Vector3 BeforeContact(Vector3 from, Vector3 to, float distance)
        {
            Vector3 motion = to - from;
            float length = motion.Length();
            if (length < .00001f) return from;
            return from + motion / length * Math.Max(0f, Math.Min(length, distance - Clearance));
        }
        // A confirmed ship relocation is a new location, not a physical sweep
        // through every obstacle between the old and new playfield positions.
        public static Vector3 SweepStart(LocalFramePose before, LocalFramePose after, Vector3 local, float seconds)
        {
            bool relocated = Vector3.Distance(before.Position, after.Position) > 300f * Math.Max(.05f, seconds) + .25f;
            return (relocated ? after : before).ToWorldPoint(local);
        }
    }
}
