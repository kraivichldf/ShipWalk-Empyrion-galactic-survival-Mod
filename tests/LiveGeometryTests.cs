using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShipWalk;

internal static class LiveGeometryTests
{
    private sealed class Source { public int Mesh, Pose; public bool Active = true, Eligible = true; }
    private sealed class Copy { public int Mesh, Pose, Updates; public bool Active; }
    private static void Update(Source source, Copy copy)
    { copy.Mesh = source.Mesh; copy.Pose = source.Pose; copy.Active = source.Active; copy.Updates++; }

    internal static void MovingPreparation()
    {
        var sources = Enumerable.Range(0, 7417).Select(i => new Source { Mesh = i + 1 }).ToArray();
        var index = new LiveGeometryIndex<Source, Copy>();
        int frame = 0, created = 0;
        using (var work = new IncrementalWork(index.Reconcile(sources, s => s.Eligible,
            _ => { created++; return new Copy(); }, Update, (_, __) => { throw new Exception("Lost a live surface."); }).GetEnumerator(), () => 0))
        {
            // Reproduce the class of failure at the logged transform index.
            // A door keeps moving throughout copying; Ready must still be finite.
            while (!work.Completed && frame < 1000)
            {
                sources[1837].Pose++; sources[1837].Active = frame % 2 == 0;
                work.Advance(); frame++;
            }
            Check(work.Completed && frame > 1 && index.Count == 7417 && created == 7417, "Changing geometry blocked or restarted preparation.");
        }
        Drain(index.Reconcile(sources, s => s.Eligible, _ => new Copy(), Update, (_, __) => { }));
        Copy door = index.Entries.Single(e => e.Key == sources[1837]).Value;
        Check(door.Pose == sources[1837].Pose && door.Active == sources[1837].Active, "Changed door was frozen in the copy.");
        Check(index.Entries.All(e => e.Value.Updates == 2), "Existing colliders were duplicated or missed on refresh.");
    }

    internal static void AddRemoveAndEmptyMeshes()
    {
        var wall = new Source { Mesh = 1 }; var empty = new Source(); var door = new Source { Mesh = 2, Active = false };
        var index = new LiveGeometryIndex<Source, Copy>(); int removed = 0, created = 0;
        Func<Source, Copy> create = _ => { created++; return new Copy(); };
        Action<Source, Copy> remove = (_, __) => removed++;
        Drain(index.Reconcile(new[] { wall, empty, door, wall }, s => s.Eligible, create, Update, remove));
        Copy existing = index.Entries.Single(e => e.Key == empty).Value;
        empty.Mesh = 3; door.Active = true; wall.Eligible = false;
        var added = new Source { Mesh = 4 };
        Drain(index.Reconcile(new[] { wall, empty, door, added }, s => s.Eligible, create, Update, remove));
        Check(index.Count == 3 && removed == 1 && created == 4, "Membership reconciliation lost ownership or duplicated a source.");
        Check(ReferenceEquals(existing, index.Entries.Single(e => e.Key == empty).Value) && existing.Mesh == 3, "A formerly empty mesh did not update in place.");
        Check(index.Entries.Single(e => e.Key == door).Value.Active, "Inactive door did not activate.");
        empty.Mesh = 0;
        Drain(index.Reconcile(new[] { empty }, s => s.Eligible, create, Update, remove));
        Check(index.Count == 1 && existing.Mesh == 0 && removed == 3, "Removed/replaced geometry left stale records.");
    }

    internal static void CancelledRefresh()
    {
        var sources = Enumerable.Range(0, 1000).Select(_ => new Source { Mesh = 1 }).ToArray();
        var index = new LiveGeometryIndex<Source, Copy>(); int removed = 0;
        Drain(index.Reconcile(sources, s => s.Eligible, _ => new Copy(), Update, (_, __) => removed++));
        using (var work = new IncrementalWork(index.Reconcile(sources, s => s.Eligible,
            _ => new Copy(), Update, (_, __) => removed++).GetEnumerator(), () => 0)) work.Advance();
        Check(index.Count == 1000 && removed == 0, "Cancelling a partial scan deleted unseen live surfaces.");
        Drain(index.Reconcile(Array.Empty<Source>(), s => s.Eligible, _ => new Copy(), Update, (_, __) => removed++));
        Check(index.Count == 0 && removed == 1000, "Final removal did not release each record exactly once.");
    }

    internal static void ShipDimensions()
    {
        foreach (float cell in new[] { 2f, .5f })
        {
            var volume = LocalVolume.FromGrid(new Vector3(-10, -2, -40), new Vector3(10, 6, 50), cell);
            Near(volume.Size, new Vector3(20, 8, 90) * cell);
            Check(volume.Contains(volume.Min) && volume.Contains(volume.Max), "Bounding faces were excluded.");
            Check(!volume.Contains(volume.Max + Vector3.UnitX * 3f, 2f), "Character outside the length/width envelope stayed aboard.");
            Check(volume.Contains(volume.Max + Vector3.UnitY * 1.9f, 2f), "Deck jump margin disappeared.");
            foreach (Quaternion rotation in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(.7f, 1.2f, -.4f) })
            {
                var pose = new LocalFramePose(new Vector3(16000, -2200, 32000), rotation);
                Vector3 local = (volume.Min + volume.Max) * .5f;
                Check(volume.Contains(pose.ToLocalPoint(pose.ToWorldPoint(local))), "World rotation/origin changed local occupancy.");
            }
        }
        Check(!LocalVolume.FromGrid(Vector3.Zero, Vector3.One, float.NaN).Valid, "Invalid scale was accepted.");
        Check(!new LocalVolume(Vector3.One, Vector3.Zero).Contains(Vector3.Zero), "Inverted bounds were accepted.");
    }

    internal static void TransformedSurfaceBounds()
    {
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(.5f, .9f, -.3f);
        Vector3 x = Vector3.Transform(Vector3.UnitX * 2f, rotation);
        Vector3 y = Vector3.Transform(Vector3.UnitY * .5f, rotation);
        Vector3 z = Vector3.Transform(Vector3.UnitZ * 3f, rotation);
        Vector3 center = new Vector3(2, -3, 4), extent = new Vector3(4, .1f, 2), origin = new Vector3(30, 4, -18);
        LocalVolume box = LocalVolume.TransformBox(center, extent, origin, x, y, z);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = center + new Vector3((i & 1) == 0 ? -extent.X : extent.X,
                (i & 2) == 0 ? -extent.Y : extent.Y, (i & 4) == 0 ? -extent.Z : extent.Z);
            Check(box.Contains(origin + x * corner.X + y * corner.Y + z * corner.Z, .0001f), "Rotated/scaled collision surface was culled.");
        }
        Vector3 boxCenter = (box.Min + box.Max) * .5f, boxExtent = box.Size * .5f;
        Check(LocalVolume.DistanceSquared(boxCenter, boxCenter, boxExtent) == 0f, "Interior was excluded from nearby refresh.");
        Check(Math.Abs(LocalVolume.DistanceSquared(box.Max + Vector3.UnitX * 3f, boxCenter, boxExtent) - 9f) < .001f,
            "Nearby refresh distance is incorrect.");
    }
    private static void Drain(IEnumerable<bool> steps)
    { using (var work = new IncrementalWork(steps.GetEnumerator(), () => 0)) while (!work.Completed) work.Advance(); }
    private static void Near(Vector3 a, Vector3 b) { Check(Vector3.Distance(a, b) < .001f, "Incorrect dimensions."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
