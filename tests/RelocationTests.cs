using System;
using System.Linq;
using System.Numerics;
using ShipWalk;

internal static class RelocationTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void AuthorityPurposes()
    {
        Check(!ShipPosePolicy.CanConfirm(ShipPosePurpose.NativeOwner, true, true), "Pilot history falsely confirmed an owner handoff.");
        Check(!ShipPosePolicy.CanConfirm(ShipPosePurpose.NativeOwner, false, true), "Occupied pilot seat confirmed release.");
        Check(!ShipPosePolicy.CanConfirm(ShipPosePurpose.NativeOwner, true, false), "Remote body confirmed local ownership.");
        Check(ShipPosePolicy.CanConfirm(ShipPosePurpose.NativeOwner, false, false), "Unpiloted native handoff rejected.");
        Check(ShipPosePolicy.CanConfirm(ShipPosePurpose.CurrentVessel, true, true), "Piloted relocation still cannot recover.");
        Check(!ShipPosePolicy.CanConfirm(ShipPosePurpose.CurrentVessel, true, false), "Unknown remote owner accepted.");
        Check(!ShipPosePolicy.CanConfirm((ShipPosePurpose)99, false, false), "Unknown purpose accepted.");
    }
    public static void RecordedGasPlanetRecovery()
    {
        Vector3 oldPoint = new Vector3(1345.05f, -76.76f, 4246.43f), newPoint = new Vector3(1467.74f, -83.76f, 4633.77f);
        var frame = new ShipFrameContinuity(); frame.Seed(new LocalFramePose(oldPoint, Quaternion.Identity), Vector3.Zero, 10);
        var moved = new LocalFramePose(newPoint, Quaternion.Identity);
        Check(frame.Resolve(moved, 10.025f, Vector3.Zero) == ShipPoseState.Holding, "Recorded native correction did not enter confirmation.");
        Check(frame.RequestPurpose == ShipPosePurpose.CurrentVessel, "Recovery requests unpiloted ownership.");
        var relay = new FrameRelay(); object connection = new object(), context = new object(); Guid client = Guid.NewGuid();
        relay.Receive(1004, connection, context, new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = client }, 10, (_, __) => true);
        Vector3 local = new Vector3(12.718f, 14.050f, 33.959f);
        for (uint i = 1; i <= 2; i++)
        {
            float now = 10.03f + (i - 1) * .15f; frame.TrackRequest(i, now);
            var request = new FrameMessage { Kind = FrameMessageKind.ShipPoseRequest, ClientSession = client, ServerSession = relay.Session,
                Sequence = i, Generation = frame.Epoch, Ship = 1019, Position = local, Mode = PassengerMode.Walking, Purpose = frame.RequestPurpose };
            Check(FrameProtocol.TryDecode(FrameProtocol.Encode(request), out var decoded), "Recovery request codec failed.");
            var replies = relay.Receive(1004, connection, context, decoded, now + .01, (_, __) => true,
                r => ShipPosePolicy.CanConfirm(r.Purpose, true, true) ? new FrameMessage { Position = newPoint } : null);
            Check(replies.Count == 1, "Piloted vessel failed to answer authenticated recovery.");
            Check(FrameProtocol.TryDecode(FrameProtocol.Encode(replies.Single().Message), out var reply) && frame.Confirm(reply, now + .02f), "Recovery reply lost request purpose.");
            var state = frame.Resolve(moved, now + .03f, Vector3.Zero);
            Check(state == (i == 1 ? ShipPoseState.Holding : ShipPoseState.Confirmed), "Recovery did not require two consistent server samples.");
        }
        Check(Vector3.Distance(frame.Pose.ToWorldPoint(local), moved.ToWorldPoint(local)) < .001f, "Standing passenger lost local pose during redirection.");
        Check(!frame.AwaitingHandoff, "Relocation pretended to change vessel ownership.");
    }
    public static void PurposeAndVersionGuards()
    {
        var frame = new ShipFrameContinuity(); frame.Seed(new LocalFramePose(Vector3.Zero, Quaternion.Identity), Vector3.Zero, 0); frame.BeginHandoff(); frame.TrackRequest(1, .01f);
        var reply = new FrameMessage { Kind = FrameMessageKind.ShipPose, ClientSession = Guid.NewGuid(), Generation = frame.Epoch,
            Sequence = 1, Tick = 20, Purpose = ShipPosePurpose.CurrentVessel };
        Check(!frame.Confirm(reply, .03f) && frame.AwaitingHandoff, "Recovery reply satisfied a real pilot-exit handoff.");
        reply.Purpose = ShipPosePurpose.NativeOwner;
        Check(frame.Confirm(reply, .03f), "Wrong-purpose packet consumed the valid request.");
        string wire = FrameProtocol.Encode(reply);
        Check(!FrameProtocol.TryDecode(wire.Replace(FrameProtocol.Prefix, "ShipWalk/frame/2:"), out _), "Old frame protocol silently mixed with recovery purposes.");
        byte[] bytes = Convert.FromBase64String(wire.Substring(FrameProtocol.Prefix.Length)); bytes[58] = 99;
        Check(!FrameProtocol.TryDecode(FrameProtocol.Prefix + Convert.ToBase64String(bytes), out _), "Unknown recovery purpose accepted.");
    }
}
