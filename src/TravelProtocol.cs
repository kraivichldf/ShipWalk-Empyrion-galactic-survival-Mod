using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ShipWalk
{
    internal enum TravelKind : byte { BoundaryRequest = 1, WarpRequest, Prepare, Ready, Cancel, Manifest, Stored, Arrived, Commit, Bound }

    internal sealed class TravelMember
    {
        public int Actor, Ship, Member = -1;
        public PassengerMode Mode;
        public Vector3 Position, Velocity;
        public Quaternion Rotation = Quaternion.Identity;
        public TravelMember Copy() => (TravelMember)MemberwiseClone();
        public FrameMessage Frame() => new FrameMessage { Actor = Actor, Ship = Ship, Member = Member, Mode = Mode,
            Position = Position, Rotation = Rotation, Velocity = Velocity };
        public static TravelMember From(FrameMessage frame) => new TravelMember { Actor = frame.Actor, Ship = frame.Ship, Member = frame.Member,
            Mode = frame.Mode, Position = frame.Position, Rotation = frame.Rotation, Velocity = frame.Velocity };
    }

    internal sealed class TravelPacket
    {
        public TravelKind Kind;
        public Guid Id, ClientSession, ServerSession;
        public int Ship, Leader, Reason = 6;
        public string Source = "", Destination = "";
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public TravelMember[] Members = Array.Empty<TravelMember>();
        public TravelPacket Copy() => new TravelPacket { Kind = Kind, Id = Id, ClientSession = ClientSession,
            ServerSession = ServerSession, Ship = Ship, Leader = Leader, Reason = Reason, Source = Source,
            Destination = Destination, Position = Position, Rotation = Rotation, Members = Members.Select(m => m.Copy()).ToArray() };
    }

    // Travel records contain identities/local poses only. Native WorldChange
    // continues to carry the ship snapshot, seats, docked vessels and world load.
    internal static class TravelProtocol
    {
        public const string Prefix = "ShipWalk/travel/2:";
        public const int MaxMembers = 1024, MaxBytes = 128 * 1024;
        public static bool IsEnvelope(string text) => text != null && text.Length <= Prefix.Length + 4 * ((MaxBytes + 2) / 3)
            && text.StartsWith(Prefix, StringComparison.Ordinal);
        public static string EncodeEnvelope(TravelPacket packet) => Prefix + Convert.ToBase64String(Encode(packet));
        public static bool TryEnvelope(string text, out TravelPacket packet)
        {
            packet = null;
            if (!IsEnvelope(text)) return false;
            try { return TryDecode(Convert.FromBase64String(text.Substring(Prefix.Length)), out packet); }
            catch (FormatException) { return false; }
        }
        public static byte[] Encode(TravelPacket p)
        {
            if (!Valid(p)) throw new InvalidDataException("Invalid ShipWalk travel record.");
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write((byte)2); w.Write((byte)p.Kind); w.Write(p.Id.ToByteArray());
                w.Write(p.ClientSession.ToByteArray()); w.Write(p.ServerSession.ToByteArray());
                w.Write(p.Ship); w.Write(p.Leader); w.Write(p.Reason); Write(w, p.Source); Write(w, p.Destination);
                Write(w, p.Position); Write(w, p.Rotation); w.Write((ushort)p.Members.Length);
                foreach (TravelMember member in p.Members)
                {
                    w.Write(member.Actor); w.Write(member.Ship); w.Write((byte)member.Mode);
                    Write(w, member.Position); Write(w, member.Rotation); Write(w, member.Velocity);
                    w.Write(member.Member);
                }
                return stream.ToArray();
            }
        }
        public static bool TryDecode(byte[] bytes, out TravelPacket packet)
        {
            packet = null;
            if (bytes == null || bytes.Length > MaxBytes) return false;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var r = new BinaryReader(stream))
                {
                    if (r.ReadByte() != 2) return false;
                    var p = new TravelPacket { Kind = (TravelKind)r.ReadByte(), Id = new Guid(Exact(r, 16)),
                        ClientSession = new Guid(Exact(r, 16)), ServerSession = new Guid(Exact(r, 16)),
                        Ship = r.ReadInt32(), Leader = r.ReadInt32(), Reason = r.ReadInt32(), Source = ReadText(r),
                        Destination = ReadText(r), Position = ReadVector(r), Rotation = ReadRotation(r) };
                    int count = r.ReadUInt16();
                    if (count > MaxMembers) return false;
                    p.Members = new TravelMember[count];
                    for (int i = 0; i < count; i++) p.Members[i] = new TravelMember { Actor = r.ReadInt32(),
                        Ship = r.ReadInt32(), Mode = (PassengerMode)r.ReadByte(), Position = ReadVector(r),
                        Rotation = ReadRotation(r), Velocity = ReadVector(r), Member = r.ReadInt32() };
                    if (stream.Position != stream.Length || !Valid(p)) return false;
                    packet = p; return true;
                }
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is OverflowException) { return false; }
        }
        public static bool Valid(TravelPacket p) => p != null && p.Kind >= TravelKind.BoundaryRequest && p.Kind <= TravelKind.Bound
            && p.Id != Guid.Empty && p.Ship > 0 && p.Leader >= 0 && p.Reason >= 0 && p.Reason <= 9
            && TextValid(p.Source) && TextValid(p.Destination) && MotionMath.Finite(p.Position) && RotationValid(p.Rotation)
            && p.Members != null && p.Members.Length <= MaxMembers && p.Members.All(MemberValid)
            && p.Members.All(m => m.Ship == p.Ship) && p.Members.Select(m => m.Actor).Distinct().Count() == p.Members.Length;
        public static bool MemberValid(TravelMember m) => m != null && m.Actor > 0 && m.Ship > 0
            && (m.Member == -1 || m.Member > 0)
            && m.Mode >= PassengerMode.Seated && m.Mode <= PassengerMode.Jetpack && MotionMath.Finite(m.Position)
            && RemoteFrameMath.ValidVelocity(m.Velocity) && RotationValid(m.Rotation);
        private static bool RotationValid(Quaternion q) => MotionMath.Finite(q.X) && MotionMath.Finite(q.Y)
            && MotionMath.Finite(q.Z) && MotionMath.Finite(q.W) && Math.Abs(q.LengthSquared() - 1f) < .02f;
        private static bool TextValid(string text) => text != null && Encoding.UTF8.GetByteCount(text) <= 1024;
        private static void Write(BinaryWriter w, string text) { byte[] b = Encoding.UTF8.GetBytes(text); w.Write((ushort)b.Length); w.Write(b); }
        private static string ReadText(BinaryReader r) { int n = r.ReadUInt16(); if (n > 1024) throw new InvalidDataException(); return new UTF8Encoding(false, true).GetString(Exact(r, n)); }
        private static byte[] Exact(BinaryReader r, int n) { byte[] b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b; }
        private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        private static void Write(BinaryWriter w, Quaternion q) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static Quaternion ReadRotation(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
