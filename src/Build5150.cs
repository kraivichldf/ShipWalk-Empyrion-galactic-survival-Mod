using System;
using System.Linq;
using System.Reflection;
using Eleon.Modding;
using UnityEngine;

namespace ShipWalk
{
    internal sealed class Build5150 : IEntityNodeAccess<Transform>
    {
        public const string Hash = "F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F";
        public const string Mvid = "6f096e23-09d8-4159-b44a-baeca78318ee";
        internal readonly NativeBuildBindings Native;
        public readonly Type ControllerType, ShipType, MbsType, XrefType, PlayerType, RoomLightType, LightControlType;
        private readonly ConstructorInfo entityBridge, playerBridge;
        private readonly PropertyInfo nativeEntity;
        private readonly Type cvCockpitType, smallCockpitType, passengerSeatType;
        public readonly MethodInfo FixedUpdate, Limiter, Damping, GroundDamping, ContactEnter, ContactStay,
            Disable, ColliderSelection, SeatExit, ShipMoving, XrefTransform, SeatDetach, RemotePose, ShipUpdate,
            CharacterPresentation, ShipPresentation, LocomotionUpdate, SetEntityPosition, RoomEnter, ShipControl, LookInput,
            PlanarLook, ClientSeatDetach, ServerSeatDetach, SetEntityRotation, SetEntityQuaternion, ShipFixedUpdate, ShipForces, ShipCollision, CharacterUpdate,
            BlockContactScan, BlockContactVisit, GridContactBounds, GridContactPoint;
        public readonly FieldInfo ControllerBody, ControllerCollider, ControllerEntity, Grounded, Jetpack, Ladder, Submerged,
            EntityId, EntityBody, EntityRemote, EntityType, EntityTransform, Origin, MbsEntity, SeatedShip,
            ActorPiloting, DockedTo, PhysicsRoot, MovementInput, JumpRequested, JumpHeld,
            LightSwitchedOn, LightPowered, CameraRig, ActorClimbing, ActorSwimming, ClimbSpeed, Sprinting, LookAccumulator,
            EntityPosition, EntityVelocity, ShipControllerEntity, ShipControllerBody, ShipWorldVelocityCache,
            ShipLocalVelocityCache, ShipAutoBrake, ShipPowered, ShipSpace, GridPosition, GridRotation, GridDivisor;
        private readonly FieldInfo seatLayout, pilotPresent;
        private readonly FieldInfo seatPosition, seatX, seatY, seatZ, blockDefinitions, blockOxygenTight;
        public readonly MethodInfo JetpackEnabled;

