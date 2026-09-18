using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class SeatMotionPatch
    {
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            MethodInfo zero, MethodInfo linear, MethodInfo angular, MethodInfo writeLinear, MethodInfo writeAngular)
        {
            var code = instructions.ToList();
            foreach (MethodInfo setter in new[] { linear, angular })
            {
                int[] sites = code.Select((instruction, index) => new { instruction, index })
                    .Where(e => e.instruction.Calls(setter)).Select(e => e.index).ToArray();
                if (sites.Length != 1 || sites[0] == 0 || !code[sites[0] - 1].Calls(zero))
                    throw new NotSupportedException("Native seat velocity reset no longer matches the verified build.");
            }
            foreach (CodeInstruction original in code)
            {
                var instruction = new CodeInstruction(original);
                if (instruction.Calls(linear) || instruction.Calls(angular))
                {
                    instruction.operand = instruction.Calls(linear) ? writeLinear : writeAngular;
                    instruction.opcode = OpCodes.Call;
                }
                yield return instruction;
            }
        }
    }
}
