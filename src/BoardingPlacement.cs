using System;
using System.Numerics;

namespace ShipWalk
{
    // A bounded transaction, independent of Unity callbacks and frame rate.
    internal sealed class BoardingPlacement
    {
        internal const float Lifetime = 2f, RetryInterval = .1f, NativeExitRange = 3f, GroupPadding = 2f;
        internal const int Candidates = 50;
        public bool Pending { get; private set; }
        public bool FromSeat { get; private set; }
        public Vector3 Anchor { get; private set; }
        public Vector3 Preferred { get; private set; }
        public Quaternion Rotation { get; private set; }
        private float expires, nextRound;
        private int candidate;
        private float lastStep;

        public void Begin(Vector3 anchor, Vector3? nativeExit, Quaternion rotation, bool fromSeat, float now)
        {
            if (Pending) return; // a duplicate native acknowledgement cannot extend the hold
            if (!new LocalFramePose(anchor, rotation).Valid || !MotionMath.Finite(now))
                throw new ArgumentException("Invalid boarding placement.");
            Anchor = anchor; Preferred = nativeExit.HasValue && AcceptNative(anchor, nativeExit.Value) ? nativeExit.Value : anchor;
            Rotation = rotation; FromSeat = fromSeat; Pending = true;
            expires = now + Lifetime; nextRound = now; candidate = 0; lastStep = float.NegativeInfinity;
        }
        public bool TryStep(float fixedTime)
        {
            if (!Pending || !MotionMath.Finite(fixedTime) || fixedTime <= lastStep) return false;
            lastStep = fixedTime; return true;
        }
        internal static bool AcceptNative(Vector3 anchor, Vector3 nativeExit)
            => MotionMath.Finite(anchor) && MotionMath.Finite(nativeExit)
                && Vector3.DistanceSquared(anchor, nativeExit) <= NativeExitRange * NativeExitRange;
        public bool Expired(float now) => Pending && (!MotionMath.Finite(now) || now >= expires);
        public bool TryCandidate(float now, out Vector3 point, out bool needsSupport)
        {
            point = Preferred; needsSupport = false;
            if (!Pending || Expired(now) || now < nextRound) return false;
            int index = candidate++;
            if (index == 1) point = Anchor;
            else if (index >= 2)
            {
                int offset = index - 2;
                float radius = (offset / 16 + 1) * .25f, height = (offset / 8 % 2) * .25f;
                float angle = offset % 8 * (float)Math.PI / 4;
                point += new Vector3((float)Math.Cos(angle) * radius, height, (float)Math.Sin(angle) * radius);
                needsSupport = true;
            }
            if (candidate == Candidates) { candidate = 0; nextRound = now + RetryInterval; }
            return true;
        }
        public void TrackWalkingPoint(Vector3 point)
        {
            if (!Pending || FromSeat || !MotionMath.Finite(point) || Vector3.DistanceSquared(point, Preferred) < .04f) return;
            Anchor = Preferred = point; candidate = 0; // keep the original deadline
        }
        public void Reframe(LocalFramePose before, LocalFramePose after)
        {
            if (!Pending) return;
            Anchor = after.ToLocalPoint(before.ToWorldPoint(Anchor));
            Preferred = after.ToLocalPoint(before.ToWorldPoint(Preferred));
            Rotation = Quaternion.Normalize(Quaternion.Inverse(after.Rotation) * before.Rotation * Rotation);
            candidate = 0; // geometry changed; time budget does not restart
        }
        public void Reset() { Pending = FromSeat = false; candidate = 0; }
    }

    internal static class CapsuleClearance
    {
        internal const int Passes = 8;
        internal const float MaxCorrection = .75f;
        public static bool Resolve(Vector3 start, Func<Vector3, Vector3> correction, out Vector3 point)
        {
            point = start;
            if (!MotionMath.Finite(start)) return false;
            for (int pass = 0; pass <= Passes; pass++)
            {
                // Every query receives the actual proposed pose. Rigidbody writes
                // need not have propagated to a Transform/broadphase cache yet.
                Vector3 shift = correction(point);
                if (!MotionMath.Finite(shift)) return false;
                if (shift == Vector3.Zero) return true;
                if (pass == Passes) return false;
                point += shift;
                if (Vector3.DistanceSquared(point, start) > MaxCorrection * MaxCorrection) return false;
            }
            return false;
        }
    }
}
