using System;
using System.Numerics;

namespace ShipWalk
{
    // Only relative motion is smoothed/measured here. Vessel translation is
    // applied once, using the same live pose that draws the ship's interior.
    internal sealed class LocalPresentation
    {
        private Vector3 previous, current, animationSample;

        public void Reset(Vector3 position)
        { previous = current = animationSample = position; }

        public void Advance(Vector3 position)
        { previous = current; current = position; }

        public void Reframe(LocalFramePose from, LocalFramePose to)
        {
            previous = to.ToLocalPoint(from.ToWorldPoint(previous));
            current = to.ToLocalPoint(from.ToWorldPoint(current));
            animationSample = to.ToLocalPoint(from.ToWorldPoint(animationSample));
        }

        public Vector3 Sample(float fraction)
        {
            if (!MotionMath.Finite(fraction)) fraction = 1f;
            return Vector3.Lerp(previous, current, Math.Max(0f, Math.Min(1f, fraction)));
        }

        public Vector3 WorldPoint(LocalFramePose renderedShip, float fraction)
            => renderedShip.ToWorldPoint(Sample(fraction));

        // The native consumer expects displacement per sample, not m/s.
        // Read once before its footstep and animation consumers share the value.
        public Vector3 TakeLocomotion(Quaternion renderedShipRotation)
        {
            Vector3 delta = current - animationSample;
            animationSample = current;
            return Vector3.Transform(delta, renderedShipRotation);
        }
    }
}
