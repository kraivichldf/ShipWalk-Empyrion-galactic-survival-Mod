using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using Object = UnityEngine.Object;

namespace ShipWalk
{
    // Collision-only, local-player interior. Never parent it to the render hull:
    // that hull is moved in Update and was the stationary surface in the trace.
    internal sealed class MovingInterior : IDisposable
    {
        private readonly Build5150 map;
        private readonly TraceLog log;
        private readonly Dictionary<Transform, Transform> nodes = new Dictionary<Transform, Transform>();
        private readonly Dictionary<Collider, Collider> copies = new Dictionary<Collider, Collider>();
        private readonly CollisionPairs<Collider> originals = new CollisionPairs<Collider>(c => c != null,
            c => c.enabled && c.gameObject.activeInHierarchy, Physics.GetIgnoreCollision, Physics.IgnoreCollision);
        private Collider[] playerShapes = Array.Empty<Collider>();
        private readonly HashSet<Collider> eligiblePlayers = new HashSet<Collider>();
        private readonly HashSet<Collider> eligibleCopies = new HashSet<Collider>();
        private GameObject root;
        private Rigidbody body, shipBody, playerBody;
        private Transform hull;
        private object ship;
        private Vector3 origin;
        private InteriorStep step;
        private float plannedAt = float.NegativeInfinity;
        private volatile InteriorContactFilter filter;
        private bool subscribed;
        public bool Active => body != null && root != null && root.activeInHierarchy;
        public object Ship => ship;
        public Rigidbody Body => body;
        public int Count => copies.Count;

        public MovingInterior(Build5150 map, TraceLog log) { this.map = map; this.log = log; }
        public bool Owns(Collider collider) => Active && collider != null && collider.attachedRigidbody == body;
        public bool Supports(Collider collider) => Owns(collider) && Live(collider) && eligibleCopies.Contains(collider);
        public bool CanContact(Collider player, Collider copy) => player != null && Supports(copy)
            && player.attachedRigidbody == playerBody && filter != null && filter.Allows(copy.GetInstanceID(), player.GetInstanceID());
        public bool Matches(object entity, Rigidbody player) => Active && ReferenceEquals(ship, entity) && playerBody == player;

        public bool Begin(object entity, Rigidbody player, bool seatExit)
        {
            if (Matches(entity, player)) return true;
            Reset();
            var sourceBody = (Rigidbody)map.EntityBody.GetValue(entity);
            var sourceHull = (Transform)map.EntityTransform.GetValue(entity);
            // Remote snapshot timing needs multiplayer acceptance. Do not invent
            // velocity from an inactive client's stale ship Rigidbody.
            if (sourceBody == null || sourceHull == null || player == null || sourceBody.isKinematic
                || !sourceBody.gameObject.activeInHierarchy || map.Bool(map.EntityRemote, entity)
                || map.DockedTo.GetValue(entity) != null || sourceBody.gameObject.scene != player.gameObject.scene) return false;
            Vector3 originalPosition = player.position;
            Quaternion originalRotation = player.rotation;
            try
            {
                ship = entity; shipBody = sourceBody; playerBody = player; hull = sourceHull;
                origin = map.OriginOffset;
                root = new GameObject("ShipWalk interior " + map.Id(ship));
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, player.gameObject.scene);
                root.transform.SetPositionAndRotation(shipBody.position, shipBody.rotation);
                body = root.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.centerOfMass = Vector3.zero;
                filter = new InteriorContactFilter(body.GetInstanceID(), player.GetInstanceID(), Array.Empty<ulong>());
                Physics.ContactModifyEvent += FilterContacts;
                Physics.ContactModifyEventCCD += FilterContacts;
                subscribed = true;
                RefreshGeometry();
                if (eligibleCopies.Count == 0)
                    throw new InvalidOperationException("No CV interior geometry eligible for the local player's native collision shapes.");
                root.SetActive(true);
                Physics.SyncTransforms();
                if (seatExit)
                {
                    // Native exit uses the visual hull's pose. Carry exactly that
                    // local exit point/orientation into the physics pose once.
                    Vector3 position = U(InteriorStep.MapPoint(N(player.position), N(hull.position), N(hull.rotation),
                        N(body.position), N(body.rotation)));
                    Quaternion rotation = body.rotation * Quaternion.Inverse(hull.rotation) * player.rotation;
                    if (!ClearExit(ref position, rotation))
                        throw new InvalidOperationException("No clear capsule pose within 1 m of the native seat exit.");
                    player.position = position;
                    player.rotation = rotation;
                    Physics.SyncTransforms();
                }
                RefreshIgnores();
                log.Info("Moving interior ready on CV " + map.Id(ship) + "; shapes=" + copies.Count
                    + "; eligiblePlayerShapes=" + eligiblePlayers.Count + "/" + playerShapes.Length
                    + "; eligiblePairs=" + filter.Count
                    + "; playerOnly=" + player.GetInstanceID() + "; exitCorrection="
                    + (player.position - originalPosition).magnitude.ToString("F3") + " m.");
                return true;
            }
            catch
            {
                player.position = originalPosition;
                player.rotation = originalRotation;
                Reset();
                throw;
            }
        }

