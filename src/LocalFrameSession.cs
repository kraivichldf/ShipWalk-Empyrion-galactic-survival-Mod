using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using Eleon.Modding;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using Object = UnityEngine.Object;

namespace ShipWalk
{
    internal sealed class LocalFrameSession
    {
        private readonly Runtime owner;
        private readonly IModApi api;
        private readonly Build5150 map;
        private readonly CharacterBodyLease lease = new CharacterBodyLease();
        private readonly CharacterBodyLease arrivalLease = new CharacterBodyLease();
        private readonly CharacterBodyLease placementLease = new CharacterBodyLease();
        private readonly BoardingPlacement placement = new BoardingPlacement();
        private Vector3? nativeExitPoint;
        private string placementFailure;
        private TravelMember arrivalMember;
        private readonly LocalPresentation presentation = new LocalPresentation();
        private readonly SeatOperationGate seatOperation = new SeatOperationGate();
        private readonly ShipFrameContinuity shipFrame = new ShipFrameContinuity();
        private readonly WorldCollisionHandoff worldCollision = new WorldCollisionHandoff();
        private NQuaternion localFacing, publishedFacing;
        private bool haveFacing;
        private bool resumeSeatFrame, resumeAcceptedProxy;
        private Vector3 resumeSeatPoint, resumeSeatVelocity;
        private Quaternion resumeSeatHeading;
        private LocalGeometry geometry;
        private Component controller;
        private object actor, ship, aboardShip;
        private object pendingDockRoot;
        private float pendingDockAt;
        private object sampledMember;
        private LocalFramePose memberSample;
        private float memberSampleAt;
        private Rigidbody body, shipBody;
        private CapsuleCollider nativeCapsule;
        private Transform hull, characterRoot;
        private IStructure structure;
        private LocalVolume shipBounds;
        private float nextBoundsRefresh;
        private LocalFramePose pose;
        private NQuaternion armedRotation;
        private NVector worldVelocity;
        private float lastStep = float.NegativeInfinity, nextReport, preparationStarted;
        private int steps, lookSamples;
        private double totalMs, maxMs;
        private bool grounded, enteredFromSeat, networkFrame;
        private bool jetpackFlying, climbing;
        private int contactBoundsQueries, contactPointQueries;
        private float contactPointGap;
        private Vector3 lastContactPoint;
        private float nextLightReport;
        private float shipPoseGap, maxRenderCorrection, nativeLocomotionDistance, localLocomotionDistance;
        private Vector3 lastInput;
        private FrameMessage lastNetworkState;
        public bool HasSession => geometry != null;
        public bool Preparing => geometry != null && !geometry.Ready;
        public bool Armed => geometry != null && geometry.Ready;
        public bool Active => lease.Held;
        public bool HoldingArrival => arrivalLease.Held;
        public bool HoldingPlacement => placementLease.Held;
        public bool HasNativeOverrides => Active || (geometry?.HasNativeOverrides ?? false);
        // Keep this session's pose source fixed even if the application changes
        // role while leaving a game. Eligibility releases it before reboarding.
        private bool NetworkFrame => networkFrame;
        public bool OwnsShipPhysics => !NetworkFrame && owner.OwnsShipPhysics;
        private Vector3 FrameVelocity => NetworkFrame ? (Vector3)map.EntityVelocity.GetValue(ship)
            : shipBody.gameObject.activeInHierarchy ? shipBody.velocity : Vector3.zero;
        public int ShipId => ship == null ? -1 : map.Id(ship);
        public int Shapes => geometry?.Count ?? 0;
        public string Status => "frame=" + (Active ? "Active" : Armed ? "Armed" : Preparing ? "Preparing" : "Off")
            + "; ship=" + ShipId + "; shapes=" + Shapes + "; steps=" + steps
            + "; aboard=" + (aboardShip == null ? -1 : map.Id(aboardShip)) + "; vessels=" + (geometry?.MemberCount ?? 0)
            + "; avgMs=" + (steps == 0 ? 0 : totalMs / steps).ToString("F3") + "; maxMs=" + maxMs.ToString("F3")
            + "; boundsSize=" + shipBounds.Size.ToString("F2")
            + "; poseSource=" + (NetworkFrame ? "network-display" : "local-physics")
            + (NetworkFrame ? "; shipPose=" + shipFrame.State + "; awaitingOwner=" + shipFrame.AwaitingHandoff : "")
            + "; seatTransaction=" + (seatOperation.Busy ? "Native" : seatOperation.CanActivate(Time.fixedTime) ? "Ready" : "NextPhysics")
            + "; exitPlacement=" + (placement.Pending ? "Pending" : "None")
            + (placementFailure == null ? "" : "; clearance=" + placementFailure)
            + (geometry == null ? "" : "; " + geometry.Metrics)
            + (Preparing ? "; progress=" + geometry.Progress : "");

        public LocalFrameSession(Runtime owner, IModApi api, Build5150 map)
        { this.owner = owner; this.api = api; this.map = map; }
        public bool Matches(object candidate) => Armed && ReferenceEquals(ship, candidate);
        public bool ContainsShip(object candidate) => Armed && DockingVessels.Member(map, ship, candidate);
        public bool NeedsBounding(object candidate) => OwnsShipPhysics && Matches(candidate) && shipBody != null
            && shipBody.gameObject.activeInHierarchy
            && (Active || owner.Momentum.Coasting.Contains(ship) || map.SeatedShip.GetValue(actor) == null);
        public bool Owns(Component candidate) => Active && controller == candidate;
        public bool OwnsArrival(Component candidate) => HoldingArrival && controller == candidate;
        public bool OwnsPlacement(Component candidate) => HoldingPlacement && controller == candidate;
        public bool OwnsActor(object candidate) => Active && ReferenceEquals(actor, candidate);
        public bool MatchesActor(object candidate) => Armed && ReferenceEquals(actor, candidate);
        public bool IsOwnHullRoomContact(Component room, Collider other)
            => Active && room != null && geometry.Members.Any(m => room.transform.IsChildOf((Transform)map.EntityTransform.GetValue(m)))
                && geometry.OwnsCollider(other);

