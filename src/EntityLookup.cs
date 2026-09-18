namespace ShipWalk
{
    internal interface IEntityNodeAccess<T> where T : class
    {
        bool IsAlive(T node);
        T Parent(T node);
        object DirectEntity(T node);
        T ReferenceTarget(T node);
    }

    internal static class EntityLookup
    {
        // A cross-reference can point at a scene root without an entity component.
        // Probe its target once; a missing entity is an ordinary non-support contact.
        internal static object Resolve<T>(T start, IEntityNodeAccess<T> access) where T : class
        {
            for (T node = start; access.IsAlive(node); node = access.Parent(node))
            {
                object entity = access.DirectEntity(node);
                if (entity != null) return entity;
                T target = access.ReferenceTarget(node);
                if (!access.IsAlive(target)) continue;
                entity = access.DirectEntity(target);
                if (entity != null) return entity;
            }
            return null;
        }
    }
}
