using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ShipWalk;

internal static class GenericFieldTests
{
    // Build 5150 ViewDictionary.BuildSelection, IL_01ae: List<!1>
    // OptionsLoaderNodeCollection<int, MenuOptions>::childMessage. Both generic
    // parameters are named A, and a Dictionary<!0,!1> field has the same name.
    internal static void RecordedSeatField()
    {
        VerifyFields(false);
    }

    internal static void NestedSignatures()
    {
        VerifyFields(true);
    }

    private static void VerifyFields(bool nested)
    {
        var name = new AssemblyName("ShipWalkGenericFixture" + Guid.NewGuid().ToString("N"));
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule(name.Name);
        var builder = module.DefineType("GenericOwner`2", TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        var parameters = builder.DefineGenericParameters("A", "A");
        Type first = parameters[0], second = parameters[1];
        Type[] signatures = nested ? new[] {
            first, second, first.MakeArrayType(), second.MakeArrayType(2),
            typeof(Dictionary<,>).MakeGenericType(first,
                typeof(List<>).MakeGenericType(second.MakeArrayType()).MakeArrayType())
        } : new[] {
            typeof(Dictionary<,>).MakeGenericType(first, second),
            typeof(List<>).MakeGenericType(second)
        };
        int[] tokens = signatures.Select(t => builder.DefineField("childMessage", t, FieldAttributes.Public).GetToken().Token).ToArray();
        Type definition = builder.CreateType();
        // Different closed owners must not share a resolved/cached field.
        Type[][] arguments = { new[] { typeof(int), typeof(string) }, new[] { typeof(long), typeof(object) },
            new[] { typeof(int), typeof(int) } };
        using (var fix = new TypedFieldResolver(assembly))
        {
            fix.Install();
            for (int ownerIndex = 0; ownerIndex < arguments.Length; ownerIndex++)
            {
                Type owner = definition.MakeGenericType(arguments[ownerIndex]);
                object instance = Activator.CreateInstance(owner);
                for (int fieldIndex = 0; fieldIndex < tokens.Length; fieldIndex++)
                {
                    FieldInfo field = owner.GetFields().Single(f => f.MetadataToken == tokens[fieldIndex]);
                    object expected = MakeValue(field.FieldType);
                    field.SetValue(instance, expected);
                    var reader = module.DefineType("Reader" + ownerIndex + "_" + fieldIndex, TypeAttributes.Public);
                    var method = reader.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static,
                        typeof(object), new[] { owner });
                    method.SetImplementationFlags(MethodImplAttributes.NoInlining);
                    var il = method.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, field);
                    if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
                    il.Emit(OpCodes.Ret);
                    MethodInfo read = reader.CreateType().GetMethod("Read");
                    AssertValue(read, instance, expected);
                    VerifyCopiedField(read, field, instance, expected);
                    var hook = new Harmony("shipwalk.tests.generic");
                    try
                    {
                        hook.Patch(read, postfix: new HarmonyMethod(typeof(GenericFieldTests).GetMethod(nameof(Postfix))));
                        if (!Equals(read.Invoke(null, new[] { instance }), "hook executed"))
                            throw new Exception("Generic-field hook did not execute.");
                    }
                    finally { hook.Unpatch(read, HarmonyPatchType.All, hook.Id); }
                    AssertValue(read, instance, expected);
                }
            }
        }
    }

    private static void VerifyCopiedField(MethodInfo read, FieldInfo expectedField, object instance, object expected)
    {
        Assembly harmony = typeof(Harmony).Assembly;
        Type dmdType = harmony.GetType("MonoMod.Utils.DynamicMethodDefinition", true);
        MethodInfo resolve = harmony.GetType("MonoMod.Utils.ReflectionHelper", true)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "ResolveReflection" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.FullName == "Mono.Cecil.FieldReference");
        using (var dmd = (IDisposable)Activator.CreateInstance(dmdType, new object[] { read }))
        {
            object body = Get(Get(dmd, "Definition"), "Body");
            object instruction = ((IEnumerable)Get(body, "Instructions")).Cast<object>()
                .Single(i => Get(Get(i, "OpCode"), "Name").ToString() == "ldfld");
            object reference = Get(instruction, "Operand");
            Console.WriteLine("GENERIC operand " + Get(reference, "FullName"));
            if (!(bool)Get(Get(reference, "FieldType"), "ContainsGenericParameter"))
                throw new Exception("Fixture failed to retain the unbound generic field signature from the game.");
            var actual = (FieldInfo)resolve.Invoke(null, new[] { reference });
            if (actual != expectedField) throw new Exception("Generic field resolved to the wrong owner/type/token.");
            MethodInfo generated = (MethodInfo)dmdType.GetMethod("Generate", Type.EmptyTypes).Invoke(dmd, null);
            AssertValue(generated, instance, expected);
        }
    }

    private static object MakeValue(Type type)
    {
        if (type == typeof(int)) return 173;
        if (type == typeof(long)) return 319L;
        if (type == typeof(string)) return "passenger";
        if (type.IsArray) return Array.CreateInstance(type.GetElementType(), Enumerable.Repeat(1, type.GetArrayRank()).ToArray());
        return Activator.CreateInstance(type);
    }

    private static void AssertValue(MethodInfo read, object instance, object expected)
    {
        if (!Equals(read.Invoke(null, new[] { instance }), expected)) throw new Exception("Generic fixture read the wrong value.");
    }

    public static void Postfix(ref object __result) => __result = "hook executed";
    private static object Get(object instance, string name) => instance.GetType().GetProperty(name).GetValue(instance);
}
