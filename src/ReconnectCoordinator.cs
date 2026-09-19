using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using UnityEngine;
using NV = System.Numerics.Vector3;
using NQ = System.Numerics.Quaternion;

namespace ShipWalk
{
    internal sealed class ReconnectCoordinator
    {
        private sealed class Client
        {
            public Guid Epoch, Session;
            public string Identity, Area;
            public bool Ready;
            public float NextSave, Seen;
            public long Sequence;
            public ReconnectPacket Offer;
        }
        private readonly Runtime owner;
        private readonly IModApi api;
        private Build5150 Map => owner.Map;
        private SharedFrameNetwork Network => owner.Network;
        private NativeFrameTransport Transport => Network.Transport;
        private readonly Dictionary<int, Client> clients = new Dictionary<int, Client>();
        private readonly Queue<Tuple<ReconnectPacket, NativeFrameEnvelope>> incoming = new Queue<Tuple<ReconnectPacket, NativeFrameEnvelope>>();
        private readonly Queue<ReconnectPacket> hub = new Queue<ReconnectPacket>();
        private readonly ReconnectHold hold;
        private Guid epoch = Guid.NewGuid();
        private ReconnectPacket ticket;
        private bool done, moved, completing, inhibited;
        private float started = -1, nextQuery, nextArm, nextTeleport;
        private string fallbackArea, failure;
        private Vector3 fallbackPoint, fallbackRotation;
        public bool Routing { get; private set; }
        public bool Blocking => owner.MultiplayerClient && !done;
        public bool Restoring => Blocking && ticket != null;
        public string Status => "reconnect=" + (owner.PlayfieldServer ? "Worker" : done ? "Ready" : ticket == null ? "Checking" : "Restoring")
            + "; reconnectHold=" + hold.Held + "; reconnectAge=" + (started < 0 ? 0 : Now - started).ToString("F2")
            + "; reconnectResult=" + (failure ?? "none");
        private static float Now => Time.realtimeSinceStartup;
        public ReconnectCoordinator(Runtime owner, IModApi api)
        { this.owner = owner; this.api = api; hold = new ReconnectHold(owner, api); }
        public bool Owns(Component controller) => hold.Owns(controller);
        public bool OwnsActor(object actor) => hold.OwnsActor(actor) || Restoring && owner.Frame.MatchesActor(actor);
        public void Receive(ReconnectPacket p, NativeFrameEnvelope envelope)
        { lock (incoming) if (incoming.Count < 128) incoming.Enqueue(Tuple.Create(p, envelope)); }
        public void ReceiveHub(string source, ReconnectPacket p)
        { if (p.Source == source) lock (hub) if (hub.Count < 128) hub.Enqueue(p); }
        public void Reset(bool left)
        {
            hold.Release();
            if (!left) return;
            ticket = null; epoch = Guid.NewGuid(); moved = completing = false; done = inhibited;
            started = -1; nextQuery = nextArm = nextTeleport = 0; fallbackArea = failure = null;
        }
        public void Disable()
        {
            inhibited = true;
            if (owner.MultiplayerClient && api.Application.LocalPlayer != null)
                Network.SendReconnect(Message(ReconnectKind.Clear));
            Abort("disabled by player", false);
        }
        public void Enable() { inhibited = false; }
        public void PlacementFailed() => Abort("No clear standing position near the saved point; original login position restored.", true);
        private ReconnectPacket Message(ReconnectKind kind) => new ReconnectPacket { Kind = kind, Epoch = epoch,
            Actor = api.Application.LocalPlayer?.Id ?? 0, Id = ticket?.Id ?? Guid.Empty };
        public void Update()
        {
            try
            {
                for (int n = 0; n < 64; n++)
                {
                    Tuple<ReconnectPacket, NativeFrameEnvelope> item;
                    lock (incoming) { if (incoming.Count == 0) break; item = incoming.Dequeue(); }
                    if (owner.PlayfieldServer) FromClient(item.Item1, item.Item2);
                    else if (owner.MultiplayerClient) FromServer(item.Item1, item.Item2);
                }
                if (owner.PlayfieldServer)
                {
                    for (int n = 0; n < 64; n++)
                    { ReconnectPacket p; lock (hub) { if (hub.Count == 0) break; p = hub.Dequeue(); } FromHub(p); }
                    foreach (int id in clients.Keys.ToArray())
                    {
                        Client c = clients[id]; object actor = Transport.Entity(id);
                        if (actor != null && Map.Player(actor)?.Health <= 0 && c.Ready)
                        { Capture(actor, new FrameMessage { Mode = PassengerMode.World }); c.Ready = false; }
                        if (Now - c.Seen > 30) clients.Remove(id);
                    }
                }
                else if (owner.MultiplayerClient) UpdateClient();
            }
            catch (Exception e)
            {
                failure = e.GetBaseException().Message;
                if (owner.MultiplayerClient) Abort("Reconnect error: " + failure, true);
            }
        }
        private void FromClient(ReconnectPacket p, NativeFrameEnvelope envelope)
        {
            if (!Transport.Authenticate(envelope, out int actor, out object native) || actor != p.Actor) return;
            FramePeer peer = Network.PeerFor(actor, envelope.Playfield);
            if (!Network.Authenticated(peer, envelope.Connection, p.ClientSession, p.ServerSession)) return;
            IPlayer player = Map.Player(native);
            if (player == null || string.IsNullOrEmpty(player.SteamId)) return;
            string area = owner.Travel.Native.ContextName(envelope.Playfield);
            clients.TryGetValue(actor, out Client c);
            if (p.Kind == ReconnectKind.Query)
            {
                if (c == null || c.Session != peer.Session)
                { c = new Client { Epoch = p.Epoch, Session = peer.Session, Identity = player.SteamId, Area = area }; clients[actor] = c; }
                if (c.Epoch != p.Epoch || Now - c.Seen < .5f) return;
                c.Seen = Now; SendHub(c, actor, p); return;
            }
            if (c == null || c.Epoch != p.Epoch || c.Session != peer.Session) return;
            if (p.Kind == ReconnectKind.Complete)
            {
                if (c.Offer == null || p.Id != c.Offer.Id) return;
                object member = Transport.Entity(c.Offer.Record.Vessel);
                bool seated = member != null && ReferenceEquals(Map.SeatedShip.GetValue(native), member);
                bool walking = peer.State?.Member == c.Offer.Record.Vessel && peer.State.Mode != PassengerMode.World
                    && DockingVessels.Member(Map, Transport.Entity(peer.State.Ship), member);
                if (!seated && !walking || player.Health <= 0) return;
                SendHub(c, actor, p);
            }
            else if (p.Kind == ReconnectKind.Abort || p.Kind == ReconnectKind.Clear)
            { p.Sequence = ++c.Sequence; SendHub(c, actor, p); }
        }
        private void FromHub(ReconnectPacket p)
        {
            if (!clients.TryGetValue(p.Actor, out Client c) || p.Epoch != c.Epoch || p.ClientSession != c.Session
                || p.Identity != c.Identity || p.Source != c.Area) return;
            object actor = Transport.Entity(p.Actor); object context = Transport.Context(actor);
            FramePeer peer = Network.PeerFor(p.Actor, context);
            if (peer == null || peer.Session != c.Session) return;
            if (p.Kind == ReconnectKind.Ready || p.Kind == ReconnectKind.Abort) { c.Ready = true; c.Offer = null; }
            else if (p.Kind == ReconnectKind.Offer && p.Record != null && p.Record.Identity == c.Identity)
            {
                c.Ready = false; c.Offer = p.Copy();
                // The database supplies the area; a loaded worker supplies the live pose.
                object member = Transport.Entity(p.Record.Vessel);
                if (DockingVessels.Valid(Map, member) && owner.Travel.Native.ContextName(Transport.Context(member)) == c.Area)
                {
                    p.Destination = c.Area;
                    LocalFramePose pose = DockingVessels.Pose(Map, member);
                    p.WorldPosition = pose.Position; p.WorldRotation = N(U(pose.Rotation).eulerAngles);
                }
            }
            else return;
            Network.SendReconnect(peer, p);
        }
        private void SendHub(Client c, int actor, ReconnectPacket packet)
        {
            var p = packet.Copy(); p.Identity = c.Identity; p.Actor = actor; p.Source = c.Area;
            p.Epoch = c.Epoch; p.ClientSession = c.Session;
            api.Network.SendToDedicatedServer("ShipWalk", ReconnectProtocol.Encode(p), c.Area);
        }
        public void Capture(object actor, FrameMessage accepted)
        {
            try { CaptureCore(actor, accepted); }
            catch (Exception e) { failure = e.GetBaseException().Message; }
        }
        private void CaptureCore(object actor, FrameMessage accepted)
        {
            if (!owner.PlayfieldServer || !clients.TryGetValue(Map.Id(actor), out Client c) || !c.Ready) return;
            object context = Transport.Context(actor); FramePeer peer = Network.PeerFor(Map.Id(actor), context);
            if (peer == null || peer.Session != c.Session) return;
            if (accepted.Mode == PassengerMode.World)
            {
                SendHub(c, peer.Actor, new ReconnectPacket { Kind = ReconnectKind.Clear, Sequence = ++c.Sequence }); return;
            }
            if (Now < c.NextSave) return; c.NextSave = Now + 1;
            object root = Transport.Entity(accepted.Ship), member = Transport.Entity(accepted.Member > 0 ? accepted.Member : accepted.Ship);
            if (!DockingVessels.Member(Map, root, member)) return;
            LocalFramePose local = PassengerRecord.IntoMember(DockingVessels.RelativePose(Map, root, member), accepted.Position, accepted.Rotation);
            var saved = new PassengerRecord { Identity = c.Identity, Actor = peer.Actor, Vessel = Map.Id(member), Area = c.Area,
                Mode = accepted.Mode, Position = local.Position, Rotation = local.Rotation };
            if (!saved.Valid) return;
            SendHub(c, peer.Actor, new ReconnectPacket { Kind = ReconnectKind.Save, Sequence = ++c.Sequence, Record = saved });
        }
        private void FromServer(ReconnectPacket p, NativeFrameEnvelope envelope)
        {
            if (p.Epoch != epoch || !Network.AcceptReconnect(p, envelope)) return;
            if (p.Kind == ReconnectKind.Abort && !done) { Abort(p.Reason, true); return; }
            if (p.Kind == ReconnectKind.Ready)
            {
                if (ticket != null && !completing) return;
                done = true; completing = false; ticket = null; hold.Release();
                if (!string.IsNullOrEmpty(p.Reason)) failure = p.Reason;
            }
            else if (p.Kind == ReconnectKind.Offer && !done && p.Record != null && p.Record.Actor == p.Actor
                && p.Id != Guid.Empty && (ticket == null || ticket.Id == p.Id)) ticket = p.Copy();
        }
        private void UpdateClient()
        {
            if (inhibited) { hold.Release(); return; }
            IPlayer player = api.Application.LocalPlayer;
            if (api.Application.State != GameState.Running || player == null) { hold.Release(); return; }
            if (started < 0 && Network.TravelReady)
            {
                started = Now; fallbackArea = api.ClientPlayfield?.Name;
                fallbackPoint = player.Position; fallbackRotation = player.Rotation.eulerAngles;
            }
            if (Network.TravelReady && Now >= nextQuery)
            {
                nextQuery = Now + 2; Network.SendReconnect(Message(ReconnectKind.Query));
                if (completing) Network.SendReconnect(Message(ReconnectKind.Complete));
            }
            if (done || started < 0) return;
            if (player.Health <= 0) { Network.SendReconnect(Message(ReconnectKind.Clear)); Abort("player died", false); return; }
            if (Now - started > 90) { Abort("Ship restoration timed out; original login position restored.", true); return; }
            if (!owner.Frame.HasSession && player.DrivingEntity == null) hold.Hold();
            if (ticket == null || !Network.TravelReady || completing) return;
            if (player.DrivingEntity != null)
            {
                if (player.DrivingEntity.Id == ticket.Record.Vessel)
                { completing = true; Network.SendReconnect(Message(ReconnectKind.Complete)); }
                else Abort("Native login restored another seat; retaining that seat.", false);
                return;
            }
            if (api.ClientPlayfield?.Name != ticket.Destination)
            {
                if (Now >= nextTeleport)
                {
                    nextTeleport = Now + 10; hold.Release();
                    Vector3 point = U(ticket.WorldPosition) + Quaternion.Euler(U(ticket.WorldRotation)) * U(ticket.Record.Position);
                    Teleport(player, ticket.Destination, point, U(ticket.WorldRotation)); moved = true;
                }
                return;
            }
            if (!api.ClientPlayfield.Entities.TryGetValue(ticket.Record.Vessel, out IEntity vessel) || vessel.Structure?.IsReady != true)
            {
                // Streaming uses a world position. Refresh from the destination worker, then prepare the local scene.
                if (Now >= nextTeleport)
                { nextTeleport = Now + 5; hold.Release(); player.Teleport(U(ticket.WorldPosition)); moved = true; }
                return;
            }
            object native = Map.NativeEntity(vessel), root = DockingVessels.Root(Map, native);
            if (root == null) return;
            if (owner.Frame.Active && owner.Frame.ShipId == Map.Id(root))
            { completing = true; hold.Release(); Network.SendReconnect(Message(ReconnectKind.Complete)); return; }
            if (owner.Frame.HasSession || Now < nextArm) return;
            nextArm = Now + 1;
            LocalFramePose local = ticket.Record.IntoRoot(DockingVessels.RelativePose(Map, root, native));
            var arrival = new TravelMember { Actor = player.Id, Ship = Map.Id(root), Member = vessel.Id, Mode = PassengerMode.Walking,
                Position = local.Position, Rotation = local.Rotation, Velocity = NV.Zero };
            hold.Release();
            try { owner.Frame.Arm(Map.Entity(root), false, arrival); moved = true; }
            catch (NotSupportedException e) { failure = e.Message; }
        }
        private void Teleport(IPlayer player, string area, Vector3 point, Vector3 rotation)
        {
            Routing = true;
            try
            {
                if (api.ClientPlayfield?.Name == area) player.Teleport(point);
                else if (!player.Teleport(area, point, rotation)) throw new InvalidOperationException("Native player transfer refused the destination.");
            }
            finally { Routing = false; }
        }
        private void Abort(string reason, bool fallback)
        {
            if (reason.Length > 240) reason = reason.Substring(0, 240);
            if (!done && api.Application.LocalPlayer != null)
            { var p = Message(ReconnectKind.Abort); p.Reason = reason; Network.SendReconnect(p); }
            bool restoring = Restoring; done = true; completing = false; ticket = null; failure = reason;
            hold.Release(); if (restoring) owner.StopFrame("reconnect cancelled", false);
            if (fallback && moved && api.Application.State == GameState.Running && api.Application.LocalPlayer?.Health > 0
                && !string.IsNullOrEmpty(fallbackArea) && api.Application.LocalPlayer.DrivingEntity == null)
            {
                moved = false;
                try { Teleport(api.Application.LocalPlayer, fallbackArea, fallbackPoint, fallbackRotation); }
                catch (Exception e) { failure += "; fallback: " + e.GetBaseException().Message; }
            }
        }
        private static NV N(Vector3 v) => new NV(v.x, v.y, v.z);
        private static Vector3 U(NV v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion U(NQ q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    internal sealed class ReconnectHold
    {
        private readonly Runtime owner;
        private readonly IModApi api;
        private Component controller;
        private Rigidbody body;
        private object actor;
        private bool kinematic;
        private Vector3 point;
        public ReconnectHold(Runtime owner, IModApi api) { this.owner = owner; this.api = api; }
        public bool Held => body != null;
        public bool Owns(Component c) => body != null && controller == c;
        public bool OwnsActor(object candidate) => body != null && ReferenceEquals(actor, candidate);
        public void Hold()
        {
            Build5150 map = owner.Map;
            object current = map.NativeEntity(api.Application.LocalPlayer);
            if (actor != null && !ReferenceEquals(actor, current)) Release();
            if (body == null)
            {
                controller = UnityEngine.Object.FindObjectsOfType(map.ControllerType, true).OfType<Component>()
                    .FirstOrDefault(c => ReferenceEquals(map.ControllerEntity.GetValue(c), current));
                if (controller == null || !(map.ControllerBody.GetValue(controller) is Rigidbody b) || !b.gameObject.activeInHierarchy) return;
                actor = current; body = b; kinematic = b.isKinematic; point = b.position + map.OriginOffset; b.isKinematic = true;
            }
            body.position = point - map.OriginOffset;
            if (map.EntityTransform.GetValue(actor) is Transform root) root.position = body.position;
        }
        public void Release()
        { if (body != null && body.isKinematic) body.isKinematic = kinematic; body = null; controller = null; actor = null; }
    }
}
