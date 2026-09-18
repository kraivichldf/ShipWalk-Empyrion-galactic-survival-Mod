using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ShipWalk
{
    internal sealed class NativeTravelPlan
    {
        public int Reason;
        public string Destination;
        public Vector3 Position;
        public Quaternion Rotation;
        public object Sector;
    }

    internal sealed class NativeTravelMap
    {
        private readonly NativeBuildBindings module;
        private readonly Build5150 map;
        private readonly MethodInfo space, clock, universe, planetForWorld, orbitName, nearestPlanet,
            planetName, leaveAllowed, enterAllowed, planetToSpace, planetToSpaceRotation,
            spaceToPlanet, spaceToPlanetRotation, snapshot, docked, dockedSnapshots, pilot,
            wrapPacket, sendManager;
        private readonly FieldInfo worldName, planets, planetCentre, planetRadius, snapshotActorIds,
            network, manager, undefinedSector, microShip, microPoint, microRotation;
        private readonly ConstructorInfo worldPacket, managerEnvelope;
        private readonly Type reasonType, effectType, flagsType, managerKind;
        public readonly MethodInfo Boundary, Recovery, WorldRequest, WarpStarted, WarpReceive, Arrival, MicroReceive, NativeBroadcast;

        public NativeTravelMap(Assembly game, Build5150 map)
        {
            this.map = map; module = map.Native;
            Type T(string name) => module.GetType("Assembly-CSharp." + name, true);
            reasonType = T("WindowScope"); effectType = T("DeviceSite"); flagsType = T("PackageService");
            managerKind = T("DatabaseToken+VectorOptions");
            Boundary = M(0x0600539E, "StreamToken", "DeployBuilder", typeof(bool), T("ViewDictionary"));
            Recovery = M(0x06001BDC, "MenuOptions", "SearchDatabase", game.GetType("EnumOutOfPlayfieldTypes", true));
            WorldRequest = M(0x06006B99, "FunctionAttributeNodeCollection", "ExitForm", typeof(void), reasonType,
                game.GetType("Vector3i", true), typeof(string), typeof(Vector3), typeof(Quaternion), T("AspectContext"),
                typeof(List<>).MakeGenericType(T("AspectContext")), effectType, typeof(string), flagsType, typeof(int));
            WarpStarted = M(0x06006B9E, "FunctionAttributeNodeCollection", "ExitQueue", typeof(void), reasonType,
                T("StreamToken"), typeof(Vector3), game.GetType("Vector3i", true), typeof(string), typeof(Vector3),
                typeof(Quaternion), typeof(int), typeof(int[]));
            Arrival = M(0x060022C1, "ViewDictionary", "RebuildPlugin", typeof(void), reasonType, typeof(string),
                typeof(string), typeof(Vector3), typeof(Quaternion), effectType, typeof(string), flagsType);
            MicroReceive = M(0x06003E1D, "EditorInvoker", "DeployAssembly", typeof(void));
            WarpReceive = M(0x06003F1E, "AssemblyLoader", "DeployAssembly", typeof(void));
            NativeBroadcast = M(0x06005AFA, "ControlToken", "ReduceStub", typeof(void), T("StreamToken"), T("FormStack"), T("StubSet"));
            space = M(0x06005332, "StreamToken", "get_GenerateBitmap", typeof(bool));
            clock = M(0x06005330, "StreamToken", "get_RegisterTreeNode", typeof(ulong));
            universe = M(0x06005E7A, "FormTree", "get_UpdateClient", T("FormTree"));
            planetForWorld = M(0x06005E8A, "FormTree", "DetachBookmark", T("MenuItemSettingsNodeCollection"), typeof(string), typeof(bool), typeof(bool));
            orbitName = M(0x06005E8B, "FormTree", "ExtractConfig", typeof(string), typeof(string));
            nearestPlanet = M(0x06005E76, "ImageTable", "JoinDeployment", T("MenuItemSettingsNodeCollection"), typeof(Vector3));
            planetName = M(0x06006112, "MenuItemSettingsNodeCollection", "get_PlayfieldName", typeof(string));
            leaveAllowed = M(0x06006DBD, "ReferenceEditorNodeCollection", "SortResource", typeof(bool), T("MenuOptions"), T("MenuItemSettingsNodeCollection"));
            enterAllowed = M(0x06006DBE, "ReferenceEditorNodeCollection", "BatchBuildDrive", typeof(bool), T("MenuOptions"), T("MenuItemSettingsNodeCollection"));
            Type[] coordinates = { T("StreamToken"), typeof(Vector3), typeof(float) };
            planetToSpace = M(0x06006135, "MenuItemSettingsNodeCollection", "OrderFile", typeof(Vector3), coordinates);
            planetToSpaceRotation = M(0x06006136, "MenuItemSettingsNodeCollection", "ReplacePartition", typeof(Quaternion), coordinates);
            spaceToPlanet = M(0x06006133, "MenuItemSettingsNodeCollection", "JoinMethod", typeof(Vector3), coordinates);
            spaceToPlanetRotation = M(0x06006134, "MenuItemSettingsNodeCollection", "ConnectBookmark", typeof(Quaternion), coordinates);
            snapshot = M(0x06001C4E, "MenuOptions", "ReduceIcon", T("AspectContext"), typeof(Vector3), typeof(Quaternion));
            docked = M(0x06001C4D, "MenuOptions", "Quote", T("DriveAttribute"));
            dockedSnapshots = M(0x06001B55, "DriveAttribute", "CleanActivator", typeof(List<>).MakeGenericType(T("AspectContext")), typeof(Vector3), typeof(Quaternion));
            pilot = M(0x06001C8A, "MenuOptions", "RebuildStream", typeof(int));
            worldName = F(0x04004F2E, T("StreamToken"), typeof(string));
            planets = F(0x04004F2F, T("StreamToken"), T("ImageTable"));
            planetCentre = F(0x04005D2F, T("MenuItemSettingsNodeCollection"), typeof(Vector3));
            planetRadius = F(0x04005D2D, T("MenuItemSettingsNodeCollection"), typeof(float));
            snapshotActorIds = F(0x040055B0, T("AspectContext"), typeof(int[]));
            network = F(0x040054EE, T("ControlToken"), T("ControlToken"), true);
            manager = F(0x040054FE, T("ControlToken"), T("DockingPaneConverterNodeCollection"));
            undefinedSector = F(0x040071DC, game.GetType("Vector3i", true), game.GetType("Vector3i", true), true);
            microShip = F(0x040038A6, T("EditorInvoker"), typeof(int));
            microPoint = F(0x040038A7, T("EditorInvoker"), typeof(Vector3));
            microRotation = F(0x040038A8, T("EditorInvoker"), typeof(Quaternion));
            worldPacket = C(0x06003C08, T("TextFileResolver"), reasonType, game.GetType("Vector3i", true), typeof(string),
                typeof(Vector3), typeof(Quaternion), typeof(int), T("AspectContext"), typeof(List<>).MakeGenericType(T("AspectContext")),
                effectType, typeof(string), flagsType);
            managerEnvelope = C(0x06003859, T("DatabaseToken+EmulatorLayout"), managerKind, typeof(int));
            wrapPacket = M(0x06003875, "DatabaseToken+EmulatorLayout", "FormatBitmap", T("DatabaseToken+EmulatorLayout"), typeof(string), T("FormStack"));
            sendManager = M(0x06007F57, "DockingPaneConverterNodeCollection", "CopyWindow", typeof(void), T("DatabaseToken+EmulatorLayout"));
        }
        private MethodInfo M(int token, string owner, string name, Type result, params Type[] args)
        {
            var value = (MethodInfo)module.ResolveMethod(token);
            if (module.CanonicalTypeName(value.DeclaringType.FullName) != "Assembly-CSharp." + owner || module.CanonicalMethodName(value) != name || value.ReturnType != result
                || !value.GetParameters().Select(p => p.ParameterType).SequenceEqual(args))
                throw new NotSupportedException("Travel method mismatch: " + token.ToString("X8"));
            return value;
        }
        private FieldInfo F(int token, Type owner, Type type, bool isStatic = false)
        {
            FieldInfo value = module.ResolveField(token);
            if (value.DeclaringType != owner || value.FieldType != type || value.IsStatic != isStatic) throw new NotSupportedException("Travel field mismatch: " + token.ToString("X8"));
            return value;
        }
        private ConstructorInfo C(int token, Type owner, params Type[] args)
        {
            var value = (ConstructorInfo)module.ResolveMethod(token);
            if (value.DeclaringType != owner || !value.GetParameters().Select(p => p.ParameterType).SequenceEqual(args))
                throw new NotSupportedException("Travel constructor mismatch.");
            return value;
        }
        public string ContextName(object context) => context == null ? null : (string)worldName.GetValue(context);
        public int Pilot(object ship) => (int)pilot.Invoke(ship, null);
        public bool BoundaryPlan(object context, object ship, out NativeTravelPlan plan)
        {
            plan = null;
            Vector3 point = (Vector3)map.EntityPosition.GetValue(ship);
            var hull = map.EntityTransform.GetValue(ship) as Transform;
            if (hull == null || !MotionMath.Finite(N(point))) return false;
            float phase = ((ulong)clock.Invoke(context, null) % 24000) / 24000f;
            Vector3 destination; Quaternion rotation; string name;
            if (!(bool)space.Invoke(context, null))
            {
                if (point.y < 1100f) return false;
                object tree = universe.Invoke(null, null);
                object planet = planetForWorld.Invoke(tree, new object[] { ContextName(context), false, false });
                if (planet == null || !(bool)leaveAllowed.Invoke(null, new[] { ship, planet })) return false;
                Vector3 centre = (Vector3)planetCentre.GetValue(planet);
                Vector3 mapped = (Vector3)planetToSpace.Invoke(planet, new object[] { context, point, phase });
                destination = centre + (mapped - centre).normalized * ((float)planetRadius.GetValue(planet) + 230f);
                rotation = (Quaternion)planetToSpaceRotation.Invoke(planet, new object[] { context, point, phase });
                name = (string)orbitName.Invoke(tree, new object[] { ContextName(context) });
            }
            else
            {
                object planet = nearestPlanet.Invoke(planets.GetValue(context), new object[] { point });
                if (planet == null || Vector3.Distance(point, (Vector3)planetCentre.GetValue(planet)) >= (float)planetRadius.GetValue(planet) + 200f
                    || !(bool)enterAllowed.Invoke(null, new[] { ship, planet })) return false;
                destination = (Vector3)spaceToPlanet.Invoke(planet, new object[] { context, point, phase }); destination.y = 1070f;
                rotation = (Quaternion)spaceToPlanetRotation.Invoke(planet, new object[] { context, point, phase });
                name = (string)planetName.Invoke(planet, null);
            }
            if (string.IsNullOrEmpty(name) || name == ContextName(context)) return false;
            plan = new NativeTravelPlan { Reason = 6, Destination = name, Position = destination,
                Rotation = rotation * hull.rotation, Sector = undefinedSector.GetValue(null) };
            return true;
        }
        public object BuildWorldPacket(object ship, NativeTravelPlan plan, int leader, IEnumerable<int> walkingPassengers)
        {
            object root = snapshot.Invoke(ship, new object[] { plan.Position, plan.Rotation });
            int[] nativePassengers = snapshotActorIds.GetValue(root) as int[] ?? Array.Empty<int>();
            snapshotActorIds.SetValue(root, TravelTransfer.PassengerIds(nativePassengers, walkingPassengers, leader));
            object attached = docked.Invoke(ship, null);
            object children = attached == null ? null : dockedSnapshots.Invoke(attached, new object[] { plan.Position, plan.Rotation });
            return worldPacket.Invoke(new[] { Enum.ToObject(reasonType, plan.Reason), plan.Sector ?? undefinedSector.GetValue(null),
                plan.Destination, (object)plan.Position, plan.Rotation, leader, root, children,
                Enum.ToObject(effectType, 0), null, Enum.ToObject(flagsType, 31) });
        }
        public bool SendWorldPacket(object packet)
        {
            object singleton = network.GetValue(null);
            object bridge = singleton == null ? null : manager.GetValue(singleton);
            if (bridge == null) return false;
            object envelope = managerEnvelope.Invoke(new[] { Enum.ToObject(managerKind, 10), (object)0 });
            envelope = wrapPacket.Invoke(envelope, new[] { null, packet });
            sendManager.Invoke(bridge, new[] { envelope }); return true;
        }
        public bool TryMicro(object packet, out int ship, out Vector3 point, out Quaternion rotation)
        {
            ship = -1; point = default; rotation = Quaternion.identity;
            if (packet == null || !microShip.DeclaringType.IsInstanceOfType(packet)) return false;
            ship = (int)microShip.GetValue(packet); point = (Vector3)microPoint.GetValue(packet); rotation = (Quaternion)microRotation.GetValue(packet);
            return ship > 0 && new LocalFramePose(N(point), N(rotation)).Valid;
        }
        private static System.Numerics.Vector3 N(Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);
        private static System.Numerics.Quaternion N(Quaternion q) => new System.Numerics.Quaternion(q.x, q.y, q.z, q.w);
    }
}
