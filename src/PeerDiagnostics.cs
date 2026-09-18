using System;
using System.Linq;
using Eleon.Modding;
using UnityEngine;

namespace ShipWalk
{
    // Observes existing replication without changing another player's pose.
    // Available only through an explicit peers command in release builds.
    internal sealed class PeerDiagnostics
    {
        private readonly IModApi api;
        private readonly Build5150 map;
        private readonly TraceLog log;
        public PeerDiagnostics(IModApi api, Build5150 map, TraceLog log)
        { this.api = api; this.map = map; this.log = log; }
        public void Tick(bool enabled) { }
        public void Capture()
        {
            try { CapturePlayers(); }
            catch (Exception error) { log.Reply("MP peers unavailable; reason=" + error.GetType().Name); }
        }
        private void CapturePlayers()
        {
            IPlayfield playfield = api.ClientPlayfield;
            if (playfield == null) { log.Reply("MP peers; no active playfield."); return; }
            IPlayer observer = api.Application.LocalPlayer;
            IEntity observerShip = null;
            try { observerShip = observer?.DrivingEntity ?? observer?.CurrentStructure?.Entity; }
            catch (Exception) { /* Peers are still observable during a local context change. */ }
            IPlayer[] players = playfield.Players.Values.Where(p => p != null).OrderBy(p => p.Id).Take(32).ToArray();
            log.Reply("MP peers; observer=" + (observer?.Id ?? -1)
                + "; playfield=" + playfield.Name + "; visiblePlayers=" + playfield.Players.Count
                + "; sampled=" + players.Length + "; time=" + Time.time.ToString("F3"));
            foreach (IPlayer player in players)
            {
                try
                {
                    object actor = map.NativeEntity(player);
                    if (actor == null) continue;
                    IEntity vessel = player.DrivingEntity ?? player.CurrentStructure?.Entity;
                    bool actorShipContext = vessel != null && LocalFrameMath.IsVessel(vessel.Type.ToString());
                    // A sliding observer may lose CurrentStructure: retain its
                    // position relative to the observer's ship for diagnosis,
                    // explicitly without claiming that the peer is attached.
                    if (!actorShipContext) vessel = observerShip;
                    var actorRoot = map.EntityTransform.GetValue(actor) as Transform;
                    if (actorRoot == null) continue;
                    Vector3 nativePosition = (Vector3)map.EntityPosition.GetValue(actor);
                    if (vessel == null || !LocalFrameMath.IsVessel(vessel.Type.ToString()))
                    {
                        log.Reply("MP observed; observer=" + (observer?.Id ?? -1) + "; actor=" + player.Id
                            + "; reference=none; renderedWorld=" + (actorRoot.position + map.OriginOffset).ToString("F3")
                            + "; nativeWorld=" + nativePosition.ToString("F3"));
                        continue;
                    }
                    object ship = map.NativeEntity(vessel);
                    if (ship == null) continue;
                    var hull = map.EntityTransform.GetValue(ship) as Transform;
                    if (hull == null) continue;
                    Vector3 local = Quaternion.Inverse(hull.rotation) * (actorRoot.position - hull.position);
                    Vector3 shipPosition = (Vector3)map.EntityPosition.GetValue(ship);
                    Vector3 nativeLocal = Quaternion.Inverse(vessel.Rotation) * (nativePosition - shipPosition);
                    log.Reply("MP observed; observer=" + (observer?.Id ?? -1)
                        + "; actor=" + player.Id + "; actorRemote=" + map.Bool(map.EntityRemote, actor)
                        + "; reference=" + (actorShipContext ? "actor-ship-context" : "observer-ship-only")
                        + "; ship=" + vessel.Id + "; shipRemote=" + map.Bool(map.EntityRemote, ship)
                        + "; seated=" + (player.DrivingEntity != null) + "; pilot=" + player.IsPilot
                        + "; renderedLocal=" + local.ToString("F3") + "; nativeLocal=" + nativeLocal.ToString("F3")
                        + "; actorDisplayGap=" + Vector3.Distance(actorRoot.position + map.OriginOffset, nativePosition).ToString("F3")
                        + "; shipDisplayGap=" + Vector3.Distance(hull.position + map.OriginOffset, shipPosition).ToString("F3")
                        + "; nativeShipSpeed=" + ((Vector3)map.EntityVelocity.GetValue(ship)).magnitude.ToString("F3"));
                }
                catch (Exception error)
                {
                    // A partial/unloaded remote entity must not disable walking.
                    log.Reply("MP observed unavailable; actor=" + player.Id + "; reason=" + error.GetType().Name);
                }
            }
        }
    }
}
