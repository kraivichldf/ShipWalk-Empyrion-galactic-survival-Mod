using System;
using System.Reflection;
using ShipWalk;

internal static class EntityLookupTests
{
    private sealed class EntityComponent { public object Entity; }
    private sealed class Node
    {
        public bool Alive = true;
        public Node Parent, Target;
        public EntityComponent Component;
        public Exception Failure;

        // Independent fixture of the reported assumption, not game code.
        public object UncheckedEntity => Target == null ? null : Target.Component.Entity;
    }

    private sealed class Access : IEntityNodeAccess<Node>
    {
        public bool IsAlive(Node node) => node != null && node.Alive;
        public Node Parent(Node node) => node.Parent;
        public object DirectEntity(Node node)
        {
            if (!IsAlive(node)) throw new Exception("Destroyed node was inspected.");
            return node.Component?.Entity;
        }
        public Node ReferenceTarget(Node node)
        {
            if (node.Failure != null) throw node.Failure;
            return node.Target;
        }
    }

    internal static void MissingComponentRegression()
    {
        var access = new Access();
        var contact = new Node { Target = new Node() };
        var getter = typeof(Node).GetProperty(nameof(Node.UncheckedEntity)).GetGetMethod();
        bool reproduced = false;
        try { getter.Invoke(contact, null); }
        catch (TargetInvocationException error) { reproduced = error.InnerException is NullReferenceException; }
        Check(reproduced, "Missing-component baseline did not reproduce the reflected null reference.");
        for (int i = 0; i < 1000; i++)
            Check(EntityLookup.Resolve(contact, access) == null, "A non-entity collider acquired support.");
        var entity = new object();
        contact.Target.Component = new EntityComponent { Entity = entity };
        Check(ReferenceEquals(getter.Invoke(contact, null), entity), "Valid baseline failed.");
        Check(ReferenceEquals(EntityLookup.Resolve(contact, access), entity), "Valid XRef entity was lost.");
        contact.Target = null;
        Check(getter.Invoke(contact, null) == null && EntityLookup.Resolve(contact, access) == null, "Null target failed.");
    }

    internal static void HierarchyAndLifetime()
    {
        var access = new Access();
        var entity = new object();
        var root = new Node { Component = new EntityComponent { Entity = entity } };
        var contact = new Node { Parent = root, Target = new Node() };
        Check(ReferenceEquals(EntityLookup.Resolve(contact, access), entity), "Missing XRef component hid a valid parent.");
        contact.Component = new EntityComponent();
        Check(ReferenceEquals(EntityLookup.Resolve(contact, access), entity), "Unbound child component hid a valid parent.");
        contact.Target = new Node { Alive = false };
        Check(ReferenceEquals(EntityLookup.Resolve(contact, access), entity), "Destroyed target hid a valid parent.");
        contact.Alive = false;
        Check(EntityLookup.Resolve(contact, access) == null, "Destroyed start was resolved.");
        Check(EntityLookup.Resolve<Node>(null, access) == null, "Null start was resolved.");
        contact.Alive = true;
        contact.Target = contact; // Probe once without recursively following reference cycles.
        Check(ReferenceEquals(EntityLookup.Resolve(contact, access), entity), "Self reference blocked parent traversal.");
        var nearest = new object();
        contact.Component.Entity = nearest;
        Check(ReferenceEquals(EntityLookup.Resolve(contact, access), nearest), "Nearest valid entity was skipped.");
    }

    internal static void UnexpectedFailurePropagates()
    {
        var expected = new InvalidOperationException("unexpected reference getter failure");
        var contact = new Node { Failure = expected };
        try { EntityLookup.Resolve(contact, new Access()); }
        catch (InvalidOperationException error)
        {
            Check(ReferenceEquals(error, expected), "Failure was replaced.");
            return;
        }
        throw new Exception("Unexpected lookup failure was silently ignored.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
