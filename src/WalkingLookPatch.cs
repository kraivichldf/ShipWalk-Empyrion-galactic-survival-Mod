using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class WalkingLookPatch
    {
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            MethodInfo moveRotation, MethodInfo writeRotation)
        {
            var code = instructions.ToList();
            if (code.Count(i => i.Calls(moveRotation)) != 1)
                throw new NotSupportedException("Native walking look no longer has one verified rotation request.");
            foreach (CodeInstruction original in code)
            {
                var instruction = new CodeInstruction(original);
                if (instruction.Calls(moveRotation))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = writeRotation;
                }
                yield return instruction;
            }
        }
    }
}
