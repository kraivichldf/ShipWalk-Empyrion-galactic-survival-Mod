using System;
using System.Numerics;

namespace ShipWalk
{
    // A seated character is at rest in the vessel frame, regardless of the stale
    // velocity stored in its disabled Rigidbody. Transfer world motion once.
    internal sealed class SeatExitInertia
    {
        private readonly ExitMomentum motion;
        private readonly Vector3 center;
        private bool cancelled, seeded;
        public bool Confirmed { get; private set; }
        public bool Applied { get; private set; }
        public Vector3 Inherited { get; private set; }
        public SeatExitInertia(Vector3 linear, Vector3 angular, Vector3 absoluteCenter, float now)
        {
            if (!MotionMath.Finite(absoluteCenter)) throw new ArgumentOutOfRangeException(nameof(absoluteCenter));
            motion = new ExitMomentum(linear, angular, now); center = absoluteCenter;
        }
        public void Confirm() { Confirmed = true; }
        public void Cancel() { cancelled = true; }
        public bool Fresh(float now) => !cancelled && WithinGrace(now, motion.CapturedAt);
        public static bool WithinGrace(float now, float started) => MotionMath.Finite(now) && MotionMath.Finite(started)
            && now >= started && now - started <= ExitMomentum.Lifetime;

        public bool TryApply(float now, Vector3 absoluteExitPoint, bool localPlayer, bool ready, bool abort, out Vector3 velocity)
        {
            velocity = Vector3.Zero;
            if (abort || !localPlayer || !Fresh(now)) { Cancel(); return false; }
            if (!Confirmed || Applied) return false;
            if (!motion.TryTake(now, localPlayer, ready, false, out Vector3 linear, out Vector3 angular)) return false;
            velocity = linear + Vector3.Cross(angular, absoluteExitPoint - center);
            if (!MotionMath.Finite(absoluteExitPoint) || !ExitMomentum.Valid(velocity, angular))
            { Cancel(); velocity = Vector3.Zero; return false; }
            Inherited = velocity; Applied = true; return true;
        }
        public bool TrySeed(float now, out Vector3 velocity)
        {
            velocity = Vector3.Zero;
            if (!Fresh(now) || !Confirmed || !Applied || seeded) return false;
            seeded = true; velocity = Inherited; return true;
        }
    }
}
