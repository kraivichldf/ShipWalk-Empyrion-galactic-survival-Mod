using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class LocalControlRegressionTests
{
    internal static void ElevatorControls()
    {
        const float nativeSpeed = 5.5f;
        Vector3 up = LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitY, nativeSpeed, false);
        Near(up, Vector3.UnitY * (nativeSpeed / 1.5f));
        Near(LocalClimb.Velocity(Quaternion.Identity, -Vector3.UnitY, nativeSpeed, false), -up);
        Near(LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitY, nativeSpeed, true), Vector3.UnitY * nativeSpeed);
        Near(LocalClimb.Velocity(Quaternion.Identity, Vector3.Zero, nativeSpeed, false), Vector3.Zero);
        Near(LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitZ, nativeSpeed, false), Vector3.UnitZ * nativeSpeed / 2.25f);
        Quaternion heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI / 2f);
        Near(LocalClimb.Velocity(heading, Vector3.UnitZ, nativeSpeed, true), Vector3.UnitX * nativeSpeed / 1.5f);
        Near(LocalClimb.Velocity(heading, Vector3.UnitY, nativeSpeed, true), Vector3.UnitY * nativeSpeed);
    }

    internal static void ElevatorTransportAndCollision()
    {
        Vector3 shipVelocity = new Vector3(100.5f, 0, 0);
        Quaternion tiltedShip = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .75f);
        var pose = new LocalFramePose(new Vector3(40, 20, -9), tiltedShip);
        Vector3 requested = LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitY, 5.5f, false);
        Near(pose.ToWorldVelocity(requested, shipVelocity), shipVelocity + Vector3.Transform(requested, tiltedShip));
        // The solver blocks a ceiling. Publish accepted velocity, then leave the
        // elevator with that velocity rather than replaying an upward command.
        Vector3 accepted = Vector3.Zero;
        Vector3 world = pose.ToWorldVelocity(accepted, shipVelocity);
        Near(world, shipVelocity);
        Near(pose.ToLocalVelocity(world, shipVelocity), Vector3.Zero);
        Near(LocalClimb.Velocity(Quaternion.Identity, Vector3.Zero, 5.5f, false), Vector3.Zero);
    }

    internal static void ElevatorInvalidInput()
    {
        foreach (float speed in new[] { -1f, float.NaN, float.PositiveInfinity })
        {
            bool rejected = false;
            try { LocalClimb.Velocity(Quaternion.Identity, Vector3.UnitY, speed, false); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid native climb speed accepted.");
        }
    }

    internal static void FreeFlightLook()
    {
        foreach (float dt in new[] { 1f / 30f, 1f / 60f, 1f / 144f })
        {
            Check(LocalLook.TryRotation(new Vector3(0, 2 * dt * 4, 0), dt, out var yaw), "Yaw rejected.");
            Near(Vector3.Transform(Vector3.UnitZ, yaw), new Vector3((float)Math.Sin(2 * Math.PI / 180), 0, (float)Math.Cos(2 * Math.PI / 180)));
            Check(LocalLook.TryRotation(new Vector3(-2 * dt * 15, 0, 0), dt, out var pitch), "Pitch rejected.");
            Check(Vector3.Transform(Vector3.UnitZ, pitch).Y > 0f, "Native mouse pitch sign lost.");
            Check(LocalLook.TryRotation(new Vector3(0, 0, dt * 20), dt, out var roll), "Roll rejected.");
            Check(Vector3.Transform(Vector3.UnitY, roll).X < 0f, "Native roll sign lost.");
            Check(LocalLook.TryRotation(Vector3.Zero, dt, out var idle), "Idle rejected.");
            Near(Vector3.Transform(Vector3.UnitZ, idle), Vector3.UnitZ);
        }
    }

    internal static void LookDoesNotAccumulate()
    {
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f);
        Check(LocalLook.TryRotation(new Vector3(0, .08f, 0), .02f, out var turn), "Turn rejected.");
        orientation *= turn;
        Vector3 facing = Vector3.Transform(Vector3.UnitZ, orientation);
        for (int i = 0; i < 100; i++)
        {
            Check(LocalLook.TryRotation(Vector3.Zero, .02f, out var idle), "Idle rejected.");
            orientation *= idle;
        }
        Near(Vector3.Transform(Vector3.UnitZ, orientation), facing);
        foreach (float dt in new[] { 0f, -.1f, float.NaN })
            Check(!LocalLook.TryRotation(Vector3.One, dt, out _), "Invalid look timestep accepted.");
        Check(!LocalLook.TryRotation(new Vector3(float.NaN, 0, 0), .02f, out _), "Invalid look input accepted.");
    }

    public sealed class NativeBody
    {
        public Vector3 Velocity { get; set; }
        public Vector3 Angular { get; set; }
    }
    public sealed class NativeShip
    {
        public readonly NativeBody Body = new NativeBody();
        public int Before, After;
        public bool Pilot;
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void ShowComponent(bool pilot)
        {
            Before++;
            Pilot = pilot;
            if (!pilot)
            {
                Body.Velocity = Vector3.Zero;
                Body.Angular = Vector3.Zero;
            }
            After++;
        }
    }
    private static readonly SeatMotionScope<NativeBody> scopes = new SeatMotionScope<NativeBody>();
    private static readonly MethodInfo zero = typeof(Vector3).GetProperty(nameof(Vector3.Zero)).GetGetMethod();
    private static readonly MethodInfo linear = typeof(NativeBody).GetProperty(nameof(NativeBody.Velocity)).GetSetMethod();
    private static readonly MethodInfo angular = typeof(NativeBody).GetProperty(nameof(NativeBody.Angular)).GetSetMethod();
    public static IEnumerable<CodeInstruction> ResetTranspiler(IEnumerable<CodeInstruction> code)
        => SeatMotionPatch.Rewrite(code, zero, linear, angular,
            typeof(LocalControlRegressionTests).GetMethod(nameof(Linear)), typeof(LocalControlRegressionTests).GetMethod(nameof(Angular)));
    public static void Linear(NativeBody body, Vector3 value) { if (!scopes.Contains(body)) body.Velocity = value; }
    public static void Angular(NativeBody body, Vector3 value) { if (!scopes.Contains(body)) body.Angular = value; }

    internal static void PassengerResetPatch()
    {
        var ship = new NativeShip();
        Vector3 velocity = new Vector3(0, 0, 100.5f), spin = new Vector3(0, .005f, 0);
        ship.Body.Velocity = velocity;
        ship.ShowComponent(false);
        Near(ship.Body.Velocity, Vector3.Zero); // Recorded native failure.
        MethodInfo method = typeof(NativeShip).GetMethod(nameof(NativeShip.ShowComponent));
        var harmony = new Harmony("shipwalk.tests.passenger-reset");
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(LocalControlRegressionTests).GetMethod(nameof(ResetTranspiler))));
            ship.Body.Velocity = velocity; ship.Body.Angular = spin;
            using (scopes.Enter(ship.Body))
            {
                method.Invoke(ship, new object[] { false });
                Near(ship.Body.Velocity, velocity); Near(ship.Body.Angular, spin);
                Check(!ship.Pilot, "Passenger was incorrectly made pilot.");
                // A real collision changes motion even during this scope. The
                // patch must keep that change, not restore the starting speed.
                ship.Body.Velocity = new Vector3(0, 0, 12);
                method.Invoke(ship, new object[] { false });
                Near(ship.Body.Velocity, new Vector3(0, 0, 12));
                var other = new NativeShip(); other.Body.Velocity = velocity;
                method.Invoke(other, new object[] { false });
                Near(other.Body.Velocity, Vector3.Zero);
                method.Invoke(ship, new object[] { true });
                Check(ship.Pilot, "Native pilot state update lost.");
            }
            method.Invoke(ship, new object[] { false });
            Near(ship.Body.Velocity, Vector3.Zero); Near(ship.Body.Angular, Vector3.Zero);
            Check(ship.Before == 5 && ship.After == 5, "Native seat work skipped or duplicated.");
        }
        finally { scopes.Clear(); harmony.UnpatchAll(harmony.Id); }
    }

    internal static void SeatScopeCleanup()
    {
        var state = new SeatMotionScope<object>(); var body = new object();
        using (state.Enter(body))
        {
            using (state.Enter(body)) Check(state.Contains(body), "Nested scope lost.");
            Check(state.Contains(body), "Inner cleanup removed outer ownership.");
        }
        Check(!state.Contains(body), "Seat scope leaked.");
        try { using (state.Enter(body)) throw new InvalidOperationException("Native seat failure"); }
        catch (InvalidOperationException) { }
        Check(!state.Contains(body), "Exception leaked seat scope.");
        IDisposable stale = state.Enter(body);
        state.Clear();
        using (state.Enter(body))
        {
            stale.Dispose(); stale.Dispose();
            Check(state.Contains(body), "Old finalizer removed new seat ownership.");
        }
        Check(!state.Contains(body), "Clear/re-entry leaked seat scope.");
    }

    internal static void ResetPatchRejectsUnknownLayout()
    {
        CodeInstruction[] valid = { new CodeInstruction(OpCodes.Call, zero), new CodeInstruction(OpCodes.Callvirt, linear),
            new CodeInstruction(OpCodes.Call, zero), new CodeInstruction(OpCodes.Callvirt, angular) };
        foreach (IEnumerable<CodeInstruction> code in new[] {
            Array.Empty<CodeInstruction>(), valid.Take(2), valid.Concat(valid.Take(2)), valid.Skip(1) })
        {
            bool rejected = false;
            try { ResetTranspiler(code).ToArray(); } catch (NotSupportedException) { rejected = true; }
            Check(rejected, "Changed native reset site accepted.");
        }
        Check(ResetTranspiler(valid).Count() == 4, "Reset patch unexpectedly changes stack layout.");
    }
    private static void Near(Vector3 a, Vector3 b) => Check(Vector3.Distance(a, b) < .001f, "Expected " + b + "; actual " + a);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
