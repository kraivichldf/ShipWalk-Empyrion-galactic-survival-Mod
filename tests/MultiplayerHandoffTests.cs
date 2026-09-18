using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class MultiplayerHandoffTests
{
    public class BaseActor
    {
        public bool CharacterKinematic, DerivedFinished;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Exit() { CharacterKinematic = false; }
    }
    public sealed class LocalActor : BaseActor
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Exit() { base.Exit(); CharacterKinematic = false; DerivedFinished = true; }
    }
    private static SeatOperationGate gate;
    public static void OldFinalizer(BaseActor __instance) { __instance.CharacterKinematic = true; }
    public static void Begin() { gate.Begin(); }
    public static void Complete(LocalActor __instance)
    {
        Check(__instance.DerivedFinished, "Acquired inside the base exit.");
        gate.Complete(10, 20);
        Check(!gate.CanActivate(10), "Still activated in the callback's fixed step.");
    }
    public static void DerivedSeatOrdering()
    {
        MethodInfo parent = typeof(BaseActor).GetMethod(nameof(BaseActor.Exit));
        MethodInfo child = typeof(LocalActor).GetMethod(nameof(LocalActor.Exit));
        var harmony = new Harmony("shipwalk.tests.handoff-order");
        try
        {
            harmony.Patch(parent, finalizer: new HarmonyMethod(typeof(MultiplayerHandoffTests), nameof(OldFinalizer)));
            var actor = new LocalActor(); child.Invoke(actor, null);
            Check(!actor.CharacterKinematic, "Old base-finalizer failure was not reproduced.");
            harmony.UnpatchAll(harmony.Id);
            gate = new SeatOperationGate();
            harmony.Patch(child, prefix: new HarmonyMethod(typeof(MultiplayerHandoffTests), nameof(Begin)),
                postfix: new HarmonyMethod(typeof(MultiplayerHandoffTests), nameof(Complete)));
            child.Invoke(actor, null);
            Check(gate.CanActivate(10.02f), "Completed outer transaction did not release on next physics step.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    public static void RepeatedAcknowledgements()
    {
        var transaction = new SeatOperationGate();
        transaction.Begin();
        Check(!transaction.CanActivate(1), "Active native transaction accepted.");
        transaction.Complete(1, 10);
        Check(!transaction.CanActivate(1) && transaction.CanActivate(1.02f), "Exit activation not deferred.");
        // Reproduce the 70/101 ms acknowledgement windows in the captured log.
        foreach (float acknowledgement in new[] { 10.070f, 10.101f })
        {
            transaction.Begin(); transaction.Begin();
            transaction.Complete(1.1f, acknowledgement);
            Check(transaction.Busy && !transaction.CanActivate(1.2f), "Nested callback ended the transaction early.");
            transaction.Complete(1.1f, acknowledgement);
            Check(!transaction.CanActivate(1.1f) && transaction.CanActivate(1.12f), "Repeated acknowledgement acquired too soon.");
        }
        Check(!transaction.CanActivate(float.NaN), "Invalid simulation clock accepted.");
        transaction.Reset();
        Check(transaction.CanActivate(0) && !transaction.Settling(0), "Session reset retained a previous acknowledgement.");
    }
    public static void VelocityHistoryReset()
    {
        var transaction = new SeatOperationGate(); transaction.Begin(); transaction.Complete(1, 10);
        var frame = new LocalFramePose(new Vector3(100, 0, 0), Quaternion.Identity);
        Vector3 accepted = new Vector3(0, 6, 0), world = new Vector3(65.437f, 6, 0);
        float clock = 10.02f;
        foreach (float historySpeed in new[] { 6.463f, .075f, 0f, 65.437f })
        {
            var transport = new Vector3(historySpeed, 0, 0);
            Near(transaction.RelativeVelocity(frame, world, transport, accepted, clock, true), accepted);
            world = frame.ToWorldVelocity(accepted, transport);
            clock += .02f;
        }
        // Once the transaction settles, ordinary inertia applies again.
        Near(transaction.RelativeVelocity(frame, new Vector3(70, 6, 0), new Vector3(65, 0, 0), accepted, 10.3f, true), new Vector3(5, 6, 0));
        Near(transaction.RelativeVelocity(frame, new Vector3(70, 6, 0), new Vector3(65, 0, 0), accepted, 10.1f, false), new Vector3(5, 6, 0));
    }
    public static void ServerTransfer()
    {
        var sample = new RemoteShipMotion();
        sample.Observe(new Vector3(1000, 5, 1000), Quaternion.Identity, 1);
        sample.Observe(new Vector3(1005, 5, 1000), Quaternion.Identity, 1.05f);
        Check(sample.TryGet(1.06f, Vector3.Zero, out var linear, out var angular), "Fresh observed server motion missing.");
        Near(linear, new Vector3(100, 0, 0));
        var release = new ExitMomentum(linear, angular, 1.06f);
        Check(!release.TryTake(1.07f, true, false, false, out _, out _), "Inactive server body received motion.");
        Check(release.TryTake(1.08f, true, true, false, out var applied, out _), "Server authority did not receive motion.");
        Near(applied, linear);
        Check(!release.TryTake(1.09f, true, true, false, out _, out _), "Collision velocity was overwritten by a repeated transfer.");
        Check(!sample.TryGet(1.5f, Vector3.Zero, out _, out _), "Stale remote motion accepted.");
        var lost = new ExitMomentum(linear, angular, 2);
        Check(!lost.TryTake(2.01f, false, true, false, out _, out _), "A client that lost authority wrote ship physics.");
    }

    public sealed class TestBody { public bool Kinematic { get; set; } = true; }
    public sealed class TestShip
    {
        public TestBody Body = new TestBody();
        public bool Coasting = true, Remote, Pilot, NativeRequest = true, Requested = true;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void NativeUpdate() { if (Body.Kinematic != NativeRequest) Body.Kinematic = NativeRequest; }
    }
    public static bool ReadMode(TestBody body, object instance)
    { var s = (TestShip)instance; return s.Coasting && !s.Remote && !s.Pilot ? s.Requested : body.Kinematic; }
    public static void WriteMode(TestBody body, bool value, object instance)
    {
        var s = (TestShip)instance;
        if (s.Coasting && !s.Remote && !s.Pilot) { s.Requested = value; body.Kinematic = false; }
        else body.Kinematic = value;
    }
    public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> code)
        => ShipKinematicPatch.Rewrite(code, typeof(TestBody).GetProperty(nameof(TestBody.Kinematic)).GetGetMethod(),
            typeof(TestBody).GetProperty(nameof(TestBody.Kinematic)).GetSetMethod(),
            typeof(MultiplayerHandoffTests).GetMethod(nameof(ReadMode)), typeof(MultiplayerHandoffTests).GetMethod(nameof(WriteMode)));
    public static void NativeModeRewrite()
    {
        var harmony = new Harmony("shipwalk.tests.server-body-mode");
        MethodInfo method = typeof(TestShip).GetMethod(nameof(TestShip.NativeUpdate));
        var ship = new TestShip(); ship.Body.Kinematic = false;
        method.Invoke(ship, null); Check(ship.Body.Kinematic, "Native unpiloted parking failure was not reproduced.");
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MultiplayerHandoffTests), nameof(Rewrite)));
            ship.Body.Kinematic = false; method.Invoke(ship, null);
            Check(!ship.Body.Kinematic, "Confirmed coasting was parked.");
            ship.NativeRequest = false; method.Invoke(ship, null);
            Check(!ship.Requested && !ship.Body.Kinematic, "Native mode request not retained.");
            ship.Remote = true; ship.NativeRequest = true; method.Invoke(ship, null);
            Check(ship.Body.Kinematic, "Remote ownership did not regain native mode.");
            ship.Remote = false; ship.Pilot = true; ship.Body.Kinematic = false; method.Invoke(ship, null);
            Check(ship.Body.Kinematic, "Pilot takeover did not regain native mode.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        bool refused = false;
        try { Rewrite(Array.Empty<CodeInstruction>()).ToArray(); } catch (NotSupportedException) { refused = true; }
        Check(refused, "Unknown native simulation-mode IL accepted.");
    }
    private static void Near(Vector3 actual, Vector3 expected) => Check(Vector3.Distance(actual, expected) < .003f, "Expected " + expected + "; actual " + actual);
    private static void Check(bool valid, string reason) { if (!valid) throw new Exception(reason); }
}
