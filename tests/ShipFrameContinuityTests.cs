using System;
using System.Linq;
using System.Numerics;
using ShipWalk;

internal static class ShipFrameContinuityTests
{
    private static readonly Quaternion Identity = Quaternion.Identity;
    private static LocalFramePose Pose(float x) => new LocalFramePose(new Vector3(x, 20, 30), Identity);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(Vector3 actual, Vector3 expected)
        => Check(Vector3.Distance(actual, expected) < .003f, "Expected " + expected + "; got " + actual);
    private static FrameMessage Reply(ShipFrameContinuity frame, uint sequence, long tick, float position)
        => new FrameMessage { Kind = FrameMessageKind.ShipPose, Ship = 1015, Generation = frame.Epoch,
            Sequence = sequence, Tick = tick, Position = Pose(position).Position, Rotation = Identity,
            Velocity = new Vector3(48.342f, 0, 0) };

    public static void RecordedDeparture()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), new Vector3(48.342f, 0, 0), 10);
        var bad = Pose(396.468f);
        Check(!RemoteFrameMath.TryTransport(Pose(0), bad, Identity, .025f, Vector3.Zero, out _), "Old recorded failure did not reproduce.");
        frame.BeginHandoff();
        Vector3 local = new Vector3(.982f, 18.206f, 13.162f), jump = new Vector3(0, 6, 0);
        Check(frame.Resolve(bad, 10.025f, Vector3.Zero) == ShipPoseState.Holding, "Bad world sample was accepted or detached passenger.");
        Near(frame.Pose.Position, Pose(0).Position);
        Near(frame.Pose.ToLocalPoint(frame.Pose.ToWorldPoint(local)), local);
        Near(PassengerLocalMotion.Velocity(jump), jump);
        frame.TrackRequest(1, 10.03f);
        Check(frame.Confirm(Reply(frame, 1, 10030, 1.45f), 10.05f), "First server response missing.");
        Check(frame.Resolve(bad, 10.06f, Vector3.Zero) == ShipPoseState.Holding, "Single response proved handoff prematurely.");
        frame.TrackRequest(2, 10.18f);
        Check(frame.Confirm(Reply(frame, 2, 10180, 8.7f), 10.20f), "Second server response missing.");
        Check(frame.Resolve(bad, 10.21f, Vector3.Zero) == ShipPoseState.Confirmed, "Confirmed owner baseline did not recover visual frame.");
        Check(!frame.AwaitingHandoff, "Handoff never completed.");
        Near(frame.Pose.ToLocalPoint(frame.Pose.ToWorldPoint(local)), local);
        Near(PassengerLocalMotion.Velocity(jump), jump);
        // Native rendering catches up: return to its ordinary interpolation.
        Check(frame.Resolve(Pose(11), 10.23f, new Vector3(48.342f, 0, 0)) == ShipPoseState.Live, "Recovered native display not retained.");
    }

    public static void RecordedSpeedDipRecovery()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), new Vector3(66.102f, 0, 0), 10);
        frame.BeginHandoff();
        // The 21:14 seat exit kept ~66 m/s on the worker, but the native
        // displayed history reset to 14.30, then 11.443 m/s on the client.
        frame.Resolve(Pose(.572f), 10.04f, new Vector3(14.3f, 0, 0));
        Check(frame.State == ShipPoseState.Predicting && frame.Velocity.X > 66,
            "Client history reset replaced the departing vessel speed.");
        Near(frame.Pose.Position, Pose(66.102f * .04f).Position);
        frame.Resolve(Pose(1.716f), 10.14f, new Vector3(11.443f, 0, 0));
        Near(frame.Pose.Position, Pose(66.102f * .14f).Position);
        frame.TrackRequest(1, 10.15f); var one = Reply(frame, 1, 10150, 9.9f); one.Velocity.X = 66;
        frame.Confirm(one, 10.16f);
        frame.TrackRequest(2, 10.20f); var two = Reply(frame, 2, 10200, 13.2f); two.Velocity.X = 66;
        frame.Confirm(two, 10.21f);
        Check(frame.Resolve(Pose(2.3f), 10.22f, new Vector3(11.443f, 0, 0)) == ShipPoseState.Confirmed,
            "Two confirmations still selected the low-speed native history.");
        Check(frame.Resolve(Pose(15.3f), 10.23f, new Vector3(65.8f, 0, 0)) == ShipPoseState.Live,
            "Native history did not resume after catching up.");
    }

    public static void HandoffPredictionLimits()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), new Vector3(66, 0, 0), 10); frame.BeginHandoff();
        frame.Resolve(Pose(0), 10.1f, Vector3.Zero);
        Check(frame.State == ShipPoseState.Predicting, "Short ownership gap did not bridge.");
        Vector3 held = frame.Pose.Position;
        frame.Resolve(Pose(0), 10.4f, Vector3.Zero);
        Check(frame.State == ShipPoseState.Holding && frame.Pose.Position == held, "Unconfirmed prediction exceeded its time budget.");
        Check(frame.Resolve(Pose(0), 13.2f, Vector3.Zero) == ShipPoseState.Expired, "Missing server never expired.");
        // A real server-side collision during the handoff must still stop it.
        frame.Seed(Pose(0), new Vector3(66, 0, 0), 20); frame.BeginHandoff();
        frame.TrackRequest(1, 20.01f); var one = Reply(frame, 1, 20010, .5f); one.Velocity = Vector3.Zero;
        frame.Confirm(one, 20.02f);
        frame.TrackRequest(2, 20.06f); var two = Reply(frame, 2, 20060, .5f); two.Velocity = Vector3.Zero;
        frame.Confirm(two, 20.07f);
        Check(frame.Resolve(Pose(4), 20.08f, new Vector3(66, 0, 0)) == ShipPoseState.Confirmed,
            "A stale moving client history overrode a confirmed server collision stop.");
        Near(frame.Velocity, Vector3.Zero); Near(frame.Pose.Position, Pose(.5f).Position);
        // Ordinary unseated slowing, without a handoff, stays native.
        frame.Seed(Pose(0), new Vector3(66, 0, 0), 30);
        Check(frame.Resolve(Pose(1), 30.1f, Vector3.Zero) == ShipPoseState.Live && frame.Velocity == Vector3.Zero,
            "Presentation began imposing cruise control on ordinary physics.");
    }

    public static void StaleAndMissingConfirmation()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), Vector3.Zero, 1); frame.BeginHandoff();
        frame.TrackRequest(1, 1); FrameMessage old = Reply(frame, 1, 1000, 0);
        frame.BeginHandoff(); Check(!frame.Confirm(old, 1.01f), "Prior handoff response accepted.");
        frame.TrackRequest(2, 1.02f); var one = Reply(frame, 2, 1020, 0);
        Check(frame.Confirm(one, 1.03f), "Current response rejected.");
        Check(!frame.Confirm(one, 1.04f), "Duplicate confirmation counted twice.");
        frame.TrackRequest(3, 1.05f); var reverseTime = Reply(frame, 3, 1010, 0);
        Check(!frame.Confirm(reverseTime, 1.06f), "Reordered server timestamp accepted.");
        frame.TrackRequest(4, 1.07f); var invalid = Reply(frame, 4, 1070, 0); invalid.Position = new Vector3(float.NaN, 0, 0);
        Check(!frame.Confirm(invalid, 1.08f), "NaN server position accepted.");
        frame.TrackRequest(5, 1.1f); Check(!frame.Confirm(Reply(frame, 5, 1100, 0), 3.2f), "Expired request accepted.");
        Check(frame.Resolve(Pose(396.468f), 3.3f, Vector3.Zero) == ShipPoseState.Holding, "Recovery not held.");
        Check(frame.Resolve(Pose(396.468f), 6.31f, Vector3.Zero) == ShipPoseState.Expired, "Recovery hangs without a deadline.");
    }

    public static void InconsistentReferences()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), Vector3.Zero, 1); frame.BeginHandoff();
        frame.Resolve(Pose(396.468f), 1.01f, Vector3.Zero);
        frame.TrackRequest(1, 1.02f); frame.Confirm(Reply(frame, 1, 1020, 1), 1.03f);
        frame.TrackRequest(2, 1.17f); frame.Confirm(Reply(frame, 2, 1170, 9999), 1.18f);
        Check(frame.Resolve(Pose(396.468f), 1.19f, Vector3.Zero) == ShipPoseState.Holding, "Inconsistent server pair accepted.");
    }

    public static void RotatingLocalFrameAndLook()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), Vector3.Zero, 1);
        Vector3 local = new Vector3(3, 2, 5), accepted = new Vector3(0, 6, 0);
        Quaternion localLook = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f);
        for (int i = 1; i <= 200; i++)
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(i * .005f, i * .002f, i * .001f);
            var hull = new LocalFramePose(new Vector3(i * 2, 20, 30), rotation);
            Check(frame.Resolve(hull, 1 + i * .025f, new Vector3(80, 0, 0)) == ShipPoseState.Live, "Continuous rotation discarded local frame.");
            Quaternion published = rotation * localLook;
            localLook = PassengerLocalMotion.ConsumeLook(localLook, published, published);
            Check(Math.Abs(Quaternion.Dot(localLook, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f))) > .99999f, "Ship rotation became mouse look.");
            Near(frame.Pose.ToLocalPoint(frame.Pose.ToWorldPoint(local)), local);
            Near(PassengerLocalMotion.Velocity(accepted), accepted);
        }
        Quaternion mouse = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .1f), world = frame.Pose.Rotation * localLook;
        Quaternion changed = PassengerLocalMotion.ConsumeLook(localLook, world, world * mouse);
        Check(Math.Abs(Quaternion.Dot(changed, localLook * mouse)) > .99999f, "User mouse movement lost.");
        Check(!RemoteFrameMath.TryTransport(Pose(0), new LocalFramePose(Pose(0).Position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, .021f)), Identity, .025f, Vector3.Zero, out _), "Old rotation rejection did not reproduce.");
    }

    public static void LocalVelocityAndWorldDeparture()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(0), new Vector3(65, 0, 0), 1);
        Vector3 jump = new Vector3(0, 6, 0);
        foreach (float speed in new[] { 65f, 6.463f, .075f, 0f, 65f, 100f })
        {
            frame.Resolve(Pose(0), 1.1f, new Vector3(speed, 0, 0));
            Near(PassengerLocalMotion.Velocity(jump), jump);
            Near(frame.WorldVelocity(Vector3.Zero, jump), new Vector3(speed, 6, 0));
        }
        frame.Seed(Pose(0), Vector3.Zero, 2);
        frame.Resolve(new LocalFramePose(Pose(0).Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .05f)), 2.05f, Vector3.Zero);
        Vector3 point = new Vector3(10, 0, 0);
        Check(frame.WorldVelocity(point, Vector3.Zero).Length() > 9.9f, "World departure omitted rotational point velocity.");
        var bounds = new LocalVolume(new Vector3(-10), new Vector3(10));
        Check(!bounds.Contains(new Vector3(20, 0, 0), 2), "Actual local departure was hidden.");
    }

    public static void OriginRebase()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(Pose(8000), Vector3.Zero, 1);
        Vector3 origin = new Vector3(8000, 0, 0), scene = Pose(8000).Position - origin;
        origin += new Vector3(1600, 0, -3200); scene -= new Vector3(1600, 0, -3200);
        Check(frame.Resolve(new LocalFramePose(scene + origin, Identity), 1.025f, Vector3.Zero) == ShipPoseState.Live, "Real floating-origin rebase mistaken for a jump.");
    }

    public static void AuthenticatedServerBaseline()
    {
        var relay = new FrameRelay(); object connection = new object(), context = new object(); Guid client = Guid.NewGuid();
        FrameMessage greeting = new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = client };
        relay.Receive(1013, connection, context, greeting, 1, (m, p) => true);
        var request = new FrameMessage { Kind = FrameMessageKind.ShipPoseRequest, ClientSession = client, ServerSession = relay.Session,
            Ship = 1015, Position = new Vector3(1, 18, 13), Mode = PassengerMode.Walking, Generation = 9, Sequence = 3 };
        int captures = 0;
        Func<FrameMessage, FrameMessage> capture = _ => { captures++; return new FrameMessage { Position = new Vector3(1000, 30, 20), Velocity = new Vector3(200, 0, 0) }; };
        Check(relay.Receive(1013, new object(), context, request, 1.1, (m, p) => true, capture).Count == 0 && captures == 0, "Unauthenticated baseline request reached capture.");
        Check(relay.Receive(1013, connection, context, request, 1.2, (m, p) => false, capture).Count == 0 && captures == 0, "Foreign ship request reached capture.");
        FrameMessage result = relay.Receive(1013, connection, context, request, 1.3, (m, p) => true, capture).Single().Message;
        Check(result.Actor == 1013 && result.Sequence == 3 && result.Generation == 9 && result.Kind == FrameMessageKind.ShipPose, "Baseline identity/correlation lost.");
        Near(result.Position, new Vector3(1000, 30, 20));
        Check(relay.Accepted == 0, "Baseline request changed passenger state.");
        Check(FrameProtocol.TryDecode(FrameProtocol.Encode(result), out var decoded) && decoded.Kind == FrameMessageKind.ShipPose, "Pose response codec failed.");
        Check(!FrameProtocol.TryDecode(FrameProtocol.Encode(result).Replace(FrameProtocol.Prefix, "ShipWalk/frame/2:"), out _), "Old incompatible wire version accepted.");
        result.Kind = FrameMessageKind.State;
        // Docking protocol 4 permits inherited frame velocity on passenger
        // states. The worker still applies ordinary walking distance checks
        // outside a confirmed root-change window (DockingTests).
        Check(FrameProtocol.TryDecode(FrameProtocol.Encode(result), out _), "Rebased passenger velocity was refused.");
        result.Velocity = new Vector3(301, 0, 0);
        bool rejected = false; try { FrameProtocol.Encode(result); } catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected, "Passenger velocity exceeded the finite transport bound.");
    }
}
