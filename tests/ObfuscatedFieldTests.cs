using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShipWalk;

internal static class ObfuscatedFieldTests
{
    internal static void Verify()
    {
        var name = new AssemblyName("ShipWalkFieldFixture" + Guid.NewGuid().ToString("N"));
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule(name.Name);
        var type = module.DefineType("ObfuscatedController", TypeAttributes.Public);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.DefineField("childMessage", typeof(int), FieldAttributes.Public);
        var objectField = type.DefineField("childMessage", typeof(string), FieldAttributes.Public);
        var method = type.DefineMethod("Read", MethodAttributes.Public, typeof(string), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, objectField);
        il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("Trim", Type.EmptyTypes));
        il.Emit(OpCodes.Ret);
        Type created = type.CreateType();
        MethodInfo read = created.GetMethod("Read");
        FieldInfo expected = created.GetFields().Single(f => f.FieldType == typeof(string));
        object instance = Activator.CreateInstance(created);
        expected.SetValue(instance, " correct field ");
        if ((string)read.Invoke(instance, null) != "correct field") throw new Exception("Fixture itself is invalid.");

        Assembly harmony = typeof(Harmony).Assembly;
        Type dmdType = harmony.GetType("MonoMod.Utils.DynamicMethodDefinition", true);
        using (var dmd = (IDisposable)Activator.CreateInstance(dmdType, new object[] { read }))
        {
            object definition = dmdType.GetProperty("Definition").GetValue(dmd);
            object body = Get(definition, "Body");
            var instruction = ((IEnumerable)Get(body, "Instructions")).Cast<object>()
                .Single(i => Get(Get(i, "OpCode"), "Name").ToString() == "ldfld");
            object reference = Get(instruction, "Operand");
            Type helper = harmony.GetType("MonoMod.Utils.ReflectionHelper", true);
            MethodInfo resolve = helper.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "ResolveReflection" && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.FullName == "Mono.Cecil.FieldReference");
            var actual = (FieldInfo)resolve.Invoke(null, new[] { reference });
            Console.WriteLine("REPRO field wanted=" + expected.FieldType + "; resolver selected=" + actual.FieldType);
            if (actual.FieldType == expected.FieldType) throw new Exception("The suspected field resolver failure was not reproduced.");
            using (var fix = new TypedFieldResolver(created.Assembly))
            {
                fix.Install();
                actual = (FieldInfo)resolve.Invoke(null, new[] { reference });
                if (actual != expected) throw new Exception("Typed resolver did not correct the cached wrong field.");
                MethodInfo generated = (MethodInfo)dmdType.GetMethod("Generate", Type.EmptyTypes).Invoke(dmd, null);
                if ((string)generated.Invoke(null, new[] { instance }) != "correct field")
                    throw new Exception("Re-emitted original returned the wrong field.");
                var hook = new Harmony("shipwalk.tests.obfuscated");
                try
                {
                    hook.Patch(read, postfix: new HarmonyMethod(typeof(ObfuscatedFieldTests).GetMethod(nameof(Postfix))));
                    if ((string)read.Invoke(instance, null) != "correct field:patched")
                        throw new Exception("Hooked obfuscated fixture failed.");
                }
                finally { hook.Unpatch(read, HarmonyPatchType.All, hook.Id); }
                if ((string)read.Invoke(instance, null) != "correct field") throw new Exception("Unpatch did not restore fixture behavior.");
            }
        }
    }

    public static void Postfix(ref string __result) => __result += ":patched";

    internal static void VerifyModifiers()
    {
        var name = new AssemblyName("ShipWalkModifierFixture" + Guid.NewGuid().ToString("N"));
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule(name.Name);
        var type = module.DefineType("ObfuscatedCollider", TypeAttributes.Public);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        Type[] none = Type.EmptyTypes, v = { typeof(IsVolatile) }, c = { typeof(IsConst) };
        Type[] vc = { typeof(IsVolatile), typeof(IsConst) }, cv = { typeof(IsConst), typeof(IsVolatile) };
        Type[][] required = { none, v, none, v, vc, cv, none, none };
        Type[][] optional = { none, none, v, c, none, none, vc, cv };
        var fieldTokens = new int[required.Length];
        for (int i = 0; i < required.Length; i++)
        {
            var field = type.DefineField("childMessage", typeof(bool), required[i], optional[i], FieldAttributes.Public);
            fieldTokens[i] = field.GetToken().Token;
            var method = type.DefineMethod("Read" + i, MethodAttributes.Public, typeof(bool), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            if (required[i].Contains(typeof(IsVolatile))) il.Emit(OpCodes.Volatile);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
        }
        Type created = type.CreateType();
        object instance = Activator.CreateInstance(created);
        FieldInfo[] fields = created.GetFields();
        Assembly harmony = typeof(Harmony).Assembly;
        Type dmdType = harmony.GetType("MonoMod.Utils.DynamicMethodDefinition", true);
        MethodInfo resolve = harmony.GetType("MonoMod.Utils.ReflectionHelper", true)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "ResolveReflection" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.FullName == "Mono.Cecil.FieldReference");
        using (var fix = new TypedFieldResolver(created.Assembly))
        {
            fix.Install();
            for (int i = 0; i < required.Length; i++)
            {
                FieldInfo expected = fields.Single(f => f.MetadataToken == fieldTokens[i]);
                foreach (FieldInfo field in fields) field.SetValue(instance, field == expected);
                MethodInfo read = created.GetMethod("Read" + i);
                if (!(bool)read.Invoke(instance, null)) throw new Exception("Modifier fixture read the wrong field: " + i);
                using (var dmd = (IDisposable)Activator.CreateInstance(dmdType, new object[] { read }))
                {
                    object body = Get(Get(dmd, "Definition"), "Body");
                    var instruction = ((IEnumerable)Get(body, "Instructions")).Cast<object>()
                        .Single(ins => Get(Get(ins, "OpCode"), "Name").ToString() == "ldfld");
                    object reference = Get(instruction, "Operand");
                    var actual = (FieldInfo)resolve.Invoke(null, new[] { reference });
                    if (actual != expected) throw new Exception("Resolver lost custom modifiers for fixture field " + i
                        + ": expected required=" + string.Join(",", expected.GetRequiredCustomModifiers().Select(t => t.Name))
                        + "; actual required=" + string.Join(",", actual.GetRequiredCustomModifiers().Select(t => t.Name))
                        + "; copied signature=" + Get(reference, "FullName"));
                    MethodInfo generated = (MethodInfo)dmdType.GetMethod("Generate", Type.EmptyTypes).Invoke(dmd, null);
                    if (!(bool)generated.Invoke(null, new[] { instance })) throw new Exception("Copied IL read the wrong modified field.");
                    var hook = new Harmony("shipwalk.tests.modifiers");
                    try
                    {
                        hook.Patch(read, postfix: new HarmonyMethod(typeof(ObfuscatedFieldTests).GetMethod(nameof(BooleanPostfix))));
                        if ((bool)read.Invoke(instance, null)) throw new Exception("Modifier fixture hook was not applied.");
                    }
                    finally { hook.Unpatch(read, HarmonyPatchType.All, hook.Id); }
                    if (!(bool)read.Invoke(instance, null)) throw new Exception("Modifier fixture unpatch changed original behavior.");
                }
            }
        }
        Console.WriteLine("PASS all 8 ordinary/required/optional/mixed/ordered modifier signatures");
    }

    public static void BooleanPostfix(ref bool __result) => __result = !__result;

    private static object Get(object instance, string name) => instance.GetType().GetProperty(name).GetValue(instance);
}