        public Build5150(Assembly game, bool playfieldServer = false)
        {
            Native = new NativeBuildBindings(game, playfieldServer);
            var assembly = Native;
            var module = Native;
            ControllerType = assembly.GetType("RbCtrlCharacter", true);
            ShipType = assembly.GetType("Assembly-CSharp.ViewContext", true);
            MbsType = assembly.GetType("MBS_E", true);
            XrefType = assembly.GetType("XRefBase", true);
            PlayerType = assembly.GetType("Assembly-CSharp.ConnectionToken", true);
            RoomLightType = assembly.GetType("RoomLightController", true);
            LightControlType = assembly.GetType("LightControl", true);
            Type entityType = assembly.GetType("Assembly-CSharp.MenuOptions", true);
            entityBridge = Bridge(assembly, "Eleon.ModBridge.EntityBridge", entityType, typeof(IEntity));
            playerBridge = Bridge(assembly, "Eleon.ModBridge.PlayerBridge", entityType, typeof(IPlayer));
            nativeEntity = entityBridge.DeclaringType.GetProperty("Entity", BindingFlags.Instance | BindingFlags.Public);
            if (nativeEntity == null || nativeEntity.PropertyType != entityType
                || nativeEntity.GetGetMethod() == null || nativeEntity.GetIndexParameters().Length != 0)
                throw new NotSupportedException("API native-entity bridge getter mismatch.");
            cvCockpitType = assembly.GetType("Assembly-CSharp.StubManager", true);
            smallCockpitType = assembly.GetType("Assembly-CSharp.FileManager", true);
            passengerSeatType = assembly.GetType("Assembly-CSharp.SelectionSettings", true);
            FixedUpdate = Method(module, 0x06004488, "RbCtrlCharacter", "FixedUpdate");
            Limiter = Method(module, 0x0600449C, "RbCtrlCharacter", "FindDatabase", typeof(float));
            Damping = Method(module, 0x06004497, "RbCtrlCharacter", "JoinCondition");
            GroundDamping = Method(module, 0x06004498, "RbCtrlCharacter", "CopyWindow");
            ContactEnter = Method(module, 0x060044A1, "RbCtrlCharacter", "ListDrive", typeof(Collision));
            ContactStay = Method(module, 0x060044A3, "RbCtrlCharacter", "OpenFunction", typeof(Collision));
            Disable = Method(module, 0x0600448A, "RbCtrlCharacter", "OnDisable");
            ColliderSelection = Method(module, 0x06002367, "Assembly-CSharp.ViewContext", "ShowPciture", typeof(bool), typeof(bool));
            SeatExit = Method(module, 0x06001C4A, "Assembly-CSharp.MenuOptions", "CleanTemplate", assembly.GetType("Assembly-CSharp.ToolbarConverter", true), typeof(bool));
            SeatDetach = Method(module, 0x06001C2F, "Assembly-CSharp.MenuOptions", "BuildSelection", assembly.GetType("Vector3i", true), typeof(byte), assembly.GetType("Assembly-CSharp.MenuOptions", true), typeof(bool));
            ClientSeatDetach = Method(module, 0x0600221D, "Assembly-CSharp.ViewDictionary", "BuildSelection", assembly.GetType("Vector3i", true), typeof(byte), assembly.GetType("Assembly-CSharp.MenuOptions", true), typeof(bool));
            ServerSeatDetach = Method(module, 0x06002153, "Assembly-CSharp.ConnectionToken", "BuildSelection", assembly.GetType("Vector3i", true), typeof(byte), assembly.GetType("Assembly-CSharp.MenuOptions", true), typeof(bool));
            RemotePose = Method(module, 0x06001C14, "Assembly-CSharp.MenuOptions", "SearchTemplate", typeof(Vector3), typeof(Vector3));
            ShipUpdate = Method(module, 0x0600233C, "Assembly-CSharp.ViewContext", "ReplacePartition", typeof(float));
            CharacterPresentation = Method(module, 0x0600222A, "Assembly-CSharp.ViewDictionary", "DetachToolbox");
            ShipPresentation = Method(module, 0x0600233F, "Assembly-CSharp.ViewContext", "DetachToolbox");
            LocomotionUpdate = Method(module, 0x06001CCD, "Assembly-CSharp.ToolbarConverter", "ReplacePartition", typeof(float));
            SetEntityPosition = Method(module, 0x06001BEA, "Assembly-CSharp.MenuOptions", "SelectDirectory", typeof(Vector3), typeof(bool));
            SetEntityRotation = Method(module, 0x06001BEE, "Assembly-CSharp.MenuOptions", "ConnectIcon", typeof(Vector3), typeof(bool));
            SetEntityQuaternion = Method(module, 0x06001BF2, "Assembly-CSharp.MenuOptions", "ConnectIcon", typeof(Quaternion), typeof(bool), typeof(bool));
            ShipFixedUpdate = Method(module, 0x060045E1, "RbCtrlPlayerShip", "FixedUpdate");
            ShipForces = Method(module, 0x06004611, "RbCtrlPlayerShip", "SortResource");
            ShipCollision = Method(module, 0x06004629, "RbCtrlPlayerShip", "OnCollisionEnter", typeof(Collision));
            RoomEnter = Method(module, 0x0600306E, "RoomLightController", "OnTriggerEnter", typeof(Collider));
            ShipControl = Method(module, 0x060045E4, "RbCtrlPlayerShip", "ShowComponent", typeof(bool));
            LookInput = Method(module, 0x06004483, "RbCtrlCharacter", "OpenLine", typeof(Vector3));
            CharacterUpdate = Method(module, 0x06004487, "RbCtrlCharacter", "Update");
            BlockContactScan = Method(module, 0x060053BC, "Assembly-CSharp.StreamToken", "ConnectToolbar", assembly.GetType("Assembly-CSharp.MenuOptions", true), typeof(bool));
            BlockContactVisit = Method(module, 0x06004EC1, "Assembly-CSharp.OptionsScope", "DisconnectPartition", assembly.GetType("Assembly-CSharp.MenuOptions", true), typeof(Bounds));
            GridContactBounds = (MethodInfo)module.ResolveMethod(0x06004EAC);
            GridContactPoint = (MethodInfo)module.ResolveMethod(0x06004EA7);
            ValidateConversion(module, GridContactBounds, "BatchBuildNode", typeof(Bounds));
            ValidateConversion(module, GridContactPoint, "DetachEmulator", typeof(Vector3));
            PlanarLook = (MethodInfo)module.ResolveMethod(0x06001C3F);
            if (module.CanonicalTypeName(PlanarLook.DeclaringType.FullName) != "Assembly-CSharp.MenuOptions" || module.CanonicalMethodName(PlanarLook) != "BuildBookmark"
                || PlanarLook.IsStatic || PlanarLook.ReturnType != typeof(bool) || PlanarLook.GetParameters().Length != 0)
                throw new NotSupportedException("Native look-mode accessor mismatch.");
            ShipMoving = (MethodInfo)module.ResolveMethod(0x06001C04);
            if (module.CanonicalTypeName(ShipMoving.DeclaringType.FullName) != "Assembly-CSharp.MenuOptions" || module.CanonicalMethodName(ShipMoving) != "LoadContext"
                || ShipMoving.IsStatic || ShipMoving.ReturnType != typeof(bool) || ShipMoving.GetParameters().Length != 0)
                throw new NotSupportedException("Seat-exit moving-state accessor mismatch.");
            XrefTransform = (MethodInfo)module.ResolveMethod(0x06007470);
            if (XrefTransform.DeclaringType != XrefType || module.CanonicalMethodName(XrefTransform) != "get_ToggleClient"
                || XrefTransform.IsStatic || XrefTransform.ReturnType != typeof(Transform)
                || XrefTransform.GetParameters().Length != 0)
                throw new NotSupportedException("XRef transform mapping mismatch.");
            ControllerBody = Field(module, 0x04003E05, "RbCtrlCharacter", typeof(Rigidbody).FullName);
            ControllerCollider = Field(module, 0x04003E06, "RbCtrlCharacter", typeof(Collider).FullName);
            ControllerEntity = Field(module, 0x04003E07, "RbCtrlCharacter", "Assembly-CSharp.ViewDictionary");
            Grounded = Field(module, 0x04003E08, "RbCtrlCharacter", "System.Boolean");
            MovementInput = Field(module, 0x04001C2E, "Assembly-CSharp.ToolbarConverter", typeof(Vector3).FullName);
            JumpRequested = Field(module, 0x04003E0E, "RbCtrlCharacter", "System.Boolean");
            JumpHeld = Field(module, 0x04003E0F, "RbCtrlCharacter", "System.Boolean");
            LightSwitchedOn = Field(module, 0x040066FE, "LightControl", "System.Boolean");
            LightPowered = Field(module, 0x0400670A, "LightControl", "System.Boolean");
            CameraRig = Field(module, 0x04001F71, "Assembly-CSharp.ViewDictionary", typeof(Transform).FullName);
            ActorClimbing = Field(module, 0x04001B4C, "Assembly-CSharp.MenuOptions", "System.Boolean");
            ActorSwimming = Field(module, 0x04001C0B, "Assembly-CSharp.ToolbarConverter", "System.Boolean");
            ClimbSpeed = Field(module, 0x04003DDF, "RbCtrlCharacter", "System.Single");
            Sprinting = Field(module, 0x04003E0C, "RbCtrlCharacter", "System.Boolean");
            LookAccumulator = Field(module, 0x04003DE9, "RbCtrlCharacter", typeof(Vector3).FullName);
            Jetpack = Field(module, 0x04003DDE, "RbCtrlCharacter", "System.Boolean");
            Ladder = Field(module, 0x04003E0B, "RbCtrlCharacter", "System.Boolean");
            Submerged = Field(module, 0x04003E11, "RbCtrlCharacter", "System.Boolean");
            EntityId = Field(module, 0x04001B24, "Assembly-CSharp.MenuOptions", "System.Int32");
            EntityPosition = Field(module, 0x04001B34, "Assembly-CSharp.MenuOptions", typeof(Vector3).FullName);
            EntityVelocity = Field(module, 0x04001B43, "Assembly-CSharp.MenuOptions", typeof(Vector3).FullName);
            ShipControllerEntity = Field(module, 0x040041C0, "RbCtrlShip", "Assembly-CSharp.MenuOptions");
            ShipControllerBody = Field(module, 0x040041BF, "RbCtrlShip", typeof(Rigidbody).FullName);
            ShipWorldVelocityCache = Field(module, 0x04004134, "RbCtrlPlayerShip", typeof(Vector3).FullName);
            ShipLocalVelocityCache = Field(module, 0x04004188, "RbCtrlPlayerShip", typeof(Vector3).FullName);
            ShipAutoBrake = Field(module, 0x040040E1, "RbCtrlPlayerShip", "System.Boolean");
            ShipPowered = Field(module, 0x040040B8, "RbCtrlPlayerShip", "System.Boolean");
            EntityBody = Field(module, 0x04001B1C, "Assembly-CSharp.MenuOptions", typeof(Rigidbody).FullName);
            PhysicsRoot = Field(module, 0x04001B17, "Assembly-CSharp.MenuOptions", typeof(Transform).FullName);
            ShipSpace = Field(module, 0x04001CDE, "Assembly-CSharp.EmulatorFactory", "Assembly-CSharp.OptionsScope");
            GridPosition = Field(module, 0x04004A64, "Assembly-CSharp.OptionsScope", typeof(Vector3).FullName);
            GridRotation = Field(module, 0x04004A65, "Assembly-CSharp.OptionsScope", typeof(Quaternion).FullName);
            GridDivisor = Field(module, 0x04004A5C, "Assembly-CSharp.OptionsScope", "System.Int32");
            EntityRemote = Field(module, 0x04001B55, "Assembly-CSharp.MenuOptions", "System.Boolean");
            EntityType = Field(module, 0x04001B61, "Assembly-CSharp.MenuOptions", "EntityType");
            EntityTransform = Field(module, 0x04001B94, "Assembly-CSharp.MenuOptions", typeof(Transform).FullName);
            Origin = Field(module, 0x04000E83, "FloatingOriginSimple", typeof(Vector3).FullName);
            MbsEntity = Field(module, 0x0400674E, "MBS_E", "Assembly-CSharp.MenuOptions");
            SeatedShip = Field(module, 0x04001B7E, "Assembly-CSharp.MenuOptions", "Assembly-CSharp.MenuOptions");
            ActorPiloting = Field(module, 0x04001B7F, "Assembly-CSharp.MenuOptions", "System.Boolean");
            DockedTo = Field(module, 0x04001B88, "Assembly-CSharp.MenuOptions", "Assembly-CSharp.EmulatorFactory");
            seatLayout = Field(module, 0x04001B85, "Assembly-CSharp.MenuOptions", "Assembly-CSharp.IconLayout");
            pilotPresent = Field(module, 0x040019F1, "Assembly-CSharp.IconLayout", "System.Boolean");
            seatPosition = Field(module, 0x04001B80, "Assembly-CSharp.MenuOptions", "Vector3i");
            seatX = Field(module, 0x040071ED, "Vector3i", "System.Int32");
            seatY = Field(module, 0x040071EE, "Vector3i", "System.Int32");
            seatZ = Field(module, 0x040071EF, "Vector3i", "System.Int32");
            blockDefinitions = Field(module, 0x0400147F, "Assembly-CSharp.ResourceList", "Assembly-CSharp.ResourceList[]");
            blockOxygenTight = Field(module, 0x040014B9, "Assembly-CSharp.ResourceList", "System.Nullable`1<System.Boolean>");
            if (!blockDefinitions.IsStatic || blockOxygenTight.IsStatic || seatPosition.IsStatic
                || seatX.IsStatic || seatY.IsStatic || seatZ.IsStatic)
                throw new NotSupportedException("Occupied-seat metadata static/instance mismatch.");
            JetpackEnabled = (MethodInfo)module.ResolveMethod(0x06001CDB);
            if (!JetpackEnabled.DeclaringType.IsAssignableFrom(ControllerEntity.FieldType)
                || module.CanonicalMethodName(JetpackEnabled) != "get_BuildProcess"
                || JetpackEnabled.IsStatic || JetpackEnabled.GetParameters().Length != 0 || JetpackEnabled.ReturnType != typeof(bool))
                throw new NotSupportedException("Jetpack state accessor missing.");
        }

