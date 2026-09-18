using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ShipWalk;

public static class BodyActivationTests
{
    public sealed class Body
    {
        public bool activeSelf { get; private set; } = true;
        public bool Kinematic;
        public Vector3 Velocity = new Vector3(50, 0, 0), Position;
        public void SetActive(bool active) { activeSelf = active; }
        public void Step(float dt) { if (activeSelf && !Kinematic) Position += Velocity * dt; }
    }
    public sealed class Node { public Body gameObject { get; } = new Body(); }
    public class Vessel
    {
        public Node Root = new Node(), Child = new Node();
        public bool Detailed = false, Initialized = true;
        public int Selections = 0;
    }
    private static PhysicsActivity lease;
    public static bool Read(Body body, object _) => lease != null ? lease.Requested : body.activeSelf;
    public static void Write(Body body, bool active, object _) => body.SetActive(lease != null ? lease.Request(active) : active);
    public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        => BodyActivationPatch.Rewrite(instructions, typeof(Vessel).GetField(nameof(Vessel.Root)),
            typeof(Node).GetProperty(nameof(Node.gameObject)).GetGetMethod(),
            typeof(Body).GetProperty(nameof(Body.activeSelf)).GetGetMethod(), typeof(Body).GetMethod(nameof(Body.SetActive)),
            typeof(BodyActivationTests).GetMethod(nameof(Read)), typeof(BodyActivationTests).GetMethod(nameof(Write)));

