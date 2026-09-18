using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ShipWalk
{
    internal static class Hooks
    {
        public static void SeatDetachPrefix(object __instance, object __2, out SeatRelease __state)
        {
            __state = null;
            try { __state = Runtime.Current?.Momentum.Begin(__instance, __2); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }

        public static Exception SeatDetachFinalizer(Exception __exception, SeatRelease __state)
        {
            try { __state?.Owner.End(__state, __exception); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
            return __exception;
        }

        public static void RemotePosePostfix(object __instance, Vector3 __0, Vector3 __1)
        {
            try { Runtime.Current?.Momentum.ObserveRemote(__instance, __0, __1); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
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
                if (Runtime.Current != null) Runtime.Current.Momentum.Coasting.SetActive(root, active, ship);
                else root.SetActive(active);
            }
            catch (Exception error) { Runtime.Current?.Fail(error); root.SetActive(active); }
        }

        public static IEnumerable<CodeInstruction> SeatExitTranspiler(IEnumerable<CodeInstruction> instructions)
            => SeatExitPatch.Rewrite(instructions, Runtime.Current.Map.ShipMoving,
                typeof(Hooks).GetMethod(nameof(SeatExitMoving)));

        public static bool SeatExitMoving(bool moving, object ship, object actor)
        {
            if (!moving) return false;
            try { return !(Runtime.Current?.AllowMovingSeatExit(ship, actor) ?? false); }
            catch (Exception error) { Runtime.Current?.Fail(error); return moving; }
        }

        public static void FixedPrefix(Component __instance, out VelocityScope __state)
        {
            __state = null;
            try { __state = Runtime.Current?.BeginFixed(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }

        public static Exception FixedFinalizer(Exception __exception, VelocityScope __state)
        {
            // Keep the scope's owner even if shutdown clears the global runtime during the original call.
            try { __state?.Owner.EndScope(__state, __exception); }
            catch (Exception error) { __state?.Owner.Fail(error); }
            return __exception;
        }

        public static void LimiterPrefix(Component __instance, out VelocityScope __state)
        {
            __state = null;
            try { __state = Runtime.Current?.BeginLimiter(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }

        public static Exception LimiterFinalizer(Exception __exception, VelocityScope __state)
            => FixedFinalizer(__exception, __state);

        public static void ProbePrefix(Component __instance, out Vector3? __state)
        {
            __state = null;
            try { __state = Runtime.Current?.Probe(__instance); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }

        public static void ProbePostfix(Component __instance, MethodBase __originalMethod, Vector3? __state)
        {
            try { Runtime.Current?.EndProbe(__instance, __originalMethod, __state); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
        }

        public static void ContactPrefix(Component __instance, Collision __0, out Vector3? __state)
        {
            __state = null;
            try { __state = Runtime.Current?.TraceContactBefore(__instance, __0); }
            catch (Exception error) { Runtime.Current?.ExitTrace.Fault(error); }
        }

        public static void ContactPostfix(Component __instance, Collision __0, Vector3? __state)
        {
            try { Runtime.Current?.ObserveContact(__instance, __0); }
            catch (Exception error) { Runtime.Current?.Fail(error); }
            try { Runtime.Current?.TraceContactAfter(__instance, __0, __state); }
            catch (Exception error) { Runtime.Current?.ExitTrace.Fault(error); }
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
