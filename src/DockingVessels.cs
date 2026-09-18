using System;
using System.Collections.Generic;
using Eleon.Modding;
using UnityEngine;
using NV = System.Numerics.Vector3;
using NQ = System.Numerics.Quaternion;

namespace ShipWalk
{
    internal static class DockingVessels
    {
        internal static bool Valid(Build5150 map, object ship) => ship != null && map.ShipType.IsInstanceOfType(ship)
            && LocalFrameMath.IsVessel(map.Entity(ship)?.Type.ToString())
            && map.EntityTransform.GetValue(ship) is Transform hull && hull != null;
        internal static object Root(Build5150 map, object member)
            => DockingTopology.Root(member, x => map.DockedTo.GetValue(x), x => Valid(map, x));
        internal static bool Member(Build5150 map, object root, object member)
            => root != null && ReferenceEquals(root, Root(map, member));
        internal static IEnumerable<object> Group(Build5150 map, object root, IEnumerable<IEntity> entities)
        {
            if (root == null) yield break;
            yield return root;
            if (entities == null) yield break;
            foreach (IEntity entity in entities)
            {
                if (entity == null || entity.Id == map.Id(root) || !LocalFrameMath.IsVessel(entity.Type.ToString())) continue;
                object member = map.NativeEntity(entity);
                if (Member(map, root, member)) yield return member;
            }
        }
        internal static LocalFramePose Pose(Build5150 map, object ship, bool native = false)
        {
            var hull = (Transform)map.EntityTransform.GetValue(ship);
            Vector3 p = native ? (Vector3)map.EntityPosition.GetValue(ship) : hull.position + map.OriginOffset;
            Quaternion q = native ? map.Entity(ship).Rotation : hull.rotation;
            return new LocalFramePose(N(p), new NQ(q.x, q.y, q.z, q.w));
        }
        internal static bool Contains(Build5150 map, object frame, object member, NV point, float padding)
        {
            if (!Valid(map, member) || map.Entity(member)?.Structure?.IsReady != true) return false;
            NV local = point;
            if (!ReferenceEquals(frame, member))
            {
                // Docked entities need not advance their independent cached
                // world position. Their actual relative transforms define the
                // same attachment on the client and authoritative worker.
                var parent = (Transform)map.EntityTransform.GetValue(frame);
                var child = (Transform)map.EntityTransform.GetValue(member);
                Quaternion q = Quaternion.Inverse(parent.rotation) * child.rotation;
                var relative = new LocalFramePose(N(Quaternion.Inverse(parent.rotation) * (child.position - parent.position)), new NQ(q.x, q.y, q.z, q.w));
                local = relative.ToLocalPoint(point);
            }
            return LocalFrameSession.ReadBounds(map.Entity(member).Structure).Contains(local, padding);
        }
        internal static Vector3 Linear(Build5150 map, object ship, bool network)
        {
            var b = map.EntityBody.GetValue(ship) as Rigidbody;
            return !network && b != null && b.gameObject.activeInHierarchy && !b.isKinematic
                ? b.velocity : (Vector3)map.EntityVelocity.GetValue(ship);
        }
        internal static Vector3 Angular(Build5150 map, object ship, bool network)
        {
            var b = map.EntityBody.GetValue(ship) as Rigidbody;
            // Remote inactive bodies contain old physics. Their angular rate is
            // sampled from presentation by the session instead.
            return !network && b != null && b.gameObject.activeInHierarchy && !b.isKinematic ? b.angularVelocity : Vector3.zero;
        }
        private static NV N(Vector3 p) => new NV(p.x, p.y, p.z);
    }
}
