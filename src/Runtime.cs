using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eleon.Modding;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using NVector = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace ShipWalk
{
    internal sealed class Passenger
    {
        public Component Controller;
        public Rigidbody Body;
        public object Entity, SupportEntity;
        public Rigidbody SupportBody, InteriorBody;
        public Transform SupportTransform;
        public Collider Floor;
        public int ShipId = -1;
        public bool Grounded, Ready, AppliedLastTick, HavePose, Sampling;
        public bool PoseFromBody;
        public float SeatExitStarted = float.NegativeInfinity;
        public readonly MotionFrame Frame = new MotionFrame();
        public readonly DragOverride Drag = new DragOverride();
        public bool AirborneVerified;
        public float LastContact = -100f, PoseTime, FixedTime, LastSample = -100f;
        public Vector3 ContactPoint, SupportVelocity, PreviousSupportVelocity, PreviousPosition;
        public Quaternion PreviousRotation;
        public string MotionSource;
        public string SupportProbe = "not-queried";
    }

    internal sealed class VelocityScope
    {
        public Runtime Owner;
        public Passenger Passenger;
        public Rigidbody Body;
        public Vector3 Before, Support, NativeBefore;
        public bool Changed, Closed, Landing;
        public string Phase, Decision;
    }

    internal sealed class Runtime : IDisposable
    {
        internal static Runtime Current;
        private const string HarmonyId = "privatecoop.shipwalk.5150";
        private readonly IModApi api;
        internal readonly Build5150 Map;
        internal readonly Settings Options;
        internal readonly TraceLog Log;
        internal readonly SeatMomentum Momentum;
        internal readonly ExitDiagnostics ExitTrace;
        internal readonly MovingInterior Interior;
        private readonly Harmony harmony;
        private readonly TypedFieldResolver fieldResolver;
        private readonly List<MethodInfo> attemptedHooks = new List<MethodInfo>();
        private readonly ContactPoint[] contacts = new ContactPoint[32];
        private Passenger passenger;
        private IPlayfield playfield;
        private object interiorLease;
        private bool restoringInterior, disposed, failed;
        private float lastFlush;
        private float lastColliderTrace = -100f;

        public Runtime(IModApi api, string folder)
        {
            this.api = api;
            Options = Settings.Load(System.IO.Path.Combine(folder, "ShipWalk.cfg"));
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
            Map = new Build5150(game);
            Log = new TraceLog(api, System.IO.Path.Combine(folder, "Logs"));
            Momentum = new SeatMomentum(api, Map, Options, Log);
            ExitTrace = new ExitDiagnostics(Map, Options, Log);
            Interior = new MovingInterior(Map, Log);
            harmony = new Harmony(HarmonyId);
            fieldResolver = new TypedFieldResolver(game);
        }

        public void Install()
        {
            if (Current != null) throw new InvalidOperationException("Another ShipWalk runtime is already active.");
            Current = this;
            try
            {
                fieldResolver.Install();
                Hook(Map.FixedUpdate, nameof(Hooks.FixedPrefix), null, nameof(Hooks.FixedFinalizer));
                Hook(Map.Limiter, nameof(Hooks.LimiterPrefix), null, nameof(Hooks.LimiterFinalizer));
                Hook(Map.Damping, nameof(Hooks.ProbePrefix), nameof(Hooks.ProbePostfix));
                Hook(Map.GroundDamping, nameof(Hooks.ProbePrefix), nameof(Hooks.ProbePostfix));
                Hook(Map.ContactEnter, nameof(Hooks.ContactPrefix), nameof(Hooks.ContactPostfix));
                Hook(Map.ContactStay, nameof(Hooks.ContactPrefix), nameof(Hooks.ContactPostfix));
                Hook(Map.Disable, nameof(Hooks.DisablePrefix), null);
                Hook(Map.ColliderSelection, nameof(Hooks.ColliderPrefix), nameof(Hooks.ColliderPostfix),
                    transpiler: nameof(Hooks.BodyActivationTranspiler));
                Hook(Map.SeatExit, null, null, transpiler: nameof(Hooks.SeatExitTranspiler));
                Hook(Map.SeatDetach, nameof(Hooks.SeatDetachPrefix), null, nameof(Hooks.SeatDetachFinalizer));
                Hook(Map.RemotePose, null, nameof(Hooks.RemotePosePostfix));
                Hook(Map.ShipUpdate, null, nameof(Hooks.ShipUpdatePostfix));
                Log.Info("Loaded v" + typeof(Runtime).Assembly.GetName().Version.ToString(3) + " for build 5150; mode=" + Options.Mode + "; interior=" + Options.PreserveInterior
                    + "; seatExit=" + Options.AllowMovingSeatExit + "; exitMomentum=" + Options.PreserveExitMomentum
                    + "; application=" + api.Application.Mode + ". Runtime hooks only. Trace: " + Log.Path);
            }
            catch (Exception error)
            {
                // Keep the first installation error; cleanup must not replace it in the log.
                api.LogError("[ShipWalk] Hook installation failed: " + error);
                if (RemoveHooks()) fieldResolver.Dispose();
                Current = null;
                throw;
            }
        }

        private void Hook(MethodInfo method, string prefix, string postfix, string finalizer = null, string transpiler = null)
        {
            HarmonyMethod Find(string name) => name == null ? null : new HarmonyMethod(typeof(Hooks).GetMethod(name));
            Log.Info("Installing hook " + method.DeclaringType.FullName + "." + method.Name);
            attemptedHooks.Add(method);
            harmony.Patch(method, prefix: Find(prefix), postfix: Find(postfix), finalizer: Find(finalizer), transpiler: Find(transpiler));
        }

        public bool AllowMovingSeatExit(object ship, object actor)
        {
            if (disposed || failed || Options.Mode != RunMode.Experimental || !Options.AllowMovingSeatExit
                || api.Application.State != GameState.Running || ship == null || actor == null
                || !Map.ShipType.IsInstanceOfType(ship) || !Map.ControllerEntity.FieldType.IsInstanceOfType(actor)) return false;
            IPlayer player = api.Application.LocalPlayer;
            if (player == null) return false;
            IEntity driving = player.DrivingEntity;
            bool seated = ReferenceEquals(Map.SeatedShip.GetValue(actor), ship)
                && driving != null && driving.Id == Map.Id(ship);
            string vesselType = Map.EntityType.GetValue(ship).ToString();
            int blockType = -1;
            bool localPlayer = Map.Id(actor) == player.Id;
            bool openSeat = localPlayer && player.Health > 0f && seated && Map.IsOpenSeat(actor, driving.Structure, out blockType);
            bool allowed = SeatExitPolicy.Allows(Options.Mode, Options.AllowMovingSeatExit,
                localPlayer, seated, vesselType, player.Health, openSeat);
            if (allowed) Log.Info("Allowing normal exit from open seat block " + blockType + " on moving "
                + vesselType + " " + Map.Id(ship) + " for local player " + player.Id);
            return allowed;
        }

        private bool RemoveHooks()
        {
            bool allRemoved = true;
            for (int i = attemptedHooks.Count - 1; i >= 0; i--)
            {
                try
                {
                    harmony.Unpatch(attemptedHooks[i], HarmonyPatchType.All, HarmonyId);
                    attemptedHooks.RemoveAt(i);
                }
                catch (Exception error)
                {
                    allRemoved = false;
                    api.LogError("[ShipWalk] Hook cleanup failed for " + attemptedHooks[i].Name + ": " + error);
                }
            }
            return allRemoved;
        }

        private bool IsLocal(Component controller, out object entity, out Rigidbody body)
        {
            entity = null;
            body = null;
            if (disposed || failed || Options.Mode == RunMode.Off || controller == null
                || api.Application.State != GameState.Running) return false;
            IPlayer player = api.Application.LocalPlayer;
            if (player == null) return false;
            entity = Map.ControllerEntity.GetValue(controller);
            body = (Rigidbody)Map.ControllerBody.GetValue(controller);
            return entity != null && body != null && Map.Id(entity) == player.Id;
        }

        private Passenger GetPassenger(Component controller)
        {
            if (!IsLocal(controller, out object entity, out Rigidbody body)) return null;
            if (passenger == null || passenger.Controller != controller)
            {
                Reset("controller-changed");
                passenger = new Passenger { Controller = controller, Body = body, Entity = entity };
            }
            return passenger;
        }

        private bool CanWalk(Passenger p) => WalkingBlock(p) == null;

        private string WalkingBlock(Passenger p)
        {
            IPlayer player = api.Application.LocalPlayer;
            if (player == null) return "no-local-player";
            if (!(player.Health > 0f)) return "not-alive";
            if (player.DrivingEntity != null) return "still-seated";
            if (p.Controller == null || p.Body == null) return "no-controller-or-body";
            if (!p.Body.gameObject.activeInHierarchy) return "inactive-body";
            if (!(p.Controller is Behaviour behaviour) || !behaviour.isActiveAndEnabled) return "inactive-controller";
            if (p.Body.isKinematic) return "kinematic-player";
            if (Map.Bool(Map.Jetpack, p.Controller)) return "controller-jetpack";
            if ((bool)Map.JetpackEnabled.Invoke(p.Entity, null)) return "entity-jetpack";
            if (Map.Bool(Map.Ladder, p.Controller)) return "ladder";
            return Map.Bool(Map.Submerged, p.Controller) ? "submerged" : null;
        }

        public void ObserveContact(Component controller, Collision collision)
        {
            Passenger p = GetPassenger(controller);
            if (p == null || !CanWalk(p)) return;
            int count = collision.GetContacts(contacts);
            for (int i = 0; i < count; i++)
            {
                ContactPoint contact = contacts[i];
                Collider floor = contact.otherCollider;
                if (floor == null || floor.attachedRigidbody == p.Body) continue;
                if (Interior.Owns(floor) && (!Interior.CanContact(contact.thisCollider, floor)
                    || !CollisionEligibility.Supports(contact.separation))) continue;
                Vector3 up = controller.transform.up;
                float height = Vector3.Dot(contact.point - controller.transform.position, up);
                if (Vector3.Dot(contact.normal, up) < 0.55f || height > 0.45f || height < -0.65f) continue;
                if (AcceptFloor(p, floor, contact.point)) break;
            }
        }

        public Vector3? TraceContactBefore(Component controller, Collision collision)
        {
            Passenger p = passenger;
            if (!ExitTrace.Active || p == null || p.Controller != controller || p.Body == null || !ExitTrace.TakeContactPair()) return null;
            Vector3 before = p.Body.velocity;
            ExitTrace.Event("contact-before", p, before, before, "stage=post-solver-before-native-contact", collision, true);
            return before;
        }

        public void TraceContactAfter(Component controller, Collision collision, Vector3? before)
        {
            Passenger p = passenger;
            if (!before.HasValue || p == null || p.Controller != controller || p.Body == null) return;
            ExitTrace.Event("contact-after", p, before.Value, p.Body.velocity, "stage=after-native-contact", collision, true);
        }

        private VelocityScope TraceScope(VelocityScope scope, string decision)
        {
            scope.Decision = decision;
            scope.NativeBefore = scope.Body != null ? scope.Body.velocity : Vector3.zero;
            ExitTrace.Event(scope.Phase + "-before", scope.Passenger, scope.Before, scope.NativeBefore,
                "decision=" + decision + ";wrapped=" + scope.Changed);
            return scope;
        }

        private bool AcceptFloor(Passenger p, Collider floor, Vector3 point)
        {
            if (floor == null || !floor.enabled || !floor.gameObject.activeInHierarchy || floor.isTrigger) return false;
            if (Interior.Owns(floor) && !Interior.Supports(floor)) return false;
            object entity = Interior.Owns(floor) ? Interior.Ship : Map.ResolveEntity(floor);
            if (entity == null || !Map.ShipType.IsInstanceOfType(entity)
                || Map.EntityType.GetValue(entity).ToString() != "CV") return false;
            var body = (Rigidbody)Map.EntityBody.GetValue(entity);
            // The visual entity/floor remains live when the native bounding body
            // is inactive (stationary interiors and remote ships). Sample that
            // stable pose; use the body only for locally simulated point velocity.
            Transform supportTransform = (Transform)Map.EntityTransform.GetValue(entity);
            if (supportTransform == null) return false;
            if (!ReferenceEquals(entity, p.SupportEntity))
            {
                ClearSupport(p, "support-changed");
                p.SupportEntity = entity;
                p.ShipId = Map.Id(entity);
                Log.Info("Floor contact acquired on CV " + p.ShipId);
            }
            p.SupportBody = body;
            p.SupportTransform = supportTransform;
            p.InteriorBody = Interior.Matches(entity, p.Body) ? Interior.Body : null;
            p.Floor = floor;
            p.ContactPoint = point;
            p.LastContact = Time.fixedTime;
            return true;
        }

        private bool CheckFloor(Passenger p)
        {
            Transform t = p.Controller.transform;
            if (Interior.Matches(p.SupportEntity, p.Body))
            {
                bool proxyHit = Interior.Raycast(new Ray(t.position + t.up * .2f, -t.up), .65f, out RaycastHit floor);
                p.SupportProbe = RayDetail("floor-proxy", proxyHit, floor, t.up);
                if (proxyHit && Vector3.Dot(floor.normal, t.up) >= .55f && AcceptFloor(p, floor.collider, floor.point)) return true;
                bool recent = Interior.Supports(p.Floor) && CheckRecentFloor(p, t);
                p.SupportProbe += "/recent=" + recent;
                return recent;
            }
            // Query the same Unity scene as this character, not the global default physics scene.
            PhysicsScene scene = p.Controller.gameObject.scene.GetPhysicsScene();
            int layers = (1 << 21) | (1 << 22) | (1 << 30);
            if (scene.IsValid() && scene.Raycast(t.position + t.up * 0.2f, -t.up, out RaycastHit hit,
                0.65f, layers, QueryTriggerInteraction.Ignore)
                && Vector3.Dot(hit.normal, t.up) >= 0.55f && AcceptFloor(p, hit.collider, hit.point)) return true;
            return CheckRecentFloor(p, t);
        }

        private static bool CheckRecentFloor(Passenger p, Transform t)
        {
            // Permit one or two solver ticks at edges, but require the actual floor to remain enabled and nearby.
            // Collider.Raycast supports concave meshes; ClosestPoint does not.
            if (p.Floor == null || !p.Floor.enabled || !p.Floor.gameObject.activeInHierarchy
                || Time.fixedTime - p.LastContact > .06f) return false;
            foreach (Vector3 offset in new[] { t.right * .15f, -t.right * .15f, t.forward * .15f, -t.forward * .15f })
                if (p.Floor.Raycast(new Ray(t.position + offset + t.up * .2f, -t.up), out RaycastHit edge, .65f)
                    && Vector3.Dot(edge.normal, t.up) >= .55f) return true;
            return false;
        }

        // The Mod API fixed event precedes Empyrion's explicit scene simulation.
        // The character prefix calls the same preparation for ordinary Unity
        // scene ordering; Advance's fixed-time guard schedules one target only.
        public void BeforePhysics()
        {
            if (disposed || failed || Options.Mode != RunMode.Experimental) return;
            Passenger p = passenger;
            if (p == null)
            {
                Rigidbody departing = Momentum.DepartingBody;
                if (departing != null)
                {
                    Component controller = departing.GetComponent(Map.ControllerType);
                    if (controller != null) p = GetPassenger(controller);
                }
            }
            if (p != null)
            {
                ExitTrace.Begin(Momentum.PeekPlayerDeparture(p.Entity, p.Body));
                string blocked = WalkingBlock(p);
                ExitTrace.Event("eligibility", p, p.Body.velocity, p.Body.velocity, "walking=" + (blocked ?? "eligible"));
                if (blocked == null) PrepareMotion(p);
            }
        }

        private void PrepareMotion(Passenger p)
        {
            PlayerDeparture departure = Momentum.TakePlayerDeparture(p.Entity, p.Body);
            if (departure != null)
            {
                ClearSupport(p, "seat-exit-start");
                p.SupportEntity = departure.Ship;
                p.SupportBody = departure.ShipBody;
                p.SupportTransform = (Transform)Map.EntityTransform.GetValue(departure.Ship);
                p.ShipId = Map.Id(departure.Ship);
                p.SupportVelocity = p.PreviousSupportVelocity = U(departure.Inertia.Inherited);
                p.Frame.BeginSeatExit(departure.Inertia.Inherited, Time.fixedTime);
                p.SeatExitStarted = Time.fixedTime;
                p.Ready = true;
                ExitTrace.Begin(departure);
            }
            if (Options.Mode == RunMode.Experimental && Options.PreserveInterior && p.SupportEntity != null)
            {
                if (Interior.Begin(p.SupportEntity, p.Body, departure != null))
                {
                    if (!Interior.Advance()) { ClearSupport(p, "interior-ownership-or-motion-changed"); return; }
                    p.InteriorBody = Interior.Body;
                    if (departure != null && Interior.TryVelocity(p.Body.worldCenterOfMass, out Vector3 velocity))
                    {
                        // Recompute the one-time inheritance at the corrected exit
                        // point. It is never repeated after a collision/solver step.
                        p.Body.velocity = velocity;
                        p.SupportVelocity = p.PreviousSupportVelocity = velocity;
                        p.Frame.BeginSeatExit(N(velocity), Time.fixedTime);
                    }
                }
                else p.InteriorBody = null;
            }
            if (departure != null)
                Log.Event("seat-exit-inertia", Options, p, departure.Before, p.Body.velocity,
                    "one-time-point-velocity;first-fixed-frame;movingInterior=" + Interior.Active);
        }

        public VelocityScope BeginFixed(Component controller)
        {
            Passenger p = GetPassenger(controller);
            if (p == null) return null;
            RestoreDrag(p);
            p.Sampling = Time.fixedTime - p.LastSample >= 0.2f;
            if (p.Sampling) p.LastSample = Time.fixedTime;
            p.Grounded = Map.Bool(Map.Grounded, controller);
            p.FixedTime = Time.fixedTime;
            ExitTrace.Begin(Momentum.PeekPlayerDeparture(p.Entity, p.Body));
            var scope = new VelocityScope { Owner = this, Passenger = p, Body = p.Body, Before = p.Body.velocity, Phase = "fixed" };
            string walkBlock = WalkingBlock(p);
            if (walkBlock != null)
            {
                ClearSupport(p, "not-walking:" + walkBlock);
                return TraceScope(scope, "not-walking:" + walkBlock);
            }
            PrepareMotion(p);
            scope.Before = p.Body.velocity;
            bool onFloor = p.Grounded && CheckFloor(p);
            if (!onFloor)
            {
                if (Options.Mode != RunMode.Experimental || !p.Frame.LeaveGround(Time.fixedTime) || !VerifyAirborne(p))
                {
                    ClearSupport(p, "left-vessel-or-airborne-limit");
                    return TraceScope(scope, "no-floor-or-airborne-support");
                }
            }
            else p.AirborneVerified = false;
            if (!SampleSupport(p))
            {
                ClearSupport(p, "support-motion-discontinuity");
                return TraceScope(scope, "motion-discontinuity");
            }
            if (!p.Ready && p.Frame.Phase != PassengerPhase.Detached)
            {
                ClearSupport(p, "support-velocity-unavailable");
                return TraceScope(scope, "velocity-unavailable");
            }
            if (Options.Mode == RunMode.Experimental && p.Ready)
            {
                NVector relative = onFloor ? p.Frame.BeginGrounded(N(p.Body.velocity), N(p.SupportVelocity))
                    : p.Frame.Relative(N(p.Body.velocity));
                if (!MotionMath.Finite(relative)) throw new InvalidOperationException("Non-finite relative velocity.");
                scope.Support = U(p.Frame.Velocity);
                p.Body.velocity = U(relative);
                scope.Changed = true;
                if (!onFloor) p.MotionSource = "airborne-departure";
            }
            p.AppliedLastTick = scope.Changed;
            return TraceScope(scope, scope.Changed ? (onFloor ? "grounded-frame" : "airborne-frame") : "not-experimental-or-unready");
        }

        private bool SampleSupport(Passenger p)
        {
            if (p.SupportEntity == null || p.SupportTransform == null || !p.SupportTransform.gameObject.activeInHierarchy) return false;
            if (Interior.Matches(p.SupportEntity, p.Body))
            {
                p.Ready = Interior.TryVelocity(p.Body.worldCenterOfMass, out Vector3 motion);
                p.SupportVelocity = p.PreviousSupportVelocity = motion;
                p.MotionSource = "moving-interior-trajectory";
                return p.Ready;
            }
            bool fromBody = p.SupportBody != null && p.SupportBody.gameObject.activeInHierarchy
                && !p.SupportBody.isKinematic && !Map.Bool(Map.EntityRemote, p.SupportEntity);
            // Visible entity poses update on render frames. A catch-up sequence of
            // physics steps must test the local body's physics pose, not a delayed
            // render pose that can jump several steps in one sample.
            Vector3 position = (fromBody ? p.SupportBody.position : p.SupportTransform.position) + Map.OriginOffset;
            Quaternion rotation = fromBody ? p.SupportBody.rotation : p.SupportTransform.rotation;
            Vector3 atFeet = p.Body.worldCenterOfMass;
            if (p.HavePose && p.PoseFromBody != fromBody) p.HavePose = false;
            float seconds = Time.fixedTime - p.PoseTime;
            bool wasReady = p.Ready;
            bool sameTick = p.HavePose && Mathf.Abs(seconds) < 0.0001f;
            if (p.HavePose && !sameTick && (!MotionMath.ContinuousPosition(N(p.PreviousPosition), N(position), seconds)
                || Quaternion.Angle(p.PreviousRotation, rotation) > 45f)) return false;
            Vector3 velocity = Vector3.zero;
            if (fromBody)
            {
                velocity = p.SupportBody.GetPointVelocity(atFeet);
                p.Ready = MotionMath.Finite(N(velocity)) && velocity.sqrMagnitude <= 300f * 300f;
                p.MotionSource = "rigidbody-point";
            }
            else
            {
                // Contacts and the limiter may query again after the same physics step.
                // Keep the last pose-derived velocity instead of dividing by zero time.
                if (sameTick) return true;
                NVector computed = default;
                p.Ready = p.HavePose && MotionMath.TryPoseVelocity(N(p.PreviousPosition), N(p.PreviousRotation),
                    N(position), N(rotation), N(atFeet + Map.OriginOffset), seconds, out computed);
                velocity = U(computed);
                p.MotionSource = "entity-pose-delta";
            }
            if (!p.Ready && ExitGrace(p))
            {
                velocity = U(p.Frame.Velocity);
                p.Ready = true;
                p.MotionSource = "seat-exit-inertia";
            }
            if (p.HavePose && wasReady && p.Ready && !sameTick
                && !MotionMath.Continuous(N(p.PreviousSupportVelocity), N(velocity), seconds)) return false;
            p.PreviousPosition = position;
            p.PreviousRotation = rotation;
            p.PoseTime = Time.fixedTime;
            p.HavePose = true;
            p.PoseFromBody = fromBody;
            p.SupportVelocity = velocity;
            p.PreviousSupportVelocity = velocity;
            return true;
        }

        private bool HasShipBelow(Passenger p)
        {
            if (p.Controller == null || p.SupportEntity == null || p.SupportTransform == null
                || !p.SupportTransform.gameObject.activeInHierarchy)
            { p.SupportProbe = "below:no-live-support"; return false; }
            Transform t = p.Controller.transform;
            if (Interior.Matches(p.SupportEntity, p.Body))
            {
                bool found = Interior.Raycast(new Ray(t.position + t.up * .2f, -t.up), 8f, out RaycastHit floor);
                p.SupportProbe = RayDetail("below-proxy", found, floor, t.up);
                return found && Vector3.Dot(floor.normal, t.up) >= .55f;
            }
            PhysicsScene scene = p.Controller.gameObject.scene.GetPhysicsScene();
            int layers = (1 << 21) | (1 << 22) | (1 << 30);
            // Retain only a bounded jump over actual geometry from the same vessel.
            // Never infer containment from the ship's outer bounding box alone.
            RaycastHit hit = default;
            bool didHit = scene.IsValid() && scene.Raycast(t.position + t.up * 0.2f, -t.up, out hit,
                8f, layers, QueryTriggerInteraction.Ignore) && hit.collider != null && hit.collider.attachedRigidbody != p.Body;
            bool sameShip = didHit && ReferenceEquals(Map.ResolveEntity(hit.collider), p.SupportEntity);
            p.SupportProbe = RayDetail("below-native", didHit, hit, t.up) + "/sameShip=" + sameShip;
            return sameShip && Vector3.Dot(hit.normal, t.up) >= .55f;
        }

        private static string RayDetail(string kind, bool hit, RaycastHit result, Vector3 up)
            => kind + (hit ? ":hit=" + result.collider.GetInstanceID() + "/distance="
                + result.distance.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)
                + "/upDot=" + Vector3.Dot(result.normal, up).ToString("F5", System.Globalization.CultureInfo.InvariantCulture) : ":miss");

        private bool VerifyAirborne(Passenger p)
        {
            bool below = HasShipBelow(p), grace = ExitGrace(p);
            p.AirborneVerified = p.Frame.CanRemainAirborne(Time.fixedTime, below || grace);
            p.SupportProbe += "/grace=" + grace + "/airborne=" + p.AirborneVerified;
            return p.AirborneVerified;
        }

        private static bool ExitGrace(Passenger p) => SeatExitInertia.WithinGrace(Time.fixedTime, p.SeatExitStarted);

        public VelocityScope BeginLimiter(Component controller)
        {
            if (passenger == null || passenger.Controller != controller) return null;
            Passenger p = passenger;
            RestoreDrag(p);
            if (!IsLocal(controller, out _, out _)) return null;
            var scope = new VelocityScope { Owner = this, Passenger = p, Body = p.Body, Before = p.Body.velocity, Phase = "late-limiter" };
            p.Grounded = Map.Bool(Map.Grounded, controller);
            string decision = "not-experimental";
            if (Options.Mode != RunMode.Experimental) return TraceScope(scope, decision);
            if (!p.Grounded) return TraceScope(scope, "native-not-grounded");
            string walkBlock = WalkingBlock(p);
            if (walkBlock != null) return TraceScope(scope, "not-walking:" + walkBlock);
            if (!CheckFloor(p)) return TraceScope(scope, "floor-unconfirmed");
            if (!SampleSupport(p)) { decision = "motion-discontinuity"; ClearSupport(p, "landing-motion-discontinuity"); }
            else if (p.Ready)
            {
                decision = "grounded-frame";
                scope.Landing = p.Frame.Phase == PassengerPhase.Airborne;
                Vector3 relative = U(p.Frame.LandingLimiter(N(p.Body.velocity), N(p.SupportVelocity)));
                if (!MotionMath.Finite(N(relative))) throw new InvalidOperationException("Non-finite landing velocity.");
                scope.Support = p.SupportVelocity;
                p.Body.velocity = relative;
                scope.Changed = true;
                p.AirborneVerified = false;
            }
            else decision = "velocity-unavailable";
            return TraceScope(scope, decision);
        }

        public void EndScope(VelocityScope scope, Exception originalError)
        {
            if (scope == null || scope.Closed) return;
            scope.Closed = true;
            Vector3 nativeAfter = scope.Body != null ? scope.Body.velocity : Vector3.zero;
            // Restoration happens before logging or state cleanup, even if the original throws.
            if (scope.Changed && scope.Body != null && !scope.Body.isKinematic)
                scope.Body.velocity += scope.Support;
            Passenger p = scope.Passenger;
            p.Grounded = p.Controller != null && Map.Bool(Map.Grounded, p.Controller);
            ExitTrace.Event(scope.Phase + "-native", p, scope.NativeBefore, nativeAfter,
                "decision=" + scope.Decision + ";wrapped=" + scope.Changed);
            if (originalError != null) { Fail(originalError); return; }
            Vector3 relativeDrag = Vector3.zero;
            if (scope.Phase == "fixed")
            {
                if (scope.Changed && !p.Grounded && CanWalk(p) && p.Frame.LeaveGround(Time.fixedTime) && VerifyAirborne(p))
                {
                    // Unity must solve collisions with world velocity. Replace only the
                    // native world drag with a force about the fixed departure frame.
                    float drag = p.Body.drag;
                    Vector3 acceleration = U(MotionMath.RelativeDragAcceleration(N(p.Body.velocity), p.Frame.Velocity,
                        drag, Time.fixedDeltaTime));
                    p.Body.drag = p.Drag.Begin(drag);
                    p.Body.AddForce(acceleration, ForceMode.Acceleration);
                    relativeDrag = acceleration;
                }
                else if (!p.Grounded) ClearSupport(p, "left-vessel-or-airborne-limit");
            }
            ExitTrace.Event(scope.Phase + "-after", p, scope.Before, scope.Body != null ? scope.Body.velocity : Vector3.zero,
                "decision=" + scope.Decision + ";wrapped=" + scope.Changed
                + ";relativeDragAcceleration=" + ExitDiagnostics.Vector(relativeDrag));
            // Logging can fail, so emit the landing event only after world velocity is restored.
            if (scope.Landing) Log.Event("landing", Options, p, scope.Before,
                scope.Body != null ? scope.Body.velocity : Vector3.zero, "rebased-before-limiter");
            if (p.Sampling) Log.Event(scope.Phase, Options, p, scope.Before,
                scope.Body != null ? scope.Body.velocity : Vector3.zero, p.MotionSource + ";relative=" + scope.Changed
                + ";phase=" + p.Frame.Phase + ";relativeDrag=" + p.Drag.Active);
        }

        private static void RestoreDrag(Passenger p)
        {
            if (!p.Drag.Active) return;
            float restored = p.Drag.Restore(p.Body != null ? p.Body.drag : 0f);
            if (p.Body != null) p.Body.drag = restored;
        }

        public Vector3? Probe(Component controller)
        {
            return passenger != null && passenger.Controller == controller && passenger.Sampling ? (Vector3?)passenger.Body.velocity : null;
        }

        public void EndProbe(Component controller, MethodBase method, Vector3? before)
        {
            if (before.HasValue && passenger != null && passenger.Controller == controller)
                Log.Event(method.Name, Options, passenger, before.Value, passenger.Body.velocity,
                    Options.Mode == RunMode.Experimental && passenger.AppliedLastTick ? "relative-frame" : "world-frame");
        }

        private bool WantsInterior(object ship)
        {
            return !restoringInterior && !failed && Options.Mode == RunMode.Experimental && Options.PreserveInterior
                && (Momentum.WantsExitInterior(ship)
                    || (passenger != null && passenger.Ready && ReferenceEquals(passenger.SupportEntity, ship)
                        && (passenger.Grounded || ExitGrace(passenger) || (passenger.AirborneVerified
                            && passenger.Frame.CanRemainAirborne(Time.fixedTime, true)))));
        }

        public void BeforeColliders(object ship, ref bool detailed, ref bool bounding)
        {
            if (WantsInterior(ship)) { detailed = true; bounding = false; interiorLease = ship; }
        }

        public void AfterColliders(object ship)
        {
            Momentum.Coasting.AfterSelection(ship);
            if (passenger == null || !ReferenceEquals(passenger.SupportEntity, ship) || Time.fixedTime - lastColliderTrace < 0.5f) return;
            lastColliderTrace = Time.fixedTime;
            var transform = (Transform)Map.EntityTransform.GetValue(ship);
            if (transform == null) return;
            Collider[] colliders = transform.GetComponentsInChildren<Collider>(true);
            int active = colliders.Count(c => c.enabled && c.gameObject.activeInHierarchy);
            Log.Event("collider-selection", Options, passenger, passenger.Body.velocity, passenger.Body.velocity,
                "active=" + active + "/" + colliders.Length + ";requestedDetailed=" + WantsInterior(ship));
        }

        private void ClearSupport(Passenger p, string reason)
        {
            RestoreDrag(p);
            if (p.SupportEntity != null)
            {
                Log.Event("detach", Options, p, p.Body.velocity, p.Body.velocity, reason + ";supportProbe=" + p.SupportProbe);
                ExitTrace.Event("detach", p, p.Body.velocity, p.Body.velocity, "reason=" + reason);
            }
            Interior.Reset();
            p.SupportEntity = null;
            p.SupportBody = null;
            p.InteriorBody = null;
            p.SupportTransform = null;
            p.Floor = null;
            p.ShipId = -1;
            p.Ready = p.HavePose = p.AppliedLastTick = false;
            p.Frame.Clear();
            p.AirborneVerified = false;
            p.SeatExitStarted = float.NegativeInfinity;
            p.SupportVelocity = p.PreviousSupportVelocity = Vector3.zero;
            p.LastContact = -100f;
        }

        private void RestoreInterior()
        {
            object ship = interiorLease;
            // A controller replacement during exit must not undo the detailed
            // selection before the new controller's first physics step.
            if (ship != null && Momentum.WantsExitInterior(ship)) return;
            interiorLease = null;
            if (ship == null) return;
            restoringInterior = true;
            try
            {
                if (Map.EntityTransform.GetValue(ship) is Transform t && t != null)
                    Map.ColliderSelection.Invoke(ship, new object[] { false, false });
            }
            finally { restoringInterior = false; }
        }

        public void Update()
        {
            Momentum.Update();
            ExitTrace.Update();
            Interior.RebaseOrigin();
            if (passenger != null) RestoreDrag(passenger);
            if (!ReferenceEquals(playfield, api.ClientPlayfield))
            {
                Reset("playfield-changed");
                playfield = api.ClientPlayfield;
            }
            if (passenger != null && (passenger.Controller == null || !CanWalk(passenger))) Reset("controller-inactive");
            if (passenger != null && passenger.Frame.Phase == PassengerPhase.Airborne && !VerifyAirborne(passenger))
                ClearSupport(passenger, "left-vessel-or-airborne-limit");
            if (interiorLease != null && !WantsInterior(interiorLease)) RestoreInterior();
            if (passenger?.SupportEntity != null && WantsInterior(passenger.SupportEntity) && interiorLease == null)
                Map.ColliderSelection.Invoke(passenger.SupportEntity, new object[] { true, false });
            if (Time.realtimeSinceStartup - lastFlush > 1f) { Log.Flush(); lastFlush = Time.realtimeSinceStartup; }
        }

        public void Reset(string reason)
        {
            // Character controller changes during seat exit must not cancel a pending
            // server handoff. Global lifecycle and mode resets still clear all motion.
            bool controllerReset = reason == "controller-changed" || reason == "controller-inactive" || reason == "controller-disabled";
            if (!controllerReset) Momentum.Reset();
            if (passenger != null && passenger.Body != null) ClearSupport(passenger, reason);
            else Interior.Reset();
            // A changing/disabled controller is part of the departure failure we
            // need to observe. Keep the bounded window for its replacement.
            if (!controllerReset) ExitTrace.Close();
            passenger = null;
            RestoreInterior();
        }

        public void Disable(Component controller)
        {
            if (passenger?.Controller == controller) Reset("controller-disabled");
        }

        public void Fail(Exception error)
        {
            if (failed) return;
            failed = true;
            Options.Mode = RunMode.Off;
            try { Reset("mod-error"); } catch (Exception cleanup) { api.LogError("[ShipWalk] Cleanup: " + cleanup.Message); }
            api.LogError("[ShipWalk] Experimental behavior disabled after error: " + error);
        }

        public void Command(List<string> args)
        {
            string command = args?.FirstOrDefault()?.ToLowerInvariant() ?? "status";
            if (failed && command != "status") { Tell("Disabled after an error; restart after inspecting the client log."); return; }
            switch (command)
            {
                case "on": Reset("mode-change"); Options.Mode = RunMode.Experimental; break;
                case "off": Reset("mode-change"); Options.Mode = RunMode.Off; break;
                case "diagnostics": Reset("mode-change"); Options.Mode = RunMode.Diagnostics; break;
                case "interior":
                    if (args.Count != 2 || (args[1] != "on" && args[1] != "off")) { Tell("Usage: mod exs interior on|off (or mod ex <mod-num> interior on|off)"); return; }
                    Options.PreserveInterior = args[1] == "on";
                    if (!Options.PreserveInterior) { Interior.Reset(); if (passenger != null) passenger.InteriorBody = null; RestoreInterior(); }
                    break;
                case "seatexit":
                    if (args.Count != 2 || (args[1] != "on" && args[1] != "off")) { Tell("Usage: mod exs seatexit on|off (or mod ex <mod-num> seatexit on|off)"); return; }
                    Options.AllowMovingSeatExit = args[1] == "on";
                    if (!Options.AllowMovingSeatExit) Momentum.Reset();
                    break;
                case "momentum":
                    if (args.Count != 2 || (args[1] != "on" && args[1] != "off")) { Tell("Usage: mod exs momentum on|off (or mod ex <mod-num> momentum on|off)"); return; }
                    Options.PreserveExitMomentum = args[1] == "on";
                    if (!Options.PreserveExitMomentum) Momentum.Reset();
                    break;
                case "trace":
                    if (args.Count != 2 || (args[1] != "on" && args[1] != "off")) { Tell("Usage: mod exs trace on|off (or mod ex <mod-num> trace on|off)"); return; }
                    Options.Trace = args[1] == "on";
                    Log.Flush();
                    break;
                case "status": break;
                default: Tell("Commands: status, diagnostics, on, off, seatexit on|off, momentum on|off, interior on|off, trace on|off"); return;
            }
            Tell("mode=" + Options.Mode + "; interior=" + Options.PreserveInterior + "; trace=" + Options.Trace
                + "; seatExit=" + Options.AllowMovingSeatExit + "; exitMomentum=" + Options.PreserveExitMomentum
                + "; application=" + api.Application.Mode + "; seatExitTypes=CV,SV,HV; seatExitScope=OpenOnly; supported CV=" + (passenger?.ShipId ?? -1)
                + "; phase=" + (passenger?.Frame.Phase ?? PassengerPhase.Detached)
                + "; movingInterior=" + Interior.Active + "; interiorShapes=" + Interior.Count + "; failed=" + failed);
        }

        private void Tell(string message)
        {
            Log.Info(message);
            if (api.GUI != null) api.GUI.ShowGameMessage("ShipWalk: " + message, prio: 1);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Reset("shutdown"); }
            finally
            {
                if (RemoveHooks()) fieldResolver.Dispose();
                if (ReferenceEquals(Current, this)) Current = null;
                Log.Dispose();
            }
        }

        private static NVector N(Vector3 v) => new NVector(v.x, v.y, v.z);
        private static NQuaternion N(Quaternion q) => new NQuaternion(q.x, q.y, q.z, q.w);
        private static Vector3 U(NVector v) => new Vector3(v.X, v.Y, v.Z);
    }
}
