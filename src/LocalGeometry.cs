using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Eleon.Modding;
using UnityEngine;
using UnityEngine.SceneManagement;
using NVector = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace ShipWalk
{
    // Collision surfaces live in an independent, stationary physics scene.
    // Changes update individual records; no whole-ship frozen snapshot is needed.
    internal sealed class LocalGeometry : IDisposable
    {
        private Transform hull;
        private readonly Build5150 map;
        private object ship;
        private LocalRoomPresence roomPresence;
        private readonly Func<IEnumerable<IEntity>> vessels;
        private readonly Dictionary<object, Transform> members = new Dictionary<object, Transform>();
        private readonly HashSet<Rigidbody> memberBodies = new HashSet<Rigidbody>();
        private readonly Dictionary<Transform, object> anchors = new Dictionary<Transform, object>();
        private readonly Dictionary<Collider, object> solidOwners = new Dictionary<Collider, object>();
        private readonly LiveGeometryIndex<Collider, Shape> shapes = new LiveGeometryIndex<Collider, Shape>();
        private readonly Dictionary<Transform, Node> nodes = new Dictionary<Transform, Node>();
        private readonly Dictionary<Collider, bool> nativeFlags = new Dictionary<Collider, bool>();
        private readonly IncrementalWork preparation;
        private IncrementalWork refresh;
        private GameObject root, playerObject;
        private Scene scene;
        private CapsuleCollider nativeCapsule;
        private Collider support;
        private bool disposed, configured, dirty, suppressNative;
        private string phase = "scan";
        private int processed, expected, round, transformPass, shapeUpdates, nodeUpdates;
        private int playerLayer, playerInclude, playerExclude, playerPriority;
        private double refreshMaxMs, nearbyMaxMs, selectionMaxMs;
        private float nextRefresh;
        private Collider clearanceBlocker;
        private float clearanceDepth;
        private string clearanceFailure;
        private readonly RaycastHit[] placementHits = new RaycastHit[64];
        public PhysicsScene Physics { get; private set; }
        public Rigidbody Player { get; private set; }
        public CapsuleCollider Capsule { get; private set; }
        public int Count => shapes.Entries.Count(e => e.Value.Copy != null);
        public int ActiveShapes => shapes.Entries.Count(e => IsEnabled(e.Value.Copy));
        public int MemberCount => members.Count;
        public object SupportEntity => support != null && solidOwners.TryGetValue(support, out object member) ? member : null;
        internal IEnumerable<object> Members => members.Keys;
        public bool Ready => !disposed && preparation.Completed;
        public bool HasNativeOverrides => nativeFlags.Count != 0;
        public string Progress => phase + " " + processed + "/" + expected;
        public string EmptyMeshExample { get; private set; } = "none";
        public string ClearanceStatus => clearanceFailure + "; blocker=" + (clearanceBlocker == null ? "none"
            : clearanceBlocker.GetType().Name + ":" + clearanceBlocker.name + "; member="
                + (solidOwners.TryGetValue(clearanceBlocker, out object member) && member != null ? map.Id(member) : -1))
            + "; depth=" + clearanceDepth.ToString("F4");
        public string Metrics => "prepareSlices=" + preparation.Slices
            + "; prepareCpuMs=" + preparation.TotalMs.ToString("F3")
            + "; prepareMaxSliceMs=" + preparation.MaxSliceMs.ToString("F3")
            + "; refreshMaxSliceMs=" + refreshMaxMs.ToString("F3")
            + "; nearbyMaxMs=" + nearbyMaxMs.ToString("F3")
            + "; selectionMaxMs=" + selectionMaxMs.ToString("F3")
            + "; meshShapes=" + shapes.Entries.Count(e => e.Value.Mesh && !e.Value.Empty)
            + "; emptyMeshes=" + shapes.Entries.Count(e => e.Value.Empty)
            + "; inactiveShapes=" + shapes.Entries.Count(e => !e.Value.Active)
            + "; refreshRounds=" + round + "; shapeUpdates=" + shapeUpdates + "; nodeUpdates=" + nodeUpdates;

        private sealed class Node
        {
            public Transform Copy;
            public int SeenRound, UpdatedPass;
        }
        private sealed class Shape
        {
            public Collider Copy;
            public Bounds Bounds;
            public int Fingerprint;
            public bool HaveFingerprint, HasBounds, Mesh, Empty, Active, PlayerEligible;
        }

        public LocalGeometry(Build5150 map, object ship, Transform hull, Rigidbody nativePlayer, CapsuleCollider capsule,
            Func<IEnumerable<IEntity>> vessels)
        {
            this.hull = hull; this.map = map; this.ship = ship; nativeCapsule = capsule;
            this.vessels = vessels;
            DiscoverMembers();
            roomPresence = new LocalRoomPresence(map, hull);
            preparation = new IncrementalWork(Prepare(nativePlayer, capsule).GetEnumerator());
        }

        public bool AdvancePreparation()
        {
            if (disposed) return false;
            transformPass++;
            bool ready = preparation.Advance();
            if (ready) { FlushTransforms(); nextRefresh = 0f; }
            return ready;
        }

        private IEnumerable<bool> Prepare(Rigidbody nativePlayer, CapsuleCollider capsule)
        {
            phase = "scene";
            scene = SceneManager.CreateScene("ShipWalk.LocalFrame." + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            Physics = scene.GetPhysicsScene();
            if (!Physics.IsValid() || Physics == UnityEngine.Physics.defaultPhysicsScene)
                throw new InvalidOperationException("Independent physics scene was not created.");
            root = new GameObject("ShipWalk local interior");
            SceneManager.MoveGameObjectToScene(root, scene);
            yield return true;
            foreach (bool step in Reconcile(true)) yield return step;
            if (Count == 0) throw new NotSupportedException("The ship has no collision surfaces to copy.");
            phase = "character"; processed = 0; expected = 1;
            playerObject = new GameObject("ShipWalk local character");
            playerObject.layer = 2;
            SceneManager.MoveGameObjectToScene(playerObject, scene);
            Player = playerObject.AddComponent<Rigidbody>();
            Player.detectCollisions = false;
            Player.useGravity = false; Player.drag = 0f; Player.angularDrag = 0f;
            Player.constraints = RigidbodyConstraints.FreezeRotation;
            Player.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Player.interpolation = RigidbodyInterpolation.None;
            Player.mass = nativePlayer.mass;
            var shape = new GameObject("capsule"); shape.layer = 2;
            shape.transform.SetParent(playerObject.transform, false);
            Capsule = shape.AddComponent<CapsuleCollider>();
            CopyPlayerShape(nativePlayer, capsule);
            Capsule.includeLayers = 1; Capsule.excludeLayers = ~1; Capsule.layerOverridePriority = 100;
            yield return true;
            // No second pass requiring thousands of transforms to stay frozen.
            // Configuration refreshes current surfaces before taking ownership.
            phase = "ready"; processed = expected = Count;
        }

        private void CopyPlayerShape(Rigidbody nativePlayer, CapsuleCollider capsule)
        {
            Transform sourceBody = nativePlayer.transform;
            Capsule.transform.localPosition = Quaternion.Inverse(sourceBody.rotation) * (capsule.transform.position - sourceBody.position);
            Capsule.transform.localRotation = Quaternion.Inverse(sourceBody.rotation) * capsule.transform.rotation;
            Capsule.transform.localScale = capsule.transform.lossyScale;
            LocalShapeCopy.Copy(capsule, Capsule); dirty = true;
        }

        public void ConfigurePlayer(Rigidbody nativePlayer, CapsuleCollider capsule)
        {
            nativeCapsule = capsule; configured = true; transformPass++;
            playerLayer = capsule.gameObject.layer; playerInclude = capsule.includeLayers.value;
            playerExclude = capsule.excludeLayers.value; playerPriority = capsule.layerOverridePriority;
            CopyPlayerShape(nativePlayer, capsule); Player.mass = nativePlayer.mass;
            foreach (var entry in shapes.Entries)
            {
                if (IsStaticHull(entry.Key)) SynchronizeShape(entry.Key, entry.Value);
                else RemoveShape(entry.Key, entry.Value);
            }
            FlushTransforms();
            if (ActiveShapes == 0)
                throw new NotSupportedException("No interior surface can collide with the native walking capsule; layer=" + capsule.gameObject.layer + ".");
        }

        // Sync only the relevant hierarchy, preserving nonuniform ancestor scale.
        // A moving door changes its clone rather than cancelling the entire ship.
        private Transform Mirror(Transform source)
        {
            if (source == null) return null;
            bool anchor = anchors.TryGetValue(source, out object member);
            if (anchor && !members.ContainsKey(member)) return null;
            Transform parent = anchor ? root.transform : Mirror(source.parent);
            if (parent == null) return null;
            if (!nodes.TryGetValue(source, out Node node) || node.Copy == null)
            {
                var copy = new GameObject("local shape").transform;
                copy.gameObject.layer = 0; copy.SetParent(parent, false);
                node = new Node { Copy = copy };
                nodes[source] = node; dirty = true;
            }
            node.SeenRound = round;
            if (node.UpdatedPass == transformPass) return node.Copy;
            node.UpdatedPass = transformPass;
            Vector3 position = anchor ? Quaternion.Inverse(hull.rotation) * (source.position - hull.position) : source.localPosition;
            Quaternion rotation = anchor ? Quaternion.Inverse(hull.rotation) * source.rotation : source.localRotation;
            Vector3 scale = anchor ? source.lossyScale : source.localScale;
            bool active = anchor ? source.gameObject.activeInHierarchy : source.gameObject.activeSelf;
            bool changed = false;
            if (node.Copy.parent != parent) { node.Copy.SetParent(parent, false); changed = true; }
            if (node.Copy.localPosition != position) { node.Copy.localPosition = position; changed = true; }
            if (node.Copy.localRotation != rotation) { node.Copy.localRotation = rotation; changed = true; }
            if (node.Copy.localScale != scale) { node.Copy.localScale = scale; changed = true; }
            if (node.Copy.gameObject.activeSelf != active) { node.Copy.gameObject.SetActive(active); changed = true; }
            if (changed) { dirty = true; nodeUpdates++; }
            return node.Copy;
        }

        public void AdvanceRefresh()
        {
            if (!Ready) return;
            if (refresh == null)
            {
                if (Time.realtimeSinceStartup < nextRefresh) return;
                refresh = new IncrementalWork(Reconcile(false).GetEnumerator());
            }
            transformPass++;
            bool finished;
            try { finished = refresh.Advance(); }
            finally { refreshMaxMs = Math.Max(refreshMaxMs, refresh.MaxSliceMs); }
            if (finished)
            {
                refresh.Dispose(); refresh = null;
                nextRefresh = Time.realtimeSinceStartup + .25f;
            }
        }

        private IEnumerable<bool> Reconcile(bool preparing)
        {
            if (hull == null) throw new NotSupportedException("The ship was removed.");
            round++;
            var previousNodes = nodes.ToArray();
            DiscoverMembers();
            Transform[] scanRoots = members.Values.Where(t => !members.Values.Any(other => other != t && t.IsChildOf(other))).ToArray();
            Collider[] current = scanRoots.SelectMany(t => t.GetComponentsInChildren<Collider>(true)).Distinct().ToArray();
            roomPresence.Refresh(current);
            if (preparing) { phase = "copy/update"; processed = 0; expected = current.Length; }
            yield return true;
            foreach (bool step in shapes.Reconcile(current, IsStaticHull, _ => new Shape(), SynchronizeShape, RemoveShape))
            {
                if (preparing) processed++;
                yield return step;
            }
            // Removed or reparented subtrees do not accumulate across rebuilds.
            foreach (var entry in previousNodes)
            {
                if (entry.Value.SeenRound != round)
                {
                    if (entry.Value.Copy != null)
                    { entry.Value.Copy.gameObject.SetActive(false); Object.Destroy(entry.Value.Copy.gameObject); dirty = true; }
                    nodes.Remove(entry.Key);
                    anchors.Remove(entry.Key);
                }
                yield return true;
            }
        }

        private void SynchronizeShape(Collider source, Shape state)
        {
            Transform target = Mirror(source.transform);
            if (target == null) { RemoveShape(source, state); return; }
            state.Active = source.gameObject.activeInHierarchy;
            state.Mesh = source is MeshCollider;
            state.Empty = source is MeshCollider empty && empty.sharedMesh == null;
            if (state.Empty && EmptyMeshExample == "none") EmptyMeshExample = Describe(source);
            bool supported = source is BoxCollider || source is SphereCollider || source is CapsuleCollider || source is MeshCollider;
            if (!supported && state.Active)
                throw new NotSupportedException("Unsupported interior shape: " + Describe(source));
            if (suppressNative) Suppress(source);
            if (!supported || state.Empty)
            {
                RemoveCopy(state); state.HasBounds = false; state.HaveFingerprint = false;
                return;
            }
            bool created = state.Copy == null;
            if (created)
            {
                state.Copy = (Collider)target.gameObject.AddComponent(source.GetType());
                solidOwners[state.Copy] = map.ResolveEntity(source);
                state.Copy.enabled = false;
                state.Copy.includeLayers = 1 << 2; state.Copy.excludeLayers = ~(1 << 2); state.Copy.layerOverridePriority = 100;
                dirty = true;
            }
            if (state.Copy.transform != target) throw new InvalidOperationException("Local collider hierarchy association changed.");
            int fingerprint = ShapeFingerprint(source);
            if (created || !state.HaveFingerprint || state.Fingerprint != fingerprint)
            {
                LocalShapeCopy.Copy(source, state.Copy);
                state.Fingerprint = fingerprint; state.HaveFingerprint = true; shapeUpdates++; dirty = true;
            }
            state.PlayerEligible = !configured || Allows(nativeCapsule, source);
            bool enabled = state.Active && state.PlayerEligible;
            if (state.Copy.enabled != enabled) { state.Copy.enabled = enabled; dirty = true; }
            state.Bounds = LocalBounds(source); state.HasBounds = true;
        }

        // Fast path for nearby doors/animated surfaces. The full bounded pass
        // discovers additions/removals; this path avoids a full-pass delay when
        // an already tracked surface near the character moves or activates.
        public void RefreshNearby()
        {
            if (!Ready || !configured) return;
            PruneMembers();
            Stopwatch timer = Stopwatch.StartNew(); transformPass++;
            Vector3 center = Capsule.bounds.center;
            float radius = 8f + Player.velocity.magnitude * Time.fixedDeltaTime;
            foreach (var entry in shapes.Entries)
            {
                Shape shape = entry.Value;
                if (!shape.HasBounds || !shape.PlayerEligible
                    || LocalVolume.DistanceSquared(N(center), N(shape.Bounds.center), N(shape.Bounds.extents)) > radius * radius) continue;
                if (IsStaticHull(entry.Key)) SynchronizeShape(entry.Key, shape);
                else RemoveShape(entry.Key, shape);
            }
            FlushTransforms();
            nearbyMaxMs = Math.Max(nearbyMaxMs, timer.Elapsed.TotalMilliseconds);
        }

        private void RemoveCopy(Shape state)
        {
            if (state.Copy != null)
            { solidOwners.Remove(state.Copy); state.Copy.enabled = false; Object.Destroy(state.Copy); dirty = true; }
            state.Copy = null;
        }
        private void RemoveShape(Collider source, Shape state)
        {
            RemoveCopy(state); state.HasBounds = false; state.HaveFingerprint = false;
            if (nativeFlags.TryGetValue(source, out bool enabled))
            {
                if (source != null && source.enabled != enabled) source.enabled = enabled;
                nativeFlags.Remove(source);
            }
        }
        private void FlushTransforms()
        {
            if (!dirty) return;
            UnityEngine.Physics.SyncTransforms(); dirty = false;
        }

        private bool IsStaticHull(Collider c)
        {
            if (c == null || c.isTrigger || c.CompareTag("T_BB")) return false;
            object member = map.ResolveEntity(c);
            if (member == null || !members.ContainsKey(member)) return false;
            Rigidbody attached = c.GetComponentInParent<Rigidbody>(true);
            return attached == null || memberBodies.Contains(attached);
        }

        public bool OwnsCollider(Collider c) => c != null && map.ResolveEntity(c) is object member
            && members.ContainsKey(member) && DockingVessels.Member(map, ship, member);
        public bool Contains(NVector point, float padding)
            => members.Keys.Any(member => DockingVessels.Member(map, ship, member)
                && DockingVessels.Contains(map, ship, member, point, padding));
        private void DiscoverMembers()
        {
            members.Clear(); memberBodies.Clear();
            foreach (object member in DockingVessels.Group(map, ship, vessels()))
            {
                var t = map.EntityTransform.GetValue(member) as Transform;
                if (t == null) continue;
                members[member] = t; anchors[t] = member;
                if (map.EntityBody.GetValue(member) is Rigidbody b && b != null) memberBodies.Add(b);
            }
        }
        private void PruneMembers()
        {
            // Remove departing collision immediately, not after an incremental
            // discovery round. The session observes support before this runs.
            foreach (var member in members.ToArray())
                if (!DockingVessels.Member(map, ship, member.Key))
                {
                    if (member.Value != null && nodes.TryGetValue(member.Value, out Node node) && node.Copy != null)
                        node.Copy.gameObject.SetActive(false);
                    members.Remove(member.Key); dirty = true;
                }
        }
        public void Reframe(object nextShip, Transform nextHull, LocalFramePose previous, LocalFramePose next)
        {
            refresh?.Dispose(); refresh = null; nextRefresh = 0;
            RestoreNativeShapes();
            ship = nextShip; hull = nextHull; roomPresence = new LocalRoomPresence(map, hull);
            DiscoverMembers(); transformPass++;
            // Existing vessel subtrees each have one local anchor. Reposition
            // those anchors; keep their colliders and the character scene alive.
            foreach (var anchor in anchors.ToArray())
                if (nodes.TryGetValue(anchor.Key, out Node node) && node.Copy != null)
                {
                    if (members.ContainsKey(anchor.Value)) Mirror(anchor.Key);
                    else { node.Copy.gameObject.SetActive(false); dirty = true; }
                }
            foreach (var entry in shapes.Entries)
            {
                Shape shape = entry.Value;
                if (!shape.HasBounds) continue;
                var b = shape.Bounds;
                NVector origin = next.ToLocalPoint(previous.ToWorldPoint(NVector.Zero));
                System.Numerics.Quaternion rotation = System.Numerics.Quaternion.Inverse(next.Rotation) * previous.Rotation;
                LocalVolume rebased = LocalVolume.TransformBox(N(b.center), N(b.extents), origin,
                    NVector.Transform(NVector.UnitX, rotation), NVector.Transform(NVector.UnitY, rotation), NVector.Transform(NVector.UnitZ, rotation));
                shape.Bounds = new Bounds(U((rebased.Min + rebased.Max) * .5f), U(rebased.Size));
            }
            FlushTransforms();
        }

        public void SelectBounding(Rigidbody body)
        {
            Stopwatch timer = Stopwatch.StartNew(); suppressNative = true;
            foreach (var entry in shapes.Entries) if (entry.Key != null) Suppress(entry.Key);
            if (!body.gameObject.activeSelf) body.gameObject.SetActive(true);
            selectionMaxMs = Math.Max(selectionMaxMs, timer.Elapsed.TotalMilliseconds);
        }
        private void Suppress(Collider source)
        {
            if (!ReferenceEquals(map.ResolveEntity(source), ship)) return;
            if (!nativeFlags.ContainsKey(source)) nativeFlags.Add(source, source.enabled);
            if (source.enabled) source.enabled = false;
        }
        public void RestoreNativeShapes()
        {
            suppressNative = false;
            roomPresence.Restore();
            foreach (var entry in nativeFlags)
                if (entry.Key != null && entry.Key.enabled != entry.Value) entry.Key.enabled = entry.Value;
            nativeFlags.Clear();
        }
        public void SetRoomPresence(bool active) => roomPresence.SetActive(active);
        public string RoomStatus(Vector3 worldPlayer) => roomPresence.Describe(worldPlayer);

        public bool ResumeAcceptedPlayer(Rigidbody nativePlayer, CapsuleCollider capsule, Vector3 position, Quaternion rotation)
        {
            // An exit acknowledgement does not create a second seat departure.
            // Retain the solver's accepted capsule when the native shape is the
            // same. A changed capsule still takes the normal clearance path.
            if (!Ready || !configured || capsule != nativeCapsule || Capsule == null
                || capsule.gameObject.layer != playerLayer || capsule.includeLayers.value != playerInclude
                || capsule.excludeLayers.value != playerExclude || capsule.layerOverridePriority != playerPriority
                || capsule.center != Capsule.center || capsule.height != Capsule.height || capsule.radius != Capsule.radius
                || capsule.direction != Capsule.direction || capsule.sharedMaterial != Capsule.sharedMaterial
                || capsule.contactOffset != Capsule.contactOffset) return false;
            Transform source = nativePlayer.transform;
            Vector3 offset = Quaternion.Inverse(source.rotation) * (capsule.transform.position - source.position);
            Quaternion facing = Quaternion.Inverse(source.rotation) * capsule.transform.rotation;
            if ((offset - Capsule.transform.localPosition).sqrMagnitude > .000001f
                || Quaternion.Angle(facing, Capsule.transform.localRotation) > .01f
                || (capsule.transform.lossyScale - Capsule.transform.localScale).sqrMagnitude > .000001f) return false;
            Player.position = position; Player.rotation = rotation; Player.detectCollisions = true; Player.mass = nativePlayer.mass;
            FlushTransforms(); return true;
        }

        public bool ClearExit(Vector3 position, Quaternion rotation)
        {
            FlushTransforms();
            clearanceBlocker = null; clearanceDepth = 0; clearanceFailure = "clear";
            bool clear = CapsuleClearance.Resolve(N(position), p => N(ClearanceCorrection(U(p), rotation)), out NVector solved);
            if (!clear) { clearanceFailure = "overlap correction exhausted"; return false; }
            SetPlayerPose(U(solved), rotation);
            return true;
        }
        private void CapsulePose(Vector3 position, Quaternion rotation, out Vector3 shapePoint, out Quaternion shapeRotation,
            out Vector3 a, out Vector3 b, out float radius)
        {
            shapePoint = position + rotation * Capsule.transform.localPosition;
            shapeRotation = rotation * Capsule.transform.localRotation;
            Vector3 scale = Capsule.transform.lossyScale;
            int axis = Capsule.direction;
            radius = Capsule.radius * Mathf.Max(Mathf.Abs(scale[(axis + 1) % 3]), Mathf.Abs(scale[(axis + 2) % 3]));
            float half = Mathf.Max(0, Capsule.height * Mathf.Abs(scale[axis]) * .5f - radius);
            Vector3 direction = shapeRotation * (axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward);
            Vector3 centre = shapePoint + shapeRotation * Vector3.Scale(Capsule.center, scale);
            a = centre + direction * half; b = centre - direction * half;
        }
        private Vector3 ClearanceCorrection(Vector3 position, Quaternion rotation)
        {
            CapsulePose(position, rotation, out Vector3 shapePoint, out Quaternion shapeRotation,
                out Vector3 a, out Vector3 b, out float radius);
            Bounds nearby = CapsuleBounds(a, b, radius);
            float worst = .005f; Vector3 shift = Vector3.zero;
            foreach (var entry in shapes.Entries)
            {
                Shape shape = entry.Value; Collider floor = shape.Copy;
                if (IsEnabled(floor) && nearby.Intersects(shape.Bounds)
                    && UnityEngine.Physics.ComputePenetration(Capsule, shapePoint, shapeRotation,
                        floor, floor.transform.position, floor.transform.rotation, out Vector3 direction, out float distance)
                    && distance > worst)
                { worst = distance; shift = direction * (distance + .005f); clearanceBlocker = floor; clearanceDepth = distance; }
            }
            return shift;
        }
        private static Bounds CapsuleBounds(Vector3 a, Vector3 b, float radius)
            => new Bounds((a + b) * .5f, new Vector3(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z))
                + Vector3.one * (radius * 2f + .01f));
        private void SetPlayerPose(Vector3 position, Quaternion rotation)
        {
            Player.position = position; Player.rotation = rotation; Player.detectCollisions = true;
            Player.transform.SetPositionAndRotation(position, rotation); dirty = true; FlushTransforms();
        }
        private bool PlacementPath(Vector3 from, Vector3 to, Quaternion rotation)
        {
            Vector3 motion = to - from; float distance = motion.magnitude;
            if (distance < .001f) return true;
            CapsulePose(from, rotation, out Vector3 point, out Quaternion facing, out Vector3 a, out Vector3 b, out float radius);
            Bounds nearby = CapsuleBounds(a, b, radius);
            // A cast omits starting overlaps. Permit escape only in the outward
            // half-space of each starting penetration, never deeper through it.
            foreach (var entry in shapes.Entries)
            {
                Collider other = entry.Value.Copy;
                if (IsEnabled(other) && nearby.Intersects(entry.Value.Bounds) && UnityEngine.Physics.ComputePenetration(Capsule, point, facing, other,
                    other.transform.position, other.transform.rotation, out Vector3 normal, out float depth)
                    && depth > .005f && Vector3.Dot(motion, normal) < -.005f) return false;
            }
            int count = Physics.CapsuleCast(a, b, radius, motion / distance, placementHits, distance, 1, QueryTriggerInteraction.Ignore);
            if (count == placementHits.Length) return false;
            for (int i = 0; i < count; i++)
                if (placementHits[i].distance < distance - .005f && Vector3.Dot(motion, placementHits[i].normal) < -.001f) return false;
            return true;
        }
        public bool? ClearBoarding(BoardingPlacement placement, float now)
        {
            if (placement.Expired(now)) return false;
            var timer = Stopwatch.StartNew();
            while (placement.TryCandidate(now, out NVector sample, out bool needsSupport))
            {
                Vector3 candidate = U(sample), origin = U(placement.Preferred);
                Quaternion rotation = new Quaternion(placement.Rotation.X, placement.Rotation.Y, placement.Rotation.Z, placement.Rotation.W);
                if (!Contains(sample, BoardingPlacement.GroupPadding)) clearanceFailure = "candidate outside docking group";
                else if (ClearExit(candidate, rotation))
                {
                    if (!Contains(N(Player.position), BoardingPlacement.GroupPadding)) clearanceFailure = "correction left docking group";
                    else if (needsSupport && !Grounded(.12f)) clearanceFailure = "nearby candidate has no floor support";
                    else if (!PlacementPath(needsSupport ? origin : candidate, Player.position, rotation)) clearanceFailure = "blocked route to candidate";
                    else return true;
                }
                if (timer.Elapsed.TotalMilliseconds >= IncrementalWork.MillisecondsPerSlice) break;
            }
            return null;
        }
        public bool Grounded(float distance = .12f)
        {
            Bounds b = Capsule.bounds;
            bool found = Physics.Raycast(new Vector3(b.center.x, b.min.y + .06f, b.center.z), Vector3.down,
                out RaycastHit hit, distance + .06f, 1, QueryTriggerInteraction.Ignore) && hit.normal.y >= .55f;
            support = found ? hit.collider : null;
            return found;
        }
        // Diagnostic once per trace sample, not another per-step shape pass.
        public string SupportAlignment()
        {
            if (support == null) return "support=none";
            foreach (var entry in shapes.Entries)
            {
                if (entry.Value.Copy != support || entry.Key == null) continue;
                Collider source = entry.Key;
                Matrix4x4 expected = Matrix4x4.TRS(hull.position, hull.rotation, Vector3.one).inverse
                    * source.transform.localToWorldMatrix;
                Matrix4x4 actual = support.transform.localToWorldMatrix;
                float error = 0f;
                foreach (Vector3 p in new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward })
                    error = Mathf.Max(error, Vector3.Distance(expected.MultiplyPoint3x4(p), actual.MultiplyPoint3x4(p)));
                return "support=" + source.GetType().Name + ":" + source.name + ":" + source.GetInstanceID()
                    + "; supportPoseError=" + error.ToString("F4");
            }
            return "support=removed";
        }
        private static bool IsEnabled(Collider c) => c != null && c.enabled && c.gameObject.activeInHierarchy;
        internal static bool Allows(Collider first, Collider second)
            => CollisionEligibility.Allows(Layers(first), Layers(second),
                !UnityEngine.Physics.GetIgnoreLayerCollision(first.gameObject.layer, second.gameObject.layer),
                UnityEngine.Physics.GetIgnoreCollision(first, second));
        private static CollisionLayers Layers(Collider c)
        {
            Rigidbody b = c.GetComponentInParent<Rigidbody>(true);
            return new CollisionLayers(c.gameObject.layer, c.includeLayers.value | (b == null ? 0 : b.includeLayers.value),
                c.excludeLayers.value | (b == null ? 0 : b.excludeLayers.value), c.layerOverridePriority);
        }

        // Native Collider.bounds is empty when disabled. Derive conservative
        // local bounds from shape data instead; vessel selection disables detail.
        private Bounds LocalBounds(Collider c)
        {
            Matrix4x4 matrix = Matrix4x4.TRS(hull.position, hull.rotation, Vector3.one).inverse * c.transform.localToWorldMatrix;
            Vector3 x = matrix.MultiplyVector(Vector3.right), y = matrix.MultiplyVector(Vector3.up), z = matrix.MultiplyVector(Vector3.forward);
            Vector3 center = Vector3.zero, extents = Vector3.zero;
            if (c is BoxCollider b) { center = b.center; extents = b.size * .5f; }
            else if (c is MeshCollider m) { center = m.sharedMesh.bounds.center; extents = m.sharedMesh.bounds.extents; }
            else
            {
                float scale = Mathf.Max(x.magnitude, Mathf.Max(y.magnitude, z.magnitude));
                if (c is SphereCollider sphere)
                    return new Bounds(matrix.MultiplyPoint3x4(sphere.center), Vector3.one * (2f * sphere.radius * scale));
                var capsule = (CapsuleCollider)c;
                Vector3 axis = capsule.direction == 0 ? x : capsule.direction == 1 ? y : z;
                Vector3 e = Vector3.one * (capsule.radius * scale)
                    + new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)) * Mathf.Max(0, capsule.height * .5f - capsule.radius);
                return new Bounds(matrix.MultiplyPoint3x4(capsule.center), e * 2f);
            }
            LocalVolume volume = LocalVolume.TransformBox(N(center), N(extents), N(matrix.MultiplyPoint3x4(Vector3.zero)), N(x), N(y), N(z));
            return new Bounds(U((volume.Min + volume.Max) * .5f), U(volume.Size));
        }
        private static NVector N(Vector3 v) => new NVector(v.x, v.y, v.z);
        private static Vector3 U(NVector v) => new Vector3(v.X, v.Y, v.Z);

        private static int ShapeFingerprint(Collider c)
        {
            unchecked
            {
                int h = c.GetInstanceID() * 397 ^ c.gameObject.layer;
                h = h * 397 ^ c.isTrigger.GetHashCode() ^ c.contactOffset.GetHashCode();
                h = h * 397 ^ c.includeLayers.value ^ c.excludeLayers.value ^ c.layerOverridePriority;
                h = h * 397 ^ (c.sharedMaterial == null ? 0 : c.sharedMaterial.GetInstanceID());
                if (c is BoxCollider b) return h ^ b.center.GetHashCode() ^ b.size.GetHashCode();
                if (c is SphereCollider s) return h ^ s.center.GetHashCode() ^ s.radius.GetHashCode();
                if (c is CapsuleCollider p) return h ^ p.center.GetHashCode() ^ p.radius.GetHashCode() ^ p.height.GetHashCode() ^ p.direction;
                if (c is MeshCollider m) return h ^ (m.sharedMesh == null ? 0 : m.sharedMesh.GetInstanceID()
                    ^ m.sharedMesh.vertexCount ^ m.sharedMesh.bounds.GetHashCode()) ^ m.convex.GetHashCode() ^ (int)m.cookingOptions;
                return h;
            }
        }
        private string Describe(Collider c)
        {
            var names = new Stack<string>();
            for (Transform t = c.transform; t != null; t = t.parent) { names.Push(t.name); if (t == hull) break; }
            return c.GetType().Name + "|id:" + c.GetInstanceID() + "|path:" + string.Join("/", names)
                + "|layer:" + c.gameObject.layer + "|enabled:" + c.enabled + "|active:" + c.gameObject.activeInHierarchy;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { preparation.Dispose(); refresh?.Dispose(); RestoreNativeShapes(); }
            finally
            {
                if (root != null) { root.SetActive(false); Object.Destroy(root); }
                if (playerObject != null) { playerObject.SetActive(false); Object.Destroy(playerObject); }
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }
    }
}
