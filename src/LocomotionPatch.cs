using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class LocomotionPatch
    {
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            MethodInfo divide, MethodInfo filter)
        {
            var code = instructions.ToList();
            int[] sites = code.Select((instruction, index) => new { instruction, index })
                .Where(e => e.instruction.Calls(divide)).Select(e => e.index).ToArray();
            if (sites.Length != 1 || sites[0] + 1 >= code.Count
                || code[sites[0] + 1].opcode != OpCodes.Stloc_0)
                throw new NotSupportedException("Native locomotion sample no longer matches the verified build.");
            for (int i = 0; i < code.Count; i++)
            {
                yield return code[i];
                if (i != sites[0]) continue;
                // Existing displacement is on the stack. Retain the native
                // world-position history; change only these locomotion consumers.
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, filter);
            }
        }
    }
}
