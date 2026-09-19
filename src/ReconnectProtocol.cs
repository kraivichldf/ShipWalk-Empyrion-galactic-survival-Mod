using System;
using System.IO;
using System.Numerics;
using System.Text;

namespace ShipWalk
{
    internal enum ReconnectKind : byte { Query = 1, Save, Clear, Offer, Ready, Complete, Abort }
    internal sealed class PassengerRecord
    {
        public string Identity = "", Area = "";
        public int Actor, Vessel;
        public PassengerMode Mode;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public long Updated;
        public PassengerRecord Copy() => (PassengerRecord)MemberwiseClone();
        public static LocalFramePose IntoMember(LocalFramePose memberInRoot, Vector3 point, Quaternion rotation)
            => new LocalFramePose(memberInRoot.ToLocalPoint(point), Quaternion.Normalize(Quaternion.Inverse(memberInRoot.Rotation) * rotation));
        public LocalFramePose IntoRoot(LocalFramePose memberInRoot)
            => new LocalFramePose(memberInRoot.ToWorldPoint(Position), Quaternion.Normalize(memberInRoot.Rotation * Rotation));
        public bool Valid => Identity.Length > 0 && Identity.Length <= 128 && Area.Length <= 256
            && Actor > 0 && Vessel > 0 && Mode >= PassengerMode.Seated && Mode <= PassengerMode.Jetpack
            && MotionMath.Finite(Position) && Position.LengthSquared() <= 100000000
            && MotionMath.Finite(Rotation.X) && MotionMath.Finite(Rotation.Y) && MotionMath.Finite(Rotation.Z)
            && MotionMath.Finite(Rotation.W) && Math.Abs(Rotation.LengthSquared() - 1) < .02f;
    }
    internal sealed class ReconnectPacket
    {
        public ReconnectKind Kind;
        public Guid Id, Epoch, ClientSession, ServerSession;
        public int Actor;
        public long Sequence;
        public string Identity = "", Source = "", Destination = "", Reason = "";
        public PassengerRecord Record;
        public Vector3 WorldPosition, WorldRotation;
        public ReconnectPacket Copy()
        { var result = (ReconnectPacket)MemberwiseClone(); result.Record = Record?.Copy(); return result; }
    }
    internal static class ReconnectProtocol
    {
        public const string Prefix = "ShipWalk/reconnect/1:";
        public const int MaxBytes = 4096;
        private const int Magic = 0x31525753;
        public static bool IsEnvelope(string text) => text != null && text.Length <= 5500 && text.StartsWith(Prefix, StringComparison.Ordinal);
        public static string Envelope(ReconnectPacket p) => Prefix + Convert.ToBase64String(Encode(p));
        public static bool TryEnvelope(string text, out ReconnectPacket p)
        {
            p = null; if (!IsEnvelope(text)) return false;
            try { return TryDecode(Convert.FromBase64String(text.Substring(Prefix.Length)), out p); }
            catch (FormatException) { return false; }
        }
        public static byte[] Encode(ReconnectPacket p)
        {
            if (!Valid(p)) throw new InvalidDataException("Invalid reconnect packet.");
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(Magic); w.Write((byte)p.Kind); w.Write(p.Id.ToByteArray()); w.Write(p.Epoch.ToByteArray());
                w.Write(p.ClientSession.ToByteArray()); w.Write(p.ServerSession.ToByteArray()); w.Write(p.Actor); w.Write(p.Sequence);
                w.Write(p.Identity); w.Write(p.Source); w.Write(p.Destination); w.Write(p.Reason);
                Vector(w, p.WorldPosition); Vector(w, p.WorldRotation); w.Write(p.Record != null);
                if (p.Record != null) WriteRecord(w, p.Record);
                w.Flush(); return stream.ToArray();
            }
        }
        public static bool TryDecode(byte[] bytes, out ReconnectPacket p)
        {
            p = null; if (bytes == null || bytes.Length > MaxBytes) return false;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return false;
                    var value = new ReconnectPacket { Kind = (ReconnectKind)r.ReadByte(), Id = Guid(r), Epoch = Guid(r),
                        ClientSession = Guid(r), ServerSession = Guid(r), Actor = r.ReadInt32(), Sequence = r.ReadInt64(),
                        Identity = r.ReadString(), Source = r.ReadString(), Destination = r.ReadString(), Reason = r.ReadString(),
                        WorldPosition = Vector(r), WorldRotation = Vector(r) };
                    if (r.ReadBoolean()) value.Record = ReadRecord(r);
                    if (r.BaseStream.Position != r.BaseStream.Length || !Valid(value)) return false;
                    p = value; return true;
                }
            }
            catch (Exception e) when (e is IOException || e is ArgumentException) { return false; }
        }
        private static bool Valid(ReconnectPacket p) => p != null && p.Kind >= ReconnectKind.Query && p.Kind <= ReconnectKind.Abort
            && p.Epoch != System.Guid.Empty && p.Actor > 0 && p.Sequence >= 0
            && p.Identity != null && p.Identity.Length <= 128 && p.Source != null && p.Source.Length <= 256
            && p.Destination != null && p.Destination.Length <= 256 && p.Reason != null && p.Reason.Length <= 256
            && MotionMath.Finite(p.WorldPosition) && MotionMath.Finite(p.WorldRotation) && (p.Record == null || p.Record.Valid);
        internal static void WriteRecord(BinaryWriter w, PassengerRecord p)
        {
            w.Write(p.Identity); w.Write(p.Area); w.Write(p.Actor); w.Write(p.Vessel); w.Write((byte)p.Mode);
            Vector(w, p.Position); w.Write(p.Rotation.X); w.Write(p.Rotation.Y); w.Write(p.Rotation.Z); w.Write(p.Rotation.W); w.Write(p.Updated);
        }
        internal static PassengerRecord ReadRecord(BinaryReader r) => new PassengerRecord { Identity = r.ReadString(), Area = r.ReadString(),
            Actor = r.ReadInt32(), Vessel = r.ReadInt32(), Mode = (PassengerMode)r.ReadByte(), Position = Vector(r),
            Rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Updated = r.ReadInt64() };
        private static Guid Guid(BinaryReader r) => new Guid(r.ReadBytes(16));
        private static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        private static Vector3 Vector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
