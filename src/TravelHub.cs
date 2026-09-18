using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace ShipWalk
{
    // The manager outlives both playfield workers. It never runs character or
    // vessel physics, and it does not replace the native world-transfer code.
    internal sealed class TravelHub
    {
        private sealed class Record { public TravelPacket Packet; public DateTime Expires, Retry; public bool Committed; }
        private readonly Func<string, string, byte[], bool> send;
        private readonly Action<string> log, warn;
        private readonly Func<DateTime> clock;
        private readonly Queue<Tuple<string, string, byte[]>> incoming = new Queue<Tuple<string, string, byte[]>>();
        private readonly Dictionary<Guid, Record> records = new Dictionary<Guid, Record>();
        private readonly Dictionary<Guid, DateTime> completed = new Dictionary<Guid, DateTime>();
        public TravelHub(IModApi api) : this((receiver, world, bytes) => api.Network.SendToPlayfieldServer(receiver, world, bytes),
            message => api.Log(message), message => api.LogWarning(message), () => DateTime.UtcNow)
        {
            if (!api.Network.RegisterReceiverForPlayfieldPackets(Receive)) throw new InvalidOperationException("Travel coordinator receiver already registered.");
            api.Log("[ShipWalk] Loaded v" + typeof(TravelHub).Assembly.GetName().Version.ToString(3)
                + "; Dedicated travel coordinator ready; startup=automatic; native world transfer retained.");
        }
        internal TravelHub(Func<string, string, byte[], bool> send, Action<string> log, Action<string> warn, Func<DateTime> clock)
        { this.send = send; this.log = log; this.warn = warn; this.clock = clock; }
        internal void Receive(string sender, string source, byte[] bytes)
        {
            if (sender != "ShipWalk" || bytes == null || bytes.Length > TravelProtocol.MaxBytes) return;
            lock (incoming) { if (incoming.Count < 256) incoming.Enqueue(Tuple.Create(sender, source, (byte[])bytes.Clone())); }
        }
        public void Update()
        {
            DateTime now = clock();
            for (int n = 0; n < 64; n++)
            {
                Tuple<string, string, byte[]> item;
                lock (incoming) { if (incoming.Count == 0) break; item = incoming.Dequeue(); }
                if (!TravelProtocol.TryDecode(item.Item3, out TravelPacket p)) continue;
                if (p.Kind == TravelKind.Manifest && p.Source == item.Item2 && p.Source != p.Destination
                    && !string.IsNullOrEmpty(p.Destination) && p.Leader > 0 && p.Members.Any(m => m.Actor == p.Leader))
                {
                    if (completed.ContainsKey(p.Id)) continue;
                    if (!records.TryGetValue(p.Id, out Record record))
                    {
                        if (records.Count >= 256 || records.Values.Any(r => r.Packet.Ship == p.Ship)) continue;
                        record = new Record { Packet = p.Copy(), Expires = now.AddMinutes(5), Retry = now };
                        records.Add(p.Id, record);
                        log("[ShipWalk] Travel manifest stored; transfer=" + p.Id + "; ship=" + p.Ship
                            + "; source=" + p.Source + "; destination=" + p.Destination + "; passengers=" + p.Members.Length);
                    }
                    else if (!TravelTransfer.SameManifest(record.Packet, p)) continue;
                    TravelPacket ack = record.Packet.Copy(); ack.Kind = TravelKind.Stored;
                    send("ShipWalk", p.Source, TravelProtocol.Encode(ack));
                }
                else if (p.Kind == TravelKind.Commit && records.TryGetValue(p.Id, out Record committing)
                    && p.Source == item.Item2 && TravelTransfer.SameManifest(committing.Packet, p)) committing.Committed = true;
                else if (records.TryGetValue(p.Id, out Record record) && record.Packet.Ship == p.Ship
                    && (p.Kind == TravelKind.Cancel && item.Item2 == record.Packet.Source
                        || p.Kind == TravelKind.Arrived && item.Item2 == record.Packet.Destination))
                {
                    if (p.Kind == TravelKind.Cancel) send("ShipWalk", record.Packet.Destination, TravelProtocol.Encode(p));
                    records.Remove(p.Id);
                    completed[p.Id] = now.AddMinutes(5);
                }
            }
            foreach (var entry in records.ToArray())
            {
                Record record = entry.Value;
                if (now >= record.Expires)
                { records.Remove(entry.Key); completed[entry.Key] = now.AddMinutes(5); warn("[ShipWalk] Travel manifest expired; transfer=" + entry.Key); continue; }
                if (!record.Committed) continue;
                if (now < record.Retry) continue;
                record.Retry = now.AddSeconds(1);
                send("ShipWalk", record.Packet.Destination, TravelProtocol.Encode(record.Packet));
            }
            foreach (Guid id in completed.Where(p => now >= p.Value).Select(p => p.Key).ToArray()) completed.Remove(id);
        }
    }
}
