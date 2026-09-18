namespace ShipWalk
{
    // User intent persists while standing, preparing, walking and reseating.
    // A failed preparation is attempted once per boarding/seat context, not
    // every frame. Enabling again explicitly retries the current context.
    internal sealed class LocalFrameControl
    {
        public bool Enabled { get; private set; }
        private int? observedShip;
        private bool observedSeated;
        private bool attempted;
        private bool automaticSuppressed, enabledAutomatically;

        public void Enable()
        { Enabled = true; attempted = false; automaticSuppressed = enabledAutomatically = false; }
        public void Disable()
        { Enabled = false; observedShip = null; attempted = false; automaticSuppressed = true; enabledAutomatically = false; }

        public bool TryEnableAutomatically(bool serverReady)
        {
            if (!serverReady || automaticSuppressed || Enabled) return false;
            Enable(); enabledAutomatically = true; return true;
        }

        public void ResetSession(bool multiplayer)
        {
            // A new world/server must complete its own handshake. Preserve an
            // explicit off across resets, and preserve manual single-player use.
            Enabled = Enabled && !multiplayer && !enabledAutomatically;
            enabledAutomatically = false;
            observedShip = null; observedSeated = false; attempted = false;
        }

        public bool ObserveContext(int? shipId, bool seated, bool hasSession)
        {
            if (observedShip != shipId || observedSeated != seated)
            { observedShip = shipId; observedSeated = seated; attempted = false; }
            if (!Enabled || !shipId.HasValue || hasSession || attempted) return false;
            attempted = true;
            return true;
        }
    }

    internal static class NativeCapsuleBinding
    {
        // The game's cached body and collider survive deactivation of Physics
        // while seated. Unity's live attachedRigidbody can then be null. Use the
        // nearest actual Rigidbody component, including inactive parents, for
        // ownership; still reject a conflicting non-null physics association.
        public static bool BelongsTo(int controllerBody, int hierarchyBody, int physicsBody)
            => controllerBody != 0 && hierarchyBody == controllerBody
                && (physicsBody == 0 || physicsBody == controllerBody);
    }
}
