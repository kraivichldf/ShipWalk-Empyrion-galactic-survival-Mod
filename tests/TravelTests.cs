using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using HarmonyLib;
using ShipWalk;
using UV = UnityEngine.Vector3;

internal static class TravelTests
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    private static TravelPacket Plan() => new TravelPacket { Id = Guid.NewGuid(), Kind = TravelKind.Prepare, Ship = 1015,
        Leader = 1004, Source = "Temperate Orbit", Destination = "Temperate", Position = new Vector3(15, 1070, -40),
        Members = new[] { Member(1004, PassengerMode.Seated), Member(2004, PassengerMode.Walking) } };
    private static TravelMember Member(int actor, PassengerMode mode) => new TravelMember { Actor = actor, Ship = 1015,
        Mode = mode, Position = new Vector3(3.5f, 16, 11), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), Velocity = new Vector3(0, 0, 2) };
    private static TravelPacket Ready(TravelPacket p, int index)
    { var r = p.Copy(); r.Kind = TravelKind.Ready; r.Members = new[] { p.Members[index].Copy() }; return r; }
    internal static void Codec()
    {
        var p = Plan(); p.ClientSession = Guid.NewGuid(); p.ServerSession = Guid.NewGuid();
        byte[] bytes = TravelProtocol.Encode(p);
        Check(TravelProtocol.TryDecode(bytes, out var read) && TravelTransfer.SameManifest(p, read)
            && p.ClientSession == read.ClientSession && p.ServerSession == read.ServerSession, "Travel manifest lost identity or local pose.");
        Check(TravelProtocol.TryEnvelope(TravelProtocol.EncodeEnvelope(p), out read) && read.Members.Length == 2, "Native string envelope failed.");
        for (int i = 0; i < bytes.Length; i++) Check(!TravelProtocol.TryDecode(bytes.Take(i).ToArray(), out _), "Truncated manifest accepted.");
        Check(!TravelProtocol.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _), "Trailing bytes accepted.");
        bytes[0] = 99; Check(!TravelProtocol.TryDecode(bytes, out _), "Unknown version accepted.");
        Check(!TravelProtocol.TryEnvelope(TravelProtocol.Prefix + "@@", out _), "Bad base64 accepted.");
    }
    internal static void InvalidMembers()
    {
        var p = Plan(); p.Members[1].Actor = p.Leader; Check(!TravelProtocol.Valid(p), "Duplicate actor accepted.");
        p = Plan(); p.Members[1].Ship++; Check(!TravelProtocol.Valid(p), "Cross-vessel manifest accepted.");
        p = Plan(); p.Members[0].Position.X = float.NaN; Check(!TravelProtocol.Valid(p), "NaN local point accepted.");
        p = Plan(); p.Members[0].Rotation = default; Check(!TravelProtocol.Valid(p), "Zero rotation accepted.");
        p = Plan(); p.Members[0].Mode = PassengerMode.World; Check(!TravelProtocol.Valid(p), "Detached passenger accepted.");
        p = Plan(); p.Id = Guid.Empty; Check(!TravelProtocol.Valid(p), "Missing transfer identity accepted.");
    }
    internal static void CommitLifecycle()
    {
        var p = Plan(); var transfer = new TravelTransfer(p);
        Check(!transfer.Persist() && !transfer.Commit(), "Unprepared transfer committed.");
        Check(transfer.Ready(1004, Ready(p, 0), _ => true) && !transfer.AllReady, "Pilot readiness lost passenger wait.");
        Check(transfer.Ready(2004, Ready(p, 1), _ => true) && transfer.Persist(), "Complete roster did not persist.");
        var ack = transfer.Packet.Copy(); ack.Kind = TravelKind.Stored;
        Check(transfer.Stored(ack) && transfer.Commit() && !transfer.Commit(), "Commit was missing or repeated.");
        Check(!transfer.Cancel() && !transfer.Ready(2004, Ready(p, 1), _ => true), "Committed transfer mutated.");
        Check(TravelTransfer.PassengerIds(new[] { 1004, 3004 }, new[] { 1004, 2004, 3004 }, 1004).SequenceEqual(new[] { 3004, 2004 }), "Native seats/walking IDs duplicated the leader.");
    }
    internal static void SpoofAndCancel()
    {
        var p = Plan(); var transfer = new TravelTransfer(p); var answer = Ready(p, 1);
        Check(!transfer.Ready(1004, answer, _ => true), "One player acknowledged another player.");
        answer.Destination = "Other planet"; Check(!transfer.Ready(2004, answer, _ => true), "Client chose destination.");
        answer = Ready(p, 1); answer.Id = Guid.NewGuid(); Check(!transfer.Ready(2004, answer, _ => true), "Unrelated transfer accepted.");
        answer = Ready(p, 1); Check(!transfer.Ready(2004, answer, _ => false), "Geometry/life-state rejection ignored.");
        answer.Members[0].Mode = PassengerMode.Seated; Check(!transfer.Ready(2004, answer, _ => true), "Seat change during prepare accepted.");
        Check(transfer.Cancel() && !transfer.Persist() && !transfer.Ready(2004, Ready(p, 1), _ => true), "Late packet revived cancellation.");
    }
    internal static void ImmutableRetry()
    {
        var p = Plan(); var transfer = new TravelTransfer(p); var answer = Ready(p, 1);
        Check(transfer.Ready(2004, answer, _ => true), "Readiness rejected.");
        answer.Members[0].Position.X += 20;
        Check(transfer.Ready(2004, answer, _ => true) && transfer.Packet.Members[1].Position == p.Members[1].Position, "Retry rewrote accepted pose.");
        transfer.Ready(1004, Ready(p, 0), _ => true); transfer.Persist();
        var ack = transfer.Packet.Copy(); ack.Kind = TravelKind.Stored; ack.Members[1].Position.X++;
        Check(!transfer.Stored(ack), "Manager acknowledged a different snapshot.");
        ack = transfer.Packet.Copy(); ack.Kind = TravelKind.Stored; ack.ClientSession = Guid.NewGuid();
        Check(transfer.Stored(ack), "Transport-only session stamping changed manifest identity.");
    }
    internal static void RosterSessions()
    {
        var relay = new FrameRelay(); var context = new object(); var connection = new object(); var session = Guid.NewGuid();
        relay.Receive(1004, connection, context, new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = session }, 0, (_, __) => true);
        var state = new FrameMessage { Kind = FrameMessageKind.Publish, ClientSession = session, ServerSession = relay.Session,
            Ship = 1015, Mode = PassengerMode.Walking, Sequence = 1, Generation = 1 };
        relay.Receive(1004, connection, context, state, .1, (_, __) => true);
        FramePeer peer = relay.Aboard(1015, context, .2).Single();
        Check(relay.Authenticated(peer, connection, session, relay.Session), "Native-authenticated peer not available for travel.");
        Check(!relay.Authenticated(peer, new object(), session, relay.Session) && !relay.Authenticated(peer, connection, Guid.NewGuid(), relay.Session), "Spoof/reconnect bypassed travel identity.");
        Check(relay.Aboard(1015, new object(), .2).Length == 0 && relay.Aboard(1015, context, 2.2).Length == 0, "Cross-world or expired membership in manifest.");
        state.Mode = PassengerMode.Seated; state.Sequence++;
        relay.Receive(1004, connection, context, state, .3, (_, __) => true);
        Check(relay.Aboard(1015, context, .4).Single().State.Mode == PassengerMode.Seated, "Seated passenger disappeared.");
        peer.State.Ship = 9; Check(relay.Aboard(1015, context, .4).Length == 1, "Roster caller mutated live relay state.");
    }
    internal static void LocalArrivalAndMicroWarp()
    {
        var m = Member(2004, PassengerMode.Jumping);
        var source = new LocalFramePose(new Vector3(50, 1095, 100), Quaternion.Identity);
        var target = new LocalFramePose(new Vector3(-400, 1070, 800), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2));
        Vector3 arrival = target.ToWorldPoint(m.Position);
        Check(Vector3.Distance(target.ToLocalPoint(arrival), m.Position) < .0001f, "Arrival used player world point instead of saved local point.");
        var continuity = new ShipFrameContinuity(); continuity.Seed(source, new Vector3(0, 0, 40), 1);
        continuity.Seed(target, new Vector3(0, 0, 40), 2);
        Check(continuity.Pose.Position == target.Position && continuity.Velocity.Length() == 40, "Explicit native jump became enormous travel velocity.");
        Check(m.Mode == PassengerMode.Jumping && m.Velocity.Z == 2, "Jump/local velocity lost at warp.");
    }
    internal static void NativeBoundaryReproduction()
    {
        Assembly fixture = Assembly.Load("ShipWalk.NativeTravelFixture");
        Type entity = fixture.GetType("Assembly-CSharp.MenuOptions"), player = fixture.GetType("Assembly-CSharp.ViewDictionary"), context = fixture.GetType("Assembly-CSharp.StreamToken");
        object ship = FormatterServices.GetUninitializedObject(entity), actor = FormatterServices.GetUninitializedObject(player), world = FormatterServices.GetUninitializedObject(context);
        FieldInfo position = entity.GetFields().Single(f => f.FieldType == typeof(UV)), seat = entity.GetFields().Single(f => f.FieldType == entity);
        position.SetValue(ship, new UV(0, 1095, 0)); position.SetValue(actor, new UV(0, 1105, 0));
        MethodInfo select = context.GetMethod("SelectedBoundaryPoint");
        UV walking = (UV)select.Invoke(world, new[] { actor });
        Check(walking.y == 1105, "Native player boundary bug no longer reproduced by installed-build IL.");
        seat.SetValue(actor, ship); UV seated = (UV)select.Invoke(world, new[] { actor });
        Check(seated.y == 1095, "Native seated branch does not use vessel point.");
        seat.SetValue(actor, null);
        Check(((UV)position.GetValue(ship)).y < 1100 && walking.y >= 1100, "Walking player and ship should cross at distinct times.");
    }
    internal static void NativeSnapshotRoundTrip()
    {
        Type type = Assembly.Load("ShipWalk.NativeTravelFixture").GetType("Assembly-CSharp.AspectContext");
        object snapshot = Activator.CreateInstance(type); FieldInfo[] fields = type.GetFields();
        var id = fields.Single(f => f.FieldType == typeof(int)); var members = fields.Single(f => f.FieldType == typeof(int[]));
        var seats = fields.Single(f => f.Name == "nodeCache"); var entity = fields.Single(f => f.FieldType == typeof(byte[]) && f != seats);
        byte[] shipBytes = { 3, 1, 4, 1 }, seatBytes = { 9, 2, 6, 5 };
        id.SetValue(snapshot, 1015); entity.SetValue(snapshot, shipBytes); seats.SetValue(snapshot, seatBytes);
        members.SetValue(snapshot, TravelTransfer.PassengerIds(new[] { 3004 }, new[] { 1004, 2004 }, 1004));
        using (var bytes = new MemoryStream())
        {
            type.GetMethod("UpdateClient").Invoke(snapshot, new object[] { new BinaryWriter(bytes) }); bytes.Position = 0;
            object read = Activator.CreateInstance(type); type.GetMethod("SplitControl").Invoke(read, new object[] { new BinaryReader(bytes) });
            Check((int)id.GetValue(read) == 1015 && ((int[])members.GetValue(read)).SequenceEqual(new[] { 3004, 2004 }), "Native codec lost standing member or ship.");
            Check(((byte[])entity.GetValue(read)).SequenceEqual(shipBytes) && ((byte[])seats.GetValue(read)).SequenceEqual(seatBytes), "Vessel bytes/native seat map were rewritten.");
        }
    }
    internal static void NativeRecoveryCancellation()
    {
        Assembly fixture = Assembly.Load("ShipWalk.NativeTravelFixture");
        Type world = fixture.GetType("Assembly-CSharp.FunctionAttributeNodeCollection"),
            player = fixture.GetType("Assembly-CSharp.ViewDictionary"), failure = fixture.GetType("EnumOutOfPlayfieldTypes");
        object actor = FormatterServices.GetUninitializedObject(player);
        world.GetFields().Single(f => f.FieldType == world).SetValue(null, FormatterServices.GetUninitializedObject(world));
        var pending = world.GetField("PendingTransfer"); var moved = world.GetField("Repositions");
        foreach (int reason in new[] { 1, 2, 5, 6, 7 })
        {
            pending.SetValue(null, true); moved.SetValue(null, 0);
            player.GetMethod("InsertBuilder", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Invoke(actor, new[] { Enum.ToObject(failure, reason) });
            Check((int)moved.GetValue(null) == 1, "Native player recovery did not reposition before cancellation.");
            Check((bool)pending.GetValue(null) == (reason != 2 && reason != 5), "Native cancellation branch differs from the captured failed crossing.");
        }
    }
    private static object ownedRecoveryActor;
    private static int RecoveryFilter(int result, object actor)
        => TravelRecoveryPatch.Filter(result, ReferenceEquals(actor, ownedRecoveryActor));
    private static IEnumerable<CodeInstruction> RecoveryTranspiler(IEnumerable<CodeInstruction> instructions)
        => TravelRecoveryPatch.Rewrite(instructions, typeof(TravelTests).GetMethod(nameof(RecoveryFilter), BindingFlags.NonPublic | BindingFlags.Static));
    internal static void NativeRecoveryGuard()
    {
        Assembly fixture = Assembly.Load("ShipWalk.NativeTravelFixture");
        Type world = fixture.GetType("Assembly-CSharp.FunctionAttributeNodeCollection"),
            player = fixture.GetType("Assembly-CSharp.ViewDictionary"), failure = fixture.GetType("EnumOutOfPlayfieldTypes");
        object actor = FormatterServices.GetUninitializedObject(player), other = FormatterServices.GetUninitializedObject(player);
        var query = player.BaseType.GetMethod("SearchDatabase"); var outcome = player.BaseType.GetField("BoundaryFailure");
        var recover = player.GetMethod("InsertBuilder", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var pending = world.GetField("PendingTransfer"); var moved = world.GetField("Repositions");
        world.GetFields().Single(f => f.FieldType == world).SetValue(null, FormatterServices.GetUninitializedObject(world));
        var harmony = new Harmony("shipwalk.travel.recovery.test");
        try
        {
            // Exercise the actual installed hook with no owner before the
            // fixture supplies an owned-character identity to the same rewrite.
            harmony.Patch(query, transpiler: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.TravelRecoveryTranspiler))));
            outcome.SetValue(actor, Enum.ToObject(failure, 5));
            Check(Convert.ToInt32(query.Invoke(actor, null)) == 5, "Unowned native recovery was changed.");
            harmony.UnpatchAll(harmony.Id);
            harmony.Patch(query, transpiler: new HarmonyMethod(typeof(TravelTests).GetMethod(nameof(RecoveryTranspiler), BindingFlags.NonPublic | BindingFlags.Static)));
            foreach (bool owned in new[] { false, true })
            foreach (object candidate in new[] { actor, other })
            foreach (int reason in new[] { 0, 1, 2, 5, 6, 7 })
            {
                ownedRecoveryActor = owned ? actor : null;
                outcome.SetValue(candidate, Enum.ToObject(failure, reason));
                pending.SetValue(null, true); moved.SetValue(null, 0);
                object result = query.Invoke(candidate, null);
                // This is the native DeployPath gate: None never calls the
                // recovery override. That override is original native IL.
                if (Convert.ToInt32(result) != 0) recover.Invoke(candidate, new[] { result });
                bool deferred = owned && ReferenceEquals(candidate, actor) && (reason == 2 || reason == 5);
                Check(Convert.ToInt32(result) == (deferred ? 0 : reason), "A foreign actor or unrelated recovery result was filtered.");
                Check((int)moved.GetValue(null) == (deferred || reason == 0 ? 0 : 1), "Recovery still repositioned the aboard character.");
                Check((bool)pending.GetValue(null) == (deferred || reason != 2 && reason != 5), "The aboard transfer was cancelled or a real departure lost native recovery.");
            }
        }
        finally { ownedRecoveryActor = null; harmony.UnpatchAll(harmony.Id); }
        outcome.SetValue(actor, Enum.ToObject(failure, 5));
        Check(Convert.ToInt32(query.Invoke(actor, null)) == 5, "Removing the mod did not restore native boundary recovery.");
    }
    internal static void RecoveryControlFlow()
    {
        var il = new DynamicMethod("recovery-labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        Label end = il.DefineLabel(); var ret = new CodeInstruction(OpCodes.Ret); ret.labels.Add(end);
        var filter = typeof(Hooks).GetMethod(nameof(Hooks.TravelRecoveryResult));
        var rewritten = TravelRecoveryPatch.Rewrite(new[] { new CodeInstruction(OpCodes.Br, end), ret }, filter).ToArray();
        Check(rewritten.Length == 4 && rewritten[1].opcode == OpCodes.Ldarg_0 && rewritten[1].labels.Contains(end)
            && rewritten[2].Calls(filter) && rewritten[3].opcode == OpCodes.Ret && rewritten[3].labels.Count == 0,
            "Native return branches can skip the recovery filter.");
        Check(ret.labels.Contains(end), "Rewrite mutated its input instructions.");
        bool refused = false;
        try { TravelRecoveryPatch.Rewrite(new[] { new CodeInstruction(OpCodes.Nop) }, filter).ToArray(); }
        catch (NotSupportedException) { refused = true; }
        Check(refused, "A changed native recovery method was silently accepted.");
    }
    internal static void NativeVesselRecoveryReproduction()
    {
        Assembly fixture = Assembly.Load("ShipWalk.NativeTravelFixture");
        Type entity = fixture.GetType("Assembly-CSharp.MenuOptions"),
            world = fixture.GetType("Assembly-CSharp.FunctionAttributeNodeCollection"),
            failure = fixture.GetType("EnumOutOfPlayfieldTypes");
        object ship = FormatterServices.GetUninitializedObject(entity);
        var reason = entity.GetField("BoundaryFailure"); var query = entity.GetMethod("SearchDatabase");
        var recover = entity.GetMethod("InsertBuilder"); var moved = world.GetField("Repositions");
        // The same native recovery query runs for vessels on the worker.
        // An active player-only hook has no owner for this ship and lets it run.
        var harmony = new Harmony("shipwalk.travel.vessel.reproduction");
        try
        {
            harmony.Patch(query, transpiler: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.TravelRecoveryTranspiler))));
            foreach (int value in new[] { 2, 5 })
            {
                reason.SetValue(ship, Enum.ToObject(failure, value)); moved.SetValue(null, 0);
                object result = query.Invoke(ship, null);
                if (Convert.ToInt32(result) != 0) recover.Invoke(ship, new[] { result });
                Check((int)moved.GetValue(null) == 1, "Vessel recovery conflict was not reproduced.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    internal static void HubLoadingAndReplay()
    {
        DateTime now = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        var delivered = new List<Tuple<string, TravelPacket>>(); bool ready = false;
        var hub = new TravelHub((receiver, world, bytes) =>
        {
            Check(receiver == "ShipWalk" && TravelProtocol.TryDecode(bytes, out _), "Bad hub transport.");
            TravelProtocol.TryDecode(bytes, out TravelPacket message);
            if (world == "Temperate" && !ready) return false;
            delivered.Add(Tuple.Create(world, message)); return true;
        }, _ => { }, _ => { }, () => now);
        var p = Plan(); p.Kind = TravelKind.Manifest;
        hub.Receive("WrongMod", p.Source, TravelProtocol.Encode(p)); hub.Update(); Check(delivered.Count == 0, "Foreign mod accepted.");
        hub.Receive("ShipWalk", "OtherSource", TravelProtocol.Encode(p)); hub.Update(); Check(delivered.Count == 0, "Wrong source accepted.");
        hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); hub.Update();
        Check(delivered.Single().Item2.Kind == TravelKind.Stored, "Source not acknowledged before transfer.");
        ready = true; now = now.AddSeconds(2); hub.Update(); Check(delivered.Count == 1, "Uncommitted manifest reached destination.");
        var altered = p.Copy(); altered.Members[1].Position.X++;
        hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(altered)); hub.Update(); Check(delivered.Count == 1, "Duplicate ID replaced stored manifest.");
        p.Kind = TravelKind.Commit; ready = false;
        hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); hub.Update(); Check(delivered.Count == 1, "Unavailable destination falsely delivered.");
        ready = true; now = now.AddSeconds(2); hub.Update();
        Check(delivered.Count == 2 && delivered[1].Item1 == p.Destination && delivered[1].Item2.Members.Length == 2, "Load gap lost passengers.");
        p.Kind = TravelKind.Arrived; hub.Receive("ShipWalk", p.Destination, TravelProtocol.Encode(p)); hub.Update();
        int count = delivered.Count; p.Kind = TravelKind.Manifest;
        hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); now = now.AddSeconds(2); hub.Update();
        Check(delivered.Count == count, "Completed transfer resurrected or continued retrying.");
    }
    internal static void HubCancellation()
    {
        DateTime now = DateTime.UtcNow; var delivered = new List<TravelPacket>();
        var hub = new TravelHub((_, __, bytes) => { TravelProtocol.TryDecode(bytes, out var p); delivered.Add(p); return true; }, _ => { }, _ => { }, () => now);
        var p = Plan(); p.Kind = TravelKind.Manifest;
        hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); hub.Update();
        p.Kind = TravelKind.Cancel; hub.Receive("ShipWalk", "WrongSource", TravelProtocol.Encode(p)); hub.Update();
        Check(delivered.Count == 1, "Foreign source cancelled transfer.");
        hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); hub.Update();
        Check(delivered.Count == 2 && delivered[1].Kind == TravelKind.Cancel, "Cancellation did not reach destination.");
        p.Kind = TravelKind.Manifest; hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); now = now.AddSeconds(2); hub.Update();
        Check(delivered.Count == 2, "Cancelled transfer revived on delayed manifest.");
        p = Plan(); p.Kind = TravelKind.Manifest; hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); hub.Update();
        now = now.AddMinutes(6); hub.Update(); int count = delivered.Count;
        p.Kind = TravelKind.Commit; hub.Receive("ShipWalk", p.Source, TravelProtocol.Encode(p)); hub.Update();
        Check(delivered.Count == count, "Expired transfer revived on late commit.");
    }
    private enum Reason { None, Warp }
    private static int nativeCalls;
    [MethodImpl(MethodImplOptions.NoInlining)] private static void RequestProbe(Reason reason) { nativeCalls++; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static bool BoundaryProbe(object actor) { nativeCalls++; return true; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static void ArrivalProbe(Reason reason, string source, string dest, UV point, UnityEngine.Quaternion rotation, int effect, string name, byte flags) { nativeCalls++; }
    internal static void HarmonySignatures()
    {
        var harmony = new Harmony("shipwalk.travel.signature.test"); nativeCalls = 0;
        MethodInfo Find(string name) => typeof(TravelTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        try
        {
            harmony.Patch(Find(nameof(RequestProbe)), prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.TravelRequestPrefix))));
            // Native boundary is an instance method. Only enum boxing and ref
            // arrival argument binding are exercised by these inert probes.
            harmony.Patch(Find(nameof(ArrivalProbe)), prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.TravelArrivalPrefix))));
            RequestProbe(Reason.Warp); ArrivalProbe(Reason.Warp, "a", "b", new UV(), new UnityEngine.Quaternion(0,0,0,1), 0, null, 31);
            Check(nativeCalls == 2, "Actual travel prefixes broke native calls without an active owner.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
