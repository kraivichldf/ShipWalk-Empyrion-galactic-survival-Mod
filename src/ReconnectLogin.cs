namespace ShipWalk
{
    // Looking for a saved passenger must not take ownership of normal movement.
    // Only an authenticated offer starts the bounded restoration phase.
    internal sealed class ReconnectLogin
    {
        private double started = -1, offered = -1;
        public bool Finished { get; private set; }
        public bool CancelPending { get; private set; }
        public bool Restoring => !Finished && offered >= 0;
        public bool Started => started >= 0;
        public double Age(double now) => Started ? System.Math.Max(0, now - started) : 0;
        public bool Begin(double now)
        {
            if (Finished || Started) return false;
            started = now; return true;
        }
        public bool AcceptOffer(double now)
        {
            if (Finished || Expired(now)) return false;
            if (offered < 0) offered = now;
            return true;
        }
        public bool Expired(double now) => !Finished && Started
            && (Restoring ? now - offered >= 90 : now - started >= 8);
        public void Finish() { Finished = true; CancelPending = false; }
        public void Cancel() { Finished = true; CancelPending = true; }
        public void Reset(bool disabled) { started = offered = -1; Finished = disabled; CancelPending = false; }
    }
}
