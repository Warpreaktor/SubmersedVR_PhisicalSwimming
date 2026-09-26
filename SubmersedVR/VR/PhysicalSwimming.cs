using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Converts physical controller strokes into ordinary Subnautica movement input.
    /// Hands generate force; that force accelerates a virtual swimmer body with its own
    /// 3D inertia and water drag. Subnautica still owns the final player physics,
    /// collision, swim speed, fins, Seaglide modifiers, etc.
    /// </summary>
    static class PhysicalSwimming
    {
        // Hand movement below this speed is treated as tracking noise / casual movement.
        // Fixed low tracking-noise threshold; intentionally not exposed in the options menu.
        private static float MinStrokeSpeed => Settings.PhysicalSwimmingMinStrokeSpeed;

        // A hand moving this fast (or faster) contributes its maximum propulsion.
        private static float FullStrokeSpeed => Settings.PhysicalSwimmingFullStrokeSpeed
            / Mathf.Max(0.25f, Settings.PhysicalSwimmingStrokeSensitivity);

        // Reject impossible one-frame tracking spikes/recentering jumps.
        private const float MaxTrackedHandSpeed = 5.0f;
        private const float MaxPositionJump = 0.25f;

        // One hand can still swim while the other holds a tool, but two hands are stronger.
        private static float MaxSingleHandContribution => Settings.PhysicalSwimmingMaxSingleHandContribution;

        // Converts the summed hand force into acceleration of the virtual swimmer body.
        // Body velocity is capped to ordinary controller-input magnitude afterwards.
        private static float HandForceCoefficient => Settings.PhysicalSwimmingHandForceCoefficient;
        private static float MaxBodyVelocity => Settings.PhysicalSwimmingMaxBodyVelocity;

        // A broadside hand sweep has one tangential component around the swimmer's body axis.
        // TurningStrength controls what that component becomes: at 0 it remains pure sideways
        // translation; from 1 upward it is fully converted into yaw and only angular impulse grows.
        // The resulting angular velocity is damped by the SAME WaterResistance as linear glide.
        private static float TurningStrength => Mathf.Max(0f, Settings.PhysicalSwimmingTurningStrength);
        private static float TurnLinearConversion => Mathf.Clamp01(TurningStrength);
        private const float TurnAccelerationAtStrengthOne = 336.0f;
        private const float YawAngularVelocityStopThreshold = 0.30f;
        private const float YawAngularVelocityEpsilon = 0.0001f;

        // Stored swimmer velocity loses the same fraction per second in every direction.
        // Stroke strength remains a separate concern: hands add acceleration first, then water
        // resistance damps the resulting body velocity without remembering stroke history.
        private static float WaterResistance => Settings.PhysicalSwimmingWaterResistance;

        // These epsilons are numerical guards only, not gameplay dead-zones. Small deliberate
        // strokes must be able to build sub-percent body velocity over several frames instead of
        // being snapped back to zero before they can accumulate.
        private const float BodyVelocityEpsilon = 0.000001f;
        private const float HandForceEpsilon = 0.00001f;
        private static float BodyVelocityDeadZone => Settings.PhysicalSwimmingBodyVelocityDeadZone;

        // A weak stroke can briefly produce zero force while the hand turns edge-on or crosses the
        // configured minimum speed. Keep the body-dead-zone disarmed for a tiny grace period so one
        // physical stroke is not chopped into unrelated one-frame impulses.
        private const float HandForceIdleGraceSeconds = 0.12f;

        // Internal surface-state thresholds. These are stability constants for bridging the
        // transition where vanilla Player.IsSwimming() flickers around the Y=0 ocean surface,
        // not player-facing tuning controls.
        internal const float SurfaceSwimTolerance = 0.65f;
        internal const float SurfaceBridgeDisarmHeight = 1.25f;

        // Subnautica's native ascent/descent controls are boolean actions (the same channels
        // used by the two VR triggers). Physical swimming has an analog vertical strength, so
        // convert it to a pulse-density signal instead of holding the trigger at 100% for the
        // entire inertial tail. Tiny inputs below this internal threshold are treated as zero
        // so numerical noise cannot emit stray native MoveUp/MoveDown pulses.
        internal const float VerticalPwmMinimumStrength = 0.005f;

        // Emergency vertical gestures are deliberate static body poses rather than strokes.
        // Players may enable/disable each gesture, while the tested recognition thresholds stay
        // internal so the options UI exposes behavior rather than detector calibration.
        private static bool EmergencyAscentEnabled => Settings.PhysicalSwimmingEmergencyAscent;
        private const float EmergencyAscentHoldTime = 0.45f;
        private const float EmergencyAscentMinHandDrop = 0.60f;
        private const float EmergencyAscentMaxHorizontalDistance = 0.50f;
        private const float EmergencyAscentMinHandSeparation = 0.18f;
        private const float EmergencyAscentPalmInwardThreshold = 0.35f;

        private static bool EmergencyDiveEnabled => Settings.PhysicalSwimmingEmergencyDive;
        private const float EmergencyDiveHoldTime = 0.35f;
        private const float EmergencyDiveMinHandRise = 0.25f;
        private const float EmergencyDiveMaxHorizontalDistance = 0.45f;
        private const float EmergencyDiveMinHandSeparation = 0.10f;
        private const float EmergencyDivePalmFacingThreshold = 0.35f;

        // Push Stop is a deliberate two-palm braking gesture: hold both hands in front of the
        // body as if pressing against an invisible wall, with both palms facing forward. The
        // gesture may be disabled by the player, but its recognition thresholds and damping stay
        // internal so the UI exposes behavior rather than detector calibration.
        private static bool InertiaBrakeEnabled => Settings.PhysicalSwimmingInertiaBrake;
        internal const float InertiaBrakeHoldTime = 0.20f;
        internal const float InertiaBrakeMinForwardDistance = 0.30f;
        internal const float InertiaBrakeMaxHorizontalDistance = 0.75f;
        // Push Stop intentionally does not require the hands to be spread apart. Players often
        // perform the natural "push against a wall" pose with both hands close together. Keep the
        // value at zero so the existing telemetry schema remains useful without gating the gesture.
        internal const float InertiaBrakeMinHandSeparation = 0f;
        internal const float InertiaBrakeMaxVerticalOffset = 0.55f;
        internal const float InertiaBrakePalmForwardThreshold = 0.55f;
        internal const float InertiaBrakeLinearDamping = 5.00f;
        internal const float InertiaBrakeAngularDamping = 6.00f;

        // Fin Kick is a direct locomotion layer, not stored body momentum. Holding both
        // lower grip buttons adds a steady input toward the HMD look direction so fins can
        // stabilize long swims without replacing physical hand strokes.
        private const bool FinKickEnabled = true;
        private static float FinKickStrength => Settings.PhysicalSwimmingFinKickStrength;

        // Actual player motion is tracked separately from the virtual swimmer velocity.
        // This lets us avoid commanding reverse thrust while vanilla Subnautica is still
        // physically carrying the player forward from the previous stroke.
        private static float PlayerVelocityDeadZone => Settings.PhysicalSwimmingPlayerVelocityDeadZone;
        private const float PlayerVelocitySmoothing = 12f;
        private const float MaxTrackedPlayerSpeed = 20f;
        private const float MaxPlayerPositionJump = 2.0f;

        // Maneuverability is the single user-facing control for changing course. It deliberately
        // does not change stroke force or maximum speed. Instead it controls how strongly the
        // perpendicular part of a stroke redirects stored momentum, how effective weak
        // counter-strokes are at shedding old momentum, and how much opposing force is required
        // before reverse propulsion is considered intentional.
        private static float Maneuverability => Mathf.Clamp(Settings.PhysicalSwimmingManeuverability, 0f, 2f);

        // Extra steering applied after the physically correct velocity + stroke impulse step.
        // 0 means pure vector addition, 1 adds 75% of the natural turn as steering assist,
        // and 2 may add up to 200%. The assist rotates direction only and never adds speed.
        internal static float ManeuverabilitySteeringAssistFactor
        {
            get
            {
                float value = Maneuverability;
                return value <= 1f
                    ? Mathf.Lerp(0f, 0.75f, value)
                    : Mathf.Lerp(0.75f, 2.00f, value - 1f);
            }
        }

        internal static float ManeuverabilityReverseForceThreshold
        {
            get
            {
                float value = Maneuverability;
                return value <= 1f
                    ? Mathf.Lerp(0.55f, 0.30f, value)
                    : Mathf.Lerp(0.30f, 0.10f, value - 1f);
            }
        }

        internal static float ManeuverabilityWeakBrakeFloor
        {
            get
            {
                float value = Maneuverability;
                return value <= 1f
                    ? Mathf.Lerp(0.02f, 0.06f, value)
                    : Mathf.Lerp(0.06f, 0.40f, value - 1f);
            }
        }

        internal static float ManeuverabilityMaxTurnRate
        {
            get
            {
                float value = Maneuverability;
                return value <= 1f
                    ? Mathf.Lerp(75f, 180f, value)
                    : Mathf.Lerp(180f, 360f, value - 1f);
            }
        }

        // Hard-stop collision reconciliation intentionally ignores obstacle normals. The game's
        // real Player movement is the source of truth: if it was moving recently, then becomes
        // nearly stationary while our virtual body still carries momentum, something external
        // stopped the swimmer and all stored inertia must be discarded.
        internal const float CollisionHardStopArmSpeed = 0.75f;
        internal const float CollisionHardStopSpeed = 0.17f;
        internal const float CollisionHardStopConfirmTime = 0.00f;
        private const float CollisionRecentMotionWindow = 0.35f;
        private const float CollisionVirtualSpeedThreshold = 0.10f;

        // Palm Physics is exposed as one gameplay control instead of four implementation details.
        // Technique Realism = 1.00 is exactly the tuned model that previously used:
        // dead-zone 0.25, exponent 2.50, edge drag 0.00 and back-of-hand drag 0.20.
        // Lower values forgive imperfect technique. Higher values require a cleaner broadside catch,
        // retain a little more edge resistance, and increasingly punish a sloppy back-of-hand recovery.
        // Full broadside palm placement still reaches 100% of the normal stroke strength at every level.
        internal static float PalmDragDeadZone => EvaluateTechniqueCurve(0.05f, 0.15f, 0.25f, 0.32f, 0.38f);
        internal static float PalmDragExponent => EvaluateTechniqueCurve(1.20f, 1.80f, 2.50f, 3.25f, 4.00f);
        internal static float PalmEdgeDrag => EvaluateTechniqueCurve(0.00f, 0.00f, 0.00f, 0.03f, 0.05f);
        internal static float BackOfHandDragMultiplier => EvaluateTechniqueCurve(0.00f, 0.10f, 0.20f, 0.40f, 0.65f);

        private static float EvaluateTechniqueCurve(
            float relaxed,
            float forgiving,
            float balanced,
            float strict,
            float hardcore
        )
        {
            float realism = Mathf.Clamp(Settings.PhysicalSwimmingTechniqueRealism, 0f, 2f);

            if (realism <= 0.50f)
            {
                return Mathf.Lerp(relaxed, forgiving, realism / 0.50f);
            }

            if (realism <= 1.00f)
            {
                return Mathf.Lerp(forgiving, balanced, (realism - 0.50f) / 0.50f);
            }

            if (realism <= 1.50f)
            {
                return Mathf.Lerp(balanced, strict, (realism - 1.00f) / 0.50f);
            }

            return Mathf.Lerp(strict, hardcore, (realism - 1.50f) / 0.50f);
        }

        private static float VelocitySmoothing => Settings.PhysicalSwimmingVelocitySmoothing;

        private struct HandStrokeState
        {
            public bool initialized;
            public Vector3 previousPosition;
            public Vector3 filteredVelocity;
        }

        internal struct HandTelemetry
        {
            public Vector3 position;
            public Vector3 rawVelocity;
            public Vector3 filteredVelocity;
            public float speed;
            public Vector3 palmNormal;
            public float palmAlignment;
            public float signedPalmAlignment;
            public float backOfHandFactor;
            public float effectivePalmAlignment;
            public float baseDragFactor;
            public float dragFactor;
            public float baseSpeedStrength;
            public float speedStrength;
            public Vector3 force;
            public bool canStroke;
            public bool trackingJumpRejected;
        }

        internal struct BodyForceTelemetry
        {
            public Vector3 velocityBefore;
            public Vector3 velocityAfter;
            public Vector3 deltaVelocity;
            public float forceStrength;
            public float directionDot;
            public float actualPlayerDirectionDot;
            public float actualPlayerOpposingForce;
            public Vector3 actualPlayerVelocity;
            public bool weakOpposingBrake;
            public bool actualPlayerReverseGuard;
            public bool intentionalReverseAllowed;
            public bool bodyVelocityDeadZoneApplied;
            public Vector3 perpendicularDeltaVelocity;
            public Vector3 velocityAfterRedirection;
            public float momentumRedirectionAngleDegrees;
            public bool momentumRedirectionApplied;
            public float maneuverabilityNaturalTurnDegrees;
            public float maneuverabilityTurnRequestedDegrees;
            public float maneuverabilityTurnAppliedDegrees;
            public float maneuverabilityReverseFactor;
            public Vector3 rawActualPlayerVelocity;
            public float collisionRecentPeakSpeed;
            public float collisionCurrentRealSpeed;
            public float collisionRecentMotionSeconds;
            public float collisionStallSeconds;
            public float collisionSpeedDrop;
            public bool collisionHardStopCandidate;
            public bool collisionHardStopApplied;
            public Vector3 velocityAfterCollisionRelease;
        }

        private static HandStrokeState leftHand;
        private static HandStrokeState rightHand;

        // Kept in world space so looking or turning the head does not rotate existing momentum.
        // A player can therefore look sideways while continuing to glide in the old direction.
        private static Vector3 worldBodyVelocity;
        private static Vector3 swimInput;

        // Angular swimmer state. The accumulated yaw offset is applied by VRCameraRig after it
        // copies the normal game camera-root transform, so physical turning affects the HMD,
        // controllers and movement reference together.
        private static float yawAngularVelocity;
        private static float physicalYawOffsetDegrees;
        private static float currentYawTorque;

        private static Vector3 previousPlayerPosition;
        private static Vector3 actualPlayerVelocity;
        private static Vector3 rawActualPlayerVelocity;
        private static bool playerVelocityInitialized;

        // Collision hard-stop state. We remember only the recent peak of the real Player speed;
        // no obstacle direction is inferred. A rapid real stop simply cancels all virtual inertia.
        private static float collisionRecentPeakSpeed;
        private static float collisionRecentMotionSeconds;
        private static float collisionStallSeconds;

        private static bool active;
        private static SwimmingControlMode controlMode = SwimmingControlMode.HandSwimming;

        // Surface bridge state. Subnautica reports IsSwimming() == false once the player's
        // head/upper body reaches the surface even though the native MoveDown control can still
        // dive back under. Remember the transition from swimming to surface and keep only the
        // physical dive channel alive until the game reports swimming again.
        private static bool swimmingStateInitialized;
        private static bool wasSwimming;
        private static bool surfaceDiveBridgeActive;
        private static bool surfaceSwimActive;
        private static float currentWaterLevel;
        private static float currentSurfaceDistance;

        private static int lastUpdatedFrame = -1;
        private static int previousUpdatedFrame = -1;

        // Pulse-density modulator for the game's boolean MoveUp/MoveDown channels.
        private static float verticalPwmAccumulator;
        private static int verticalPwmDirection;
        private static bool verticalPwmUp;
        private static bool verticalPwmDown;

        // Emergency vertical gesture state and telemetry. Metrics are kept even before activation
        // so a recording shows exactly which individual pose condition failed its threshold.
        private static bool emergencyAscentCandidate;
        private static float emergencyAscentHoldSeconds;
        private static bool emergencyAscentActive;
        private static bool emergencyAscentHandsLow;
        private static bool emergencyAscentHandsNearBody;
        private static bool emergencyAscentHandsSeparated;
        private static bool emergencyAscentPalmsInward;
        private static float emergencyAscentHandSeparation;
        private static float emergencyLeftHandDrop;
        private static float emergencyRightHandDrop;
        private static float emergencyLeftBodyDistance;
        private static float emergencyRightBodyDistance;
        private static float emergencyLeftPalmInward;
        private static float emergencyRightPalmInward;

        private static bool emergencyDiveCandidate;
        private static float emergencyDiveHoldSeconds;
        private static bool emergencyDiveActive;
        private static bool emergencyDiveHandsHigh;
        private static bool emergencyDiveHandsNearBody;
        private static bool emergencyDiveHandsSeparated;
        private static bool emergencyDivePalmsFacing;
        private static float emergencyDiveHandSeparation;
        private static float emergencyLeftHandRise;
        private static float emergencyRightHandRise;
        private static float emergencyDiveLeftBodyDistance;
        private static float emergencyDiveRightBodyDistance;
        private static float emergencyDiveLeftPalmFacing;
        private static float emergencyDiveRightPalmFacing;

        // Push-stop state and telemetry. The pose is evaluated in rig space relative to the HMD
        // facing direction so it follows the player's body orientation instead of room axes.
        private static bool inertiaBrakeCandidate;
        private static float inertiaBrakeHoldSeconds;
        private static bool inertiaBrakeActive;
        private static bool inertiaBrakeHandsForward;
        private static bool inertiaBrakeHandsNearBody;
        private static bool inertiaBrakeHandsSeparated;
        private static bool inertiaBrakeHandsHorizontal;
        private static bool inertiaBrakePalmsForward;
        private static float inertiaBrakeLeftForwardDistance;
        private static float inertiaBrakeRightForwardDistance;
        private static float inertiaBrakeLeftBodyDistance;
        private static float inertiaBrakeRightBodyDistance;
        private static float inertiaBrakeHandSeparation;
        private static float inertiaBrakeLeftVerticalOffset;
        private static float inertiaBrakeRightVerticalOffset;
        private static float inertiaBrakeLeftPalmForward;
        private static float inertiaBrakeRightPalmForward;
        private static float inertiaBrakeLinearFactor = 1f;
        private static float inertiaBrakeAngularFactor = 1f;
        private static Vector3 inertiaBrakeVelocityBefore;
        private static Vector3 inertiaBrakeVelocityAfter;

        // Fin-kick grip state and direct input contribution.
        private static bool finKickLeftGripHeld;
        private static bool finKickRightGripHeld;
        private static bool finKickActive;
        private static Vector3 finKickInput;

        private static float currentWaterDampingFactor = 1f;
        private static float handForceIdleSeconds = HandForceIdleGraceSeconds;

        public static float CombineFloatInput(GameInput.Button action, float vanillaValue)
        {
            UpdateIfNeeded();

            if (!active)
            {
                return vanillaValue;
            }

            if (controlMode == SwimmingControlMode.Seaglide)
            {
                return CombineSeaglideFloatInput(action, vanillaValue);
            }

            // Emergency vertical poses deliberately bypass the analog physical vertical model
            // and command the same full-speed native channels as the two VR triggers.
            if (emergencyAscentActive)
            {
                if (action == GameInput.Button.MoveUp)
                {
                    return 1f;
                }
                if (action == GameInput.Button.MoveDown)
                {
                    return 0f;
                }
            }
            if (emergencyDiveActive)
            {
                if (action == GameInput.Button.MoveDown)
                {
                    return 1f;
                }
                if (action == GameInput.Button.MoveUp)
                {
                    return 0f;
                }
            }

            // Both grip buttons are repurposed as Fin Kick while the chord is held. Suppress
            // their normal MoveUp/MoveDown bindings so the kick cannot ascend and descend at
            // the same time.
            if (finKickActive)
            {
                if (action == GameInput.Button.MoveUp)
                {
                    return Mathf.Max(0f, swimInput.y);
                }
                if (action == GameInput.Button.MoveDown)
                {
                    return Mathf.Max(0f, -swimInput.y);
                }
            }

            // Physical Swimming owns all locomotion controls while the player is in water.
            // At the surface bridge, upward movement is disabled and horizontal hand swimming
            // is only available inside the actual surface band. Native stick/trigger input is
            // never mixed back in; disabling Physical Swimming restores vanilla controls.
            if (surfaceDiveBridgeActive)
            {
                if (action == GameInput.Button.MoveUp)
                {
                    return 0f;
                }

                bool horizontalAction =
                    action == GameInput.Button.MoveForward
                    || action == GameInput.Button.MoveBackward
                    || action == GameInput.Button.MoveLeft
                    || action == GameInput.Button.MoveRight;

                if (horizontalAction && !surfaceSwimActive)
                {
                    return 0f;
                }

                if (!horizontalAction && action != GameInput.Button.MoveDown)
                {
                    return vanillaValue;
                }
            }

            float physicalValue;
            switch (action)
            {
                case GameInput.Button.MoveForward:
                    physicalValue = Mathf.Max(0f, swimInput.z);
                    break;
                case GameInput.Button.MoveBackward:
                    physicalValue = Mathf.Max(0f, -swimInput.z);
                    break;
                case GameInput.Button.MoveRight:
                    physicalValue = Mathf.Max(0f, swimInput.x);
                    break;
                case GameInput.Button.MoveLeft:
                    physicalValue = Mathf.Max(0f, -swimInput.x);
                    break;
                case GameInput.Button.MoveUp:
                    physicalValue = Mathf.Max(0f, swimInput.y);
                    break;
                case GameInput.Button.MoveDown:
                    physicalValue = Mathf.Max(0f, -swimInput.y);
                    break;
                default:
                    return vanillaValue;
            }

            // Once Physical Swimming is active, native locomotion is intentionally ignored.
            // Hands, Fin Kick and gesture-generated vertical channels are the only swim inputs.
            return physicalValue;
        }

        private static float CombineSeaglideFloatInput(GameInput.Button action, float vanillaValue)
        {
            switch (action)
            {
                case GameInput.Button.MoveForward:
                    return Mathf.Max(0f, swimInput.z);
                case GameInput.Button.MoveBackward:
                case GameInput.Button.MoveRight:
                case GameInput.Button.MoveLeft:
                case GameInput.Button.MoveUp:
                case GameInput.Button.MoveDown:
                    return 0f;
                default:
                    return vanillaValue;
            }
        }

        public static bool CombineButtonHeld(GameInput.Button action, bool vanillaHeld)
        {
            UpdateIfNeeded();

            if (!active || (action != GameInput.Button.MoveUp && action != GameInput.Button.MoveDown))
            {
                return vanillaHeld;
            }

            if (controlMode == SwimmingControlMode.Seaglide)
            {
                return false;
            }

            if (emergencyAscentActive)
            {
                return action == GameInput.Button.MoveUp;
            }
            if (emergencyDiveActive)
            {
                return action == GameInput.Button.MoveDown;
            }

            if (finKickActive)
            {
                // The raw grip actions are suppressed, but the combined hand + fin vertical
                // component still uses the same pulse-density channel as ordinary physical
                // swimming. Looking down while kicking therefore dives instead of cancelling out.
                return action == GameInput.Button.MoveUp
                    ? verticalPwmUp
                    : verticalPwmDown;
            }

            if (surfaceDiveBridgeActive)
            {
                // On the surface only physical MoveDown may pass. Native trigger/grip movement
                // is intentionally disabled while Physical Swimming owns locomotion.
                return action == GameInput.Button.MoveDown && verticalPwmDown;
            }

            // Feed physical vertical swimming through the same boolean channels used by the game,
            // but never mix in native MoveUp/MoveDown while Physical Swimming is active.
            return action == GameInput.Button.MoveUp
                ? verticalPwmUp
                : verticalPwmDown;
        }

        public static bool ShouldSuppressNativeSwimmingAction(GameInput.Button action)
        {
            UpdateIfNeeded();

            if (!active)
            {
                return false;
            }

            // Native ascent/descent buttons are never part of Physical Swimming. Emergency
            // gestures and hand-generated vertical PWM are reintroduced by CombineButtonHeld.
            if (action == GameInput.Button.MoveUp || action == GameInput.Button.MoveDown)
            {
                return true;
            }

            // The grip controls may also map to Sprint in the stock bindings. The input belongs to the
            // active locomotion mode: Fin Kick for hand swimming, Seaglide throttle for Seaglide.
            bool gripChordClaimed = controlMode == SwimmingControlMode.Seaglide
                ? SeaglideSwimmingController.Throttle > 0f
                : finKickActive;
            return gripChordClaimed && action.ToString() == "Sprint";
        }

        public static float GetCurrentCombinedInputLimit()
        {
            UpdateIfNeeded();

            if (!active || controlMode == SwimmingControlMode.Seaglide || !finKickActive)
            {
                return 1f;
            }

            return 1f + Mathf.Clamp01(FinKickStrength);
        }

        public static Vector2 CombineMoveAxis(Vector2 vanillaAxis)
        {
            UpdateIfNeeded();

            if (!active)
            {
                return vanillaAxis;
            }

            if (controlMode == SwimmingControlMode.Seaglide)
            {
                return new Vector2(0f, Mathf.Clamp01(swimInput.z));
            }

            // Physical Swimming fully owns the movement stick while swimming. Outside the narrow
            // surface-swim band, the bridge is dive-only and horizontal input is deliberately zero.
            if (surfaceDiveBridgeActive && !surfaceSwimActive)
            {
                return Vector2.zero;
            }

            // Horizontal movement comes only from the physical model (hands + Fin Kick).
            // Hand swimming itself remains capped at the ordinary 1.0 input, but Fin Kick is a
            // separate propulsion layer and is allowed to add on top of it. A Fin Kick strength
            // of 0.25 therefore permits a combined magnitude up to 1.25; strength 1.0 permits 2.0.
            // Do not clamp this back to the stock stick magnitude here or the leg contribution
            // would merely replace hand speed instead of adding to it.
            return Vector2.ClampMagnitude(
                new Vector2(swimInput.x, swimInput.z),
                GetCurrentCombinedInputLimit()
            );
        }

        private static void UpdateIfNeeded()
        {
            int frame = Time.frameCount;
            if (lastUpdatedFrame == frame)
            {
                return;
            }

            previousUpdatedFrame = lastUpdatedFrame;
            lastUpdatedFrame = frame;

            PhysicalSwimmingDebugRecorder.SyncEnabled();
            PhysicalSwimmingDiagnosticRecorder.SyncEnabled();
            RightWristDebugTimer.Update(
                PhysicalSwimmingDebugRecorder.IsRecording,
                PhysicalSwimmingDebugRecorder.ElapsedSeconds
            );

            var rig = VRCameraRig.instance;
            var player = Player.main;
            if (!Settings.PhysicalSwimming
                || rig == null
                || rig.leftController == null
                || rig.rightController == null
                || player == null
                || player.currentMountedVehicle != null)
            {
                PhysicalSwimmingStrokeHud.Hide();
                Reset();
                return;
            }

            bool playerIsSwimming = player.IsSwimming();
            UpdateSurfaceDiveBridgeState(rig, player, playerIsSwimming);
            PhysicalSwimmingDiagnosticRecorder.RecordFrame(rig, player, playerIsSwimming);

            // Away from water, physical swimming remains completely inactive. The only exception
            // is Surface Dive Bridge, which is armed by an actual Swimming -> Surface transition.
            if (!playerIsSwimming && !surfaceDiveBridgeActive)
            {
                PhysicalSwimmingStrokeHud.Hide();
                ResetMotionState();
                active = false;
                return;
            }

            // Do not turn hand motions made while using the PDA into locomotion. Preserve the
            // surface-bridge state itself so closing the PDA at the surface still allows diving.
            if (player.pda != null && player.pda.isOpen)
            {
                PhysicalSwimmingStrokeHud.Hide();
                ResetMotionState();
                active = false;
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0.0001f || (previousUpdatedFrame >= 0 && frame - previousUpdatedFrame > 2))
            {
                PhysicalSwimmingStrokeHud.Hide();
                ResetTracking(rig.leftController.transform.localPosition, rig.rightController.transform.localPosition, player.transform.position);
                return;
            }

            active = true;

            Vector3 actualPlayerWorldVelocity = UpdateActualPlayerVelocity(player.transform.position, dt);

            // Raw controller positions are used instead of the rendered hand targets. Tool/hand
            // offsets therefore cannot create fake strokes simply by rotating the controller.
            Vector3 leftPosition = rig.leftController.transform.localPosition;
            Vector3 rightPosition = rig.rightController.transform.localPosition;

            SwimmingControlMode resolvedMode =
                SwimmingControlModeResolver.Resolve(out Seaglide activeSeaglide);
            bool controlModeChanged = resolvedMode != controlMode;
            controlMode = resolvedMode;

            if (controlMode == SwimmingControlMode.Seaglide)
            {
                UpdateSeaglideSwimmingMode(
                    rig,
                    player,
                    activeSeaglide,
                    playerIsSwimming,
                    leftPosition,
                    rightPosition,
                    actualPlayerWorldVelocity,
                    dt
                );
                return;
            }

            if (controlModeChanged)
            {
                SeaglideSwimmingController.Reset();
                ResetHandTrackingForModeTransition(leftPosition, rightPosition);
            }

            bool leftCanStroke = true;
            bool rightCanStroke = Inventory.main == null || Inventory.main.GetHeld() == null;

            Vector3 leftPalmNormal = GetPalmNormalInRigSpace(rig, true, rig.leftController.transform);
            Vector3 rightPalmNormal = GetPalmNormalInRigSpace(rig, false, rig.rightController.transform);

            SteamVrGameInput.GetFinKickGripState(out finKickLeftGripHeld, out finKickRightGripHeld);

            UpdateEmergencyAscentGesture(
                rig,
                playerIsSwimming,
                leftPosition,
                rightPosition,
                leftPalmNormal,
                rightPalmNormal,
                dt
            );
            UpdateEmergencyDiveGesture(
                rig,
                playerIsSwimming || surfaceDiveBridgeActive,
                leftPosition,
                rightPosition,
                leftPalmNormal,
                rightPalmNormal,
                dt
            );
            UpdateInertiaBrakeGesture(
                rig,
                playerIsSwimming || surfaceSwimActive,
                leftPosition,
                rightPosition,
                leftPalmNormal,
                rightPalmNormal,
                dt
            );

            UpdateFinKick(
                rig,
                playerIsSwimming || surfaceSwimActive,
                emergencyAscentActive || emergencyDiveActive || inertiaBrakeActive
            );

            Vector3 leftForce = UpdateHand(
                ref leftHand,
                leftPosition,
                leftPalmNormal,
                leftCanStroke,
                dt,
                out HandTelemetry leftTelemetry
            );
            Vector3 rightForce = UpdateHand(
                ref rightHand,
                rightPosition,
                rightPalmNormal,
                rightCanStroke,
                dt,
                out HandTelemetry rightTelemetry
            );

            // Candidate detection only starts the hold timer. Propulsion is suppressed only after
            // the deliberate Push Stop pose has been held long enough to become active, so merely
            // passing through a similar hand position during a swimming stroke cannot steal force.
            if (inertiaBrakeActive)
            {
                leftForce = Vector3.zero;
                rightForce = Vector3.zero;
                leftTelemetry.force = Vector3.zero;
                rightTelemetry.force = Vector3.zero;
            }

            // The tangential part of a broadside hand sweep is the one physical quantity that can
            // either move the swimmer sideways or rotate them around the body axis. Only the NET
            // uncancelled yaw contribution is converted, so normal two-hand swimming strokes do
            // not lose propulsion merely because each hand has an opposite tangential component.
            Vector3 bodyCenterRig = GetBodyCenterInRigSpace(rig);
            ResolveTurningForces(
                leftPosition,
                rightPosition,
                bodyCenterRig,
                leftForce,
                rightForce,
                TurnLinearConversion,
                out Vector3 leftLinearForce,
                out Vector3 rightLinearForce,
                out currentYawTorque
            );

            Vector3 rigSpaceForce = Vector3.ClampMagnitude(leftLinearForce + rightLinearForce, 1f);
            Vector3 worldForce = Vector3.zero;
            BodyForceTelemetry bodyForceTelemetry = new BodyForceTelemetry
            {
                velocityBefore = worldBodyVelocity,
                velocityAfter = worldBodyVelocity,
                directionDot = 1f,
                actualPlayerDirectionDot = 1f,
                actualPlayerVelocity = actualPlayerWorldVelocity,
                rawActualPlayerVelocity = rawActualPlayerVelocity,
                velocityAfterCollisionRelease = worldBodyVelocity
            };

            bool hasActiveHandForce =
                rigSpaceForce.sqrMagnitude > HandForceEpsilon * HandForceEpsilon;
            if (hasActiveHandForce)
            {
                handForceIdleSeconds = 0f;
            }
            else
            {
                handForceIdleSeconds += dt;
            }

            bool handForceRecentlyActive =
                hasActiveHandForce || handForceIdleSeconds < HandForceIdleGraceSeconds;

            // The dead-zone is only for residual drift after the swimmer really stops producing
            // force. Do not erase a small velocity during a weak stroke, or during a very short
            // edge-on gap inside that stroke: several frames must be allowed to accumulate from
            // complete rest.
            if (!handForceRecentlyActive
                && !inertiaBrakeActive
                && worldBodyVelocity.sqrMagnitude < BodyVelocityDeadZone * BodyVelocityDeadZone)
            {
                worldBodyVelocity = Vector3.zero;
                bodyForceTelemetry.bodyVelocityDeadZoneApplied = true;
            }

            if (hasActiveHandForce)
            {
                // Hand tracking is measured in rig space, while body momentum is stored in world
                // space. This preserves the actual 3D direction of the stroke independently of
                // where the player happens to look afterwards.
                worldForce = rig.transform.TransformDirection(rigSpaceForce);

                if (surfaceDiveBridgeActive)
                {
                    if (surfaceSwimActive)
                    {
                        // At the actual water line horizontal swimming remains physical. Upward
                        // hand thrust is discarded so surface strokes cannot lift the player into
                        // the air, while downward thrust still feeds the native dive PWM.
                        worldForce.y = Mathf.Min(0f, worldForce.y);
                    }
                    else
                    {
                        // Outside the surface band retain the conservative dive-only bridge.
                        float downwardForce = Mathf.Max(0f, Vector3.Dot(worldForce, Vector3.down));
                        worldForce = Vector3.down * downwardForce;
                    }
                }

                ApplyForceToBodyVelocity(
                    ref worldBodyVelocity,
                    worldForce,
                    surfaceDiveBridgeActive ? Vector3.zero : actualPlayerWorldVelocity,
                    dt,
                    ref bodyForceTelemetry
                );
            }

            // Turning receives angular impulse from the same broadside stroke component that was
            // removed from linear strafe above. Surface Dive Bridge without surface swimming
            // remains one-dimensional and never accumulates hidden yaw.
            if (!surfaceDiveBridgeActive || surfaceSwimActive)
            {
                ApplyTurningImpulse(currentYawTorque, dt);
            }
            else
            {
                yawAngularVelocity = 0f;
                currentYawTorque = 0f;
            }

            // One water-resistance factor damps both stored linear and angular velocity. There is
            // no separate yaw drag or yaw glide timer: after the stroke, both kinds of momentum
            // decay according to exactly the same environmental setting.
            ApplyWaterResistance(ref worldBodyVelocity, ref yawAngularVelocity, dt);
            IntegratePhysicalYaw(currentYawTorque, dt);

            if (surfaceDiveBridgeActive)
            {
                if (surfaceSwimActive)
                {
                    // Horizontal glide is allowed at the water line, but the surface state may
                    // never accumulate upward virtual velocity.
                    worldBodyVelocity.y = Mathf.Min(0f, worldBodyVelocity.y);
                }
                else
                {
                    // Dive-only fallback remains one-dimensional.
                    worldBodyVelocity = new Vector3(0f, Mathf.Min(0f, worldBodyVelocity.y), 0f);
                }
            }

            Vector3 bodyVelocityAfterDrag = worldBodyVelocity;

            // A deliberate Push Stop does not stop the swimmer instantly. Instead it applies
            // strong temporary damping to every stored linear component and to yaw inertia.
            ApplyInertiaBrake(ref worldBodyVelocity, dt);

            // Subnautica's real Player is the collision authority. If real displacement drops
            // from genuine motion to almost zero while virtual momentum is still present, some
            // external game physics stopped us. Forget ALL stored swimmer inertia immediately.
            ReconcileCollisionMomentum(
                ref worldBodyVelocity,
                rawActualPlayerVelocity,
                dt,
                ref bodyForceTelemetry
            );

            // Once the hands have really been idle for a moment, snap the very last tiny drift
            // to zero. During and immediately around an active stroke this is deliberately skipped
            // so low-speed propulsion remains continuous and controllable.
            if (!handForceRecentlyActive
                && !inertiaBrakeActive
                && worldBodyVelocity.sqrMagnitude < BodyVelocityDeadZone * BodyVelocityDeadZone)
            {
                worldBodyVelocity = Vector3.zero;
                bodyForceTelemetry.bodyVelocityDeadZoneApplied = true;
            }

            Vector3 physicalSwimInput = ConvertWorldVelocityToGameInput(rig, worldBodyVelocity);

            // Hands and Fin Kick are intentionally additive. The hand model still tops out at
            // ordinary full input (1.0), while the kick contributes an independent 0..1 layer.
            // This lets sustained kicking make a full-power hand stroke genuinely faster instead
            // of disappearing into the old ClampMagnitude(..., 1f).
            swimInput = Vector3.ClampMagnitude(
                physicalSwimInput + finKickInput,
                GetCurrentCombinedInputLimit()
            );

            // The tuning HUD deliberately reports both the raw hand speed and the resulting
            // combined pull. They are not the same thing: palm orientation, back-of-hand drag
            // and opposite hand forces can all reduce useful propulsion.
            float currentHandSpeed = Mathf.Max(leftTelemetry.speed, rightTelemetry.speed);
            float currentPullStrength = Mathf.Clamp01(rigSpaceForce.magnitude);
            PhysicalSwimmingStrokeHud.Update(
                Settings.PhysicalSwimmingStrokeHud,
                swimInput.magnitude,
                GetCurrentCombinedInputLimit(),
                currentHandSpeed,
                FullStrokeSpeed,
                currentPullStrength
            );

            UpdateVerticalPwm(swimInput.y);
            if (emergencyAscentActive || emergencyDiveActive)
            {
                // Do not let residual virtual vertical motion fight the full-speed native
                // emergency channel while either static vertical pose is active.
                verticalPwmUp = false;
                verticalPwmDown = false;
            }

            PhysicalSwimmingDebugRecorder.RecordFrame(
                rig,
                player,
                dt,
                leftTelemetry,
                rightTelemetry,
                rigSpaceForce,
                worldForce,
                bodyForceTelemetry,
                bodyVelocityAfterDrag,
                swimInput
            );
        }

        private static void UpdateSeaglideSwimmingMode(
            VRCameraRig rig,
            Player player,
            Seaglide seaglide,
            bool playerIsSwimming,
            Vector3 leftPosition,
            Vector3 rightPosition,
            Vector3 actualPlayerWorldVelocity,
            float dt)
        {
            PhysicalSwimmingStrokeHud.Hide();
            DisableHandSwimmingControls(leftPosition, rightPosition);

            Vector3 velocityBefore = worldBodyVelocity;

            // Surface Dive Bridge is still considered water availability here, but its hand
            // gestures are not. Pointing the Seaglide is the only locomotion intent in this mode.
            SeaglideSwimmingController.Update(
                seaglide,
                playerIsSwimming,
                surfaceDiveBridgeActive,
                worldBodyVelocity
            );

            BodyForceTelemetry bodyForceTelemetry = new BodyForceTelemetry
            {
                velocityBefore = velocityBefore,
                velocityAfter = velocityBefore,
                directionDot = 1f,
                actualPlayerDirectionDot = 1f,
                actualPlayerVelocity = actualPlayerWorldVelocity,
                rawActualPlayerVelocity = rawActualPlayerVelocity,
                velocityAfterCollisionRelease = velocityBefore
            };

            if (SeaglideSwimmingController.PropulsionActive)
            {
                // Preserve the analog grip throttle as the magnitude of the shared movement
                // vector. Vanilla Seaglide locomotion still owns the actual speed multiplier,
                // battery, sounds and effects; we only provide a 0..1 forward input magnitude.
                worldBodyVelocity = SeaglideSwimmingController.ToolDirection
                    * SeaglideSwimmingController.Throttle;
                ApplyWaterResistance(
                    ref worldBodyVelocity,
                    ref yawAngularVelocity,
                    dt,
                    dampLinearVelocity: false
                );
            }
            else
            {
                // Once the grips are released no new propulsion is generated. The same stored
                // body velocity used by hand swimming simply decays through Water Resistance.
                ApplyWaterResistance(ref worldBodyVelocity, ref yawAngularVelocity, dt);

                if (worldBodyVelocity.sqrMagnitude < BodyVelocityDeadZone * BodyVelocityDeadZone)
                {
                    worldBodyVelocity = Vector3.zero;
                    bodyForceTelemetry.bodyVelocityDeadZoneApplied = true;
                }
            }

            currentYawTorque = 0f;
            IntegratePhysicalYaw(0f, dt);

            bodyForceTelemetry.velocityAfter = worldBodyVelocity;
            bodyForceTelemetry.deltaVelocity = worldBodyVelocity - velocityBefore;
            bodyForceTelemetry.velocityAfterRedirection = worldBodyVelocity;

            Vector3 bodyVelocityAfterDrag = worldBodyVelocity;

            // Reuse the same collision authority as hand swimming. A wall is still a wall even
            // when the source of movement is a motor instead of a stroke.
            ReconcileCollisionMomentum(
                ref worldBodyVelocity,
                rawActualPlayerVelocity,
                dt,
                ref bodyForceTelemetry
            );

            // The dedicated movement reference points along the active Seaglide while driving and
            // freezes along the stored glide direction when the grips are released. Therefore the
            // mode only needs one positive forward axis; head direction and native stick input are
            // never mixed into it.
            swimInput = new Vector3(
                0f,
                0f,
                Mathf.Clamp01(worldBodyVelocity.magnitude)
            );

            UpdateVerticalPwm(0f);

            HandTelemetry leftTelemetry = CreateDisabledHandTelemetry(leftPosition);
            HandTelemetry rightTelemetry = CreateDisabledHandTelemetry(rightPosition);

            PhysicalSwimmingDebugRecorder.RecordFrame(
                rig,
                player,
                dt,
                leftTelemetry,
                rightTelemetry,
                Vector3.zero,
                Vector3.zero,
                bodyForceTelemetry,
                bodyVelocityAfterDrag,
                swimInput
            );
        }

        private static HandTelemetry CreateDisabledHandTelemetry(Vector3 position)
        {
            return new HandTelemetry
            {
                position = position,
                canStroke = false
            };
        }

        private static void ResetHandTrackingForModeTransition(
            Vector3 leftPosition,
            Vector3 rightPosition)
        {
            leftHand = new HandStrokeState
            {
                initialized = true,
                previousPosition = leftPosition,
                filteredVelocity = Vector3.zero
            };
            rightHand = new HandStrokeState
            {
                initialized = true,
                previousPosition = rightPosition,
                filteredVelocity = Vector3.zero
            };
            handForceIdleSeconds = HandForceIdleGraceSeconds;
        }

        private static void DisableHandSwimmingControls(
            Vector3 leftPosition,
            Vector3 rightPosition)
        {
            ResetHandTrackingForModeTransition(leftPosition, rightPosition);

            verticalPwmAccumulator = 0f;
            verticalPwmDirection = 0;
            verticalPwmUp = false;
            verticalPwmDown = false;

            emergencyAscentCandidate = false;
            emergencyAscentHoldSeconds = 0f;
            emergencyAscentActive = false;
            emergencyAscentHandsLow = false;
            emergencyAscentHandsNearBody = false;
            emergencyAscentHandsSeparated = false;
            emergencyAscentPalmsInward = false;
            emergencyAscentHandSeparation = 0f;
            emergencyLeftHandDrop = 0f;
            emergencyRightHandDrop = 0f;
            emergencyLeftBodyDistance = 0f;
            emergencyRightBodyDistance = 0f;
            emergencyLeftPalmInward = -1f;
            emergencyRightPalmInward = -1f;

            emergencyDiveCandidate = false;
            emergencyDiveHoldSeconds = 0f;
            emergencyDiveActive = false;
            emergencyDiveHandsHigh = false;
            emergencyDiveHandsNearBody = false;
            emergencyDiveHandsSeparated = false;
            emergencyDivePalmsFacing = false;
            emergencyDiveHandSeparation = 0f;
            emergencyLeftHandRise = 0f;
            emergencyRightHandRise = 0f;
            emergencyDiveLeftBodyDistance = 0f;
            emergencyDiveRightBodyDistance = 0f;
            emergencyDiveLeftPalmFacing = -1f;
            emergencyDiveRightPalmFacing = -1f;

            ResetInertiaBrakeState();

            finKickLeftGripHeld = false;
            finKickRightGripHeld = false;
            finKickActive = false;
            finKickInput = Vector3.zero;
            currentYawTorque = 0f;
        }

        private static void UpdateFinKick(
            VRCameraRig rig,
            bool swimmingAvailable,
            bool conflictingGestureActive)
        {
            finKickActive =
                FinKickEnabled
                && swimmingAvailable
                && !conflictingGestureActive
                && finKickLeftGripHeld
                && finKickRightGripHeld;

            finKickInput = Vector3.zero;
            if (!finKickActive || MainCamera.camera == null)
            {
                return;
            }

            Vector3 lookDirection = MainCamera.camera.transform.forward;
            if (lookDirection.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            // Convert the desired world-space HMD direction into the same game-input basis used
            // by physical swimming. This keeps the kick head-directed even when SubmersedVR's
            // normal movement mode is hand-based. Vertical look is carried through the native
            // MoveUp/MoveDown PWM channels.
            finKickInput =
                ConvertWorldVelocityToGameInput(rig, lookDirection.normalized)
                * Mathf.Clamp01(FinKickStrength);
        }

        private static Vector3 UpdateHand(
            ref HandStrokeState state,
            Vector3 currentPosition,
            Vector3 palmNormal,
            bool canStroke,
            float dt,
            out HandTelemetry telemetry)
        {
            telemetry = new HandTelemetry
            {
                position = currentPosition,
                palmNormal = palmNormal,
                canStroke = canStroke
            };
            if (!state.initialized)
            {
                state.initialized = true;
                state.previousPosition = currentPosition;
                state.filteredVelocity = Vector3.zero;
                telemetry.filteredVelocity = Vector3.zero;
                return Vector3.zero;
            }

            Vector3 delta = currentPosition - state.previousPosition;
            state.previousPosition = currentPosition;

            if (delta.magnitude > MaxPositionJump)
            {
                state.filteredVelocity = Vector3.zero;
                telemetry.trackingJumpRejected = true;
                telemetry.filteredVelocity = Vector3.zero;
                return Vector3.zero;
            }

            Vector3 rawVelocity = delta / dt;
            telemetry.rawVelocity = rawVelocity;

            Vector3 velocity = Vector3.ClampMagnitude(rawVelocity, MaxTrackedHandSpeed);

            float smoothing = 1f - Mathf.Exp(-VelocitySmoothing * dt);
            state.filteredVelocity = Vector3.Lerp(state.filteredVelocity, velocity, smoothing);
            telemetry.filteredVelocity = state.filteredVelocity;

            if (!canStroke)
            {
                // Continue sampling the occupied hand so releasing a tool cannot create a large
                // synthetic delta on the next frame.
                state.filteredVelocity = Vector3.zero;
                telemetry.filteredVelocity = Vector3.zero;
                return Vector3.zero;
            }

            float speed = state.filteredVelocity.magnitude;
            telemetry.speed = speed;
            if (speed < MinStrokeSpeed)
            {
                return Vector3.zero;
            }

            // Magnitude remains smoothed to keep stroke strength stable, but direction must come
            // from the current clamped hand velocity. Using the lagging filtered vector here makes
            // fast curved strokes compare today's palm orientation with yesterday's motion vector,
            // which can collapse projected palm area exactly when the player strokes hardest.
            float currentVelocityMagnitude = velocity.magnitude;
            Vector3 velocityDirection = currentVelocityMagnitude > 0.0001f
                ? velocity / currentVelocityMagnitude
                : state.filteredVelocity / speed;

            // Water pushes the swimmer opposite to the direction in which the hand pushes water.
            Vector3 propulsionDirection = -velocityDirection;

            // Projected hand area is still driven by how broadside the hand moves through the
            // water. The sign selects which anatomical side leads: positive is the inner palm,
            // negative is the back of the hand. A single back-side multiplier makes recovery
            // strokes weaker without inventing any separate intent or low-speed assist layer.
            float signedPalmAlignment = Vector3.Dot(palmNormal.normalized, velocityDirection);
            float palmAlignment = Mathf.Abs(signedPalmAlignment);
            float effectivePalmAlignment = Mathf.InverseLerp(PalmDragDeadZone, 1f, palmAlignment);
            float projectedPalmArea = Mathf.Pow(effectivePalmAlignment, PalmDragExponent);
            float baseDragFactor = Mathf.Lerp(PalmEdgeDrag, 1f, projectedPalmArea);

            float backOfHandExposure = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(-signedPalmAlignment));
            float backOfHandFactor = Mathf.Lerp(
                1f,
                Mathf.Clamp01(BackOfHandDragMultiplier),
                backOfHandExposure
            );
            float dragFactor = baseDragFactor * backOfHandFactor;

            telemetry.palmAlignment = palmAlignment;
            telemetry.signedPalmAlignment = signedPalmAlignment;
            telemetry.backOfHandFactor = backOfHandFactor;
            telemetry.effectivePalmAlignment = effectivePalmAlignment;
            telemetry.baseDragFactor = baseDragFactor;
            telemetry.dragFactor = dragFactor;

            // Hydrodynamic stroke response remains the same v^2 curve for both anatomical sides.
            // Only projected resistance differs, so tiny precise strokes and full-power strokes
            // stay on the same continuous physical model.
            float minSpeedSquared = MinStrokeSpeed * MinStrokeSpeed;
            float fullSpeedSquared = FullStrokeSpeed * FullStrokeSpeed;
            float speedSquared = speed * speed;
            float baseSpeedStrength = Mathf.InverseLerp(minSpeedSquared, fullSpeedSquared, speedSquared);
            float speedStrength = baseSpeedStrength;

            telemetry.baseSpeedStrength = baseSpeedStrength;
            telemetry.speedStrength = speedStrength;

            float strength = speedStrength * dragFactor;
            Vector3 force = propulsionDirection * strength * MaxSingleHandContribution;
            telemetry.force = force;
            return force;
        }

        private static void ApplyForceToBodyVelocity(
            ref Vector3 velocity,
            Vector3 force,
            Vector3 actualPlayerWorldVelocity,
            float dt,
            ref BodyForceTelemetry telemetry)
        {
            telemetry.velocityBefore = velocity;
            telemetry.velocityAfter = velocity;
            telemetry.forceStrength = force.magnitude;
            telemetry.directionDot = 1f;
            telemetry.actualPlayerDirectionDot = 1f;
            telemetry.actualPlayerVelocity = actualPlayerWorldVelocity;
            telemetry.maneuverabilityReverseFactor = 1f;
            telemetry.maneuverabilityNaturalTurnDegrees = 0f;
            telemetry.maneuverabilityTurnRequestedDegrees = 0f;
            telemetry.maneuverabilityTurnAppliedDegrees = 0f;

            float forceStrength = telemetry.forceStrength;
            if (forceStrength <= HandForceEpsilon)
            {
                return;
            }

            Vector3 forceDirection = force / forceStrength;
            Vector3 deltaVelocity = force * HandForceCoefficient * dt;
            telemetry.deltaVelocity = deltaVelocity;

            float currentSpeed = velocity.magnitude;
            if (currentSpeed < BodyVelocityEpsilon)
            {
                velocity = Vector3.zero;
                currentSpeed = 0f;
            }

            float actualPlayerSpeed = actualPlayerWorldVelocity.magnitude;
            Vector3 actualPlayerDirection = Vector3.zero;
            bool actualPlayerStillMoving = actualPlayerSpeed >= PlayerVelocityDeadZone;
            if (actualPlayerStillMoving)
            {
                actualPlayerDirection = actualPlayerWorldVelocity / actualPlayerSpeed;
                telemetry.actualPlayerDirectionDot = Vector3.Dot(actualPlayerDirection, forceDirection);
            }

            bool forceOpposesActualPlayer = actualPlayerStillMoving && telemetry.actualPlayerDirectionDot < 0f;
            telemetry.actualPlayerOpposingForce = forceOpposesActualPlayer
                ? Mathf.Max(0f, Vector3.Dot(force, -actualPlayerDirection))
                : 0f;

            float reverseForceThreshold = ManeuverabilityReverseForceThreshold;
            bool deliberateReverse = telemetry.actualPlayerOpposingForce >= reverseForceThreshold;
            telemetry.intentionalReverseAllowed = deliberateReverse;

            // From virtual rest we may still be physically gliding in vanilla Subnautica.
            // Moderate force opposite that real glide may not create reverse thrust yet, but
            // every perpendicular component remains available immediately for course changes.
            if (currentSpeed <= 0f)
            {
                Vector3 restCandidateVelocity = deltaVelocity;
                if (forceOpposesActualPlayer && !deliberateReverse)
                {
                    float candidateAlongActual = Vector3.Dot(restCandidateVelocity, actualPlayerDirection);
                    if (candidateAlongActual < 0f)
                    {
                        telemetry.actualPlayerReverseGuard = true;
                        restCandidateVelocity -= actualPlayerDirection * candidateAlongActual;
                    }
                }

                velocity = Vector3.ClampMagnitude(restCandidateVelocity, MaxBodyVelocity);
                telemetry.velocityAfter = velocity;
                telemetry.velocityAfterRedirection = velocity;
                return;
            }

            Vector3 travelDirection = velocity / currentSpeed;
            float directionDot = Vector3.Dot(travelDirection, forceDirection);
            telemetry.directionDot = directionDot;

            float alongTravelDelta = Vector3.Dot(deltaVelocity, travelDirection);
            Vector3 perpendicularDelta = deltaVelocity - travelDirection * alongTravelDelta;
            telemetry.perpendicularDeltaVelocity = perpendicularDelta;

            // Maneuverability changes how efficiently a counter-stroke sheds old momentum.
            // It only scales the component along the current travel direction. The perpendicular
            // impulse is never suppressed: it is part of the real stroke and must start changing
            // course immediately even while a long glide is still active.
            float opposingAlongForce = Mathf.Max(0f, Vector3.Dot(force, -travelDirection));
            float opposingResponse = 1f;
            if (alongTravelDelta < 0f && opposingAlongForce > 0f)
            {
                float brakeBlend = reverseForceThreshold > 0.0001f
                    ? Mathf.Clamp01(opposingAlongForce / reverseForceThreshold)
                    : 1f;
                opposingResponse = Mathf.Lerp(ManeuverabilityWeakBrakeFloor, 1f, brakeBlend);
                telemetry.weakOpposingBrake = opposingResponse < 0.999f;
            }
            telemetry.maneuverabilityReverseFactor = opposingResponse;

            float adjustedAlongTravelDelta = alongTravelDelta < 0f
                ? alongTravelDelta * opposingResponse
                : alongTravelDelta;

            // Baseline physics is always ordinary vector addition. This is the crucial rule:
            // a sideways / vertical stroke contributes its real impulse exactly once, regardless
            // of existing glide direction. Maneuverability is only an extra steering assist on
            // top of this physically valid candidate, never a replacement for the impulse.
            Vector3 adjustedDeltaVelocity =
                travelDirection * adjustedAlongTravelDelta
                + perpendicularDelta;
            Vector3 naturalCandidateVelocity = velocity + adjustedDeltaVelocity;

            // The real Subnautica player remains the authority for reverse protection. Clamp only
            // the forbidden component opposite its actual motion; keep all lateral / vertical
            // motion so changing course never waits for the old glide to die.
            if (forceOpposesActualPlayer && !deliberateReverse)
            {
                float candidateAlongActual = Vector3.Dot(naturalCandidateVelocity, actualPlayerDirection);
                if (candidateAlongActual < 0f)
                {
                    telemetry.actualPlayerReverseGuard = true;
                    naturalCandidateVelocity -= actualPlayerDirection * candidateAlongActual;
                }
            }

            Vector3 candidateVelocity = ApplyManeuverabilityAssist(
                travelDirection,
                naturalCandidateVelocity,
                forceDirection,
                dt,
                ref telemetry
            );

            velocity = Vector3.ClampMagnitude(candidateVelocity, MaxBodyVelocity);
            telemetry.velocityAfter = velocity;
        }

        private static Vector3 ApplyManeuverabilityAssist(
            Vector3 previousTravelDirection,
            Vector3 naturalCandidateVelocity,
            Vector3 strokeDirection,
            float dt,
            ref BodyForceTelemetry telemetry)
        {
            telemetry.velocityAfterRedirection = naturalCandidateVelocity;
            telemetry.momentumRedirectionAngleDegrees = 0f;
            telemetry.momentumRedirectionApplied = false;
            telemetry.maneuverabilityNaturalTurnDegrees = 0f;
            telemetry.maneuverabilityTurnRequestedDegrees = 0f;
            telemetry.maneuverabilityTurnAppliedDegrees = 0f;

            float resultingSpeed = naturalCandidateVelocity.magnitude;
            if (resultingSpeed <= BodyVelocityEpsilon || dt <= 0f)
            {
                return naturalCandidateVelocity;
            }

            Vector3 naturalDirection = naturalCandidateVelocity / resultingSpeed;
            float naturalTurnDegrees = Vector3.Angle(previousTravelDirection, naturalDirection);
            telemetry.maneuverabilityNaturalTurnDegrees = naturalTurnDegrees;

            float steeringAssist = ManeuverabilitySteeringAssistFactor;
            if (steeringAssist <= 0f || naturalTurnDegrees <= 0.0001f)
            {
                return naturalCandidateVelocity;
            }

            // Natural vector addition already performed the physical turn. Maneuverability only
            // rotates the result a little farther toward the measured stroke direction, without
            // changing its magnitude. The degrees-per-second cap applies to this EXTRA assist,
            // never to the natural turn itself.
            float requestedAssistDegrees = naturalTurnDegrees * steeringAssist;
            float maxAssistThisFrame = Mathf.Max(0f, ManeuverabilityMaxTurnRate) * dt;
            float remainingTowardStrokeDegrees = Vector3.Angle(naturalDirection, strokeDirection);
            float appliedAssistDegrees = Mathf.Min(
                requestedAssistDegrees,
                maxAssistThisFrame,
                remainingTowardStrokeDegrees
            );

            telemetry.maneuverabilityTurnRequestedDegrees = requestedAssistDegrees;
            telemetry.maneuverabilityTurnAppliedDegrees = appliedAssistDegrees;
            if (appliedAssistDegrees <= 0.0001f)
            {
                return naturalCandidateVelocity;
            }

            Vector3 assistedDirection = Vector3.RotateTowards(
                naturalDirection,
                strokeDirection,
                appliedAssistDegrees * Mathf.Deg2Rad,
                0f
            ).normalized;

            Vector3 result = assistedDirection * resultingSpeed;
            telemetry.velocityAfterRedirection = result;

            // Preserve the historical fields for telemetry compatibility. They now represent
            // only the extra steering assist, not the natural course change from vector addition.
            telemetry.momentumRedirectionAngleDegrees = appliedAssistDegrees;
            telemetry.momentumRedirectionApplied = true;
            return result;
        }

        private static Vector3 UpdateActualPlayerVelocity(Vector3 currentPosition, float dt)
        {
            if (!playerVelocityInitialized)
            {
                playerVelocityInitialized = true;
                previousPlayerPosition = currentPosition;
                rawActualPlayerVelocity = Vector3.zero;
                actualPlayerVelocity = Vector3.zero;
                return actualPlayerVelocity;
            }

            Vector3 delta = currentPosition - previousPlayerPosition;
            previousPlayerPosition = currentPosition;

            if (delta.magnitude > MaxPlayerPositionJump)
            {
                rawActualPlayerVelocity = Vector3.zero;
                actualPlayerVelocity = Vector3.zero;
                return actualPlayerVelocity;
            }

            rawActualPlayerVelocity = Vector3.ClampMagnitude(delta / dt, MaxTrackedPlayerSpeed);
            float smoothing = 1f - Mathf.Exp(-PlayerVelocitySmoothing * dt);
            actualPlayerVelocity = Vector3.Lerp(actualPlayerVelocity, rawActualPlayerVelocity, smoothing);
            return actualPlayerVelocity;
        }

        private static void ReconcileCollisionMomentum(
            ref Vector3 velocity,
            Vector3 rawPlayerVelocity,
            float dt,
            ref BodyForceTelemetry telemetry)
        {
            float realSpeed = rawPlayerVelocity.magnitude;

            telemetry.rawActualPlayerVelocity = rawPlayerVelocity;
            telemetry.collisionRecentPeakSpeed = collisionRecentPeakSpeed;
            telemetry.collisionCurrentRealSpeed = realSpeed;
            telemetry.collisionRecentMotionSeconds = collisionRecentMotionSeconds;
            telemetry.collisionStallSeconds = collisionStallSeconds;
            telemetry.collisionSpeedDrop = Mathf.Max(0f, collisionRecentPeakSpeed - realSpeed);
            telemetry.collisionHardStopCandidate = false;
            telemetry.collisionHardStopApplied = false;
            telemetry.velocityAfterCollisionRelease = velocity;

            // The brake intentionally causes real Player speed to collapse. Do not let the
            // collision hard-stop misinterpret that controlled deceleration as hitting a wall
            // and snap the remaining brake glide to zero. Disarm while braking instead.
            if (inertiaBrakeActive)
            {
                collisionRecentPeakSpeed = 0f;
                collisionRecentMotionSeconds = 0f;
                collisionStallSeconds = 0f;
                telemetry.collisionRecentPeakSpeed = 0f;
                telemetry.collisionRecentMotionSeconds = 0f;
                telemetry.collisionStallSeconds = 0f;
                telemetry.collisionSpeedDrop = 0f;
                return;
            }

            if (dt <= 0f)
            {
                return;
            }

            // Any confirmed real movement arms the detector for a short window. Keep the largest
            // recent real speed so a multi-frame impact such as 3 -> 1 -> 0 is still recognized.
            if (realSpeed >= CollisionHardStopArmSpeed)
            {
                collisionRecentPeakSpeed = Mathf.Max(collisionRecentPeakSpeed, realSpeed);
                collisionRecentMotionSeconds = CollisionRecentMotionWindow;
                collisionStallSeconds = 0f;
            }
            else
            {
                collisionRecentMotionSeconds = Mathf.Max(0f, collisionRecentMotionSeconds - dt);
                if (collisionRecentMotionSeconds <= 0f)
                {
                    collisionRecentPeakSpeed = 0f;
                }
            }

            bool virtualMomentumMeaningful = velocity.magnitude >= CollisionVirtualSpeedThreshold
                || Mathf.Abs(yawAngularVelocity) >= YawAngularVelocityStopThreshold;
            bool recentlyMoving = collisionRecentMotionSeconds > 0f
                && collisionRecentPeakSpeed >= CollisionHardStopArmSpeed;
            bool realPlayerStopped = realSpeed <= CollisionHardStopSpeed;
            bool hardStopCandidate = virtualMomentumMeaningful && recentlyMoving && realPlayerStopped;

            telemetry.collisionRecentPeakSpeed = collisionRecentPeakSpeed;
            telemetry.collisionCurrentRealSpeed = realSpeed;
            telemetry.collisionRecentMotionSeconds = collisionRecentMotionSeconds;
            telemetry.collisionSpeedDrop = Mathf.Max(0f, collisionRecentPeakSpeed - realSpeed);
            telemetry.collisionHardStopCandidate = hardStopCandidate;

            if (!hardStopCandidate)
            {
                collisionStallSeconds = 0f;
                telemetry.collisionStallSeconds = 0f;
                return;
            }

            collisionStallSeconds += dt;
            telemetry.collisionStallSeconds = collisionStallSeconds;

            if (collisionStallSeconds < CollisionHardStopConfirmTime)
            {
                return;
            }

            // The real game has already decided that the Player stopped. Retaining any old virtual
            // velocity here can only keep pressing or scraping the swimmer along the obstacle.
            velocity = Vector3.zero;
            yawAngularVelocity = 0f;
            currentYawTorque = 0f;

            telemetry.collisionHardStopApplied = true;
            telemetry.velocityAfterCollisionRelease = Vector3.zero;

            // Disarm immediately after the one-shot reset. A new stroke can therefore start from
            // rest and move away from the obstacle instead of being swallowed by a contact latch.
            collisionRecentPeakSpeed = 0f;
            collisionRecentMotionSeconds = 0f;
            collisionStallSeconds = 0f;
        }

        private static Vector3 GetBodyCenterInRigSpace(VRCameraRig rig)
        {
            // For yaw, only the horizontal lever arm matters. Project the HMD position onto the
            // rig's horizontal plane so room-scale head offsets do not falsely move the body's
            // center away from the swimmer.
            if (rig != null && rig.vrCamera != null)
            {
                Vector3 hmd = rig.vrCamera.transform.localPosition;
                return new Vector3(hmd.x, 0f, hmd.z);
            }

            return Vector3.zero;
        }

        private static void ResolveTurningForces(
            Vector3 leftPosition,
            Vector3 rightPosition,
            Vector3 bodyCenter,
            Vector3 leftForce,
            Vector3 rightForce,
            float conversion,
            out Vector3 leftLinearForce,
            out Vector3 rightLinearForce,
            out float yawTorque
        )
        {
            float leftYawTorque = Vector3.Cross(leftPosition - bodyCenter, leftForce).y;
            float rightYawTorque = Vector3.Cross(rightPosition - bodyCenter, rightForce).y;
            yawTorque = leftYawTorque + rightYawTorque;

            leftLinearForce = leftForce;
            rightLinearForce = rightForce;

            float clampedConversion = Mathf.Clamp01(conversion);
            if (clampedConversion <= 0f || Mathf.Abs(yawTorque) <= 0.0001f)
            {
                return;
            }

            // Two normal swimming strokes often generate opposite hand torques that cancel each
            // other. Never steal their linear propulsion just because each hand individually has
            // a tangential component. Only the uncancelled NET yaw contribution is eligible for
            // conversion from strafe into rotation.
            float yawSign = Mathf.Sign(yawTorque);
            float leftSameDirectionTorque = Mathf.Max(0f, leftYawTorque * yawSign);
            float rightSameDirectionTorque = Mathf.Max(0f, rightYawTorque * yawSign);
            float sameDirectionTorque = leftSameDirectionTorque + rightSameDirectionTorque;
            if (sameDirectionTorque <= 0.0001f)
            {
                return;
            }

            float netShare = Mathf.Clamp01(Mathf.Abs(yawTorque) / sameDirectionTorque);
            float handConversion = clampedConversion * netShare;

            if (leftSameDirectionTorque > 0f)
            {
                leftLinearForce = RemoveTangentialForce(
                    leftPosition,
                    bodyCenter,
                    leftForce,
                    handConversion
                );
            }

            if (rightSameDirectionTorque > 0f)
            {
                rightLinearForce = RemoveTangentialForce(
                    rightPosition,
                    bodyCenter,
                    rightForce,
                    handConversion
                );
            }
        }

        private static Vector3 RemoveTangentialForce(
            Vector3 handPosition,
            Vector3 bodyCenter,
            Vector3 handForce,
            float conversion
        )
        {
            Vector3 lever = handPosition - bodyCenter;
            lever.y = 0f;
            if (lever.sqrMagnitude <= BodyVelocityEpsilon * BodyVelocityEpsilon)
            {
                return handForce;
            }

            // Tangent to the horizontal circle around the swimmer. This is exactly the horizontal
            // force component that contributes to r x F around Y. Vertical and radial propulsion
            // are untouched.
            Vector3 tangent = Vector3.Cross(lever.normalized, Vector3.up);
            Vector3 tangentialForce = tangent * Vector3.Dot(handForce, tangent);
            return handForce - tangentialForce * Mathf.Clamp01(conversion);
        }

        private static void ApplyTurningImpulse(float yawTorque, float dt)
        {
            if (dt <= 0f || TurningStrength <= 0f || Mathf.Abs(yawTorque) <= 0.0001f)
            {
                return;
            }

            // TurningStrength is the sole user-facing yaw control. 1.0 is calibrated from the
            // user's tuned 250 torque / 67 deg/s / 0.065 drag profile after moving its tail to
            // unified Water Resistance. Around 4.0 is intentionally extreme and can approach a
            // full turn from one strong broadside sweep at Water Resistance 1.0.
            yawAngularVelocity +=
                yawTorque * TurnAccelerationAtStrengthOne * TurningStrength * dt;
        }

        private static void IntegratePhysicalYaw(float yawTorque, float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            // Exponential water damping never mathematically reaches zero, so retain one small
            // non-user-facing stop threshold to eliminate an imperceptible infinite tail.
            if (Mathf.Abs(yawTorque) <= 0.0001f
                && Mathf.Abs(yawAngularVelocity) < YawAngularVelocityStopThreshold)
            {
                yawAngularVelocity = 0f;
            }

            physicalYawOffsetDegrees = Mathf.Repeat(
                physicalYawOffsetDegrees + yawAngularVelocity * dt + 180f,
                360f
            ) - 180f;
        }

        private static float GetWaterLevel()
        {
            // Vanilla Subnautica's global ocean surface is Y=0.
            // Do not reference WaterMove directly here: that type is not present in every
            // Subnautica build/reference set and would make the whole mod fail to compile.
            return 0f;
        }

        private static void ApplyWaterResistance(
            ref Vector3 velocity,
            ref float angularVelocity,
            float dt,
            bool dampLinearVelocity = true
        )
        {
            if (dt <= 0f)
            {
                currentWaterDampingFactor = 1f;
                return;
            }

            // Analytic integration of dv/dt = -S * v. The exact same scalar factor is shared by
            // translational and rotational inertia. A driven propulsion mode may opt out of
            // damping the linear command for the current frame, but the water model itself stays
            // singular and angular inertia is still damped by the same factor.
            float resistance = Mathf.Max(0f, WaterResistance);
            currentWaterDampingFactor = Mathf.Exp(-resistance * dt);

            bool hasLinearMomentum =
                velocity.sqrMagnitude > BodyVelocityEpsilon * BodyVelocityEpsilon;
            bool hasAngularMomentum =
                Mathf.Abs(angularVelocity) > YawAngularVelocityEpsilon;

            if (dampLinearVelocity)
            {
                velocity = hasLinearMomentum
                    ? velocity * currentWaterDampingFactor
                    : Vector3.zero;
            }
            else if (!hasLinearMomentum)
            {
                velocity = Vector3.zero;
            }

            angularVelocity = hasAngularMomentum
                ? angularVelocity * currentWaterDampingFactor
                : 0f;
        }

        private static void UpdateEmergencyAscentGesture(
            VRCameraRig rig,
            bool playerIsSwimming,
            Vector3 leftPosition,
            Vector3 rightPosition,
            Vector3 leftPalmNormal,
            Vector3 rightPalmNormal,
            float dt)
        {
            emergencyAscentCandidate = false;
            emergencyAscentHandsLow = false;
            emergencyAscentHandsNearBody = false;
            emergencyAscentHandsSeparated = false;
            emergencyAscentPalmsInward = false;
            emergencyAscentHandSeparation = 0f;
            emergencyLeftHandDrop = 0f;
            emergencyRightHandDrop = 0f;
            emergencyLeftBodyDistance = 0f;
            emergencyRightBodyDistance = 0f;
            emergencyLeftPalmInward = -1f;
            emergencyRightPalmInward = -1f;

            if (!EmergencyAscentEnabled
                || !playerIsSwimming
                || surfaceDiveBridgeActive
                || rig == null
                || rig.vrCamera == null
                || dt <= 0f)
            {
                emergencyAscentHoldSeconds = 0f;
                emergencyAscentActive = false;
                return;
            }

            Vector3 hmd = rig.vrCamera.transform.localPosition;
            emergencyLeftHandDrop = hmd.y - leftPosition.y;
            emergencyRightHandDrop = hmd.y - rightPosition.y;

            Vector3 leftHorizontalFromBody = new Vector3(
                leftPosition.x - hmd.x,
                0f,
                leftPosition.z - hmd.z
            );
            Vector3 rightHorizontalFromBody = new Vector3(
                rightPosition.x - hmd.x,
                0f,
                rightPosition.z - hmd.z
            );

            emergencyLeftBodyDistance = leftHorizontalFromBody.magnitude;
            emergencyRightBodyDistance = rightHorizontalFromBody.magnitude;
            emergencyAscentHandSeparation = Vector3.Distance(
                leftHorizontalFromBody,
                rightHorizontalFromBody
            );

            Vector3 leftInward = -leftHorizontalFromBody;
            Vector3 rightInward = -rightHorizontalFromBody;
            Vector3 leftPalmHorizontal = Vector3.ProjectOnPlane(leftPalmNormal, Vector3.up);
            Vector3 rightPalmHorizontal = Vector3.ProjectOnPlane(rightPalmNormal, Vector3.up);

            if (leftInward.sqrMagnitude > 0.0001f && leftPalmHorizontal.sqrMagnitude > 0.0001f)
            {
                emergencyLeftPalmInward = Vector3.Dot(
                    leftPalmHorizontal.normalized,
                    leftInward.normalized
                );
            }
            if (rightInward.sqrMagnitude > 0.0001f && rightPalmHorizontal.sqrMagnitude > 0.0001f)
            {
                emergencyRightPalmInward = Vector3.Dot(
                    rightPalmHorizontal.normalized,
                    rightInward.normalized
                );
            }

            emergencyAscentHandsLow =
                emergencyLeftHandDrop >= EmergencyAscentMinHandDrop
                && emergencyRightHandDrop >= EmergencyAscentMinHandDrop;
            emergencyAscentHandsNearBody =
                emergencyLeftBodyDistance <= EmergencyAscentMaxHorizontalDistance
                && emergencyRightBodyDistance <= EmergencyAscentMaxHorizontalDistance;
            emergencyAscentHandsSeparated =
                emergencyAscentHandSeparation >= EmergencyAscentMinHandSeparation;
            emergencyAscentPalmsInward =
                emergencyLeftPalmInward >= EmergencyAscentPalmInwardThreshold
                && emergencyRightPalmInward >= EmergencyAscentPalmInwardThreshold;

            emergencyAscentCandidate =
                emergencyAscentHandsLow
                && emergencyAscentHandsNearBody
                && emergencyAscentHandsSeparated
                && emergencyAscentPalmsInward;

            if (!emergencyAscentCandidate)
            {
                emergencyAscentHoldSeconds = 0f;
                emergencyAscentActive = false;
                return;
            }

            emergencyAscentHoldSeconds += dt;
            emergencyAscentActive =
                emergencyAscentHoldSeconds >= Mathf.Max(0f, EmergencyAscentHoldTime);
        }

        private static void UpdateEmergencyDiveGesture(
            VRCameraRig rig,
            bool canDive,
            Vector3 leftPosition,
            Vector3 rightPosition,
            Vector3 leftPalmNormal,
            Vector3 rightPalmNormal,
            float dt)
        {
            emergencyDiveCandidate = false;
            emergencyDiveHandsHigh = false;
            emergencyDiveHandsNearBody = false;
            emergencyDiveHandsSeparated = false;
            emergencyDivePalmsFacing = false;
            emergencyDiveHandSeparation = 0f;
            emergencyLeftHandRise = 0f;
            emergencyRightHandRise = 0f;
            emergencyDiveLeftBodyDistance = 0f;
            emergencyDiveRightBodyDistance = 0f;
            emergencyDiveLeftPalmFacing = -1f;
            emergencyDiveRightPalmFacing = -1f;

            if (!EmergencyDiveEnabled
                || !canDive
                || rig == null
                || rig.vrCamera == null
                || dt <= 0f)
            {
                emergencyDiveHoldSeconds = 0f;
                emergencyDiveActive = false;
                return;
            }

            Vector3 hmd = rig.vrCamera.transform.localPosition;
            emergencyLeftHandRise = leftPosition.y - hmd.y;
            emergencyRightHandRise = rightPosition.y - hmd.y;

            Vector3 leftHorizontalFromBody = new Vector3(
                leftPosition.x - hmd.x,
                0f,
                leftPosition.z - hmd.z
            );
            Vector3 rightHorizontalFromBody = new Vector3(
                rightPosition.x - hmd.x,
                0f,
                rightPosition.z - hmd.z
            );

            emergencyDiveLeftBodyDistance = leftHorizontalFromBody.magnitude;
            emergencyDiveRightBodyDistance = rightHorizontalFromBody.magnitude;
            emergencyDiveHandSeparation = Vector3.Distance(
                leftHorizontalFromBody,
                rightHorizontalFromBody
            );

            Vector3 leftToRight = rightPosition - leftPosition;
            Vector3 rightToLeft = -leftToRight;
            Vector3 leftPalmHorizontal = Vector3.ProjectOnPlane(leftPalmNormal, Vector3.up);
            Vector3 rightPalmHorizontal = Vector3.ProjectOnPlane(rightPalmNormal, Vector3.up);
            Vector3 leftToRightHorizontal = Vector3.ProjectOnPlane(leftToRight, Vector3.up);
            Vector3 rightToLeftHorizontal = Vector3.ProjectOnPlane(rightToLeft, Vector3.up);

            if (leftPalmHorizontal.sqrMagnitude > 0.0001f
                && leftToRightHorizontal.sqrMagnitude > 0.0001f)
            {
                emergencyDiveLeftPalmFacing = Vector3.Dot(
                    leftPalmHorizontal.normalized,
                    leftToRightHorizontal.normalized
                );
            }
            if (rightPalmHorizontal.sqrMagnitude > 0.0001f
                && rightToLeftHorizontal.sqrMagnitude > 0.0001f)
            {
                emergencyDiveRightPalmFacing = Vector3.Dot(
                    rightPalmHorizontal.normalized,
                    rightToLeftHorizontal.normalized
                );
            }

            emergencyDiveHandsHigh =
                emergencyLeftHandRise >= EmergencyDiveMinHandRise
                && emergencyRightHandRise >= EmergencyDiveMinHandRise;
            emergencyDiveHandsNearBody =
                emergencyDiveLeftBodyDistance <= EmergencyDiveMaxHorizontalDistance
                && emergencyDiveRightBodyDistance <= EmergencyDiveMaxHorizontalDistance;
            emergencyDiveHandsSeparated =
                emergencyDiveHandSeparation >= EmergencyDiveMinHandSeparation;
            emergencyDivePalmsFacing =
                emergencyDiveLeftPalmFacing >= EmergencyDivePalmFacingThreshold
                && emergencyDiveRightPalmFacing >= EmergencyDivePalmFacingThreshold;

            emergencyDiveCandidate =
                emergencyDiveHandsHigh
                && emergencyDiveHandsNearBody
                && emergencyDiveHandsSeparated
                && emergencyDivePalmsFacing;

            if (!emergencyDiveCandidate)
            {
                emergencyDiveHoldSeconds = 0f;
                emergencyDiveActive = false;
                return;
            }

            emergencyDiveHoldSeconds += dt;
            emergencyDiveActive =
                emergencyDiveHoldSeconds >= Mathf.Max(0f, EmergencyDiveHoldTime);
        }

        private static void UpdateInertiaBrakeGesture(
            VRCameraRig rig,
            bool canBrake,
            Vector3 leftPosition,
            Vector3 rightPosition,
            Vector3 leftPalmNormal,
            Vector3 rightPalmNormal,
            float dt)
        {
            inertiaBrakeCandidate = false;
            inertiaBrakeHandsForward = false;
            inertiaBrakeHandsNearBody = false;
            inertiaBrakeHandsSeparated = false;
            inertiaBrakeHandsHorizontal = false;
            inertiaBrakePalmsForward = false;
            inertiaBrakeLeftForwardDistance = 0f;
            inertiaBrakeRightForwardDistance = 0f;
            inertiaBrakeLeftBodyDistance = 0f;
            inertiaBrakeRightBodyDistance = 0f;
            inertiaBrakeHandSeparation = 0f;
            inertiaBrakeLeftVerticalOffset = 0f;
            inertiaBrakeRightVerticalOffset = 0f;
            inertiaBrakeLeftPalmForward = -1f;
            inertiaBrakeRightPalmForward = -1f;

            if (!InertiaBrakeEnabled
                || !canBrake
                || rig == null
                || rig.vrCamera == null
                || dt <= 0f)
            {
                inertiaBrakeHoldSeconds = 0f;
                inertiaBrakeActive = false;
                return;
            }

            Vector3 hmd = rig.vrCamera.transform.localPosition;
            Vector3 bodyForward = rig.transform.InverseTransformDirection(
                rig.vrCamera.transform.forward
            );
            bodyForward = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
            if (bodyForward.sqrMagnitude <= 0.0001f)
            {
                inertiaBrakeHoldSeconds = 0f;
                inertiaBrakeActive = false;
                return;
            }
            bodyForward.Normalize();

            Vector3 leftHorizontal = new Vector3(
                leftPosition.x - hmd.x,
                0f,
                leftPosition.z - hmd.z
            );
            Vector3 rightHorizontal = new Vector3(
                rightPosition.x - hmd.x,
                0f,
                rightPosition.z - hmd.z
            );

            inertiaBrakeLeftForwardDistance = Vector3.Dot(leftHorizontal, bodyForward);
            inertiaBrakeRightForwardDistance = Vector3.Dot(rightHorizontal, bodyForward);
            inertiaBrakeLeftBodyDistance = leftHorizontal.magnitude;
            inertiaBrakeRightBodyDistance = rightHorizontal.magnitude;
            inertiaBrakeHandSeparation = Vector3.Distance(leftHorizontal, rightHorizontal);
            inertiaBrakeLeftVerticalOffset = Mathf.Abs(leftPosition.y - hmd.y);
            inertiaBrakeRightVerticalOffset = Mathf.Abs(rightPosition.y - hmd.y);

            Vector3 leftPalmHorizontal = Vector3.ProjectOnPlane(leftPalmNormal, Vector3.up);
            Vector3 rightPalmHorizontal = Vector3.ProjectOnPlane(rightPalmNormal, Vector3.up);
            if (leftPalmHorizontal.sqrMagnitude > 0.0001f)
            {
                inertiaBrakeLeftPalmForward = Vector3.Dot(
                    leftPalmHorizontal.normalized,
                    bodyForward
                );
            }
            if (rightPalmHorizontal.sqrMagnitude > 0.0001f)
            {
                inertiaBrakeRightPalmForward = Vector3.Dot(
                    rightPalmHorizontal.normalized,
                    bodyForward
                );
            }

            inertiaBrakeHandsForward =
                inertiaBrakeLeftForwardDistance >= InertiaBrakeMinForwardDistance
                && inertiaBrakeRightForwardDistance >= InertiaBrakeMinForwardDistance;
            inertiaBrakeHandsNearBody =
                inertiaBrakeLeftBodyDistance <= InertiaBrakeMaxHorizontalDistance
                && inertiaBrakeRightBodyDistance <= InertiaBrakeMaxHorizontalDistance;
            inertiaBrakeHandsSeparated =
                inertiaBrakeHandSeparation >= InertiaBrakeMinHandSeparation;
            inertiaBrakeHandsHorizontal =
                inertiaBrakeLeftVerticalOffset <= InertiaBrakeMaxVerticalOffset
                && inertiaBrakeRightVerticalOffset <= InertiaBrakeMaxVerticalOffset;
            inertiaBrakePalmsForward =
                inertiaBrakeLeftPalmForward >= InertiaBrakePalmForwardThreshold
                && inertiaBrakeRightPalmForward >= InertiaBrakePalmForwardThreshold;

            inertiaBrakeCandidate =
                inertiaBrakeHandsForward
                && inertiaBrakeHandsNearBody
                && inertiaBrakeHandsSeparated
                && inertiaBrakeHandsHorizontal
                && inertiaBrakePalmsForward;

            if (!inertiaBrakeCandidate)
            {
                inertiaBrakeHoldSeconds = 0f;
                inertiaBrakeActive = false;
                return;
            }

            inertiaBrakeHoldSeconds += dt;
            inertiaBrakeActive =
                inertiaBrakeHoldSeconds >= Mathf.Max(0f, InertiaBrakeHoldTime);
        }

        private static void ApplyInertiaBrake(ref Vector3 velocity, float dt)
        {
            inertiaBrakeVelocityBefore = velocity;
            inertiaBrakeVelocityAfter = velocity;
            inertiaBrakeLinearFactor = 1f;
            inertiaBrakeAngularFactor = 1f;

            if (!inertiaBrakeActive || dt <= 0f)
            {
                return;
            }

            // Exponential damping is frame-rate independent and never overshoots/reverses the
            // current velocity. It behaves like temporarily adding a very large viscous drag.
            inertiaBrakeLinearFactor = Mathf.Exp(-Mathf.Max(0f, InertiaBrakeLinearDamping) * dt);
            inertiaBrakeAngularFactor = Mathf.Exp(-Mathf.Max(0f, InertiaBrakeAngularDamping) * dt);

            velocity *= inertiaBrakeLinearFactor;
            yawAngularVelocity *= inertiaBrakeAngularFactor;
            currentYawTorque = 0f;

            if (velocity.sqrMagnitude < BodyVelocityEpsilon * BodyVelocityEpsilon)
            {
                velocity = Vector3.zero;
            }
            if (Mathf.Abs(yawAngularVelocity) < YawAngularVelocityStopThreshold)
            {
                yawAngularVelocity = 0f;
            }

            inertiaBrakeVelocityAfter = velocity;
        }

        private static Vector3 GetPalmNormalInRigSpace(
            VRCameraRig rig,
            bool isLeft,
            Transform controller)
        {
            // Experimentally verified for the SteamVR pose used here: local X/right is normal to
            // the palm plane. The SIGN of that normal tells the base physics which anatomical side
            // leads the motion: inner/palmar side or back of the hand.
            //
            // Use the rendered hand skeleton only to orient the sign. The controller axis remains
            // the actual normal used for physics, so finger animation/offsets cannot distort the
            // projected palm area as happened in earlier experiments.
            Vector3 controllerNormal = (controller.localRotation * Vector3.right).normalized;

            var hands = VRHands.instance;
            if (hands != null && rig != null)
            {
                Transform wrist = isLeft ? hands.leftHand : hands.rightHand;
                Transform[] fingers = isLeft ? hands.leftHandFingers : hands.rightHandFingers;
                int indexFinger = (int)VRHands.HandSkeletonBone.eBone_IndexFinger1;
                int pinkyFinger = (int)VRHands.HandSkeletonBone.eBone_PinkyFinger1;

                if (wrist != null
                    && fingers != null
                    && fingers.Length > pinkyFinger
                    && fingers[indexFinger] != null
                    && fingers[pinkyFinger] != null)
                {
                    Vector3 wristToIndex = fingers[indexFinger].position - wrist.position;
                    Vector3 wristToPinky = fingers[pinkyFinger].position - wrist.position;

                    // The hands are mirrored. With the same index->pinky cross order, left already
                    // points toward the palmar side while right points toward the dorsal side.
                    Vector3 anatomicalPalmNormal = Vector3.Cross(wristToIndex, wristToPinky);
                    if (!isLeft)
                    {
                        anatomicalPalmNormal = -anatomicalPalmNormal;
                    }

                    if (anatomicalPalmNormal.sqrMagnitude > 0.0001f)
                    {
                        Vector3 anatomicalPalmNormalRig = rig.transform.InverseTransformDirection(
                            anatomicalPalmNormal.normalized
                        );

                        if (Vector3.Dot(controllerNormal, anatomicalPalmNormalRig) < 0f)
                        {
                            controllerNormal = -controllerNormal;
                        }
                    }
                }
                else
                {
                    // Fallback for a loading/reinitialization frame. The two controller poses are
                    // mirrored, so use the expected anatomical sign until skeleton bones exist.
                    controllerNormal *= isLeft ? 1f : -1f;
                }
            }
            else
            {
                controllerNormal *= isLeft ? 1f : -1f;
            }

            // Empirical calibration for the right SteamVR hand: with the current controller pose
            // and rendered skeleton, the resolved signed normal is reversed relative to the inner
            // palm. Flip the final right-hand normal so positive signed alignment consistently
            // means "inner palm leads the stroke" on both hands.
            if (!isLeft)
            {
                controllerNormal = -controllerNormal;
            }

            return controllerNormal;
        }

        private static Vector3 ConvertWorldVelocityToGameInput(VRCameraRig rig, Vector3 worldVelocity)
        {
            if (worldVelocity.sqrMagnitude <= BodyVelocityEpsilon * BodyVelocityEpsilon)
            {
                return Vector3.zero;
            }

            Transform reference = null;
            if (Settings.HandBasedTurning)
            {
                reference = Settings.LeftHandBasedTurning ? VRCameraRig.GetLeftTargetTansform() : VRCameraRig.GetTargetTansform();
            }
            else if (MainCamera.camera != null)
            {
                reference = MainCamera.camera.transform;
            }

            if (reference == null)
            {
                // Preserve the old behaviour as a safe fallback when the normal movement
                // reference is temporarily unavailable during loading/reinitialization.
                return Vector3.ClampMagnitude(rig.transform.InverseTransformDirection(worldVelocity), 1f);
            }

            // Only horizontal input is expressed relative to Subnautica's current movement yaw.
            // Y remains true world up/down. This is what lets the player look straight ahead and
            // ascend by pushing water downward with the hands, or descend by pushing it upward.
            Quaternion yawOnly = Quaternion.Euler(0f, reference.eulerAngles.y, 0f);
            Vector3 horizontalWorld = new Vector3(worldVelocity.x, 0f, worldVelocity.z);
            Vector3 horizontalInput = Quaternion.Inverse(yawOnly) * horizontalWorld;

            return Vector3.ClampMagnitude(
                new Vector3(horizontalInput.x, worldVelocity.y, horizontalInput.z),
                1f
            );
        }

        private static void UpdateVerticalPwm(float verticalInput)
        {
            verticalPwmUp = false;
            verticalPwmDown = false;

            float strength = Mathf.Clamp01(Mathf.Abs(verticalInput));
            if (strength < VerticalPwmMinimumStrength)
            {
                verticalPwmAccumulator = 0f;
                verticalPwmDirection = 0;
                return;
            }

            int direction = verticalInput > 0f ? 1 : -1;
            if (direction != verticalPwmDirection)
            {
                // Do not let accumulated duty from the opposite direction leak into a reversal.
                verticalPwmAccumulator = 0f;
                verticalPwmDirection = direction;
            }

            // First-order pulse-density modulation. Over time the fraction of true frames closely
            // follows the analog input without introducing a slow fixed-frequency on/off cycle.
            verticalPwmAccumulator += strength;
            bool pulse = verticalPwmAccumulator >= 1f;
            if (pulse)
            {
                verticalPwmAccumulator -= 1f;
            }

            verticalPwmUp = pulse && direction > 0;
            verticalPwmDown = pulse && direction < 0;
        }

        internal static float VerticalPhysicalStrength => (emergencyAscentActive || emergencyDiveActive)
            ? 1f
            : Mathf.Clamp01(Mathf.Abs(swimInput.y));
        internal static bool VerticalPwmUp => verticalPwmUp;
        internal static bool VerticalPwmDown => verticalPwmDown;

        internal static bool EmergencyAscentCandidate => emergencyAscentCandidate;
        internal static float EmergencyAscentHoldSeconds => emergencyAscentHoldSeconds;
        internal static bool EmergencyAscentActive => emergencyAscentActive;
        internal static bool EmergencyAscentHandsLow => emergencyAscentHandsLow;
        internal static bool EmergencyAscentHandsNearBody => emergencyAscentHandsNearBody;
        internal static bool EmergencyAscentHandsSeparated => emergencyAscentHandsSeparated;
        internal static bool EmergencyAscentPalmsInward => emergencyAscentPalmsInward;
        internal static float EmergencyAscentHandSeparation => emergencyAscentHandSeparation;
        internal static float EmergencyLeftHandDrop => emergencyLeftHandDrop;
        internal static float EmergencyRightHandDrop => emergencyRightHandDrop;
        internal static float EmergencyLeftBodyDistance => emergencyLeftBodyDistance;
        internal static float EmergencyRightBodyDistance => emergencyRightBodyDistance;
        internal static float EmergencyLeftPalmInward => emergencyLeftPalmInward;
        internal static float EmergencyRightPalmInward => emergencyRightPalmInward;

        internal static bool EmergencyDiveCandidate => emergencyDiveCandidate;
        internal static float EmergencyDiveHoldSeconds => emergencyDiveHoldSeconds;
        internal static bool EmergencyDiveActive => emergencyDiveActive;
        internal static bool EmergencyDiveHandsHigh => emergencyDiveHandsHigh;
        internal static bool EmergencyDiveHandsNearBody => emergencyDiveHandsNearBody;
        internal static bool EmergencyDiveHandsSeparated => emergencyDiveHandsSeparated;
        internal static bool EmergencyDivePalmsFacing => emergencyDivePalmsFacing;
        internal static float EmergencyDiveHandSeparation => emergencyDiveHandSeparation;
        internal static float EmergencyLeftHandRise => emergencyLeftHandRise;
        internal static float EmergencyRightHandRise => emergencyRightHandRise;
        internal static float EmergencyDiveLeftBodyDistance => emergencyDiveLeftBodyDistance;
        internal static float EmergencyDiveRightBodyDistance => emergencyDiveRightBodyDistance;
        internal static float EmergencyDiveLeftPalmFacing => emergencyDiveLeftPalmFacing;
        internal static float EmergencyDiveRightPalmFacing => emergencyDiveRightPalmFacing;

        internal static bool InertiaBrakeCandidate => inertiaBrakeCandidate;
        internal static float InertiaBrakeHoldSeconds => inertiaBrakeHoldSeconds;
        internal static bool InertiaBrakeActive => inertiaBrakeActive;
        internal static bool InertiaBrakeHandsForward => inertiaBrakeHandsForward;
        internal static bool InertiaBrakeHandsNearBody => inertiaBrakeHandsNearBody;
        internal static bool InertiaBrakeHandsSeparated => inertiaBrakeHandsSeparated;
        internal static bool InertiaBrakeHandsHorizontal => inertiaBrakeHandsHorizontal;
        internal static bool InertiaBrakePalmsForward => inertiaBrakePalmsForward;
        internal static float InertiaBrakeLeftForwardDistance => inertiaBrakeLeftForwardDistance;
        internal static float InertiaBrakeRightForwardDistance => inertiaBrakeRightForwardDistance;
        internal static float InertiaBrakeLeftBodyDistance => inertiaBrakeLeftBodyDistance;
        internal static float InertiaBrakeRightBodyDistance => inertiaBrakeRightBodyDistance;
        internal static float InertiaBrakeHandSeparation => inertiaBrakeHandSeparation;
        internal static float InertiaBrakeLeftVerticalOffset => inertiaBrakeLeftVerticalOffset;
        internal static float InertiaBrakeRightVerticalOffset => inertiaBrakeRightVerticalOffset;
        internal static float InertiaBrakeLeftPalmForward => inertiaBrakeLeftPalmForward;
        internal static float InertiaBrakeRightPalmForward => inertiaBrakeRightPalmForward;
        internal static float InertiaBrakeLinearFactor => inertiaBrakeLinearFactor;
        internal static float InertiaBrakeAngularFactor => inertiaBrakeAngularFactor;
        internal static Vector3 InertiaBrakeVelocityBefore => inertiaBrakeVelocityBefore;
        internal static Vector3 InertiaBrakeVelocityAfter => inertiaBrakeVelocityAfter;

        internal static bool FinKickLeftGripHeld => finKickLeftGripHeld;
        internal static bool FinKickRightGripHeld => finKickRightGripHeld;
        internal static bool FinKickActive => finKickActive;
        internal static Vector3 FinKickInput => finKickInput;

        internal static float CurrentWaterResistance => WaterResistance;
        internal static float CurrentWaterDampingFactor => currentWaterDampingFactor;
        internal static float CurrentTurnLinearConversion => TurnLinearConversion;
        internal static bool SurfaceDiveBridgeActive => surfaceDiveBridgeActive;
        internal static bool SurfaceSwimActive => surfaceSwimActive;
        internal static bool FullPhysicalSwimmingActive => active && !surfaceDiveBridgeActive;
        internal static SwimmingControlMode ControlMode => controlMode;
        internal static bool SeaglideEquipped => controlMode == SwimmingControlMode.Seaglide
            && SeaglideSwimmingController.IsEquipped;
        internal static bool SeaglidePropulsionActive => controlMode == SwimmingControlMode.Seaglide
            && SeaglideSwimmingController.PropulsionActive;
        internal static bool SeaglideLeftGripHeld => SeaglideSwimmingController.LeftGripHeld;
        internal static bool SeaglideRightGripHeld => SeaglideSwimmingController.RightGripHeld;
        internal static float SeaglideLeftGripAnalog => SeaglideSwimmingController.LeftGripAnalog;
        internal static float SeaglideRightGripAnalog => SeaglideSwimmingController.RightGripAnalog;
        internal static float SeaglideThrottle => SeaglideSwimmingController.Throttle;
        internal static Vector3 SeaglideToolDirection => SeaglideSwimmingController.ToolDirection;

        internal static bool TryGetSeaglideMovementReference(out Transform movementReference)
        {
            UpdateIfNeeded();

            movementReference = null;
            if (!active || controlMode != SwimmingControlMode.Seaglide)
            {
                return false;
            }

            movementReference = SeaglideSwimmingController.MovementReference;
            return movementReference != null;
        }

        internal static bool ShouldConsumeSeaglideMotorEnergy(Seaglide seaglide)
        {
            UpdateIfNeeded();

            // Outside our active Seaglide mode vanilla owns the item completely. Inside it, motor
            // energy is consumed only while analog grip throttle is non-zero. Inertial movement
            // after release is body momentum and must not keep powering the Seaglide.
            return !active
                || controlMode != SwimmingControlMode.Seaglide
                || !SeaglideSwimmingController.IsControlling(seaglide)
                || SeaglideSwimmingController.PropulsionActive;
        }

        internal static bool ShouldSuppressNativeTurnInput
        {
            get
            {
                UpdateIfNeeded();
                return active;
            }
        }

        internal static bool CanUseLandGripJump
        {
            get
            {
                UpdateIfNeeded();

                Player player = Player.main;
                return Settings.PhysicalSwimming
                    && player != null
                    && player.currentMountedVehicle == null
                    && !player.IsSwimming()
                    && !surfaceDiveBridgeActive
                    && SwimmingControlModeResolver.Resolve(out _) == SwimmingControlMode.HandSwimming
                    && (player.pda == null || !player.pda.isOpen);
            }
        }
        internal static float CurrentWaterLevel => currentWaterLevel;
        internal static float CurrentSurfaceDistance => currentSurfaceDistance;
        internal static float CurrentYawTorque => currentYawTorque;
        internal static float YawAngularVelocity => yawAngularVelocity;
        internal static float PhysicalYawOffsetDegrees => physicalYawOffsetDegrees;

        private static void UpdateSurfaceDiveBridgeState(VRCameraRig rig, Player player, bool playerIsSwimming)
        {
            currentWaterLevel = GetWaterLevel();
            currentSurfaceDistance = player.transform.position.y - currentWaterLevel;

            if (!swimmingStateInitialized)
            {
                swimmingStateInitialized = true;
                wasSwimming = playerIsSwimming;
                surfaceSwimActive = false;
                return;
            }

            if (playerIsSwimming)
            {
                surfaceDiveBridgeActive = false;
                surfaceSwimActive = false;
            }
            else if (wasSwimming
                && !surfaceDiveBridgeActive
                && Mathf.Abs(currentSurfaceDistance) <= SurfaceSwimTolerance)
            {
                // We have actually crossed the ocean surface. A Swimming -> non-Swimming transition
                // can also happen when entering an underwater base; arming the bridge there would
                // keep Physical Swimming in control of the movement stick and make walking impossible.
                // Clear underwater linear/angular momentum so
                // surface locomotion starts from an intuitive rest state, while keeping the body's
                // accumulated yaw orientation itself.
                surfaceDiveBridgeActive = true;
                ResetTracking(
                    rig.leftController.transform.localPosition,
                    rig.rightController.transform.localPosition,
                    player.transform.position
                );
                worldBodyVelocity = Vector3.zero;
                yawAngularVelocity = 0f;
                currentYawTorque = 0f;
                swimInput = Vector3.zero;
                verticalPwmAccumulator = 0f;
                verticalPwmDirection = 0;
                verticalPwmUp = false;
                verticalPwmDown = false;
            }

            if (surfaceDiveBridgeActive)
            {
                surfaceSwimActive =
                    Mathf.Abs(currentSurfaceDistance) <= SurfaceSwimTolerance;

                // Once the player's root is clearly above the ocean surface we have transitioned
                // onto land / a structure rather than merely bobbing at the water line.
                if (currentSurfaceDistance > SurfaceBridgeDisarmHeight)
                {
                    surfaceDiveBridgeActive = false;
                    surfaceSwimActive = false;
                    ResetMotionState();
                }
            }

            wasSwimming = playerIsSwimming;
        }

        private static void ResetInertiaBrakeState()
        {
            inertiaBrakeCandidate = false;
            inertiaBrakeHoldSeconds = 0f;
            inertiaBrakeActive = false;
            inertiaBrakeHandsForward = false;
            inertiaBrakeHandsNearBody = false;
            inertiaBrakeHandsSeparated = false;
            inertiaBrakeHandsHorizontal = false;
            inertiaBrakePalmsForward = false;
            inertiaBrakeLeftForwardDistance = 0f;
            inertiaBrakeRightForwardDistance = 0f;
            inertiaBrakeLeftBodyDistance = 0f;
            inertiaBrakeRightBodyDistance = 0f;
            inertiaBrakeHandSeparation = 0f;
            inertiaBrakeLeftVerticalOffset = 0f;
            inertiaBrakeRightVerticalOffset = 0f;
            inertiaBrakeLeftPalmForward = -1f;
            inertiaBrakeRightPalmForward = -1f;
            inertiaBrakeLinearFactor = 1f;
            inertiaBrakeAngularFactor = 1f;
            inertiaBrakeVelocityBefore = Vector3.zero;
            inertiaBrakeVelocityAfter = Vector3.zero;
        }

        private static void ResetMotionState()
        {
            controlMode = SwimmingControlMode.HandSwimming;
            SeaglideSwimmingController.Reset();
            leftHand = default;
            rightHand = default;
            worldBodyVelocity = Vector3.zero;
            swimInput = Vector3.zero;
            verticalPwmAccumulator = 0f;
            verticalPwmDirection = 0;
            verticalPwmUp = false;
            verticalPwmDown = false;
            emergencyAscentCandidate = false;
            emergencyAscentHoldSeconds = 0f;
            emergencyAscentActive = false;
            emergencyAscentHandsLow = false;
            emergencyAscentHandsNearBody = false;
            emergencyAscentHandsSeparated = false;
            emergencyAscentPalmsInward = false;
            emergencyAscentHandSeparation = 0f;
            emergencyLeftHandDrop = 0f;
            emergencyRightHandDrop = 0f;
            emergencyLeftBodyDistance = 0f;
            emergencyRightBodyDistance = 0f;
            emergencyLeftPalmInward = -1f;
            emergencyRightPalmInward = -1f;

            emergencyDiveCandidate = false;
            emergencyDiveHoldSeconds = 0f;
            emergencyDiveActive = false;
            emergencyDiveHandsHigh = false;
            emergencyDiveHandsNearBody = false;
            emergencyDiveHandsSeparated = false;
            emergencyDivePalmsFacing = false;
            emergencyDiveHandSeparation = 0f;
            emergencyLeftHandRise = 0f;
            emergencyRightHandRise = 0f;
            emergencyDiveLeftBodyDistance = 0f;
            emergencyDiveRightBodyDistance = 0f;
            emergencyDiveLeftPalmFacing = -1f;
            emergencyDiveRightPalmFacing = -1f;

            ResetInertiaBrakeState();
            finKickLeftGripHeld = false;
            finKickRightGripHeld = false;
            finKickActive = false;
            finKickInput = Vector3.zero;
            currentWaterDampingFactor = 1f;
            handForceIdleSeconds = HandForceIdleGraceSeconds;
            previousPlayerPosition = Vector3.zero;
            actualPlayerVelocity = Vector3.zero;
            rawActualPlayerVelocity = Vector3.zero;
            playerVelocityInitialized = false;
            collisionRecentPeakSpeed = 0f;
            collisionRecentMotionSeconds = 0f;
            collisionStallSeconds = 0f;
            yawAngularVelocity = 0f;
            currentYawTorque = 0f;
        }

        private static void ResetTracking(Vector3 leftPosition, Vector3 rightPosition, Vector3 playerPosition)
        {
            leftHand = new HandStrokeState
            {
                initialized = true,
                previousPosition = leftPosition
            };
            rightHand = new HandStrokeState
            {
                initialized = true,
                previousPosition = rightPosition
            };
            previousPlayerPosition = playerPosition;
            actualPlayerVelocity = Vector3.zero;
            rawActualPlayerVelocity = Vector3.zero;
            playerVelocityInitialized = true;
            collisionRecentPeakSpeed = 0f;
            collisionRecentMotionSeconds = 0f;
            collisionStallSeconds = 0f;
            worldBodyVelocity = Vector3.zero;
            swimInput = Vector3.zero;
            verticalPwmAccumulator = 0f;
            verticalPwmDirection = 0;
            verticalPwmUp = false;
            verticalPwmDown = false;
            emergencyAscentCandidate = false;
            emergencyAscentHoldSeconds = 0f;
            emergencyAscentActive = false;
            emergencyAscentHandsLow = false;
            emergencyAscentHandsNearBody = false;
            emergencyAscentHandsSeparated = false;
            emergencyAscentPalmsInward = false;
            emergencyAscentHandSeparation = 0f;
            emergencyLeftHandDrop = 0f;
            emergencyRightHandDrop = 0f;
            emergencyLeftBodyDistance = 0f;
            emergencyRightBodyDistance = 0f;
            emergencyLeftPalmInward = -1f;
            emergencyRightPalmInward = -1f;

            emergencyDiveCandidate = false;
            emergencyDiveHoldSeconds = 0f;
            emergencyDiveActive = false;
            emergencyDiveHandsHigh = false;
            emergencyDiveHandsNearBody = false;
            emergencyDiveHandsSeparated = false;
            emergencyDivePalmsFacing = false;
            emergencyDiveHandSeparation = 0f;
            emergencyLeftHandRise = 0f;
            emergencyRightHandRise = 0f;
            emergencyDiveLeftBodyDistance = 0f;
            emergencyDiveRightBodyDistance = 0f;
            emergencyDiveLeftPalmFacing = -1f;
            emergencyDiveRightPalmFacing = -1f;

            ResetInertiaBrakeState();
            finKickLeftGripHeld = false;
            finKickRightGripHeld = false;
            finKickActive = false;
            finKickInput = Vector3.zero;
            currentWaterDampingFactor = 1f;
            handForceIdleSeconds = HandForceIdleGraceSeconds;
            yawAngularVelocity = 0f;
            currentYawTorque = 0f;
            active = true;
        }

        private static void Reset()
        {
            ResetMotionState();
            swimmingStateInitialized = false;
            wasSwimming = false;
            surfaceDiveBridgeActive = false;
            surfaceSwimActive = false;
            currentWaterLevel = 0f;
            currentSurfaceDistance = 0f;
            active = false;
        }
    }
}
