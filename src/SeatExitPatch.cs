using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class SeatExitPolicy
    {
        internal static bool IsOpenSeat(string seatClass, bool? oxygenTight)
        {
            // CV cockpit blocks are open pilot stations by definition. SV/HV
            // cockpits and passenger seats must explicitly be non-airtight.
            if (seatClass == "CockpitMS") return oxygenTight != true;
            return (seatClass == "CockpitSS" || seatClass == "PassengerSeat") && oxygenTight == false;
        }

        internal static bool Allows(RunMode mode, bool enabled, bool localPlayer,
            bool seatedOnShip, string vesselType, float health, bool openSeat)
            => mode == RunMode.Experimental && enabled && localPlayer && seatedOnShip
                && (vesselType == "CV" || vesselType == "SV" || vesselType == "HV") && health > 0f && openSeat;
    }

    internal static class SeatExitPatch
    {
        // Filter only the moving-state query in the normal seat-exit method. Never
        // change the ship's state or replace the game's detach/network operations.
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            MethodInfo movingQuery, MethodInfo filter)
        {
            var code = instructions.ToList();
            int[] matches = code.Select((instruction, index) => new { instruction, index })
                .Where(entry => entry.instruction.Calls(movingQuery)).Select(entry => entry.index).ToArray();
            if (matches.Length != 1 || matches[0] == 0 || code[matches[0] - 1].opcode != OpCodes.Ldarg_0)
                throw new NotSupportedException("Seat-exit moving-state query does not match the verified build.");
            int site = matches[0];
            for (int i = 0; i < code.Count; i++)
            {
                yield return code[i];
                if (i != site) continue;
                // Original bool result, then ship (this) and actor (first argument).
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, filter);
            }
        }
    }
}
