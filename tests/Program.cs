using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ShipWalk;

internal static class Program
{
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Pass the installed game root."); return 2; }
        string managed = Path.Combine(Path.GetFullPath(args[0]), "Client", "Empyrion_Data", "Managed");
        AppDomain.CurrentDomain.AssemblyResolve += (_, ev) =>
        {
            string path = Path.Combine(managed, new AssemblyName(ev.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        return RunTests();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunTests()
    {
        try
        {
            Test("boarding clearance queries each proposed capsule pose without a world physics step", BoardingPlacementTests.ProposedPoseClearance);
            Test("capsule clearance bounds correction, handles conflicting surfaces and checks the final shift", BoardingPlacementTests.ClearanceBounds);
            Test("recorded docked seat exit prefers completed native placement and rejects distant world poses", BoardingPlacementTests.RecordedSeatAndNativeExit);
            Test("blocked placement retains its transaction and retries after geometry settles", BoardingPlacementTests.FailedRoundThenRecovery);
            Test("duplicate seat acknowledgements and moving candidates cannot renew the exit hold", BoardingPlacementTests.DuplicateExpiryAndCancellation);
            Test("multiple native controllers cannot multiply the exit-placement search budget", BoardingPlacementTests.OneSlicePerPhysicsStep);
            Test("pending seat exit follows a moving carrier through a docking root change", BoardingPlacementTests.MovingCarrierAndRootChange);
            Test("same-context boarding retry requires movement and has a fixed rebuild budget", BoardingPlacementTests.RetryRequiresMovementAndIsBounded);
            Test("placement recovery expires and respects off, new vessels and session changes", BoardingPlacementTests.RetryContextIsolation);
            Test("pending passenger stays in the server roster and CV floor handover reaches the observer", BoardingPlacementTests.PendingMembershipAndFloorHandover);
            Test("compiled controller and cleanup paths retain placement ownership boundaries", BoardingPlacementTests.ControllerAndCleanupIntegration);
            Test("public release contains no passenger reconnect controller or persistent store", QuietReleaseTests.NoReconnectRecovery);
            Test("release output emits only explicit replies and retains errors without log IO", QuietReleaseTests.CommandOnlyOutput);
            Test("legacy Trace=true cannot reenable automatic logging", QuietReleaseTests.LegacyTraceConfig);
            Test("release logger construction creates no log directory or CSV", QuietReleaseTests.NoTraceFiles);
            Test("recorded CV collision omission includes docked SV and removes it on undock", DockingTests.RecordedCollisionOmission);
            Test("docking roots retain airborne/seat association and reject unrelated or cyclic graphs", DockingTests.RootAndAssociation);
            Test("undock and redock preserve world position facing and linear/angular momentum", DockingTests.ContinuousRebase);
            Test("reference switch preserves interpolated camera and relative walking animation", DockingTests.PresentationRebase);
            Test("docked SV block contacts use child orientation and half-metre block scale", DockingTests.DockedBlockCoordinates);
            Test("movement and travel codecs preserve occupied vessel and reject old formats", DockingTests.WireMembershipAndTravel);
            Test("authenticated relay changes frame generation on undock and rejects stale or foreign membership", DockingTests.RelayUndocking);
            Test("airborne handover retains 100 m/s momentum without disabling transport or weakening normal movement checks", DockingTests.AirborneHandoverBudget);
            Test("falling player hands collision to the world before terrain inside the ship envelope", DepartureAndObserverTests.TerrainBeforeBounds);
            Test("world collision sweep includes normal ship motion but excludes confirmed relocation and origin shifts", DepartureAndObserverTests.WorldSweepAndRelocation);
            Test("remote walking animation follows local interpolated travel and ignores ship speed and turning", DepartureAndObserverTests.RemoteRelativeAnimation);
            Test("remote animation resets on generation, mode, stale data and actual departure", DepartureAndObserverTests.RemoteAnimationLifecycle);
            Test("pose recovery accepts piloted vessels without weakening native owner handoff", RelocationTests.AuthorityPurposes);
            Test("recorded gas-planet correction retains standing local pose through authenticated relay and two confirmations", RelocationTests.RecordedGasPlanetRecovery);
            Test("pose confirmation purpose and frame protocol version cannot be substituted", RelocationTests.PurposeAndVersionGuards);
            Test("standalone worker startup selects its own bindings while client and co-op retain theirs", NativeBuildTests.BothBuilds);
            Test("native profiles reject mismatched hashes, module IDs and process roles", NativeBuildTests.ExactIdentityAndRole);
            Test("standalone token and type collisions cannot resolve to unrelated native members", NativeBuildTests.NativeLayoutRegression);
            Test("travel codec preserves local membership and rejects truncated, malformed and unknown records", TravelTests.Codec);
            Test("travel manifests reject duplicate actors, cross-vessel data and invalid local poses", TravelTests.InvalidMembers);
            Test("vessel transfer waits for every passenger and persists before a single commit", TravelTests.CommitLifecycle);
            Test("travel identity, destination, geometry, seating and cancelled-transfer guards", TravelTests.SpoofAndCancel);
            Test("retries cannot change acknowledged poses or accept a different stored manifest", TravelTests.ImmutableRetry);
            Test("travel roster retains seated membership and rejects stale sessions and cross-world peers", TravelTests.RosterSessions);
            Test("arrival and explicit MicroWarp retain local jump state without displacement velocity", TravelTests.LocalArrivalAndMicroWarp);
            Test("extracted native IL reproduces player crossing separately from the unseated ship", TravelTests.NativeBoundaryReproduction);
            Test("native vessel snapshot codec carries standing IDs without modifying ship bytes or seats", TravelTests.NativeSnapshotRoundTrip);
            Test("native planet-boundary recovery reproduces cancellation of a pending ship transfer", TravelTests.NativeRecoveryCancellation);
            Test("aboard recovery guard preserves pending travel and retains foreign, detached and unrelated native recovery", TravelTests.NativeRecoveryGuard);
            Test("boundary recovery rewrite preserves return targets and rejects a missing return", TravelTests.RecoveryControlFlow);
            Test("worker vessel recovery remains active under the client-only recovery hook", TravelTests.NativeVesselRecoveryReproduction);
            Test("worker boundary protection prepares once before native vessel recovery", VesselBoundaryTests.RecoveryRace);
            Test("boundary cancellation expiry context changes and exceptions restore recovery", VesselBoundaryTests.BoundedCancellation);
            Test("dedicated hub retains committed manifests across worker load and prevents replay", TravelTests.HubLoadingAndReplay);
            Test("dedicated hub rejects foreign cancellation and does not revive cancelled or expired travel", TravelTests.HubCancellation);
            Test("actual Harmony travel hooks bind enum and by-reference native arrival arguments", TravelTests.HarmonySignatures);
            Test("extracted native grid lookup reproduces recorded moving elevator miss while stationary climb works", BlockContactTests.RecordedMovingLookup);
            Test("local block bounds and point preserve native elevator callback with jetpack off across pose gaps", BlockContactTests.LocalContactPipeline);
            Test("block contacts retain other actors, seat callbacks, exceptions, native fallback and real local exit", BlockContactTests.ContactLifecycle);
            Test("local block coordinates retain grid offset, rotation, all bounds corners and CV/SV/HV scale", BlockContactTests.RotatedContactBounds);
            Test("contact callsite rewrite preserves branches and refuses changed native layouts", BlockContactTests.ContactRewriteLayout);
            Test("walking turn queued before presentation reproduces consumed but frozen mouse input", WalkingLookTests.QueuedTurnReproduction);
            Test("native walking turn adapter preserves yaw wrap, ship rotation, repeated presentation and idle", WalkingLookTests.ImmediateTurnAndTransport);
            Test("unowned walking rotation retains native scheduling and unpatch restores original", WalkingLookTests.NativeFallbackAndUnpatch);
            Test("walking look rewriter preserves control flow and refuses missing or duplicate sites", WalkingLookTests.ChangedLayout);
            Test("shared frame codec rejects malformed, unknown and non-finite payloads", SharedFrameTests.Codec);
            Test("shared frame identity, playfield, session and replay boundaries", SharedFrameTests.AuthorityAndSessions);
            Test("seat, jump, elevator and flight preserve membership; leave, reconnect and expiry detach", SharedFrameTests.ModesAndDeparture);
            Test("observer reconstructs local travel against the same moving and rotating displayed hull", SharedFrameTests.SharedDisplayFrame);
            Test("native receive factory reproduces unknown ID 139, then registration permits decode and sender binding", SharedFrameTests.NativeFactoryRegistration);
            Test("native packet registration refuses conflicting mappings without partial writes", SharedFrameTests.RegistryConflicts);
            Test("native receive factory and envelope codec carry two server-confirmed ship baselines", SharedFrameTests.NativePoseConfirmation);
            Test("extracted native packet factory and codec carry client A to relay to client B over loopback sockets", SharedFrameTests.NativeEnvelopeTwoClients);
            Test("native channel getter routes only tagged frame packets to gameplay and restores on unpatch", SharedFrameTests.NativeChannelRouting);
            Test("shared frames stay in their playfield and repeated receipts cannot inflate evidence", SharedFrameTests.PlayfieldAndReceiptScope);
            Test("actual Harmony prefix retains position and X velocity before native argument mutation", SharedFrameTests.HistoryMutation);
            Test("recorded client seat generic field: duplicate parameter names, closed owners, copy, hook and unpatch", GenericFieldTests.RecordedSeatField);
            Test("generic field parameters bind by position through nested generics and arrays", GenericFieldTests.NestedSignatures);
            Test("recorded seat ownership race: complete derived exit before deferred activation", MultiplayerHandoffTests.DerivedSeatOrdering);
            Test("70/101 ms exit acknowledgements, nesting and session reset preserve transaction ordering", MultiplayerHandoffTests.RepeatedAcknowledgements);
            Test("seat velocity-history reset does not launch the local player during handoff", MultiplayerHandoffTests.VelocityHistoryReset);
            Test("fresh playfield motion transfers once after server body readiness", MultiplayerHandoffTests.ServerTransfer);
            Test("server body-mode IL preserves coasting and returns native control on authority loss", MultiplayerHandoffTests.NativeModeRewrite);
            Test("396 m parked-body handoff aligns pose once across an origin shift", ServerCoastingTests.ParkedBodyAndOrigin);
            Test("remote COM velocity excludes the parked root translation", ServerCoastingTests.RotatingCenterOfMass);
            Test("pose handoff rejects stale, remote, cancelled and discontinuous samples", ServerCoastingTests.PoseCancellation);
            Test("native cache restore preserves live velocity and collision results within coasting scope", ServerCoastingTests.NativeCacheAndCollision);
            Test("unpiloted automatic braking filter retains thrust and pilot damping preference", ServerCoastingTests.NativeAutomaticBraking);
            Test("unknown controller cache and braking IL is refused", ServerCoastingTests.ChangedControllerIL);
            Test("recorded 396 metre handoff retains local state until two server confirmations", ShipFrameContinuityTests.RecordedDeparture);
            Test("recorded unseat speed dip bridges to confirmed server motion", ShipFrameContinuityTests.RecordedSpeedDipRecovery);
            Test("handoff prediction expires and accepts real server stops", ShipFrameContinuityTests.HandoffPredictionLimits);
            Test("stale duplicate reordered invalid and missing confirmations cannot strand recovery", ShipFrameContinuityTests.StaleAndMissingConfirmation);
            Test("inconsistent server pose pair cannot authorize a correction", ShipFrameContinuityTests.InconsistentReferences);
            Test("continuous ship rotation retains local position and mouse look", ShipFrameContinuityTests.RotatingLocalFrameAndLook);
            Test("local jump velocity survives history resets and real departure inherits point velocity", ShipFrameContinuityTests.LocalVelocityAndWorldDeparture);
            Test("real floating origin rebase is distinct from the recorded hull discontinuity", ShipFrameContinuityTests.OriginRebase);
            Test("server baseline uses authenticated identity membership and correlated protocol two response", ShipFrameContinuityTests.AuthenticatedServerBaseline);
            Test("multiplayer passenger authority never owns remote ship physics", MultiplayerFrameTests.AuthorityBoundary);
            Test("single-player ship ownership and coasting policy remain intact", MultiplayerFrameTests.SinglePlayerBaseline);
            Test("remote 20 Hz displayed poses retain 100.5 m/s transport across 50 Hz character steps", MultiplayerFrameTests.RepeatedDisplayPoses);
            Test("remote acceleration and collision results preserve airborne inertia", MultiplayerFrameTests.RemoteAccelerationAndCollision);
            Test("passenger presentation follows live remote hull while idle locomotion stays zero", MultiplayerFrameTests.LiveHullPresentation);
            Test("remote transport handles tilted ships and floating-origin shifts", MultiplayerFrameTests.RemoteOriginAndTilt);
            Test("invalid remote motion, teleports and unsupported turns release safely", MultiplayerFrameTests.InvalidRemoteState);
            Test("observer timing fixture exposes native-world replication offset instead of claiming synchronization", MultiplayerFrameTests.ObserverTimingEvidence);
            Test("native elevator ascent/descent, idle, running and local heading", LocalControlRegressionTests.ElevatorControls);
            Test("elevator transport on recorded 100.5 m/s ship retains accepted collision velocity", LocalControlRegressionTests.ElevatorTransportAndCollision);
            Test("invalid native elevator speed is rejected", LocalControlRegressionTests.ElevatorInvalidInput);
            Test("free-flight mouse pitch/yaw/roll consumes native encoded input at varied frame times", LocalControlRegressionTests.FreeFlightLook);
            Test("free-flight look stops when input stops and rejects invalid samples", LocalControlRegressionTests.LookDoesNotAccumulate);
            Test("actual Harmony passenger reset patch retains motion and native seat state without undoing collisions", LocalControlRegressionTests.PassengerResetPatch);
            Test("seat transaction scope handles nesting, exceptions and stale cleanup", LocalControlRegressionTests.SeatScopeCleanup);
            Test("passenger reset patch rejects missing, duplicate and nonzero sites", LocalControlRegressionTests.ResetPatchRejectsUnknownLayout);
            Test("pilot takeover keeps the coasting body active while shutdown restores the native selector", LocalTransitionTests.PilotHandoff);
            Test("native presence sensor lease restores solid shape and body state across repeated seats", LocalTransitionTests.TriggerLease);
            Test("jetpack activation and release preserve recorded 55.566 m/s ship transport", LocalTransitionTests.FlightTransport);
            Test("ship-local jetpack ascent/descent, heading, diagonal speed and collision feedback", LocalTransitionTests.FlightControlsAndCollision);
            Test("local flight rejects invalid timesteps", LocalTransitionTests.FlightRejectsInvalidData);
            Test("stationary local character does not animate from recorded ship transport", LocalPresentationTests.IdleTransport);
            Test("locomotion uses accepted local travel, including collisions and airborne steps", LocalPresentationTests.ActualRelativeTravel);
            Test("rendered character shares live hull pose and local interpolation", LocalPresentationTests.RenderPoseAlignment);
            Test("reboarding resets local presentation and retains native interpolation lease", LocalPresentationTests.ResetAndLease);
            Test("actual Harmony patch feeds both native locomotion consumers while preserving world history", LocalPresentationTests.NativeConsumerPatch);
            Test("locomotion patch rejects missing, duplicate or changed native sites", LocalPresentationTests.PatchRejectsUnknownLayout);
            Test("local frame point/velocity round trips and floating origin", LocalFrameTests.RoundTripsAndOrigin);
            Test("local frame airborne inertia under acceleration, braking and reversal", LocalFrameTests.AcceleratingJump);
            Test("local frame ground motion and post-collision velocity transfer", LocalFrameTests.GroundAndCollisionTransfer);
            Test("local frame accepts large ships and rejects unsupported ownership, turns and teleports", LocalFrameTests.GuardUnsupportedFrames);
            Test("recorded tilted CV, inverted/vertical frames, local heading and jump inertia", LocalFrameTests.TiltedFrameRegression);
            Test("large geometry preparation visits every item once across bounded batches", IncrementalWorkTests.LargeSnapshots);
            Test("geometry preparation respects time budget and measures a single native overrun", IncrementalWorkTests.TimeBudgetAndNativeOverrun);
            Test("cancelled or failed preparation disposes once and never publishes Ready", IncrementalWorkTests.CancellationAndFailure);
            Test("enable anywhere, board on foot, leave to world movement and reboard without another command", LocalFrameControlTests.EnableAnywhereAndBoarding);
            Test("seat transitions and failed preparation do not spam, steal ownership or block retries", LocalFrameControlTests.SeatTransitionsAndFailedAttempts);
            Test("recorded inactive native capsule keeps component ownership across seat transitions", LocalFrameControlTests.InactiveCapsuleRegression);
            Test("server handshake automatically enables movement and prepares an already boarded ship once", LocalFrameControlTests.AutomaticHandshakeAndBoarding);
            Test("explicit off survives handshakes and resets until manual on or a new runtime", LocalFrameControlTests.AutomaticRespectsOff);
            Test("new playfields require their own handshake and automatic startup stays scoped to multiplayer", LocalFrameControlTests.AutomaticSessionIsolation);
            Test("automatic startup cannot spam failed preparation or revive a disabled runtime", LocalFrameControlTests.AutomaticDoesNotRetryFailures);
            Test("7417 changing surfaces complete preparation and refresh a continuously moving door", LiveGeometryTests.MovingPreparation);
            Test("live geometry handles additions, removals, duplicates, inactive doors and empty mesh transitions", LiveGeometryTests.AddRemoveAndEmptyMeshes);
            Test("cancelled refresh preserves unseen geometry and later cleanup removes each record once", LiveGeometryTests.CancelledRefresh);
            Test("ship dimensions govern local occupancy across CV/SV/HV scales, world rotation and origin", LiveGeometryTests.ShipDimensions);
            Test("rotated and scaled surface bounds enclose all corners", LiveGeometryTests.TransformedSurfaceBounds);
            Test("local character body lease restores original flags exactly once", LocalFrameTests.RestoreLeaseOnce);
            Test("duplicate-name field regression: reproduce, resolve, execute, patch and unpatch", ObfuscatedFieldTests.Verify);
            Test("same-type field modifiers: resolve, copy, patch and unpatch every signature", ObfuscatedFieldTests.VerifyModifiers);
            Test("moving CV/SV/HV open-seat exit: enclosed/stationary cases, guards and unpatch", SeatExitTests.Verify);
            Test("open pilot/passenger classification rejects enclosed and unknown seats", SeatExitTests.OpenSeatClassification);
            Test("seat-exit patch rejects missing or duplicate moving-state queries", SeatExitTests.RejectUnknownIL);
            Test("native-pattern exit-stop fixture, scoped motion restore, guards and Harmony unpatch", ExitMomentumTests.NativeStopAndScopedRestore);
            Test("one-time authority handoff preserves later collisions; stale/cancelled transfers expire", ExitMomentumTests.HandoffAndCollision);
            Test("server pose motion handles rotation/COM, duplicate ticks and invalid/stale samples", ExitMomentumTests.ServerPoseSampling);
            Test("body activation regression: retained speed must move outside seat; cache, impact, kinematic and unpatch", BodyActivationTests.FreezeRegression);
            Test("physics-root rewrite rejects missing or duplicate activation sites", BodyActivationTests.RejectUnknownIL);
            Test("own-hull collision exclusions preserve world/player collisions and existing ignores across rebuilds", BodyActivationTests.OwnCollisionPairs);
            Test("self-collision coverage exceeds the old 262144-pair cutoff and restores correctly", CollisionScaleTests.AboveOldLimit);
            Test("two million self-collision pairs use compact masks, linear activity checks and correct rebuild cleanup", CollisionScaleTests.CompactMillionPairState);
            Test("partial pair-write failures, inactive colliders and duplicates preserve ownership", CollisionScaleTests.PartialFailureAndInactivePairs);
            Test("live seat-exit velocity deficit reproduces before passenger inheritance", SeatDepartureTests.MissingInheritanceRegression);
            Test("seat departure retains stationary/100 m/s/oblique motion through first airborne ticks and walking", SeatDepartureTests.TranslatingExit);
            Test("seat departure inherits rotation at the exit point across a floating-origin shift", SeatDepartureTests.RotatingExitAndOrigin);
            Test("passenger inheritance and frame seed run once, preserve collisions, and respect cancellation/ownership", SeatDepartureTests.OneShotAndCancellation);
            Test("passenger handoff waits for body readiness and rejects stale/invalid motion", SeatDepartureTests.ReadinessAndInvalidData);
            Test("initial seat-exit contact grace is bounded and retains airborne inertia without attachment", SeatDepartureTests.InitialAirborneWindow);
            Test("exit diagnostic capture survives support loss but stops at its time or event bound", DiagnosticWindowTests.CaptureBounds);
            Test("exit diagnostic capture cancels, rejects invalid time and restarts for the next exit", DiagnosticWindowTests.Lifecycle);
            Test("recorded contact storm preserves the complete first-second core capture and paired contact sampling", DiagnosticWindowTests.RecordedContactStorm);
            Test("routine trace cannot consume the exit reserve; full session and repeated samples stay bounded", DiagnosticWindowTests.TraceReservation);
            Test("recorded camera/hull matrix exclusion and separated contact regression", CollisionEligibilityTests.RecordedCameraContact);
            Test("native layer inclusion, exclusion, priorities and pair ignores survive replacement", CollisionEligibilityTests.NativeOverrides);
            Test("recorded static-cockpit impulse and replacement moving-surface velocity", MovingInteriorTests.RecordedExitRegression);
            Test("moving interior rotates about the ship COM and supplies distinct point velocities", MovingInteriorTests.RotationAndOffsetCenter);
            Test("native visual exit point maps once into the physics frame across origin changes", MovingInteriorTests.ExitPoseAndFloatingOrigin);
            Test("1000 interior targets share the walking frame and preserve independent jump inertia", MovingInteriorTests.TrackingAndAirborne);
            Test("interior trajectories reject teleports, invalid time/data and excessive turns", MovingInteriorTests.RejectDiscontinuity);
            Test("proxy contact isolation and native pair restoration preserve external ignores", MovingInteriorTests.PlayerOnlyFilterAndLease);
            Test("XRef missing-component regression: reproduce and skip 1000 non-entity contacts", EntityLookupTests.MissingComponentRegression);
            Test("entity lookup preserves valid ancestors and rejects missing/destroyed targets", EntityLookupTests.HierarchyAndLifetime);
            Test("unexpected entity lookup errors still propagate", EntityLookupTests.UnexpectedFailurePropagates);
            Test("jump/air-drag/landing model is invariant between stationary and 50 m/s ships", JumpTests.CompleteJump);
            Test("airborne inertia and first landing avoid double ship acceleration", JumpTests.AccelerationAndLanding);
            Test("collision velocity, leaving the vessel, timeout and reset", JumpTests.CollisionAndRelease);
            Test("native drag restoration and bounded relative damping", JumpTests.DragRestoration);
            Test("1000 jump transitions and teleport/stale-sample release", JumpTests.RepeatedJumpsAndDiscontinuities);
            Test("stationary deck preserves walking", () => Near(MotionMath.RelativeVelocity(new Vector3(4, 0, 0), Vector3.Zero, Vector3.Zero, false), new Vector3(4, 0, 0)));
            Test("50 m/s deck preserves 4 m/s walking", () =>
            {
                var ship = new Vector3(50, 0, 0);
                var relative = MotionMath.RelativeVelocity(new Vector3(54, 0, 0), ship, ship, true);
                Near(relative, new Vector3(4, 0, 0));
                Near(MotionMath.WorldVelocity(relative, ship), new Vector3(54, 0, 0));
            });
            Test("support acceleration applied once", () =>
            {
                var relative = MotionMath.RelativeVelocity(new Vector3(14, 0, 0), new Vector3(10, 0, 0), new Vector3(20, 0, 0), true);
                Near(relative, new Vector3(4, 0, 0));
                var world = MotionMath.WorldVelocity(relative, new Vector3(20, 0, 0));
                var lateRelative = MotionMath.RelativeVelocity(world, Vector3.Zero, new Vector3(20, 0, 0), false);
                Near(lateRelative, new Vector3(4, 0, 0));
                Near(MotionMath.WorldVelocity(lateRelative, new Vector3(20, 0, 0)), new Vector3(24, 0, 0));
            });
            Test("boarding does not add ship speed twice", () =>
                Near(MotionMath.RelativeVelocity(new Vector3(12, 0, 0), Vector3.Zero, new Vector3(10, 0, 0), false), new Vector3(2, 0, 0)));
            Test("takeoff velocity composition", () =>
                Near(MotionMath.WorldVelocity(new Vector3(0, 6, 0), new Vector3(50, 0, 0)), new Vector3(50, 6, 0)));
            Test("stationary relative player survives 10000 speed changes", () =>
            {
                Vector3 previous = Vector3.Zero, world = Vector3.Zero;
                for (int i = 0; i < 10000; i++)
                {
                    Vector3 current = new Vector3((float)Math.Sin(i * .01) * 50, 0, (float)Math.Cos(i * .01) * 20);
                    var relative = MotionMath.RelativeVelocity(world, previous, current, true);
                    Near(relative, Vector3.Zero, 0.003f);
                    world = MotionMath.WorldVelocity(relative, current);
                    previous = current;
                }
            });
            Test("remote translated pose measures deck speed", () =>
            {
                Check(MotionMath.TryPoseVelocity(Vector3.Zero, Quaternion.Identity, new Vector3(1, 0, 0), Quaternion.Identity,
                    new Vector3(1, 0, 5), .02f, out var v));
                Near(v, new Vector3(50, 0, 0));
            });
            Test("turning deck uses velocity at the passenger", () =>
            {
                var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI / 2f);
                var point = Vector3.Transform(new Vector3(10, 0, 0), rotation);
                Check(MotionMath.TryPoseVelocity(Vector3.Zero, Quaternion.Identity, Vector3.Zero, rotation, point, .1f, out var v));
                Near(v, (point - new Vector3(10, 0, 0)) / .1f, .001f);
            });
            Test("floating-origin change does not create motion", () =>
            {
                var oldAbsolute = new Vector3(2000, 0, 0) + new Vector3(1000, 0, 0);
                var newAbsolute = new Vector3(0, 0, 0) + new Vector3(3000, 0, 0);
                Check(MotionMath.TryPoseVelocity(oldAbsolute, Quaternion.Identity, newAbsolute, Quaternion.Identity,
                    newAbsolute + Vector3.UnitX, .02f, out var v));
                Near(v, Vector3.Zero);
            });
            Test("teleports and invalid sampling are rejected", () =>
            {
                Check(!MotionMath.TryPoseVelocity(Vector3.Zero, Quaternion.Identity, new Vector3(1000, 0, 0), Quaternion.Identity, new Vector3(1000, 0, 0), .02f, out _));
                Check(!MotionMath.TryPoseVelocity(Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity, Vector3.Zero, 0, out _));
                Check(!MotionMath.TryPoseVelocity(Vector3.Zero, Quaternion.Identity, Vector3.Zero, Quaternion.Identity, Vector3.Zero, .5f, out _));
                Check(!MotionMath.Continuous(Vector3.Zero, new Vector3(float.NaN, 0, 0), .02f));
                Check(!MotionMath.Continuous(Vector3.Zero, new Vector3(100, 0, 0), .02f));
            });
            Test("configuration defaults to non-mutating diagnostics", () =>
            {
                var settings = Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing"));
                Check(settings.Mode == RunMode.Diagnostics && !settings.Trace && !settings.PreserveInterior && settings.AllowMovingSeatExit && settings.PreserveExitMomentum);
            });
            Test("seat-exit configuration can disable and re-enable the feature", () =>
            {
                string file = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(file, "AllowMovingSeatExit=false\nPreserveExitMomentum=false");
                    Check(!Settings.Load(file).AllowMovingSeatExit && !Settings.Load(file).PreserveExitMomentum);
                    File.WriteAllText(file, "Mode=Experimental\nAllowMovingSeatExit=true\nPreserveExitMomentum=true");
                    Check(Settings.Load(file).AllowMovingSeatExit && Settings.Load(file).Mode == RunMode.Experimental && Settings.Load(file).PreserveExitMomentum);
                }
                finally { File.Delete(file); }
            });
            Test("invalid config modes are rejected", () =>
            {
                string file = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(file, "Mode=88");
                    bool rejected = false;
                    try { Settings.Load(file); } catch (FormatException) { rejected = true; }
                    Check(rejected);
                }
                finally { File.Delete(file); }
            });
            ValidateBuildGuard();
            Console.WriteLine("PASS: " + passed + " checks. No Unity gameplay or multiplayer simulation was executed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidateBuildGuard()
    {
        Test("foreign assembly is refused before resolving hooks", () =>
        {
            bool rejected = false;
            try { new Build5150(typeof(Program).Assembly); } catch (NotSupportedException) { rejected = true; }
            Check(rejected);
        });
    }

    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool result) { if (!result) throw new Exception("Assertion failed."); }
    private static void Near(Vector3 a, Vector3 b, float tolerance = .0001f)
    {
        if (!MotionMath.Finite(a) || Vector3.Distance(a, b) > tolerance) throw new Exception("Expected " + b + ", got " + a);
    }
}
