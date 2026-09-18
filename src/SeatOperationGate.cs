using System;
using System.Numerics;

namespace ShipWalk
{
    // A native seat callback is a transaction, including derived-class work.
    // Never acquire character physics from inside that transaction's finalizer.
    internal sealed class SeatOperationGate
    {
        private int depth;
        private float completedFixed = float.NegativeInfinity;
        private float completedAt = float.NegativeInfinity;
        public bool Busy => depth != 0;
        public void Begin() { depth++; }
        public void Complete(float fixedTime, float now)
        {
            if (depth <= 0) throw new InvalidOperationException("Unbalanced native seat operation.");
            if (--depth != 0) return;
            completedFixed = fixedTime; completedAt = now;
        }
        public bool CanActivate(float fixedTime) => !Busy && MotionMath.Finite(fixedTime) && fixedTime > completedFixed;
        public bool Settling(float now) => !Busy && MotionMath.Finite(now) && now >= completedAt && now - completedAt <= ExitMomentum.Lifetime;
        public Vector3 RelativeVelocity(LocalFramePose frame, Vector3 worldVelocity, Vector3 transport,
            Vector3 acceptedLocalVelocity, float now, bool enteredFromSeat)
            => enteredFromSeat && Settling(now) ? acceptedLocalVelocity : frame.ToLocalVelocity(worldVelocity, transport);
        public void Reset() { depth = 0; completedFixed = completedAt = float.NegativeInfinity; }
    }
}