        private void FilterContacts(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            InteriorContactFilter snapshot = filter;
            if (snapshot == null) return;
            // No GameObject, Transform, logging or mutable collections on workers.
            for (int i = 0; i < pairs.Length; i++)
            {
                ModifiableContactPair pair = pairs[i];
                if (!snapshot.Reject(pair.bodyInstanceID, pair.otherBodyInstanceID,
                    pair.colliderInstanceID, pair.otherColliderInstanceID)) continue;
                for (int j = 0; j < pair.contactCount; j++) pair.IgnoreContact(j);
            }
        }

        private Transform Mirror(Transform source)
        {
            if (nodes.TryGetValue(source, out Transform result)) return result;
            Transform parent = source == hull ? root.transform : Mirror(source.parent);
            var node = new GameObject(source.name + " [ShipWalk collision]");
            node.layer = 2; // Ignore Raycast: regular game use/weapon queries keep the native hull.
            result = node.transform;
            result.SetParent(parent, false);
            nodes.Add(source, result);
            return result;
        }

        private bool Detailed(Collider collider) => collider != null && !collider.isTrigger
            && collider.attachedRigidbody == null && !collider.CompareTag("T_BB")
            && (copies.ContainsKey(collider) || ReferenceEquals(map.ResolveEntity(collider), ship));

        private static bool Supported(Collider collider) => collider is BoxCollider || collider is SphereCollider
            || collider is CapsuleCollider || collider is MeshCollider;

        private void RefreshGeometry()
        {
            playerShapes = playerBody.GetComponentsInChildren<Collider>(true)
                .Where(c => c != null && c.attachedRigidbody == playerBody && !c.isTrigger).ToArray();
            if (playerShapes.Length == 0) throw new InvalidOperationException("Local player has no collision shapes.");
            var playerLayers = playerShapes.ToDictionary(c => c, Layers);
            var matrix = new bool?[32, 32];
            var allowed = new List<ulong>();
            eligiblePlayers.Clear(); eligibleCopies.Clear();
            var found = new HashSet<Collider>();
            foreach (Collider source in hull.GetComponentsInChildren<Collider>(true))
            {
                if (!Detailed(source)) continue;
                if (!Supported(source)) throw new NotSupportedException("Unsupported interior shape: " + source.GetType().Name);
                found.Add(source);
                if (!copies.TryGetValue(source, out Collider copy))
                {
                    copy = (Collider)Mirror(source.transform).gameObject.AddComponent(source.GetType());
                    copy.enabled = false;
                    copy.hasModifiableContacts = true;
                    copies.Add(source, copy);
                }
                // Derive each copy's mask from its original surface's eligibility.
                // The former union of every player layer enabled camera/model
                // contacts that the native hull deliberately excludes.
                int layers = 0;
                CollisionLayers sourceLayers = Layers(source);
                foreach (var entry in playerLayers)
                {
                    Collider player = entry.Key;
                    if (!Live(player) || !Live(source)) continue;
                    CollisionLayers candidate = entry.Value;
                    bool matrixAllows = matrix[candidate.Layer, sourceLayers.Layer]
                        ?? (matrix[candidate.Layer, sourceLayers.Layer] = !Physics.GetIgnoreLayerCollision(
                            candidate.Layer, sourceLayers.Layer)).Value;
                    bool originalIgnore = originals.OriginalIgnore(player, source) ?? Physics.GetIgnoreCollision(player, source);
                    if (!CollisionEligibility.Allows(candidate, sourceLayers, matrixAllows, originalIgnore)) continue;
                    layers |= 1 << candidate.Layer;
                    allowed.Add(InteriorContactFilter.Pair(copy.GetInstanceID(), player.GetInstanceID()));
                    eligiblePlayers.Add(player); eligibleCopies.Add(copy);
                }
                // Broadphase removes other layers. The immutable exact-pair
                // filter and per-pair ignores also isolate same-layer shapes.
                if (copy.includeLayers.value != layers) copy.includeLayers = layers;
                if (copy.excludeLayers.value != ~layers) copy.excludeLayers = ~layers;
                if (copy.layerOverridePriority != 100) copy.layerOverridePriority = 100;
                CopyShape(source, copy);
                bool enabled = source.enabled && source.gameObject.activeInHierarchy;
                if (copy.enabled != enabled) copy.enabled = enabled;
            }
            foreach (Collider source in copies.Keys.Where(c => !found.Contains(c)).ToArray())
            {
                Collider copy = copies[source];
                if (copy != null) { copy.enabled = false; Object.Destroy(copy); }
                copies.Remove(source);
            }
            // Keep the ancestor chain: flattening lossyScale breaks rotated,
            // nonuniformly scaled doors/cockpit shapes and mirrored geometry.
            foreach (var entry in nodes.ToArray())
            {
                Transform source = entry.Key, copy = entry.Value;
                if (source == null)
                {
                    if (copy != null) { copy.gameObject.SetActive(false); Object.Destroy(copy.gameObject); }
                    nodes.Remove(source);
                    continue;
                }
                if (copy == null) { nodes.Remove(source); continue; }
                Transform parent = source == hull ? root.transform : Mirror(source.parent);
                if (copy.parent != parent) copy.SetParent(parent, false);
                Vector3 position = source == hull ? Vector3.zero : source.localPosition;
                Quaternion rotation = source == hull ? Quaternion.identity : source.localRotation;
                Vector3 scale = source == hull ? source.lossyScale : source.localScale;
                if (copy.localPosition != position) copy.localPosition = position;
                if (copy.localRotation != rotation) copy.localRotation = rotation;
                if (copy.localScale != scale) copy.localScale = scale;
            }
            filter = new InteriorContactFilter(body.GetInstanceID(), playerBody.GetInstanceID(), allowed);
        }

