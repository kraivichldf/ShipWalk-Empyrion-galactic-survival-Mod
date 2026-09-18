using System;
using System.Collections.Generic;
using ShipWalk;

internal static class IncrementalWorkTests
{
    internal static void LargeSnapshots()
    {
        foreach (int count in new[] { 7417, 20000, 50000 })
        {
            int visited = 0, closed = 0;
            var seen = new bool[count];
            using (var work = new IncrementalWork(Items(count, i =>
            {
                Check(!seen[i], "Work was duplicated across frames."); seen[i] = true; visited++;
            }, () => closed++).GetEnumerator(), () => 0))
            {
                while (!work.Completed)
                {
                    int before = visited;
                    work.Advance();
                    Check(visited - before <= IncrementalWork.UnitsPerSlice, "One frame processed the whole ship.");
                }
                Check(visited == count && Array.TrueForAll(seen, x => x), "Large snapshot was truncated.");
                Check(work.Slices > 1 && closed == 1, "Snapshot was not split or cleaned up.");
                work.Advance();
                Check(visited == count && closed == 1, "Completed work restarted.");
            }
        }
    }

    internal static void TimeBudgetAndNativeOverrun()
    {
        foreach (double cost in new[] { 1.25, 12.0 })
        {
            double now = 0;
            int visited = 0;
            using (var work = new IncrementalWork(Items(1000, _ => { visited++; now += cost; }, () => { }).GetEnumerator(), () => now))
            {
                work.Advance();
                int expected = cost > IncrementalWork.MillisecondsPerSlice ? 1 : (int)Math.Ceiling(IncrementalWork.MillisecondsPerSlice / cost);
                Check(visited == expected, "Work continued after the time budget.");
                Check(work.MaxSliceMs == now && work.TotalMs == now, "Overrun was hidden from timing metrics.");
                Check(!work.Completed, "A partial build was published as complete.");
            }
        }
    }

    internal static void CancellationAndFailure()
    {
        int visited = 0, closed = 0;
        var cancelled = new IncrementalWork(Items(50000, _ => visited++, () => closed++).GetEnumerator(), () => 0);
        cancelled.Advance();
        int before = visited;
        cancelled.Dispose(); cancelled.Dispose(); cancelled.Advance();
        Check(visited == before && closed == 1 && !cancelled.Completed, "Cancellation resumed work or published Ready.");

        visited = closed = 0;
        var failed = new IncrementalWork(Items(50000, _ =>
        {
            visited++;
            if (visited == 37) throw new NotSupportedException("geometry changed");
        }, () => closed++).GetEnumerator(), () => 0);
        bool rejected = false;
        try { failed.Advance(); } catch (NotSupportedException) { rejected = true; }
        failed.Advance(); failed.Dispose();
        Check(rejected && visited == 37 && closed == 1 && !failed.Completed, "Failed geometry continued or became Ready.");
    }

    private static IEnumerable<bool> Items(int count, Action<int> visit, Action close)
    {
        try { for (int i = 0; i < count; i++) { visit(i); yield return true; } }
        finally { close(); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