    // Native IL shape: root.activeSelf is also its collider-selection cache.
    private static MethodInfo BuildFixture()
    {
        var asm = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("ShipWalk.BodyFixture"), AssemblyBuilderAccess.Run);
        var type = asm.DefineDynamicModule("fixture").DefineType("Selector", TypeAttributes.Public, typeof(Vessel));
        var method = type.DefineMethod("Select", MethodAttributes.Public, typeof(void), new[] { typeof(bool) });
        var il = method.GetILGenerator();
        il.DeclareLocal(typeof(int)); il.DeclareLocal(typeof(bool));
        var update = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, typeof(Vessel).GetField(nameof(Vessel.Root)));
        il.Emit(OpCodes.Callvirt, typeof(Node).GetProperty(nameof(Node.gameObject)).GetGetMethod());
        il.Emit(OpCodes.Callvirt, typeof(Body).GetProperty(nameof(Body.activeSelf)).GetGetMethod());
        il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Bne_Un_S, update);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, typeof(Vessel).GetField(nameof(Vessel.Initialized)));
        il.Emit(OpCodes.Brfalse_S, update); il.Emit(OpCodes.Ret);
        il.MarkLabel(update);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, typeof(Vessel).GetField(nameof(Vessel.Root)));
        il.Emit(OpCodes.Callvirt, typeof(Node).GetProperty(nameof(Node.gameObject)).GetGetMethod());
        il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Callvirt, typeof(Body).GetMethod(nameof(Body.SetActive)));
        // An unrelated child's native activation must remain untouched.
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, typeof(Vessel).GetField(nameof(Vessel.Child)));
        il.Emit(OpCodes.Callvirt, typeof(Node).GetProperty(nameof(Node.gameObject)).GetGetMethod());
        il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Callvirt, typeof(Body).GetMethod(nameof(Body.SetActive)));
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Stfld, typeof(Vessel).GetField(nameof(Vessel.Detailed)));
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, typeof(Vessel).GetField(nameof(Vessel.Selections)));
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, typeof(Vessel).GetField(nameof(Vessel.Selections)));
        il.Emit(OpCodes.Ret);
        return type.CreateType().GetMethod("Select");
    }

    public static void FreezeRegression()
    {
        lease = null;
        var method = BuildFixture();
        var ship = (Vessel)Activator.CreateInstance(method.DeclaringType);
        void Select(bool bounding) => method.Invoke(ship, new object[] { bounding });
        var body = ship.Root.gameObject;
        Select(false); body.Step(1f);
        Check(body.Velocity.X == 50 && body.Position.X == 0, "baseline: saved speed but no motion outside seat");
        Select(true); body.Step(1f);
        Check(body.Position.X == 50, "baseline: re-entering resumes motion");
        var harmony = new Harmony("shipwalk.tests.bodyactivation");
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BodyActivationTests).GetMethod(nameof(Rewrite))));
            body.Position = Vector3.Zero; lease = new PhysicsActivity(true);
            Select(false);
            Check(body.activeSelf && ship.Detailed && !ship.Child.gameObject.activeSelf, "only body stays live; detailed hull selection and unrelated activation unchanged");
            int selections = ship.Selections;
            for (int i = 0; i < 100; i++) { Select(false); body.Step(.02f); }
            Check(Math.Abs(body.Position.X - 100) < .001f, "ship travels 100m in two seconds without re-entering");
            Check(ship.Selections == selections, "virtual selector cache preserves native early return");
            body.Velocity = new Vector3(-3, 0, 0); body.Step(1f);
            Check(Math.Abs(body.Position.X - 97) < .001f, "impact response remains; no velocity overwrite");
            body.Kinematic = true; body.Step(1f);
            Check(body.Position.X == 97, "native kinematic state still prevents simulation"); body.Kinematic = false;
            // Release to recorded native state, then native re-entry must refresh detailed colliders.
            body.SetActive(lease.Requested); lease = null; Select(true);
            Check(body.activeSelf && !ship.Detailed && ship.Child.gameObject.activeSelf, "re-entry restores bounding selection instead of early-returning with stale hulls");
            lease = new PhysicsActivity(true); Select(false);
            body.SetActive(lease.Requested); lease = null; body.Step(1f);
            Check(!body.activeSelf && body.Position.X == 97, "cancellation restores native requested activation");
        }
        finally { lease = null; harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
        Select(true); Select(false); Check(!body.activeSelf, "unpatch restores original freeze behavior");
    }

    public static void RejectUnknownIL()
    {
        var code = PatchProcessor.GetOriginalInstructions(BuildFixture()).ToList();
        Check(Rewrite(code).Any(), "known selector accepted");
        void Reject(IEnumerable<CodeInstruction> body)
        {
            bool rejected = false;
            try { Rewrite(body).ToArray(); } catch (NotSupportedException) { rejected = true; }
            Check(rejected, "unknown activation pattern must fail closed");
        }
        Reject(Array.Empty<CodeInstruction>());
        Reject(code.Concat(code));
        Reject(code.Where(i => !i.Calls(typeof(Body).GetMethod(nameof(Body.SetActive)))));
    }

    private sealed class Collider { public bool Alive = true, Active = true; }
    public static void OwnCollisionPairs()
    {
        var bounds = new Collider(); var hull = new Collider(); var previous = new Collider();
        var world = new Collider(); var player = new Collider(); var rebuilt = new Collider();
        var pairs = new HashSet<Tuple<Collider, Collider>> { Tuple.Create(bounds, previous) };
        var lease = new CollisionPairs<Collider>(c => c.Alive, c => c.Active,
            (a, b) => pairs.Contains(Tuple.Create(a, b)),
            (a, b, ignored) => { if (ignored) pairs.Add(Tuple.Create(a, b)); else pairs.Remove(Tuple.Create(a, b)); });
        lease.Refresh(new[] { bounds }, new[] { hull, previous });
        Check(pairs.Contains(Tuple.Create(bounds, hull)), "own hull suppressed");
        Check(!pairs.Contains(Tuple.Create(bounds, world)) && !pairs.Contains(Tuple.Create(bounds, player)), "world and players unaffected");
        hull.Active = false; pairs.Remove(Tuple.Create(bounds, hull));
        lease.Refresh(new[] { bounds }, new[] { hull, previous });
        Check(!pairs.Contains(Tuple.Create(bounds, hull)), "inactive collider not passed to ignore write");
        hull.Active = true; lease.Refresh(new[] { bounds }, new[] { hull, previous });
        Check(pairs.Contains(Tuple.Create(bounds, hull)), "reenabled collider suppression restored");
        lease.Refresh(new[] { rebuilt }, new[] { hull });
        Check(!pairs.Contains(Tuple.Create(bounds, hull)) && pairs.Contains(Tuple.Create(bounds, previous)), "rebuild restores owned ignores only");
        Check(pairs.Contains(Tuple.Create(rebuilt, hull)), "rebuilt collider covered");
        lease.Reset(); Check(lease.Count == 0 && pairs.Count == 1, "cleanup preserves preexisting ignore");
        lease.Refresh(new[] { bounds }, new[] { hull }); hull.Alive = false;
        lease.Refresh(new[] { rebuilt }, new[] { hull }); lease.Reset();
        Check(lease.Count == 0, "destroyed colliders released without native calls");
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
}
