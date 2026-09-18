using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eleon.Modding;
using HarmonyLib;
using UnityEngine;

namespace ShipWalk
{
    // Kept for the inherited offline diagnostic fixtures. The lab never creates
    // a legacy passenger, moving world interior or Cartesian collision lease.
    internal sealed class Passenger
    {
        public Component Controller;
        public Rigidbody Body, SupportBody, InteriorBody;
        public object Entity, SupportEntity;
        public Transform SupportTransform;
        public Collider Floor;
        public int ShipId = -1;
        public bool Grounded, Ready, AppliedLastTick, HavePose, Sampling, PoseFromBody, AirborneVerified;
        public float SeatExitStarted = float.NegativeInfinity, LastContact = -100f, PoseTime, FixedTime, LastSample = -100f;
        public Vector3 ContactPoint, SupportVelocity, PreviousSupportVelocity, PreviousPosition;
        public Quaternion PreviousRotation;
        public string MotionSource, SupportProbe = "not-queried";
        public readonly MotionFrame Frame = new MotionFrame();
        public readonly DragOverride Drag = new DragOverride();
    }

    internal sealed class Runtime : IDisposable
    {
        internal static Runtime Current;
        private const string HarmonyId = "privatecoop.shipwalk.multiplayer.5150";
        private readonly IModApi api;
        internal readonly Build5150 Map;
        internal readonly Settings Options;
        internal readonly TraceLog Log;
        internal readonly SeatMomentum Momentum;
        internal readonly LocalFrameSession Frame;
        internal readonly PeerDiagnostics Peers;
        internal readonly SharedFrameNetwork Network;
        internal readonly TravelCoordinator Travel;
        internal bool PlayfieldServer => api.Application.Mode == ApplicationMode.PlayfieldServer;
        internal bool MultiplayerClient => api.Application.Mode == ApplicationMode.Client;
        internal bool ClientMovement => api.Application.Mode == ApplicationMode.SinglePlayer || MultiplayerClient;
        internal bool OwnsShipPhysics => ClientFramePolicy.OwnsShipPhysics(api.Application.Mode == ApplicationMode.SinglePlayer);
        private readonly Harmony harmony;
        private readonly TypedFieldResolver fieldResolver;
        private readonly LocalFrameControl control = new LocalFrameControl();
        private readonly List<MethodInfo> attemptedHooks = new List<MethodInfo>();
        private LocalFrameDriver driver;
        private IPlayfield playfield;
        private bool disposed, failed, stopping;
        private float lastFlush;

        public Runtime(IModApi api, string folder)
        {
            this.api = api;
            Options = Settings.Load(System.IO.Path.Combine(folder, "ShipWalk.cfg"));
            // A headless playfield has no player console. It handles native seat
            // releases automatically. Clients enable after the server handshake.
            Options.Mode = PlayfieldServer ? RunMode.Experimental : RunMode.Diagnostics;
            Options.PreserveInterior = false;
            if (PlayfieldServer) Options.AllowMovingSeatExit = Options.PreserveExitMomentum = true;
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
            Map = new Build5150(game, PlayfieldServer);
            Log = new TraceLog(api, System.IO.Path.Combine(folder, "Logs"));
            Frame = new LocalFrameSession(this, api, Map);
            Momentum = new SeatMomentum(api, Map, Options, Log);
            Peers = new PeerDiagnostics(api, Map, Log);
            Network = new SharedFrameNetwork(this, api, Map, game);
            Travel = new TravelCoordinator(this, api, game);
            harmony = new Harmony(HarmonyId); fieldResolver = new TypedFieldResolver(game);
            playfield = api.ClientPlayfield;
        }

