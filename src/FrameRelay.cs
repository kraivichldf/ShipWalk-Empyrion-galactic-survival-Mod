using System;
using System.Collections.Generic;
using System.Linq;

namespace ShipWalk
{
    internal sealed class FramePeer
    {
        public int Actor;
        public object Connection, Context;
        public Guid Session;
        public FrameMessage State;
    }
    internal sealed class FrameDelivery
    {
        public object Connection;
        public FrameMessage Message;
    }
    // Native authentication and geometry access stay outside this pure state
    // machine so the actual sessions, relay and teardown are executable tests.
    internal sealed class FrameRelay
    {
        private sealed class Peer
        {
            public int Actor;
            public object Connection, Context;
            public Guid Session;
            public double Seen, Published, Window;
            public int Received;
            public FrameSequence Input = new FrameSequence();
            public FrameMessage State;
            public readonly Dictionary<int, uint> Receipts = new Dictionary<int, uint>();
        }
        public Guid Session { get; } = Guid.NewGuid();
        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private uint sequence, generation;
        public int Count => peers.Count;
        public long Accepted { get; private set; }
        public long Rejected { get; private set; }
        public long ObserverReceipts { get; private set; }
        public int LastReceiptObserver { get; private set; }
        public int LastReceiptActor { get; private set; }

        public FramePeer PeerFor(int actor, object context, double now)
        {
            if (!peers.TryGetValue(actor, out Peer peer) || !ReferenceEquals(context, peer.Context) || now - peer.Seen > 8) return null;
            return new FramePeer { Actor = actor, Connection = peer.Connection, Context = context, Session = peer.Session,
                State = now - peer.Published <= 2 ? peer.State?.Copy() : null };
        }
        public FramePeer[] Aboard(int ship, object context, double now) => peers.Keys.Select(id => PeerFor(id, context, now))
            .Where(p => p?.State != null && p.State.Ship == ship && p.State.Mode != PassengerMode.World).ToArray();
        public bool Authenticated(FramePeer peer, object connection, Guid client, Guid server)
            => peer != null && ReferenceEquals(peer.Connection, connection) && peer.Session == client && Session == server;

        public List<FrameDelivery> Receive(int actor, object connection, object context, FrameMessage message, double now,
            Func<FrameMessage, FrameMessage, bool> validate, Func<FrameMessage, FrameMessage> shipPose = null)
        {
            var output = new List<FrameDelivery>();
            if (actor <= 0 || connection == null || context == null) { Rejected++; return output; }
            peers.TryGetValue(actor, out Peer peer);
            if (message.Kind == FrameMessageKind.Hello && message.ServerSession == Guid.Empty)
            {
                if (peer != null && (!ReferenceEquals(peer.Connection, connection) || peer.Session != message.ClientSession
                    || !ReferenceEquals(peer.Context, context)))
                { Depart(peer, output); peers.Remove(actor); peer = null; }
                if (peer == null)
                {
                    peer = new Peer { Actor = actor, Connection = connection, Context = context, Session = message.ClientSession, Window = now };
                    peers.Add(actor, peer);
                }
                if (!Budget(peer, now)) { Rejected++; return output; }
                peer.Seen = now;
                Send(peer, new FrameMessage { Kind = FrameMessageKind.Welcome, Actor = actor }, output);
                foreach (Peer other in peers.Values)
                    if (other.State != null && ReferenceEquals(other.Context, context)) Send(peer, other.State, output);
                return output;
            }
            if (peer == null || !ReferenceEquals(connection, peer.Connection) || !ReferenceEquals(context, peer.Context)
                || message.ClientSession != peer.Session || message.ServerSession != Session || !Budget(peer, now))
            { Rejected++; return output; }
            peer.Seen = now;
            if (message.Kind == FrameMessageKind.ShipPoseRequest)
            {
                if (message.Actor != 0 || message.Sequence == 0 || message.Generation == 0
                    || message.Ship <= 0 || message.Mode == PassengerMode.World
                    || !validate(message, peer.State)) { Rejected++; return output; }
                FrameMessage snapshot = shipPose?.Invoke(message);
                if (snapshot != null)
                {
                    snapshot.Kind = FrameMessageKind.ShipPose; snapshot.Actor = actor; snapshot.Ship = message.Ship;
                    snapshot.Generation = message.Generation; snapshot.Sequence = message.Sequence;
                    snapshot.Purpose = message.Purpose;
                    snapshot.Tick = (long)(now * 1000); Send(peer, snapshot, output);
                }
                return output;
            }
            if (message.Kind == FrameMessageKind.Receipt)
            {
                if (message.Actor != actor && peers.TryGetValue(message.Actor, out Peer source)
                    && source.State != null && ReferenceEquals(source.Context, context)
                    && message.Generation == source.State.Generation && message.Sequence <= source.State.Sequence && message.Sequence > 0
                    && (!peer.Receipts.TryGetValue(message.Actor, out uint acknowledged) || acknowledged != message.Generation))
                { peer.Receipts[message.Actor] = message.Generation; ObserverReceipts++; LastReceiptObserver = actor; LastReceiptActor = message.Actor; }
                return output;
            }
            if (message.Kind != FrameMessageKind.Publish || message.Actor != 0 || !validate(message, peer.State)
                || !peer.Input.Accept(message)) { Rejected++; return output; }
            uint attachment = peer.State != null && peer.State.Ship == message.Ship ? peer.State.Generation : ++generation;
            FrameMessage state = message.Copy(); state.Kind = FrameMessageKind.State;
            state.Actor = actor; state.Generation = attachment; state.Sequence = ++sequence;
            state.Tick = (long)(now * 1000); peer.State = state; peer.Published = now; Accepted++;
            Broadcast(peer, state, output); return output;
        }
        public List<FrameDelivery> Tick(double now, Func<int, object, object, bool> connected)
        {
            var output = new List<FrameDelivery>();
            foreach (Peer peer in peers.Values.ToArray())
            {
                if (now - peer.Seen > 10 || !connected(peer.Actor, peer.Connection, peer.Context))
                { Depart(peer, output); peers.Remove(peer.Actor); }
                else if (peer.State != null && peer.State.Mode != PassengerMode.World && now - peer.Published > 2)
                    Depart(peer, output);
            }
            return output;
        }
        private static bool Budget(Peer peer, double now)
        {
            if (now - peer.Window >= 1) { peer.Window = now; peer.Received = 0; }
            return ++peer.Received <= 64;
        }
        private void Depart(Peer peer, List<FrameDelivery> output)
        {
            if (peer.State == null) return;
            var state = new FrameMessage { Kind = FrameMessageKind.State, Actor = peer.Actor,
                Generation = ++generation, Sequence = ++sequence, Mode = PassengerMode.World };
            peer.State = state; Broadcast(peer, state, output);
        }
        private void Broadcast(Peer source, FrameMessage state, List<FrameDelivery> output)
        {
            foreach (Peer peer in peers.Values)
                if (ReferenceEquals(source.Context, peer.Context)) Send(peer, state, output);
        }
        private void Send(Peer peer, FrameMessage message, List<FrameDelivery> output)
        {
            FrameMessage copy = message.Copy(); copy.ClientSession = peer.Session; copy.ServerSession = Session;
            output.Add(new FrameDelivery { Connection = peer.Connection, Message = copy });
        }
    }
}
