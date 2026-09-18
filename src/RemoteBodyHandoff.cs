using System.Numerics;

namespace ShipWalk
{
    // The parked physics root is not a motion sample. Carry the pose and COM
    // velocity from the same received packet pair across native seat release.
    internal sealed class RemoteBodyHandoff
    {
        public readonly LocalFramePose AbsolutePose;
        public readonly Vector3 Linear, Angular;
        public readonly float SampledAt;
        public bool Finished { get; private set; }

        public RemoteBodyHandoff(Vector3 position, Quaternion rotation, Vector3 linear,
            Vector3 angular, float sampledAt)
        {
            AbsolutePose = new LocalFramePose(position, rotation);
            Linear = linear; Angular = angular; SampledAt = sampledAt;
        }

        public bool TryTake(float now, Vector3 origin, bool authoritative, bool ready, bool cancelled,
            out LocalFramePose scenePose)
        {
            scenePose = default;
            if (Finished) return false;
            if (cancelled || !authoritative || !AbsolutePose.Valid || !ExitMomentum.Valid(Linear, Angular)
                || !MotionMath.Finite(SampledAt) || !MotionMath.Finite(now) || now < SampledAt
                || now - SampledAt > ExitMomentum.Lifetime || !MotionMath.Finite(origin))
            { Finished = true; return false; }
            if (!ready) return false;
            Finished = true;
            scenePose = new LocalFramePose(AbsolutePose.Position - origin, AbsolutePose.Rotation);
            return true;
        }
    }
}
