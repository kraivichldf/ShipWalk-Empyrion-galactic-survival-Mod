using System;
using System.Numerics;

namespace ShipWalk
{
    internal enum PassengerPhase { Detached, Grounded, Airborne }

    internal sealed class DragOverride
    {
        internal bool Active { get; private set; }
        private float saved;

        internal float Begin(float original)
        {
            if (Active) throw new InvalidOperationException("Previous drag override was not restored.");
            saved = original;
            Active = true;
            return 0f;
        }

        internal float Restore(float current)
        {
            if (!Active) return current;
            Active = false;
            // A later explicit change from another component takes precedence.
            return current == 0f ? saved : current;
        }
    }

    // The frame describes velocity accounting, never a transform parent or a constraint.
    // Airborne velocity is the departure frame; later ship acceleration is not inherited.
    internal sealed class MotionFrame
    {
        internal const float MaxAirborneSeconds = 6f;
        internal PassengerPhase Phase { get; private set; }
        internal Vector3 Velocity { get; private set; }
        private float departureTime;

        internal void BeginSeatExit(Vector3 support, float time)
        {
            if (!MotionMath.Finite(time)) throw new ArgumentOutOfRangeException(nameof(time));
            SetGrounded(support);
            LeaveGround(time);
        }

        internal Vector3 BeginGrounded(Vector3 world, Vector3 support)
        {
            Vector3 relative = MotionMath.RelativeVelocity(world, Velocity, support, Phase == PassengerPhase.Grounded);
            SetGrounded(support);
            return relative;
        }

        internal bool LeaveGround(float time)
        {
            if (!MotionMath.Finite(time) || Phase == PassengerPhase.Detached) return false;
            if (Phase == PassengerPhase.Grounded)
            {
                departureTime = time;
                Phase = PassengerPhase.Airborne;
            }
            return true;
        }

        internal bool CanRemainAirborne(float time, bool sameShipBelow)
            => Phase == PassengerPhase.Airborne && sameShipBelow && MotionMath.Finite(time)
                && time >= departureTime && time - departureTime <= MaxAirborneSeconds;

        internal Vector3 Relative(Vector3 world) => world - Velocity;

        internal Vector3 LandingLimiter(Vector3 world, Vector3 support)
        {
            // A collision has already run. Rebase without adding support acceleration,
            // including when the ship changed speed while the passenger was in flight.
            SetGrounded(support);
            return world - support;
        }

        private void SetGrounded(Vector3 velocity)
        {
            if (!MotionMath.Finite(velocity)) throw new ArgumentException("Invalid support velocity.");
            Phase = PassengerPhase.Grounded;
            Velocity = velocity;
        }

        internal void Clear()
        {
            Phase = PassengerPhase.Detached;
            Velocity = Vector3.Zero;
            departureTime = 0f;
        }
    }
}
