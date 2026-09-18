using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ShipWalk
{
    // The standalone playfield executable has a separate obfuscation layout.
    // Translate only the reviewed bindings after identifying the exact binary.
    internal sealed partial class NativeBuildBindings
    {
        internal const string StandaloneHash = "3323156C21E7CE8558DC1235DA33405233E772C4CF867DED2B1CC77BB6823F4E";
        internal const string StandaloneMvid = "03cf9f7a-af5e-41be-9aa2-a93972062700";
        private readonly Assembly assembly;
        private readonly Dictionary<string, string> canonicalTypes;
        private readonly Dictionary<int, string> canonicalMethods;
        internal readonly bool Standalone;
        internal string ProfileName => Standalone ? "StandalonePlayfield5150" : "ClientCoop5150";

        internal NativeBuildBindings(Assembly assembly, bool playfieldServer)
        {
            this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            string hash;
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(assembly.Location))
                hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            Standalone = SelectStandalone(hash, assembly.ManifestModule.ModuleVersionId.ToString(), playfieldServer);
            if (!Standalone) return;
            canonicalTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in StandaloneTypes) canonicalTypes.Add(pair.Value, pair.Key);
            canonicalMethods = new Dictionary<int, string>();
            foreach (var pair in StandaloneMembers)
                if ((pair.Key & 0xFF000000) == 0x06000000)
                    canonicalMethods.Add(pair.Value.Token, pair.Value.Name);
        }

        internal static bool SelectStandalone(string hash, string mvid, bool playfieldServer)
        {
            if (hash == Build5150.Hash && mvid == Build5150.Mvid) return false;
            if (hash == StandaloneHash && mvid == StandaloneMvid && playfieldServer) return true;
            throw new NotSupportedException("Unsupported game binary for " + (playfieldServer ? "playfield worker" : "client")
                + ". ShipWalk supports the verified client/co-op and standalone playfield variants of build 5150."
                + " Actual SHA256=" + hash + "; MVID=" + mvid);
        }

        internal static int StandaloneToken(int canonical)
        {
            if (!StandaloneMembers.TryGetValue(canonical, out Member binding))
                throw new NotSupportedException("Unmapped standalone native member: " + canonical.ToString("X8"));
            return binding.Token;
        }

        internal static string StandaloneTypeName(string canonical)
        {
            if (StandaloneTypes.TryGetValue(canonical, out string actual)) return actual;
            if (canonical.StartsWith("Assembly-CSharp.", StringComparison.Ordinal))
                throw new NotSupportedException("Unmapped standalone native type: " + canonical);
            return canonical;
        }

        internal Type GetType(string canonical, bool throwOnError = true) =>
            assembly.GetType(Standalone ? StandaloneTypeName(canonical) : canonical, throwOnError);
        internal MethodBase ResolveMethod(int canonical) =>
            assembly.ManifestModule.ResolveMethod(Standalone ? StandaloneToken(canonical) : canonical);
        internal FieldInfo ResolveField(int canonical) =>
            assembly.ManifestModule.ResolveField(Standalone ? StandaloneToken(canonical) : canonical);

        internal string CanonicalTypeName(string actual)
        {
            if (!Standalone) return actual;
            return Regex.Replace(actual, @"[\w`-]+(?:[.+][\w`-]+)*",
                match => canonicalTypes.TryGetValue(match.Value, out string value) ? value : match.Value);
        }

        internal string CanonicalMethodName(MethodBase actual)
        {
            if (!Standalone) return actual.Name;
            if (actual.Module != assembly.ManifestModule || !canonicalMethods.TryGetValue(actual.MetadataToken, out string name))
                throw new NotSupportedException("Unmapped standalone native method signature: " + actual);
            return name;
        }

        private readonly struct Member
        {
            internal readonly int Token;
            internal readonly string Name;
            internal Member(int token, string name) { Token = token; Name = name; }
        }
    }
}
