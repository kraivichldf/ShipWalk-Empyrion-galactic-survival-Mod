using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShipWalk
{
    // The native collider selector uses activeSelf as its last-selection cache.
    // Keep that cache separate from the simulation body's actual activation.
    internal sealed class PhysicsActivity
    {
        public bool Requested { get; private set; }
        public PhysicsActivity(bool active) { Requested = active; }
        public bool Request(bool active) { Requested = active; return true; }
        // A pilot takes over an already live body. Replaying the old on-foot
        // selector request here would deactivate it in the middle of boarding.
        public bool Release(bool pilotTakingOver) => pilotTakingOver || Requested;
    }

    internal static class BodyActivationPatch
    {
        public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,
            FieldInfo root, MethodInfo gameObject, MethodInfo getActive, MethodInfo setActive,
            MethodInfo readHook, MethodInfo writeHook)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            int reads = 0, writes = 0;
            for (int i = 0; i < code.Count - 3; i++)
            {
                if (code[i].opcode != OpCodes.Ldarg_0 || !code[i + 1].LoadsField(root)
                    || !code[i + 2].Calls(gameObject)) continue;
                int call;
                MethodInfo replacement;
                if (code[i + 3].Calls(getActive)) { reads++; call = i + 3; replacement = readHook; }
                else if (i + 4 < code.Count && code[i + 3].opcode == OpCodes.Ldloc_1
                    && code[i + 4].Calls(setActive)) { writes++; call = i + 4; replacement = writeHook; }
                else continue;
                // Move branch/EH markers to the first new instruction.
                var owner = new CodeInstruction(OpCodes.Ldarg_0);
                owner.labels.AddRange(code[call].labels); code[call].labels.Clear();
                owner.blocks.AddRange(code[call].blocks); code[call].blocks.Clear();
                code[call].opcode = OpCodes.Call;
                code[call].operand = replacement;
                code.Insert(call, owner);
                i = call + 1;
            }
            if (reads != 1 || writes != 1)
                throw new NotSupportedException("Physics-root activation sites changed: reads=" + reads + "; writes=" + writes);
            return code;
        }
    }
}
