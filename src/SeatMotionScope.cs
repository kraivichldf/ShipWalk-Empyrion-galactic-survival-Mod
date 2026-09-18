using System;
using System.Collections.Generic;

namespace ShipWalk
{
    // A synchronous native seat transaction, never a saved velocity. A collision
    // or pilot input outside the exact patched reset is always authoritative.
    internal sealed class SeatMotionScope<T> where T : class
    {
        private readonly Dictionary<T, int> depths = new Dictionary<T, int>();
        private int generation;
        public bool Contains(T body) => body != null && depths.ContainsKey(body);
        public IDisposable Enter(T body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            depths.TryGetValue(body, out int depth);
            depths[body] = depth + 1;
            return new Lease(this, body, generation);
        }
        public void Clear() { depths.Clear(); generation++; }
        private sealed class Lease : IDisposable
        {
            private SeatMotionScope<T> owner;
            private readonly T body;
            private readonly int generation;
            public Lease(SeatMotionScope<T> owner, T body, int generation)
            { this.owner = owner; this.body = body; this.generation = generation; }
            public void Dispose()
            {
                SeatMotionScope<T> current = owner;
                owner = null;
                if (current == null || current.generation != generation) return;
                int remaining = current.depths[body] - 1;
                if (remaining == 0) current.depths.Remove(body);
                else current.depths[body] = remaining;
            }
        }
    }
}