        public void Arm(IEntity vessel, bool seated, TravelMember arrival = null)
        {
            if (geometry != null) return;
            bool candidateNetworkFrame = owner.MultiplayerClient;
            IPlayer player = api.Application.LocalPlayer;
            if (!owner.ClientMovement || api.Application.State != GameState.Running
                || player == null || !(player.Health > 0f) || vessel == null)
                throw new NotSupportedException("The local client character and its ship context must be available.");
            Component[] found = Object.FindObjectsOfType(map.ControllerType, true).OfType<Component>()
                .Where(c => map.ControllerEntity.GetValue(c) is object entity && map.Id(entity) == player.Id).ToArray();
            if (found.Length != 1) throw new NotSupportedException("Expected one local character controller; found " + found.Length + ".");
            Component candidate = found[0]; object candidateActor = map.ControllerEntity.GetValue(candidate);
            object boarded = map.NativeEntity(vessel);
            object candidateShip = DockingVessels.Root(map, boarded);
            if (candidateShip == null || !map.ShipType.IsInstanceOfType(candidateShip))
                throw new NotSupportedException("The current structure is not a native ship.");
            if (seated && (!ReferenceEquals(map.SeatedShip.GetValue(candidateActor), boarded)
                || player.DrivingEntity?.Id != vessel.Id))
                throw new NotSupportedException("Seat transition is not complete.");
            vessel = map.Entity(candidateShip);
            bool restoring = arrival != null && owner.Travel.Restoring && arrival.Actor == player.Id && arrival.Ship == vessel.Id
                && arrival.Mode != PassengerMode.Seated && TravelProtocol.MemberValid(arrival)
                && DockingVessels.Group(map, candidateShip, api.ClientPlayfield?.Entities.Values)
                    .Any(member => DockingVessels.Contains(map, candidateShip, member, arrival.Position, 3));
            if (arrival != null && !restoring) throw new NotSupportedException("Arrival ticket does not match the native player and ship.");
            if (!seated && (map.SeatedShip.GetValue(candidateActor) != null || !restoring
                && !DockingVessels.Member(map, candidateShip, map.NativeEntity(player.CurrentStructure?.Entity))))
                throw new NotSupportedException("The character's current ship changed before preparation.");
            var candidateBody = (Rigidbody)map.ControllerBody.GetValue(candidate);
            var candidateShipBody = (Rigidbody)map.EntityBody.GetValue(candidateShip);
            var candidateHull = (Transform)map.EntityTransform.GetValue(candidateShip);
            var candidateCharacterRoot = (Transform)map.EntityTransform.GetValue(candidateActor);
            if (candidateBody == null || candidateShipBody == null || candidateHull == null || candidateCharacterRoot == null
                || candidateBody.gameObject.scene != candidateShipBody.gameObject.scene)
                throw new NotSupportedException("Missing or cross-scene native bodies.");
            if (!ClientFramePolicy.CanPrepare(api.Application.Mode == ApplicationMode.SinglePlayer, candidateNetworkFrame,
                LocalFrameMath.IsVessel(map.EntityType.GetValue(candidateShip).ToString()), map.Bool(map.EntityRemote, candidateShip),
                !candidateShipBody.isKinematic || !candidateShipBody.gameObject.activeInHierarchy,
                map.DockedTo.GetValue(candidateShip) == null)
                || (!candidateNetworkFrame && candidateShipBody.gameObject.activeInHierarchy && candidateShipBody.angularVelocity.magnitude > .01f))
                throw new NotSupportedException("The local character needs an undocked CV/SV/HV in straight or stationary flight.");
            // The native controller already identifies its physics shape. A
            // layer-number search conflates camera/model shapes and seated state.
            Collider nativeShape = (Collider)map.ControllerCollider.GetValue(candidate);
            owner.Log.Info("LocalFrame preflight; ship=" + map.Id(candidateShip)
                + "; shipUp=" + (candidateShipBody.rotation * Vector3.up).ToString("F4")
                + "; boarding=" + (seated ? "seat" : "structure")
                + "; collider=" + Describe(nativeShape) + "; body=" + candidateBody.GetInstanceID());
            CapsuleCollider capsule = CheckCapsule(candidateBody, nativeShape);
            var candidatePose = candidateNetworkFrame || !candidateShipBody.gameObject.activeInHierarchy
                ? new LocalFramePose(N(candidateHull.position + map.OriginOffset), N(candidateHull.rotation))
                : new LocalFramePose(N(candidateShipBody.position + map.OriginOffset), N(candidateShipBody.rotation));
            if (!candidatePose.Valid)
                throw new NotSupportedException("The ship pose is invalid; local movement was not enabled.");
            // Preparation is incremental and never writes native collision state.
            IStructure candidateStructure = vessel.Structure;
            LocalVolume candidateBounds = ReadBounds(candidateStructure);
            LocalGeometry snapshot = new LocalGeometry(map, candidateShip, candidateHull, candidateBody, capsule,
                () => api.ClientPlayfield?.Entities.Values);
            geometry = snapshot; controller = candidate; actor = candidateActor; ship = candidateShip;
            aboardShip = restoring && arrival.Member > 0 && api.ClientPlayfield.Entities.TryGetValue(arrival.Member, out IEntity arrivedMember)
                ? map.NativeEntity(arrivedMember) : boarded;
            lastNetworkState = null;
            placement.Reset(); nativeExitPoint = null; placementFailure = null;
            networkFrame = candidateNetworkFrame;
            body = candidateBody; shipBody = candidateShipBody; hull = candidateHull; nativeCapsule = capsule;
            characterRoot = candidateCharacterRoot;
            structure = candidateStructure; shipBounds = candidateBounds; nextBoundsRefresh = Time.realtimeSinceStartup + .5f;
            enteredFromSeat = seated;
            haveFacing = false;
            if (networkFrame) shipFrame.Seed(candidatePose,
                RemoteFrameMath.ValidVelocity(N(FrameVelocity)) ? N(FrameVelocity) : NVector.Zero, Time.realtimeSinceStartup);
            lastStep = float.NegativeInfinity; steps = lookSamples = 0; totalMs = maxMs = 0;
            shipPoseGap = maxRenderCorrection = nativeLocomotionDistance = localLocomotionDistance = 0f;
            lastInput = Vector3.zero;
            contactBoundsQueries = contactPointQueries = 0; contactPointGap = 0; lastContactPoint = Vector3.zero;
            preparationStarted = Time.realtimeSinceStartup; nextReport = preparationStarted + 1f;
            owner.Log.Info("LocalFrame preparing; " + Status
                + "; shapeLimit=none; budgetMs=" + IncrementalWork.MillisecondsPerSlice
                + ". Native movement remains active while preparing; off cancels.");
            if (restoring)
            {
                arrivalMember = arrival.Copy(); resumeSeatFrame = true;
                resumeSeatPoint = U(arrival.Position); resumeSeatVelocity = U(arrival.Velocity); resumeSeatHeading = U(arrival.Rotation);
                arrivalLease.Capture(body.isKinematic, body.detectCollisions, (int)body.interpolation, nativeCapsule.isTrigger);
                body.isKinematic = true; body.interpolation = RigidbodyInterpolation.None; nativeCapsule.isTrigger = true;
                HoldArrival();
            }
        }

        private void HoldArrival()
        {
            if (!HoldingArrival || body == null || hull == null || characterRoot == null) return;
            Vector3 point = hull.position + hull.rotation * U(arrivalMember.Position);
            Quaternion rotation = hull.rotation * U(arrivalMember.Rotation);
            body.position = point; body.rotation = rotation; characterRoot.SetPositionAndRotation(point, rotation);
            map.SetEntityPosition.Invoke(actor, new object[] { point + map.OriginOffset, false });
        }
        private void ReleaseArrival()
        {
            if (!arrivalLease.Release()) return;
            if (body != null)
            {
                if (body.isKinematic) body.isKinematic = arrivalLease.Kinematic;
                if (body.interpolation == RigidbodyInterpolation.None) body.interpolation = (RigidbodyInterpolation)arrivalLease.Interpolation;
                if (nativeCapsule != null && nativeCapsule.isTrigger) nativeCapsule.isTrigger = arrivalLease.Trigger;
            }
            arrivalMember = null;
        }
        private void HoldPlacement()
        {
            if (!HoldingPlacement || !placement.Pending || body == null || hull == null || characterRoot == null || seatOperation.Busy) return;
            if (!PlacementHoldIntact()) return;
            Vector3 point = hull.position + hull.rotation * U(placement.Preferred);
            body.position = point; body.transform.position = point; characterRoot.position = point;
            // Keep native look input available during the short placement hold.
            map.SetEntityPosition.Invoke(actor, new object[] { point + map.OriginOffset, false });
            LocalFramePose frame = NetworkFrame ? shipFrame.Pose : ReadPose();
            worldVelocity = DockingRebase.PointVelocity(frame, NetworkFrame ? shipFrame.Velocity : N(FrameVelocity),
                NetworkFrame ? shipFrame.Angular : N(DockingVessels.Angular(map, ship, false)), frame.ToWorldPoint(placement.Preferred));
        }
        private bool PlacementHoldIntact() => body != null && body.isKinematic && body.detectCollisions
            && nativeCapsule != null && nativeCapsule.isTrigger && body.gameObject.activeInHierarchy
            && controller is Behaviour behaviour && behaviour.isActiveAndEnabled && map.SeatedShip.GetValue(actor) == null;
        private void AcquirePlacement()
        {
            if (HoldingPlacement || !placement.FromSeat || HoldingArrival) return;
            placementLease.Capture(body.isKinematic, body.detectCollisions, (int)body.interpolation, nativeCapsule.isTrigger);
            body.isKinematic = true; body.detectCollisions = true;
            body.interpolation = RigidbodyInterpolation.None; nativeCapsule.isTrigger = true;
            HoldPlacement();
        }
        private void ReleasePlacement(bool inherit)
        {
            if (!placementLease.Release() || body == null) return;
            if (body.isKinematic) body.isKinematic = placementLease.Kinematic;
            if (body.detectCollisions) body.detectCollisions = placementLease.DetectCollisions;
            if (body.interpolation == RigidbodyInterpolation.None) body.interpolation = (RigidbodyInterpolation)placementLease.Interpolation;
            if (nativeCapsule != null && nativeCapsule.isTrigger) nativeCapsule.isTrigger = placementLease.Trigger;
            if (inherit && !body.isKinematic && MotionMath.Finite(worldVelocity)) body.velocity = U(worldVelocity);
        }
        private void PlacementExpired()
        {
            owner.RetryBoardingAfterPlacement(ShipId, placement.Preferred, Time.realtimeSinceStartup);
            owner.Log.Error("Boarding placement timed out; ship=" + ShipId + "; first=" + placementFailure
                + "; last=" + geometry?.ClearanceStatus);
            owner.Log.Info("LocalFrame placement expired; ship=" + ShipId + "; " + placementFailure);
            owner.StopFrame("boarding placement timed out", true);
        }
        internal void AcceptNativeTeleport(Vector3 point, Quaternion rotation)
        {
            if (!NetworkFrame || !Armed || hull == null) return;
            var confirmed = new LocalFramePose(N(point), N(rotation));
            if (!confirmed.Valid) return;
            shipFrame.Seed(confirmed, RemoteFrameMath.ValidVelocity(N(FrameVelocity)) ? N(FrameVelocity) : NVector.Zero, Time.realtimeSinceStartup);
            pose = confirmed; hull.SetPositionAndRotation(point - map.OriginOffset, rotation);
            if (Active) { PublishPhysics(); PublishRender(); }
            owner.Log.Info("LocalFrame accepted native MicroWarp completion; ship=" + ShipId + "; local pose retained.");
        }

