using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipWalk
{
    internal sealed class Coast
    {
        public object Ship;
        public Rigidbody Body;
        public PhysicsActivity Activity;
        public bool Confirmed, RefreshPairs = true;
        public float Started, LastReport, RestSince = -1f;
        public int Reports, PairReports;
        public Vector3 LastPosition;
        public readonly CollisionPairs<Collider> Pairs = new CollisionPairs<Collider>(c => c != null,
            c => c.enabled && c.gameObject.activeInHierarchy, Physics.GetIgnoreCollision, Physics.IgnoreCollision);
    }

    // Leases ONLY the body's activation after an eligible moving pilot exit.
    // Native authority, kinematic state, forces, collisions and pose publication remain in charge.
    internal sealed class CoastingPhysics
    {
        private readonly Build5150 map;
        private readonly TraceLog log;
        private readonly Func<bool> enabled;
        private readonly Dictionary<object, Coast> ships = new Dictionary<object, Coast>();
        public CoastingPhysics(Build5150 map, TraceLog log, Func<bool> enabled)
        { this.map = map; this.log = log; this.enabled = enabled; }

        public void Begin(object ship, Rigidbody body)
        {
            if (ships.ContainsKey(ship)) return;
            ships.Add(ship, new Coast { Ship = ship, Body = body, Activity = new PhysicsActivity(body.gameObject.activeSelf),
                Started = Time.time, LastReport = Time.time, LastPosition = body.position + map.OriginOffset });
        }

        public void Confirm(object ship)
        {
            if (ships.TryGetValue(ship, out var coast)) coast.Confirmed = true;
        }

        private Coast Find(object ship, GameObject root = null)
        {
            if (!ships.TryGetValue(ship, out var coast)) return null;
            if (!enabled() || coast.Body == null || !ReferenceEquals(map.EntityBody.GetValue(ship), coast.Body)
                || map.DockedTo.GetValue(ship) != null || map.Bool(map.EntityRemote, ship)
                || (coast.Confirmed && map.HasPilot(ship))
                || (!coast.Confirmed && Time.time - coast.Started > ExitMomentum.Lifetime))
            { Cancel(ship); return null; }
            return root == null || coast.Body.gameObject == root ? coast : null;
        }

        public bool ReadActive(GameObject root, object ship)
        {
            var coast = Find(ship, root);
            return coast != null ? coast.Activity.Requested : root.activeSelf;
        }

        public void SetActive(GameObject root, bool active, object ship)
        {
            var coast = Find(ship, root);
            if (coast == null) { root.SetActive(active); return; }
            coast.RefreshPairs = true;
            root.SetActive(coast.Activity.Request(active));
        }

        public void AfterSelection(object ship)
        {
            var coast = Find(ship);
            if (coast == null) return;
            // The selector may have early-returned without reaching SetActive.
            if (!coast.Body.gameObject.activeSelf)
            { coast.Body.gameObject.SetActive(true); coast.RefreshPairs = true; }
            if (!coast.RefreshPairs) return;
            RefreshPairs(coast);
            coast.RefreshPairs = false;
        }

        private void RefreshPairs(Coast coast)
        {
            Transform visual = (Transform)map.EntityTransform.GetValue(coast.Ship);
            if (visual == null) { Cancel(coast.Ship); return; }
            // Bounding colliders and detailed hull colliders normally alternate.
            // While both are active, suppress ONLY collisions between this body's
            // bounds and its own separate hull; terrain/other ships/players stay native.
            var bounds = coast.Body.GetComponentsInChildren<Collider>(true)
                .Where(c => c != null && c.attachedRigidbody == coast.Body && !c.isTrigger).ToArray();
            var hull = visual.GetComponentsInChildren<Collider>(true)
                .Where(c => c != null && c.attachedRigidbody != coast.Body && !c.isTrigger
                    && !c.CompareTag("T_BB") && ReferenceEquals(map.ResolveEntity(c), coast.Ship)).ToArray();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            coast.Pairs.Refresh(bounds, hull);
            timer.Stop();
            if (coast.PairReports++ < 4)
                log.Info("Coasting collision setup ship " + map.Id(coast.Ship) + "; bodyColliders=" + bounds.Length
                    + "; hullColliders=" + hull.Length + "; pairs=" + coast.Pairs.Count
                    + "; writes=" + coast.Pairs.LastWrites + "; maskKiB=" + (coast.Pairs.SnapshotBytes / 1024f).ToString("F1")
                    + "; elapsedMs=" + timer.ElapsedMilliseconds + ".");
        }

        public void Cancel(object ship)
        {
            if (ship == null || !ships.TryGetValue(ship, out var coast)) return;
            ships.Remove(ship);
            // Restore the selector's requested activation before restoring own-hull
            // contacts, so two representations cannot push against each other.
            if (coast.Body != null) coast.Body.gameObject.SetActive(coast.Activity.Requested);
            coast.Pairs.Reset();
        }

        public void Update(object ship)
        {
            var coast = Find(ship);
            if (coast == null || !coast.Confirmed) return;
            // Rest is measured for a full second; collisions may legitimately stop motion.
            bool resting = coast.Body.IsSleeping() || coast.Body.isKinematic
                || (coast.Body.velocity.sqrMagnitude < .0001f && coast.Body.angularVelocity.sqrMagnitude < .0001f);
            if (!resting) coast.RestSince = -1f;
            else if (coast.RestSince < 0f) coast.RestSince = Time.time;
            else if (Time.time - coast.RestSince >= 1f) { Cancel(ship); return; }
            if (coast.Reports >= 12 || Time.time - coast.LastReport < 0.5f) return;
            Vector3 position = coast.Body.position + map.OriginOffset;
            log.Info("Coasting ship " + map.Id(ship) + "; moved=" + Vector3.Distance(position, coast.LastPosition).ToString("F2")
                + " m/" + (Time.time - coast.LastReport).ToString("F2") + " s; speed=" + coast.Body.velocity.magnitude.ToString("F2")
                + " m/s; active=" + coast.Body.gameObject.activeInHierarchy + "; kinematic=" + coast.Body.isKinematic
                + "; nativeBounds=" + coast.Activity.Requested + "; ownPairs=" + coast.Pairs.Count + ".");
            coast.LastReport = Time.time; coast.LastPosition = position; coast.Reports++;
        }

        public void Update() { foreach (object ship in ships.Keys.ToArray()) Update(ship); }
        public void Reset() { foreach (object ship in ships.Keys.ToArray()) Cancel(ship); }
    }
}
