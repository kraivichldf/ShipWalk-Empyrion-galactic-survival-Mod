namespace ShipWalk
{
    // Main-thread snapshots of the original shapes, before ShipWalk ignores
    // their native pairs. Never infer eligibility from a shared Rigidbody alone.
    internal readonly struct CollisionLayers
    {
        internal readonly int Layer, Include, Exclude, Priority;
        internal CollisionLayers(int layer, int include = 0, int exclude = 0, int priority = 0)
        { Layer = layer; Include = include; Exclude = exclude; Priority = priority; }
        internal bool Overrides => (Include | Exclude) != 0;
        internal bool Allows(int otherLayer, bool matrixAllows)
            => (matrixAllows || (Include & (1 << otherLayer)) != 0) && (Exclude & (1 << otherLayer)) == 0;
    }

    internal static class CollisionEligibility
    {
        // Unity 2022.3: exclusions win within one shape; conflicting overrides
        // use the higher collider priority, or reject if priorities are equal.
        internal static bool Allows(CollisionLayers first, CollisionLayers second, bool matrixAllows, bool pairIgnored)
        {
            if (pairIgnored) return false;
            bool a = first.Allows(second.Layer, matrixAllows), b = second.Allows(first.Layer, matrixAllows);
            if (a == b) return a;
            if (!first.Overrides) return b;
            if (!second.Overrides) return a;
            if (first.Priority == second.Priority) return false;
            return first.Priority > second.Priority ? a : b;
        }

        // Speculative CCD can report distant contacts. They may prevent future
        // impacts, but are not evidence of a floor under the character's feet.
        internal static bool Supports(float separation) => MotionMath.Finite(separation) && separation <= .02f;
    }
}
