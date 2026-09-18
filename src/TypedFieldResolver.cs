using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShipWalk
{
    // Harmony 2.3.6's bundled MonoMod resolver matches fields by name/owner, ignoring
    // field type and custom modifiers. Empyrion has same-name fields with different signatures.
    // Resolve those references before MonoMod's cache/name matcher can choose the wrong one.
    // This changes no DLL and applies only to the explicitly verified game module.
    internal sealed class TypedFieldResolver : IDisposable
    {
        private const string PatchId = "privatecoop.shipwalk.typedfields";
        private static TypedFieldResolver active;
        private readonly Harmony harmony = new Harmony(PatchId);
        private readonly string assemblyName;
        private readonly Guid moduleId;
        private readonly Type fieldReference;
        private readonly Type requiredModifier, optionalModifier;
        private readonly Type genericParameter, genericInstance, arrayType, byReferenceType, pointerType;
        private readonly PropertyInfo declaringType, fieldName, fieldType;
        private readonly PropertyInfo modifierType, modifierElement;
        private readonly PropertyInfo containsGenericParameter, parameterPosition, parameterKind;
        private readonly PropertyInfo genericArguments, elementType, arrayRank, arrayIsVector;
        private readonly MethodInfo resolveType, resolver;
        private bool installed;

        public TypedFieldResolver(Assembly target)
        {
            assemblyName = target.FullName;
            moduleId = target.ManifestModule.ModuleVersionId;
            Assembly library = typeof(Harmony).Assembly;
            Type reference = library.GetType("Mono.Cecil.MemberReference", true);
            fieldReference = library.GetType("Mono.Cecil.FieldReference", true);
            Type typeReference = library.GetType("Mono.Cecil.TypeReference", true);
            genericParameter = library.GetType("Mono.Cecil.GenericParameter", true);
            genericInstance = library.GetType("Mono.Cecil.GenericInstanceType", true);
            arrayType = library.GetType("Mono.Cecil.ArrayType", true);
            byReferenceType = library.GetType("Mono.Cecil.ByReferenceType", true);
            pointerType = library.GetType("Mono.Cecil.PointerType", true);
            containsGenericParameter = typeReference.GetProperty("ContainsGenericParameter");
            parameterPosition = genericParameter.GetProperty("Position");
            parameterKind = genericParameter.GetProperty("Type");
            genericArguments = genericInstance.GetProperty("GenericArguments");
            elementType = library.GetType("Mono.Cecil.TypeSpecification", true).GetProperty("ElementType");
            arrayRank = arrayType.GetProperty("Rank");
            arrayIsVector = arrayType.GetProperty("IsVector");
            Type helper = library.GetType("MonoMod.Utils.ReflectionHelper", true);
            declaringType = reference.GetProperty("DeclaringType");
            fieldName = reference.GetProperty("Name");
            fieldType = fieldReference.GetProperty("FieldType");
            requiredModifier = library.GetType("Mono.Cecil.RequiredModifierType", true);
            optionalModifier = library.GetType("Mono.Cecil.OptionalModifierType", true);
            Type modifier = library.GetType("Mono.Cecil.IModifierType", true);
            modifierType = modifier.GetProperty("ModifierType");
            modifierElement = modifier.GetProperty("ElementType");
            resolveType = helper.GetMethod("ResolveReflection", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeReference }, null);
            resolver = helper.GetMethod("_ResolveReflection", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { reference, typeof(Module[]) }, null);
            if (declaringType == null || fieldName == null || fieldType == null || modifierType == null
                || modifierElement == null || resolveType == null || resolver == null
                || containsGenericParameter == null || parameterPosition == null || parameterKind == null
                || genericArguments == null || elementType == null || arrayRank == null || arrayIsVector == null)
                throw new NotSupportedException("The bundled Harmony field resolver has an unexpected signature.");
        }

        public void Install()
        {
            if (active != null) throw new InvalidOperationException("Typed field resolver already active.");
            active = this;
            try
            {
                harmony.Patch(resolver, prefix: new HarmonyMethod(typeof(TypedFieldResolver).GetMethod(nameof(Prefix))));
                installed = true;
            }
            catch { active = null; throw; }
        }

        public static bool Prefix(object __0, ref MemberInfo __result)
        {
            TypedFieldResolver fix = active;
            if (fix == null || __0 == null || !fix.fieldReference.IsInstanceOfType(__0)) return true;
            object ownerReference = fix.declaringType.GetValue(__0);
            if (ownerReference == null) return true;
            // Resolving a TYPE re-enters this prefix, which immediately defers to MonoMod.
            var owner = (Type)fix.resolveType.Invoke(null, new[] { ownerReference });
            if (owner.Assembly.FullName != fix.assemblyName || owner.Module.ModuleVersionId != fix.moduleId) return true;
            object signature = fix.fieldType.GetValue(__0);
            // Match definition parameters before closing the field. Otherwise !0
            // and !1 collapse to the same type when both owner arguments are int.
            Type declaration = owner.IsGenericType && (bool)fix.containsGenericParameter.GetValue(signature)
                ? owner.GetGenericTypeDefinition() : owner;
            Type[] ownerArguments = declaration.GetGenericArguments();
            var required = new List<Type>();
            var optional = new List<Type>();
            // ResolveReflection(TypeReference) discards modreq/modopt. Capture them first:
            // in build 5150 a plain bool and a volatile bool share the name childMessage.
            while (fix.requiredModifier.IsInstanceOfType(signature) || fix.optionalModifier.IsInstanceOfType(signature))
            {
                var modifiers = fix.requiredModifier.IsInstanceOfType(signature) ? required : optional;
                modifiers.Add(fix.ResolveSignature(fix.modifierType.GetValue(signature), ownerArguments));
                signature = fix.modifierElement.GetValue(signature);
            }
            // Harmony's MMReflectionImporter wraps each reflection modifier in order;
            // walking those wrappers visits the last modifier first.
            required.Reverse();
            optional.Reverse();
            Type expectedType = fix.ResolveSignature(signature, ownerArguments);
            string name = (string)fix.fieldName.GetValue(__0);
            const BindingFlags fields = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            FieldInfo[] candidates = declaration.GetFields(fields)
                .Where(f => f.Name == name && f.FieldType == expectedType
                    && f.GetRequiredCustomModifiers().SequenceEqual(required)
                    && f.GetOptionalCustomModifiers().SequenceEqual(optional)).ToArray();
            if (candidates.Length != 1)
                throw new AmbiguousMatchException("ShipWalk could not uniquely resolve field " + owner.FullName + "::" + name
                    + " (" + expectedType + "; required=" + string.Join(",", required.Select(t => t.FullName))
                    + "; optional=" + string.Join(",", optional.Select(t => t.FullName)) + "; matches=" + candidates.Length + ")");
            __result = declaration == owner ? candidates[0]
                : owner.GetFields(fields).Single(f => f.MetadataToken == candidates[0].MetadataToken);
            return false;
        }

        private Type ResolveSignature(object signature, Type[] ownerArguments)
        {
            // A field on a CLOSED generic owner still has a DEFINITION signature:
            // List<!1>, not List<MenuOptions>. MonoMod cannot resolve !1 alone.
            // Build 5150 also calls both parameters A, so names are not identities.
            if (genericParameter.IsInstanceOfType(signature))
            {
                int position = (int)parameterPosition.GetValue(signature);
                if (parameterKind.GetValue(signature).ToString() != "Type"
                    || position < 0 || position >= ownerArguments.Length)
                    throw new NotSupportedException("ShipWalk field generic parameter has no declaring-type argument: " + signature);
                return ownerArguments[position];
            }
            if (!(bool)containsGenericParameter.GetValue(signature))
                return (Type)resolveType.Invoke(null, new[] { signature });
            if (genericInstance.IsInstanceOfType(signature))
            {
                var definition = (Type)resolveType.Invoke(null, new[] { elementType.GetValue(signature) });
                Type[] arguments = ((IEnumerable)genericArguments.GetValue(signature)).Cast<object>()
                    .Select(argument => ResolveSignature(argument, ownerArguments)).ToArray();
                return definition.MakeGenericType(arguments);
            }
            if (arrayType.IsInstanceOfType(signature))
            {
                Type element = ResolveSignature(elementType.GetValue(signature), ownerArguments);
                return (bool)arrayIsVector.GetValue(signature) ? element.MakeArrayType()
                    : element.MakeArrayType((int)arrayRank.GetValue(signature));
            }
            if (byReferenceType.IsInstanceOfType(signature))
                return ResolveSignature(elementType.GetValue(signature), ownerArguments).MakeByRefType();
            if (pointerType.IsInstanceOfType(signature))
                return ResolveSignature(elementType.GetValue(signature), ownerArguments).MakePointerType();
            if (requiredModifier.IsInstanceOfType(signature) || optionalModifier.IsInstanceOfType(signature))
                return ResolveSignature(modifierElement.GetValue(signature), ownerArguments);
            throw new NotSupportedException("ShipWalk cannot resolve generic field signature: " + signature);
        }

        public void Dispose()
        {
            if (!installed) return;
            // Call only after the game's hooks have been removed: unpatching also copies IL.
            harmony.Unpatch(resolver, HarmonyPatchType.All, PatchId);
            installed = false;
            if (ReferenceEquals(active, this)) active = null;
        }
    }
}