        // A parked ship's native bounding Rigidbody is inactive. Its live hull
        // transform is authoritative then; do not wake it just to walk aboard.
        private LocalFramePose ReadPose() => !NetworkFrame && shipBody.gameObject.activeInHierarchy
            ? new LocalFramePose(N(shipBody.position + map.OriginOffset), N(shipBody.rotation))
            : new LocalFramePose(N(hull.position + map.OriginOffset), N(hull.rotation));

        public void AfterShipPresentation()
        {
            HoldArrival();
            if (!Armed || hull == null) return;
            if (NetworkFrame)
            {
                LocalFramePose observed = ReadPose();
                float now = Time.realtimeSinceStartup;
                if (!Active && !seatOperation.Busy && map.SeatedShip.GetValue(actor) != null)
                {
                    // Normal seated/pilot presentation is owned by the game.
                    if (observed.Valid && RemoteFrameMath.ValidVelocity(N(FrameVelocity)))
                        shipFrame.Seed(observed, N(FrameVelocity), now);
                    haveFacing = false;
                    return;
                }
                if (!Active && !HoldingPlacement && !shipFrame.AwaitingHandoff) return;
                ShipPoseState before = shipFrame.State;
                ShipPoseState result = shipFrame.Resolve(observed, now, N(FrameVelocity));
                LocalFramePose accepted = shipFrame.Pose;
                // Apply the selected visual frame to the hull and all its native
                // children together. Never write the vessel Rigidbody or ownership.
                hull.SetPositionAndRotation(U(accepted.Position) - map.OriginOffset, U(accepted.Rotation));
                if (result != before)
                    owner.Log.Info("Ship frame presentation; ship=" + ShipId + "; state=" + result
                        + "; observed=" + observed.Position + "; accepted=" + accepted.Position
                        + "; native=" + map.EntityPosition.GetValue(ship) + "; body=" + (shipBody.position + map.OriginOffset)
                        + "; origin=" + map.OriginOffset + "; seat=" + seatOperation.Busy
                        + "; awaitingOwner=" + shipFrame.AwaitingHandoff + "; localState=retained.");
                if (result == ShipPoseState.Expired)
                {
                    owner.StopFrame("ship pose confirmation timed out", false);
                    owner.Tell("Ship pose recovery timed out. Check that client and playfield both use ShipWalk protocol " + FrameProtocol.Version + ".");
                    return;
                }
            }
            PublishRender();
        }

        internal FrameMessage ShipPoseRequest(float now)
        {
            if (!NetworkFrame || !Armed || !shipFrame.NeedsReference(now)) return null;
            FrameMessage request = NetworkState();
            if (request == null) return null;
            request.Kind = FrameMessageKind.ShipPoseRequest; request.Generation = shipFrame.Epoch; request.Purpose = shipFrame.RequestPurpose;
            return request;
        }
        internal void TrackShipPoseRequest(uint sequence, float now) => shipFrame.TrackRequest(sequence, now);
        internal bool ConfirmShipPose(FrameMessage message, float now)
            => NetworkFrame && Armed && message.Ship == ShipId && shipFrame.Confirm(message, now);

        private void ConsumeLocalLook()
        {
            if (!NetworkFrame || !haveFacing || body == null) return;
            NQuaternion native = N(body.rotation);
            if (!new LocalFramePose(NVector.Zero, native).Valid) return;
            localFacing = PassengerLocalMotion.ConsumeLook(localFacing, publishedFacing, native);
            publishedFacing = native;
        }

        public bool ApplyWalkingLook(Rigidbody target, Quaternion rotation)
        {
            if (!Active || !NetworkFrame || !haveFacing || body != target) return false;
            NQuaternion requested = N(rotation);
            if (!new LocalFramePose(NVector.Zero, requested).Valid) return false;
            // RbCtrlCharacter.Update has already calculated the native yaw delta
            // and will advance previousWindow after this call. MoveRotation would
            // queue it for world physics; our render publisher can overwrite that
            // request first. Commit it to the local facing at the producer instead.
            localFacing = PassengerLocalMotion.ConsumeLook(localFacing, publishedFacing, requested);
            publishedFacing = requested;
            body.rotation = rotation;
            body.transform.rotation = rotation;
            return true;
        }

        private bool TryBlockCoordinates(object space, object candidate, out LocalBlockCoordinates coordinates)
        {
            coordinates = default;
            if (!Active || !NetworkFrame || seatOperation.Busy || !ReferenceEquals(actor, candidate)
                || map.SeatedShip.GetValue(actor) != null || structure?.Entity == null) return false;
            object member = null;
            foreach (object vessel in geometry.Members)
                if (ReferenceEquals(space, map.ShipSpace.GetValue(vessel))) { member = vessel; break; }
            if (member == null) return false;
            var nativeShip = DockingVessels.Pose(map, member, true);
            var nativeGrid = new LocalFramePose(N((Vector3)map.GridPosition.GetValue(space)), N((Quaternion)map.GridRotation.GetValue(space)));
            coordinates = new LocalBlockCoordinates(nativeShip, nativeGrid, 4f / (int)map.GridDivisor.GetValue(space));
            if (!ReferenceEquals(member, ship))
            {
                var memberHull = (Transform)map.EntityTransform.GetValue(member);
                coordinates = coordinates.InParent(new LocalFramePose(N(Quaternion.Inverse(hull.rotation) * (memberHull.position - hull.position)),
                    N(Quaternion.Inverse(hull.rotation) * memberHull.rotation)));
            }
            return true;
        }
        public bool TryBlockContactBounds(object space, object candidate, out Bounds result)
        {
            result = default;
            if (!TryBlockCoordinates(space, candidate, out LocalBlockCoordinates coordinates)) return false;
            Bounds local = geometry.Capsule.bounds;
            coordinates.Bounds(N(local.min), N(local.max), out NVector min, out NVector max);
            result.SetMinMax(U(min), U(max));
            contactBoundsQueries++;
            return true;
        }
        public bool TryBlockContactPoint(object space, object candidate, Vector3 nativePoint, out Vector3 result)
        {
            result = default;
            if (!TryBlockCoordinates(space, candidate, out LocalBlockCoordinates coordinates)) return false;
            NVector point = coordinates.Point(N(geometry.Player.position));
            result = U(point); lastContactPoint = result;
            contactPointGap = NVector.Distance(coordinates.NativePoint(N(nativePoint)), point) * coordinates.Scale;
            contactPointQueries++;
            return true;
        }

        internal static LocalVolume ReadBounds(IStructure current)
        {
            if (current == null) throw new NotSupportedException("Ship structure dimensions are unavailable.");
            VectorInt3 min = current.MinPos, max = current.MaxPos;
            // Difference removes position/origin; the API supplies the game's
            // own block scale instead of guessing CV versus SV/HV dimensions.
            float cellSize = (current.StructToGlobalPos(new VectorInt3(1, 0, 0))
                - current.StructToGlobalPos(new VectorInt3(0, 0, 0))).magnitude;
            LocalVolume result = LocalVolume.FromGrid(new NVector(min.x, min.y, min.z), new NVector(max.x, max.y, max.z), cellSize);
            if (!result.Valid) throw new NotSupportedException("Ship structure dimensions are not available yet.");
            return result;
        }

        private bool WithinShip(Vector3 worldPosition)
        {
            NVector local = N(Quaternion.Inverse(hull.rotation) * (worldPosition - hull.position));
            return shipBounds.Contains(local, 2f) || geometry.Contains(local, 2f);
        }

