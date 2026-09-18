using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipWalk
{
    // A finite discovery pass updates existing records in place. Geometry need
    // not stop changing before preparation completes. Unity work is supplied by
    // the adapter; the same lifecycle is exercised by offline regression tests.
    internal sealed class LiveGeometryIndex<TKey, TValue>
    {
        private readonly Dictionary<TKey, TValue> entries = new Dictionary<TKey, TValue>();
        public IEnumerable<KeyValuePair<TKey, TValue>> Entries => entries;
        public int Count => entries.Count;

        public IEnumerable<bool> Reconcile(IEnumerable<TKey> sources, Func<TKey, bool> eligible,
            Func<TKey, TValue> create, Action<TKey, TValue> update, Action<TKey, TValue> remove)
        {
            var seen = new HashSet<TKey>();
            TKey[] previous = entries.Keys.ToArray();
            foreach (TKey source in sources)
            {
                if (eligible(source) && seen.Add(source))
                {
                    if (!entries.TryGetValue(source, out TValue value))
                    {
                        value = create(source);
                        entries.Add(source, value);
                    }
                    update(source, value);
                }
                yield return true;
            }
            // Snapshot keys because removal changes membership. Cancellation
            // before this phase never erases unvisited but still valid entries.
            foreach (TKey key in previous)
            {
                if (!seen.Contains(key))
                {
                    remove(key, entries[key]);
                    entries.Remove(key);
                }
                yield return true;
            }
        }
    }
}
