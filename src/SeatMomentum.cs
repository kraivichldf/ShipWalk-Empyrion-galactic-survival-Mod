using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace ShipWalk
{
    internal sealed class SeatRelease
    {
        public SeatMomentum Owner;
        public object Actor, Ship;
        public Rigidbody Body;
        public ExitMomentum Motion;
        public RemoteBodyHandoff Pose;
        public bool WasPilot;
        public int Generation;
        public string Source;
        public PlayerDeparture Passenger;
    }

    internal sealed class PlayerDeparture
    {
        public object Actor, Ship;
        public Rigidbody Body, ShipBody;
        public SeatExitInertia Inertia;
        public Vector3 Before;
    }

    internal sealed class SeatMomentum
    {
        private readonly IModApi api;
        private readonly Build5150 map;
        private readonly Settings settings;
        private readonly TraceLog log;
        public readonly CoastingPhysics Coasting;
        private readonly Dictionary<object, RemoteShipMotion> samples = new Dictionary<object, RemoteShipMotion>();
        private readonly Dictionary<object, SeatRelease> pending = new Dictionary<object, SeatRelease>();
        private float lastPrune;
        private int generation;
        private PlayerDeparture departingPlayer;

        public SeatMomentum(IModApi api, Build5150 map, Settings settings, TraceLog log)
        {
            this.api = api; this.map = map; this.settings = settings; this.log = log;
            Coasting = new CoastingPhysics(map, log, () => Enabled);
        }

        private bool Enabled => settings.Mode == RunMode.Experimental && settings.AllowMovingSeatExit
            && settings.PreserveExitMomentum && api.Application.State == GameState.Running;

        public void ObserveRemote(object ship, Vector3 absolutePosition, Vector3 euler)
        {
            if (!Enabled || api.Application.Mode != ApplicationMode.PlayfieldServer
                || !map.ShipType.IsInstanceOfType(ship) || !map.Bool(map.EntityRemote, ship)
                || !map.HasPilot(ship)) return;
            if (!samples.TryGetValue(ship, out var sample))
            {
                samples.Add(ship, sample = new RemoteShipMotion());
            }
            Quaternion rotation = Quaternion.Euler(euler);
            sample.Observe(N(absolutePosition), new NQuaternion(rotation.x, rotation.y, rotation.z, rotation.w), Time.time);
        }

        public SeatRelease Begin(object actor, object destination)
        {
            if (!Enabled || destination != null || actor == null || !map.PlayerType.IsInstanceOfType(actor)) return null;
            object ship = map.SeatedShip.GetValue(actor);
            if (ship == null || !map.ShipType.IsInstanceOfType(ship) || map.DockedTo.GetValue(ship) != null) return null;
            bool server = api.Application.Mode == ApplicationMode.PlayfieldServer;
            // This is the actual native detach, not permission to request an exit.
            // Also preserve a pilot client's motion when a remote passenger exits.
            IPlayer player = map.Player(actor);
            if (player == null || player.Id != map.Id(actor) || !(player.Health > 0f)) return null;
            IEntity vessel = map.Entity(ship);
            if (!SeatExitPolicy.Allows(settings.Mode, settings.AllowMovingSeatExit, true, true,
                vessel.Type.ToString(), player.Health, map.IsOpenSeat(actor, vessel.Structure, out _))) return null;
            Rigidbody body = (Rigidbody)map.EntityBody.GetValue(ship);
            if (body == null) return null;
            NVector velocity, angular;
            RemoteBodyHandoff handoff = null;
            string source;
            if (!map.Bool(map.EntityRemote, ship) && !body.isKinematic && body.gameObject.activeInHierarchy)
            { velocity = N(body.velocity); angular = N(body.angularVelocity); source = "local-rigidbody"; }
            else if (server && samples.TryGetValue(ship, out var sample)
                && sample.TryCapture(Time.time, N(Vector3.Scale(body.centerOfMass, body.transform.lossyScale)), out handoff))
            { velocity = handoff.Linear; angular = handoff.Angular; source = "server-observed-pose"; }
            else
            {
                log.Info("Exit momentum unavailable for ship " + vessel.Id + "; application=" + api.Application.Mode
                    + "; remote=" + map.Bool(map.EntityRemote, ship) + "; kinematic=" + body.isKinematic
                    + "; active=" + body.gameObject.activeInHierarchy + "; no usable motion source.");
                return null;
            }
            if (!ExitMomentum.Valid(velocity, angular) || (velocity.LengthSquared() < .0001f && angular.LengthSquared() < .0001f)) return null;
            var release = new SeatRelease { Owner = this, Actor = actor, Ship = ship, Body = body,
                Motion = new ExitMomentum(velocity, angular, Time.time), WasPilot = map.Bool(map.ActorPiloting, actor),
                Source = source, Pose = handoff, Generation = generation };
            if (server && release.WasPilot)
                log.Info("Playfield pilot release captured; ship=" + vessel.Id + "; player=" + player.Id
                    + "; source=" + source + "; speed=" + velocity.Length().ToString("F3")
                    + "; remoteBefore=" + map.Bool(map.EntityRemote, ship) + "; transfer=pending-native-release.");
            // Character movement belongs to the departing player's own client,
            // independently of which process will simulate the unpiloted ship.
            if (vessel.Type.ToString() == "CV" && api.Application.LocalPlayer?.Id == map.Id(actor)
                && map.EntityBody.GetValue(actor) is Rigidbody playerBody && playerBody != null)
            {
                departingPlayer?.Inertia.Cancel();
                release.Passenger = departingPlayer = new PlayerDeparture { Actor = actor, Ship = ship,
                    Body = playerBody, ShipBody = body,
                    Inertia = new SeatExitInertia(velocity, angular, N(body.worldCenterOfMass + map.OriginOffset), Time.time) };
            }
            // Install before native detach calls ShowPciture(true), disabling the body.
            if (release.WasPilot && !map.Bool(map.EntityRemote, ship)) Coasting.Begin(ship, body);
            return release;
        }

        public void End(SeatRelease release, Exception originalError)
        {
            if (release == null) return;
            if (originalError != null || !Enabled || release.Generation != generation
                || map.SeatedShip.GetValue(release.Actor) != null)
            {
                release.Passenger?.Inertia.Cancel();
                if (release.WasPilot) Coasting.Cancel(release.Ship);
                return;
            }
            if (release.Passenger != null && ReferenceEquals(release.Passenger, departingPlayer))
            {
                release.Passenger.Inertia.Confirm();
                ApplyPlayerDeparture();
            }
            // Actual detachment completed. A normal MP client loses authority here.
            // The playfield server independently uses its recent observed motion.
            if (map.Bool(map.EntityRemote, release.Ship))
            {
                if (release.WasPilot) Coasting.Cancel(release.Ship);
                log.Info("Exit momentum write skipped for ship " + map.Id(release.Ship)
                    + "; application=" + api.Application.Mode + "; ship is now remotely simulated.");
                return;
            }
            samples.Remove(release.Ship);
            if (release.WasPilot)
            {
                // Align before enabling the parked native body. Otherwise its old
                // position becomes the next authoritative pose and teleports the hull.
                if (release.Pose != null && !ApplyHandoffPose(release))
                {
                    Coasting.Cancel(release.Ship);
                    log.Info("Playfield pose handoff cancelled; ship=" + map.Id(release.Ship) + "; stale or changed native ownership/body.");
                    return;
                }
                // Server ownership becomes local only inside the original release.
                Coasting.Begin(release.Ship, release.Body);
                Coasting.Confirm(release.Ship);
                Coasting.SelectForHandoff(release.Ship);
            }
            pending[release.Ship] = release;
            Apply(release.Ship);
        }

        private bool ApplyHandoffPose(SeatRelease release)
        {
            object ship = release.Ship;
            Rigidbody body = release.Body;
            var root = map.PhysicsRoot.GetValue(ship) as Transform;
            var hull = map.EntityTransform.GetValue(ship) as Transform;
            bool ready = body != null && root != null && hull != null
                && ReferenceEquals(map.EntityBody.GetValue(ship), body);
            bool cancelled = !Enabled || api.Application.Mode != ApplicationMode.PlayfieldServer
                || map.HasPilot(ship) || map.DockedTo.GetValue(ship) != null;
            Vector3 origin = map.OriginOffset;
            if (!release.Pose.TryTake(Time.time, N(origin), !map.Bool(map.EntityRemote, ship), ready, cancelled, out var pose))
                return false;
            Vector3 before = body.position + origin, hullBefore = hull.position + origin;
            Vector3 position = U(pose.Position), absolute = U(release.Pose.AbsolutePose.Position);
            var rotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
            map.SetEntityPosition.Invoke(ship, new object[] { absolute, false });
            map.SetEntityRotation.Invoke(ship, new object[] { rotation.eulerAngles, false });
            map.SetEntityQuaternion.Invoke(ship, new object[] { rotation, false, false });
            root.SetPositionAndRotation(position, rotation);
            body.position = position; body.rotation = rotation;
            hull.SetPositionAndRotation(position, rotation);
            log.Info("Playfield pose handoff; ship=" + map.Id(ship) + "; bodyBefore=" + before.ToString("F3")
                + "; hullBefore=" + hullBefore.ToString("F3") + "; accepted=" + absolute.ToString("F3")
                + "; parkedGap=" + Vector3.Distance(before, absolute).ToString("F3")
                + "; sampleAge=" + (Time.time - release.Pose.SampledAt).ToString("F3") + "; transfer=once-before-activation.");
            return true;
        }

        public void AfterShipUpdate(object ship)
        {
            if (pending.ContainsKey(ship)) Apply(ship);
            Coasting.Update(ship);
        }

        private bool ValidPlayerDeparture()
        {
            PlayerDeparture p = departingPlayer;
            if (p == null) return false;
            bool valid = Enabled && p.Inertia.Fresh(Time.time) && p.Body != null && p.ShipBody != null
                && api.Application.LocalPlayer?.Id == map.Id(p.Actor) && api.Application.LocalPlayer.Health > 0f
                && ReferenceEquals(map.EntityBody.GetValue(p.Actor), p.Body)
                && ReferenceEquals(map.EntityBody.GetValue(p.Ship), p.ShipBody)
                && map.DockedTo.GetValue(p.Ship) == null
                && (p.Inertia.Confirmed ? map.SeatedShip.GetValue(p.Actor) == null
                    : map.SeatedShip.GetValue(p.Actor) == null || ReferenceEquals(map.SeatedShip.GetValue(p.Actor), p.Ship));
            if (!valid) { p.Inertia.Cancel(); departingPlayer = null; }
            return valid;
        }

        public bool WantsExitInterior(object ship) => ValidPlayerDeparture()
            && settings.PreserveInterior && ReferenceEquals(departingPlayer.Ship, ship);

        public Rigidbody DepartingBody => ValidPlayerDeparture() ? departingPlayer.Body : null;
        public PlayerDeparture PeekPlayerDeparture(object actor, Rigidbody body)
            => ValidPlayerDeparture() && ReferenceEquals(departingPlayer.Actor, actor) && departingPlayer.Body == body
                ? departingPlayer : null;

        private void ApplyPlayerDeparture()
        {
            if (!ValidPlayerDeparture()) return;
            PlayerDeparture p = departingPlayer;
            bool ready = p.Body.gameObject.activeInHierarchy && !p.Body.isKinematic;
            if (p.Inertia.TryApply(Time.time, N(p.Body.worldCenterOfMass + map.OriginOffset), true, ready, false, out NVector velocity))
            {
                p.Before = p.Body.velocity;
                // Replace the stale seated value; adding to it can double speed
                // after a previous exit, and a later collision must never be reset.
                p.Body.velocity = U(velocity);
                log.Info("Inherited CV " + map.Id(p.Ship) + " motion for local player " + map.Id(p.Actor)
                    + "; speed=" + p.Before.magnitude.ToString("F2") + "->" + p.Body.velocity.magnitude.ToString("F2")
                    + " m/s; fixedTime=" + Time.fixedTime.ToString("F3") + ".");
            }
        }

        public PlayerDeparture TakePlayerDeparture(object actor, Rigidbody body)
        {
            if (!ValidPlayerDeparture() || !ReferenceEquals(departingPlayer.Actor, actor)
                || departingPlayer.Body != body) return null;
            // Also covers a body that became dynamic only on its first FixedUpdate.
            ApplyPlayerDeparture();
            return departingPlayer != null && departingPlayer.Inertia.TrySeed(Time.time, out _) ? departingPlayer : null;
        }

        private void Apply(object ship)
        {
            if (!pending.TryGetValue(ship, out SeatRelease release)) return;
            Rigidbody body = release.Body;
            bool cancelled = !Enabled || body == null || map.DockedTo.GetValue(ship) != null
                || map.SeatedShip.GetValue(release.Actor) != null
                || (release.WasPilot && map.HasPilot(ship))
                || !ReferenceEquals(map.EntityBody.GetValue(ship), body);
            if (cancelled && release.WasPilot) Coasting.Cancel(ship);
            bool ready = body != null && !body.isKinematic && body.gameObject.activeInHierarchy;
            bool applied = release.Motion.TryTake(Time.time, !map.Bool(map.EntityRemote, ship), ready, cancelled,
                out NVector velocity, out NVector angular);
            if (applied)
            {
                Vector3 before = body.velocity;
                body.velocity = U(velocity);
                body.angularVelocity = U(angular);
                log.Info("Preserved exit momentum on " + map.EntityType.GetValue(ship) + " " + map.Id(ship)
                    + "; source=" + release.Source + "; speed=" + before.magnitude.ToString("F2") + "->"
                    + body.velocity.magnitude.ToString("F2") + " m/s; angular=" + body.angularVelocity.magnitude.ToString("F3")
                    + " rad/s; application=" + api.Application.Mode + "; remote=" + map.Bool(map.EntityRemote, ship)
                    + "; active=" + body.gameObject.activeInHierarchy + "; kinematic=" + body.isKinematic + "; transfer=once.");
            }
            if (release.Motion.Finished)
            {
                pending.Remove(ship);
                if (!applied && release.WasPilot) Coasting.Cancel(ship);
            }
        }

        public void Update()
        {
            // Apply only inside the native detachment/update callbacks, never after
            // an arbitrary physics step (which could have changed velocity on impact).
            if (!Enabled) { Reset(); return; }
            ValidPlayerDeparture();
            Coasting.Update();
            if (Time.time - lastPrune < 1f) return;
            lastPrune = Time.time;
            foreach (object ship in pending.Where(p => Time.time - p.Value.Motion.CapturedAt > ExitMomentum.Lifetime).Select(p => p.Key).ToArray())
            {
                if (pending[ship].WasPilot) Coasting.Cancel(ship);
                pending.Remove(ship); log.Info("Exit momentum expired before physics became ready for ship " + map.Id(ship) + ".");
            }
            foreach (object ship in samples.Where(p => Time.time - p.Value.LastSeen > 1f).Select(p => p.Key).ToArray()) samples.Remove(ship);
        }

        public void Reset()
        {
            generation++; pending.Clear(); samples.Clear();
            departingPlayer?.Inertia.Cancel(); departingPlayer = null;
            Coasting.Reset();
        }
        private static NVector N(Vector3 v) => new NVector(v.x, v.y, v.z);
        private static Vector3 U(NVector v) => new Vector3(v.X, v.Y, v.Z);
    }
}
