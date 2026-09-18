namespace ShipWalk
{
    // Separate from support lifetime: record why support is lost, including the
    // following ticks, without enabling full-rate logging for the whole session.
    internal sealed class DiagnosticWindow
    {
        internal const float Duration = 1f;
        internal const int MaxCoreEvents = 512;
        internal const int MaxContactPairs = 128;
        internal const int ContactsPerStep = 2;
        private float started;
        private float contactStep;
        private int stepContacts;
        private bool opened;
        internal int CoreEvents { get; private set; }
        internal int ContactPairs { get; private set; }
        internal int DroppedCore { get; private set; }
        internal int DroppedContacts { get; private set; }
        internal int Events => CoreEvents + 2 * ContactPairs;

        internal void Open(float now)
        {
            started = now; CoreEvents = ContactPairs = DroppedCore = DroppedContacts = stepContacts = 0;
            contactStep = float.NegativeInfinity; opened = MotionMath.Finite(now);
        }
        internal bool Active(float now) => opened && MotionMath.Finite(now)
            && now >= started && now - started <= Duration;
        internal bool Take(float now)
        {
            if (!Active(now)) { Close(); return false; }
            if (CoreEvents >= MaxCoreEvents) { DroppedCore++; return false; }
            CoreEvents++; return true;
        }
        // Reserve a complete before/after callback pair; contact storms cannot
        // consume controller, limiter, support-loss or post-loss capacity.
        internal bool TakeContactPair(float now)
        {
            if (!Active(now)) { Close(); return false; }
            if (contactStep != now) { contactStep = now; stepContacts = 0; }
            if (stepContacts >= ContactsPerStep || ContactPairs >= MaxContactPairs)
            { DroppedContacts++; return false; }
            stepContacts++; ContactPairs++; return true;
        }
        internal void Close() { opened = false; }
    }
}
