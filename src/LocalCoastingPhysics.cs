using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipWalk
{
    internal struct ShipControllerTick
    {
        internal object Ship;
        internal Rigidbody Body;
        internal Vector3 Before;
        internal bool FirstTicks;
    }
    // This alternative runtime never enables both detailed world collision and
    // the ship's bounding body. LocalGeometry provides the detailed player scene.
    internal sealed class CoastingPhysics
    {
        private static readonly float[] ReportTimes = { .1f, .25f, .5f, 1f };
        private sealed class Coast
        {
            internal Rigidbody Body;
            internal PhysicsActivity Activity;
            internal bool Confirmed;
            internal float Started;
            internal bool KinematicRequested, Server;
            internal int Reports;
            internal int CacheReports, ControllerTicks, ControllerReports, CollisionReports;
            internal bool BrakeReported;
            internal Vector3 StartPosition;
        }
        private readonly Build5150 map;
        private readonly Func<bool> enabled;
        private readonly TraceLog log;
        private readonly SeatMotionScope<Rigidbody> seatMotion = new SeatMotionScope<Rigidbody>();
        private readonly Dictionary<object, Coast> ships = new Dictionary<object, Coast>();
        public CoastingPhysics(Build5150 map, TraceLog log, Func<bool> enabled)
        { this.map = map; this.enabled = enabled; this.log = log; }
        public void Begin(object ship, Rigidbody body)
        {
            if (!ships.ContainsKey(ship)) ships.Add(ship, new Coast { Body = body,
                Activity = new PhysicsActivity(body.gameObject.activeSelf), Started = Time.time,
                Server = Runtime.Current?.PlayfieldServer == true, KinematicRequested = body.isKinematic,
                StartPosition = (Vector3)map.EntityPosition.GetValue(ship) });
        }
        public void Confirm(object ship) { if (ships.TryGetValue(ship, out Coast c)) c.Confirmed = true; }
        public bool Contains(object ship) => ship != null && ships.ContainsKey(ship);
        public int Count => ships.Count;
        private Coast FindController(object controller, out object ship)
        {
            ship = map.ShipControllerEntity.GetValue(controller);
            if (ship == null) return null;
            Coast c = Find(ship);
            return c?.Server == true && c.Confirmed && c.Body.gameObject.activeInHierarchy && !c.Body.isKinematic
                && ReferenceEquals(map.ShipControllerBody.GetValue(controller), c.Body) ? c : null;
        }
        public Vector3 ReadVelocityCache(Vector3 cached, Component controller, bool local)
        {
            Coast c = FindController(controller, out object ship);
            if (c == null) return cached;
            // Read today's physical result, including impacts. Never replay the
            // departing pilot's saved velocity or alter the native cache itself.
            Vector3 live = local ? controller.transform.InverseTransformDirection(c.Body.velocity) : c.Body.velocity;
            if (c.CacheReports < 4 && (cached - live).sqrMagnitude > .0001f)
            {
                c.CacheReports++;
                log.Info("Playfield controller cache corrected; ship=" + map.Id(ship) + "; cache=" + (local ? "local" : "world")
                    + "; cached=" + cached.ToString("F3") + "; live=" + live.ToString("F3")
                    + "; elapsed=" + (Time.time - c.Started).ToString("F3"));
            }
            return live;
        }
        public bool AutoBrake(bool requested, object controller)
        {
            Coast c = FindController(controller, out object ship);
            if (c == null) return requested;
            if (requested && !c.BrakeReported)
            {
                c.BrakeReported = true;
                log.Info("Playfield automatic thruster braking suppressed; ship=" + map.Id(ship) + "; unpiloted coasting.");
            }
            return false;
        }
        public ShipControllerTick BeforeController(object controller)
        {
            Coast c = FindController(controller, out object ship);
            if (c == null || c.ControllerReports >= 8 || Time.time - c.Started > 1f) return default;
            return new ShipControllerTick { Ship = ship, Body = c.Body, Before = c.Body.velocity, FirstTicks = c.ControllerTicks++ < 4 };
        }
        public void AfterController(object controller, ShipControllerTick tick)
        {
            if (tick.Ship == null || tick.Body == null) return;
            object ship = tick.Ship;
            Coast c = Find(ship);
            if (c?.Server != true || tick.Body != c.Body || !ReferenceEquals(map.ShipControllerEntity.GetValue(controller), ship)) return;
            Vector3 after = c.Body.velocity;
            if (!tick.FirstTicks && (after - tick.Before).sqrMagnitude < .01f) return;
            c.ControllerReports++;
            var hull = map.EntityTransform.GetValue(ship) as Transform;
            log.Info("Playfield controller step; ship=" + map.Id(ship) + "; elapsed=" + (Time.time - c.Started).ToString("F3")
                + "; before=" + tick.Before.ToString("F3") + "; after=" + after.ToString("F3")
                + "; powered=" + map.Bool(map.ShipPowered, controller) + "; gravity=" + c.Body.useGravity
                + "; active=" + c.Body.gameObject.activeInHierarchy + "; kinematic=" + c.Body.isKinematic
                + "; drag=" + c.Body.drag.ToString("F3")
                + "; body=" + (c.Body.position + map.OriginOffset).ToString("F3")
                + "; hull=" + (hull == null ? "none" : (hull.position + map.OriginOffset).ToString("F3"))
                + "; native=" + ((Vector3)map.EntityPosition.GetValue(ship)).ToString("F3"));
        }
        public void Collision(object controller, Collision collision)
        {
            Coast c = FindController(controller, out object ship);
            if (c == null || collision == null || c.CollisionReports >= 4) return;
            c.CollisionReports++;
            log.Info("Playfield coasting collision; ship=" + map.Id(ship) + "; elapsed=" + (Time.time - c.Started).ToString("F3")
                + "; collider=" + (collision.collider == null ? "none" : collision.collider.name)
                + "; relative=" + collision.relativeVelocity.ToString("F3") + "; impulse=" + collision.impulse.ToString("F3")
                + "; velocity=" + c.Body.velocity.ToString("F3"));
        }
        public bool WantsBounding(object ship) => ship != null && Find(ship) != null;
        public bool ReadKinematic(Rigidbody body, object ship)
        {
            Coast c = Find(ship);
            return c?.Server == true && c.Body == body ? c.KinematicRequested : body.isKinematic;
        }
        public void SetKinematic(Rigidbody body, bool value, object ship)
        {
            Coast c = Find(ship);
            if (c?.Server == true && c.Body == body)
            { c.KinematicRequested = value; body.isKinematic = false; }
            else body.isKinematic = value;
        }
        public void SelectForHandoff(object ship)
        {
            Coast c = Find(ship);
            if (c == null) return;
            if (c.Server)
            {
                // The game's selector switches its own cached detail colliders
                // off and its bounding body on. No copied interior on the server.
                map.ColliderSelection.Invoke(ship, new object[] { false, true });
            }
            AfterSelection(ship);
        }
        public IDisposable EnterSeatTransition(object ship)
        {
            Coast coast = ship == null ? null : Find(ship);
            return coast?.Confirmed == true ? seatMotion.Enter(coast.Body) : null;
        }
        public bool PreserveSeatReset(Rigidbody body, Vector3 value, bool angular)
        {
            if (body == null || value != Vector3.zero || !seatMotion.Contains(body) || !enabled()) return false;
            // Re-check ownership and pilot takeover at the actual setter, after
            // native seating has updated its state. No velocity is replayed.
            foreach (var pair in ships)
            {
                if (pair.Value.Body != body || !pair.Value.Confirmed) continue;
                if (!ReferenceEquals(map.EntityBody.GetValue(pair.Key), body) || map.Bool(map.EntityRemote, pair.Key)
                    || map.DockedTo.GetValue(pair.Key) != null || map.HasPilot(pair.Key)) return false;
                if (!angular) log.Info("LocalFrame passenger seat reset suppressed; ship=" + map.Id(pair.Key)
                    + "; shipSpeed=" + body.velocity.magnitude.ToString("F2") + "; native seat state retained.");
                return true;
            }
            return false;
        }
        private Coast Find(object ship, GameObject root = null)
        {
            if (!ships.TryGetValue(ship, out Coast c)) return null;
            if (!enabled() || c.Body == null || !ReferenceEquals(map.EntityBody.GetValue(ship), c.Body)
                || map.Bool(map.EntityRemote, ship) || map.DockedTo.GetValue(ship) != null
                || (!c.Confirmed && Time.time - c.Started > ExitMomentum.Lifetime))
            { Cancel(ship); return null; }
            if (c.Confirmed && map.HasPilot(ship))
            { Cancel(ship, true); return null; }
            return root == null || c.Body.gameObject == root ? c : null;
        }
        public bool ReadActive(GameObject root, object ship)
        {
            Coast c = Find(ship, root);
            return c != null && !c.Server ? c.Activity.Requested : root.activeSelf;
        }
        public void SetActive(GameObject root, bool active, object ship)
        {
            Coast c = Find(ship, root);
            if (c == null) { root.SetActive(active); return; }
            // Suppress cached static detail before activating the native body.
            if (!c.Server) Runtime.Current?.Frame.SelectBounding(ship);
            bool leased = c.Activity.Request(active);
            // Server calls already request bounding=true. Respect a native
            // refusal (geometry not ready, docking, etc.); forcing active while
            // the selector kept detail enabled would cause self-collisions.
            root.SetActive(c.Server ? active : leased);
        }
        public void AfterSelection(object ship)
        {
            Coast c = Find(ship);
            if (c == null) return;
            if (!c.Server) Runtime.Current?.Frame.SelectBounding(ship);
            else if (c.Body.gameObject.activeInHierarchy) c.Body.isKinematic = false;
        }
        public void Cancel(object ship, bool pilotTakingOver = false)
        {
            if (ship == null || !ships.TryGetValue(ship, out Coast c)) return;
            ships.Remove(ship);
            if (c.Body != null)
            {
                if (c.Server && !c.Body.isKinematic) c.Body.isKinematic = c.KinematicRequested;
                c.Body.gameObject.SetActive(c.Activity.Release(pilotTakingOver));
                if (c.Server && map.EntityTransform.GetValue(ship) is Transform t && t != null)
                    map.ColliderSelection.Invoke(ship, new object[] { false, false });
            }
        }
        public void Update(object ship)
        {
            Coast c = Find(ship);
            if (c?.Server == true && c.Confirmed && c.Reports < 4
                && Time.time - c.Started >= ReportTimes[c.Reports])
            {
                c.Reports++;
                log.Info("Playfield coasting sample; ship=" + map.Id(ship) + "; elapsed=" + (Time.time - c.Started).ToString("F3")
                    + "; active=" + c.Body.gameObject.activeInHierarchy + "; kinematic=" + c.Body.isKinematic
                    + "; speed=" + c.Body.velocity.magnitude.ToString("F3") + "; distance="
                    + Vector3.Distance(c.StartPosition, (Vector3)map.EntityPosition.GetValue(ship)).ToString("F3")
                    + "; pilot=" + map.HasPilot(ship) + "; remote=" + map.Bool(map.EntityRemote, ship));
            }
            if (c?.Server == true && c.Confirmed && Time.time - c.Started > ExitMomentum.Lifetime
                && c.Body.gameObject.activeInHierarchy && !c.Body.isKinematic
                && c.Body.velocity.sqrMagnitude < .0001f && c.Body.angularVelocity.sqrMagnitude < .0001f)
            { log.Info("Playfield coasting finished at rest; ship=" + map.Id(ship)); Cancel(ship); }
        }
        public void Update() { foreach (object ship in ships.Keys.ToArray()) Update(ship); }
        public void Reset() { seatMotion.Clear(); foreach (object ship in ships.Keys.ToArray()) Cancel(ship); }
    }
}