        private static string Describe(Collider shape) => shape == null ? "missing"
            : shape.GetType().Name + "|name:" + shape.name + "|layer:" + shape.gameObject.layer
                + "|enabled:" + shape.enabled + "|active:" + shape.gameObject.activeInHierarchy + "|trigger:" + shape.isTrigger
                + "|physicsBody:" + BodyId(shape.attachedRigidbody)
                + "|hierarchyBody:" + BodyId(shape.GetComponentInParent<Rigidbody>(true));

        private static int BodyId(Rigidbody candidate) => candidate == null ? 0 : candidate.GetInstanceID();

        private static CapsuleCollider CheckCapsule(Rigidbody nativeBody, Collider shape)
        {
            if (!(shape is CapsuleCollider capsule))
                throw new NotSupportedException("Native character controller shape must be a capsule; found " + Describe(shape) + ".");
            Rigidbody hierarchyBody = capsule.GetComponentInParent<Rigidbody>(true);
            if (!NativeCapsuleBinding.BelongsTo(BodyId(nativeBody), BodyId(hierarchyBody), BodyId(capsule.attachedRigidbody)))
                throw new NotSupportedException("Controller capsule has inconsistent component ownership; expected body="
                    + BodyId(nativeBody) + "; " + Describe(shape) + ".");
            Vector3 axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 inBody = Quaternion.Inverse(nativeBody.transform.rotation) * (capsule.transform.rotation * axis);
            if (!LocalFrameMath.CapsuleAxisSupported(N(inBody)))
                throw new NotSupportedException("Native capsule is sideways relative to its character body; axis="
                    + inBody.ToString("F4") + "; direction=" + capsule.direction + ".");
            Vector3 scale = capsule.transform.lossyScale;
            if (!MotionMath.Finite(N(scale)) || scale.x <= 0f || scale.y <= 0f || scale.z <= 0f
                || !MotionMath.Finite(capsule.radius) || !MotionMath.Finite(capsule.height)
                || capsule.radius <= 0f || capsule.height <= 0f)
                throw new NotSupportedException("Native capsule dimensions or scale are invalid.");
            return capsule;
        }

        public void TryActivate()
        {
            try { Activate(); }
            catch (NotSupportedException error)
            {
                owner.StopFrame(error.Message);
                owner.Tell("Local frame exit refused: " + error.Message);
            }
        }

        private void Activate()
        {
            if (!Armed || Active || map.SeatedShip.GetValue(actor) != null) return;
            if (placement.Expired(Time.realtimeSinceStartup)) { PlacementExpired(); return; }
            if (!UpdateDocking()) return;
            if (!seatOperation.CanActivate(Time.fixedTime)) return;
            if (HoldingPlacement && !PlacementHoldIntact()) { owner.StopFrame("native controller reclaimed exit placement", false); return; }
            if (body == null || controller == null || !body.gameObject.activeInHierarchy
                || !(controller is Behaviour behaviour) || !behaviour.isActiveAndEnabled || body.isKinematic && !HoldingArrival && !HoldingPlacement) return;
            if (!Eligible()) { owner.StopFrame("unsupported character or ship state", false); return; }
            if (!LocalMovementAvailable()) return;
            if (!enteredFromSeat && !WithinShip(body.position))
            { owner.StopFrame("character left the ship before local movement started", false); return; }
            if (!NetworkFrame && shipBody.gameObject.activeInHierarchy && shipBody.angularVelocity.magnitude > .01f)
            { owner.StopFrame("ship is turning during boarding"); return; }
            Collider currentShape = (Collider)map.ControllerCollider.GetValue(controller);
            if (currentShape != nativeCapsule) { owner.StopFrame("native character capsule changed during exit", false); return; }
            CheckCapsule(body, currentShape);
            Stopwatch timer = Stopwatch.StartNew();
            if (OwnsShipPhysics) owner.Momentum.TakePlayerDeparture(actor, body);
            pose = NetworkFrame ? shipFrame.Pose : ReadPose();
            armedRotation = pose.Rotation;
            NVector frameVelocity = NetworkFrame ? shipFrame.Velocity : N(FrameVelocity);
            if (!RemoteFrameMath.ValidVelocity(frameVelocity)) throw new NotSupportedException("Ship movement sample is invalid.");
            Vector3 local = resumeSeatFrame ? resumeSeatPoint : Quaternion.Inverse(hull.rotation) * (body.position - hull.position);
            if (NetworkFrame && owner.Options.Trace) owner.Log.Info("Shared frame boarding pose; ship=" + ShipId
                + "; body=" + body.position.ToString("F3") + "; render=" + characterRoot.position.ToString("F3")
                + "; hull=" + hull.position.ToString("F3") + "; local=" + local.ToString("F3")
                + "; min=" + shipBounds.Min + "; max=" + shipBounds.Max);
            if (!LocalFrameMath.TryLocalHeading(pose.Rotation, N(body.rotation), out NQuaternion heading))
            { owner.StopFrame("invalid local heading", false); return; }
            Quaternion rotation = resumeSeatFrame ? resumeSeatHeading : U(heading);
            if (NetworkFrame)
            {
                localFacing = N(rotation); publishedFacing = N(body.rotation); haveFacing = true;
            }
            // Seated layers and capsule dimensions can change at native detach.
            // Refresh against the actual walking shape before enabling the proxy.
            bool resumed = resumeAcceptedProxy && resumeSeatFrame && geometry.ResumeAcceptedPlayer(body, nativeCapsule, local, rotation);
            if (!resumed)
            {
                bool pending = placement.Pending;
                if (!HoldingArrival)
                {
                    placement.Begin(N(local), nativeExitPoint.HasValue ? (NVector?)N(nativeExitPoint.Value) : null,
                        N(rotation), enteredFromSeat && resumeSeatFrame, Time.realtimeSinceStartup);
                    placement.TrackWalkingPoint(N(Quaternion.Inverse(hull.rotation) * (body.position - hull.position)));
                    if (!placement.TryStep(Time.fixedTime)) return;
                }
                // Reconfigure only when native seat detach actually changed the
                // capsule. Retrying must not rescan every hull collider per tick.
                if (!pending || !geometry.ResumeAcceptedPlayer(body, nativeCapsule, local, rotation))
                    geometry.ConfigurePlayer(body, nativeCapsule);
                bool? clear;
                if (HoldingArrival)
                    clear = geometry.ClearExit(local, rotation);
                else
                {
                    clear = geometry.ClearBoarding(placement, Time.realtimeSinceStartup);
                }
                if (!clear.HasValue)
                {
                    if (placement.Pending)
                    {
                        if (placementFailure == null)
                        {
                            placementFailure = geometry.ClearanceStatus;
                            owner.Log.Info("LocalFrame placement pending; ship=" + ShipId + "; " + placementFailure);
                        }
                        AcquirePlacement();
                    }
                    return;
                }
                if (!clear.Value)
                {
                    if (placement.Pending) PlacementExpired();
                    else owner.StopFrame("no safe local capsule clearance", false);
                    return;
                }
            }
            else owner.Log.Info("LocalFrame exit acknowledgement resumed accepted capsule; ship=" + ShipId);
            if (owner.Options.Trace) owner.Log.Info("LocalFrame lighting before attach; ship=" + ShipId + "; " + geometry.RoomStatus(body.position));
            ReleaseArrival();
            ReleasePlacement(false); placement.Reset(); nativeExitPoint = null; placementFailure = null;
            NVector boardingVelocity = N(body.velocity);
            // Complete overlap clearance before taking ownership of world physics.
            lease.Capture(body.isKinematic, body.detectCollisions, (int)body.interpolation, nativeCapsule.isTrigger);
            // The rendered character follows the ship's live visual pose. Its
            // independent world interpolation would introduce a second delay.
            body.interpolation = RigidbodyInterpolation.None;
            // The real player remains visible to native room/door triggers,
            // while the isolated capsule alone resolves solid contacts.
            body.isKinematic = true; nativeCapsule.isTrigger = true; body.detectCollisions = true;
            body.rotation = U(pose.Rotation) * rotation;
            if (NetworkFrame) publishedFacing = N(body.rotation);
            // Boarding on foot preserves the character's existing movement.
            // Seat departure inherits the ship velocity exactly once.
            worldVelocity = enteredFromSeat ? frameVelocity : boardingVelocity;
            if (resumeSeatFrame) worldVelocity = pose.ToWorldVelocity(N(resumeSeatVelocity), frameVelocity);
            geometry.Player.velocity = U(pose.ToLocalVelocity(worldVelocity, frameVelocity));
            resumeSeatFrame = resumeAcceptedProxy = false;
            grounded = geometry.Grounded();
            if (grounded && geometry.SupportEntity is object floorMember && DockingVessels.Member(map, ship, floorMember))
                aboardShip = floorMember;
            climbing = map.Bool(map.ActorClimbing, actor);
            jetpackFlying = !climbing && JetpackEnabled && (FreeLook || !grounded || map.Bool(map.Jetpack, controller));
            map.LookAccumulator.SetValue(controller, Vector3.zero);
            presentation.Reset(N(geometry.Player.position));
            SelectBounding(ship);
            PublishPhysics();
            PublishRender();
            owner.Log.Info("LocalFrame attached; " + Status + "; collidingShapes=" + geometry.ActiveShapes
                + "; attachMs=" + timer.Elapsed.TotalMilliseconds.ToString("F3")
                + "; nativeCapsule=" + Describe(nativeCapsule) + "; world character physics suspended.");
        }

