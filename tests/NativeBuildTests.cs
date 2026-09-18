using System;
using ShipWalk;

internal static class NativeBuildTests
{
    internal static void BothBuilds()
    {
        Check(!NativeBuildBindings.SelectStandalone(Build5150.Hash, Build5150.Mvid, false), "Client changed profile.");
        Check(!NativeBuildBindings.SelectStandalone(Build5150.Hash, Build5150.Mvid, true), "Co-op worker changed profile.");
        Check(NativeBuildBindings.SelectStandalone(NativeBuildBindings.StandaloneHash, NativeBuildBindings.StandaloneMvid, true),
            "Recorded standalone worker startup refusal was not fixed.");
    }
    internal static void ExactIdentityAndRole()
    {
        Refused(() => NativeBuildBindings.SelectStandalone(NativeBuildBindings.StandaloneHash, NativeBuildBindings.StandaloneMvid, false));
        Refused(() => NativeBuildBindings.SelectStandalone(Build5150.Hash, NativeBuildBindings.StandaloneMvid, true));
        Refused(() => NativeBuildBindings.SelectStandalone(NativeBuildBindings.StandaloneHash, Build5150.Mvid, true));
        Refused(() => NativeBuildBindings.SelectStandalone(new string('0', 64), NativeBuildBindings.StandaloneMvid, true));
        Refused(() => NativeBuildBindings.SelectStandalone(null, null, true));
    }
    internal static void NativeLayoutRegression()
    {
        // The uploaded worker uses the original rigidbody token for a bool.
        // Names also collide: its MenuOptions is a different native type.
        Check(NativeBuildBindings.StandaloneToken(0x04003E05) == 0x04003DD4, "Character rigidbody resolved to the worker's unrelated bool.");
        Check(NativeBuildBindings.StandaloneTypeName("Assembly-CSharp.MenuOptions") == "Assembly-CSharp.ConditionSettings", "Native entity type collision was ignored.");
        Check(NativeBuildBindings.StandaloneTypeName("UnityEngine.Vector3") == "UnityEngine.Vector3", "External type was renamed.");
        Refused(() => NativeBuildBindings.StandaloneToken(0x06000001));
        Refused(() => NativeBuildBindings.StandaloneTypeName("Assembly-CSharp.UnreviewedType"));
    }
    private static void Refused(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new Exception("Unverified native profile or binding was accepted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
