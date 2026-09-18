using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipWalk
{
    internal enum TravelPhase { Preparing, Persisting, Committed, Cancelled }

    // One immutable roster per ship transfer. Only an authenticated member may
    // replace their own local pose, and only before the manifest is persisted.
    internal sealed class TravelTransfer
    {
        public TravelPacket Packet { get; }
        public TravelPhase Phase { get; private set; }
        private readonly HashSet<int> ready = new HashSet<int>();
        public bool AllReady => ready.Count == Packet.Members.Length;
        public TravelTransfer(TravelPacket packet)
        {
            if (!TravelProtocol.Valid(packet) || packet.Members.Length == 0 || !packet.Members.Any(m => m.Actor == packet.Leader))
                throw new ArgumentException("Transfer requires a validated leader and aboard roster.");
            Packet = packet.Copy(); Phase = TravelPhase.Preparing;
        }
        public bool Ready(int authenticatedActor, TravelPacket answer, Func<TravelMember, bool> validate)
        {
            if (Phase != TravelPhase.Preparing || answer.Kind != TravelKind.Ready || !SameRoute(Packet, answer)
                || answer.Members.Length != 1 || answer.Members[0].Actor != authenticatedActor) return false;
            int slot = Array.FindIndex(Packet.Members, m => m.Actor == authenticatedActor);
            TravelMember member = answer.Members[0];
            if (slot < 0 || !TravelProtocol.MemberValid(member) || member.Ship != Packet.Ship
                || (Packet.Members[slot].Mode == PassengerMode.Seated) != (member.Mode == PassengerMode.Seated)
                || !validate(member)) return false;
            if (ready.Contains(authenticatedActor)) return true; // Retries cannot mutate an accepted pose.
            Packet.Members[slot] = member.Copy(); ready.Add(authenticatedActor); return true;
        }
        public bool Persist()
        {
            if (Phase != TravelPhase.Preparing || !AllReady) return false;
            Phase = TravelPhase.Persisting; Packet.Kind = TravelKind.Manifest; return true;
        }
        public bool Stored(TravelPacket acknowledgement)
            => Phase == TravelPhase.Persisting && acknowledgement.Kind == TravelKind.Stored && SameManifest(Packet, acknowledgement);
        public bool Commit()
        {
            if (Phase != TravelPhase.Persisting) return false;
            Phase = TravelPhase.Committed; return true;
        }
        public bool Cancel()
        {
            if (Phase == TravelPhase.Committed || Phase == TravelPhase.Cancelled) return false;
            Phase = TravelPhase.Cancelled; return true;
        }
        public static bool SameRoute(TravelPacket a, TravelPacket b) => a.Id == b.Id && a.Ship == b.Ship && a.Leader == b.Leader
            && a.Reason == b.Reason && a.Source == b.Source && a.Destination == b.Destination;
        public static bool SameManifest(TravelPacket a, TravelPacket b)
        {
            if (!SameRoute(a, b)) return false;
            var x = a.Copy(); var y = b.Copy(); x.Kind = y.Kind = TravelKind.Manifest;
            x.ClientSession = y.ClientSession = x.ServerSession = y.ServerSession = Guid.Empty;
            return TravelProtocol.Encode(x).SequenceEqual(TravelProtocol.Encode(y));
        }
        public static int[] PassengerIds(IEnumerable<int> native, IEnumerable<int> walking, int leader)
            => native.Concat(walking).Where(id => id > 0 && id != leader).Distinct().ToArray();
    }
}