        private static void ValidateConversion(NativeBuildBindings module, MethodInfo method, string name, Type type)
        {
            if (method.IsStatic || module.CanonicalTypeName(method.DeclaringType.FullName) != "Assembly-CSharp.OptionsScope" || module.CanonicalMethodName(method) != name
                || method.ReturnType != type || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { type }))
                throw new NotSupportedException("Native block-contact conversion signature mismatch.");
        }

        private static ConstructorInfo Bridge(NativeBuildBindings assembly, string name, Type entity, Type contract)
        {
            Type bridge = assembly.GetType(name, true);
            ConstructorInfo constructor = bridge.GetConstructor(new[] { entity });
            if (constructor == null || !contract.IsAssignableFrom(bridge))
                throw new NotSupportedException("Mod API bridge mapping mismatch: " + name);
            return constructor;
        }

        public IEntity Entity(object entity) => (IEntity)entityBridge.Invoke(new[] { entity });
        public object NativeEntity(IEntity entity) => entity != null && entityBridge.DeclaringType.IsInstanceOfType(entity)
            ? nativeEntity.GetValue(entity, null) : null;
        public IPlayer Player(object actor) => (IPlayer)playerBridge.Invoke(new[] { actor });
        public bool HasPilot(object ship)
        {
            object layout = seatLayout.GetValue(ship);
            return layout != null && Bool(pilotPresent, layout);
        }

