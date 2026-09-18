using System;
using System.Linq;
using System.Numerics;
using ShipWalk;

internal static class DockingTests
{
    private sealed class Vessel { public int Id; public Vessel Parent; public bool Alive = true; }
    private sealed class Surface { public Vessel Owner; public bool Present; }
    private static Vessel Root(Vessel vessel) => DockingTopology.Root(vessel, x => x.Parent, x => x.Alive);
    private static bool Member(Vessel root, Vessel vessel) => DockingTopology.Member(root, vessel, x => x.Parent, x => x.Alive);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(Vector3 a, Vector3 b, string message) => Check(Vector3.Distance(a, b) < .002f, message + ": " + a + " != " + b);
    private static LocalFramePose Pose(Vector3 p, float yaw) => new LocalFramePose(p, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

    public static void RecordedCollisionOmission()
    {
        var cv = new Vessel { Id = 1011 }; var sv = new Vessel { Id = 5004, Parent = cv };
        var foreign = new Vessel { Id = 6000 };
        var sources = Enumerable.Range(0, 1675).Select(_ => new Surface { Owner = cv })
            .Concat(Enumerable.Range(0, 177).Select(_ => new Surface { Owner = sv }))
            .Concat(new[] { new Surface { Owner = foreign } }).ToArray();
        // Reproduce the logged 1675 unchanged shapes with the former exact-owner filter.
        Check(sources.Count(x => ReferenceEquals(x.Owner, cv)) == 1675, "Recorded omission fixture changed.");
        var index = new LiveGeometryIndex<Surface, Surface>();
        Action refresh = () => { foreach (bool _ in index.Reconcile(sources, x => Member(cv, x.Owner), x => x,
            (s, copy) => copy.Present = true, (s, copy) => copy.Present = false)) { } };
        refresh();
        Check(index.Count == 1852 && sources.Where(x => x.Owner == sv).All(x => x.Present), "Docked SV surfaces were excluded.");
        Check(!sources.Last().Present, "Unrelated nearby vessel became carrier collision.");
        sv.Parent = null; refresh();
        Check(index.Count == 1675 && sources.Where(x => x.Owner == sv).All(x => !x.Present), "Undocking left ghost collision.");
        sv.Parent = cv; refresh();
        Check(index.Count == 1852, "Redocking lost surfaces.");
    }
    public static void RootAndAssociation()
    {
        var cv = new Vessel { Id = 1011 }; var sv = new Vessel { Id = 5004, Parent = cv };
        var nested = new Vessel { Id = 7000, Parent = sv };
        Check(Root(nested) == cv && Member(cv, sv), "Nested docking root not resolved.");
        Check(DockingTopology.Occupant(5004, -1, -1, true, 1011) == 5004, "Jump/elevator forgot occupied SV.");
        Check(DockingTopology.Occupant(5004, 1011, -1, true, 1011) == 1011, "Landing on CV retained the departing SV.");
        Check(DockingTopology.Occupant(1011, 1011, 5004, true, 1011) == 5004, "Native SV seat did not supersede floor.");
        Check(DockingTopology.Occupant(5004, -1, -1, false, 1011) == 1011, "Leaving member envelope retained it indefinitely.");
        sv.Parent = null;
        Check(Root(sv) == sv && Root(nested) == sv && Root(cv) == cv && !Member(cv, sv), "Undocking changed unrelated CV passengers.");
        sv.Parent = nested;
        Check(Root(sv) == null, "Docking cycle accepted.");
        sv.Parent = cv; cv.Alive = false;
        Check(Root(sv) == null, "Removed carrier accepted.");
    }
    public static void ContinuousRebase()
    {
        var cv = Pose(new Vector3(2000, 50, -800), .75f);
        var sv = Pose(cv.ToWorldPoint(new Vector3(12, 4, -35)), -1.2f);
        Vector3 point = new Vector3(13, 6, -34), velocity = new Vector3(2, 6, -1);
        Vector3 cvLinear = new Vector3(70, 1, 60), svLinear = new Vector3(68, 2, 61);
        Vector3 cvAngular = new Vector3(0, .15f, 0), svAngular = new Vector3(0, -.2f, 0);
        Quaternion heading = Quaternion.CreateFromYawPitchRoll(.2f, .3f, .1f);
        var result = DockingRebase.Change(cv, sv, point, heading, velocity, cvLinear, cvAngular, svLinear, svAngular);
        Vector3 world = cv.ToWorldPoint(point);
        Near(sv.ToWorldPoint(result.Position), world, "Undocking teleported the passenger");
        Vector3 before = DockingRebase.PointVelocity(cv, cvLinear, cvAngular, world) + Vector3.Transform(velocity, cv.Rotation);
        Vector3 after = DockingRebase.PointVelocity(sv, svLinear, svAngular, world) + Vector3.Transform(result.Velocity, sv.Rotation);
        Near(before, after, "Undocking lost or doubled translational/angular velocity");
        Check(Math.Abs(Quaternion.Dot(cv.Rotation * heading, sv.Rotation * result.Rotation)) > .99999f, "Camera facing snapped.");
        var back = DockingRebase.Change(sv, cv, result.Position, result.Rotation, result.Velocity, svLinear, svAngular, cvLinear, cvAngular);
        Near(back.Position, point, "Redocking position changed"); Near(back.Velocity, velocity, "Redocking momentum changed");
    }
    public static void PresentationRebase()
    {
        var oldFrame = Pose(new Vector3(50, 2, 17), 1);
        var nextFrame = Pose(new Vector3(52, 4, 10), -.5f);
        var presentation = new LocalPresentation();
        presentation.Reset(new Vector3(1, 2, 3)); presentation.Advance(new Vector3(1.1f, 2, 3));
        Vector3[] samples = new[] { 0f, .25f, .6f, 1f }.Select(t => presentation.WorldPoint(oldFrame, t)).ToArray();
        Vector3 expectedStep = Vector3.Transform(new Vector3(.1f, 0, 0), oldFrame.Rotation);
        presentation.Reframe(oldFrame, nextFrame);
        int i = 0; foreach (float t in new[] { 0f, .25f, .6f, 1f }) Near(samples[i++], presentation.WorldPoint(nextFrame, t), "Interpolated view jumped");
        Near(presentation.TakeLocomotion(nextFrame.Rotation), expectedStep, "Coordinate switch became walking animation");
    }
    public static void DockedBlockCoordinates()
    {
        var nativeSv = Pose(new Vector3(200, 15, 80), .8f);
        var nativeGrid = new LocalFramePose(nativeSv.ToWorldPoint(new Vector3(1, 0, -2)), nativeSv.Rotation);
        var svInCv = Pose(new Vector3(20, 4, -70), -.7f);
        var own = new LocalBlockCoordinates(nativeSv, nativeGrid, .5f);
        var carrier = own.InParent(svInCv);
        Vector3 point = new Vector3(2, 1, 3);
        Near(carrier.Point(svInCv.ToWorldPoint(point)), own.Point(point), "Moving child elevator used carrier block scale");
        carrier.Bounds(new Vector3(15, 0, -75), new Vector3(25, 8, -65), out Vector3 min, out Vector3 max);
        for (int i = 0; i < 8; i++)
        {
            var p = carrier.Point(new Vector3((i & 1) == 0 ? 15 : 25, (i & 2) == 0 ? 0 : 8, (i & 4) == 0 ? -75 : -65));
            Check(p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z,
                "Rotated child contact bounds lost a corner.");
        }
    }
    public static void WireMembershipAndTravel()
    {
        var state = new FrameMessage { Kind = FrameMessageKind.Publish, ClientSession = Guid.NewGuid(), Ship = 1011,
            Member = 5004, Sequence = 1, Generation = 1, Actor = 1013, Mode = PassengerMode.Walking, Position = new Vector3(10, 2, -30) };
        string wire = FrameProtocol.Encode(state);
        Check(FrameProtocol.TryDecode(wire, out FrameMessage decoded) && decoded.Member == 5004 && decoded.Ship == 1011, "Occupied vessel lost in movement codec.");
        Check(!FrameProtocol.TryDecode(wire.Replace(FrameProtocol.Prefix, "ShipWalk/frame/3:"), out _), "Old clients silently joined new docking protocol.");
        var packet = new TravelPacket { Id = Guid.NewGuid(), Kind = TravelKind.Manifest, Ship = 1011, Leader = 1013,
            Source = "space", Destination = "planet", Members = new[] { TravelMember.From(decoded) } };
        byte[] bytes = TravelProtocol.Encode(packet);
        Check(TravelProtocol.TryDecode(bytes, out TravelPacket restored) && restored.Members[0].Frame().Member == 5004, "Travel lost occupied docked vessel.");
        var changed = restored.Copy(); changed.Members[0].Member = 6000;
        Check(!TravelTransfer.SameManifest(restored, changed), "Stored acknowledgement substituted the occupied vessel.");
        bytes[0] = 1; Check(!TravelProtocol.TryDecode(bytes, out _), "Old travel record interpreted as docking membership.");
    }
    public static void AirborneHandoverBudget()
    {
        var frame = Pose(Vector3.Zero, 0);
        var rebase = DockingRebase.Change(frame, frame, Vector3.Zero, Quaternion.Identity, new Vector3(0, 6, 0),
            new Vector3(100, 0, 0), Vector3.Zero, Vector3.Zero, Vector3.Zero);
        var previous = new FrameMessage { Ship = 1011, Member = 5004, Mode = PassengerMode.Jumping };
        var next = new FrameMessage { ClientSession = Guid.NewGuid(), Kind = FrameMessageKind.Publish,
            Ship = 5004, Member = 5004, Mode = PassengerMode.Jumping, Velocity = rebase.Velocity };
        Check(FrameProtocol.TryDecode(FrameProtocol.Encode(next), out _), "100 m/s handover plus jump disabled the transport.");
        var window = new DockingMotionWindow();
        Check(window.Distance(1, rebase.Velocity, .05, 0) == 4.75, "Normal movement validation weakened without a handover.");
        window.Accepted(1, previous, next, 1);
        Check(window.Distance(1, rebase.Velocity, .05, 1.1) > 8, "Accepted rebase momentum exceeded walking validation.");
        window.Accepted(1, next, next, 1.9);
        Check(window.Distance(1, rebase.Velocity, .05, 2.1) == 4.75, "Repeated ordinary states renewed the handover window.");
        window.Accepted(1, previous, next, 3); window.Clear();
        Check(window.Distance(1, rebase.Velocity, .05, 3.1) == 4.75, "World/session reset retained handover allowance.");
    }
    public static void RelayUndocking()
    {
        var cv = new Vessel { Id = 1011 }; var sv = new Vessel { Id = 5004, Parent = cv }; var foreign = new Vessel { Id = 9000 };
        var vessels = new[] { cv, sv, foreign };
        var relay = new FrameRelay(); object connection = new object(), observerConnection = new object(), context = new object();
        Guid client = Guid.NewGuid();
        Func<FrameMessage, FrameMessage, bool> validate = (m, old) => Member(vessels.SingleOrDefault(x => x.Id == m.Ship), vessels.SingleOrDefault(x => x.Id == m.Member));
        relay.Receive(1013, connection, context, new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = client }, 0, validate);
        relay.Receive(1014, observerConnection, context, new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = Guid.NewGuid() }, 0, validate);
        var state = new FrameMessage { Kind = FrameMessageKind.Publish, ClientSession = client, ServerSession = relay.Session,
            Ship = 1011, Member = 5004, Sequence = 1, Generation = 1, Mode = PassengerMode.Walking, Position = new Vector3(10, 4, 20) };
        var observer = new RemotePassenger();
        var first = relay.Receive(1013, connection, context, state, .1, validate).Single(x => x.Connection == observerConnection).Message;
        Check(observer.Accept(first, .1), "Docked observer baseline rejected.");
        var forged = state.Copy(); forged.Sequence = 2; forged.Member = foreign.Id;
        Check(relay.Receive(1013, connection, context, forged, .15, validate).Count == 0, "Unrelated vessel claimed as docked.");
        sv.Parent = null;
        Check(relay.Receive(1013, connection, context, state, .2, validate).Count == 0, "Former carrier accepted departing member.");
        state.Ship = sv.Id; state.Sequence = 3; state.Generation = 2; state.Position = Vector3.Zero;
        var next = relay.Receive(1013, connection, context, state, .25, validate).Single(x => x.Connection == observerConnection).Message;
        Check(next.Generation > first.Generation && observer.Accept(next, .25), "Undocking did not establish new reference generation.");
        Check(!observer.Accept(first, .3), "Delayed CV packet undid SV handover.");
        Check(observer.Sample(.3, out Vector3 p, out _) && p == Vector3.Zero, "Observer interpolated between different reference frames.");
        Check(relay.Aboard(cv.Id, context, .3).Length == 0 && relay.Aboard(sv.Id, context, .3).Length == 1, "Server roster lost the departing passenger.");
    }
}
