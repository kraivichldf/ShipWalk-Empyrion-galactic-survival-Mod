using System;
using System.Numerics;

namespace ShipWalk
{
    // One kinematic trajectory supplies both MovePosition/MoveRotation and the
    // controller's support velocity. Coordinates here are absolute (origin-safe).
    internal sealed class InteriorStep
    {
        public Vector3 Start, Target, Linear, Angular;
        public Quaternion StartRotation, TargetRotation;

        public static bool TryPlan(Vector3 start, Quaternion startRotation, Vector3 shipPosition,
            Quaternion shipRotation, Vector3 shipCenter, Vector3 velocity, Vector3 angular,
            float seconds, out InteriorStep step)
        {
            step = null;
            if (!MotionMath.Finite(start) || !MotionMath.Finite(shipPosition) || !MotionMath.Finite(shipCenter)
                || !ExitMomentum.Valid(velocity, angular) || !Valid(startRotation) || !Valid(shipRotation)
                || !MotionMath.Finite(seconds) || seconds < .0001f || seconds > .1f) return false;
            float speed = angular.Length();
            Quaternion turn = speed < .00001f ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(angular / speed, speed * seconds);
            Quaternion targetRotation = Quaternion.Normalize(turn * shipRotation);
            // Rigidbody.velocity is the COM velocity, including for an offset COM.
            Vector3 target = shipCenter + velocity * seconds
                + Vector3.Transform(shipPosition - shipCenter, turn);
            Quaternion delta = Quaternion.Normalize(targetRotation * Quaternion.Inverse(startRotation));
            if (delta.W < 0f) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
            float sin = new Vector3(delta.X, delta.Y, delta.Z).Length();
            float angle = 2f * (float)Math.Atan2(sin, delta.W);
            Vector3 spin = sin < .000001f ? Vector3.Zero
                : new Vector3(delta.X, delta.Y, delta.Z) * (angle / (sin * seconds));
            Vector3 translation = (target - start) / seconds;
            if (!MotionMath.Finite(translation) || translation.LengthSquared() > 300f * 300f
                || !MotionMath.Finite(spin) || angle > (float)Math.PI / 4f) return false;
            step = new InteriorStep { Start = start, StartRotation = startRotation,
                Target = target, TargetRotation = targetRotation, Linear = translation, Angular = spin };
            return true;
        }

        public Vector3 PointVelocity(Vector3 worldPoint) => PointVelocity(worldPoint, Start);
        public Vector3 PointVelocity(Vector3 worldPoint, Vector3 currentRoot)
            => Linear + Vector3.Cross(Angular, worldPoint - currentRoot);

        public static Vector3 MapPoint(Vector3 point, Vector3 source, Quaternion sourceRotation,
            Vector3 destination, Quaternion destinationRotation)
            => destination + Vector3.Transform(Vector3.Transform(point - source,
                Quaternion.Inverse(sourceRotation)), destinationRotation);

        private static bool Valid(Quaternion rotation)
            => MotionMath.Finite(rotation.X) && MotionMath.Finite(rotation.Y) && MotionMath.Finite(rotation.Z)
                && MotionMath.Finite(rotation.W) && Math.Abs(rotation.LengthSquared() - 1f) < .01f;
    }

    // Immutable snapshots are safe for Unity's contact modification worker threads.
    // Other mods' contacts are never changed; the proxy can affect one body only.
    internal sealed class InteriorContactFilter
    {
        public readonly int ProxyBody, PlayerBody;
        private readonly System.Collections.Generic.HashSet<ulong> allowed;
        public int Count => allowed.Count;
        public InteriorContactFilter(int proxy, int player, System.Collections.Generic.IEnumerable<ulong> pairs)
        {
            ProxyBody = proxy; PlayerBody = player;
            allowed = new System.Collections.Generic.HashSet<ulong>(pairs);
        }
        internal static ulong Pair(int proxyCollider, int playerCollider)
            => ((ulong)(uint)proxyCollider << 32) | (uint)playerCollider;
        public bool Allows(int proxyCollider, int playerCollider) => allowed.Contains(Pair(proxyCollider, playerCollider));
        public bool Reject(int firstBody, int secondBody, int firstCollider, int secondCollider)
        {
            if (firstBody == ProxyBody) return secondBody != PlayerBody || !Allows(firstCollider, secondCollider);
            if (secondBody == ProxyBody) return firstBody != PlayerBody || !Allows(secondCollider, firstCollider);
            return false;
        }
    }
}
