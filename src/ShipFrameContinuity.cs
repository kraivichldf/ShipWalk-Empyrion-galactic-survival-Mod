using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShipWalk
{
    internal enum ShipPoseState { Live, Predicting, Holding, Confirmed, Expired }

    // This owns presentation continuity, never passenger membership or physics.
    // Only authenticated server replies can establish a discontinuous baseline.
    internal sealed class ShipFrameContinuity
    {
        public const float RecoverySeconds = 3f;
        public LocalFramePose Pose { get; private set; }
        public Vector3 Velocity { get; private set; }
        public Vector3 Angular { get; private set; }
        public ShipPoseState State { get; private set; }
        public uint Epoch { get; private set; }
        public bool AwaitingHandoff { get; private set; }
        private readonly Dictionary<uint, Tuple<float, ShipPosePurpose>> requests = new Dictionary<uint, Tuple<float, ShipPosePurpose>>();
        public ShipPosePurpose RequestPurpose => AwaitingHandoff ? ShipPosePurpose.NativeOwner : ShipPosePurpose.CurrentVessel;
        private LocalFramePose reference;
        private Vector3 referenceVelocity, referenceAngular;
        private float sampledAt, replyAt = -100, recoveryAt = -1;
        private long referenceTick;
        private uint replySequence;
        private int confirmations;
        private bool referenceControl;
        private bool handoffPresentation;
        private LocalFramePose departurePose;
        private Vector3 departureVelocity;
        private float departedAt;
        internal const float PredictionSeconds = .3f;
        public bool NeedsReference(float now) => AwaitingHandoff || State == ShipPoseState.Holding
            || referenceControl && now - replyAt >= .15f;

        public void Seed(LocalFramePose pose, Vector3 velocity, float now, Vector3 angular = default)
        {
            if (!pose.Valid || !RemoteFrameMath.ValidVelocity(velocity) || !MotionMath.Finite(now) || !MotionMath.Finite(angular))
                throw new ArgumentException("Invalid ship presentation baseline.");
            Pose = pose; Velocity = velocity; Angular = angular; sampledAt = now;
            State = ShipPoseState.Live; recoveryAt = -1; referenceControl = AwaitingHandoff = handoffPresentation = false;
            NewEpoch();
        }
        private void NewEpoch()
        {
            Epoch++; if (Epoch == 0) Epoch = 1;
            requests.Clear(); confirmations = 0; replySequence = 0; referenceTick = 0; replyAt = -100;
        }
        public void BeginHandoff()
        {
            NewEpoch(); AwaitingHandoff = true;
            handoffPresentation = true; departurePose = Pose; departureVelocity = Velocity; departedAt = sampledAt;
        }
        public void TrackRequest(uint sequence, float now)
        {
            // Bounded even while a peer never answers.
            var expired = new List<uint>();
            foreach (var item in requests) if (now - item.Value.Item1 > 2f) expired.Add(item.Key);
            foreach (uint id in expired) requests.Remove(id);
            if (requests.Count < 16 && sequence != 0) requests[sequence] = Tuple.Create(now, RequestPurpose);
        }
        public bool Confirm(FrameMessage message, float now)
        {
            if (message.Generation != Epoch || message.Sequence <= replySequence
                || !requests.TryGetValue(message.Sequence, out var request) || message.Purpose != request.Item2
                || AwaitingHandoff && message.Purpose != ShipPosePurpose.NativeOwner) return false;
            float sent = request.Item1;
            requests.Remove(message.Sequence);
            var candidate = new LocalFramePose(message.Position, message.Rotation);
            if (!candidate.Valid || !RemoteFrameMath.ValidVelocity(message.Velocity)
                || !MotionMath.Finite(now) || now < sent || now - sent > 2f || message.Tick <= referenceTick) return false;
            float seconds = (message.Tick - referenceTick) / 1000f;
            bool pair = confirmations > 0 && seconds > 0 && seconds <= 1f
                && Continuous(reference, candidate, seconds);
            referenceAngular = pair ? AngularVelocity(reference.Rotation, candidate.Rotation, seconds) : Vector3.Zero;
            confirmations = pair ? Math.Min(2, confirmations + 1) : 1;
            reference = candidate; referenceVelocity = message.Velocity;
            referenceTick = message.Tick; replyAt = now; replySequence = message.Sequence;
            if (confirmations == 2) AwaitingHandoff = false;
            return true;
        }
        public ShipPoseState Resolve(LocalFramePose displayed, float now, Vector3 nativeVelocity)
        {
            if (!Pose.Valid || !MotionMath.Finite(now) || now < sampledAt) return State = ShipPoseState.Expired;
            float dt = now - sampledAt;
            bool continuous = displayed.Valid && Continuous(Pose, displayed, Math.Max(.05f, dt));
            bool confirmed = confirmations == 2 && now >= replyAt && now - replyAt <= .75f;
            Vector3 expectedVelocity = confirmed ? referenceVelocity : departureVelocity;
            bool historyReady = !handoffPresentation || RemoteFrameMath.ValidVelocity(nativeVelocity)
                && Vector3.Distance(nativeVelocity, expectedVelocity) <= Math.Max(2f, expectedVelocity.Length() * .2f);
            LocalFramePose selected;
            if (continuous && historyReady && (recoveryAt < 0 || !AwaitingHandoff))
            {
                selected = displayed;
                referenceControl = false; recoveryAt = -1; State = ShipPoseState.Live;
                if (!AwaitingHandoff) handoffPresentation = false;
                if (RemoteFrameMath.ValidVelocity(nativeVelocity)) Velocity = nativeVelocity;
                if (dt > .0001f) Angular = AngularVelocity(Pose.Rotation, selected.Rotation, dt);
            }
            else if (confirmed)
            {
                // Server sample is absolute. Predict only a small bounded interval;
                // never add a scene origin twice or use a parked Rigidbody pose.
                selected = new LocalFramePose(reference.Position + referenceVelocity * Math.Min(.1f, now - replyAt), reference.Rotation);
                Velocity = referenceVelocity; Angular = referenceAngular;
                referenceControl = true; recoveryAt = -1; State = ShipPoseState.Confirmed;
            }
            else
            {
                if (recoveryAt < 0) { recoveryAt = now; if (!AwaitingHandoff) NewEpoch(); }
                if (handoffPresentation && AwaitingHandoff && continuous && now - departedAt <= PredictionSeconds)
                {
                    // A short client history reset is not a physical stop. Carry
                    // the complete displayed ship through the ownership gap,
                    // then use confirmed server motion (including real impacts).
                    selected = new LocalFramePose(departurePose.Position + departureVelocity * (now - departedAt), departurePose.Rotation);
                    Velocity = departureVelocity; State = ShipPoseState.Predicting;
                }
                else
                {
                    // Hold the complete visual frame, including the ship interior.
                    // Local character simulation and membership continue independently.
                    selected = Pose; State = now - recoveryAt >= RecoverySeconds ? ShipPoseState.Expired : ShipPoseState.Holding;
                }
            }
            Pose = selected; sampledAt = now;
            return State;
        }
        private static bool Continuous(LocalFramePose from, LocalFramePose to, float seconds)
        {
            float window = Math.Min(.25f, seconds);
            return from.Valid && to.Valid && Vector3.Distance(from.Position, to.Position) <= 300f * window + .25f
                && Angle(from.Rotation, to.Rotation) <= 6f * window + .02f;
        }
        private static float Angle(Quaternion a, Quaternion b)
            => 2f * (float)Math.Acos(Math.Min(1f, Math.Abs(Quaternion.Dot(a, b))));
        internal static Vector3 AngularVelocity(Quaternion from, Quaternion to, float seconds)
        {
            if (seconds <= .0001f) return Vector3.Zero;
            Quaternion delta = Quaternion.Normalize(to * Quaternion.Inverse(from));
            if (delta.W < 0) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
            Vector3 axis = new Vector3(delta.X, delta.Y, delta.Z);
            float length = axis.Length();
            if (length < .00001f) return Vector3.Zero;
            float rate = Math.Min(6f, 2f * (float)Math.Atan2(length, delta.W) / seconds);
            return axis / length * rate;
        }
        public Vector3 WorldVelocity(Vector3 localPoint, Vector3 localVelocity)
            => Velocity + Vector3.Cross(Angular, Vector3.Transform(localPoint, Pose.Rotation))
                + Vector3.Transform(localVelocity, Pose.Rotation);
    }

    internal static class ShipPosePolicy
    {
        // Owner handoff requires a locally simulated unpiloted vessel. Recovery
        // may use the worker's replicated pilot-owned vessel, without changing
        // who controls it. Unknown/transitioning remote ownership stays refused.
        public static bool CanConfirm(ShipPosePurpose purpose, bool remote, bool piloted)
            => purpose == ShipPosePurpose.NativeOwner ? !remote && !piloted
                : purpose == ShipPosePurpose.CurrentVessel && (!remote || piloted);
    }

    internal static class PassengerLocalMotion
    {
        // Ship acceleration/history resets are not character input. While aboard,
        // the local solver's accepted velocity remains the source of truth.
        public static Vector3 Velocity(Vector3 acceptedLocal) => acceptedLocal;
        public static Quaternion ConsumeLook(Quaternion local, Quaternion lastPublishedWorld, Quaternion nativeWorld)
            => Quaternion.Normalize(local * Quaternion.Inverse(lastPublishedWorld) * nativeWorld);
    }
}