        public void Install()
        {
            if (Current != null || AppDomain.CurrentDomain.GetAssemblies().Any(a => a != typeof(Runtime).Assembly && a.GetName().Name == "ShipWalk"))
                throw new InvalidOperationException("Load only one ShipWalk build at a time.");
            if (!ClientMovement && !PlayfieldServer)
                throw new NotSupportedException("ShipWalk runs on clients and gameplay playfield workers.");
            Current = this;
            try
            {
                fieldResolver.Install();
                Network.Transport.RegisterPacket();
                Log.Info("Native gameplay packet registered: ModGameEvent=139; client/playfield receive factory ready; bindings=" + Map.Native.ProfileName + ".");
                Hook(Network.Transport.Receive, postfix: nameof(Hooks.FramePacketPostfix));
                Hook(Network.Transport.Channel, prefix: nameof(Hooks.FrameChannelPrefix));
                if (PlayfieldServer)
                {
                    Hook(Travel.Native.Recovery, transpiler: nameof(Hooks.TravelRecoveryTranspiler));
                    Hook(Travel.Native.WarpReceive, prefix: nameof(Hooks.TravelWarpReceivePrefix), finalizer: nameof(Hooks.TravelWarpReceiveFinalizer));
                    Hook(Travel.Native.WarpStarted, postfix: nameof(Hooks.TravelWarpPostfix));
                    Hook(Travel.Native.NativeBroadcast, prefix: nameof(Hooks.TravelMicroBroadcastPrefix));
                    Hook(Map.ServerSeatDetach, nameof(Hooks.SeatDetachPrefix), finalizer: nameof(Hooks.SeatDetachFinalizer));
                    Hook(Map.RemotePose, nameof(Hooks.RemotePosePrefix), nameof(Hooks.RemotePosePostfix));
                    Hook(Map.ColliderSelection, nameof(Hooks.ColliderPrefix), nameof(Hooks.ColliderPostfix),
                        transpiler: nameof(Hooks.BodyActivationTranspiler));
                    Hook(Map.ShipUpdate, postfix: nameof(Hooks.ShipUpdatePostfix), transpiler: nameof(Hooks.ShipKinematicTranspiler));
                    Hook(Map.ShipControl, transpiler: nameof(Hooks.ShipControlTranspiler));
                    Hook(Map.ShipFixedUpdate, nameof(Hooks.ShipFixedPrefix), nameof(Hooks.ShipFixedPostfix),
                        transpiler: nameof(Hooks.ShipCacheTranspiler));
                    Hook(Map.ShipForces, transpiler: nameof(Hooks.ShipBrakeTranspiler));
                    Hook(Map.ShipCollision, postfix: nameof(Hooks.ShipCollisionPostfix));
                    Log.Info("Loaded ShipWalk v" + typeof(Runtime).Assembly.GetName().Version.ToString(3)
                        + "; application=PlayfieldServer; native seat handoff active; startup=automatic; collision=native-bounding; velocityTransfer=once."
                        + " Shared passenger-frame protocol=" + FrameProtocol.Version + "; awaiting native client handshake.");
                    return;
                }
                Hook(Map.FixedUpdate, nameof(Hooks.FixedPrefix));
                if (MultiplayerClient)
                {
                    Hook(Travel.Native.Boundary, nameof(Hooks.TravelBoundaryPrefix));
                    Hook(Travel.Native.Recovery, transpiler: nameof(Hooks.TravelRecoveryTranspiler));
                    Hook(Travel.Native.WorldRequest, nameof(Hooks.TravelRequestPrefix));
                    Hook(Travel.Native.Arrival, nameof(Hooks.TravelArrivalPrefix));
                    Hook(Travel.Native.MicroReceive, postfix: nameof(Hooks.TravelMicroPostfix));
                }
                Hook(Map.Limiter, nameof(Hooks.LimiterPrefix));
                Hook(Map.Disable, nameof(Hooks.DisablePrefix));
                Hook(Map.ColliderSelection, nameof(Hooks.ColliderPrefix), nameof(Hooks.ColliderPostfix),
                    transpiler: nameof(Hooks.BodyActivationTranspiler));
                Hook(Map.SeatExit, transpiler: nameof(Hooks.SeatExitTranspiler));
                Hook(Map.ClientSeatDetach, nameof(Hooks.SeatDetachPrefix), finalizer: nameof(Hooks.SeatDetachFinalizer));
                Hook(Map.ShipUpdate, postfix: nameof(Hooks.ShipUpdatePostfix));
                Hook(Map.CharacterPresentation, nameof(Hooks.CharacterPresentationPrefix), nameof(Hooks.CharacterPresentationPostfix));
                Hook(Map.ShipPresentation, postfix: nameof(Hooks.ShipPresentationPostfix));
                Hook(Map.LocomotionUpdate, transpiler: nameof(Hooks.LocomotionTranspiler));
                Hook(Map.RoomEnter, nameof(Hooks.RoomEnterPrefix));
                Hook(Map.ShipControl, transpiler: nameof(Hooks.ShipControlTranspiler));
                Hook(Map.LookInput, nameof(Hooks.LookInputPrefix));
                Hook(Map.CharacterUpdate, transpiler: nameof(Hooks.WalkingLookTranspiler));
                Hook(Map.BlockContactScan, transpiler: nameof(Hooks.BlockContactBoundsTranspiler));
                Hook(Map.BlockContactVisit, transpiler: nameof(Hooks.BlockContactPointTranspiler));
                var go = new GameObject("ShipWalk Local Frame driver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                driver = go.AddComponent<LocalFrameDriver>(); driver.Owner = this;
                Log.Info("Loaded ShipWalk v" + typeof(Runtime).Assembly.GetName().Version.ToString(3)
                    + ", build 5150; shared passenger-frame protocol=" + FrameProtocol.Version + "; playfieldHandoff=requires-matching-server-mod."
                    + " Multiplayer startup=automatic after server handshake; single-player uses mod exs on. Native two-client validation pending.");
            }
            catch
            {
                if (RemoveHooks()) fieldResolver.Dispose(); Current = null; throw;
            }
        }

        private void Hook(MethodInfo method, string prefix = null, string postfix = null, string finalizer = null, string transpiler = null)
        {
            HarmonyMethod Find(string name) => name == null ? null : new HarmonyMethod(typeof(Hooks).GetMethod(name));
            attemptedHooks.Add(method);
            harmony.Patch(method, Find(prefix), Find(postfix), Find(transpiler), Find(finalizer));
        }
        private bool RemoveHooks()
        {
            bool removed = true;
            for (int i = attemptedHooks.Count - 1; i >= 0; i--)
                try { harmony.Unpatch(attemptedHooks[i], HarmonyPatchType.All, HarmonyId); attemptedHooks.RemoveAt(i); }
                catch (Exception error) { removed = false; api.LogError("[ShipWalk] Hook cleanup: " + error); }
            return removed;
        }

        public bool AllowMovingSeatExit(object ship, object actor)
        {
            IPlayer p = api.Application.LocalPlayer;
            return !failed && !disposed && Options.Mode == RunMode.Experimental && Frame.ContainsShip(ship)
                && ClientMovement && p != null && p.Health > 0f
                && Map.Id(actor) == p.Id && p.DrivingEntity?.Id == Map.Id(ship)
                && ReferenceEquals(Map.SeatedShip.GetValue(actor), ship)
                && Map.IsOpenSeat(actor, p.DrivingEntity.Structure, out _);
        }
        public void BeforePhysics() { if (!failed && !disposed && !PlayfieldServer) Frame.TryActivate(); }
        public bool RunNativeController(Component controller)
        {
            BeforePhysics();
            return !Frame.Owns(controller) && !Frame.OwnsArrival(controller);
        }
        public void BeforeColliders(object ship, ref bool detailed, ref bool bounding)
        { if (Frame.NeedsBounding(ship) || (PlayfieldServer || OwnsShipPhysics) && Momentum.Coasting.WantsBounding(ship)) { detailed = false; bounding = true; } }
        public void AfterColliders(object ship)
        { Frame.SelectBounding(ship); Momentum.Coasting.AfterSelection(ship); }
        public void Disable(Component controller)
        { if (Frame.Owns(controller)) StopFrame("native controller disabled", false); }

        public void Update()
        {
            if (PlayfieldServer)
            {
                Network.Update();
                Travel.Update();
                Momentum.Update();
                if (Time.realtimeSinceStartup - lastFlush > 1f) { Log.Flush(); lastFlush = Time.realtimeSinceStartup; }
                return;
            }
            if (!ReferenceEquals(playfield, api.ClientPlayfield))
            { Reset("playfield changed"); playfield = api.ClientPlayfield; }
            Travel.Update(); Frame.Update(); Momentum.Update(); PrepareWhenAboard();
            Network.Update();
            AutomaticStartup();
            Peers.Tick(control.Enabled && Options.Trace);
            if (Time.realtimeSinceStartup - lastFlush > 1f) { Log.Flush(); lastFlush = Time.realtimeSinceStartup; }
        }
        public void StopFrame(string reason, bool inherit = true, bool reselect = true)
        {
            if (stopping) return;
            stopping = true;
            object ship = Frame.Ship;
            bool restoreSelection = Frame.OwnsShipPhysics && Frame.HasNativeOverrides;
            try
            {
                Frame.Disarm(() =>
                {
                    Momentum.Reset();
                    if (OwnsShipPhysics && reselect && restoreSelection && ship != null && Map.EntityTransform.GetValue(ship) is Transform t && t != null)
                        Map.ColliderSelection.Invoke(ship, new object[] { false, false });
                }, inherit);
                if (ship != null) Log.Info("LocalFrame stopped: " + reason);
            }
            finally { stopping = false; }
        }
        public void Reset(string reason)
        {
            Network.Reset();
            if (PlayfieldServer) { Momentum.Reset(); Log.Info("Playfield handoff reset: " + reason); return; }
            StopFrame(reason, false);
            control.ResetSession(MultiplayerClient);
            if (!control.Enabled && !failed) Options.Mode = RunMode.Diagnostics;
        }
        private void EnableMovement()
        {
            Options.Mode = RunMode.Experimental;
            Options.AllowMovingSeatExit = Options.PreserveExitMomentum = true;
        }
        private void AutomaticStartup()
        {
            bool ready = !failed && !disposed && MultiplayerClient && api.Application.State == GameState.Running
                && api.Application.LocalPlayer != null && api.ClientPlayfield != null && Network.TravelReady;
            if (!control.TryEnableAutomatically(ready)) return;
            EnableMovement();
            Log.Info("Automatically enabled after ShipWalk server handshake; ship detection active; no on command required.");
            PrepareWhenAboard();
        }
        private void PrepareWhenAboard()
        {
            if (!control.Enabled || failed || disposed || Travel.Restoring) return;
            IPlayer player = api.Application.LocalPlayer;
            IEntity vessel = null;
            bool seated = false;
            if (api.Application.State == GameState.Running && player != null && player.Health > 0f)
            {
                vessel = player.DrivingEntity; seated = vessel != null;
                if (vessel == null) vessel = player.CurrentStructure?.Entity;
                if (!LocalFrameMath.IsVessel(vessel?.Type.ToString())) vessel = null;
            }
            object root = DockingVessels.Root(Map, Map.NativeEntity(vessel));
            if (!control.ObserveContext(root == null ? (int?)null : Map.Id(root), seated, Frame.HasSession)) return;
            try
            {
                Frame.Arm(vessel, seated);
                Tell(seated ? "Preparing ship interior. Moving-seat exit will be available when Ready."
                    : "Preparing the ship you are aboard. Normal movement stays active until Ready.");
            }
            catch (NotSupportedException error) { Tell("Could not prepare this ship: " + error.Message); }
            catch (InvalidOperationException error) { Tell("Could not prepare this ship: " + error.Message); }
        }
        public void Fail(Exception error)
        {
            if (failed) return;
            failed = true; control.Disable(); Options.Mode = RunMode.Off;
            Travel.CancelLocal("runtime stopped");
            try { StopFrame("error", false); } catch (Exception cleanup) { api.LogError("[ShipWalk] Cleanup: " + cleanup); }
            api.LogError("[ShipWalk] Local Frame Lab disabled: " + error);
        }
        public void Command(List<string> args)
        {
            string command = args?.FirstOrDefault()?.ToLowerInvariant() ?? "status";
            if (failed && command != "status") { Tell("Disabled after an error; inspect the client log and restart."); return; }
            if (PlayfieldServer)
            {
                if (command == "off") { Options.Mode = RunMode.Off; Momentum.Reset(); }
                else if (command == "on") Options.Mode = RunMode.Experimental;
                Log.Info("application=PlayfieldServer; nativeHandoff=" + (Options.Mode == RunMode.Experimental)
                    + "; coastingShips=" + Momentum.Coasting.Count + "; " + Network.Status + "; failed=" + failed);
                return;
            }
            bool enable = command == "on" && args.Count == 1
                || command == "frame" && args.Count == 2 && string.Equals(args[1], "on", StringComparison.OrdinalIgnoreCase);
            bool disable = command == "off" && args.Count == 1
                || command == "frame" && args.Count == 2 && string.Equals(args[1], "off", StringComparison.OrdinalIgnoreCase);
            if (enable)
            {
                control.Enable(); EnableMovement();
                PrepareWhenAboard();
                if (!Frame.HasSession)
                { Tell("Enabled. ShipWalk follows your ship automatically; normal world movement applies outside."); return; }
            }
            else if (disable)
            { Travel.CancelLocal("command off"); control.Disable(); StopFrame("command off"); Options.Mode = RunMode.Diagnostics; }
            else if (command == "trace" && args.Count == 2 && (args[1] == "on" || args[1] == "off")) Options.Trace = args[1] == "on";
            else if (command == "peers" && args.Count == 1) { Peers.Capture(); Tell("Observed peer positions written to the client log."); return; }
            else if (command == "network" && args.Count == 1) { Tell(Network.Status + "; " + Travel.Status); return; }
            else if (command != "status") { Tell("Commands: on, off, status, network, peers, trace on|off. Enable anywhere; ship detection is automatic, seated or on foot."); return; }
            Tell("enabled=" + control.Enabled + "; movement=" + (Frame.Active ? "Ship" : "World")
                + "; " + Frame.Status + "; application=" + api.Application.Mode
                + "; " + Network.Status + "; " + Travel.Status + "; failed=" + failed);
        }
        internal void Tell(string message)
        { Log.Info(message); api.GUI?.ShowGameMessage("ShipWalk: " + message, prio: 1); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Reset("shutdown"); }
            finally
            {
                if (driver != null) { driver.Owner = null; UnityEngine.Object.Destroy(driver.gameObject); }
                if (RemoveHooks()) fieldResolver.Dispose();
                if (ReferenceEquals(Current, this)) Current = null;
                Log.Dispose();
            }
        }
    }
}
