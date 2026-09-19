using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using NV = System.Numerics.Vector3;

namespace ShipWalk
{
    internal sealed class ReconnectPlayer { public string Identity, Area; public bool Online; }
    internal sealed class ReconnectVessel { public int Id; public string Area; public NV Position, Rotation; }
    internal sealed class ReconnectHub
    {
        private sealed class Attempt
        {
            public Guid Epoch, Id = Guid.NewGuid(), Session;
            public ReconnectPacket Query;
            public PassengerRecord Record;
            public bool Pending, Looking;
            public long Sequence;
            public DateTime Seen, Lookup;
        }
        private readonly Func<int, ReconnectPlayer> player;
        private readonly Func<int, Action<ReconnectVessel>, bool> locate;
        private readonly Func<string, ReconnectPacket, bool> send;
        private readonly Func<string> saveFolder;
        private readonly Func<DateTime> clock;
        private readonly Dictionary<string, Attempt> attempts = new Dictionary<string, Attempt>(StringComparer.Ordinal);
        private readonly Dictionary<Guid, DateTime> retired = new Dictionary<Guid, DateTime>();
        private readonly Queue<Action> callbacks = new Queue<Action>();
        private PassengerStore store;
        private DateTime nextFlush;
        public string Error { get; private set; }
        public string Status => "reconnectRecords=" + (store?.Count ?? 0) + "; reconnectPending=" + attempts.Values.Count(a => a.Pending)
            + "; reconnectError=" + (Error ?? store?.Error ?? "none");

        public ReconnectHub(IModApi api) : this(() => api.Application.GetPathFor(AppFolder.SaveGame), id =>
        {
            PlayerData? data = api.Application.GetPlayerDataFor(id);
            return data.HasValue ? new ReconnectPlayer { Identity = data.Value.SteamId, Area = data.Value.PlayfieldName, Online = data.Value.IsOnline } : null;
        }, (id, ready) => api.Application.GetStructure(id, info => ready(new ReconnectVessel { Id = info.id, Area = info.PlayfieldName,
            Position = new NV(info.pos.x, info.pos.y, info.pos.z), Rotation = new NV(info.rot.x, info.rot.y, info.rot.z) })),
            (area, p) => api.Network.SendToPlayfieldServer("ShipWalk", area, ReconnectProtocol.Encode(p)), () => DateTime.UtcNow) { }

        internal ReconnectHub(Func<string> folder, Func<int, ReconnectPlayer> player,
            Func<int, Action<ReconnectVessel>, bool> locate, Func<string, ReconnectPacket, bool> send, Func<DateTime> clock)
        { saveFolder = folder; this.player = player; this.locate = locate; this.send = send; this.clock = clock; }