        private static CollisionLayers Layers(Collider shape)
        {
            Rigidbody owner = shape.attachedRigidbody;
            return new CollisionLayers(shape.gameObject.layer,
                shape.includeLayers.value | (owner != null ? owner.includeLayers.value : 0),
                shape.excludeLayers.value | (owner != null ? owner.excludeLayers.value : 0), shape.layerOverridePriority);
        }

        internal static void CopyShape(Collider source, Collider copy)
        {
            if (copy.sharedMaterial != source.sharedMaterial) copy.sharedMaterial = source.sharedMaterial;
            if (copy.contactOffset != source.contactOffset) copy.contactOffset = source.contactOffset;
            if (source is BoxCollider box && copy is BoxCollider boxCopy)
            {
                if (boxCopy.center != box.center) boxCopy.center = box.center;
                if (boxCopy.size != box.size) boxCopy.size = box.size;
            }
            else if (source is SphereCollider sphere && copy is SphereCollider sphereCopy)
            {
                if (sphereCopy.center != sphere.center) sphereCopy.center = sphere.center;
                if (sphereCopy.radius != sphere.radius) sphereCopy.radius = sphere.radius;
            }
            else if (source is CapsuleCollider capsule && copy is CapsuleCollider capsuleCopy)
            {
                if (capsuleCopy.center != capsule.center) capsuleCopy.center = capsule.center;
                if (capsuleCopy.direction != capsule.direction) capsuleCopy.direction = capsule.direction;
                if (capsuleCopy.radius != capsule.radius) capsuleCopy.radius = capsule.radius;
                if (capsuleCopy.height != capsule.height) capsuleCopy.height = capsule.height;
            }
            else if (source is MeshCollider mesh && copy is MeshCollider meshCopy)
            {
                // Concave geometry is valid on this kinematic body. Convexifying
                // the hull would seal rooms/corridors and eject the occupant.
                if (meshCopy.convex != mesh.convex) meshCopy.convex = mesh.convex;
                if (meshCopy.cookingOptions != mesh.cookingOptions) meshCopy.cookingOptions = mesh.cookingOptions;
                if (meshCopy.sharedMesh != mesh.sharedMesh) meshCopy.sharedMesh = mesh.sharedMesh;
            }
        }

        private void RefreshIgnores()
        {
            // Ignore a native surface only after its replacement exists. Keep
            // deliberate pre-existing pair exclusions on the corresponding copy.
            foreach (var entry in copies)
                foreach (Collider player in playerShapes)
                {
                    if (!Live(player) || !Live(entry.Key) || !Live(entry.Value)) continue;
                    bool ignore = !filter.Allows(entry.Value.GetInstanceID(), player.GetInstanceID());
                    if (Physics.GetIgnoreCollision(player, entry.Value) != ignore)
                        Physics.IgnoreCollision(player, entry.Value, ignore);
                }
            Collider[] bounds = shipBody.GetComponentsInChildren<Collider>(true)
                .Where(c => c != null && c.attachedRigidbody == shipBody && !c.isTrigger).ToArray();
            originals.Refresh(playerShapes, copies.Keys.Concat(bounds));
        }

