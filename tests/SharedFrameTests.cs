using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using ShipWalk;

internal static class SharedFrameTests
{
    private static readonly Guid client = Guid.NewGuid();
    private static FrameMessage Hello(Guid session) => new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = session };
    private static FrameMessage Publish(FrameRelay relay, Guid session, uint seq = 1, uint generation = 1, int ship = 5001)
        => new FrameMessage { Kind = FrameMessageKind.Publish, ClientSession = session, ServerSession = relay.Session,
            Sequence = seq, Generation = generation, Ship = ship, Mode = ship == -1 ? PassengerMode.World : PassengerMode.Walking,
            Position = new Vector3(2, 4, -6) };
    private static bool Allow(FrameMessage message, FrameMessage previous) => true;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static void Codec()
    {
        var relay = new FrameRelay(); var sent = Publish(relay, client); sent.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f);
        string wire = FrameProtocol.Encode(sent);
        Check(FrameProtocol.TryDecode(wire, out var read) && read.ClientSession == client && read.ServerSession == relay.Session
            && read.Position == sent.Position && read.Rotation == sent.Rotation && read.Ship == 5001, "Frame round trip failed.");
        foreach (string bad in new[] { "", wire + "a", wire.Substring(1), FrameProtocol.Prefix + "garbage" })
            Check(!FrameProtocol.TryDecode(bad, out _), "Malformed frame accepted.");
        byte[] bytes = Convert.FromBase64String(wire.Substring(FrameProtocol.Prefix.Length));
        bytes[0] = 240;
        Check(!FrameProtocol.TryDecode(FrameProtocol.Prefix + Convert.ToBase64String(bytes), out _), "Unknown protocol kind accepted.");
        sent.Position = new Vector3(float.NaN, 0, 0);
        try { FrameProtocol.Encode(sent); throw new Exception("NaN accepted."); } catch (InvalidDataException) { }
    }
    public static void AuthorityAndSessions()
    {
        var relay = new FrameRelay(); object a = new object(), b = new object(), context = new object();
        Guid bSession = Guid.NewGuid(); relay.Receive(1013, a, context, Hello(client), 0, Allow);
        relay.Receive(2020, b, context, Hello(bSession), 0, Allow);
        var message = Publish(relay, client); message.Actor = 2020;
        Check(relay.Receive(1013, a, context, message, .1, Allow).Count == 0, "Payload impersonated another player.");
        message.Actor = 0;
        Check(relay.Receive(1013, b, context, message, .1, Allow).Count == 0, "Wrong connection accepted.");
        Check(relay.Receive(1013, a, new object(), message, .1, Allow).Count == 0, "Cross-playfield packet accepted.");
        var states = relay.Receive(1013, a, context, message, .1, Allow);
        Check(states.Count == 2 && states.All(d => d.Message.Actor == 1013), "Authenticated identity was not assigned by server.");
        Check(relay.Receive(1013, a, context, message, .2, Allow).Count == 0, "Replayed sequence accepted.");
        var rejected = Publish(relay, client, 2);
        Check(relay.Receive(1013, a, context, rejected, .3, (_, __) => false).Count == 0, "Invalid membership accepted.");
        Check(relay.Receive(1013, a, context, rejected, .4, Allow).Count == 2, "Rejected data consumed valid sequence.");
        Guid replacement = Guid.NewGuid(); relay.Receive(1013, a, context, Hello(replacement), 1, Allow);
        Check(relay.Receive(1013, a, context, Publish(relay, client, 3), 1.1, Allow).Count == 0, "Old client session survived reconnect.");
        Check(relay.Receive(1013, a, context, Publish(relay, replacement), 1.2, Allow).Count == 2, "New session unable to attach.");
    }
    public static void ModesAndDeparture()
    {
        var relay = new FrameRelay(); object a = new object(), context = new object();
        relay.Receive(1013, a, context, Hello(client), 0, Allow);
        uint sequence = 0, generation = 0;
        foreach (PassengerMode mode in new[] { PassengerMode.Seated, PassengerMode.Walking, PassengerMode.Jumping, PassengerMode.Elevator, PassengerMode.Jetpack, PassengerMode.Seated })
        {
            var message = Publish(relay, client, ++sequence); message.Mode = mode;
            var state = relay.Receive(1013, a, context, message, sequence * .1, Allow).Single().Message;
            if (generation == 0) generation = state.Generation;
            Check(state.Generation == generation && state.Ship == 5001, "Movement mode detached the passenger.");
        }
        Check(relay.Receive(1013, a, context, Publish(relay, client, ++sequence, 1, -1), 1, Allow).Count == 0,
            "Departure without new attachment generation accepted.");
        var departed = relay.Receive(1013, a, context, Publish(relay, client, ++sequence, 2, -1), 1.1, Allow).Single().Message;
        Check(departed.Mode == PassengerMode.World && departed.Generation > generation, "Explicit departure missing.");
        var boarded = relay.Receive(1013, a, context, Publish(relay, client, ++sequence, 3), 1.2, Allow).Single().Message;
        Check(boarded.Generation > departed.Generation, "Reboarding reused attachment generation.");
        var observer = new RemotePassenger(); Check(observer.Accept(boarded, 1.2), "Baseline rejected.");
        Check(!observer.Accept(departed, 1.3), "Delayed departure erased a new attachment.");
        Check(observer.Sample(1.3, out var point, out _) && point == boarded.Position, "Observer lost stationary local point.");
        Check(!observer.Sample(4, out _, out _), "Stale observer continued overriding native presentation.");
        Check(relay.Tick(4, (_, __, ___) => true).Single().Message.Mode == PassengerMode.World, "Lost updates did not expire membership.");
        Check(relay.Tick(12, (_, __, ___) => false).Count == 1 && relay.Count == 0, "Disconnect did not clear subscription.");
    }
    public static void SharedDisplayFrame()
    {
        var relay = new FrameRelay(); var passenger = new RemotePassenger(); var state = Publish(relay, client);
        state.Kind = FrameMessageKind.State; state.Actor = 1013; passenger.Accept(state, 1);
        for (int i = 0; i < 60; i++)
        {
            float now = 1 + i / 60f;
            Check(passenger.Sample(now, out var local, out _), "Missing local frame sample.");
            var ship = new LocalFramePose(new Vector3(4800 + 100.5f * now, 1000, 1700), Quaternion.CreateFromAxisAngle(Vector3.UnitY, now));
            Vector3 world = ship.ToWorldPoint(local);
            Check(Vector3.Distance(ship.ToLocalPoint(world), state.Position) < .001f, "Observer drifted relative to displayed hull.");
        }
        var moved = state.Copy(); moved.Sequence++; moved.Position += Vector3.UnitX; passenger.Accept(moved, 2);
        Check(passenger.Sample(2.1, out var interpolated, out _) && interpolated.X > state.Position.X && interpolated.X < moved.Position.X,
            "Observer does not interpolate local travel.");
    }
    public static void NativeFactoryRegistration()
    {
        Assembly fixture = Assembly.Load("ShipWalk.NativeEnvelopeFixture");
        Type registry = fixture.GetType("Assembly-CSharp.AspectScopeNodeCollection", true);
        var byId = (Dictionary<int, Type>)registry.GetFields().Single(f => f.FieldType == typeof(Dictionary<int, Type>)).GetValue(null);
        var byType = (Dictionary<Type, int>)registry.GetFields().Single(f => f.FieldType == typeof(Dictionary<Type, int>)).GetValue(null);
        Check(!byId.ContainsKey(139), "Fixture must start with native ModGameEvent registration missing.");
        byte[] bytes = PacketBytes(Hello(client));
        try { DecodeNative(bytes); throw new Exception("Native unknown-packet failure was not reproduced."); }
        catch (TargetInvocationException e) { Check(e.InnerException.Message == "Unknown package id 139", "Unexpected native dispatch failure: " + e); }
        Type packet = fixture.GetType("Assembly-CSharp.ServerDictionary", true);
        NativePacketRegistry.Register(byId, byType, packet);
        Check(byId[139] == packet && byType[packet] == 139, "Both native registry directions must agree.");
        NativePacketRegistry.Register(byId, byType, packet);
        Check(byId.Count == 1 && byType.Count == 1, "Reinitialization duplicated native mappings.");
        Check(DecodeNative(bytes).ClientSession == client, "Registered native receiver lost the greeting.");
        bytes[0] = 197;
        try { DecodeNative(bytes); throw new Exception("Unrelated unknown packet ID was accepted."); }
        catch (TargetInvocationException e) { Check(e.InnerException.Message == "Unknown package id 197", "Unrelated native rejection changed."); }
    }
    public static void NativePoseConfirmation()
    {
        var relay = new FrameRelay(); object connection = new object(), context = new object();
        relay.Receive(1013, connection, context, DecodeNative(PacketBytes(Hello(client))), 1, (m, p) => true);
        var frame = new ShipFrameContinuity();
        frame.Seed(new LocalFramePose(Vector3.Zero, Quaternion.Identity), new Vector3(48, 0, 0), 1);
        frame.BeginHandoff();
        for (uint id = 1; id <= 2; id++)
        {
            float sent = 1 + id * .15f;
            frame.TrackRequest(id, sent);
            var request = new FrameMessage { Kind = FrameMessageKind.ShipPoseRequest, ClientSession = client,
                ServerSession = relay.Session, Ship = 1015, Generation = frame.Epoch, Sequence = id,
                Position = new Vector3(1, 18, 13), Mode = PassengerMode.Walking };
            FrameMessage incoming = DecodeNative(PacketBytes(request));
            var deliveries = relay.Receive(1013, connection, context, incoming, sent + .02,
                (m, p) => m.Ship == 1015, _ => new FrameMessage { Position = new Vector3(id * 7.2f, 0, 0), Velocity = new Vector3(48, 0, 0) });
            FrameMessage reply = DecodeNative(PacketBytes(deliveries.Single().Message));
            Check(reply.Actor == 1013 && frame.Confirm(reply, sent + .04f), "Native decoded baseline did not correlate to request.");
            ShipPoseState state = frame.Resolve(new LocalFramePose(new Vector3(396.468f, 0, 0), Quaternion.Identity), sent + .05f, Vector3.Zero);
            Check(state == (id == 1 ? ShipPoseState.Holding : ShipPoseState.Confirmed), "Native envelope handoff lifecycle mismatch.");
        }
    }
    public static void RegistryConflicts()
    {
        for (int scenario = 0; scenario < 4; scenario++)
        {
            var byId = new Dictionary<int, Type>(); var byType = new Dictionary<Type, int>();
            if (scenario == 0) byId.Add(139, typeof(string));
            if (scenario == 1) byType.Add(typeof(FrameMessage), 138);
            if (scenario == 2) byId.Add(138, typeof(FrameMessage));
            if (scenario == 3) byType.Add(typeof(string), 139);
            var beforeId = byId.ToArray(); var beforeType = byType.ToArray();
            bool refused = false;
            try { NativePacketRegistry.Register(byId, byType, typeof(FrameMessage)); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && beforeId.SequenceEqual(byId) && beforeType.SequenceEqual(byType), "Conflicting registration partially changed native maps.");
        }
    }
    // Exercise the installed game's actual packet factory/writer/reader over two TCP
    // connections. The socket identity fixture stands in for native auth; this
    // is not a running Empyrion client/worker multiplayer acceptance test.
    public static void NativeEnvelopeTwoClients()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using (var a = new TcpClient()) using (var b = new TcpClient())
            {
                a.Connect(IPAddress.Loopback, port); b.Connect(IPAddress.Loopback, port);
                using (TcpClient serverA = listener.AcceptTcpClient()) using (TcpClient serverB = listener.AcceptTcpClient())
                {
                    foreach (TcpClient socket in new[] { a, b, serverA, serverB }) { socket.ReceiveTimeout = socket.SendTimeout = 3000; socket.NoDelay = true; }
                    var relay = new FrameRelay(); var context = new object(); Guid bSession = Guid.NewGuid();
                    Write(a, Hello(client)); var hello = Read(serverA);
                    var welcome = relay.Receive(1013, serverA, context, hello, 0, Allow).Single();
                    Write(serverA, welcome.Message); Check(Read(a).Kind == FrameMessageKind.Welcome, "Client A handshake failed.");
                    Write(a, Publish(relay, client));
                    var own = relay.Receive(1013, serverA, context, Read(serverA), .1, Allow).Single();
                    Write(serverA, own.Message); Check(Read(a).Actor == 1013, "Server did not bind connection identity.");
                    Write(b, Hello(bSession));
                    var baseline = relay.Receive(2020, serverB, context, Read(serverB), .2, Allow);
                    foreach (var delivery in baseline) Write((TcpClient)delivery.Connection, delivery.Message);
                    Check(Read(b).Kind == FrameMessageKind.Welcome, "Client B handshake failed.");
                    FrameMessage observed = Read(b);
                    Check(observed.Kind == FrameMessageKind.State && observed.Actor == 1013 && observed.Ship == 5001
                        && observed.Position == own.Message.Position, "Late observer baseline did not cross native envelopes.");
                    observed.Kind = FrameMessageKind.Receipt; Write(b, observed);
                    relay.Receive(2020, serverB, context, Read(serverB), .3, Allow);
                    Check(relay.ObserverReceipts == 1 && relay.LastReceiptObserver == 2020, "Observer receipt did not return to server.");
                    var update = Publish(relay, client, 2); update.Position += Vector3.UnitZ; Write(a, update);
                    foreach (var delivery in relay.Receive(1013, serverA, context, Read(serverA), .4, Allow))
                        Write((TcpClient)delivery.Connection, delivery.Message);
                    Check(Read(a).Position == update.Position && Read(b).Position == update.Position, "Live delta did not reach both clients.");
                }
            }
        }
        finally { listener.Stop(); }
    }
    private static void Write(TcpClient socket, FrameMessage message)
    {
        byte[] bytes = PacketBytes(message); var output = new BinaryWriter(socket.GetStream());
        output.Write(bytes.Length); output.Write(bytes); output.Flush();
    }
    private static byte[] PacketBytes(FrameMessage message)
    {
        Type type = Assembly.Load("ShipWalk.NativeEnvelopeFixture").GetType("Assembly-CSharp.ServerDictionary", true);
        var writerMethod = type.GetMethod("ToggleClient");
        object packet = FormatterServices.GetUninitializedObject(type);
        Field(type, typeof(string)).SetValue(packet, FrameProtocol.Encode(message));
        FieldInfo variant = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(f => f.FieldType.Name == "PartitionTree"); variant.SetValue(packet, Enum.ToObject(variant.FieldType, 2));
        using (var buffer = new MemoryStream()) using (var writer = new BinaryWriter(buffer))
        {
            writerMethod.Invoke(packet, new object[] { writer });
            return buffer.ToArray();
        }
    }
    private static FrameMessage Read(TcpClient socket)
    {
        var input = new BinaryReader(socket.GetStream()); int length = input.ReadInt32();
        Check(length > 0 && length < 512, "Native envelope size invalid.");
        byte[] bytes = input.ReadBytes(length); Check(bytes.Length == length, "Truncated TCP frame.");
        return DecodeNative(bytes);
    }
    private static FrameMessage DecodeNative(byte[] bytes)
    {
        Assembly fixture = Assembly.Load("ShipWalk.NativeEnvelopeFixture");
        Type type = fixture.GetType("Assembly-CSharp.ServerDictionary", true);
        object connection = FormatterServices.GetUninitializedObject(fixture.GetType("Assembly-CSharp.XmlFileLoader", true));
        using (var reader = new BinaryReader(new MemoryStream(bytes)))
        {
            object packet = fixture.GetType("Assembly-CSharp.FormStack", true).GetMethod("QuoteReference").Invoke(null, new[] { reader, connection });
            Check(packet.GetType() == type && ReferenceEquals(type.GetMethod("get_ClearMethod").Invoke(packet, null), connection), "Native decoder did not bind the original sender.");
            Check(reader.BaseStream.Position == bytes.Length, "Native reader did not consume exact envelope.");
            Check(FrameProtocol.TryDecode((string)Field(type, typeof(string)).GetValue(packet), out FrameMessage message), "Native payload lost.");
            return message;
        }
    }
    private static FieldInfo Field(Type owner, Type fieldType) => owner.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Single(f => f.FieldType == fieldType);

    public static bool ChannelPrefix(object __instance, ref int __result)
    {
        FieldInfo kind = __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(f => f.FieldType.Name == "PartitionTree");
        if (!FrameProtocol.UseGameplayChannel(Convert.ToInt32(kind.GetValue(__instance)),
            (string)Field(__instance.GetType(), typeof(string)).GetValue(__instance))) return true;
        __result = 1; return false;
    }
    public static void NativeChannelRouting()
    {
        Type type = Assembly.Load("ShipWalk.NativeEnvelopeFixture").GetType("Assembly-CSharp.ServerDictionary", true);
        object packet = FormatterServices.GetUninitializedObject(type);
        MethodInfo getter = type.GetMethod("get_CleanActivator");
        Check((int)getter.Invoke(packet, null) == 4, "Native default mod-event channel changed.");
        var harmony = new Harmony("shipwalk.tests.native-frame-channel");
        try
        {
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(SharedFrameTests).GetMethod(nameof(ChannelPrefix))));
            Check((int)getter.Invoke(packet, null) == 4, "Ordinary mod event was rerouted.");
            Field(type, typeof(string)).SetValue(packet, FrameProtocol.Encode(Hello(client)));
            Check((int)getter.Invoke(packet, null) == 4, "A different native event variant was rerouted.");
            FieldInfo kind = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Single(f => f.FieldType.Name == "PartitionTree"); kind.SetValue(packet, Enum.ToObject(kind.FieldType, 2));
            Check((int)getter.Invoke(packet, null) == 1, "ShipWalk envelope did not select worker gameplay channel.");
            Field(type, typeof(string)).SetValue(packet, "some other mod");
            Check((int)getter.Invoke(packet, null) == 4, "Another mod's event was rerouted.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
        Check((int)getter.Invoke(packet, null) == 4, "Native channel did not restore after unpatch.");
    }
    public static void PlayfieldAndReceiptScope()
    {
        var relay = new FrameRelay(); object a = new object(), b = new object(), c = new object(), context = new object();
        Guid bs = Guid.NewGuid(), cs = Guid.NewGuid();
        relay.Receive(1, a, context, Hello(client), 0, Allow);
        relay.Receive(2, b, context, Hello(bs), 0, Allow);
        relay.Receive(3, c, new object(), Hello(cs), 0, Allow);
        var sent = relay.Receive(1, a, context, Publish(relay, client), .1, Allow);
        Check(sent.Count == 2 && sent.All(d => d.Connection != c), "Frame leaked to a different playfield.");
        var receipt = sent.Single(d => d.Connection == b).Message; receipt.Kind = FrameMessageKind.Receipt;
        relay.Receive(2, b, context, receipt, .2, Allow); relay.Receive(2, b, context, receipt, .3, Allow);
        Check(relay.ObserverReceipts == 1, "Repeated receipt inflated delivery evidence.");
        var bad = Publish(relay, client, 2); bad.ServerSession = Guid.NewGuid();
        Check(relay.Receive(1, a, context, bad, .4, Allow).Count == 0, "Foreign server session accepted.");
    }

    public sealed class NativeHistory
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Update(UnityEngine.Vector3 position, UnityEngine.Vector3 rotation)
        { position.x = 1f / (5 * .05f); Last = position; }
        public UnityEngine.Vector3 Last;
    }
    private static RemotePoseArguments captured;
    private static UnityEngine.Vector3 overwritten;
    public static void CapturePrefix(UnityEngine.Vector3 __0, UnityEngine.Vector3 __1, out RemotePoseArguments __state)
        => Hooks.RemotePosePrefix(__0, __1, out __state);
    public static void CapturePostfix(UnityEngine.Vector3 __0, RemotePoseArguments __state)
    { captured = __state; overwritten = __0; }
    public static void HistoryMutation()
    {
        var harmony = new Harmony("shipwalk.tests.history-argument");
        MethodInfo method = typeof(NativeHistory).GetMethod(nameof(NativeHistory.Update));
        try
        {
            harmony.Patch(method, new HarmonyMethod(typeof(SharedFrameTests).GetMethod(nameof(CapturePrefix))),
                new HarmonyMethod(typeof(SharedFrameTests).GetMethod(nameof(CapturePostfix))));
            var native = new NativeHistory(); var position = new UnityEngine.Vector3(4834.705f, 1073.147f, 1713.768f);
            method.Invoke(native, new object[] { position, new UnityEngine.Vector3(0, 10, 0) });
            Check(overwritten.x == 4 && native.Last.x == 4, "Recorded native argument mutation was not reproduced.");
            Check(captured.Position.x == position.x && captured.Rotation.y == 10, "Prefix did not retain original pose.");
            var first = captured.Position;
            position.x += 3;
            method.Invoke(native, new object[] { position, new UnityEngine.Vector3() });
            Check(Math.Abs((captured.Position.x - first.x) / .05f - 60) < .01, "Original X velocity was lost.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
