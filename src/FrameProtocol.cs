using System;
using System.IO;
using System.Numerics;

namespace ShipWalk
{
    internal enum FrameMessageKind : byte { Hello = 1, Welcome, Publish, State, Receipt, ShipPoseRequest, ShipPose }
    internal enum PassengerMode : byte { World, Seated, Walking, Jumping, Elevator, Jetpack }
    internal enum ShipPosePurpose : byte { NativeOwner, CurrentVessel }

    internal sealed class FrameMessage
    {
        public FrameMessageKind Kind;
        public Guid ClientSession, ServerSession;
        public uint Generation, Sequence;
        public int Actor, Ship = -1, Member = -1;
        public long Tick;
        public PassengerMode Mode;
        public ShipPosePurpose Purpose;
        public Vector3 Position, Velocity;
        public Quaternion Rotation = Quaternion.Identity;
        public FrameMessage Copy() => (FrameMessage)MemberwiseClone();
    }

    // A namespaced payload inside the game's existing reliable mod-event packet.
    // No game packet IDs, chat messages, ports or credentials are invented.
    internal static class FrameProtocol
    {
        public const int Version = 4;
        public const string Prefix = "ShipWalk/frame/4:";
        public const int PayloadBytes = 103;
        public static bool IsEnvelope(string text) => text != null
            && text.Length == Prefix.Length + 4 * ((PayloadBytes + 2) / 3) && text.StartsWith(Prefix, StringComparison.Ordinal);
        public static bool UseGameplayChannel(int nativeVariant, string text) => nativeVariant == 2 && IsEnvelope(text);
        public static string Encode(FrameMessage message)
        {
            if (!Valid(message)) throw new InvalidDataException("Invalid ShipWalk frame message.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)message.Kind);
                writer.Write(message.ClientSession.ToByteArray()); writer.Write(message.ServerSession.ToByteArray());
                writer.Write(message.Generation); writer.Write(message.Sequence);
                writer.Write(message.Actor); writer.Write(message.Ship); writer.Write(message.Tick);
                writer.Write((byte)message.Mode);
                writer.Write((byte)message.Purpose);
                Write(writer, message.Position); Write(writer, message.Velocity);
                writer.Write(message.Rotation.X); writer.Write(message.Rotation.Y);
                writer.Write(message.Rotation.Z); writer.Write(message.Rotation.W);
                writer.Write(message.Member);
                return Prefix + Convert.ToBase64String(stream.ToArray());
            }
        }
        public static bool TryDecode(string text, out FrameMessage message)
        {
            message = null;
            if (!IsEnvelope(text)) return false;
            try
            {
                byte[] bytes = Convert.FromBase64String(text.Substring(Prefix.Length));
                if (bytes.Length != PayloadBytes) return false;
                using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    var value = new FrameMessage {
                        Kind = (FrameMessageKind)reader.ReadByte(), ClientSession = new Guid(reader.ReadBytes(16)),
                        ServerSession = new Guid(reader.ReadBytes(16)), Generation = reader.ReadUInt32(), Sequence = reader.ReadUInt32(),
                        Actor = reader.ReadInt32(), Ship = reader.ReadInt32(), Tick = reader.ReadInt64(),
                        Mode = (PassengerMode)reader.ReadByte(), Purpose = (ShipPosePurpose)reader.ReadByte(),
                        Position = Read(reader), Velocity = Read(reader),
                        Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Member = reader.ReadInt32()
                    };
                    if (!Valid(value)) return false;
                    message = value; return true;
                }
            }
            catch (Exception error) when (error is FormatException || error is IOException || error is ArgumentException) { return false; }
        }
        private static bool Valid(FrameMessage m) => m != null && m.Kind >= FrameMessageKind.Hello && m.Kind <= FrameMessageKind.ShipPose
            && m.Purpose >= ShipPosePurpose.NativeOwner && m.Purpose <= ShipPosePurpose.CurrentVessel
            && (m.Purpose == ShipPosePurpose.NativeOwner || m.Kind == FrameMessageKind.ShipPoseRequest || m.Kind == FrameMessageKind.ShipPose)
            && m.Mode >= PassengerMode.World && m.Mode <= PassengerMode.Jetpack && m.ClientSession != Guid.Empty
            && (m.Member == -1 || m.Member > 0)
            && MotionMath.Finite(m.Position) && MotionMath.Finite(m.Velocity)
            // Frame changes can add ship point velocity to a jumping passenger.
            // Ordinary walking distance is validated separately by the worker.
            && m.Velocity.LengthSquared() <= 90000f
            && MotionMath.Finite(m.Rotation.X) && MotionMath.Finite(m.Rotation.Y)
            && MotionMath.Finite(m.Rotation.Z) && MotionMath.Finite(m.Rotation.W)
            && Math.Abs(m.Rotation.LengthSquared() - 1f) < .02f;
        private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        private static Vector3 Read(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    internal sealed class FrameSequence
    {
        public uint Sequence { get; private set; }
        public uint Generation { get; private set; }
        public int Ship { get; private set; } = -1;
        public bool Accept(FrameMessage message)
        {
            if (message.Sequence == 0 || message.Sequence <= Sequence || message.Generation < Generation
                || (message.Ship != Ship && message.Generation <= Generation)) return false;
            Sequence = message.Sequence; Generation = message.Generation; Ship = message.Ship; return true;
        }
    }

    internal sealed class RemotePassenger
    {
        public FrameMessage State { get; private set; }
        public double Received { get; private set; }
        private FrameMessage previous;
        private double previousTime;
        private Vector3 locomotionPoint;
        private bool haveLocomotion;
        private readonly FrameSequence order = new FrameSequence();
        public bool Accept(FrameMessage next, double now)
        {
            if (!order.Accept(next)) return false;
            if (State == null || State.Generation != next.Generation || State.Ship != next.Ship || State.Mode != next.Mode || now - Received > 1.5)
                haveLocomotion = false;
            previous = State != null && State.Generation == next.Generation && State.Ship == next.Ship ? State : next;
            previousTime = State == null ? now : Received;
            State = next.Copy(); Received = now; return true;
        }
        public bool Sample(double now, out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.Identity;
            if (State == null || State.Mode == PassengerMode.World || now - Received > 1.5) return false;
            float interval = (float)Math.Max(.05, Math.Min(.25, Received - previousTime));
            float fraction = Math.Max(0f, Math.Min(1f, (float)(now - Received) / interval));
            position = Vector3.Lerp(previous.Position, State.Position, fraction);
            rotation = Quaternion.Slerp(previous.Rotation, State.Rotation, fraction); return true;
        }
        public bool TakeLocomotion(double now, Quaternion shipRotation, out Vector3 delta)
        {
            delta = Vector3.Zero;
            if (!Sample(now, out Vector3 point, out _)) { haveLocomotion = false; return false; }
            // Use the same interpolated local point as the observer's model.
            // Vessel travel/rotation and attachment corrections are not steps.
            if (haveLocomotion && State.Mode == PassengerMode.Walking)
                delta = Vector3.Transform(point - locomotionPoint, shipRotation);
            locomotionPoint = point; haveLocomotion = true;
            return true;
        }
    }
}
