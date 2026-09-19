using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ShipWalk
{
    internal sealed class NativeSeatOperation : IDisposable
    {
        internal SeatRelease Release;
        internal IDisposable Motion;
        internal bool LocalSeatChange;
        public void Dispose() { Motion?.Dispose(); Motion = null; }
    }

    internal static class Hooks
    {
        public static IEnumerable<CodeInstruction> TravelRecoveryTranspiler(IEnumerable<CodeInstruction> instructions)
            => TravelRecoveryPatch.Rewrite(instructions, typeof(Hooks).GetMethod(nameof(TravelRecoveryResult)));
        public static int TravelRecoveryResult(int result, object actor)
        {
            try { return Runtime.Current?.Travel.RecoveryResult(result, actor) ?? result; }
            catch (Exception e) { Runtime.Current?.Log.Info("Travel recovery filter failed: " + e.GetBaseException()); return result; }
        }
        public static bool TravelBoundaryPrefix(object __instance, object __0, ref bool __result)
        {
            try { if (Runtime.Current?.Travel.Boundary(__instance, __0) != false) return true; }
            catch (Exception e) { Runtime.Current?.Log.Info("Travel boundary failed: " + e.GetBaseException()); }
            __result = false; return false;
        }
        public static bool TravelRequestPrefix(object __0)
        {
            try { return Runtime.Current?.Travel.WorldRequest(Convert.ToInt32(__0)) ?? true; }
            catch (Exception e) { Runtime.Current?.Log.Info("Travel request failed: " + e.GetBaseException()); return false; }
        }
        public static void TravelArrivalPrefix(string __1, string __2, ref Vector3 __3, ref Quaternion __4)
        {
            try { Runtime.Current?.Travel.Arrival(__1, __2, ref __3, ref __4); }
            catch (Exception e) { Runtime.Current?.Log.Info("Travel arrival failed: " + e.GetBaseException()); }
        }
        public static void TravelWarpPostfix(object[] __args)
        {
            try { Runtime.Current?.Travel.WarpStarted(__args); }
            catch (Exception e) { Runtime.Current?.Log.Info("Travel warp capture failed: " + e.GetBaseException()); }
        }
        public static void TravelWarpReceivePrefix(object __instance, out IDisposable __state)
        {
            __state = null;
            try { __state = Runtime.Current?.Travel.BeginNativeWarp(__instance); }
            catch (Exception e) { Runtime.Current?.Log.Info("Travel warp sender check failed: " + e.GetBaseException()); }
        }
        public static Exception TravelWarpReceiveFinalizer(Exception __exception, IDisposable __state)
        { __state?.Dispose(); return __exception; }
        public static void TravelMicroBroadcastPrefix(object __0, object __1)
        {
            try { Runtime.Current?.Travel.MicroBroadcast(__0, __1); }
            catch (Exception e) { Runtime.Current?.Log.Info("MicroWarp forwarding failed: " + e.GetBaseException()); }
        }
        public static void TravelMicroPostfix(object __instance)
        {
            try { Runtime.Current?.Travel.MicroArrived(__instance); }
            catch (Exception e) { Runtime.Current?.Log.Info("MicroWarp local frame failed: " + e.GetBaseException()); }
        }
        public static bool FrameChannelPrefix(object __instance, ref int __result)
        {
            if (Runtime.Current?.Network.Transport.IsFramePacket(__instance) != true) return true;
            // Channel 4 terminates in the dedicated manager. Its channel 1
            // ConfigScope forwards the existing packet bytes to the worker.
            __result = 1; return false;
        }
        public static void FramePacketPostfix(object __instance)
        {
            try { Runtime.Current?.Network.Receive(__instance); }
            catch (Exception error) { Runtime.Current?.Network.Fault(error); }
        }
        public static void CharacterPresentationPostfix(object __instance) => Runtime.Current?.Network.Present(actor: __instance);
        public static bool RoomEnterPrefix(Component __instance, Collider __0)
        {
            try { return !(Runtime.Current?.Frame.IsOwnHullRoomContact(__instance, __0) ?? false); }
            catch (Exception error) { Runtime.Current?.Fail(error); return true; }
        }
        public static void CharacterPresentationPrefix(object __instance)
        {
            try
            {
                Runtime r = Runtime.Current;
                if (r?.Frame.OwnsActor(__instance) == true) r.Frame.PublishRender(false);
            }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static void ShipPresentationPostfix(object __instance)
        {
            try
            {
                Runtime r = Runtime.Current;
                if (r?.Frame.Matches(__instance) == true) r.Frame.AfterShipPresentation();
            }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static IEnumerable<CodeInstruction> LocomotionTranspiler(IEnumerable<CodeInstruction> instructions)
            => LocomotionPatch.Rewrite(instructions,
                typeof(Vector3).GetMethod("op_Division", new[] { typeof(Vector3), typeof(float) }),
                typeof(Hooks).GetMethod(nameof(RelativeLocomotion)));
        public static Vector3 RelativeLocomotion(Vector3 nativeDelta, object actor)
        {
            try
            {
                Runtime r = Runtime.Current;
                if (r?.Frame.OwnsActor(actor) == true) return r.Frame.TakeLocomotion(nativeDelta);
                return r != null && r.Network.TakeLocomotion(actor, nativeDelta, out Vector3 relative) ? relative : nativeDelta;
            }
            catch (Exception error) { Runtime.Current?.Fail(error); return nativeDelta; }
        }

        public static void SeatDetachPrefix(object __instance, object __2, out NativeSeatOperation __state)
        {
            __state = null;
            try
            {
                Runtime r = Runtime.Current;
                if (r == null) return;
                if (r.PlayfieldServer)
                {
                    object serverVessel = __2 ?? r.Map.SeatedShip.GetValue(__instance);
                    __state = new NativeSeatOperation { Motion = r.Momentum.Coasting.EnterSeatTransition(serverVessel) };
                    if (__2 == null) __state.Release = r.Momentum.Begin(__instance, __2);
                    return;
                }
                if (!r.Frame.MatchesActor(__instance)) return;
                __state = new NativeSeatOperation();
                object vessel = __2 ?? r.Map.SeatedShip.GetValue(__instance);
                __state.LocalSeatChange = true;
                r.Frame.BeginSeatOperation(__2);
                if (r.Frame.OwnsShipPhysics && r.Frame.Matches(vessel)) __state.Motion = r.Momentum.Coasting.EnterSeatTransition(vessel);
                if (__2 != null && r.Frame.OwnsActor(__instance))
                {
                    if (r.Frame.ContainsShip(__2)) r.Frame.SuspendForSeat();
                    else r.StopFrame("boarding another vessel", false, false);
                }
                if (r.Frame.OwnsShipPhysics && __2 == null && r.Frame.Matches(vessel)) __state.Release = r.Momentum.Begin(__instance, __2);
            }
            catch (Exception error) { __state?.Dispose(); Runtime.Current?.Fail(error); }
        }
        public static Exception SeatDetachFinalizer(Exception __exception, NativeSeatOperation __state)
        {
            try
            {
                __state?.Release?.Owner.End(__state.Release, __exception);
                if (__exception == null && __state?.LocalSeatChange == true) Runtime.Current?.Frame.CompleteSeatOperation();
                else if (__exception != null) Runtime.Current?.StopFrame("native seat operation failed", false);
            }
            catch (Exception error) { Runtime.Current?.Fail(error); }
            finally { __state?.Dispose(); }
            return __exception;
        }
        public static void RemotePosePrefix(Vector3 __0, Vector3 __1, out RemotePoseArguments __state)
            => __state = new RemotePoseArguments(__0, __1);
        public static void RemotePosePostfix(object __instance, RemotePoseArguments __state)
        {
            // SearchTemplate overwrites its by-value position argument's x
            // with a history reciprocal before returning. Postfix args are not
            // the original input; only the prefix snapshot is a usable pose.
            try { Runtime.Current?.Momentum.ObserveRemote(__instance, __state.Position, __state.Rotation); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static IEnumerable<CodeInstruction> ShipCacheTranspiler(IEnumerable<CodeInstruction> instructions)
            => CoastingControllerPatch.RewriteCaches(instructions, Runtime.Current.Map.ShipWorldVelocityCache,
                Runtime.Current.Map.ShipLocalVelocityCache, typeof(Hooks).GetMethod(nameof(ReadCoastingCache)));
        public static Vector3 ReadCoastingCache(Vector3 cached, Component controller, bool local)
        {
            try { return Runtime.Current?.Momentum.Coasting.ReadVelocityCache(cached, controller, local) ?? cached; }
            catch (Exception error) { Runtime.Current?.Fail(error); return cached; }
        }
        public static IEnumerable<CodeInstruction> ShipBrakeTranspiler(IEnumerable<CodeInstruction> instructions)
            => CoastingControllerPatch.RewriteBraking(instructions, Runtime.Current.Map.ShipAutoBrake,
                typeof(Hooks).GetMethod(nameof(ReadCoastingBrake)));
        public static bool ReadCoastingBrake(bool requested, object controller)
        {
            try { return Runtime.Current?.Momentum.Coasting.AutoBrake(requested, controller) ?? requested; }
            catch (Exception error) { Runtime.Current?.Fail(error); return requested; }
        }
        public static void ShipFixedPrefix(object __instance, out ShipControllerTick __state)
        {
            __state = default;
            try { if (Runtime.Current != null) __state = Runtime.Current.Momentum.Coasting.BeforeController(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static void ShipFixedPostfix(object __instance, ShipControllerTick __state)
        {
            try { Runtime.Current?.Momentum.Coasting.AfterController(__instance, __state); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static void ShipCollisionPostfix(object __instance, Collision __0)
        {
            try { Runtime.Current?.Momentum.Coasting.Collision(__instance, __0); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static IEnumerable<CodeInstruction> ShipKinematicTranspiler(IEnumerable<CodeInstruction> instructions)
            => ShipKinematicPatch.Rewrite(instructions,
                typeof(Rigidbody).GetProperty(nameof(Rigidbody.isKinematic)).GetGetMethod(),
                typeof(Rigidbody).GetProperty(nameof(Rigidbody.isKinematic)).GetSetMethod(),
                typeof(Hooks).GetMethod(nameof(ReadShipKinematic)), typeof(Hooks).GetMethod(nameof(WriteShipKinematic)));
        public static bool ReadShipKinematic(Rigidbody body, object ship)
        {
            try { return Runtime.Current?.Momentum.Coasting.ReadKinematic(body, ship) ?? body.isKinematic; }
            catch (Exception error) { Runtime.Current?.Fail(error); return body.isKinematic; }
        }
        public static void WriteShipKinematic(Rigidbody body, bool value, object ship)
        {
            try
            {
                if (Runtime.Current == null) body.isKinematic = value;
                else Runtime.Current.Momentum.Coasting.SetKinematic(body, value, ship);
            }
            catch (Exception error) { Runtime.Current?.Fail(error); body.isKinematic = value; }
        }
        public static IEnumerable<CodeInstruction> ShipControlTranspiler(IEnumerable<CodeInstruction> instructions)
            => SeatMotionPatch.Rewrite(instructions, typeof(Vector3).GetProperty(nameof(Vector3.zero)).GetGetMethod(),
                typeof(Rigidbody).GetProperty(nameof(Rigidbody.velocity)).GetSetMethod(),
                typeof(Rigidbody).GetProperty(nameof(Rigidbody.angularVelocity)).GetSetMethod(),
                typeof(Hooks).GetMethod(nameof(WriteSeatVelocity)), typeof(Hooks).GetMethod(nameof(WriteSeatAngularVelocity)));
        public static void WriteSeatVelocity(Rigidbody body, Vector3 value)
        {
            try { if (Runtime.Current?.Momentum.Coasting.PreserveSeatReset(body, value, false) == true) return; }
            catch (Exception error) { Runtime.Current?.Fail(error); }
            body.velocity = value;
        }
        public static void WriteSeatAngularVelocity(Rigidbody body, Vector3 value)
        {
            try { if (Runtime.Current?.Momentum.Coasting.PreserveSeatReset(body, value, true) == true) return; }
            catch (Exception error) { Runtime.Current?.Fail(error); }
            body.angularVelocity = value;
        }
        public static IEnumerable<CodeInstruction> BlockContactBoundsTranspiler(IEnumerable<CodeInstruction> instructions)
            => BlockContactPatch.Rewrite(instructions, Runtime.Current.Map.GridContactBounds,
                typeof(Hooks).GetMethod(nameof(ReadBlockContactBounds)), 2);
        public static IEnumerable<CodeInstruction> BlockContactPointTranspiler(IEnumerable<CodeInstruction> instructions)
            => BlockContactPatch.Rewrite(instructions, Runtime.Current.Map.GridContactPoint,
                typeof(Hooks).GetMethod(nameof(ReadBlockContactPoint)), 1);
        public static Bounds ReadBlockContactBounds(object space, Bounds native, object actor)
        {
            Runtime r = Runtime.Current;
            try { if (r.Frame.TryBlockContactBounds(space, actor, out Bounds result)) return result; }
            catch (Exception error) { r.Fail(error); }
            return (Bounds)r.Map.GridContactBounds.Invoke(space, new object[] { native });
        }
        public static Vector3 ReadBlockContactPoint(object space, Vector3 native, object actor)
        {
            Runtime r = Runtime.Current;
            try { if (r.Frame.TryBlockContactPoint(space, actor, native, out Vector3 result)) return result; }
            catch (Exception error) { r.Fail(error); }
            return (Vector3)r.Map.GridContactPoint.Invoke(space, new object[] { native });
        }
        public static IEnumerable<CodeInstruction> WalkingLookTranspiler(IEnumerable<CodeInstruction> instructions)
            => WalkingLookPatch.Rewrite(instructions,
                typeof(Rigidbody).GetMethod(nameof(Rigidbody.MoveRotation), new[] { typeof(Quaternion) }),
                typeof(Hooks).GetMethod(nameof(WriteWalkingLook)));
        public static void WriteWalkingLook(Rigidbody body, Quaternion rotation)
        {
            try { if (Runtime.Current?.Frame.ApplyWalkingLook(body, rotation) == true) return; }
            catch (Exception error) { Runtime.Current?.Fail(error); }
            body.MoveRotation(rotation);
        }
        public static bool LookInputPrefix(Component __instance, Vector3 __0)
        {
            try
            {
                Runtime r = Runtime.Current;
                if (r?.Frame.Owns(__instance) != true) return true;
                r.Frame.ApplyLook(__0);
                return false;
            }
            catch (Exception error) { Runtime.Current?.Fail(error); return true; }
        }
        public static void ShipUpdatePostfix(object __instance)
        {
            try { Runtime.Current?.Momentum.AfterShipUpdate(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static IEnumerable<CodeInstruction> BodyActivationTranspiler(IEnumerable<CodeInstruction> instructions)
            => BodyActivationPatch.Rewrite(instructions, Runtime.Current.Map.PhysicsRoot,
                typeof(Component).GetProperty(nameof(Component.gameObject)).GetGetMethod(),
                typeof(GameObject).GetProperty(nameof(GameObject.activeSelf)).GetGetMethod(),
                typeof(GameObject).GetMethod(nameof(GameObject.SetActive)),
                typeof(Hooks).GetMethod(nameof(ReadBodyActive)), typeof(Hooks).GetMethod(nameof(SetBodyActive)));
        public static bool ReadBodyActive(GameObject root, object ship)
        {
            try { return Runtime.Current?.Momentum.Coasting.ReadActive(root, ship) ?? root.activeSelf; }
            catch (Exception error) { Runtime.Current?.Fail(error); return root.activeSelf; }
        }
        public static void SetBodyActive(GameObject root, bool active, object ship)
        {
            try
            {
                if (Runtime.Current == null) root.SetActive(active);
                else Runtime.Current.Momentum.Coasting.SetActive(root, active, ship);
            }
            catch (Exception error) { Runtime.Current?.Fail(error); root.SetActive(active); }
        }
        public static IEnumerable<CodeInstruction> SeatExitTranspiler(IEnumerable<CodeInstruction> instructions)
            => SeatExitPatch.Rewrite(instructions, Runtime.Current.Map.ShipMoving, typeof(Hooks).GetMethod(nameof(SeatExitMoving)));
        public static bool SeatExitMoving(bool moving, object ship, object actor)
        {
            if (!moving) return false;
            try { return !(Runtime.Current?.AllowMovingSeatExit(ship, actor) ?? false); }
            catch (Exception error) { Runtime.Current?.Fail(error); return moving; }
        }
        public static bool FixedPrefix(Component __instance)
        {
            try { return Runtime.Current?.RunNativeController(__instance) ?? true; }
            catch (Exception error) { Runtime.Current?.Fail(error); return true; }
        }
        public static bool LimiterPrefix(Component __instance)
        {
            try { return !(Runtime.Current?.Frame.Owns(__instance) ?? false) && !(Runtime.Current?.Frame.OwnsArrival(__instance) ?? false)
                    && !(Runtime.Current?.Frame.OwnsPlacement(__instance) ?? false)
                    && !(Runtime.Current?.Reconnect.Owns(__instance) ?? false); }
            catch (Exception error) { Runtime.Current?.Fail(error); return true; }
        }
        public static void DisablePrefix(Component __instance)
        {
            try { Runtime.Current?.Disable(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static void ColliderPrefix(object __instance, ref bool __0, ref bool __1)
        {
            try { Runtime.Current?.BeforeColliders(__instance, ref __0, ref __1); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
        public static void ColliderPostfix(object __instance)
        {
            try { Runtime.Current?.AfterColliders(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }
    }
}
