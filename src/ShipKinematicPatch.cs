using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class ShipKinematicPatch
    {
        // ViewContext's update can park an unpiloted ship as kinematic even
        // after ownership transfers. Redirect only its single native decision.
        public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source,
            MethodInfo getter, MethodInfo setter, MethodInfo read, MethodInfo write)
        {
            var code = source.ToList();
            if (code.Count(c => c.Calls(getter)) != 1 || code.Count(c => c.Calls(setter)) != 1)
                throw new NotSupportedException("Ship simulation-mode decision changed.");
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo target = instruction.Calls(getter) ? read : instruction.Calls(setter) ? write : null;
                if (target == null) { yield return instruction; continue; }
                var ship = new CodeInstruction(OpCodes.Ldarg_0);
                ship.labels.AddRange(instruction.labels);
                ship.blocks.AddRange(instruction.blocks);
                yield return ship;
                yield return new CodeInstruction(OpCodes.Call, target);
            }
        }
    }
}
