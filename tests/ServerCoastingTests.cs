using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class ServerCoastingTests
{
    public static void ParkedBodyAndOrigin()
    {
        var sample = new RemoteShipMotion();
        sample.Observe(new Vector3(1395, 5, 20), Quaternion.Identity, 1);
        sample.Observe(new Vector3(1396.442f, 5, 20), Quaternion.Identity, 1.025f);
        Check(sample.TryCapture(1.03f, Vector3.Zero, out var handoff), "Missing paired pose and velocity.");
        var parked = new Vector3(1000, 5, 20);
        // The old velocity-only transfer leaves the parked root 396.442 m behind.
        Near(Vector3.Distance(parked, handoff.AbsolutePose.Position), 396.442f);
        Check(!RemoteFrameMath.TryTransport(handoff.AbsolutePose, new LocalFramePose(parked, Quaternion.Identity),
            Quaternion.Identity, .025f, Vector3.Zero, out _), "Recorded player-frame displacement refusal not reproduced.");
        var newOrigin = new Vector3(1024, 0, 0);
        Check(handoff.TryTake(1.04f, newOrigin, true, true, false, out var pose), "Fresh handoff did not take server authority.");
        Near(pose.Position + newOrigin, handoff.AbsolutePose.Position);
        var nextPose = new LocalFramePose(pose.Position + newOrigin + handoff.Linear * .025f, pose.Rotation);
        Check(RemoteFrameMath.TryTransport(handoff.AbsolutePose, nextPose, Quaternion.Identity, .025f, handoff.Linear, out _),
            "Aligned authority handoff still released the passenger frame.");
        var bodyAfterImpact = pose.Position + new Vector3(-2, 0, 0);
        Check(!handoff.TryTake(1.05f, newOrigin, true, true, false, out _), "Pose replay undid later physical movement.");
        Near(bodyAfterImpact, new Vector3(370.442f, 5, 20));
    }

    public static void RotatingCenterOfMass()
    {
        var sample = new RemoteShipMotion();
        sample.Observe(new Vector3(4000, 0, 0), Quaternion.Identity, 1);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .02f);
        sample.Observe(new Vector3(4002, 0, 0), rotation, 1.05f);
        var localCenter = new Vector3(0, 0, 5);
        Check(sample.TryCapture(1.06f, localCenter, out var handoff), "COM handoff unavailable.");
        var expected = new Vector3(40, 0, 0) + Vector3.Cross(handoff.Angular, Vector3.Transform(localCenter, rotation));
        Near(handoff.Linear, expected);
        // Falsify using body.worldCenterOfMass - hull.position: a parked body
        // 396 m behind introduces a spurious ~158 m/s rotational component.
        sample.TryGet(1.06f, localCenter - new Vector3(396, 0, 0), out var stale, out _);
        Check(Vector3.Distance(stale, expected) > 100, "Stale COM translation failure not reproduced.");
        Near(handoff.AbsolutePose.Position, new Vector3(4002, 0, 0));
    }

    public static void PoseCancellation()
    {
        foreach (string scenario in new[] { "stale", "remote", "cancelled", "clock", "origin" })
        {
            var handoff = new RemoteBodyHandoff(Vector3.One, Quaternion.Identity, Vector3.UnitX, Vector3.Zero, 1);
            float now = scenario == "stale" ? 1.3f : scenario == "clock" ? .9f : 1.1f;
            Vector3 origin = scenario == "origin" ? new Vector3(float.NaN, 0, 0) : Vector3.Zero;
            Check(!handoff.TryTake(now, origin, scenario != "remote", true, scenario == "cancelled", out _) && handoff.Finished, scenario);
            Check(!handoff.TryTake(1.1f, Vector3.Zero, true, true, false, out _), "Cancelled pose revived: " + scenario);
        }
        var pending = new RemoteBodyHandoff(Vector3.One, Quaternion.Identity, Vector3.UnitX, Vector3.Zero, 1);
        Check(!pending.TryTake(1.02f, Vector3.Zero, true, false, false, out _) && !pending.Finished, "Unready body was moved.");
        Check(pending.TryTake(1.03f, Vector3.Zero, true, true, false, out _), "Ready body not aligned.");
        var sample = new RemoteShipMotion(); sample.Observe(Vector3.Zero, Quaternion.Identity, 1);
        Check(!sample.TryCapture(1.01f, Vector3.Zero, out _), "Single packet treated as motion.");
        sample.Observe(new Vector3(1000, 0, 0), Quaternion.Identity, 1.025f);
        Check(!sample.TryCapture(1.03f, Vector3.Zero, out _), "Teleport packet accepted for physical handoff.");
    }

    public sealed class Controller
    {
        public Vector3 WorldCache = Vector3.Zero, LocalCache = Vector3.Zero, Velocity;
        public Quaternion Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f);
        public bool Coasting = true, Server = true, Confirmed = true, Remote = false, Pilot, Docked = false, Enabled = true, MatchingBody = true;
        public bool Brake = true, RestoreWorld = true;
        public int Transition;
        public float Thrust;
        public bool Scoped => Coasting && Server && Confirmed && !Remote && !Pilot && !Docked && Enabled && MatchingBody;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Fixed()
        {
            if (Transition == 1) Velocity = Vector3.Transform(LocalCache, Rotation);
            if (Transition == 2) Velocity = Vector3.Transform(LocalCache, Rotation);
            if (RestoreWorld) Velocity = WorldCache;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forces()
        {
            if (Thrust != 0) Velocity += Vector3.UnitX * Thrust;
            else if (Velocity.LengthSquared() > .01f && Brake) Velocity *= .5f;
            else if (Brake) Velocity = Vector3.Zero;
        }
    }
    public static Vector3 Cache(Vector3 requested, object instance, bool local)
    {
        var controller = (Controller)instance;
        return controller.Scoped ? local ? Vector3.Transform(controller.Velocity, Quaternion.Inverse(controller.Rotation))
            : controller.Velocity : requested;
    }
    public static bool Brake(bool requested, object instance) => !((Controller)instance).Scoped && requested;
    public static IEnumerable<CodeInstruction> Caches(IEnumerable<CodeInstruction> code)
        => CoastingControllerPatch.RewriteCaches(code, typeof(Controller).GetField(nameof(Controller.WorldCache)),
            typeof(Controller).GetField(nameof(Controller.LocalCache)), typeof(ServerCoastingTests).GetMethod(nameof(Cache)));
    public static IEnumerable<CodeInstruction> Braking(IEnumerable<CodeInstruction> code)
        => CoastingControllerPatch.RewriteBraking(code, typeof(Controller).GetField(nameof(Controller.Brake)),
            typeof(ServerCoastingTests).GetMethod(nameof(Brake)));

    public static void NativeCacheAndCollision()
    {
        var controller = new Controller { Velocity = new Vector3(66.897f, 0, 0) };
        var method = typeof(Controller).GetMethod(nameof(Controller.Fixed));
        method.Invoke(controller, null); Near(controller.Velocity, Vector3.Zero); // Native first-step reset.
        var harmony = new Harmony("shipwalk.tests.coasting-cache");
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ServerCoastingTests), nameof(Caches)));
            foreach (int transition in new[] { 0, 1, 2 })
            {
                controller.Transition = transition; controller.Velocity = new Vector3(66.897f, 0, 0);
                method.Invoke(controller, null); Near(controller.Velocity, new Vector3(66.897f, 0, 0));
                // A collision changes live velocity. A saved exit velocity would
                // erase this impulse; the cache filters must preserve it instead.
                controller.Velocity = new Vector3(-3, 2, 1);
                method.Invoke(controller, null); Near(controller.Velocity, new Vector3(-3, 2, 1));
            }
            foreach (string scope in new[] { "Remote", "Pilot", "Docked", "Coasting", "Server", "Confirmed", "Enabled", "MatchingBody" })
            {
                var other = new Controller { Velocity = Vector3.One };
                var field = typeof(Controller).GetField(scope); field.SetValue(other, !(bool)field.GetValue(other));
                method.Invoke(other, null); Near(other.Velocity, Vector3.Zero);
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        controller.Velocity = Vector3.One; method.Invoke(controller, null); Near(controller.Velocity, Vector3.Zero);
    }

    public static void NativeAutomaticBraking()
    {
        var method = typeof(Controller).GetMethod(nameof(Controller.Forces));
        var controller = new Controller { Velocity = new Vector3(60, 0, 0) };
        method.Invoke(controller, null); Near(controller.Velocity, new Vector3(30, 0, 0));
        var harmony = new Harmony("shipwalk.tests.coasting-braking");
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ServerCoastingTests), nameof(Braking)));
            controller.Velocity = new Vector3(60, 0, 0); method.Invoke(controller, null); Near(controller.Velocity, new Vector3(60, 0, 0));
            controller.Thrust = 4; method.Invoke(controller, null); Near(controller.Velocity, new Vector3(64, 0, 0));
            controller.Thrust = 0; controller.Velocity = new Vector3(.05f, 0, 0);
            method.Invoke(controller, null); Near(controller.Velocity, new Vector3(.05f, 0, 0));
            Check(controller.Brake, "Native damping preference was mutated.");
            controller.Pilot = true; controller.Velocity = new Vector3(60, 0, 0);
            method.Invoke(controller, null); Near(controller.Velocity, new Vector3(30, 0, 0));
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        controller.Pilot = false; controller.Velocity = new Vector3(60, 0, 0);
        method.Invoke(controller, null); Near(controller.Velocity, new Vector3(30, 0, 0));
    }

    public static void ChangedControllerIL()
    {
        foreach (var rewrite in new Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>[] { Caches, Braking })
        {
            bool refused = false;
            try { rewrite(Array.Empty<CodeInstruction>()).ToArray(); } catch (NotSupportedException) { refused = true; }
            Check(refused, "Unknown controller IL accepted.");
        }
    }
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static void Near(Vector3 actual, Vector3 expected) => Check(Vector3.Distance(actual, expected) < .005f, "Expected " + expected + "; got " + actual);
    private static void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .005f, "Expected " + expected + "; got " + actual);
}
