using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class TravelRecoveryPatch
    {
        // Build 5150: only these two native results reposition a player and
        // send WorldChangeCancel. An owned character crosses with its vessel.
        // Keep terrain, structure, ship eligibility and other entities native.
        internal static int Filter(int result, bool ownedBoundary)
            => ownedBoundary && (result == 2 || result == 5) ? 0 : result;

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodInfo filter)
        {
            int returns = 0;
            foreach (CodeInstruction source in instructions)
            {
                var instruction = new CodeInstruction(source);
                if (instruction.opcode == OpCodes.Ret)
                {
                    var actor = new CodeInstruction(OpCodes.Ldarg_0);
                    // Branches targeting a return must also run the filter.
                    instruction.MoveLabelsTo(actor); instruction.MoveBlocksTo(actor);
                    yield return actor;
                    yield return new CodeInstruction(OpCodes.Call, filter);
                    returns++;
                }
                yield return instruction;
            }
            if (returns == 0) throw new NotSupportedException("Native boundary recovery has no return site.");
        }
    }
}
