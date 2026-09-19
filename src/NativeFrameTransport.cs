using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ShipWalk
{
    // Build5150 has already pinned the assembly hash and MVID before this map
    // is created. Bind by token and full signatures, never obfuscated name alone.
    internal sealed class NativeFrameTransport
    {
        private readonly FieldInfo network, game, payload, variant, playerId, connectionPlayfield,
            disconnected, actorPlayfield, packetPlayfield, clientChannels, packetTypes, packetIds;
        private readonly MethodInfo toServer, toClient, findConnection, findEntity, sender;
        private readonly ConstructorInfo packet;
        private readonly object eventType;
        public readonly MethodInfo Receive, Channel;
        public NativeFrameTransport(NativeBuildBindings assembly)
        {
            NativeBuildBindings m = assembly;
            Type T(string n) => assembly.GetType("Assembly-CSharp." + n, true);
            Type form = T("FormStack"), connection = T("XmlFileLoader"), control = T("ControlToken"), world = T("FunctionAttributeNodeCollection");
            network = F(m, 0x040054EE, control, control, true);
            game = F(m, 0x0400647E, world, world, true);
            payload = F(m, 0x04003636, T("ServerDictionary"), typeof(string));
            variant = F(m, 0x0400363C, T("ServerDictionary"), T("ServerDictionary+PartitionTree"));
            playerId = F(m, 0x040033D7, connection, typeof(int));
            connectionPlayfield = F(m, 0x040033E3, connection, T("StreamToken"));
            disconnected = F(m, 0x040033E2, connection, typeof(bool));
            actorPlayfield = F(m, 0x04001B1D, T("MenuOptions"), T("StreamToken"));
            packetPlayfield = F(m, 0x04003564, form, T("StreamToken"));
            clientChannels = F(m, 0x040054F8, control, T("EditorType").MakeArrayType());
            packetTypes = F(m, 0x04006787, T("AspectScopeNodeCollection"), typeof(Dictionary<int, Type>), true);
            packetIds = F(m, 0x04006788, T("AspectScopeNodeCollection"), typeof(Dictionary<Type, int>), true);
            toServer = M(m, 0x06005AE8, control, "CopyImage", typeof(void), form, typeof(bool));
            toClient = M(m, 0x060037FA, connection, "SaveTemplate", typeof(void), form, typeof(int));
            findConnection = M(m, 0x06005AF3, control, "ClearMethod", connection, typeof(int));
            findEntity = M(m, 0x06006C3F, world, "ToggleDevice", T("MenuOptions"), typeof(int));
            sender = M(m, 0x060038C7, form, "get_ClearMethod", connection);
            Receive = M(m, 0x06003A70, T("ServerDictionary"), "DeployAssembly", typeof(void));
            Channel = M(m, 0x06003A6C, T("ServerDictionary"), "get_CleanActivator", typeof(int));
            packet = (ConstructorInfo)m.ResolveMethod(0x06003A69);
            Type gameEvent = typeof(GameEventType);
            if (packet.DeclaringType != T("ServerDictionary") || packet.GetParameters().Length != 2
                || packet.GetParameters()[0].ParameterType != gameEvent || packet.GetParameters()[1].ParameterType != typeof(string))
                throw new NotSupportedException("Native mod-event constructor changed.");
            eventType = Enum.ToObject(gameEvent, 0);
        }
        public void RegisterPacket()
        {
            NativePacketRegistry.Register((Dictionary<int, Type>)packetTypes.GetValue(null),
                (Dictionary<Type, int>)packetIds.GetValue(null), packet.DeclaringType);
        }
        private static FieldInfo F(NativeBuildBindings m, int token, Type owner, Type type, bool isStatic = false)
        {
            FieldInfo value = m.ResolveField(token);
            if (value.DeclaringType != owner || value.FieldType != type || value.IsStatic != isStatic)
                throw new NotSupportedException("Network field mismatch: " + token.ToString("X8"));
            return value;
        }
        private static MethodInfo M(NativeBuildBindings m, int token, Type owner, string name, Type result, params Type[] args)
        {
            var value = (MethodInfo)m.ResolveMethod(token); ParameterInfo[] parameters = value.GetParameters();
            if (value.DeclaringType != owner || m.CanonicalMethodName(value) != name || value.ReturnType != result || value.IsStatic || parameters.Length != args.Length)
                throw new NotSupportedException("Network method mismatch: " + token.ToString("X8"));
            for (int i = 0; i < args.Length; i++) if (parameters[i].ParameterType != args[i])
                throw new NotSupportedException("Network parameter mismatch: " + token.ToString("X8"));
            return value;
        }
        public bool Capture(object nativePacket, out NativeFrameEnvelope envelope)
        {
            if (!CapturePayload(nativePacket, out string text, out envelope)
                || !FrameProtocol.TryDecode(text, out FrameMessage message)) return false;
            envelope.Message = message; return true;
        }
        public bool CapturePayload(object nativePacket, out string text, out NativeFrameEnvelope envelope)
        {
            text = null; envelope = null;
            if (Convert.ToInt32(variant.GetValue(nativePacket)) != 2) return false;
            text = payload.GetValue(nativePacket) as string;
            envelope = new NativeFrameEnvelope { Connection = sender.Invoke(nativePacket, null), Playfield = packetPlayfield.GetValue(nativePacket) };
            return true;
        }
        public bool IsFramePacket(object nativePacket)
        {
            int kind = Convert.ToInt32(variant.GetValue(nativePacket)); string text = payload.GetValue(nativePacket) as string;
            return FrameProtocol.UseGameplayChannel(kind, text) || kind == 2 && (TravelProtocol.IsEnvelope(text) || ReconnectProtocol.IsEnvelope(text));
        }
        public object Entity(int id)
        {
            object singleton = game.GetValue(null);
            return singleton == null ? null : findEntity.Invoke(singleton, new object[] { id });
        }
        public object Context(object actor) => actor == null ? null : actorPlayfield.GetValue(actor);
        public object PacketContext(object nativePacket) => packetPlayfield.GetValue(nativePacket);
        public object Sender(object nativePacket) => sender.Invoke(nativePacket, null);
        public bool Authenticate(NativeFrameEnvelope envelope, out int id, out object actor)
        {
            id = -1; actor = null;
            object connection = envelope.Connection, singleton = network.GetValue(null);
            if (singleton == null || connection == null || (bool)disconnected.GetValue(connection)) return false;
            id = (int)playerId.GetValue(connection);
            // The worker's manager-authenticated proxy does not set AuthState.
            // Instead require the exact live native connection indexed by the
            // server-assigned player ID and matching native playfield objects.
            if (id <= 0 || !ReferenceEquals(findConnection.Invoke(singleton, new object[] { id }), connection)) return false;
            actor = Entity(id);
            object context = connectionPlayfield.GetValue(connection);
            return actor != null && context != null && ReferenceEquals(Context(actor), context)
                && ReferenceEquals(envelope.Playfield, context);
        }
        public bool SendServer(FrameMessage message) => SendServerPayload(FrameProtocol.Encode(message));
        public bool SendServerPayload(string text)
        {
            object singleton = network.GetValue(null);
            var channels = singleton == null ? null : clientChannels.GetValue(singleton) as Array;
            if (channels == null || channels.Length <= 1 || channels.GetValue(1) == null) return false;
            toServer.Invoke(singleton, new[] { packet.Invoke(new[] { eventType, text }), (object)true });
            return true;
        }
        public bool SendClient(object connection, FrameMessage message) => SendClientPayload(connection, FrameProtocol.Encode(message));
        public bool SendClientPayload(object connection, string text) => SendNativeClient(connection, packet.Invoke(new[] { eventType, text }), 1);
        public bool SendNativeClient(object connection, object value, int channel = -1)
        {
            if (connection == null || (bool)disconnected.GetValue(connection)) return false;
            toClient.Invoke(connection, new[] { value, (object)channel });
            return true;
        }
    }
    internal sealed class NativeFrameEnvelope
    {
        public object Connection, Playfield;
        public FrameMessage Message;
    }
}
