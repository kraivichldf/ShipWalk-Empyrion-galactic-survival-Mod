using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using UnityEngine;
using NV = System.Numerics.Vector3;
using NQ = System.Numerics.Quaternion;

namespace ShipWalk
{
    internal sealed class TravelCoordinator
    {
        private sealed class WarpIngress : IDisposable
        {
            public int Actor;
            public object Context;
            public WarpIngress Previous;
            public void Dispose() { if (ReferenceEquals(warpIngress, this)) warpIngress = Previous; }
        }
        [ThreadStatic] private static WarpIngress warpIngress;
        private sealed class Source
        {
            public TravelTransfer Transfer;
            public NativeTravelPlan Plan;
            public object Context, Ship;
            public FramePeer[] Peers;
            public float Deadline, Retry;
        }
        private sealed class Destination
        {
            public TravelPacket Packet;
            public float Deadline, Retry;
            public readonly HashSet<int> Positioned = new HashSet<int>(), Bound = new HashSet<int>();
        }
        private sealed class Warp { public NativeTravelPlan Plan; public object Context; public int Leader; public float Expires; }
        private readonly Runtime owner;
        private readonly IModApi api;
        private Build5150 Map => owner.Map;
        private SharedFrameNetwork Network => owner.Network;
        private NativeFrameTransport Transport => Network.Transport;
        internal readonly NativeTravelMap Native;
        private readonly Queue<Tuple<TravelPacket, NativeFrameEnvelope>> incoming = new Queue<Tuple<TravelPacket, NativeFrameEnvelope>>();
        private readonly Queue<Tuple<string, TravelPacket>> fromHub = new Queue<Tuple<string, TravelPacket>>();
        private readonly Dictionary<int, Source> sources = new Dictionary<int, Source>();
        private readonly VesselBoundaryGate vesselBoundary = new VesselBoundaryGate();
        private readonly Dictionary<Guid, Destination> destinations = new Dictionary<Guid, Destination>();
        private readonly Dictionary<int, Warp> warps = new Dictionary<int, Warp>();
        private readonly Dictionary<Guid, float> completed = new Dictionary<Guid, float>();
        private readonly Dictionary<Guid, TravelPacket> clientCompleted = new Dictionary<Guid, TravelPacket>();
        private TravelPacket ticket;
        private TravelMember localMember;
        private bool arrivalSeen, boundSent;
        private float ticketDeadline, nextRequest, nextRestore, nextWarning;
        private int requestShip = -1, requestReason;
        private float requestStarted;
        private float nextRecoveryNotice;
        public bool Suspended => ticket != null && !arrivalSeen && ContextName() == ticket.Source && localMember?.Mode != PassengerMode.Seated;
        public bool Restoring => ticket != null && arrivalSeen && localMember?.Mode != PassengerMode.Seated;
        public string Status => "travel=" + (ticket != null ? arrivalSeen ? "Arriving" : "Prepared" : sources.Count > 0 ? "Transferring" : "Idle")
            + "; transfers=" + sources.Count + "; arrivals=" + destinations.Count;

