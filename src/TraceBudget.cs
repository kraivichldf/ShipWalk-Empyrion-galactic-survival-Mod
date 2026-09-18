using System.Collections.Generic;

namespace ShipWalk
{
    internal sealed class TraceBudget
    {
        internal const int MaxBytes = 8 * 1024 * 1024;
        internal const int RoutineBytes = 2 * 1024 * 1024;
        private readonly Dictionary<string, float> last = new Dictionary<string, float>();
        internal int Used { get; private set; }
        internal int RoutineUsed { get; private set; }
        internal bool Sample(string kind, float now)
        {
            if (!MotionMath.Finite(now)) return false;
            if (last.TryGetValue(kind, out float previous) && now >= previous && now - previous < .2f) return false;
            last[kind] = now; return true;
        }
        internal bool Take(int bytes, bool critical)
        {
            if (bytes < 0 || bytes > MaxBytes - Used || !critical && bytes > RoutineBytes - RoutineUsed) return false;
            Used += bytes;
            if (!critical) RoutineUsed += bytes;
            return true;
        }
    }
}
