using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class BlockContactPatch
    {
        // Both retained native methods receive MenuOptions actor as argument 1.
        // Supply it explicitly to the adapter at the conversion call; do not
        // patch OptionsScope's general conversions or carry a scope into callbacks.
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            MethodInfo conversion, MethodInfo adapter, int expectedSites)
        {
            var code = instructions.ToList();
            if (code.Count(i => i.Calls(conversion)) != expectedSites)
                throw new NotSupportedException("Native block-contact conversion sites changed.");
            foreach (CodeInstruction original in code)
            {
                var instruction = new CodeInstruction(original);
                if (instruction.Calls(conversion))
                {
                    var actor = new CodeInstruction(OpCodes.Ldarg_1);
                    actor.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    actor.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                    yield return actor;
                    instruction.opcode = OpCodes.Call; instruction.operand = adapter;
                }
                yield return instruction;
            }
        }
    }
}