        public TravelCoordinator(Runtime owner, IModApi api, System.Reflection.Assembly assembly)
        {
            this.owner = owner; this.api = api; Native = new NativeTravelMap(assembly, owner.Map);
            if (owner.PlayfieldServer && !api.Network.RegisterReceiverForDediPackets(ReceiveHub))
                throw new InvalidOperationException("ShipWalk travel worker receiver already registered.");
        }
        private string ContextName() => Native.ContextName(api.Application.LocalPlayer == null ? null : Transport.Context(Map.NativeEntity(api.Application.LocalPlayer)));
        private static float Now => Time.realtimeSinceStartup;
        public void Receive(string text, NativeFrameEnvelope envelope)
        {
            if (!TravelProtocol.TryEnvelope(text, out TravelPacket p)) return;
            lock (incoming) if (incoming.Count < 128) incoming.Enqueue(Tuple.Create(p, envelope));
        }
        private void ReceiveHub(string sender, string playfield, byte[] bytes)
        {
            if (sender != "ShipWalk" || !TravelProtocol.TryDecode(bytes, out TravelPacket p)) return;
            lock (fromHub) if (fromHub.Count < 128) fromHub.Enqueue(Tuple.Create(playfield, p));
        }
        public bool Boundary(object context, object actor)
        {
            if (!owner.MultiplayerClient || !owner.Frame.MatchesActor(actor) || Map.SeatedShip.GetValue(actor) != null) return true;
            // The native trigger is in the player update. A walking member must
            // test the ship boundary here, never fall into the player-only path.
            if (ticket == null && Native.BoundaryPlan(context, owner.Frame.Ship, out _)) Request(owner.Frame.ShipId, 6);
            return false;
        }
        public int RecoveryResult(int result, object actor)
        {
            if (owner.PlayfieldServer && Map.ShipType.IsInstanceOfType(actor)) return VesselRecovery(result, actor);
            bool owned = owner.MultiplayerClient && owner.Options.Mode == RunMode.Experimental
                && api.Application.LocalPlayer?.Health > 0 && owner.Frame.OwnsActor(actor);
            int filtered = TravelRecoveryPatch.Filter(result, owned);
            if (filtered != result && Now >= nextRecoveryNotice)
            {
                nextRecoveryNotice = Now + 10;
                owner.Log.Info("Travel player boundary recovery deferred to vessel; ship=" + owner.Frame.ShipId
                    + "; nativeReason=" + result + "; " + Status);
            }
            return filtered;
        }
        private int VesselRecovery(int result, object ship)
        {
            vesselBoundary.Observe(ship, result);
            if (result != 2 && result != 5) return result;
            object context = Transport.Context(ship);
            if (owner.Options.Mode != RunMode.Experimental || context == null
                || !ReferenceEquals(Transport.Entity(Map.Id(ship)), ship)
                || !LocalFrameMath.IsVessel(Map.Entity(ship)?.Type.ToString()) || Map.DockedTo.GetValue(ship) != null
                || !owner.Momentum.Coasting.Contains(ship) || Map.Bool(Map.EntityRemote, ship) || Native.Pilot(ship) > 0)
                return result;
            bool deferred = vesselBoundary.Defer(ship, context, result, Now, () =>
            {
                if (!sources.TryGetValue(Map.Id(ship), out Source source))
                {
                    // The worker can arrive before the client's interpolated
                    // player tick (especially climbing). Use its authenticated,
                    // fresh aboard roster to start the same vessel transaction.
                    FramePeer leader = Network.Aboard(Map.Id(ship), context).FirstOrDefault();
                    if (leader == null || !Native.BoundaryPlan(context, ship, out NativeTravelPlan plan)
                        || !Prepare(ship, leader, plan)) return false;
                    source = sources[Map.Id(ship)];
                }
                if (!ReferenceEquals(source.Ship, ship) || !ReferenceEquals(source.Context, context) || source.Plan.Reason != 6) return false;
                if (source.Transfer.Phase == TravelPhase.Committed) return Now < source.Deadline;
                if (Now >= source.Deadline || !PassengersValid(source) || !SameBoundary(source))
                { Cancel(source, "boundary passenger or destination changed"); return false; }
                return true;
            });
            if (deferred && Now >= nextRecoveryNotice)
            {
                nextRecoveryNotice = Now + 10;
                owner.Log.Info("Travel vessel boundary recovery deferred; ship=" + Map.Id(ship) + "; nativeReason=" + result + "; " + Status);
            }
            return TravelRecoveryPatch.Filter(result, deferred);
        }
        public bool WorldRequest(int reason)
        {
            if (!owner.MultiplayerClient || owner.Options.Mode != RunMode.Experimental || reason != 6 && reason != 2) return true;
            IPlayer player = api.Application.LocalPlayer;
            object actor = player == null ? null : Map.NativeEntity(player);
            object ship = actor == null ? null : DockingVessels.Root(Map, Map.SeatedShip.GetValue(actor));
            int id = ship != null ? Map.Id(ship) : owner.Frame.Active ? owner.Frame.ShipId : -1;
            if (id <= 0 || !LocalFrameMath.IsVessel(Map.Entity(ship ?? owner.Frame.Ship)?.Type.ToString())) return true;
            Request(id, reason); return false;
        }
        private void Request(int ship, int reason)
        {
            if (ticket != null) return;
            if (requestShip != ship || requestReason != reason)
            { requestShip = ship; requestReason = reason; requestStarted = Now; }
            if (Now < nextRequest) return;
            nextRequest = Now + 1;
            if (!Network.TravelReady)
            {
                if (Now >= nextWarning) { nextWarning = Now + 10; owner.Tell("Waiting for the ShipWalk server before moving ship and passengers between playfields."); }
                return;
            }
            Network.SendTravel(new TravelPacket { Kind = reason == 2 ? TravelKind.WarpRequest : TravelKind.BoundaryRequest,
                Id = Guid.NewGuid(), Ship = ship, Reason = reason });
        }
        public void WarpStarted(object[] args)
        {
            if (!owner.PlayfieldServer || Convert.ToInt32(args[0]) != 2) return;
            int leader = (int)args[7]; object actor = Transport.Entity(leader);
            if (warpIngress == null || warpIngress.Actor != leader || !ReferenceEquals(warpIngress.Context, args[1])) return;
            object ship = actor == null ? null : Map.SeatedShip.GetValue(actor);
            if (ship == null || !ReferenceEquals(Transport.Context(actor), args[1]) || Native.Pilot(ship) != leader) return;
            warps[Map.Id(ship)] = new Warp { Leader = leader, Context = args[1], Expires = Now + 30,
                Plan = new NativeTravelPlan { Reason = 2, Sector = args[3], Destination = (string)args[4],
                    Position = (Vector3)args[5], Rotation = (Quaternion)args[6] } };
        }
        public IDisposable BeginNativeWarp(object packet)
        {
            var scope = new WarpIngress { Previous = warpIngress }; warpIngress = scope;
            try
            {
                var envelope = new NativeFrameEnvelope { Connection = Transport.Sender(packet), Playfield = Transport.PacketContext(packet) };
                if (Transport.Authenticate(envelope, out int id, out _)) { scope.Actor = id; scope.Context = envelope.Playfield; }
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }
        public void MicroBroadcast(object context, object packet)
        {
            if (!owner.PlayfieldServer || !Native.TryMicro(packet, out int ship, out _, out _)) return;
            int sent = 0;
            foreach (FramePeer peer in Network.Aboard(ship, context))
            {
                object actor = Transport.Entity(peer.Actor);
                if (actor == null || Map.SeatedShip.GetValue(actor) != null || !Connected(peer)) continue;
                if (Transport.SendNativeClient(peer.Connection, packet)) sent++;
            }
            if (sent > 0) owner.Log.Info("MicroWarp native completion forwarded; ship=" + ship + "; walkingPassengers=" + sent);
        }
        public void MicroArrived(object packet)
        {
            if (owner.MultiplayerClient && Native.TryMicro(packet, out int ship, out Vector3 point, out Quaternion rotation)
                && owner.Frame.ShipId == ship && ReferenceEquals(Transport.Entity(ship), owner.Frame.Ship)
                && ReferenceEquals(Transport.PacketContext(packet), Transport.Context(owner.Frame.Ship))) owner.Frame.AcceptNativeTeleport(point, rotation);
        }
        public void Arrival(string source, string destination, ref Vector3 point, ref Quaternion rotation)
        {
            if (ticket == null || Now >= ticketDeadline || source != ticket.Source || destination != ticket.Destination) return;
            arrivalSeen = true;
            ticketDeadline = Now + 300;
            if (localMember.Mode != PassengerMode.Seated)
            {
                Vector3 local = U(localMember.Position); Quaternion localRotation = U(localMember.Rotation);
                point += rotation * local; rotation *= localRotation;
            }
            owner.Log.Info("Travel native arrival; transfer=" + ticket.Id + "; ship=" + ticket.Ship + "; mode=" + localMember.Mode);
        }
        public void Update()
        {
            for (int i = 0; i < 64; i++)
            {
                Tuple<TravelPacket, NativeFrameEnvelope> item;
                lock (incoming) { if (incoming.Count == 0) break; item = incoming.Dequeue(); }
                if (owner.PlayfieldServer) ReceiveServer(item.Item1, item.Item2);
                else if (Network.AcceptTravel(item.Item1, item.Item2)) ReceiveClient(item.Item1);
            }
            if (owner.PlayfieldServer) UpdateServer(); else UpdateClient();
            foreach (Guid id in completed.Where(p => Now >= p.Value).Select(p => p.Key).ToArray()) { completed.Remove(id); clientCompleted.Remove(id); }
        }
        private bool Connected(FramePeer peer) => Transport.Authenticate(new NativeFrameEnvelope { Connection = peer.Connection,
            Playfield = peer.Context }, out int id, out _) && id == peer.Actor && Network.PeerFor(id, peer.Context)?.Session == peer.Session;
        private TravelMember CaptureLocal(int ship)
        {
            IPlayer player = api.Application.LocalPlayer;
            if (player == null || player.Health <= 0) return null;
            FrameMessage state = owner.Frame.NetworkState();
            if (state != null && state.Ship == ship) { state.Actor = player.Id; return TravelMember.From(state); }
            object actor = Map.NativeEntity(player), vessel = Transport.Entity(ship);
            if (vessel == null || player.DrivingEntity?.Id != ship || !ReferenceEquals(Map.SeatedShip.GetValue(actor), vessel)) return null;
            return Seated(actor, vessel);
        }
        private TravelMember Seated(object actor, object ship)
        {
            var hull = Map.EntityTransform.GetValue(ship) as Transform; var root = Map.EntityTransform.GetValue(actor) as Transform;
            if (hull == null || root == null) return null;
            return new TravelMember { Actor = Map.Id(actor), Ship = Map.Id(ship), Mode = PassengerMode.Seated,
                Position = N(Quaternion.Inverse(hull.rotation) * (root.position - hull.position)), Rotation = N(Quaternion.Inverse(hull.rotation) * root.rotation) };
        }
        private void ReceiveServer(TravelPacket p, NativeFrameEnvelope envelope)
        {
            if (!Network.AuthenticateTravel(p, envelope, out object actor, out FramePeer peer)) return;
            if (p.Kind == TravelKind.Bound)
            {
                if (destinations.TryGetValue(p.Id, out Destination d) && TravelTransfer.SameRoute(d.Packet, p)
                    && Native.ContextName(peer.Context) == p.Destination && d.Positioned.Contains(peer.Actor)) d.Bound.Add(peer.Actor);
                return;
            }
            if (sources.TryGetValue(p.Ship, out Source current))
            {
                if (!current.Peers.Any(x => x.Actor == peer.Actor && x.Session == peer.Session && ReferenceEquals(x.Connection, peer.Connection))) return;
                if (p.Kind == TravelKind.Cancel && TravelTransfer.SameRoute(current.Transfer.Packet, p)) Cancel(current, "passenger cancelled");
                else if (p.Kind == TravelKind.Ready && current.Transfer.Ready(peer.Actor, p, member => ValidReady(actor, peer, member)))
                {
                    if (current.Transfer.Persist()) { current.Retry = 0; owner.Log.Info("Travel passengers ready; transfer=" + p.Id); }
                }
                return;
            }
            if (p.Kind != TravelKind.BoundaryRequest && p.Kind != TravelKind.WarpRequest || owner.Options.Mode != RunMode.Experimental) return;
            object vessel = Transport.Entity(p.Ship);
            if (vessel == null || !Map.ShipType.IsInstanceOfType(vessel) || !ReferenceEquals(Transport.Context(vessel), peer.Context)
                || !LocalFrameMath.IsVessel(Map.Entity(vessel)?.Type.ToString()) || Map.Player(actor)?.Health <= 0) return;
            int pilot = Native.Pilot(vessel);
            // A pilot owns an occupied ship's crossing. An empty coasting ship
            // may be led by any currently validated walking member.
            if (pilot > 0 ? pilot != peer.Actor : peer.State?.Ship != p.Ship || peer.State.Mode == PassengerMode.World) return;
            NativeTravelPlan plan;
            if (p.Kind == TravelKind.WarpRequest)
            {
                if (!warps.TryGetValue(p.Ship, out Warp warp) || warp.Leader != peer.Actor || warp.Expires < Now
                    || !ReferenceEquals(warp.Context, peer.Context)) return;
                plan = warp.Plan;
            }
            else if (!Native.BoundaryPlan(peer.Context, vessel, out plan)) return;
            if (plan.Reason == 6 && !vesselBoundary.AllowsStart(vessel, peer.Context, Now)) return;
            Prepare(vessel, peer, plan);
        }
        private bool Prepare(object vessel, FramePeer peer, NativeTravelPlan plan)
        {
            int ship = Map.Id(vessel);
            if (sources.ContainsKey(ship)) return false;
            var peers = Network.Aboard(ship, peer.Context).ToList();
            if (!peers.Any(x => x.Actor == peer.Actor)) peers.Add(peer);
            var members = new List<TravelMember>();
            foreach (FramePeer memberPeer in peers)
            {
                object nativeActor = Transport.Entity(memberPeer.Actor);
                if (nativeActor == null || !Connected(memberPeer) || !(Map.Player(nativeActor)?.Health > 0)
                    || !ReferenceEquals(Transport.Context(nativeActor), peer.Context)) return false;
                TravelMember member = Map.SeatedShip.GetValue(nativeActor) == vessel ? Seated(nativeActor, vessel)
                    : memberPeer.State == null ? null : TravelMember.From(memberPeer.State);
                if (member == null || member.Ship != ship || !TravelProtocol.MemberValid(member)) return false;
                members.Add(member);
            }
            if (sources.Count >= 64 || sources.Values.Any(s => s.Transfer.Packet.Members.Any(m => members.Any(n => n.Actor == m.Actor)))) return false;
            var record = new TravelPacket { Id = Guid.NewGuid(), Kind = TravelKind.Prepare, Ship = ship, Leader = peer.Actor,
                Reason = plan.Reason, Source = Native.ContextName(peer.Context), Destination = plan.Destination,
                Position = N(plan.Position), Rotation = N(plan.Rotation), Members = members.ToArray() };
            var current = new Source { Transfer = new TravelTransfer(record), Plan = plan, Context = peer.Context, Ship = vessel,
                Peers = peers.ToArray(), Deadline = Now + 15 };
            sources.Add(ship, current);
            owner.Log.Info("Travel prepare; transfer=" + record.Id + "; ship=" + ship + "; source=" + record.Source
                + "; destination=" + record.Destination + "; aboard=" + record.Members.Length + "; leader=" + record.Leader);
            return true;
        }
        private bool ValidReady(object actor, FramePeer peer, TravelMember member)
        {
            var state = member.Frame(); state.Actor = 0; state.Generation = peer.State?.Generation ?? 1;
            if (state.Generation == 0) state.Generation = 1;
            return Network.Validate(actor, state, peer.State, Now);
        }
        private void ReceiveClient(TravelPacket p)
        {
            int actor = api.Application.LocalPlayer?.Id ?? -1;
            if (p.Kind == TravelKind.Arrived && clientCompleted.TryGetValue(p.Id, out TravelPacket previous)
                && TravelTransfer.SameRoute(previous, p) && ContextName() == p.Destination)
            { Network.SendTravel(CopyKind(previous, TravelKind.Bound)); return; }
            if (p.Kind == TravelKind.Prepare && ContextName() == p.Source)
            {
                if (ticket != null && !TravelTransfer.SameRoute(ticket, p)) return;
                TravelMember proposed = p.Members.SingleOrDefault(m => m.Actor == actor);
                TravelMember current = ticket == null ? CaptureLocal(p.Ship) : localMember;
                if (proposed == null || current == null || (current.Mode == PassengerMode.Seated) != (proposed.Mode == PassengerMode.Seated))
                { var no = p.Copy(); no.Kind = TravelKind.Cancel; no.Members = Array.Empty<TravelMember>(); Network.SendTravel(no); return; }
                if (ticket == null)
                {
                    ticket = p.Copy(); localMember = current.Copy(); ticketDeadline = Now + 20; arrivalSeen = boundSent = false;
                    requestShip = -1;
                    owner.Log.Info("Travel passenger prepared; transfer=" + p.Id + "; ship=" + p.Ship + "; mode=" + current.Mode);
                }
                var answer = ticket.Copy(); answer.Kind = TravelKind.Ready; answer.Members = new[] { localMember.Copy() }; Network.SendTravel(answer);
            }
            else if (p.Kind == TravelKind.Commit && ticket != null && TravelTransfer.SameRoute(ticket, p)) ticketDeadline = Now + 300;
            else if (p.Kind == TravelKind.Cancel && ticket != null && !arrivalSeen && ContextName() == p.Source
                && TravelTransfer.SameRoute(ticket, p)) ClearTicket("source cancelled");
            else if (p.Kind == TravelKind.Arrived && ticket != null && TravelTransfer.SameRoute(ticket, p) && ContextName() == p.Destination)
            { arrivalSeen = true; nextRestore = 0; }
        }
        private void UpdateServer()
        {
            for (int i = 0; i < 64; i++)
            {
                Tuple<string, TravelPacket> item;
                lock (fromHub) { if (fromHub.Count == 0) break; item = fromHub.Dequeue(); }
                TravelPacket p = item.Item2;
                if (p.Kind == TravelKind.Stored && p.Source == item.Item1 && sources.TryGetValue(p.Ship, out Source source)
                    && source.Transfer.Stored(p))
                {
                    try { Commit(source); }
                    catch (Exception error)
                    {
                        if (source.Transfer.Phase == TravelPhase.Committed)
                            owner.Log.Info("Travel dispatch outcome uncertain; retained manifest, no duplicate native send; transfer=" + p.Id + "; " + error.GetBaseException());
                        else Cancel(source, "native transfer error: " + error.GetBaseException().Message);
                    }
                }
                else if (p.Kind == TravelKind.Manifest && p.Destination == item.Item1)
                {
                    if (completed.ContainsKey(p.Id)) { SendHub(p, TravelKind.Arrived, p.Destination); continue; }
                    if (!destinations.ContainsKey(p.Id) && destinations.Count < 64)
                        destinations.Add(p.Id, new Destination { Packet = p.Copy(), Deadline = Now + 300 });
                }
                else if (p.Kind == TravelKind.Cancel && p.Destination == item.Item1) destinations.Remove(p.Id);
            }
            foreach (Source source in sources.Values.ToArray())
            {
                TravelPhase phase = source.Transfer.Phase;
                if (phase == TravelPhase.Committed)
                {
                    object liveShip = Transport.Entity(source.Transfer.Packet.Ship);
                    if (Now >= source.Deadline || liveShip == null || !ReferenceEquals(Transport.Context(liveShip), source.Context)) sources.Remove(source.Transfer.Packet.Ship);
                    else if (Now >= source.Retry) { source.Retry = Now + 2; SendHub(source.Transfer.Packet, TravelKind.Commit, source.Transfer.Packet.Source); }
                    continue;
                }
                if (Now >= source.Deadline || source.Peers.Any(p => !Connected(p))) { Cancel(source, "prepare timed out or passenger disconnected"); continue; }
                if (Now < source.Retry) continue;
                source.Retry = Now + 1;
                if (phase == TravelPhase.Preparing) foreach (FramePeer peer in source.Peers) Network.SendTravel(peer, source.Transfer.Packet);
                else if (phase == TravelPhase.Persisting) SendHub(source.Transfer.Packet, TravelKind.Manifest, source.Transfer.Packet.Source);
            }
            foreach (Destination d in destinations.Values.ToArray()) RestoreDestination(d);
            vesselBoundary.Prune((ship, context) => ReferenceEquals(Transport.Entity(Map.Id(ship)), ship)
                && ReferenceEquals(Transport.Context(ship), context));
            foreach (int ship in warps.Where(p => Now >= p.Value.Expires).Select(p => p.Key).ToArray()) warps.Remove(ship);
        }
        private void Commit(Source source)
        {
            if (Now >= source.Deadline || source.Peers.Any(p => !Connected(p))) { Cancel(source, "source no longer valid"); return; }
            if (!PassengersValid(source)) { Cancel(source, "passenger membership, seat or life state changed"); return; }
            if (source.Plan.Reason == 6 && !SameBoundary(source))
            { Cancel(source, "ship no longer at the prepared boundary"); return; }
            object packet = Native.BuildWorldPacket(source.Ship, source.Plan, source.Transfer.Packet.Leader,
                source.Transfer.Packet.Members.Select(m => m.Actor));
            source.Transfer.Commit(); source.Deadline = Now + 300; source.Retry = Now + 2;
            foreach (FramePeer peer in source.Peers) Network.SendTravel(peer, CopyKind(source.Transfer.Packet, TravelKind.Commit));
            SendHub(source.Transfer.Packet, TravelKind.Commit, source.Transfer.Packet.Source);
            if (!Native.SendWorldPacket(packet)) { Cancel(source, "native manager connection unavailable", knownUnsent: true); return; }
            warps.Remove(source.Transfer.Packet.Ship);
            owner.Log.Info("Travel native vessel transfer sent; transfer=" + source.Transfer.Packet.Id + "; ship=" + source.Transfer.Packet.Ship);
        }
        private bool SameBoundary(Source source)
            => Native.BoundaryPlan(source.Context, source.Ship, out NativeTravelPlan current)
                && current.Destination == source.Plan.Destination;
        private bool PassengersValid(Source source)
        {
            foreach (TravelMember member in source.Transfer.Packet.Members)
            {
                object actor = Transport.Entity(member.Actor);
                FramePeer peer = Network.PeerFor(member.Actor, source.Context);
                if (actor == null || peer == null || !Connected(peer) || !(Map.Player(actor)?.Health > 0)
                    || !source.Peers.Any(p => p.Actor == peer.Actor && p.Session == peer.Session && ReferenceEquals(p.Connection, peer.Connection))
                    || !ReferenceEquals(Transport.Context(actor), source.Context)
                    || (member.Mode != PassengerMode.Seated && (peer.State?.Ship != member.Ship || peer.State.Mode == PassengerMode.World))
                    || DockingVessels.Member(Map, source.Ship, Map.SeatedShip.GetValue(actor)) != (member.Mode == PassengerMode.Seated)
                    || member.Member > 0 && !DockingVessels.Member(Map, source.Ship, Transport.Entity(member.Member)))
                    return false;
            }
            return true;
        }
        private void RestoreDestination(Destination d)
        {
            if (Now >= d.Deadline) { destinations.Remove(d.Packet.Id); completed[d.Packet.Id] = Now + 300; owner.Log.Info("Travel arrival expired; transfer=" + d.Packet.Id); return; }
            if (Now < d.Retry) return; d.Retry = Now + .5f;
            object ship = Transport.Entity(d.Packet.Ship);
            if (ship == null || Native.ContextName(Transport.Context(ship)) != d.Packet.Destination) return;
            IEntity vessel = Map.Entity(ship);
            if (vessel?.Structure?.IsReady != true) return;
            foreach (TravelMember member in d.Packet.Members)
            {
                object actor = Transport.Entity(member.Actor); object context = Transport.Context(actor);
                if (actor == null || Native.ContextName(context) != d.Packet.Destination || Map.Player(actor)?.Health <= 0) continue;
                FramePeer peer = Network.PeerFor(member.Actor, context);
                if (peer == null || !Connected(peer)) continue;
                if (!d.Positioned.Contains(member.Actor))
                {
                    // Native arrival + the local ticket place the character.
                    // Wait for the destination's validated membership instead
                    // of replaying an old pose over fresh client movement.
                    bool aboard = member.Mode == PassengerMode.Seated ? DockingVessels.Member(Map, Transport.Entity(d.Packet.Ship), Map.SeatedShip.GetValue(actor))
                        : peer.State?.Ship == d.Packet.Ship && peer.State.Mode != PassengerMode.World;
                    if (aboard) d.Positioned.Add(member.Actor);
                }
                if (!d.Bound.Contains(member.Actor)) Network.SendTravel(peer, CopyKind(d.Packet, TravelKind.Arrived));
            }
            if (d.Bound.Count != d.Packet.Members.Length) return;
            SendHub(d.Packet, TravelKind.Arrived, d.Packet.Destination); destinations.Remove(d.Packet.Id); completed[d.Packet.Id] = Now + 300;
            owner.Log.Info("Travel all passengers rebound; transfer=" + d.Packet.Id + "; ship=" + d.Packet.Ship);
        }
        private void UpdateClient()
        {
            if (ticket == null)
            {
                if (requestShip > 0)
                {
                    if (Now - requestStarted >= 15 || CaptureLocal(requestShip) == null)
                    {
                        requestShip = -1;
                        if (Now >= nextWarning) { nextWarning = Now + 15; owner.Tell("Ship travel was not acknowledged. Check ShipWalk is updated on the client, dedicated manager and playfield workers."); }
                    }
                    else Request(requestShip, requestReason);
                }
                return;
            }
            if (Now >= ticketDeadline)
            {
                if (arrivalSeen) owner.StopFrame("travel expired", false);
                CancelLocal("arrival timeout"); return;
            }
            if (!arrivalSeen || ContextName() != ticket.Destination || api.Application.State != GameState.Running) return;
            if (localMember.Mode == PassengerMode.Seated)
            {
                if (DockingVessels.Member(Map, Transport.Entity(ticket.Ship), Map.NativeEntity(api.Application.LocalPlayer?.DrivingEntity))) CompleteClient();
                return;
            }
            if (owner.Frame.Active && owner.Frame.ShipId == ticket.Ship) { CompleteClient(); return; }
            if (owner.Frame.HasSession || Now < nextRestore || !Network.TravelReady) return;
            nextRestore = Now + .5f;
            if (!api.ClientPlayfield.Entities.TryGetValue(ticket.Ship, out IEntity vessel) || vessel.Structure?.IsReady != true) return;
            try { owner.Frame.Arm(vessel, false, localMember); }
            catch (NotSupportedException error) { if (Now >= nextWarning) { nextWarning = Now + 5; owner.Log.Info("Travel waiting for native character: " + error.Message); } }
        }
        private void CompleteClient()
        {
            if (!Network.TravelReady) return;
            boundSent = Network.SendTravel(CopyKind(ticket, TravelKind.Bound));
            if (boundSent)
            {
                completed[ticket.Id] = Now + 300; clientCompleted[ticket.Id] = ticket.Copy();
                ClearTicket("local frame restored");
            }
        }
        private void Cancel(Source source, string reason, bool knownUnsent = false)
        {
            if (!source.Transfer.Cancel() && !knownUnsent) return;
            TravelPacket packet = CopyKind(source.Transfer.Packet, TravelKind.Cancel);
            if (source.Plan.Reason == 6) vesselBoundary.Block(source.Ship, source.Context);
            foreach (FramePeer peer in source.Peers) Network.SendTravel(peer, packet);
            SendHub(packet, TravelKind.Cancel, packet.Source); sources.Remove(packet.Ship);
            owner.Log.Info("Travel cancelled; transfer=" + packet.Id + "; reason=" + reason);
        }
        public void CancelLocal(string reason)
        {
            requestShip = -1;
            if (ticket == null) return;
            Network.SendTravel(CopyKind(ticket, TravelKind.Cancel)); ClearTicket(reason);
        }
        private void ClearTicket(string reason)
        {
            owner.Log.Info("Travel passenger handoff ended; transfer=" + ticket?.Id + "; reason=" + reason);
            ticket = null; localMember = null; arrivalSeen = boundSent = false; requestShip = -1; nextRequest = Now + 2;
        }
        private bool SendHub(TravelPacket packet, TravelKind kind, string context)
            => api.Network.SendToDedicatedServer("ShipWalk", TravelProtocol.Encode(CopyKind(packet, kind)), context);
        private static TravelPacket CopyKind(TravelPacket packet, TravelKind kind) { var copy = packet.Copy(); copy.Kind = kind; return copy; }
        private static NV N(Vector3 v) => new NV(v.x, v.y, v.z);
        private static NQ N(Quaternion q) => new NQ(q.x, q.y, q.z, q.w);
        private static Vector3 U(NV v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion U(NQ q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