        private bool Eligible()
        {
            IPlayer p = api.Application.LocalPlayer;
            return api.Application.State == GameState.Running
                && ClientFramePolicy.SameSessionMode(NetworkFrame, api.Application.Mode == ApplicationMode.SinglePlayer, owner.MultiplayerClient)
                && p != null && p.Id == map.Id(actor) && p.Health > 0f && controller != null && body != null
                && shipBody != null && hull != null && characterRoot != null
                && !map.Bool(map.EntityRemote, actor) && map.DockedTo.GetValue(ship) == null
                && (NetworkFrame || (!map.Bool(map.EntityRemote, ship)
                    && (!shipBody.isKinematic || !shipBody.gameObject.activeInHierarchy)))
                && ReferenceEquals(map.EntityBody.GetValue(ship), shipBody)
                && ReferenceEquals(map.ControllerBody.GetValue(controller), body)
                && body.gameObject.scene == shipBody.gameObject.scene && (!Active || body.gameObject.activeInHierarchy);
        }

        private bool JetpackEnabled => (bool)map.JetpackEnabled.Invoke(actor, null);
        private bool FreeLook => !(bool)map.PlanarLook.Invoke(actor, null);
        private bool LocalMovementAvailable() => !map.Bool(map.ActorSwimming, actor);

        public void ApplyLook(Vector3 sample)
        {
            // Native input and ordinary walking/planetary look already run.
            // Only free-flight turning was stranded in suspended FixedUpdate.
            map.LookAccumulator.SetValue(controller, Vector3.zero);
            if (!JetpackEnabled || !FreeLook || map.Bool(map.ActorClimbing, actor)) return;
            map.Jetpack.SetValue(controller, true);
            if (Cursor.lockState != CursorLockMode.Locked
                || !LocalLook.TryRotation(N(sample), Time.deltaTime, out NQuaternion delta)) return;
            Quaternion rotation = body.rotation * U(delta);
            body.rotation = rotation;
            body.transform.rotation = rotation;
            if (sample.sqrMagnitude > 0f) lookSamples++;
        }

        private bool UpdateDocking()
        {
            if (geometry == null || actor == null || owner.Travel.Suspended || HoldingArrival) return geometry != null;
            object seat = map.SeatedShip.GetValue(actor);
            object support = Active && grounded ? geometry.SupportEntity : null;
            int selected = DockingTopology.Occupant(aboardShip == null ? -1 : map.Id(aboardShip),
                support == null ? -1 : map.Id(support), seat == null ? -1 : map.Id(seat), true, ShipId);
            if (seat != null && map.Id(seat) == selected) aboardShip = seat;
            else if (support != null && map.Id(support) == selected) aboardShip = support;
            if (!DockingVessels.Valid(map, aboardShip)) aboardShip = ship;
            object next = DockingVessels.Root(map, aboardShip);
            if (next == null) { owner.StopFrame("docking group is unavailable"); return false; }
            if (ReferenceEquals(next, ship))
            {
                pendingDockRoot = null;
                if (Active && !grounded && !climbing && !ReferenceEquals(aboardShip, ship)
                    && !DockingVessels.Contains(map, ship, aboardShip, N(geometry.Player.position), 2f)) aboardShip = ship;
                sampledMember = aboardShip; memberSample = DockingVessels.Pose(map, aboardShip); memberSampleAt = Time.realtimeSinceStartup;
                return true;
            }
            if (!Armed) { owner.StopFrame("docking root changed during preparation", false); return false; }
            if (!ReferenceEquals(pendingDockRoot, next)) { pendingDockRoot = next; pendingDockAt = Time.realtimeSinceStartup; }
            if (Time.realtimeSinceStartup - pendingDockAt > 2f)
            { owner.StopFrame("docking target did not become available"); return false; }
            IEntity vessel = map.Entity(next);
            var nextHull = map.EntityTransform.GetValue(next) as Transform;
            var nextBody = map.EntityBody.GetValue(next) as Rigidbody;
            if (nextHull == null || nextBody == null || body == null || nextBody.gameObject.scene != body.gameObject.scene
                || vessel?.Structure?.IsReady != true) return false; // retain old local scene while native dock operation completes
            LocalFramePose before = NetworkFrame ? shipFrame.Pose : ReadPose();
            LocalFramePose after = DockingVessels.Pose(map, next);
            NVector oldLinear = NetworkFrame ? shipFrame.Velocity : N(FrameVelocity);
            NVector oldAngular = NetworkFrame ? shipFrame.Angular : N(DockingVessels.Angular(map, ship, false));
            NVector nextLinear = N(DockingVessels.Linear(map, next, NetworkFrame));
            NVector nextAngular = N(DockingVessels.Angular(map, next, NetworkFrame));
            float sampleAge = Time.realtimeSinceStartup - memberSampleAt;
            if (NetworkFrame && ReferenceEquals(sampledMember, next) && memberSample.Valid && sampleAge > .0001f && sampleAge <= .25f)
                nextAngular = ShipFrameContinuity.AngularVelocity(memberSample.Rotation, after.Rotation, sampleAge);
            else if (NetworkFrame && ReferenceEquals(aboardShip, next) && !ReferenceEquals(aboardShip, ship)) nextAngular = oldAngular;
            if (!before.Valid || !after.Valid || !RemoteFrameMath.ValidVelocity(nextLinear)) return false;
            DockingRebase rebased = DockingRebase.Change(before, after, N(geometry.Player.position),
                NetworkFrame && haveFacing ? localFacing : N(geometry.Player.rotation), N(geometry.Player.velocity),
                oldLinear, oldAngular, nextLinear, nextAngular);
            object previousShip = ship;
            geometry.Reframe(next, nextHull, before, after);
            ship = next; hull = nextHull; shipBody = nextBody; structure = vessel.Structure;
            shipBounds = ReadBounds(structure); pose = after; armedRotation = after.Rotation;
            if (NetworkFrame) shipFrame.Seed(after, nextLinear, Time.realtimeSinceStartup, nextAngular);
            geometry.Player.position = U(rebased.Position); geometry.Player.rotation = U(rebased.Rotation);
            geometry.Player.velocity = U(rebased.Velocity);
            worldVelocity = VectorTransformVelocity(after, rebased.Position, rebased.Velocity, nextLinear, nextAngular);
            if (haveFacing) { localFacing = rebased.Rotation; publishedFacing = N(body.rotation); }
            if (resumeSeatFrame)
            {
                DockingRebase resumed = DockingRebase.Change(before, after, N(resumeSeatPoint), N(resumeSeatHeading),
                    N(resumeSeatVelocity), oldLinear, oldAngular, nextLinear, nextAngular);
                resumeSeatPoint = U(resumed.Position); resumeSeatHeading = U(resumed.Rotation); resumeSeatVelocity = U(resumed.Velocity);
            }
            placement.Reframe(before, after);
            if (nativeExitPoint.HasValue) nativeExitPoint = U(after.ToLocalPoint(before.ToWorldPoint(N(nativeExitPoint.Value))));
            presentation.Reframe(before, after); lastNetworkState = null;
            pendingDockRoot = null;
            sampledMember = aboardShip; memberSample = DockingVessels.Pose(map, aboardShip); memberSampleAt = Time.realtimeSinceStartup;
            if (OwnsShipPhysics)
                map.ColliderSelection.Invoke(previousShip, new object[] { false, owner.Momentum.Coasting.Contains(previousShip) });
            SelectBounding(ship);
            if (Active) { geometry.SetRoomPresence(true); PublishPhysics(); PublishRender(); }
            owner.Log.Info("LocalFrame docking handover; from=" + map.Id(previousShip) + "; to=" + ShipId
                + "; aboard=" + map.Id(aboardShip) + "; seated=" + (seat != null) + "; local=" + rebased.Position
                + "; worldVelocity=" + worldVelocity + "; characterLease=" + Active + "; scene=reused.");
            return true;
        }
        private static NVector VectorTransformVelocity(LocalFramePose frame, NVector point, NVector velocity, NVector linear, NVector angular)
            => DockingRebase.PointVelocity(frame, linear, angular, frame.ToWorldPoint(point)) + NVector.Transform(velocity, frame.Rotation);

