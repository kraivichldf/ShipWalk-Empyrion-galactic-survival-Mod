using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class ExitMomentumTests
{
    public sealed class Vessel
    {
        public Vector3 Velocity = new Vector3(50, 2, -4), Angular = new Vector3(0, .4f, 0);
        public bool Local = true, Ready = true;
    }
    public sealed class Actor
    {
        public Vessel Seat;
        public bool FailExit, ThrowOnExit, TransferAuthority;
        public int ExitCalls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Exit(object destination)
        {
            ExitCalls++;
            if (ThrowOnExit) throw new InvalidOperationException("native exit failure");
            if (destination != null || FailExit) return;
            Seat.Velocity = Vector3.Zero; Seat.Angular = Vector3.Zero;
            if (TransferAuthority) Seat.Local = false;
            Seat = null;
        }
    }
    public sealed class Capture { public Vessel Ship; public ExitMomentum Motion; }
    private static float now;
    private static bool enabled;
    public static void Prefix(Actor __instance, object __0, out Capture __state)
    {
        __state = null;
        if (enabled && __0 == null && __instance.Seat != null && __instance.Seat.Local)
            __state = new Capture { Ship = __instance.Seat, Motion = new ExitMomentum(__instance.Seat.Velocity, __instance.Seat.Angular, now) };
    }
    public static Exception Finalizer(Actor __instance, Exception __exception, Capture __state)
    {
        if (__exception == null && __instance.Seat == null && __state != null
            && __state.Motion.TryTake(now, __state.Ship.Local, __state.Ship.Ready, !enabled, out var v, out var w))
        { __state.Ship.Velocity = v; __state.Ship.Angular = w; }
        return __exception;
    }

    public static void NativeStopAndScopedRestore()
    {
        var originalVelocity = new Vector3(50, 2, -4);
        var originalAngular = new Vector3(0, .4f, 0);
        var ship = new Vessel(); var actor = new Actor { Seat = ship };
        var method = typeof(Actor).GetMethod(nameof(Actor.Exit));
        void Exit() => method.Invoke(actor, new object[] { null });
        enabled = true; now = 10f;
        Exit(); // Reliable baseline reproduction: unlock seat, then original release erases motion.
        Near(ship.Velocity, Vector3.Zero); Near(ship.Angular, Vector3.Zero);
        var harmony = new Harmony("shipwalk.tests.exitmomentum");
        try
        {
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ExitMomentumTests).GetMethod(nameof(Prefix))),
                finalizer: new HarmonyMethod(typeof(ExitMomentumTests).GetMethod(nameof(Finalizer))));
            void RestoreSeat() { actor.Seat = ship; ship.Velocity = originalVelocity; ship.Angular = originalAngular; }
            RestoreSeat(); Exit();
            Near(ship.Velocity, originalVelocity); Near(ship.Angular, originalAngular);
            Check(actor.Seat == null && actor.ExitCalls == 2, "original detachment still executed exactly once");
            // Subsequent collision changes persist: there is no velocity restore loop.
            ship.Velocity = new Vector3(-3, 0, 0); Near(ship.Velocity, new Vector3(-3, 0, 0));
            RestoreSeat(); actor.FailExit = true; Exit();
            Check(actor.Seat == ship, "failed exit must keep original seat"); Near(ship.Velocity, originalVelocity);
            actor.FailExit = false; actor.ThrowOnExit = true;
            bool threw = false;
            try { Exit(); } catch (TargetInvocationException e) { threw = e.InnerException is InvalidOperationException; }
            Check(threw, "native exceptions must propagate"); actor.ThrowOnExit = false;
            RestoreSeat(); actor.TransferAuthority = true; Exit();
            Near(ship.Velocity, Vector3.Zero); Check(!ship.Local, "departing client must not reclaim server authority");
            ship.Local = true; actor.TransferAuthority = false; enabled = false; RestoreSeat(); Exit();
            Near(ship.Velocity, Vector3.Zero);
            enabled = true; RestoreSeat(); method.Invoke(actor, new object[] { new object() });
            Check(actor.Seat == ship, "attachment is unchanged"); Near(ship.Velocity, originalVelocity);
        }
        finally { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
        actor.Seat = ship; ship.Velocity = originalVelocity; Exit(); Near(ship.Velocity, Vector3.Zero);
    }

    public static void HandoffAndCollision()
    {
        var motion = new ExitMomentum(new Vector3(40, 0, 0), new Vector3(0, .2f, 0), 10f);
        Check(!motion.TryTake(10.01f, true, false, false, out _, out _) && !motion.Finished, "wait for native dynamic body");
        Check(motion.TryTake(10.02f, true, true, false, out var v, out var w), "server receives momentum once");
        Near(v, new Vector3(40, 0, 0)); Near(w, new Vector3(0, .2f, 0));
        var afterImpact = new Vector3(-4, 0, 0);
        for (int i = 0; i < 1000; i++)
            if (motion.TryTake(10.03f + i * .02f, true, true, false, out var repeated, out _)) afterImpact = repeated;
        Near(afterImpact, new Vector3(-4, 0, 0));
        foreach (string scenario in new[] { "expired", "authority-lost", "cancelled", "clock-reset" })
        {
            var pending = new ExitMomentum(v, w, 10f);
            Check(!pending.TryTake(scenario == "expired" ? 10.3f : scenario == "clock-reset" ? 9f : 10.01f,
                scenario != "authority-lost", true, scenario == "cancelled", out _, out _) && pending.Finished, scenario);
            Check(!pending.TryTake(10.02f, true, true, false, out _, out _), "cancelled transfer cannot restart");
        }
        Check(!ExitMomentum.Valid(new Vector3(float.NaN, 0, 0), Vector3.Zero), "reject invalid velocity");
        Check(!ExitMomentum.Valid(new Vector3(301, 0, 0), Vector3.Zero), "reject discontinuous speed");
    }

    public static void ServerPoseSampling()
    {
        var sample = new RemoteShipMotion();
        sample.Observe(new Vector3(10000, 0, 0), Quaternion.Identity, 1f);
        Check(!sample.TryGet(1f, Vector3.Zero, out _, out _), "one pose cannot establish motion");
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .02f);
        sample.Observe(new Vector3(10002, 0, 0), rotation, 1.05f);
        Check(sample.TryGet(1.06f, new Vector3(0, 0, 5), out var v, out var w), "fresh authoritative observed motion");
        Near(w, new Vector3(0, .4f, 0), .001f); Near(v, new Vector3(42, 0, 0), .006f);
        sample.Observe(new Vector3(10002, 0, 0), rotation, 1.05f);
        Check(sample.TryGet(1.06f, Vector3.Zero, out v, out _), "same-tick packet does not destroy good sample");
        Near(v, new Vector3(40, 0, 0));
        Check(!sample.TryGet(1.4f, Vector3.Zero, out _, out _), "stale samples rejected");
        sample.Observe(new Vector3(30000, 0, 0), rotation, 1.1f);
        Check(!sample.TryGet(1.1f, Vector3.Zero, out _, out _), "teleport rejected");
        sample.Observe(new Vector3(float.NaN, 0, 0), rotation, 1.15f);
        Check(!sample.TryGet(1.15f, Vector3.Zero, out _, out _), "invalid pose rejected");
        var wrap = new RemoteShipMotion();
        wrap.Observe(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(Math.PI * 359 / 180)), 2f);
        wrap.Observe(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(Math.PI / 180)), 2.05f);
        Check(wrap.TryGet(2.05f, Vector3.Zero, out _, out w), "rotation wraps through zero");
        Near(w, new Vector3(0, (float)(Math.PI * 2 / 180 / .05), 0), .002f);
    }

    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    private static void Near(Vector3 a, Vector3 b, float tolerance = .0002f)
    { Check(Vector3.Distance(a, b) < tolerance, "expected " + b + ", got " + a); }
}
