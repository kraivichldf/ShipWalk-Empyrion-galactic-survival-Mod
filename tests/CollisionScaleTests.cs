using System;
using System.Linq;
using ShipWalk;

internal static class CollisionScaleTests
{
    private sealed class Collider
    {
        public int Index;
        public bool Alive = true, Active = true;
    }

    public static void AboveOldLimit()
    {
        const int bodyCount = 513, hullCount = 513;
        var bodies = Enumerable.Range(0, bodyCount).Select(i => new Collider { Index = i }).ToArray();
        var hulls = Enumerable.Range(0, hullCount).Select(i => new Collider { Index = i }).ToArray();
        var ignored = new bool[bodyCount, hullCount];
        ignored[12, 45] = true; // Another owner's exclusion must survive our cleanup.
        long writes = 0;
        var lease = new CollisionPairs<Collider>(c => c.Alive, c => c.Active,
            (a, b) => ignored[a.Index, b.Index],
            (a, b, value) => { ignored[a.Index, b.Index] = value; writes++; });
        lease.Refresh(bodies, hulls);
        Check(lease.Count == (long)bodyCount * hullCount, "all pairs above the old cutoff retained");
        Check(ignored[0, 0] && ignored[512, 512], "coverage reaches the last pair");
        lease.Refresh(bodies.Reverse(), hulls.Reverse());
        lease.Reset();
        for (int a = 0; a < bodyCount; a++)
        for (int b = 0; b < hullCount; b++)
            Check(ignored[a, b] == (a == 12 && b == 45), "reorder/cleanup preserves original state");
        Check(writes > 262144 && lease.Count == 0, "large lease completed and cleared");
    }

    public static void CompactMillionPairState()
    {
        const int bodyCount = 1024, hullCount = 2048;
        var bodies = Enumerable.Range(0, bodyCount).Select(i => new Collider { Index = i }).ToArray();
        var hulls = Enumerable.Range(0, hullCount).Select(i => new Collider { Index = i }).ToArray();
        var ignored = new bool[bodyCount, hullCount];
        ignored[17, 24] = true;
        int activityQueries = 0;
        var lease = new CollisionPairs<Collider>(c => c.Alive, c => { activityQueries++; return c.Active; },
            (a, b) => ignored[a.Index, b.Index], (a, b, value) => ignored[a.Index, b.Index] = value);
        lease.Refresh(bodies, hulls);
        Check(activityQueries == bodyCount + hullCount, "native activity checks grow with colliders, not pair count");
        Check(lease.Count == 2097152 && ignored[1023, 2047], "all two million pairs covered");
        Check(lease.SnapshotBytes <= 300 * 1024, "state masks below 300 KiB rather than millions of hash entries");
        Check(lease.LastWrites == lease.Count - 1, "skip the existing native ignore");
        lease.Refresh(bodies, hulls);
        Check(lease.LastWrites == 0, "stable ignored pairs need no repeated native writes");
        // Keep part of each axis, including the preexisting ignore, then rebuild the rest.
        lease.Refresh(bodies.Take(512), hulls.Take(1024));
        Check(!ignored[1023, 2047] && ignored[20, 30] && ignored[17, 24], "removed pairs restored; retained pairs remain excluded");
        lease.Reset();
        Check(!ignored[20, 30] && ignored[17, 24] && lease.SnapshotBytes == 0, "original states survive partial rebuild and cleanup");
        Console.WriteLine("SCALE 2097152 pairs; activity queries per refresh=3072; mask payload <=300 KiB (native solver not run)");
    }

    public static void PartialFailureAndInactivePairs()
    {
        var bodies = new[] { new Collider { Index = 0 }, new Collider { Index = 1 } };
        var hulls = new[] { new Collider { Index = 0 }, new Collider { Index = 1 } };
        var ignored = new bool[2, 2];
        ignored[0, 0] = true;
        bool fail = true;
        var lease = new CollisionPairs<Collider>(c => c.Alive, c => c.Active,
            (a, b) => ignored[a.Index, b.Index], (a, b, value) =>
            {
                ignored[a.Index, b.Index] = value;
                if (fail && value && a.Index == 1 && b.Index == 0) { fail = false; throw new InvalidOperationException("injected native write failure"); }
            });
        bool threw = false;
        try { lease.Refresh(bodies, hulls); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "fixture interrupts a partially applied refresh");
        lease.Reset();
        Check(ignored[0, 0] && !ignored[0, 1] && !ignored[1, 0] && !ignored[1, 1], "cleanup restores partial writes, including the failing write");
        hulls[1].Active = false;
        lease.Refresh(bodies, hulls);
        ignored[1, 1] = true; // External owner changes a pair we have never applied.
        hulls[1].Active = true;
        lease.Refresh(bodies, hulls);
        lease.Reset();
        Check(ignored[1, 1], "capture original state on first actual activation, not on enumeration");
        lease.Refresh(bodies.Concat(bodies), hulls.Concat(hulls));
        Check(lease.Count == 4, "duplicate components do not duplicate pair ownership");
        bodies[0].Alive = false;
        lease.Refresh(bodies, hulls); lease.Reset();
        Check(ignored[1, 1], "destroyed collider cleanup preserves surviving external ignores");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
