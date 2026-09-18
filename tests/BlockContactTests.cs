using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using ShipWalk;
using HarmonyLib;
using UVector = UnityEngine.Vector3;
using UQuaternion = UnityEngine.Quaternion;

internal static class BlockContactTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Set(object owner, string name, Type type, object value)
        => owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(f => f.Name == name && f.FieldType == type).SetValue(owner, value);
    private static object NativeSpace(Vector3 origin)
    {
        Type type = Assembly.Load("ShipWalk.NativeBlockContactFixture").GetType("Assembly-CSharp.OptionsScope", true);
        object space = FormatterServices.GetUninitializedObject(type);
        Set(space, "childMessage", typeof(int), 2);
        Set(space, "nodeCache", typeof(int), 1);
        Set(space, "childMessage", typeof(UVector), new UVector(origin.X, origin.Y, origin.Z));
        Set(space, "childMessage", typeof(UQuaternion), new UQuaternion(0, 0, 0, 1));
        Set(space, "nodeCache", typeof(UQuaternion), new UQuaternion(0, 0, 0, 1));
        return space;
    }
    private static Vector3 NativePoint(object grid, Vector3 world)
    {
        var result = (UVector)grid.GetType().GetMethod("DetachEmulator", new[] { typeof(UVector) })
            .Invoke(grid, new object[] { new UVector(world.X, world.Y, world.Z) });
        return new Vector3(result.x, result.y, result.z);
    }
    internal static void RecordedMovingLookup()
    {
        // 17:58:33.406: displayed local Z 10.944, native local Z 9.557.
        // A CV elevator cell centred at local Z 11 is missed after this gap.
        // The log lacks the actual block ID; this fixture reproduces the
        // measured coordinate discrepancy using an explicit test elevator.
        var origin = new Vector3(100, 200, 300);
        object grid = NativeSpace(origin);
        Vector3 local = new Vector3(3.580f, 16.064f, 10.944f);
        Vector3 stopped = NativePoint(grid, origin + local);
        Vector3 moving = NativePoint(grid, origin + local - new Vector3(0, 0, 1.387f));
        Check(stopped.Z >= 4.95f && stopped.Z <= 6.05f, "Stationary elevator reproduction misses its expanded native cell.");
        Check(moving.Z < 4.95f && Math.Abs(moving.Z * 2 - 9.557f) < .0001f,
            "Measured moving-pose discrepancy did not miss the same elevator cell.");
        // The native climb input/velocity path already works with jetpack off.
        Check(Math.Abs(LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitY, 5.5f, false).Y - 3.666667f) < .0001f,
            "Climb velocity itself requires a jetpack.");
    }

    public readonly struct Box
    {
        public readonly Vector3 Min, Max;
        public Box(Vector3 min, Vector3 max) { Min = min; Max = max; }
    }
    public sealed class Actor
    {
        public Vector3 Local, World;
        public Box WorldBounds;
        public bool Climbing;
    }
    public sealed class Grid
    {
        private readonly object native;
        public int Callbacks;
        public Action Callback;
        public Grid(Vector3 origin) { native = NativeSpace(origin); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Vector3 Point(Vector3 world) => NativePoint(native, world);
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Box Bounds(Box world) => new Box(Point(world.Min), Point(world.Max));
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void Visit(Actor actor, Box bounds)
        {
            Vector3 point = Point(actor.World);
            if (bounds.Max.Z >= 5 && bounds.Min.Z <= 6 && point.Z >= 4.95f && point.Z <= 6.05f)
            {
                actor.Climbing = true;
                Callbacks++;
                Callback?.Invoke();
            }
        }
    }
    public sealed class Scanner
    {
        public Grid Primary, Candidate;
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void Scan(Actor actor, bool nearby)
        {
            actor.Climbing = false;
            Primary.Visit(actor, Primary.Bounds(actor.WorldBounds));
            if (nearby) Candidate.Visit(actor, Candidate.Bounds(actor.WorldBounds));
        }
    }
    private static Actor owner;
    private static Grid ship;
    private static bool active;
    private static LocalBlockCoordinates coordinates;
    private static readonly MethodInfo boundsMethod = typeof(Grid).GetMethod(nameof(Grid.Bounds));
    private static readonly MethodInfo pointMethod = typeof(Grid).GetMethod(nameof(Grid.Point));
    private static readonly MethodInfo scanMethod = typeof(Scanner).GetMethod(nameof(Scanner.Scan));
    private static readonly MethodInfo visitMethod = typeof(Grid).GetMethod(nameof(Grid.Visit));
    public static IEnumerable<CodeInstruction> ScanPatch(IEnumerable<CodeInstruction> code)
        => BlockContactPatch.Rewrite(code, boundsMethod, typeof(BlockContactTests).GetMethod(nameof(ReadBounds)), 2);
    public static IEnumerable<CodeInstruction> VisitPatch(IEnumerable<CodeInstruction> code)
        => BlockContactPatch.Rewrite(code, pointMethod, typeof(BlockContactTests).GetMethod(nameof(ReadPoint)), 1);
    public static Box ReadBounds(Grid grid, Box world, Actor actor)
    {
        if (!active || grid != ship || actor != owner) return grid.Bounds(world);
        coordinates.Bounds(actor.Local, actor.Local + new Vector3(.4f, 1.5f, .4f), out var min, out var max);
        return new Box(min, max);
    }
    public static Vector3 ReadPoint(Grid grid, Vector3 world, Actor actor)
        => active && grid == ship && actor == owner ? coordinates.Point(actor.Local) : grid.Point(world);
    private static Scanner Prepare(float gap)
    {
        Vector3 origin = new Vector3(100, 200, 300);
        var pose = new LocalFramePose(origin, Quaternion.Identity);
        coordinates = new LocalBlockCoordinates(pose, pose, 2);
        ship = new Grid(origin); owner = new Actor { Local = new Vector3(3.58f, 16.064f, 10.944f) };
        owner.World = origin + owner.Local - new Vector3(0, 0, gap);
        owner.WorldBounds = new Box(owner.World, owner.World + new Vector3(.4f, 1.5f, .4f));
        active = true;
        return new Scanner { Primary = ship, Candidate = new Grid(origin) };
    }
    private static Harmony Patch(string name)
    {
        var harmony = new Harmony(name);
        harmony.Patch(scanMethod, transpiler: new HarmonyMethod(typeof(BlockContactTests).GetMethod(nameof(ScanPatch))));
        harmony.Patch(visitMethod, transpiler: new HarmonyMethod(typeof(BlockContactTests).GetMethod(nameof(VisitPatch))));
        return harmony;
    }
    internal static void LocalContactPipeline()
    {
        var scan = Prepare(1.387f);
        scan.Scan(owner, true);
        Check(!owner.Climbing, "Native-pattern pipeline did not reproduce moving elevator loss.");
        var harmony = Patch("shipwalk.tests.block-contact");
        try
        {
            foreach (float gap in new[] { 0f, 1.387f, 1.609f, 4.393f, -.877f })
            {
                scan = Prepare(gap);
                scanMethod.Invoke(scan, new object[] { owner, true });
                Check(owner.Climbing && ship.Callbacks == 1, "Local contact does not retain elevator callback across displayed/native gap.");
                if (gap > 1.3f) Check(scan.Candidate.Callbacks == 0, "Other grid adopted the current ship frame.");
                Vector3 speed = LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitY, 5.5f, false);
                Check(speed.Y > 3.6f, "Jetpack-off ascent missing.");
                Vector3 world = owner.World;
                Check(Vector3.Distance(ship.Point(world), (world - new Vector3(100, 200, 300)) / 2) < .0001f,
                    "General native coordinate conversion was overridden.");
            }
        }
        finally { active = false; owner = null; ship = null; harmony.UnpatchAll(harmony.Id); }
    }
    internal static void ContactLifecycle()
    {
        var harmony = Patch("shipwalk.tests.block-lifecycle");
        try
        {
            var scan = Prepare(1.387f);
            var other = new Actor { Local = owner.Local, World = owner.World, WorldBounds = owner.WorldBounds };
            scanMethod.Invoke(scan, new object[] { other, false });
            Check(!other.Climbing, "Unowned actor acquired another character's local point.");
            active = false; // Seated, off, single-player or transitioning: use native path.
            scanMethod.Invoke(scan, new object[] { owner, false });
            Check(!owner.Climbing, "Inactive local frame overrode native contact.");
            active = true;
            ship.Callback = () =>
            {
                active = false; // Native callback can start a seat transition.
                Check(ship.Point(owner.World).Z < 4.95f, "Query state leaked into a native callback.");
                throw new InvalidOperationException("native callback fixture");
            };
            bool threw = false;
            try { scanMethod.Invoke(scan, new object[] { owner, false }); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { threw = true; }
            Check(threw && !active, "Native callback exception/lifecycle was swallowed.");
            ship.Callback = null;
            scanMethod.Invoke(scan, new object[] { owner, false });
            Check(!owner.Climbing, "Disposed frame retained sticky elevator state.");
            active = true; owner.Local = new Vector3(3.58f, 16.064f, 13f);
            scanMethod.Invoke(scan, new object[] { owner, false });
            Check(!owner.Climbing, "Actual local exit retained elevator state.");
        }
        finally { active = false; owner = null; ship = null; harmony.UnpatchAll(harmony.Id); }
        var restored = Prepare(1.387f);
        scanMethod.Invoke(restored, new object[] { owner, false });
        Check(!owner.Climbing, "Unpatch did not restore native contact.");
        active = false; owner = null; ship = null;
    }
    internal static void RotatedContactBounds()
    {
        foreach (float scale in new[] { .5f, 2f })
        foreach (Vector3 origin in new[] { Vector3.Zero, new Vector3(9000, 2000, -4000) })
        {
            var native = new LocalFramePose(origin, Quaternion.CreateFromYawPitchRoll(.6f, .2f, .1f));
            var offset = new Vector3(0, 1.3f, 0);
            var gridRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
            var grid = new LocalFramePose(native.ToWorldPoint(offset), native.Rotation * gridRotation);
            var frame = new LocalBlockCoordinates(native, grid, scale);
            var cell = new Vector3(1.5f, 2.5f, 5.5f);
            Vector3 local = offset + Vector3.Transform(cell * scale, gridRotation);
            Check(Vector3.Distance(frame.Point(local), cell) < .002f, "Grid offset, rotation or vessel scale changed the cell.");
            Vector3 size = new Vector3(.7f, 1.5f, .9f);
            frame.Bounds(local, local + size, out var min, out var max);
            for (int i = 0; i < 8; i++)
            {
                var p = frame.Point(local + new Vector3((i & 1) == 0 ? 0 : size.X,
                    (i & 2) == 0 ? 0 : size.Y, (i & 4) == 0 ? 0 : size.Z));
                Check(p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z,
                    "Rotated bounds excluded a capsule-envelope corner.");
            }
        }
    }
    internal static void ContactRewriteLayout()
    {
        var instruction = new CodeInstruction(OpCodes.Callvirt, pointMethod);
        var il = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        instruction.labels.Add(il.DefineLabel());
        instruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        var rewritten = VisitPatch(new[] { instruction }).ToArray();
        Check(rewritten.Length == 2 && rewritten[0].opcode == OpCodes.Ldarg_1
            && rewritten[0].labels.SequenceEqual(instruction.labels) && rewritten[0].blocks.SequenceEqual(instruction.blocks)
            && rewritten[1].labels.Count == 0 && rewritten[1].blocks.Count == 0 && instruction.Calls(pointMethod),
            "Actor injection lost branch/exception metadata or changed original instructions.");
        foreach (var invalid in new[] { Array.Empty<CodeInstruction>(), new[] { instruction, instruction } })
        {
            bool refused = false;
            try { VisitPatch(invalid).ToArray(); } catch (NotSupportedException) { refused = true; }
            Check(refused, "Changed point conversion site count accepted.");
        }
    }
}