        public void Update()
        {
            if (geometry == null) return;
            if (!UpdateDocking()) return;
            HoldArrival();
            if (placement.Expired(Time.realtimeSinceStartup)) { PlacementExpired(); return; }
            if (!Eligible()) { owner.StopFrame("ownership or character state changed", false); return; }
            if (HoldingPlacement && !PlacementHoldIntact()) { owner.StopFrame("native controller reclaimed exit placement", false); return; }
            HoldPlacement();
            if (Time.realtimeSinceStartup >= nextBoundsRefresh)
            {
                nextBoundsRefresh = Time.realtimeSinceStartup + .5f;
                shipBounds = ReadBounds(structure);
            }
            if ((Active || HoldingPlacement) && !LocalMovementAvailable()) { owner.StopFrame("native swimming control"); return; }
            if (Preparing)
            {
                IPlayer player = api.Application.LocalPlayer;
                bool aboard = DockingVessels.Member(map, ship, map.NativeEntity(player.DrivingEntity)) || WithinShip(body.position);
                if (!aboard) { owner.StopFrame("character left the ship during preparation", false); return; }
                // Changing from a seat to the deck while still preparing is a
                // normal boarding transition, not a reason to discard the copy.
                if (map.SeatedShip.GetValue(actor) == null) enteredFromSeat = false;
                try
                {
                    if (geometry.AdvancePreparation())
                    {
                        owner.Log.Info("LocalFrame armed; " + Status + "; prepareWallMs="
                            + ((Time.realtimeSinceStartup - preparationStarted) * 1000f).ToString("F1") + "; worldIgnorePairs=0.");
                        owner.Log.Info("LocalFrame empty mesh example; " + geometry.EmptyMeshExample);
                        owner.Tell(map.SeatedShip.GetValue(actor) != null
                            ? "ShipWalk Ready. You can leave the open seat normally."
                            : "ShipWalk Ready. Ship-relative movement will start automatically.");
                    }
                    else if (Time.realtimeSinceStartup >= nextReport)
                    {
                        nextReport = Time.realtimeSinceStartup + 1f;
                        if (owner.Options.Trace) owner.Log.Info("LocalFrame preparing; " + Status);
                    }
                }
                catch (NotSupportedException error)
                {
                    owner.Log.Info("LocalFrame preparation snapshot; " + Status + "; emptyMeshExample=" + geometry.EmptyMeshExample);
                    owner.StopFrame(error.Message, false);
                    owner.Tell("Local frame preparation refused: " + error.Message);
                }
                return;
            }
            if (Active && (map.SeatedShip.GetValue(actor) != null
                || !(controller is Behaviour b) || !b.isActiveAndEnabled || !body.isKinematic
                || !body.detectCollisions || !nativeCapsule.isTrigger))
            {
                owner.StopFrame("native controller reclaimed the character; seated=" + (map.SeatedShip.GetValue(actor) != null)
                    + "; enabled=" + ((controller as Behaviour)?.isActiveAndEnabled ?? false)
                    + "; kinematic=" + body.isKinematic + "; detect=" + body.detectCollisions
                    + "; trigger=" + nativeCapsule.isTrigger + "; seatSettling=" + seatOperation.Settling(Time.time), false);
                return;
            }
            try { geometry.AdvanceRefresh(); }
            catch (NotSupportedException error)
            {
                owner.StopFrame(error.Message);
                return;
            }
            if (Active && Time.realtimeSinceStartup >= nextReport)
            {
                nextReport = Time.realtimeSinceStartup + 1f;
                if (owner.Options.Trace) owner.Log.Info("LocalFrame sample; " + Status + "; grounded=" + grounded
                    + "; local=" + geometry.Player.position.ToString("F3") + "; worldSpeed=" + worldVelocity.Length().ToString("F3")
                    + "; localSpeed=" + geometry.Player.velocity.magnitude.ToString("F3")
                    + "; input=" + lastInput.ToString("F2")
                    + "; climbing=" + climbing + "; jetpack=" + jetpackFlying + "; lookSamples=" + lookSamples
                    + "; contactBounds=" + contactBoundsQueries + "; contactPoints=" + contactPointQueries
                    + "; contactGap=" + contactPointGap.ToString("F3") + "; contactCell=" + lastContactPoint.ToString("F3")
                    + "; shipPoseGap=" + shipPoseGap.ToString("F4") + "; maxRenderCorrection=" + maxRenderCorrection.ToString("F4")
                    + "; nativeLocomotionDistance=" + nativeLocomotionDistance.ToString("F4")
                    + "; localLocomotionDistance=" + localLocomotionDistance.ToString("F4")
                    + "; " + geometry.SupportAlignment()
                    + "; fixedTime=" + Time.fixedTime.ToString("F3"));
                maxRenderCorrection = 0f;
            }
            if (Active && owner.Options.Trace && Time.realtimeSinceStartup >= nextLightReport)
            {
                nextLightReport = Time.realtimeSinceStartup + 5f;
                var camera = map.CameraRig.GetValue(actor) as Transform;
                owner.Log.Info("LocalFrame room lighting; ship=" + ShipId + "; " + geometry.RoomStatus(body.position)
                    + "; playerSensor=" + nativeCapsule.isTrigger + "; jetpack=" + jetpackFlying
                    + "; coasting=" + owner.Momentum.Coasting.Contains(ship)
                    + "; lightCameraGap=" + (camera == null ? "missing" : Vector3.Distance(camera.position, body.position).ToString("F2")));
            }
        }

        public void SelectBounding(object candidate)
        {
            if (NeedsBounding(candidate)) geometry.SelectBounding(shipBody);
            if (Matches(candidate)) geometry.SetRoomPresence(Active);
        }

