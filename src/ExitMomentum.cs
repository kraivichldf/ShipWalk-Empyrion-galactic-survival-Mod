using System;
using System.Numerics;

namespace ShipWalk
{
    // A one-time transfer, never a stored velocity imposed on later physics steps.
    internal sealed class ExitMomentum
    {
        public const float Lifetime = 0.25f;
        public readonly Vector3 Linear, Angular;
        public readonly float CapturedAt;
        public bool Finished { get; private set; }

        public ExitMomentum(Vector3 linear, Vector3 angular, float now)
        {
            if (!Valid(linear, angular) || !MotionMath.Finite(now)) throw new ArgumentOutOfRangeException();
            Linear = linear; Angular = angular; CapturedAt = now;
        }

        public static bool Valid(Vector3 linear, Vector3 angular) => MotionMath.Finite(linear)
            && MotionMath.Finite(angular) && linear.LengthSquared() <= 300f * 300f
            && angular.LengthSquared() <= 20f * 20f;

        public bool TryTake(float now, bool authoritative, bool ready, bool cancelled,
            out Vector3 linear, out Vector3 angular)
        {
            linear = angular = Vector3.Zero;
            if (Finished) return false;
            if (cancelled || !authoritative || !MotionMath.Finite(now)
                || now < CapturedAt || now - CapturedAt > Lifetime)
            { Finished = true; return false; }
            if (!ready) return false;
            Finished = true;
            linear = Linear; angular = Angular;
            return true;
        }
    }

    // Samples the game's existing remote pose stream. Positions are absolute world
    // positions, so a floating-origin shift is not interpreted as ship movement.
    internal sealed class RemoteShipMotion
    {
        private bool havePose, haveVelocity;
        private Vector3 previousPosition, linear, angular;
        private Quaternion previousRotation;
        private float previousTime, velocityTime;
        public float LastSeen { get; private set; }

        public void Observe(Vector3 position, Quaternion rotation, float now)
        {
            if (!MotionMath.Finite(position) || !MotionMath.Finite(now)
                || !MotionMath.Finite(rotation.LengthSquared()) || rotation.LengthSquared() < .99f
                || rotation.LengthSquared() > 1.01f)
            { havePose = haveVelocity = false; return; }
            LastSeen = now;
            if (havePose && now == previousTime) return; // Same simulation tick, including final exit packet.
            float dt = now - previousTime;
            haveVelocity = false;
            if (havePose && dt >= .01f && dt <= ExitMomentum.Lifetime
                && MotionMath.ContinuousPosition(previousPosition, position, dt))
            {
                var delta = Quaternion.Normalize(rotation * Quaternion.Inverse(previousRotation));
                if (delta.W < 0f) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
                float angle = 2f * (float)Math.Acos(Math.Max(-1f, Math.Min(1f, delta.W)));
                var axis = new Vector3(delta.X, delta.Y, delta.Z);
                angular = axis.LengthSquared() < 1e-10f ? Vector3.Zero : Vector3.Normalize(axis) * (angle / dt);
                linear = (position - previousPosition) / dt;
                haveVelocity = angle <= (float)Math.PI / 4f && ExitMomentum.Valid(linear, angular);
                velocityTime = now;
            }
            previousPosition = position; previousRotation = rotation; previousTime = now;
            havePose = true;
        }

        public bool TryGet(float now, Vector3 centerOffset, out Vector3 velocity, out Vector3 rotationSpeed)
        {
            velocity = linear + Vector3.Cross(angular, centerOffset);
            rotationSpeed = angular;
            return haveVelocity && MotionMath.Finite(now) && now >= velocityTime
                && now - velocityTime <= ExitMomentum.Lifetime && ExitMomentum.Valid(velocity, rotationSpeed);
        }

        public bool TryCapture(float now, Vector3 localCenterOffset, out RemoteBodyHandoff handoff)
        {
            handoff = null;
            // Derive COM offset from shape-local data and the observed rotation.
            // body.worldCenterOfMass - hull.position includes the stale parked
            // root's translation, which produces a false angular contribution.
            if (!TryGet(now, Vector3.Transform(localCenterOffset, previousRotation), out var velocity, out var spin))
                return false;
            handoff = new RemoteBodyHandoff(previousPosition, previousRotation, velocity, spin, velocityTime);
            return true;
        }
    }
}
