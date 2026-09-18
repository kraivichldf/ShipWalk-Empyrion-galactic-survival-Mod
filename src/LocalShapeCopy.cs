using UnityEngine;

namespace ShipWalk
{
    internal static class LocalShapeCopy
    {
        public static void Copy(Collider source, Collider target)
        {
            target.sharedMaterial = source.sharedMaterial;
            target.contactOffset = source.contactOffset;
            if (source is BoxCollider box && target is BoxCollider b)
            { b.center = box.center; b.size = box.size; }
            else if (source is SphereCollider sphere && target is SphereCollider s)
            { s.center = sphere.center; s.radius = sphere.radius; }
            else if (source is CapsuleCollider capsule && target is CapsuleCollider c)
            { c.center = capsule.center; c.direction = capsule.direction; c.radius = capsule.radius; c.height = capsule.height; }
            else if (source is MeshCollider mesh && target is MeshCollider m)
            {
                m.convex = mesh.convex; m.cookingOptions = mesh.cookingOptions; m.sharedMesh = mesh.sharedMesh;
            }
        }
    }
}
