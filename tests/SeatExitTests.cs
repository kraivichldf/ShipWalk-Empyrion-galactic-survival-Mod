using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class SeatExitTests
{
    public sealed class Actor
    {
        public Vessel Seat;
        public bool Local = true;
        public float Health = 100f;
        public string SeatClass = "CockpitSS";
        public bool? OxygenTight = false;
    }

    public sealed class Vessel
    {
        public bool Moving = true, OtherExitLock;
        public string Kind = "CV";
        public int ExitRequests, MovementWarnings, OtherWarnings, Interactions;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsMoving() => Moving;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Activate(Actor actor, bool modifier = false)
        {
            if (!ReferenceEquals(actor.Seat, this)) { Interactions++; return; }
            if (OtherExitLock) { OtherWarnings++; return; }
            bool moving = IsMoving();
            if (moving) { MovementWarnings++; return; }
            // Stand-in for the existing exit request. The patch must reach this path.
            ExitRequests++;
            actor.Seat = null;
        }
    }

    private static RunMode mode;
    private static bool enabled;
    private static readonly MethodInfo query = typeof(Vessel).GetMethod(nameof(Vessel.IsMoving));

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => SeatExitPatch.Rewrite(instructions, query, typeof(SeatExitTests).GetMethod(nameof(Filter)));

    public static bool Filter(bool moving, object ship, object actor)
    {
        var vessel = (Vessel)ship;
        var occupant = (Actor)actor;
        return moving && !SeatExitPolicy.Allows(mode, enabled, occupant.Local,
            ReferenceEquals(occupant.Seat, vessel), vessel.Kind, occupant.Health,
            SeatExitPolicy.IsOpenSeat(occupant.SeatClass, occupant.OxygenTight));
    }

    internal static void Verify()
    {
        foreach (string kind in new[] { "CV", "SV", "HV" }) VerifyForKind(kind);
    }

    private static void VerifyForKind(string vesselType)
    {
        var vessel = new Vessel { Kind = vesselType };
        var actor = new Actor { Seat = vessel };
        MethodInfo method = typeof(Vessel).GetMethod(nameof(Vessel.Activate));
        void Activate() => method.Invoke(vessel, new object[] { actor, false });
        void Expect(bool expected, string scenario)
        {
            actor.Seat = vessel;
            int before = vessel.ExitRequests;
            Activate();
            if ((vessel.ExitRequests == before + 1) != expected || (actor.Seat == null) != expected)
                throw new Exception("Seat exit failed for " + vesselType + ": " + scenario);
            if (!vessel.IsMoving()) throw new Exception("Seat-exit patch changed actual ship motion.");
        }
        mode = RunMode.Experimental;
        enabled = true;
        Expect(false, "unpatched moving ship blocks normal exit");
        var harmony = new Harmony("shipwalk.tests.seatexit");
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(SeatExitTests).GetMethod(nameof(Transpiler))));
            Expect(true, "local occupied moving vessel reaches existing exit request");
            actor.OxygenTight = true;
            Expect(false, "enclosed cockpit retains movement restriction");
            actor.OxygenTight = null;
            Expect(false, "unclassified cockpit retains movement restriction");
            actor.SeatClass = "PassengerSeat";
            actor.OxygenTight = true;
            Expect(false, "enclosed passenger seat retains movement restriction");
            actor.OxygenTight = false;
            Expect(true, "open passenger seat reaches existing exit request");
            actor.SeatClass = "CockpitSS";
            mode = RunMode.Diagnostics;
            Expect(false, "diagnostics preserves movement gate");
            mode = RunMode.Off;
            Expect(false, "off preserves movement gate");
            mode = RunMode.Experimental;
            enabled = false;
            Expect(false, "seat-exit option off preserves movement gate");
            enabled = true;
            actor.Local = false;
            Expect(false, "remote actor is unchanged");
            actor.Local = true;
            actor.Health = 0f;
            Expect(false, "dead actor is unchanged");
            actor.Health = float.NaN;
            Expect(false, "invalid actor health is refused");
            actor.Health = 100f;
            foreach (string kind in new[] { "BA", "Player", "Unknown", "", null })
            {
                vessel.Kind = kind;
                Expect(false, "unsupported vessel type: " + kind);
            }
            vessel.Kind = vesselType;
            vessel.OtherExitLock = true;
            Expect(false, "other exit restriction is preserved");
            if (vessel.OtherWarnings != 1) throw new Exception("Other restriction was bypassed.");
            vessel.OtherExitLock = false;
            actor.Seat = new Vessel();
            int previousExits = vessel.ExitRequests;
            Activate();
            if (vessel.Interactions != 1 || vessel.ExitRequests != previousExits)
                throw new Exception("Unrelated activation was changed.");
            if (!Filter(true, vessel, actor)) throw new Exception("Different seat owner was accepted.");
            // Enclosed seats can still exit normally when stationary.
            actor.OxygenTight = true;
            vessel.Moving = false;
            actor.Seat = vessel;
            Activate();
            if (actor.Seat != null || vessel.ExitRequests != previousExits + 1)
                throw new Exception("Stationary enclosed cockpit exit changed.");
            previousExits = vessel.ExitRequests;
            // Normal stationary exit remains available even when the mod is off.
            mode = RunMode.Off;
            vessel.Moving = false;
            actor.Seat = vessel;
            Activate();
            if (actor.Seat != null || vessel.ExitRequests != previousExits + 1)
                throw new Exception("Stationary exit changed.");
            vessel.Moving = true;
            mode = RunMode.Experimental;
            actor.OxygenTight = false;
        }
        finally { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
        Expect(false, "unpatch restores original moving-seat restriction");
    }

    internal static void OpenSeatClassification()
    {
        void Expect(string seatClass, bool? tight, bool expected)
        {
            if (SeatExitPolicy.IsOpenSeat(seatClass, tight) != expected)
                throw new Exception("Wrong open-seat classification: " + seatClass + "/" + tight);
        }
        Expect("CockpitMS", null, true); // Stock CV pilot station omits IsOxygenTight.
        Expect("CockpitMS", false, true);
        Expect("CockpitMS", true, false);
        Expect("CockpitSS", false, true); // CockpitOpenSV / CockpitOpen2SV.
        Expect("CockpitSS", true, false); // Enclosed SV/HV cockpits.
        Expect("CockpitSS", null, false);
        Expect("PassengerSeat", false, true); // PassengerSeatMS / PassengerSeat2SV.
        Expect("PassengerSeat", true, false); // PassengerSeatSV / legacy enclosed seats.
        Expect("PassengerSeat", null, false);
        Expect("Container", false, false);
        Expect("", false, false);
        Expect(null, false, false);
    }

    internal static void RejectUnknownIL()
    {
        var filter = typeof(SeatExitTests).GetMethod(nameof(Filter));
        foreach (int count in new[] { 0, 2 })
        {
            var code = new List<CodeInstruction>();
            for (int i = 0; i < count; i++)
            {
                code.Add(new CodeInstruction(OpCodes.Ldarg_0));
                code.Add(new CodeInstruction(OpCodes.Call, query));
            }
            bool rejected = false;
            try { SeatExitPatch.Rewrite(code, query, filter).ToArray(); }
            catch (NotSupportedException) { rejected = true; }
            if (!rejected) throw new Exception("Unexpected seat-exit IL was accepted: count=" + count);
        }
    }
}
