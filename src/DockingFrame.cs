using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShipWalk
{
    internal static class DockingTopology
    {
        // Cycles, removed entities and unsupported roots are not a docking group.
        internal static T Root<T>(T member, Func<T, T> parent, Func<T, bool> valid) where T : class
        {
            var seen = new HashSet<T>();
            for (T current = member; current != null && valid(current) && seen.Add(current);)
            {
                T next = parent(current);
                if (next == null) return current;
                current = next;
            }
            return null;
        }
        internal static bool Member<T>(T frame, T candidate, Func<T, T> parent, Func<T, bool> valid) where T : class
            => frame != null && ReferenceEquals(frame, Root(candidate, parent, valid));

        // An association survives jumping/climbing. Overlapping envelopes alone
        // never change it; a solid support or native seat identifies a new member.
        internal static int Occupant(int previous, int support, int seat, bool stillAboard, int frame)
            => seat > 0 ? seat : support > 0 ? support : stillAboard && previous > 0 ? previous : frame;
    }

    internal readonly struct DockingRebase
    {
        public readonly Vector3 Position, Velocity;
        public readonly Quaternion Rotation;
        public DockingRebase(Vector3 position, Vector3 velocity, Quaternion rotation)
        { Position = position; Velocity = velocity; Rotation = rotation; }
        internal static Vector3 PointVelocity(LocalFramePose frame, Vector3 linear, Vector3 angular, Vector3 worldPoint)
            => linear + Vector3.Cross(angular, worldPoint - frame.Position);
        internal static DockingRebase Change(LocalFramePose from, LocalFramePose to, Vector3 position,
            Quaternion rotation, Vector3 velocity, Vector3 fromLinear, Vector3 fromAngular,
            Vector3 toLinear, Vector3 toAngular)
        {
            Vector3 world = from.ToWorldPoint(position);
            Vector3 worldVelocity = Vector3.Transform(velocity, from.Rotation)
                + PointVelocity(from, fromLinear, fromAngular, world);
            return new DockingRebase(to.ToLocalPoint(world),
                Vector3.Transform(worldVelocity - PointVelocity(to, toLinear, toAngular, world), Quaternion.Inverse(to.Rotation)),
                Quaternion.Normalize(Quaternion.Inverse(to.Rotation) * from.Rotation * rotation));
        }
    }

    internal sealed class DockingMotionWindow
    {
        private readonly Dictionary<int, double> until = new Dictionary<int, double>();
        internal void Accepted(int actor, FrameMessage previous, FrameMessage next, double now)
        {
            if (next.Mode == PassengerMode.World) { until.Remove(actor); return; }
            if (previous != null && previous.Ship > 0 && next.Ship > 0 && previous.Ship != next.Ship)
                until[actor] = now + 1;
        }
        internal double Distance(int actor, Vector3 acceptedVelocity, double seconds, double now)
        {
            double speed = 35;
            if (until.TryGetValue(actor, out double deadline))
            {
                if (now > deadline) until.Remove(actor);
                else speed = Math.Max(speed, Math.Min(300, acceptedVelocity.Length() + 10 * seconds));
            }
            return 3 + speed * seconds;
        }
        internal void Clear() => until.Clear();
    }
}
