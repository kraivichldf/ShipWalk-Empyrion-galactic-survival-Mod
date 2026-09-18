using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipWalk
{
    // Recovery and travel run on different native ticks. One bounded attempt
    // owns this boundary encounter; a failure cannot renew its own protection.
    internal sealed class VesselBoundaryGate
    {
        internal const float Lifetime = 15f;
        private sealed class Entry { internal object Context; internal float Deadline; internal bool Blocked; }
        private readonly Dictionary<object, Entry> entries = new Dictionary<object, Entry>();
        public bool AllowsStart(object ship, object context, float now)
            => !entries.TryGetValue(ship, out Entry entry) || !ReferenceEquals(entry.Context, context)
                || !entry.Blocked && now < entry.Deadline;
        public void Observe(object ship, int result)
        {
            if (result == 0) entries.Remove(ship);
        }
        public void Block(object ship, object context)
        {
            if (!entries.TryGetValue(ship, out Entry entry))
            {
                if (entries.Count >= 64) return;
                entries.Add(ship, entry = new Entry());
            }
            entry.Context = context; entry.Blocked = true;
        }
        public bool Defer(object ship, object context, int result, float now, Func<bool> prepareOrContinue)
        {
            if (ship == null || context == null || (result != 2 && result != 5) || !MotionMath.Finite(now)) return false;
            if (!entries.TryGetValue(ship, out Entry entry) || !ReferenceEquals(entry.Context, context))
            {
                if (entry == null && entries.Count >= 64) return false;
                entries[ship] = entry = new Entry { Context = context, Deadline = now + Lifetime };
            }
            if (entry.Blocked || now >= entry.Deadline) { entry.Blocked = true; return false; }
            // Also latch a thrown eligibility/transport error. Native recovery
            // resumes, and another tick must not start an endless retry loop.
            entry.Blocked = true;
            if (!prepareOrContinue()) return false;
            entry.Blocked = false;
            return true;
        }
        public void Prune(Func<object, object, bool> exists)
        {
            foreach (object ship in entries.Where(p => !exists(p.Key, p.Value.Context)).Select(p => p.Key).ToArray())
                entries.Remove(ship);
        }
    }
}
