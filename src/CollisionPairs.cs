using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ShipWalk
{
    // Preserve every own-hull pair without allocating a tuple/hash entry per pair.
    // Native collision exclusion still requires one operation per active pair.
    internal sealed class CollisionPairs<T> where T : class
    {
        private sealed class Grid
        {
            public readonly T[] Left, Right;
            public readonly Dictionary<T, int> LeftIndex, RightIndex;
            public readonly BitArray[] Known, Original;
            public Grid(T[] left, T[] right)
            {
                Left = left; Right = right;
                LeftIndex = Index(left); RightIndex = Index(right);
                Known = new BitArray[left.Length]; Original = new BitArray[left.Length];
            }
            private static Dictionary<T, int> Index(T[] values)
            {
                var result = new Dictionary<T, int>();
                for (int i = 0; i < values.Length; i++) result.Add(values[i], i);
                return result;
            }
            public bool Matches(T[] left, T[] right) => left.Length == Left.Length && right.Length == Right.Length
                && left.All(LeftIndex.ContainsKey) && right.All(RightIndex.ContainsKey);
            public bool Captured(int a, int b) => Known[a] != null && Known[a][b];
            public bool WasIgnored(int a, int b) => Original[a] != null && Original[a][b];
            public void Remember(int a, int b, bool ignored)
            {
                if (Known[a] == null) Known[a] = new BitArray(Right.Length);
                Known[a][b] = true;
                if (ignored)
                {
                    if (Original[a] == null) Original[a] = new BitArray(Right.Length);
                }
                if (Original[a] != null) Original[a][b] = ignored;
            }
            public void CopyRetained(Grid old)
            {
                var columns = Right.Select(c => old.RightIndex.TryGetValue(c, out int index) ? index : -1).ToArray();
                for (int a = 0; a < Left.Length; a++)
                {
                    if (!old.LeftIndex.TryGetValue(Left[a], out int oldRow) || old.Known[oldRow] == null) continue;
                    for (int b = 0; b < Right.Length; b++)
                        if (columns[b] >= 0 && old.Captured(oldRow, columns[b]))
                            Remember(a, b, old.WasIgnored(oldRow, columns[b]));
                }
            }
        }

        private Grid grid;
        private readonly Func<T, bool> alive, active;
        private readonly Func<T, T, bool> ignored;
        private readonly Action<T, T, bool> setIgnored;
        public long Count => grid == null ? 0 : (long)grid.Left.Length * grid.Right.Length
            - grid.Left.Count(grid.RightIndex.ContainsKey);
        // Payload only, for diagnostics/tests; collider arrays/indexes add O(n+m) overhead.
        public long SnapshotBytes => grid == null ? 0 : ((grid.Right.Length + 31L) / 32L) * 4L
            * (grid.Known.Count(row => row != null) + grid.Original.Count(row => row != null));
        public long LastWrites { get; private set; }
        public CollisionPairs(Func<T, bool> alive, Func<T, bool> active, Func<T, T, bool> ignored, Action<T, T, bool> setIgnored)
        { this.alive = alive; this.active = active; this.ignored = ignored; this.setIgnored = setIgnored; }

        public bool? OriginalIgnore(T first, T second)
        {
            if (grid == null || !grid.LeftIndex.TryGetValue(first, out int a)
                || !grid.RightIndex.TryGetValue(second, out int b) || !grid.Captured(a, b)) return null;
            return grid.WasIgnored(a, b);
        }

        public void Refresh(IEnumerable<T> first, IEnumerable<T> second)
        {
            LastWrites = 0;
            var left = first.Where(alive).Distinct().ToArray();
            var right = second.Where(alive).Distinct().ToArray();
            if (grid == null || !grid.Matches(left, right))
            {
                var replacement = new Grid(left, right);
                if (grid != null)
                {
                    replacement.CopyRetained(grid);
                    RestoreRemoved(grid, replacement);
                }
                grid = replacement;
            }
            // Unity activity checks are O(n+m), not two native queries per pair.
            var leftActive = grid.Left.Select(active).ToArray();
            var rightActive = grid.Right.Select(active).ToArray();
            for (int a = 0; a < grid.Left.Length; a++)
            {
                if (!leftActive[a]) continue;
                for (int b = 0; b < grid.Right.Length; b++)
                {
                    if (!rightActive[b] || ReferenceEquals(grid.Left[a], grid.Right[b])) continue;
                    bool current = ignored(grid.Left[a], grid.Right[b]);
                    if (!grid.Captured(a, b)) grid.Remember(a, b, current);
                    // Record ownership before the native write, so failure cleanup
                    // can restore even a partially completed large refresh.
                    if (!current) { setIgnored(grid.Left[a], grid.Right[b], true); LastWrites++; }
                }
            }
        }

        private void RestoreRemoved(Grid old, Grid replacement)
        {
            var retainedRight = replacement == null ? new bool[old.Right.Length]
                : old.Right.Select(replacement.RightIndex.ContainsKey).ToArray();
            var liveRight = old.Right.Select(alive).ToArray();
            for (int a = 0; a < old.Left.Length; a++)
            {
                if (old.Known[a] == null) continue;
                bool retainedLeft = replacement != null && replacement.LeftIndex.ContainsKey(old.Left[a]);
                bool liveLeft = alive(old.Left[a]);
                for (int b = 0; b < old.Right.Length; b++)
                {
                    if (retainedLeft && retainedRight[b] || !old.Captured(a, b)) continue;
                    if (!old.WasIgnored(a, b) && liveLeft && liveRight[b]) setIgnored(old.Left[a], old.Right[b], false);
                    old.Known[a][b] = false;
                }
            }
        }
        public void Reset()
        {
            if (grid == null) return;
            RestoreRemoved(grid, null);
            grid = null;
        }
    }
}
