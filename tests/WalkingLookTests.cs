using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class WalkingLookTests
{
    // Model the two distinct operations at the verified native IL boundary:
    // MoveRotation queues a physics request; presentation directly publishes pose.
    // This is a scheduling fixture, not an execution of Unity's physics engine.
    public sealed class Body
    {
        public Quaternion Rotation = Quaternion.Identity;
        public Quaternion? Pending;
        public int Requests;
        public void MoveRotation(Quaternion target) { Pending = target; Requests++; }
        public void Publish(Quaternion target) { Rotation = target; Pending = null; }
        public void Physics() { if (Pending.HasValue) Rotation = Pending.Value; Pending = null; }
    }
    public sealed class Controller
    {
        public readonly Body Body = new Body();
        public float Caption, Previous;
        public int Completed;
        // Native Update 0x06004487: wrapped captionPosition-previousWindow,
        // Transform.rotation * AngleAxis, MoveRotation, then previousWindow write.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void Update()
        {
            float delta = Caption - Previous;
            if (Math.Abs(delta) > 180) delta += delta > 0 ? -360 : 360;
            if (delta < -.001f || delta > .001f)
            {
                Body.MoveRotation(Body.Rotation * Yaw(delta));
                Previous = Caption;
            }
            Completed++;
        }
    }
    private static Body owned;
    private static Quaternion local, published;
    private static readonly MethodInfo move = typeof(Body).GetMethod(nameof(Body.MoveRotation));
    private static readonly MethodInfo update = typeof(Controller).GetMethod(nameof(Controller.Update));
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        => WalkingLookPatch.Rewrite(code, move, typeof(WalkingLookTests).GetMethod(nameof(Write)));
    public static void Write(Body body, Quaternion target)
    {
        if (body != owned) { body.MoveRotation(target); return; }
        local = PassengerLocalMotion.ConsumeLook(local, published, target);
        published = target;
        body.Publish(target);
    }
    private static void Render(Body body, Quaternion ship)
    {
        local = PassengerLocalMotion.ConsumeLook(local, published, body.Rotation);
        published = ship * local;
        body.Publish(published);
    }
    internal static void QueuedTurnReproduction()
    {
        var controller = new Controller { Caption = 12 };
        local = published = Quaternion.Identity;
        controller.Update();
        Check(controller.Body.Pending.HasValue && controller.Previous == 12, "Native turn was not queued/consumed.");
        Render(controller.Body, Quaternion.Identity);
        controller.Body.Physics();
        Near(controller.Body.Rotation, Quaternion.Identity);
        controller.Update();
        Check(controller.Body.Requests == 1, "Consumed mouse turn was unexpectedly replayed.");
    }
    internal static void ImmediateTurnAndTransport()
    {
        var harmony = new Harmony("shipwalk.tests.walking-look");
        try
        {
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(WalkingLookTests).GetMethod(nameof(Transpiler))));
            var controller = new Controller(); owned = controller.Body;
            local = published = Quaternion.Identity;
            float expectedDegrees = 0;
            // Native +/-360 caption wrapping, changing hull heading, multiple
            // presentation passes before physics, and idle input after each turn.
            foreach (float input in new[] { 5f, 170f, 170f, 30f, -80f, -120f })
            {
                expectedDegrees += input;
                controller.Caption += input;
                if (controller.Caption > 360) controller.Caption -= 720;
                update.Invoke(controller, null);
                Check(controller.Previous == controller.Caption, "Native yaw bookkeeping lost.");
                Near(local, Yaw(expectedDegrees));
                Quaternion hull = Yaw(expectedDegrees / 3);
                for (int i = 0; i < 3; i++) Render(owned, hull);
                owned.Physics();
                Near(owned.Rotation, hull * Yaw(expectedDegrees));
                update.Invoke(controller, null);
                Render(owned, hull);
                Near(local, Yaw(expectedDegrees));
            }
            Check(owned.Requests == 0 && controller.Completed == 12, "Native work skipped or a world-physics turn remained queued.");
        }
        finally { owned = null; harmony.UnpatchAll(harmony.Id); }
    }
    internal static void NativeFallbackAndUnpatch()
    {
        var harmony = new Harmony("shipwalk.tests.walking-fallback");
        var controller = new Controller { Caption = 7 };
        try
        {
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(WalkingLookTests).GetMethod(nameof(Transpiler))));
            // Inactive, seated, single-player and unrelated bodies use fallback.
            owned = new Body();
            update.Invoke(controller, null);
            Check(controller.Body.Requests == 1 && controller.Body.Pending.HasValue, "Other body lost native physics request.");
            Near(controller.Body.Rotation, Quaternion.Identity);
            controller.Body.Physics(); Near(controller.Body.Rotation, Yaw(7));
        }
        finally { owned = null; harmony.UnpatchAll(harmony.Id); }
        controller.Caption = 12;
        update.Invoke(controller, null);
        Check(controller.Body.Requests == 2, "Unpatch did not restore native call.");
        controller.Body.Physics(); Near(controller.Body.Rotation, Yaw(12));
    }
    internal static void ChangedLayout()
    {
        var original = new CodeInstruction(OpCodes.Callvirt, move);
        var generator = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        original.labels.Add(generator.DefineLabel());
        original.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        CodeInstruction rewritten = Transpiler(new[] { original }).Single();
        Check(rewritten.labels.SequenceEqual(original.labels) && rewritten.blocks.SequenceEqual(original.blocks), "Control-flow metadata lost.");
        Check(original.Calls(move) && rewritten.opcode == OpCodes.Call, "Original mutated or instance stack signature retained.");
        foreach (var code in new[] { Array.Empty<CodeInstruction>(), new[] { original, original } })
        {
            bool refused = false;
            try { Transpiler(code).ToArray(); } catch (NotSupportedException) { refused = true; }
            Check(refused, "Changed native rotation layout accepted.");
        }
    }
    private static Quaternion Yaw(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * (float)Math.PI / 180);
    private static void Near(Quaternion a, Quaternion b) => Check(Math.Abs(Quaternion.Dot(a, b)) > .99999f, "Facing mismatch: " + a + " / " + b);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