        private bool Current(ReconnectPacket p, string area, out string identity)
        {
            ReconnectPlayer live = player(p.Actor);
            identity = live?.Identity;
            return live != null && live.Online && !string.IsNullOrEmpty(live.Identity)
                && (string.IsNullOrEmpty(p.Identity) || live.Identity == p.Identity) && live.Area == area && p.Source == area;
        }
        public void Receive(string area, ReconnectPacket p)
        {
            try { ReceiveCore(area, p); }
            catch (Exception e) { Error = e.GetBaseException().Message; }
        }
        private void ReceiveCore(string area, ReconnectPacket p)
        {
            if (!Current(p, area, out string identity) || p.ClientSession == Guid.Empty) return;
            // The worker authenticates the native connection and actor. The
            // manager resolves that actor's account; IPlayer.SteamId on workers
            // reads process-wide settings in build 5150, not the remote player.
            p = p.Copy(); p.Identity = identity;
            if (store == null) store = new PassengerStore(saveFolder());
            if (store.Error != null) { Reply(p, ReconnectKind.Ready, store.Error); return; }
            DateTime now = clock();
            attempts.TryGetValue(p.Identity, out Attempt a);
            if (p.Kind == ReconnectKind.Query)
            {
                if (retired.ContainsKey(p.Epoch)) return;
                if (a == null || a.Epoch != p.Epoch)
                {
                    if (attempts.Count >= 10000 && a == null) { Reply(p, ReconnectKind.Ready, "Reconnect capacity reached."); return; }
                    PassengerRecord saved = store.Find(p.Identity, p.Actor);
                    if (a != null) retired[a.Epoch] = now.AddHours(1);
                    a = new Attempt { Epoch = p.Epoch, Record = saved, Pending = saved != null };
                    attempts[p.Identity] = a;
                }
                if (a.Session != p.ClientSession) { a.Session = p.ClientSession; a.Sequence = 0; }
                a.Query = p.Copy(); a.Seen = now;
                if (!a.Pending) { Reply(p, ReconnectKind.Ready, ""); return; }
                if (a.Looking || now < a.Lookup) return;
                a.Lookup = now.AddSeconds(2); a.Looking = true;
                Attempt expected = a; Guid session = a.Session;
                bool queued = locate(a.Record.Vessel, vessel =>
                {
                    lock (callbacks) if (callbacks.Count < 256) callbacks.Enqueue(() => Located(p.Identity, expected, session, vessel));
                });
                if (!queued) a.Looking = false;
                return;
            }
            if (a == null || a.Epoch != p.Epoch || a.Session != p.ClientSession) return;
            a.Seen = now;
            if (p.Kind == ReconnectKind.Complete && a.Pending && p.Id == a.Id)
            { a.Pending = false; Reply(p, ReconnectKind.Ready, "Restored aboard saved vessel."); }
            else if (p.Kind == ReconnectKind.Complete && !a.Pending && p.Id == a.Id) Reply(p, ReconnectKind.Ready, "");
            else if (p.Kind == ReconnectKind.Abort)
            { a.Pending = false; Reply(p, ReconnectKind.Ready, p.Reason); }
            else if (p.Kind == ReconnectKind.Clear && p.Sequence > a.Sequence)
            {
                a.Sequence = p.Sequence; a.Pending = false; store.Remove(p.Identity); store.Flush();
                Reply(p, ReconnectKind.Ready, "");
            }
            else if (p.Kind == ReconnectKind.Save && !a.Pending && p.Sequence > a.Sequence && p.Record != null
                && p.Record.Identity == p.Identity && p.Record.Actor == p.Actor && p.Record.Area == area)
            {
                a.Sequence = p.Sequence; var record = p.Record.Copy(); record.Updated = now.Ticks; store.Put(record);
            }
        }
        private void Located(string identity, Attempt expected, Guid session, ReconnectVessel vessel)
        {
            if (!attempts.TryGetValue(identity, out Attempt a) || !ReferenceEquals(a, expected)) return;
            a.Looking = false;
            if (!a.Pending || a.Session != session || !Current(a.Query, a.Query.Source, out _)) return;
            if (vessel != null && vessel.Id <= 0)
            {
                store.Remove(identity); store.Flush(); a.Pending = false;
                Reply(a.Query, ReconnectKind.Abort, "Saved vessel no longer exists; using the original login position."); return;
            }
            if (vessel == null || vessel.Id != a.Record.Vessel || string.IsNullOrEmpty(vessel.Area)
                || !MotionMath.Finite(vessel.Position) || !MotionMath.Finite(vessel.Rotation)) return;
            var offer = a.Query.Copy(); offer.Kind = ReconnectKind.Offer; offer.Id = a.Id; offer.Record = a.Record.Copy();
            offer.Destination = vessel.Area; offer.WorldPosition = vessel.Position; offer.WorldRotation = vessel.Rotation;
            send(offer.Source, offer);
        }
        private void Reply(ReconnectPacket query, ReconnectKind kind, string reason)
        { var p = query.Copy(); p.Kind = kind; p.Record = null; p.Reason = reason; send(p.Source, p); }
        public void Update()
        {
            try
            {
                for (int i = 0; i < 64; i++)
                {
                    Action action; lock (callbacks) { if (callbacks.Count == 0) break; action = callbacks.Dequeue(); } action();
                }
                DateTime now = clock();
                if (now >= nextFlush)
                {
                    nextFlush = now.AddSeconds(2); store?.Flush();
                    foreach (var key in attempts.Where(p => now - p.Value.Seen > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray()) attempts.Remove(key);
                    foreach (var key in retired.Where(p => now >= p.Value).Select(p => p.Key).ToArray()) retired.Remove(key);
                    // A missing native callback must never permanently stall a retry.
                    foreach (var a in attempts.Values) if (now - a.Lookup > TimeSpan.FromSeconds(8)) a.Looking = false;
                }
            }
            catch (Exception e) { Error = e.GetBaseException().Message; }
        }
        public void Shutdown() { try { store?.Flush(); } catch (Exception e) { Error = e.GetBaseException().Message; } }
    }
}