        private bool ClearExit(ref Vector3 position, Quaternion rotation)
        {
            Vector3 start = position;
            Quaternion turn = rotation * Quaternion.Inverse(playerBody.rotation);
            Collider[] shapes = eligiblePlayers.Where(Live).ToArray();
            if (shapes.Length == 0 || shapes.Any(c => !(c is BoxCollider || c is SphereCollider
                || c is CapsuleCollider || c is MeshCollider mesh && mesh.convex))) return false;
            for (int pass = 0; pass < 8; pass++)
            {
                float worst = .01f;
                Vector3 correction = Vector3.zero;
                foreach (Collider player in shapes)
                {
                    Vector3 at = position + turn * (player.transform.position - playerBody.position);
                    Quaternion orientation = turn * player.transform.rotation;
                    float radius = player.bounds.extents.magnitude
                        + (player.bounds.center - player.transform.position).magnitude;
                    foreach (var entry in copies)
                    {
                        Collider floor = entry.Value;
                        if (!CanContact(player, floor)
                            || floor.bounds.SqrDistance(at) > radius * radius) continue;
                        if (Physics.ComputePenetration(player, at, orientation, floor, floor.transform.position,
                            floor.transform.rotation, out Vector3 direction, out float distance) && distance > worst)
                        { worst = distance; correction = direction * (distance + .003f); }
                    }
                }
                if (correction == Vector3.zero) return true;
                position += correction;
                if ((position - start).sqrMagnitude > 1f) return false;
            }
            return false;
        }

        public void RebaseOrigin()
        {
            if (!Active) return;
            Vector3 current = map.OriginOffset;
            if (current == origin) return;
            body.position += origin - current;
            origin = current;
            if (step != null && plannedAt == Time.fixedTime)
            { body.MovePosition(U(step.Target) - origin); body.MoveRotation(U(step.TargetRotation)); }
        }

        public bool Advance()
        {
            if (!Active) return false;
            if (shipBody == null || hull == null || playerBody == null || !hull.gameObject.activeInHierarchy
                || shipBody.isKinematic || !shipBody.gameObject.activeInHierarchy || map.Bool(map.EntityRemote, ship)
                || map.DockedTo.GetValue(ship) != null || !ReferenceEquals(map.EntityBody.GetValue(ship), shipBody)
                || body.gameObject.scene != playerBody.gameObject.scene) { Reset(); return false; }
            RebaseOrigin();
            if (plannedAt == Time.fixedTime) return true;
            RefreshGeometry();
            Physics.SyncTransforms();
            RefreshIgnores();
            if (!InteriorStep.TryPlan(N(body.position + origin), N(body.rotation), N(shipBody.position + origin),
                N(shipBody.rotation), N(shipBody.worldCenterOfMass + origin), N(shipBody.velocity),
                N(shipBody.angularVelocity), Time.fixedDeltaTime, out step))
            { Reset(); return false; }
            body.MovePosition(U(step.Target) - origin);
            body.MoveRotation(U(step.TargetRotation));
            plannedAt = Time.fixedTime;
            return true;
        }

        public bool TryVelocity(Vector3 point, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!Active || step == null) return false;
            // Late landing queries run after the solve: use the current proxy
            // root, not the previous step's root, for the angular lever arm.
            velocity = U(step.PointVelocity(N(point + map.OriginOffset), N(body.position + map.OriginOffset)));
            return MotionMath.Finite(N(velocity)) && velocity.sqrMagnitude <= 300f * 300f;
        }

        public bool Raycast(Ray ray, float distance, out RaycastHit hit)
        {
            hit = default;
            if (!Active) return false;
            bool found = false;
            // Direct shape raycasts support non-convex meshes and cannot hit the
            // duplicate native hull or our ignored ship bounding colliders.
            foreach (Collider collider in eligibleCopies)
                if (Live(collider) && collider.Raycast(ray, out RaycastHit candidate, distance)
                    && (!found || candidate.distance < hit.distance))
                { hit = candidate; found = true; }
            return found;
        }

        private static bool Live(Collider c) => c != null && c.enabled && c.gameObject.activeInHierarchy;
        public void Reset()
        {
            if (root != null) root.SetActive(false);
            // Disable copies before restoring native pairs; Destroy is deferred.
            try { originals.Reset(); }
            finally
            {
                if (subscribed)
                { Physics.ContactModifyEvent -= FilterContacts; Physics.ContactModifyEventCCD -= FilterContacts; }
                subscribed = false;
                filter = null;
                if (root != null) Object.Destroy(root);
                root = null; body = shipBody = playerBody = null; hull = null; ship = null;
                step = null; plannedAt = float.NegativeInfinity;
                nodes.Clear(); copies.Clear(); playerShapes = Array.Empty<Collider>();
                eligiblePlayers.Clear(); eligibleCopies.Clear();
            }
        }
        public void Dispose() => Reset();
        private static NVector N(Vector3 v) => new NVector(v.x, v.y, v.z);
        private static NQuaternion N(Quaternion q) => new NQuaternion(q.x, q.y, q.z, q.w);
        private static Vector3 U(NVector v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion U(NQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
