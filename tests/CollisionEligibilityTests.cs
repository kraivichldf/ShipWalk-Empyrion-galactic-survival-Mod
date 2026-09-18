using System;
using ShipWalk;

internal static class CollisionEligibilityTests
{
    internal static void RecordedCameraContact()
    {
        // Installed build-5150 matrix and the first 0.1.14 contact:
        // GunCamera (10) -> MSHull (22) excluded; PlayerPhysics (15) allowed.
        var camera = new CollisionLayers(10);
        var physics = new CollisionLayers(15);
        var hull = new CollisionLayers(22);
        int oldUnion = (1 << 10) | (1 << 15);
        var oldCopy = new CollisionLayers(2, oldUnion, ~oldUnion, 100);
        Check(CollisionEligibility.Allows(camera, oldCopy, false, false), "old union must reproduce the excluded camera contact");
        Check(!CollisionEligibility.Allows(camera, hull, false, false), "camera must stay excluded by native matrix");
        Check(CollisionEligibility.Allows(physics, hull, true, false), "real walking shape must still collide with hull");
        Check(!CollisionEligibility.Allows(physics, hull, true, true), "pre-existing pair ignore must be retained");
        Check(!CollisionEligibility.Supports(1.26318f), "recorded speculative contact is not floor support");
        Check(CollisionEligibility.Supports(0f) && CollisionEligibility.Supports(-.05f)
            && CollisionEligibility.Supports(.01f), "touching, overlapping and nearby floor contacts remain valid");
        Check(!CollisionEligibility.Supports(float.NaN) && !CollisionEligibility.Supports(float.PositiveInfinity), "invalid contacts cannot support player");
    }

    internal static void NativeOverrides()
    {
        var native = new CollisionLayers(22);
        var include = new CollisionLayers(15, 1 << 22);
        Check(CollisionEligibility.Allows(include, native, false, false), "one native include override enables pair");
        var exclude = new CollisionLayers(15, 1 << 22, 1 << 22);
        Check(!CollisionEligibility.Allows(exclude, native, true, false), "exclusion wins over inclusion on same shape");
        var higherHull = new CollisionLayers(22, 1 << 15, 0, 2);
        Check(CollisionEligibility.Allows(exclude, higherHull, true, false), "higher conflicting include priority wins");
        var higherPlayer = new CollisionLayers(15, 0, 1 << 22, 3);
        Check(!CollisionEligibility.Allows(higherPlayer, higherHull, true, false), "higher conflicting exclusion priority wins");
        var equalHull = new CollisionLayers(22, 1 << 15, 0, 3);
        Check(!CollisionEligibility.Allows(higherPlayer, equalHull, true, false), "equal conflicting priorities reject contact");
        Check(!CollisionEligibility.Allows(include, higherHull, false, true), "pair ignore wins even over layer overrides");
        Check(CollisionEligibility.Allows(native, include, false, false), "pair order must not change eligibility");
        Check(CollisionEligibility.Allows(new CollisionLayers(31), new CollisionLayers(15, 1 << 31), false, false), "layer 31 mask bit supported");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
