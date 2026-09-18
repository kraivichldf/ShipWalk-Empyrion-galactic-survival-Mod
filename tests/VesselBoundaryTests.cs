using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections.Generic;
using HarmonyLib;
using ShipWalk;

internal static class VesselBoundaryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static VesselBoundaryGate gate;
    private static object owned, context;
    private static float now;
    private static Func<bool> continueTravel;
    private static int Filter(int result, object ship)
        => TravelRecoveryPatch.Filter(result, ReferenceEquals(ship, owned) && gate.Defer(ship, context, result, now, continueTravel));
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => TravelRecoveryPatch.Rewrite(instructions, typeof(VesselBoundaryTests).GetMethod(nameof(Filter), BindingFlags.Static | BindingFlags.NonPublic));
    public static void RecoveryRace()
    {
        var fixture = Assembly.Load("ShipWalk.NativeTravelFixture");
        var entity = fixture.GetType("Assembly-CSharp.MenuOptions"); var world = fixture.GetType("Assembly-CSharp.FunctionAttributeNodeCollection");
        var failure = fixture.GetType("EnumOutOfPlayfieldTypes"); var query = entity.GetMethod("SearchDatabase");
        var recover = entity.GetMethod("InsertBuilder"); var outcome = entity.GetField("BoundaryFailure"); var moved = world.GetField("Repositions");
        object ship = FormatterServices.GetUninitializedObject(entity), foreign = FormatterServices.GetUninitializedObject(entity);
        gate = new VesselBoundaryGate(); owned = ship; context = new object(); now = 1;
        int prepared = 0; bool committed = false;
        continueTravel = () => { if (prepared == 0) prepared++; return true; };
        var harmony = new Harmony("shipwalk.vessel.boundary.guard");
        try
        {
            harmony.Patch(query, transpiler: new HarmonyMethod(typeof(VesselBoundaryTests).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic)));
            foreach (var mode in new[] { PassengerMode.Elevator, PassengerMode.Walking, PassengerMode.Jumping, PassengerMode.Jetpack, PassengerMode.Seated })
            {
                gate.Observe(ship, 0); prepared = 0;
                foreach (int reason in new[] { 5, 2 })
                {
                    outcome.SetValue(ship, Enum.ToObject(failure, reason)); moved.SetValue(null, 0);
                    object result = query.Invoke(ship, null);
                    if (Convert.ToInt32(result) != 0) recover.Invoke(ship, new[] { result });
                    Check(prepared == 1 && (int)moved.GetValue(null) == 0, "Worker recovery raced preparation in " + mode);
                    now += .15f;
                    committed = Convert.ToInt32(query.Invoke(ship, null)) == 0;
                    Check(committed && prepared == 1, "Prepare/manager wait lost protection or sent a second transfer.");
                }
            }
            outcome.SetValue(foreign, Enum.ToObject(failure, 5));
            Check(Convert.ToInt32(query.Invoke(foreign, null)) == 5, "Foreign vessel recovery was suppressed.");
            foreach (int reason in new[] { 0, 1, 6, 7 })
            {
                outcome.SetValue(ship, Enum.ToObject(failure, reason));
                Check(Convert.ToInt32(query.Invoke(ship, null)) == reason, "Unrelated native recovery was suppressed.");
            }
            continueTravel = () => false; outcome.SetValue(ship, Enum.ToObject(failure, 5));
            Check(Convert.ToInt32(query.Invoke(ship, null)) == 5, "Lost membership still disabled native recovery.");
        }
        finally { harmony.UnpatchAll(harmony.Id); gate = null; owned = context = null; continueTravel = null; }
    }
    public static void BoundedCancellation()
    {
        var boundary = new VesselBoundaryGate(); object ship = new object(), world = new object(); int calls = 0;
        Func<bool> valid = () => { calls++; return true; };
        Check(boundary.Defer(ship, world, 5, 1, valid), "Fresh aboard crossing was refused.");
        Check(!boundary.Defer(ship, world, 5, 16, valid) && calls == 1, "Missing acknowledgement did not expire.");
        Check(!boundary.Defer(ship, world, 5, 50, valid) && !boundary.AllowsStart(ship, world, 50), "Expiry renewed itself while stuck at boundary.");
        boundary.Observe(ship, 0);
        Check(boundary.Defer(ship, world, 2, 51, valid), "Leaving and reaching a boundary again did not reset attempt.");
        boundary.Block(ship, world);
        Check(!boundary.Defer(ship, world, 2, 51.1f, valid), "Cancellation immediately restarted from the recovery tick.");
        boundary.Observe(ship, 0);
        Check(!boundary.Defer(ship, world, 5, 52, () => false), "Rejected roster was protected.");
        Check(!boundary.Defer(ship, world, 5, 52.1f, valid), "Rejected transfer retried indefinitely.");
        boundary.Prune((_, __) => false);
        Check(boundary.Defer(ship, world, 5, 53, valid), "Removed ship left a stale gate.");
        Check(boundary.Defer(ship, new object(), 5, 54, valid), "Destination context inherited source cancellation.");
        boundary.Observe(ship, 0);
        try { boundary.Defer(ship, world, 5, 55, () => throw new InvalidOperationException()); }
        catch (InvalidOperationException) { }
        Check(!boundary.Defer(ship, world, 5, 55.1f, valid), "Thrown preparation error did not restore recovery.");
        Check(!boundary.Defer(new object(), world, 5, float.NaN, valid), "Invalid clock accepted.");
    }
}