        // Called from WaitForFixedUpdate, outside native contact callbacks and
        // after the game's world physics. Each fixed timestamp is consumed once.
        public void AfterWorldPhysics()
        {
            if (!Active || lastStep == Time.fixedTime) return;
            if (!UpdateDocking() || !Active) return;
            if (!Eligible()) { owner.StopFrame("invalid frame before physics", false); return; }
            if (!LocalMovementAvailable()) { owner.StopFrame("native swimming control"); return; }
            if (owner.Travel.Suspended)
            {
                lastStep = Time.fixedTime; pose = NetworkFrame ? shipFrame.Pose : ReadPose();
                PublishPhysics(); PublishRender(); return;
            }
            float dt = Time.fixedDeltaTime;
            LocalFramePose current = NetworkFrame ? shipFrame.Pose : ReadPose();
            NVector transport;
            bool validMotion = NetworkFrame
                ? NetworkMotion(current, dt, out transport)
                : LocalFrameMath.TryTransport(pose, current, armedRotation, dt, out transport);
            if ((!NetworkFrame && shipBody.gameObject.activeInHierarchy && shipBody.angularVelocity.magnitude > .01f) || !validMotion)
            {
                owner.StopFrame("frame motion rejected; dt=" + dt.ToString("F5")
                    + "; displacement=" + NVector.Distance(pose.Position, current.Position).ToString("F3")
                    + "; armedRotationDot=" + Math.Abs(NQuaternion.Dot(armedRotation, current.Rotation)).ToString("F7")
                    + "; velocity=" + FrameVelocity.ToString("F3") + "; shipRemote=" + map.Bool(map.EntityRemote, ship)
                    + "; seatSettling=" + seatOperation.Settling(Time.time));
                return;
            }
            Stopwatch timer = Stopwatch.StartNew();
            geometry.RefreshNearby();
            NVector sweepStart = WorldDeparture.SweepStart(pose, current, N(geometry.Player.position), dt);
            bool hadFloor = grounded;
            Vector3 relative = NetworkFrame ? U(PassengerLocalMotion.Velocity(N(geometry.Player.velocity)))
                : U(current.ToLocalVelocity(worldVelocity, transport));
            ConsumeLocalLook();
            if (!LocalFrameMath.TryLocalHeading(NetworkFrame ? NQuaternion.Identity : current.Rotation,
                NetworkFrame ? localFacing : N(body.rotation), out NQuaternion heading))
            { owner.StopFrame("invalid local heading", false); return; }
            geometry.Player.rotation = U(heading);
            Vector3 input = Cursor.lockState == CursorLockMode.Locked ? (Vector3)map.MovementInput.GetValue(actor) : Vector3.zero;
            lastInput = input;
            if (!MotionMath.Finite(N(input))) { owner.StopFrame("invalid movement input", false); return; }
            bool inElevator = map.Bool(map.ActorClimbing, actor);
            if (inElevator != climbing)
                owner.Log.Info("LocalFrame elevator/ladder " + (inElevator ? "entered" : "left") + "; ship=" + ShipId
                    + "; inputY=" + input.y.ToString("F2") + "; shipSpeed=" + FrameVelocity.magnitude.ToString("F2")
                    + "; native climb mode=" + inElevator + "; frame retained.");
            climbing = inElevator;
            bool flying = !inElevator && JetpackEnabled && (FreeLook || jetpackFlying || !grounded || input.y > 0f);
            if (flying != jetpackFlying)
                owner.Log.Info("LocalFrame jetpack " + (flying ? "flight" : "off") + "; ship=" + ShipId
                    + "; shipSpeed=" + FrameVelocity.magnitude.ToString("F2") + "; frame retained.");
            if (jetpackFlying && !flying)
            {
                // End free-flight roll/pitch when returning to deck movement;
                // native walking camera pitch remains independently controlled.
                body.rotation = U(current.Rotation * heading);
                body.transform.rotation = body.rotation;
                if (NetworkFrame) { localFacing = heading; publishedFacing = N(body.rotation); }
            }
            jetpackFlying = flying;
            map.Jetpack.SetValue(controller, flying);
            bool jump = Cursor.lockState == CursorLockMode.Locked
                && (map.Bool(map.JumpRequested, controller) || map.Bool(map.JumpHeld, controller));
            map.JumpRequested.SetValue(controller, false);
            map.JumpHeld.SetValue(controller, false);
            if (inElevator)
            {
                relative = U(LocalClimb.Velocity(heading, N(input), (float)map.ClimbSpeed.GetValue(controller),
                    map.Bool(map.Sprinting, controller)));
                grounded = false;
            }
            else if (flying)
            {
                NQuaternion flightHeading = NetworkFrame ? localFacing : N(Quaternion.Inverse(U(current.Rotation)) * body.rotation);
                relative = U(LocalFlight.Step(N(relative), flightHeading, N(input), dt));
                grounded = false;
            }
            else if (hadFloor)
            {
                Vector3 forward = U(heading) * Vector3.forward;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 walking = Vector3.ClampMagnitude(right * input.x + forward * input.z, 1f) * LocalFrameMath.WalkSpeed;
                relative = new Vector3(walking.x, Mathf.Min(0, geometry.Player.velocity.y), walking.z);
                if (jump) { relative.y = LocalFrameMath.JumpSpeed; grounded = false; }
            }
            if (!flying && !inElevator) relative.y -= LocalFrameMath.Gravity * dt;
            geometry.Player.velocity = relative;
            geometry.Physics.Simulate(dt);
            worldVelocity = NetworkFrame ? shipFrame.WorldVelocity(N(geometry.Player.position), N(geometry.Player.velocity))
                : current.ToWorldVelocity(N(geometry.Player.velocity), transport);
            pose = current;
            presentation.Advance(N(geometry.Player.position));
            grounded = !flying && !inElevator && geometry.Player.velocity.y <= .15f && geometry.Grounded();
            map.Grounded.SetValue(controller, grounded);
            lastStep = Time.fixedTime;
            Vector3 proposedWorld = U(current.ToWorldPoint(N(geometry.Player.position))) - map.OriginOffset;
            if (!grounded && worldCollision.TryContact(map, ship, hull, nativeCapsule, geometry.Capsule, current,
                U(sweepStart) - map.OriginOffset, proposedWorld, out Vector3 safeWorld, out string contact, geometry.OwnsCollider))
            {
                body.position = safeWorld; body.transform.position = safeWorld; characterRoot.position = safeWorld;
                map.SetEntityPosition.Invoke(actor, new object[] { safeWorld + map.OriginOffset, false });
                owner.Log.Info("LocalFrame world collision handoff; ship=" + ShipId + "; contact=" + contact
                    + "; proposed=" + proposedWorld.ToString("F3") + "; safe=" + safeWorld.ToString("F3"));
                owner.StopFrame("world collision: " + contact);
                return;
            }
            PublishPhysics();
            timer.Stop(); steps++; totalMs += timer.Elapsed.TotalMilliseconds; maxMs = Math.Max(maxMs, timer.Elapsed.TotalMilliseconds);
            if (!shipBounds.Contains(N(geometry.Capsule.bounds.center), 2f) && !geometry.Contains(N(geometry.Capsule.bounds.center), 2f))
            {
                owner.Log.Info("LocalFrame departure bounds; ship=" + ShipId + "; player=" + geometry.Player.position.ToString("F3")
                    + "; capsuleCenter=" + geometry.Capsule.bounds.center.ToString("F3")
                    + "; min=" + shipBounds.Min + "; max=" + shipBounds.Max + "; enteredFromSeat=" + enteredFromSeat);
                owner.StopFrame("left ship bounds");
            }
        }

        private void PublishPhysics()
        {
            if (!Active || body == null) return;
            body.position = U(pose.ToWorldPoint(N(geometry.Player.position))) - map.OriginOffset;
        }

        private bool NetworkMotion(LocalFramePose current, float dt, out NVector transport)
        {
            transport = shipFrame.Velocity;
            // Ship sample continuity is handled once at presentation. A local
            // physics step validates only its own usable frame and timestep.
            return current.Valid && RemoteFrameMath.ValidVelocity(transport)
                && MotionMath.Finite(dt) && dt >= .0001f && dt <= .1f;
        }

        internal FrameMessage NetworkState()
        {
            if (!NetworkFrame || !Armed || actor == null || hull == null || !Eligible()) return null;
            bool seated = ContainsShip(map.SeatedShip.GetValue(actor));
            if (!Active && !seated)
            {
                if (HoldingPlacement && placement.Pending && geometry.Contains(placement.Preferred, BoardingPlacement.GroupPadding))
                {
                    // This is a bounded airborne hold, not a successful walking
                    // placement. Keep the authenticated carrier membership alive.
                    return new FrameMessage { Ship = ShipId, Member = aboardShip == null ? ShipId : map.Id(aboardShip),
                        Position = placement.Preferred, Rotation = placement.Rotation, Velocity = NVector.Zero, Mode = PassengerMode.Jumping };
                }
                // Native seat callbacks briefly suspend the local capsule. This
                // changes the character mode without ending ship membership.
                if (lastNetworkState != null && (seatOperation.Busy || seatOperation.Settling(Time.time)))
                { var pending = lastNetworkState.Copy(); pending.Mode = PassengerMode.Jumping; return pending; }
                return null;
            }
            Vector3 point = Active ? geometry.Player.position : Quaternion.Inverse(hull.rotation) * (characterRoot.position - hull.position);
            Quaternion rotation = Active ? (NetworkFrame && haveFacing ? U(localFacing) : geometry.Player.rotation)
                : Quaternion.Inverse(hull.rotation) * characterRoot.rotation;
            lastNetworkState = new FrameMessage { Ship = ShipId, Position = N(point), Rotation = N(rotation),
                Member = aboardShip == null ? ShipId : map.Id(aboardShip),
                Velocity = Active ? N(geometry.Player.velocity) : NVector.Zero,
                Mode = seated ? PassengerMode.Seated : climbing ? PassengerMode.Elevator : jetpackFlying ? PassengerMode.Jetpack
                    : grounded ? PassengerMode.Walking : PassengerMode.Jumping };
            return lastNetworkState.Copy();
        }

