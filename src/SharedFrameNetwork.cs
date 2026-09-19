using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace ShipWalk
{
    internal sealed class SharedFrameNetwork
    {
        private readonly Runtime owner;
        private readonly IModApi api;
        private readonly Build5150 map;
        internal readonly NativeFrameTransport Transport;
        private readonly Queue<NativeFrameEnvelope> incoming = new Queue<NativeFrameEnvelope>();
        private readonly Dictionary<int, RemotePassenger> passengers = new Dictionary<int, RemotePassenger>();
        private FrameRelay relay = new FrameRelay();
        private readonly DockingMotionWindow dockingMotion = new DockingMotionWindow();
        private Guid clientSession = Guid.NewGuid(), serverSession;
        private uint generation, sequence;
        private int lastShip = -1;
        private float nextHello, nextPublish, nextPoseRequest, nextReport, lastWelcome = -100;
        private long sent, received, invalid, rendered, lastReceipts;
        private long animationSamples;
        private float nativeAnimationDistance, localAnimationDistance;
        private bool faulted;
        public string Status => "sharedFrame=" + (faulted ? "TransportError" : owner.PlayfieldServer ? "ServerListening"
            : !owner.MultiplayerClient ? "SinglePlayer" : serverSession == Guid.Empty ? "WaitingForServer" : "ServerAcknowledged")
            + "; frameTx=" + sent + "; frameRx=" + received + "; rejected=" + (invalid + relay.Rejected)
            + "; occupants=" + passengers.Count + "; rendered=" + rendered
            + "; subscribers=" + relay.Count + "; accepted=" + relay.Accepted + "; observerReceipts=" + relay.ObserverReceipts
            + "; animationSamples=" + animationSamples + "; nativeAnimationDistance=" + nativeAnimationDistance.ToString("F4")
            + "; localAnimationDistance=" + localAnimationDistance.ToString("F4");
        public SharedFrameNetwork(Runtime owner, IModApi api, Build5150 map, System.Reflection.Assembly game)
        { this.owner = owner; this.api = api; this.map = map; Transport = new NativeFrameTransport(map.Native); }

        public void Receive(object packet)
        {
            if (!faulted && Transport.CapturePayload(packet, out string reconnectText, out NativeFrameEnvelope reconnectEnvelope)
                && ReconnectProtocol.TryEnvelope(reconnectText, out ReconnectPacket reconnect))
            { owner.Reconnect.Receive(reconnect, reconnectEnvelope); return; }
            if (!faulted && Transport.CapturePayload(packet, out string payload, out NativeFrameEnvelope travelEnvelope)
                && TravelProtocol.IsEnvelope(payload))
            { owner.Travel.Receive(payload, travelEnvelope); return; }
            if (faulted || !Transport.Capture(packet, out NativeFrameEnvelope envelope)) return;
            lock (incoming)
            {
                if (incoming.Count >= 256) { invalid++; return; }
                incoming.Enqueue(envelope);
            }
        }
        public void Reset()
        {
            lock (incoming) incoming.Clear();
            passengers.Clear(); relay = new FrameRelay(); clientSession = Guid.NewGuid(); serverSession = Guid.Empty;
            dockingMotion.Clear();
            generation = sequence = 0; lastShip = -1; nextHello = nextPublish = nextPoseRequest = 0; lastWelcome = -100; lastReceipts = 0;
        }
        public void Update()
        {
            if (faulted || !owner.PlayfieldServer && !owner.MultiplayerClient) return;
            try { UpdateCore(); }
            catch (Exception error) { Fault(error); }
        }
        public void Fault(Exception error)
        {
            if (faulted) return;
            faulted = true; passengers.Clear();
            owner.Log.Info("Shared frame transport disabled; " + error.GetBaseException());
        }
        private void UpdateCore()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < 64; i++)
            {
                NativeFrameEnvelope envelope;
                lock (incoming) { if (incoming.Count == 0) break; envelope = incoming.Dequeue(); }
                received++;
                if (owner.PlayfieldServer)
                {
                    if (!Transport.Authenticate(envelope, out int id, out object actor)
                        || !map.PlayerType.IsInstanceOfType(actor)) { invalid++; continue; }
                    FrameMessage before = relay.PeerFor(id, envelope.Playfield, now)?.State;
                    long accepted = relay.Accepted;
                    var deliveries = relay.Receive(id, envelope.Connection, envelope.Playfield, envelope.Message, now,
                        (message, previous) => Validate(actor, message, previous, now), CaptureShipPose);
                    if (relay.Accepted != accepted)
                    {
                        dockingMotion.Accepted(id, before, envelope.Message, now);
                        owner.Reconnect.Capture(actor, envelope.Message);
                    }
                    Deliver(deliveries);
                }
                else ReceiveClient(envelope, now);
            }
            if (owner.PlayfieldServer)
            {
                Deliver(relay.Tick(now, (id, connection, context) => Transport.Authenticate(
                    new NativeFrameEnvelope { Connection = connection, Playfield = context }, out int actual, out _) && actual == id));
                if (relay.ObserverReceipts != lastReceipts)
                {
                    if (lastReceipts == 0) owner.Log.Info("Shared frame observer receipt; publisher=" + relay.LastReceiptActor
                        + "; observer=" + relay.LastReceiptObserver + "; route=client-server-client.");
                    lastReceipts = relay.ObserverReceipts;
                }
            }
            else if (api.Application.State == GameState.Running && api.Application.LocalPlayer != null && api.ClientPlayfield != null)
            {
                if (now >= nextHello)
                {
                    nextHello = now + 2;
                    Send(new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = clientSession });
                }
                if (serverSession != Guid.Empty && now - lastWelcome > 8)
                { serverSession = Guid.Empty; passengers.Clear(); owner.Log.Info("Shared frame acknowledgement expired; reconnecting."); }
                if (serverSession != Guid.Empty && now >= nextPublish && (!owner.Reconnect.Blocking || owner.Frame.Active))
                {
                    nextPublish = now + .05f;
                    FrameMessage state = owner.Frame.NetworkState() ?? new FrameMessage { Mode = PassengerMode.World };
                    if (state.Ship != -1 || lastShip != -1)
                    {
                        if (state.Ship != lastShip) { generation++; lastShip = state.Ship; }
                        if (generation == 0) generation = 1;
                        state.Kind = FrameMessageKind.Publish; state.Generation = generation; state.Sequence = ++sequence;
                        state.ClientSession = clientSession; state.ServerSession = serverSession; Send(state);
                    }
                }
                if (serverSession != Guid.Empty && now >= nextPoseRequest)
                {
                    FrameMessage request = owner.Frame.ShipPoseRequest(now);
                    if (request != null)
                    {
                        nextPoseRequest = now + .15f;
                        request.Sequence = ++sequence; request.ClientSession = clientSession; request.ServerSession = serverSession;
                        owner.Frame.TrackShipPoseRequest(request.Sequence, now); Send(request);
                    }
                }
            }
            if (now >= nextReport)
            {
                nextReport = now + 5;
                if (!owner.PlayfieldServer && api.ClientPlayfield != null)
                    foreach (int id in passengers.Keys.Where(id => !api.ClientPlayfield.Players.ContainsKey(id)).ToArray()) passengers.Remove(id);
                if (owner.Options.Trace && (sent != 0 || received != 0)) owner.Log.Info("Shared frame network; " + Status);
            }
        }
        private void ReceiveClient(NativeFrameEnvelope envelope, float now)
        {
            FrameMessage message = envelope.Message;
            IPlayer local = api.Application.LocalPlayer;
            object native = local == null ? null : map.NativeEntity(local);
            if (native == null || !ReferenceEquals(envelope.Playfield, Transport.Context(native))
                || message.ClientSession != clientSession) { invalid++; return; }
            if (message.Kind == FrameMessageKind.Welcome && message.Actor == local.Id && message.ServerSession != Guid.Empty)
            {
                if (serverSession != message.ServerSession)
                {
                    passengers.Clear(); serverSession = message.ServerSession;
                    owner.Log.Info("Shared frame server acknowledged; actor=" + local.Id + "; protocol=" + FrameProtocol.Version + "; session=" + serverSession);
                }
                lastWelcome = now; return;
            }
            if (message.Kind == FrameMessageKind.ShipPose && serverSession != Guid.Empty
                && message.ServerSession == serverSession && message.Actor == local.Id)
            { if (!owner.Frame.ConfirmShipPose(message, now)) invalid++; return; }
            if (message.Kind != FrameMessageKind.State || serverSession == Guid.Empty || message.ServerSession != serverSession)
            { invalid++; return; }
            if (message.Actor == local.Id) return; // Local collision prediction owns our character.
            if (!passengers.TryGetValue(message.Actor, out RemotePassenger passenger))
            {
                // Only native-visible players in this playfield can be presented.
                if (!api.ClientPlayfield.Players.ContainsKey(message.Actor)) return;
                passengers[message.Actor] = passenger = new RemotePassenger();
            }
            bool firstAttachment = passenger.State == null || passenger.State.Generation != message.Generation;
            if (!passenger.Accept(message, now)) return;
            if (firstAttachment && message.Mode != PassengerMode.World)
            {
                var receipt = message.Copy(); receipt.Kind = FrameMessageKind.Receipt; Send(receipt);
            }
        }
        internal bool Validate(object actor, FrameMessage message, FrameMessage previous, float now)
        {
            if (owner.Options.Mode != RunMode.Experimental) return message.Mode == PassengerMode.World && message.Ship == -1;
            IPlayer player = map.Player(actor);
            if (player == null || player.Health <= 0) return message.Mode == PassengerMode.World && message.Ship == -1;
            if (message.Mode == PassengerMode.World) return message.Ship == -1;
            if (message.Ship <= 0 || message.Generation == 0) return false;
            object nativeShip = Transport.Entity(message.Ship);
            if (nativeShip == null || !map.ShipType.IsInstanceOfType(nativeShip)
                || !ReferenceEquals(Transport.Context(actor), Transport.Context(nativeShip))) return false;
            IEntity vessel = map.Entity(nativeShip);
            if (!LocalFrameMath.IsVessel(vessel.Type.ToString()) || vessel.Structure == null || !vessel.Structure.IsReady) return false;
            object member = message.Member > 0 ? Transport.Entity(message.Member) : nativeShip;
            if (!DockingVessels.Member(map, nativeShip, member)
                || !ReferenceEquals(Transport.Context(actor), Transport.Context(member))
                || !DockingVessels.Contains(map, nativeShip, member, message.Position, 3)) return false;
            bool remaining = previous != null && previous.Ship == message.Ship && previous.Mode != PassengerMode.World;
            if (!remaining)
            {
                bool nativeContext = DockingVessels.Member(map, nativeShip, map.NativeEntity(player.DrivingEntity))
                    || DockingVessels.Member(map, nativeShip, map.NativeEntity(player.CurrentStructure?.Entity));
                Vector3 world = (Vector3)map.EntityPosition.GetValue(actor);
                Vector3 local = Quaternion.Inverse(vessel.Rotation) * (world - (Vector3)map.EntityPosition.GetValue(nativeShip));
                if (!nativeContext && !DockingVessels.Contains(map, nativeShip, member, N(local), 3)) return false;
            }
            else if (previous.Mode != PassengerMode.Seated && message.Mode != PassengerMode.Seated)
            {
                double dt = Math.Max(.05, Math.Min(2, now - previous.Tick / 1000d));
                if (NVector.Distance(previous.Position, message.Position) > dockingMotion.Distance(map.Id(actor), previous.Velocity, dt, now)) return false;
            }
            // Seating uses the server's existing seat ownership, not a client's mode claim.
            return message.Mode != PassengerMode.Seated || player.DrivingEntity?.Id == map.Id(member);
        }

        private FrameMessage CaptureShipPose(FrameMessage request)
        {
            object ship = Transport.Entity(request.Ship);
            if (ship == null || !map.ShipType.IsInstanceOfType(ship)) return null;
            // A pilot release must reach the native owner. A relocation query
            // can instead read the worker's authenticated pilot-owned vessel.
            bool remote = map.Bool(map.EntityRemote, ship);
            if (!ShipPosePolicy.CanConfirm(request.Purpose, remote, map.HasPilot(ship))) return null;
            IEntity vessel = map.Entity(ship);
            if (vessel == null) return null;
            var body = map.EntityBody.GetValue(ship) as Rigidbody;
            var position = (Vector3)map.EntityPosition.GetValue(ship);
            Vector3 velocity = !remote && body != null && body.gameObject.activeInHierarchy && !body.isKinematic
                ? body.velocity : (Vector3)map.EntityVelocity.GetValue(ship);
            var pose = new LocalFramePose(N(position), new NQuaternion(vessel.Rotation.x, vessel.Rotation.y, vessel.Rotation.z, vessel.Rotation.w));
            if (!pose.Valid || !RemoteFrameMath.ValidVelocity(N(velocity))) return null;
            return new FrameMessage { Position = pose.Position, Rotation = pose.Rotation, Velocity = N(velocity) };
        }
        private void Deliver(List<FrameDelivery> deliveries)
        { foreach (FrameDelivery delivery in deliveries) if (Transport.SendClient(delivery.Connection, delivery.Message)) sent++; }
        private void Send(FrameMessage message) { if (Transport.SendServer(message)) sent++; }

        internal bool TravelReady => !faulted && serverSession != Guid.Empty && Time.realtimeSinceStartup - lastWelcome <= 8;
        internal bool Authenticated(FramePeer peer, object connection, Guid client, Guid server)
            => relay.Authenticated(peer, connection, client, server);
        internal bool AcceptReconnect(ReconnectPacket packet, NativeFrameEnvelope envelope)
        {
            IPlayer player = api.Application.LocalPlayer;
            object actor = player == null ? null : map.NativeEntity(player);
            return TravelReady && actor != null && player.Id == packet.Actor && ReferenceEquals(envelope.Playfield, Transport.Context(actor))
                && packet.ClientSession == clientSession && packet.ServerSession == serverSession;
        }
        internal bool SendReconnect(ReconnectPacket packet)
        {
            if (!TravelReady) return false;
            var copy = packet.Copy(); copy.ClientSession = clientSession; copy.ServerSession = serverSession;
            return Transport.SendServerPayload(ReconnectProtocol.Envelope(copy));
        }
        internal bool SendReconnect(FramePeer peer, ReconnectPacket packet)
        {
            var copy = packet.Copy(); copy.ClientSession = peer.Session; copy.ServerSession = relay.Session;
            return Transport.SendClientPayload(peer.Connection, ReconnectProtocol.Envelope(copy));
        }
        internal FramePeer PeerFor(int id, object context) => relay.PeerFor(id, context, Time.realtimeSinceStartup);
        internal FramePeer[] Aboard(int ship, object context) => relay.Aboard(ship, context, Time.realtimeSinceStartup);
        internal bool AuthenticateTravel(TravelPacket packet, NativeFrameEnvelope envelope, out object actor, out FramePeer peer)
        {
            peer = null;
            if (!Transport.Authenticate(envelope, out int id, out actor)) return false;
            peer = PeerFor(id, envelope.Playfield);
            return relay.Authenticated(peer, envelope.Connection, packet.ClientSession, packet.ServerSession);
        }
        internal bool AcceptTravel(TravelPacket packet, NativeFrameEnvelope envelope)
        {
            object actor = api.Application.LocalPlayer == null ? null : map.NativeEntity(api.Application.LocalPlayer);
            return TravelReady && actor != null && ReferenceEquals(envelope.Playfield, Transport.Context(actor))
                && packet.ClientSession == clientSession && packet.ServerSession == serverSession;
        }
        internal bool SendTravel(TravelPacket packet)
        {
            if (!TravelReady) return false;
            TravelPacket copy = packet.Copy(); copy.ClientSession = clientSession; copy.ServerSession = serverSession;
            return Transport.SendServerPayload(TravelProtocol.EncodeEnvelope(copy));
        }
        internal bool SendTravel(FramePeer peer, TravelPacket packet)
        {
            TravelPacket copy = packet.Copy(); copy.ClientSession = peer.Session; copy.ServerSession = relay.Session;
            copy.Members = copy.Members.Where(m => m.Actor == peer.Actor).ToArray();
            return Transport.SendClientPayload(peer.Connection, TravelProtocol.EncodeEnvelope(copy));
        }

        public void Present(object actor = null)
        {
            if (faulted || !owner.MultiplayerClient || serverSession == Guid.Empty || api.ClientPlayfield == null) return;
            try
            {
                if (actor != null)
                {
                    int id = map.Id(actor);
                    if (passengers.TryGetValue(id, out RemotePassenger passenger)) PresentOne(id, passenger);
                }
                else foreach (var pair in passengers) PresentOne(pair.Key, pair.Value);
            }
            catch (Exception error) { Fault(error); }
        }
        internal bool TakeLocomotion(object actor, Vector3 nativeDelta, out Vector3 delta)
        {
            delta = Vector3.zero;
            if (faulted || !owner.MultiplayerClient || !TravelReady || api.ClientPlayfield == null
                || actor == null || !map.Bool(map.EntityRemote, actor)
                || !passengers.TryGetValue(map.Id(actor), out RemotePassenger passenger)
                || passenger.State == null || !api.ClientPlayfield.Entities.TryGetValue(passenger.State.Ship, out IEntity vessel)) return false;
            object ship = map.NativeEntity(vessel);
            var hull = ship == null ? null : map.EntityTransform.GetValue(ship) as Transform;
            if (hull == null || !passenger.TakeLocomotion(Time.realtimeSinceStartup,
                new NQuaternion(hull.rotation.x, hull.rotation.y, hull.rotation.z, hull.rotation.w), out NVector relative)) return false;
            delta = U(relative); animationSamples++;
            nativeAnimationDistance = nativeDelta.magnitude; localAnimationDistance = delta.magnitude;
            return true;
        }
        private void PresentOne(int id, RemotePassenger passenger)
        {
            FrameMessage state = passenger.State;
            if (state == null || !api.ClientPlayfield.Players.TryGetValue(id, out IPlayer player)) return;
            object native = map.NativeEntity(player);
            if (native == null || !map.Bool(map.EntityRemote, native)) return;
            if (!passenger.Sample(Time.realtimeSinceStartup, out NVector point, out NQuaternion rotation)) return;
            if (!api.ClientPlayfield.Entities.TryGetValue(state.Ship, out IEntity vessel)) return;
            object nativeShip = map.NativeEntity(vessel);
            if (nativeShip == null) return;
            var hull = map.EntityTransform.GetValue(nativeShip) as Transform;
            var root = map.EntityTransform.GetValue(native) as Transform;
            if (hull == null || root == null) return;
            // Both visual children use the observer's current displayed hull.
            // Do not move a remote physics body or claim ship authority here.
            root.SetPositionAndRotation(hull.position + hull.rotation * U(point), hull.rotation * U(rotation));
            rendered++;
        }
        private static NVector N(Vector3 v) => new NVector(v.x, v.y, v.z);
        private static Vector3 U(NVector v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion U(NQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