        private static MethodInfo Method(NativeBuildBindings module, int token, string owner, string name, params Type[] parameters)
        {
            var method = module.ResolveMethod(token) as MethodInfo;
            if (method == null || module.CanonicalTypeName(method.DeclaringType.FullName) != owner || module.CanonicalMethodName(method) != name
                || method.IsStatic || method.ReturnType != typeof(void)
                || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
                throw new NotSupportedException("Method signature mismatch: " + token.ToString("X8"));
            return method;
        }

        private static FieldInfo Field(NativeBuildBindings module, int token, string owner, string type)
        {
            var field = module.ResolveField(token);
            string actualType = field.FieldType == typeof(bool?) ? "System.Nullable`1<System.Boolean>" : field.FieldType.FullName;
            if (module.CanonicalTypeName(field.DeclaringType.FullName) != owner || module.CanonicalTypeName(actualType) != type)
                throw new NotSupportedException("Field signature mismatch: " + token.ToString("X8"));
            return field;
        }

        public bool Bool(FieldInfo field, object instance) => (bool)field.GetValue(instance);
        public int Id(object entity) => (int)EntityId.GetValue(entity);
        public Vector3 OriginOffset => (Vector3)Origin.GetValue(null);

        public bool IsOpenSeat(object actor, IStructure structure, out int blockType)
        {
            blockType = -1;
            if (actor == null || structure == null || !structure.IsReady) return false;
            object position = seatPosition.GetValue(actor);
            IBlock block = structure.GetBlock((int)seatX.GetValue(position), (int)seatY.GetValue(position), (int)seatZ.GetValue(position));
            if (block == null) return false;
            block = block.ParentBlock;
            if (block == null) return false;
            block.Get(out blockType, out _, out _, out _);
            var definitions = (Array)blockDefinitions.GetValue(null);
            if (definitions == null || blockType <= 0 || blockType >= definitions.Length) return false;
            object definition = definitions.GetValue(blockType);
            if (definition == null) return false;
            string seatClass = cvCockpitType.IsInstanceOfType(definition) ? "CockpitMS"
                : smallCockpitType.IsInstanceOfType(definition) ? "CockpitSS"
                : passengerSeatType.IsInstanceOfType(definition) ? "PassengerSeat" : null;
            return SeatExitPolicy.IsOpenSeat(seatClass, (bool?)blockOxygenTight.GetValue(definition));
        }

        public object ResolveEntity(Collider collider)
        {
            return collider != null ? EntityLookup.Resolve(collider.transform, this) : null;
        }

        bool IEntityNodeAccess<Transform>.IsAlive(Transform node) => node != null;
        Transform IEntityNodeAccess<Transform>.Parent(Transform node) => node.parent;

        object IEntityNodeAccess<Transform>.DirectEntity(Transform node)
        {
            if (node == null) return null;
            Component mbs = node.GetComponent(MbsType);
            return mbs != null ? MbsEntity.GetValue(mbs) : null;
        }

        Transform IEntityNodeAccess<Transform>.ReferenceTarget(Transform node)
        {
            Component xref = node.GetComponent(XrefType);
            // Do not invoke get_DeployAssembly: it dereferences GetComponent<MBS_E>()
            // without a null check, including on ordinary XRefP scene roots.
            return xref != null ? (Transform)XrefTransform.Invoke(xref, null) : null;
        }
    }
}