        public void PublishRender(bool publishEntity = true)
        {
            HoldPlacement();
            if (!Active || body == null || hull == null || characterRoot == null) return;
            ConsumeLocalLook();
            // Stay in Unity world coordinates here: adding a large absolute
            // origin just to subtract it would discard useful float precision.
            var renderedShip = new LocalFramePose(N(hull.position), N(hull.rotation));
            float fraction = Time.fixedDeltaTime > 0f ? (Time.time - Time.fixedTime) / Time.fixedDeltaTime : 1f;
            Vector3 point = U(presentation.WorldPoint(renderedShip, fraction));
            shipPoseGap = shipBody.gameObject.activeInHierarchy ? Vector3.Distance(hull.position, shipBody.position) : 0f;
            body.position = point;
            // DetachToolbox consumes the physics Transform immediately. Writing
            // the Rigidbody alone can leave that Transform stale until physics.
            body.transform.position = point;
            if (NetworkFrame && haveFacing)
            {
                Quaternion facing = hull.rotation * U(localFacing);
                body.rotation = facing; body.transform.rotation = facing;
                publishedFacing = N(facing);
                if (publishEntity) characterRoot.rotation = facing;
            }
            if (publishEntity)
            {
                maxRenderCorrection = Mathf.Max(maxRenderCorrection, Vector3.Distance(characterRoot.position, point));
                characterRoot.position = point;
                // Preserve native world coordinates and spatial bookkeeping for
                // interactions; false avoids writing the physics body again.
                map.SetEntityPosition.Invoke(actor, new object[] { point + map.OriginOffset, false });
            }
        }

        public Vector3 TakeLocomotion(Vector3 nativeDelta)
        {
            Vector3 relative = U(presentation.TakeLocomotion(N(hull.rotation)));
            nativeLocomotionDistance = nativeDelta.magnitude;
            localLocomotionDistance = relative.magnitude;
            return relative;
        }

        public void BeginSeatOperation(object destination)
        {
            if (!Armed) return;
            ReleasePlacement(false);
            seatOperation.Begin();
            if (destination != null)
            { placement.Reset(); nativeExitPoint = null; placementFailure = null; resumeAcceptedProxy = resumeSeatFrame = false; }
            if (destination != null && ContainsShip(destination)) aboardShip = destination;
            if (destination == null && ContainsShip(map.SeatedShip.GetValue(actor)) && !placement.Pending)
            {
                // Capture in the stable pre-detach frame. Native world placement
                // during ownership changes must not redefine the local point.
                resumeSeatPoint = Quaternion.Inverse(hull.rotation) * (characterRoot.position - hull.position);
                resumeSeatVelocity = Vector3.zero;
                LocalFrameMath.TryLocalHeading(N(hull.rotation), N(characterRoot.rotation), out NQuaternion heading);
                resumeSeatHeading = U(heading); resumeSeatFrame = true; resumeAcceptedProxy = false;
                nativeExitPoint = null;
                if (NetworkFrame && map.Bool(map.ActorPiloting, actor) && ReferenceEquals(map.SeatedShip.GetValue(actor), ship)) shipFrame.BeginHandoff();
            }
            // The server can acknowledge an exit after the local player is
            // already walking. Retain the accepted local pose across that
            // repeated native callback and release our flags before it runs.
            if (destination == null && Active && map.SeatedShip.GetValue(actor) == null)
            {
                resumeSeatPoint = geometry.Player.position;
                resumeSeatVelocity = geometry.Player.velocity;
                resumeSeatHeading = geometry.Player.rotation;
                resumeSeatFrame = resumeAcceptedProxy = true;
                SuspendForSeat();
                owner.Log.Info("LocalFrame native exit acknowledgement; ship=" + ShipId + "; localPoint=" + resumeSeatPoint.ToString("F3"));
            }
        }

        public void CompleteSeatOperation()
        {
            if (!Armed || !seatOperation.Busy) return;
            seatOperation.Complete(Time.fixedTime, Time.time);
            if (map.SeatedShip.GetValue(actor) == null) enteredFromSeat = true;
            if (!seatOperation.Busy && enteredFromSeat && resumeSeatFrame && !resumeAcceptedProxy && !placement.Pending
                && map.SeatedShip.GetValue(actor) == null && body != null && hull != null)
            {
                // Pair the completed native exit point with the hull pose from
                // this same callback. Never subtract a later moving hull pose.
                Vector3 point = Quaternion.Inverse(hull.rotation) * (body.position - hull.position);
                if (BoardingPlacement.AcceptNative(N(resumeSeatPoint), N(point)) && geometry.Contains(N(point), BoardingPlacement.GroupPadding))
                    nativeExitPoint = point;
            }
            owner.Log.Info("LocalFrame native seat operation complete; ship=" + ShipId
                + "; seated=" + (map.SeatedShip.GetValue(actor) != null) + "; resumeLocal=" + resumeSeatFrame
                + "; shipRemote=" + map.Bool(map.EntityRemote, ship) + "; activation=next-fixed-step.");
        }

        public void SuspendForSeat()
        {
            if (!Active) return;
            // Seat entry changes the character owner, not the vessel owner.
            // Keep the prepared geometry and coasting collision lease alive.
            PublishRender();
            if (lease.Release()) RestoreCharacter(false);
            geometry.Player.detectCollisions = false;
            geometry.SetRoomPresence(false);
            enteredFromSeat = true; jetpackFlying = false; climbing = false; grounded = false;
            lastStep = float.NegativeInfinity;
            owner.Log.Info("LocalFrame seated; ship=" + ShipId + "; shipSpeed="
                + FrameVelocity.magnitude.ToString("F2") + "; prepared frame retained; shipMotionOwner="
                + (NetworkFrame ? "native-network" : "local-coasting") + ".");
        }

        private void RestoreCharacter(bool inherit)
        {
            if (body == null) return;
            // Restore only values still owned by this adapter. Solid collision
            // is restored after kinematic state, before native movement resumes.
            if (body.isKinematic) body.isKinematic = lease.Kinematic;
            if (nativeCapsule != null && nativeCapsule.isTrigger) nativeCapsule.isTrigger = lease.Trigger;
            if (body.detectCollisions) body.detectCollisions = lease.DetectCollisions;
            if (body.interpolation == RigidbodyInterpolation.None)
                body.interpolation = (RigidbodyInterpolation)lease.Interpolation;
            if (inherit && !body.isKinematic && MotionMath.Finite(worldVelocity)) body.velocity = U(worldVelocity);
            if (controller != null)
            {
                map.JumpRequested.SetValue(controller, false); map.JumpHeld.SetValue(controller, false);
                map.Grounded.SetValue(controller, false); map.Jetpack.SetValue(controller, false);
                map.LookAccumulator.SetValue(controller, Vector3.zero);
            }
            if (owner.Options.Trace) owner.Log.Info("LocalFrame native physics restored; kinematic=" + body.isKinematic
                + "; detect=" + body.detectCollisions + "; trigger=" + (nativeCapsule != null && nativeCapsule.isTrigger)
                + "; position=" + body.position.ToString("F3") + "; inherit=" + inherit);
        }

        public void Disarm(Action restoreNativeSelection, bool inherit)
        {
            ReleaseArrival();
            ReleasePlacement(inherit); placement.Reset(); nativeExitPoint = null; placementFailure = null;
            pendingDockRoot = sampledMember = null;
            seatOperation.Reset(); resumeSeatFrame = resumeAcceptedProxy = false; lastNetworkState = null; haveFacing = false;
            LocalGeometry old = geometry;
            bool wasActive = lease.Held;
            // Release ownership before native selector callbacks or reseating.
            geometry = null;
            bool held = lease.Release();
            try
            {
                old?.RestoreNativeShapes();
                restoreNativeSelection();
            }
            finally
            {
                try
                {
                    if (held) RestoreCharacter(inherit);
                    if (old != null && body != null && owner.Options.Trace)
                        owner.Log.Info("LocalFrame lighting after release; ship=" + ShipId + "; " + old.RoomStatus(body.position));
                }
                finally { old?.Dispose(); ship = actor = aboardShip = null; body = shipBody = null; controller = null; hull = characterRoot = null; nativeCapsule = null; structure = null; shipBounds = default; }
            }
            if (wasActive) owner.Log.Info("LocalFrame released; native character body flags restored.");
        }

        public object Ship => ship;
        private static NVector N(Vector3 v) => new NVector(v.x, v.y, v.z);
        private static NQuaternion N(Quaternion q) => new NQuaternion(q.x, q.y, q.z, q.w);
        private static Vector3 U(NVector v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion U(NQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class LocalFrameDriver : MonoBehaviour
    {
        internal Runtime Owner;
        private IEnumerator Start()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                try { Owner?.Frame.AfterWorldPhysics(); }
                catch (Exception error) { Owner?.Fail(error); }
            }
        }
        private void LateUpdate()
        {
            try { Owner?.Frame.PublishRender(); Owner?.Network.Present(); }
            catch (Exception error) { Owner?.Fail(error); }
        }
    }
}
