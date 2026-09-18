using System;
using System.Globalization;
using UnityEngine;

namespace ShipWalk
{
    // Observes existing callbacks only. Failure of extra telemetry must not turn
    // off movement or change collision/velocity state.
    internal sealed class ExitDiagnostics
    {
        private readonly Build5150 map;
        private readonly Settings settings;
        private readonly TraceLog log;
        private readonly DiagnosticWindow window = new DiagnosticWindow();
        private Rigidbody shipBody;
        private Transform hull;
        private int shipId;
        private int sequence;
        private bool failed;
        private bool capturing;
        private PlayerDeparture lastDeparture;

        public ExitDiagnostics(Build5150 map, Settings settings, TraceLog log)
        { this.map = map; this.settings = settings; this.log = log; }

        public bool Active => !failed && settings.Trace && settings.Mode == RunMode.Experimental && window.Active(Time.fixedTime);

        public void Begin(PlayerDeparture departure)
        {
            if (failed || !settings.Trace || departure == null || ReferenceEquals(lastDeparture, departure)) return;
            try
            {
                Close();
                lastDeparture = departure;
                shipBody = departure.ShipBody;
                hull = (Transform)map.EntityTransform.GetValue(departure.Ship);
                shipId = map.Id(departure.Ship);
                window.Open(Time.fixedTime);
                sequence = 0; capturing = true;
                log.Info("Exit physics capture started for CV " + shipId + "; duration=1 s; coreLimit="
                    + DiagnosticWindow.MaxCoreEvents + "; contactPairsPerStep=" + DiagnosticWindow.ContactsPerStep
                    + "; contactPairLimit=" + DiagnosticWindow.MaxContactPairs + ".");
            }
            catch (Exception error) { Fault(error); }
        }

        public bool TakeContactPair() => Active && window.TakeContactPair(Time.fixedTime);

        public void Event(string stage, Passenger p, Vector3 before, Vector3 after, string detail,
            Collision collision = null, bool reservedContact = false)
        {
            if (!Active || !reservedContact && !window.Take(Time.fixedTime)) return;
            try
            {
                string snapshot = "sequence=" + (++sequence) + ";exitShip=" + shipId
                    + ";droppedContactPairs=" + window.DroppedContacts + ";droppedCore=" + window.DroppedCore
                    + ";supportProbe=" + (p?.SupportProbe ?? "none")
                    + ";realtime=" + F(Time.realtimeSinceStartup)
                    + ";step=" + F(Time.fixedDeltaTime)
                    + ";body=" + Body(p?.Body)
                    + ";controllerPos=" + V(p?.Controller != null ? p.Controller.transform.position : Vector3.zero)
                    + ";nativeGrounded=" + (p?.Controller != null && map.Bool(map.Grounded, p.Controller))
                    + ";shipBody=" + Body(shipBody)
                    + ";interiorBody=" + Body(p?.InteriorBody)
                    + ";hullPos=" + V(hull != null ? hull.position : Vector3.zero)
                    + ";hullRot=" + Q(hull != null ? hull.rotation : Quaternion.identity)
                    + ";floor=" + ColliderInfo(p?.Floor)
                    + ";origin=" + V(map.OriginOffset);
                if (collision != null)
                {
                    snapshot += ";other=" + ColliderInfo(collision.collider)
                        + ";collisionRelative=" + V(collision.relativeVelocity)
                        + ";impulse=" + V(collision.impulse) + ";contacts=" + collision.contactCount;
                    if (collision.contactCount > 0)
                    {
                        ContactPoint c = collision.GetContact(0);
                        snapshot += ";contactPoint=" + V(c.point) + ";contactNormal=" + V(c.normal)
                            + ";separation=" + F(c.separation)
                            + ";thisCollider=" + ColliderInfo(c.thisCollider)
                            + ";otherCollider=" + ColliderInfo(c.otherCollider);
                    }
                }
                log.Event("exit-" + stage, settings, p, before, after, detail + ";" + snapshot);
            }
            catch (Exception error) { Fault(error); }
        }

        private static string Body(Rigidbody body) => body == null ? "none" : body.GetInstanceID()
            + "|pos:" + V(body.position) + "|rot:" + Q(body.rotation) + "|vel:" + V(body.velocity)
            + "|angular:" + V(body.angularVelocity) + "|active:" + body.gameObject.activeInHierarchy
            + "|kinematic:" + body.isKinematic + "|drag:" + F(body.drag) + "|mass:" + F(body.mass);

        private static string ColliderInfo(Collider collider) => collider == null ? "none" : collider.GetInstanceID()
            + "|name:" + Clean(collider.name) + "|layer:" + collider.gameObject.layer
            + "|enabled:" + (collider.enabled && collider.gameObject.activeInHierarchy)
            + "|pos:" + V(collider.transform.position) + "|rigidbody:" + Body(collider.attachedRigidbody);

        private static string Clean(string text) => text.Replace(';', '_').Replace('|', '_').Replace('\r', ' ').Replace('\n', ' ');
        private static string F(float value) => value.ToString("F5", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => F(v.x) + " " + F(v.y) + " " + F(v.z);
        internal static string Vector(Vector3 value) => V(value);
        private static string Q(Quaternion q) => F(q.x) + " " + F(q.y) + " " + F(q.z) + " " + F(q.w);

        public void Fault(Exception error)
        {
            failed = true; Close();
            try { log.Info("Exit physics capture disabled; movement unchanged: " + error); } catch { }
        }
        public void Update()
        {
            if (!Active) Close();
        }
        public void Close()
        {
            window.Close(); shipBody = null; hull = null;
            if (!capturing) return;
            capturing = false;
            try { log.Info("Exit physics capture ended for CV " + shipId + "; core=" + window.CoreEvents
                + "; contactPairs=" + window.ContactPairs + "; omittedContactPairs=" + window.DroppedContacts
                + "; omittedCore=" + window.DroppedCore + "."); } catch { }
        }
    }
}
