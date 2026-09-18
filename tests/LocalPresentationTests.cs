using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class LocalPresentationTests
{
    internal static void IdleTransport()
    {
        var state = new LocalPresentation();
        Vector3 local = new Vector3(3.809f, 46.073f, -143.774f);
        state.Reset(local);
        for (int tick = 0; tick < 300; tick++)
        {
            state.Advance(local);
            // Recorded coasting speed; world movement must not advance a step.
            var ship = new LocalFramePose(new Vector3(100.5f * tick * .025f, 10, 0), Quaternion.Identity);
            Near(state.WorldPoint(ship, .3f) - ship.Position, local, .0001f);
            Near(state.TakeLocomotion(ship.Rotation), Vector3.Zero);
        }
    }

    internal static void ActualRelativeTravel()
    {
        var state = new LocalPresentation();
        state.Reset(new Vector3(10, 3, 8));
        // A collision accepts only 0.02 m of a requested 0.1 m step.
        state.Advance(new Vector3(10.02f, 3, 8));
        Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI / 2);
        Near(state.TakeLocomotion(q), new Vector3(0, 0, -.02f));
        Near(state.TakeLocomotion(q), Vector3.Zero); // repeated consumer tick
        state.Advance(new Vector3(10.02f, 3.15f, 8));
        state.Advance(new Vector3(10.02f, 3.28f, 8));
        Near(state.TakeLocomotion(q), new Vector3(0, .28f, 0));
        state.Advance(new Vector3(10.02f, 3.28f, 8));
        Near(state.TakeLocomotion(q), Vector3.Zero);
    }

    internal static void RenderPoseAlignment()
    {
        var state = new LocalPresentation();
        Vector3 point = new Vector3(2, 42, -160);
        state.Reset(point);
        var physics = new LocalFramePose(new Vector3(100, 0, 0), Quaternion.Identity);
        var visible = new LocalFramePose(new Vector3(102.5125f, 0, 0), Quaternion.Identity);
        Check(Vector3.Distance(physics.ToWorldPoint(point), visible.ToWorldPoint(point)) > 2.5f,
            "Fixture did not reproduce one physics interval of offset at 100.5 m/s.");
        Near(visible.ToLocalPoint(state.WorldPoint(visible, .5f)), point);
        state.Advance(point + new Vector3(.1f, .15f, 0));
        Near(state.Sample(.5f), point + new Vector3(.05f, .075f, 0));
        Near(state.Sample(-1), point);
        Near(state.Sample(2), point + new Vector3(.1f, .15f, 0));
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f);
        var shifted = new LocalFramePose(new Vector3(-30, 10, 7), tilt);
        Near(shifted.ToLocalPoint(state.WorldPoint(shifted, .5f)), state.Sample(.5f));
    }

    internal static void ResetAndLease()
    {
        var state = new LocalPresentation();
        state.Reset(new Vector3(100, 40, -150));
        state.Advance(new Vector3(101, 40, -150));
        state.Reset(new Vector3(-20, 3, 40)); // another ship/seat exit
        Near(state.TakeLocomotion(Quaternion.Identity), Vector3.Zero);
        Near(state.Sample(.5f), new Vector3(-20, 3, 40));
        var lease = new CharacterBodyLease();
        lease.Capture(false, true, 1);
        Check(lease.Release() && !lease.Held && lease.Interpolation == 1 && !lease.Kinematic && lease.DetectCollisions,
            "Character interpolation or physics state was lost on release.");
        Check(!lease.Release(), "Lease released twice.");
    }

    public sealed class NativeConsumer
    {
        public readonly LocalPresentationState State = new LocalPresentationState();
        public bool Owned;
        public int Count = 4, TailCalls, FilterCalls;
        public Vector3 WorldHistory = new Vector3(0, 0, 40), Footstep, Animation;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Tick(float dt)
        {
            Vector3 sample = WorldHistory;
            sample /= Count;
            Feet(sample);
            Animate(sample, dt);
            TailCalls++;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Feet(Vector3 value) { Footstep = value; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Animate(Vector3 value, float dt) { Animation = value; }
    }
    // Public fixture wrapper keeps internal production types out of its API.
    public sealed class LocalPresentationState
    { internal readonly LocalPresentation Value = new LocalPresentation(); }

    private static readonly MethodInfo division = typeof(Vector3).GetMethod("op_Division", new[] { typeof(Vector3), typeof(float) });
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => LocomotionPatch.Rewrite(instructions, division, typeof(LocalPresentationTests).GetMethod(nameof(Filter)));
    public static Vector3 Filter(Vector3 nativeDelta, object actor)
    {
        var consumer = (NativeConsumer)actor;
        consumer.FilterCalls++;
        return consumer.Owned ? consumer.State.Value.TakeLocomotion(Quaternion.Identity) : nativeDelta;
    }

    internal static void NativeConsumerPatch()
    {
        var consumer = new NativeConsumer();
        MethodInfo tick = typeof(NativeConsumer).GetMethod(nameof(NativeConsumer.Tick));
        var harmony = new Harmony("shipwalk.tests.local-locomotion");
        try
        {
            harmony.Patch(tick, transpiler: new HarmonyMethod(typeof(LocalPresentationTests).GetMethod(nameof(Transpiler))));
            tick.Invoke(consumer, new object[] { 1f });
            Near(consumer.Footstep, new Vector3(0, 0, 10));
            Near(consumer.Animation, consumer.Footstep);
            consumer.Owned = true;
            consumer.State.Value.Reset(new Vector3(2, 42, -160));
            tick.Invoke(consumer, new object[] { 1f });
            Near(consumer.Footstep, Vector3.Zero); Near(consumer.Animation, Vector3.Zero);
            consumer.State.Value.Advance(new Vector3(2.1f, 42, -160));
            tick.Invoke(consumer, new object[] { 1f });
            Near(consumer.Footstep, new Vector3(.1f, 0, 0)); Near(consumer.Animation, consumer.Footstep);
            Check(consumer.FilterCalls == 3 && consumer.TailCalls == 3, "Consumers or downstream native work were skipped/duplicated.");
            Near(consumer.WorldHistory, new Vector3(0, 0, 40));
            consumer.Owned = false;
            tick.Invoke(consumer, new object[] { 1f });
            Near(consumer.Animation, new Vector3(0, 0, 10));
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    internal static void PatchRejectsUnknownLayout()
    {
        foreach (var code in new[] {
            new[] { new CodeInstruction(OpCodes.Ret) },
            new[] { new CodeInstruction(OpCodes.Call, division), new CodeInstruction(OpCodes.Stloc_1) },
            new[] { new CodeInstruction(OpCodes.Call, division), new CodeInstruction(OpCodes.Stloc_0),
                new CodeInstruction(OpCodes.Call, division), new CodeInstruction(OpCodes.Stloc_0) } })
        {
            bool refused = false;
            try { Transpiler(code).ToArray(); } catch (NotSupportedException) { refused = true; }
            Check(refused, "Unknown locomotion layout was accepted.");
        }
    }
    private static void Near(Vector3 actual, Vector3 expected, float tolerance = .0001f)
    { Check(Vector3.Distance(actual, expected) <= tolerance, "Expected " + expected + "; actual " + actual); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
