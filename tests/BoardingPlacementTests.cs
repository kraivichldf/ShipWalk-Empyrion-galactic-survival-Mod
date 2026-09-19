using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ShipWalk;

internal static class BoardingPlacementTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(Vector3 a, Vector3 b) => Check(Vector3.Distance(a, b) < .001f, "Placement point changed: " + a + " vs " + b);

    internal static void ProposedPoseClearance()
    {
        // The old loop could query a Transform still at the seat point after a
        // Rigidbody position write. Resolve a floor, then a seat side, using
        // the proposed pose for each successive query, without a physics step.
        var visited = new List<Vector3>();
        Check(CapsuleClearance.Resolve(Vector3.Zero, point =>
        {
            visited.Add(point);
            if (point.Y < .2f) return new Vector3(0, .205f - point.Y, 0);
            if (point.X < .15f) return new Vector3(.155f - point.X, 0, 0);
            return Vector3.Zero;
        }, out Vector3 solved), "A valid exit position was rejected without a world physics step.");
        Near(solved, new Vector3(.155f, .205f, 0));
        Check(visited.Count == 3 && visited.Distinct().Count() == 3, "Clearance reused a stale capsule pose.");
    }
    internal static void ClearanceBounds()
    {
        int calls = 0;
        Check(CapsuleClearance.Resolve(Vector3.Zero, p => ++calls <= 8 ? new Vector3(.02f, 0, 0) : Vector3.Zero, out _),
            "A capsule cleared by the last permitted shift was rejected without checking its final pose.");
        Check(calls == 9, "Last shifted position was not checked.");
        Check(!CapsuleClearance.Resolve(Vector3.Zero, p => new Vector3(1, 0, 0), out _), "Correction crossed a metre-thick obstacle.");
        calls = 0;
        Check(!CapsuleClearance.Resolve(Vector3.Zero, p => { calls++; return p.X > 0 ? new Vector3(-.1f, 0, 0) : new Vector3(.1f, 0, 0); }, out _)
            && calls == 9, "Conflicting collider corrections looped indefinitely.");
        Check(!CapsuleClearance.Resolve(Vector3.Zero, p => new Vector3(float.NaN, 0, 0), out _), "Invalid collision result accepted.");
    }
    internal static void RecordedSeatAndNativeExit()
    {
        var seat = new Vector3(4.820f, 5.064f, -14);
        var native = new Vector3(3.818f, 5.577f, -13.988f);
        var placement = new BoardingPlacement();
        placement.Begin(seat, native, Quaternion.Identity, true, 1);
        Check(placement.FromSeat, "Seat departure lost its hold intent.");
        Check(placement.TryCandidate(1, out Vector3 point, out bool support) && !support, "Native exit was not tried first.");
        Near(point, native);
        Check(placement.TryCandidate(1, out point, out support) && !support, "Seat anchor fallback missing."); Near(point, seat);
        Check(!BoardingPlacement.AcceptNative(seat, native + Vector3.UnitZ * 100), "Stale world placement could teleport the player away.");
        placement.Reset(); placement.Begin(seat, native + Vector3.UnitZ * 100, Quaternion.Identity, true, 2);
        Near(placement.Preferred, seat);
    }
    internal static void FailedRoundThenRecovery()
    {
        var placement = new BoardingPlacement(); placement.Begin(Vector3.Zero, null, Quaternion.Identity, true, 10);
        int tested = 0;
        while (placement.TryCandidate(10, out Vector3 point, out bool support))
        {
            Check(MotionMath.Finite(point) && point.Length() < 1, "Nearby search escaped its local radius.");
            if (tested >= 2) Check(support, "An alternate destination did not require a supporting floor.");
            tested++;
        }
        Check(tested == BoardingPlacement.Candidates && placement.Pending, "Initial failure discarded the transaction.");
        Check(!placement.TryCandidate(10.05f, out _, out _), "Retry cooldown ignored.");
        Check(placement.TryCandidate(10.11f, out Vector3 retry, out _), "Settled collision geometry could not be retried.");
        Check(CapsuleClearance.Resolve(retry, p => Vector3.Zero, out _), "Now-clear original position still failed.");
        placement.Reset(); Check(!placement.Pending && !placement.TryCandidate(10.12f, out _, out _), "Completed placement repeated.");
    }
    internal static void DuplicateExpiryAndCancellation()
    {
        var placement = new BoardingPlacement(); placement.Begin(Vector3.Zero, Vector3.UnitX, Quaternion.Identity, true, 10);
        placement.Begin(Vector3.One * 100, null, Quaternion.Identity, false, 11.9f);
        Near(placement.Anchor, Vector3.Zero); Near(placement.Preferred, Vector3.UnitX);
        Check(placement.FromSeat && placement.Expired(12) && !placement.TryCandidate(12, out _, out _), "Duplicate acknowledgement extended the hold.");
        placement.Reset(); Check(!placement.Pending && !placement.Expired(20), "Cancellation left a hold active.");
        placement.Begin(Vector3.One, null, Quaternion.Identity, false, 20);
        placement.TrackWalkingPoint(new Vector3(2, 1, 1)); Near(placement.Preferred, new Vector3(2, 1, 1));
        Check(placement.Expired(22), "Walking during clearance extended the deadline.");
    }
    internal static void OneSlicePerPhysicsStep()
    {
        var placement = new BoardingPlacement(); placement.Begin(Vector3.Zero, null, Quaternion.Identity, true, 10);
        Check(placement.TryStep(10), "First placement slice was blocked.");
        // Every native controller can invoke the same prefix in one physics
        // timestamp; distant players/NPCs must not multiply the search budget.
        for (int controller = 0; controller < 100; controller++) Check(!placement.TryStep(10), "Another controller repeated the placement work.");
        Check(placement.TryStep(10.02f) && !placement.TryStep(9), "Placement step ordering failed.");
        placement.Reset(); placement.Begin(Vector3.Zero, null, Quaternion.Identity, true, 10.03f);
        Check(placement.TryStep(10.02f), "A new session inherited the old search throttle.");
    }
    internal static void MovingCarrierAndRootChange()
    {
        var sv = new LocalFramePose(new Vector3(200, 100, -20), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f));
        var cv = new LocalFramePose(new Vector3(150, 90, -50), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.4f));
        var placement = new BoardingPlacement(); placement.Begin(new Vector3(1, 2, 3), new Vector3(2, 2, 3), Quaternion.Identity, true, 0);
        Vector3 before = sv.ToWorldPoint(placement.Preferred);
        placement.Reframe(sv, cv); Near(cv.ToWorldPoint(placement.Preferred), before);
        var moved = new LocalFramePose(cv.Position + new Vector3(100, 0, 0), cv.Rotation);
        Near(moved.ToWorldPoint(placement.Preferred), before + new Vector3(100, 0, 0));
        Check(placement.Expired(2), "Docking root change renewed placement indefinitely.");
    }
    internal static void RetryRequiresMovementAndIsBounded()
    {
        var control = new LocalFrameControl(); control.Enable();
        Check(control.ObserveContext(54067, false, false), "Initial boarding missing.");
        control.PlacementFailed(54067, Vector3.Zero, 1);
        Check(!control.ObserveContext(54067, false, false, 1.1f, Vector3.One), "Retry ignored cooldown.");
        Check(!control.ObserveContext(54067, false, false, 2, new Vector3(.01f, 0, 0)), "Stationary overlap triggered a full rebuild.");
        Check(control.ObserveContext(54067, false, false, 2, Vector3.UnitX), "Walking to a different spot did not recover the same boarding context.");
        control.PlacementFailed(54067, Vector3.UnitX, 4);
        Check(!control.ObserveContext(54067, false, true, 5, Vector3.One), "Existing preparation was restarted.");
        Check(control.ObserveContext(54067, false, false, 5, Vector3.One), "Second bounded retry missing.");
        control.PlacementFailed(54067, Vector3.One, 6);
        for (int i = 0; i < 300; i++) Check(!control.ObserveContext(54067, false, false, 7, new Vector3(i, 0, 0)), "Retry budget was renewable.");
    }
    internal static void RetryContextIsolation()
    {
        var control = new LocalFrameControl(); control.Enable(); control.ObserveContext(54067, false, false);
        control.PlacementFailed(54067, Vector3.Zero, 1);
        Check(!control.ObserveContext(54067, false, false, 12, Vector3.One), "Expired retry was revived.");
        control.Disable(); Check(!control.ObserveContext(54067, false, false, 2, Vector3.One), "Off allowed recovery.");
        control.Enable(); control.ObserveContext(54067, false, false); control.PlacementFailed(54067, Vector3.Zero, 20);
        Check(control.ObserveContext(8715, false, false, 21, Vector3.One), "A new vessel could not board.");
        Check(!control.ObserveContext(8715, false, false, 22, Vector3.Zero), "Old carrier retry leaked into another vessel.");
        control.ResetSession(true); Check(!control.ObserveContext(54067, false, false, 21, Vector3.One), "Old recovery crossed a login boundary.");
    }
    internal static void PendingMembershipAndFloorHandover()
    {
        var relay = new FrameRelay(); object connection = new object(), observer = new object(), context = new object();
        Guid client = Guid.NewGuid();
        Func<FrameMessage, FrameMessage, bool> validate = (message, old) => message.Ship == 54067 && (message.Member == 8715 || message.Member == 54067);
        relay.Receive(1, connection, context, new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = client }, 0, validate);
        relay.Receive(2, observer, context, new FrameMessage { Kind = FrameMessageKind.Hello, ClientSession = Guid.NewGuid() }, 0, validate);
        var state = new FrameMessage { Kind = FrameMessageKind.Publish, ClientSession = client, ServerSession = relay.Session,
            Ship = 54067, Member = 8715, Sequence = 1, Generation = 1, Mode = PassengerMode.Seated };
        relay.Receive(1, connection, context, state, .1, validate);
        for (int i = 1; i <= 30; i++)
        {
            state.Mode = PassengerMode.Jumping; state.Sequence++;
            Check(relay.Receive(1, connection, context, state, .1 + i * .05, validate).Any(d => d.Connection == observer), "Observer lost the pending passenger.");
        }
        Check(relay.Aboard(54067, context, 1.7).Length == 1, "Server roster lost the carrier attachment while placing.");
        state.Member = DockingTopology.Occupant(8715, 54067, -1, true, 54067);
        state.Mode = PassengerMode.Walking; state.Sequence++;
        var applied = relay.Receive(1, connection, context, state, 1.8, validate).Single(d => d.Connection == observer).Message;
        Check(applied.Member == 54067 && applied.Mode == PassengerMode.Walking, "CV floor support did not reach the observer.");
    }
    internal static void ControllerAndCleanupIntegration()
    {
        bool Calls(Type type, string method, string target)
            => PatchProcessor.GetOriginalInstructions(type.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Any(i => i.operand is MethodInfo called && called.Name == target);
        Check(Calls(typeof(Runtime), "RunNativeController", "OwnsPlacement"), "Native fixed controller can move a held character.");
        Check(Calls(typeof(Hooks), "LimiterPrefix", "OwnsPlacement"), "Native limiter can move a held character.");
        Check(Calls(typeof(Runtime), "Disable", "OwnsPlacement"), "Controller removal leaks the placement lease.");
        Check(Calls(typeof(LocalFrameSession), "Disarm", "ReleasePlacement"), "Shutdown/off/death cleanup misses placement.");
        Check(Calls(typeof(LocalFrameSession), "BeginSeatOperation", "ReleasePlacement"), "A new native seat callback starts with held body flags.");
        Check(Calls(typeof(LocalFrameSession), "UpdateDocking", "Reframe"), "Docking does not transform pending coordinates.");
        Check(Calls(typeof(LocalGeometry), "ClearExit", "Resolve"), "Actual clearance does not use the tested proposed-pose solver.");
    }
}
