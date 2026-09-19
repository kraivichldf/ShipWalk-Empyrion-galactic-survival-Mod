using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ShipWalk;

internal static class ReconnectTests
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    private static PassengerRecord Record(string identity = "player-a", int actor = 1004) => new PassengerRecord
    { Identity = identity, Actor = actor, Vessel = 5004, Area = "Orbit A", Mode = PassengerMode.Walking,
        Position = new Vector3(2, 3, 4), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), Updated = 123456 };
    private sealed class Sandbox : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "ShipWalkReconnect-" + Guid.NewGuid().ToString("N"));
        public Sandbox() { Directory.CreateDirectory(Folder); }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Folder), root = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(Path.Combine(root, "ShipWalkReconnect-"), StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test cleanup path.");
            Directory.Delete(resolved, true);
        }
    }
    private sealed class Server : IDisposable
    {
        public readonly Sandbox Disk = new Sandbox();
        public DateTime Now = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);
        public ReconnectPlayer Player = new ReconnectPlayer { Identity = "player-a", Area = "Orbit A", Online = true };
        public ReconnectVessel Vessel = new ReconnectVessel { Id = 5004, Area = "Planet B", Position = new Vector3(100, 200, 300) };
        public readonly List<ReconnectPacket> Sent = new List<ReconnectPacket>();
        public readonly List<Action<ReconnectVessel>> Lookups = new List<Action<ReconnectVessel>>();
        public ReconnectHub Hub;
        public Server() { Restart(); }
        public void Restart()
        {
            Hub?.Shutdown(); Sent.Clear(); Lookups.Clear();
            Hub = new ReconnectHub(() => Disk.Folder, _ => Player, (id, callback) => { Lookups.Add(callback); return true; },
                (area, p) => { Sent.Add(p.Copy()); return true; }, () => Now);
        }
        public ReconnectPacket Query() => new ReconnectPacket { Kind = ReconnectKind.Query, Actor = 1004, Identity = "player-a",
            Epoch = Guid.NewGuid(), ClientSession = Guid.NewGuid(), Source = Player.Area };
        public void Send(ReconnectPacket p) => Hub.Receive(p.Source, p);
        public void Locate()
        { var actions = Lookups.ToArray(); Lookups.Clear(); foreach (var callback in actions) callback(Vessel); Hub.Update(); }
        public void Seed()
        {
            var q = Query(); Send(q); var saved = q.Copy(); saved.Kind = ReconnectKind.Save; saved.Sequence = 1; saved.Record = Record();
            Send(saved); Hub.Shutdown();
        }
        public void Dispose() { Hub.Shutdown(); Disk.Dispose(); }
    }
    public static void Codec()
    {
        var p = new ReconnectPacket { Kind = ReconnectKind.Offer, Actor = 1004, Epoch = Guid.NewGuid(), Id = Guid.NewGuid(),
            ClientSession = Guid.NewGuid(), ServerSession = Guid.NewGuid(), Record = Record(), Destination = "Planet B" };
        byte[] bytes = ReconnectProtocol.Encode(p);
        Check(ReconnectProtocol.TryEnvelope(ReconnectProtocol.Envelope(p), out var copy) && copy.Id == p.Id
            && copy.Record.Vessel == 5004 && copy.Record.Rotation == p.Record.Rotation, "Lost occupied vessel or local pose.");
        for (int n = 0; n < bytes.Length; n++) Check(!ReconnectProtocol.TryDecode(bytes.Take(n).ToArray(), out _), "Accepted truncated recovery packet.");
        Check(!ReconnectProtocol.TryDecode(bytes.Concat(new byte[] { 1 }).ToArray(), out _), "Accepted trailing data.");
        Check(!ReconnectProtocol.TryEnvelope(ReconnectProtocol.Prefix + "invalid", out _), "Accepted invalid encoding.");
        p.Record.Position.X = float.NaN;
        bool refused = false; try { ReconnectProtocol.Encode(p); } catch (InvalidDataException) { refused = true; }
        Check(refused, "Non-finite save accepted.");
    }
    public static void DurableStore()
    {
        using (var disk = new Sandbox())
        using (var otherWorld = new Sandbox())
        {
            var store = new PassengerStore(disk.Folder); var record = Record(); store.Put(record); record.Position = Vector3.Zero; store.Flush();
            var restart = new PassengerStore(disk.Folder);
            Check(restart.Find("player-a", 1004).Position == new Vector3(2, 3, 4), "Save changed through alias or restart.");
            Check(restart.Find("player-a", 1005) == null && restart.Find("player-b", 1004) == null, "Restored a different character/account.");
            Check(new PassengerStore(otherWorld.Folder).Count == 0, "Cross-save recovery.");
            restart.Remove("player-a"); restart.Flush();
            Check(new PassengerStore(disk.Folder).Count == 0, "Physical departure returned after restart.");
        }
    }
    public static void DockingCoordinates()
    {
        var occupied = new LocalFramePose(new Vector3(30, 10, -5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f));
        var saved = Record(); var inCarrier = saved.IntoRoot(occupied);
        var captured = PassengerRecord.IntoMember(occupied, inCarrier.Position, inCarrier.Rotation);
        Check(Vector3.Distance(saved.Position, captured.Position) < .00001f && Math.Abs(Quaternion.Dot(saved.Rotation, captured.Rotation)) > .99999f,
            "Occupied-vessel snapshot lost pose inside the carrier.");
        var undocked = saved.IntoRoot(new LocalFramePose(Vector3.Zero, Quaternion.Identity));
        Check(undocked.Position == saved.Position, "Offline undocking retained old carrier offset.");
        var newCarrier = new LocalFramePose(new Vector3(-12, 30, 25), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -1.3f));
        var redocked = saved.IntoRoot(newCarrier);
        Check(Vector3.Distance(newCarrier.ToLocalPoint(redocked.Position), saved.Position) < .00001f, "Redocking lost saved SV position.");
    }
    public static void WriteFailureRetry()
    {
        using (var disk = new Sandbox())
        {
            var store = new PassengerStore(disk.Folder); store.Put(Record());
            string blocked = Path.Combine(disk.Folder, "ShipWalk"); File.WriteAllText(blocked, "blocked directory");
            bool failed = false; try { store.Flush(); } catch (IOException) { failed = true; }
            Check(failed && store.Count == 1, "Write failure lost pending checkpoint.");
            File.Delete(blocked); store.Flush();
            Check(new PassengerStore(disk.Folder).Count == 1, "Failed checkpoint could not be retried.");
        }
    }
    public static void CorruptSave()
    {
        using (var disk = new Sandbox())
        {
            var store = new PassengerStore(disk.Folder); store.Put(Record()); store.Flush();
            var next = Record(); next.Position.X = 9; store.Put(next); store.Flush();
            string path = Path.Combine(disk.Folder, "ShipWalk", "passengers-v1.bin");
            File.WriteAllText(path, "interrupted or corrupt save");
            var backup = new PassengerStore(disk.Folder);
            Check(backup.Error == null && backup.Find("player-a", 1004).Position.X == 2, "Valid backup not recovered.");
            File.WriteAllText(path + ".bak", "also corrupt");
            var failed = new PassengerStore(disk.Folder); failed.Put(Record()); failed.Flush();
            Check(failed.Error != null && File.ReadAllText(path) == "interrupted or corrupt save", "Corrupt evidence overwritten.");
        }
    }
    public static void RestartAndWarp()
    {
        using (var s = new Server())
        {
            s.Seed(); s.Restart(); var q = s.Query(); s.Send(q);
            Check(s.Sent.Count == 0 && s.Lookups.Count == 1, "Restoration did not wait for global ship lookup.");
            s.Locate(); var offer = s.Sent.Last();
            Check(offer.Kind == ReconnectKind.Offer && offer.Destination == "Planet B" && offer.WorldPosition == s.Vessel.Position
                && offer.Record.Vessel == 5004 && offer.Record.Position == Record().Position, "Warped occupied SV was not recovered after manager restart.");
            var premature = q.Copy(); premature.Kind = ReconnectKind.Save; premature.Record = Record(); premature.Record.Vessel = 9999; premature.Sequence = 100;
            s.Send(premature); s.Hub.Shutdown();
            Check(new PassengerStore(s.Disk.Folder).Find("player-a", 1004).Vessel == 5004, "Ordinary spawn replaced pending saved attachment.");
            var complete = q.Copy(); complete.Kind = ReconnectKind.Complete; complete.Id = offer.Id; s.Send(complete);
            Check(s.Sent.Last().Kind == ReconnectKind.Ready, "Validated restore not completed.");
            int lookups = s.Lookups.Count; s.Send(q);
            Check(s.Sent.Last().Kind == ReconnectKind.Ready && s.Lookups.Count == lookups, "Repeated login query replayed teleport.");
            s.Send(complete); Check(s.Sent.Last().Kind == ReconnectKind.Ready, "Duplicate completion did not receive acknowledgement.");
        }
    }
    public static void AuthorityAndStaleSessions()
    {
        using (var s = new Server())
        {
            s.Seed(); s.Restart(); var old = s.Query(); var foreign = old.Copy(); foreign.Identity = "player-b"; s.Send(foreign);
            Check(s.Lookups.Count == 0, "Accepted foreign identity.");
            foreign = old.Copy(); foreign.Source = "Other Area"; s.Send(foreign);
            Check(s.Lookups.Count == 0, "Accepted old worker area.");
            s.Send(old); var current = s.Query(); s.Send(current); s.Send(old); s.Locate();
            Check(s.Sent.Count == 1 && s.Sent[0].Epoch == current.Epoch, "Old login or lookup resurrected an attachment.");
            var stale = old.Copy(); stale.Kind = ReconnectKind.Clear; stale.Sequence = 10000; s.Send(stale);
            Check(new PassengerStore(s.Disk.Folder).Find("player-a", 1004) != null, "Old session cleared current save.");
            s.Player.Online = false; s.Sent.Clear(); s.Send(current); s.Locate();
            Check(s.Sent.Count == 0, "Disconnected player received restore.");
        }
    }
    public static void DisconnectRetainsDepartureClears()
    {
        using (var s = new Server())
        {
            var q = s.Query(); s.Send(q); var save = q.Copy(); save.Kind = ReconnectKind.Save; save.Record = Record(); save.Sequence = 1; s.Send(save);
            s.Player.Online = false; s.Now = s.Now.AddSeconds(3); s.Hub.Update(); s.Restart();
            Check(new PassengerStore(s.Disk.Folder).Find("player-a", 1004) != null, "Offline cleanup deleted attachment.");
            s.Player.Online = true; q = s.Query(); s.Send(q); s.Locate();
            var clear = q.Copy(); clear.Kind = ReconnectKind.Clear; clear.Sequence = 1; s.Send(clear); s.Restart();
            Check(new PassengerStore(s.Disk.Folder).Count == 0, "Death/physical departure was not durably cleared.");
        }
    }
    public static void PendingDisconnectAndMissingShip()
    {
        using (var s = new Server())
        {
            s.Seed(); s.Restart(); var q = s.Query(); s.Send(q);
            s.Player.Online = false; s.Locate(); Check(s.Sent.Count == 0, "Late lookup restored disconnected player.");
            s.Player.Online = true; s.Now = s.Now.AddSeconds(3); s.Vessel = null; s.Send(q); s.Locate();
            Check(s.Sent.Count == 0, "Missing ship sent a bogus spawn.");
            var abort = q.Copy(); abort.Kind = ReconnectKind.Abort; s.Send(abort);
            s.Send(q); Check(s.Sent.Last().Kind == ReconnectKind.Ready, "Aborted login retried indefinitely.");
            Check(new PassengerStore(s.Disk.Folder).Count == 1, "Temporary missing ship destroyed saved record.");
        }
    }
    public static void CrossWorkerAndReplay()
    {
        using (var s = new Server())
        {
            s.Seed(); s.Restart(); var q = s.Query(); s.Send(q); s.Locate(); var original = s.Sent.Last();
            s.Player.Area = "Planet B"; s.Now = s.Now.AddSeconds(3);
            var destination = q.Copy(); destination.Source = "Planet B"; destination.ClientSession = Guid.NewGuid(); s.Send(destination); s.Locate();
            var arrived = s.Sent.Last(); Check(arrived.Id == original.Id && arrived.Source == "Planet B", "Travel lost recovery transaction.");
            var stale = q.Copy(); stale.Kind = ReconnectKind.Complete; stale.Id = original.Id; s.Sent.Clear(); s.Send(stale);
            Check(s.Sent.Count == 0, "Old area completion accepted.");
            var complete = destination.Copy(); complete.Kind = ReconnectKind.Complete; complete.Id = arrived.Id; s.Send(complete);
            var save = destination.Copy(); save.Kind = ReconnectKind.Save; save.Sequence = 1; save.Record = Record(); save.Record.Area = "Planet B";
            save.Record.Position.X = 8; s.Send(save); save.Record.Position.X = 20; s.Send(save); s.Hub.Shutdown();
            Check(new PassengerStore(s.Disk.Folder).Find("player-a", 1004).Position.X == 8, "Replayed checkpoint overwrote newer state.");
        }
    }
    public static void ConfirmedDestroyedVessel()
    {
        using (var s = new Server())
        {
            s.Seed(); s.Restart(); s.Vessel = new ReconnectVessel { Id = 0 };
            var q = s.Query(); s.Send(q); s.Locate();
            Check(s.Sent.Last().Kind == ReconnectKind.Abort && new PassengerStore(s.Disk.Folder).Count == 0,
                "Confirmed deleted vessel left a recurring login recovery.");
            s.Send(q); Check(s.Sent.Last().Kind == ReconnectKind.Ready, "Destroyed vessel was retried on the same login.");
        }
    }
}
