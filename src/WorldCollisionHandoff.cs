using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using NV = System.Numerics.Vector3;

namespace ShipWalk
{
    // The isolated interior contains no terrain. Before publishing airborne
    // movement, sweep the character through its native scene and hand control
    // back on the near side of an external solid. Never ignore world pairs.
    internal sealed class WorldCollisionHandoff
    {
        private RaycastHit[] hits = new RaycastHit[32];
        private Collider[] overlaps = new Collider[32];
        public bool TryContact(Build5150 map, object ship, Transform hull, CapsuleCollider native,
            CapsuleCollider proxy, LocalFramePose frame, Vector3 from, Vector3 to,
            out Vector3 safePoint, out string reason, Func<Collider, bool> ownGroup = null)
        {
            safePoint = to; reason = null;
            PhysicsScene scene = native.gameObject.scene.GetPhysicsScene();
            Quaternion rotation = new Quaternion(frame.Rotation.X, frame.Rotation.Y, frame.Rotation.Z, frame.Rotation.W);
            Vector3 origin = U(frame.Position) - map.OriginOffset;
            Vector3 scale = proxy.transform.lossyScale;
            int axisIndex = proxy.direction;
            Vector3 axis = axisIndex == 0 ? Vector3.right : axisIndex == 1 ? Vector3.up : Vector3.forward;
            float axisScale = Mathf.Abs(scale[axisIndex]);
            float radius = proxy.radius * Mathf.Max(Mathf.Abs(scale[(axisIndex + 1) % 3]), Mathf.Abs(scale[(axisIndex + 2) % 3]));
            float half = Mathf.Max(0f, proxy.height * axisScale * .5f - radius);
            axis = rotation * proxy.transform.rotation * axis;
            Vector3 centre = origin + rotation * proxy.transform.TransformPoint(proxy.center);
            Vector3 motion = to - from;
            Vector3 a = centre + axis * half - motion, b = centre - axis * half - motion;
            Vector3 shapePosition = origin + rotation * proxy.transform.position - motion;
            Quaternion shapeRotation = rotation * proxy.transform.rotation;
            int count;
            while ((count = scene.OverlapCapsule(a, b, radius, overlaps, ~0, QueryTriggerInteraction.Ignore)) == overlaps.Length)
            {
                if (overlaps.Length >= 1024) { safePoint = from; reason = "world overlap query saturated"; return true; }
                Array.Resize(ref overlaps, overlaps.Length * 2);
            }
            float deepest = .005f;
            // Casts do not report starting overlaps. Resolve a small existing
            // overlap as well, e.g. native terrain enabled beside a parked ship.
            for (int i = 0; i < count; i++)
            {
                Collider other = overlaps[i];
                if (!External(map, ship, hull, native, other, ownGroup)) continue;
                if (Physics.ComputePenetration(proxy, shapePosition, shapeRotation, other,
                    other.transform.position, other.transform.rotation, out Vector3 normal, out float depth) && depth > deepest)
                { deepest = depth; safePoint = from + normal * (depth + WorldDeparture.Clearance); reason = other.GetType().Name + ":" + other.name + ":overlap"; }
            }
            if (reason != null) return true;
            float distance = motion.magnitude;
            if (distance <= .00001f) return false;
            Vector3 direction = motion / distance;
            while ((count = scene.CapsuleCast(a, b, radius, direction, hits, distance + WorldDeparture.Clearance,
                ~0, QueryTriggerInteraction.Ignore)) == hits.Length)
            {
                if (hits.Length >= 1024) { safePoint = from; reason = "world sweep query saturated"; return true; }
                Array.Resize(ref hits, hits.Length * 2);
            }
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.distance >= nearest || !External(map, ship, hull, native, hit.collider, ownGroup)
                    || Vector3.Dot(direction, hit.normal) >= 0f) continue;
                nearest = hit.distance;
                safePoint = U(WorldDeparture.BeforeContact(N(from), N(to), hit.distance));
                reason = hit.collider.GetType().Name + ":" + hit.collider.name + ":sweep";
            }
            return reason != null;
        }
        private static bool External(Build5150 map, object ship, Transform hull, CapsuleCollider native, Collider other, Func<Collider, bool> ownGroup)
        {
            if (other == null || other == native || other.isTrigger || !other.enabled || !other.gameObject.activeInHierarchy
                || (ownGroup != null ? ownGroup(other) : other.transform.IsChildOf(hull))
                || other.attachedRigidbody != null && other.attachedRigidbody == native.attachedRigidbody
                || ReferenceEquals(map.ResolveEntity(other), ship)) return false;
            return LocalGeometry.Allows(native, other);
        }
        private static Vector3 U(NV v) => new Vector3(v.X, v.Y, v.Z);
        private static NV N(Vector3 v) => new NV(v.x, v.y, v.z);
    }
}
