using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    internal static class CoastingControllerPatch
    {
        internal static IEnumerable<CodeInstruction> RewriteCaches(IEnumerable<CodeInstruction> instructions,
            FieldInfo worldCache, FieldInfo localCache, MethodInfo filter)
        {
            var code = instructions.ToList();
            if (code.Count(i => i.LoadsField(worldCache)) != 1 || code.Count(i => i.LoadsField(localCache)) != 2)
                throw new NotSupportedException("Native ship velocity restore sites changed.");
            foreach (var instruction in code)
            {
                yield return instruction;
                bool local = instruction.LoadsField(localCache);
                if (!local && !instruction.LoadsField(worldCache)) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(local ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                yield return new CodeInstruction(OpCodes.Call, filter);
            }
        }

        internal static IEnumerable<CodeInstruction> RewriteBraking(IEnumerable<CodeInstruction> instructions,
            FieldInfo braking, MethodInfo filter)
        {
            var code = instructions.ToList();
            if (code.Count(i => i.LoadsField(braking)) != 2)
                throw new NotSupportedException("Native automatic thruster braking sites changed.");
            foreach (var instruction in code)
            {
                yield return instruction;
                if (!instruction.LoadsField(braking)) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, filter);
            }
        }
    }
}
