using BepInEx;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Writes physical-swimming telemetry to a standalone CSV file while the
    /// "Physical Swimming Telemetry" option is enabled.
    ///
    /// Recording is intentionally session-only: starting the game never silently
    /// resumes a previous recording.
    /// </summary>
    static class PhysicalSwimmingDebugRecorder
    {
        private const float SampleInterval = 1f / 60f;
        private const float FlushInterval = 1f;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static StreamWriter writer;
        private static bool recording;
        private static float startRealtime;
        private static float nextSampleRealtime;
        private static float nextFlushRealtime;
        private static Vector3 previousPlayerPosition;
        private static float previousPlayerSampleRealtime;
        private static bool hasPreviousPlayerPosition;
        private static string currentFilePath;

        public static bool IsRecording => recording;

        public static float ElapsedSeconds
        {
            get
            {
                if (!recording)
                {
                    return 0f;
                }
                return Mathf.Max(0f, Time.realtimeSinceStartup - startRealtime);
            }
        }

        public static string CurrentFilePath => currentFilePath;

        public static void SyncEnabled()
        {
            if (Settings.PhysicalSwimmingDebugRecording)
            {
                if (!recording)
                {
                    Start();
                }
            }
            else if (recording)
            {
                Stop();
            }
        }

        private static void Start()
        {
            try
            {
                string directory = Path.Combine(Paths.BepInExRootPath, "logs", "SubmersedVR");
                Directory.CreateDirectory(directory);

                string fileName = $"physical-swimming-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv";
                currentFilePath = Path.Combine(directory, fileName);

                writer = new StreamWriter(currentFilePath, false, new UTF8Encoding(false));
                PhysicalSwimmingSettingsSnapshotWriter.WriteTelemetrySnapshot(writer);
                writer.WriteLine(
                    "time_s,frame,dt," +
                    "player_pos_x,player_pos_y,player_pos_z,player_vel_x,player_vel_y,player_vel_z," +
                    "left_pos_x,left_pos_y,left_pos_z,left_raw_vx,left_raw_vy,left_raw_vz,left_filtered_vx,left_filtered_vy,left_filtered_vz,left_speed," +
                    "left_palm_nx,left_palm_ny,left_palm_nz,left_palm_alignment,left_palm_signed_alignment,left_back_of_hand_factor,left_effective_palm,left_base_drag,left_drag,left_base_speed_strength,left_speed_strength,left_force_x,left_force_y,left_force_z,left_can_stroke,left_tracking_jump," +
                    "right_pos_x,right_pos_y,right_pos_z,right_raw_vx,right_raw_vy,right_raw_vz,right_filtered_vx,right_filtered_vy,right_filtered_vz,right_speed," +
                    "right_palm_nx,right_palm_ny,right_palm_nz,right_palm_alignment,right_palm_signed_alignment,right_back_of_hand_factor,right_effective_palm,right_base_drag,right_drag,right_base_speed_strength,right_speed_strength,right_force_x,right_force_y,right_force_z,right_can_stroke,right_tracking_jump," +
                    "rig_force_x,rig_force_y,rig_force_z,world_force_x,world_force_y,world_force_z,world_force_strength,opposing_force,direction_dot,actual_player_direction_dot,actual_player_opposing_force," +
                    "model_player_vel_x,model_player_vel_y,model_player_vel_z,weak_opposing_brake,actual_player_reverse_guard,intentional_reverse_allowed,body_velocity_dead_zone,perp_delta_vx,perp_delta_vy,perp_delta_vz,body_after_redirection_x,body_after_redirection_y,body_after_redirection_z,momentum_redirection_angle_deg,momentum_redirection_applied,maneuverability_natural_turn_deg,maneuverability_turn_requested_deg,maneuverability_turn_applied_deg,maneuverability_reverse_factor," +
                    "raw_model_player_vel_x,raw_model_player_vel_y,raw_model_player_vel_z,collision_recent_peak_speed,collision_current_real_speed,collision_recent_motion_s,collision_stall_s,collision_speed_drop,collision_hard_stop_candidate,collision_hard_stop_applied,body_after_collision_x,body_after_collision_y,body_after_collision_z," +
                    "body_before_x,body_before_y,body_before_z,body_after_force_x,body_after_force_y,body_after_force_z,body_after_drag_x,body_after_drag_y,body_after_drag_z," +
                    "swim_input_x,swim_input_y,swim_input_z,physical_move_up,physical_move_down,physical_move_up_held,physical_move_down_held," +
                    "player_is_swimming,motor_mode,physical_vertical_strength,vertical_pwm_on," +
                    "surface_dive_bridge,surface_swim_active,full_physical_swimming_active," +
                    "control_mode,seaglide_equipped,seaglide_propulsion_active,seaglide_left_grip,seaglide_right_grip,seaglide_left_grip_analog,seaglide_right_grip_analog,seaglide_throttle,seaglide_dir_x,seaglide_dir_y,seaglide_dir_z," +
                    "water_level,surface_distance,yaw_torque,yaw_angular_velocity,physical_yaw_offset_deg,turn_linear_conversion,water_resistance,water_damping_factor," +
                    "emergency_ascent_candidate,emergency_ascent_hold_s,emergency_ascent_active,emergency_ascent_hands_low,emergency_ascent_hands_near_body,emergency_ascent_hands_separated,emergency_ascent_palms_inward,emergency_ascent_hand_separation,emergency_left_drop,emergency_right_drop,emergency_left_body_distance,emergency_right_body_distance,emergency_left_palm_inward,emergency_right_palm_inward," +
                    "emergency_dive_candidate,emergency_dive_hold_s,emergency_dive_active,emergency_dive_hands_high,emergency_dive_hands_near_body,emergency_dive_hands_separated,emergency_dive_palms_facing,emergency_dive_hand_separation,emergency_left_rise,emergency_right_rise,emergency_dive_left_body_distance,emergency_dive_right_body_distance,emergency_dive_left_palm_facing,emergency_dive_right_palm_facing," +
                    "push_stop_candidate,push_stop_hold_s,push_stop_active,push_stop_hands_forward,push_stop_hands_near_body,push_stop_hands_separated,push_stop_hands_horizontal,push_stop_palms_forward,push_stop_left_forward_distance,push_stop_right_forward_distance,push_stop_left_body_distance,push_stop_right_body_distance,push_stop_hand_separation,push_stop_left_vertical_offset,push_stop_right_vertical_offset,push_stop_left_palm_forward,push_stop_right_palm_forward,push_stop_linear_factor,push_stop_angular_factor,push_stop_before_x,push_stop_before_y,push_stop_before_z,push_stop_after_x,push_stop_after_y,push_stop_after_z," +
                    "fin_kick_left_grip,fin_kick_right_grip,fin_kick_active,fin_kick_input_x,fin_kick_input_y,fin_kick_input_z,fin_kick_strength," +
                    "cfg_min_stroke_speed,cfg_full_stroke_speed,cfg_hand_force,cfg_single_hand_strength,cfg_max_body_velocity,cfg_velocity_smoothing," +
                    "cfg_water_resistance,cfg_body_dead_zone," +
                    "cfg_turning_strength," +
                    "cfg_player_motion_dead_zone,cfg_maneuverability,cfg_maneuverability_steering_assist,cfg_maneuverability_reverse_force,cfg_maneuverability_weak_brake_floor,cfg_maneuverability_max_turn_rate," +
                    "cfg_collision_hard_stop_arm_speed,cfg_collision_hard_stop_speed,cfg_collision_hard_stop_confirm_time," +
                    "cfg_push_stop_enabled,cfg_push_stop_hold_time,cfg_push_stop_min_forward_distance,cfg_push_stop_max_horizontal_distance,cfg_push_stop_min_hand_separation,cfg_push_stop_max_vertical_offset,cfg_push_stop_palm_forward_threshold,cfg_push_stop_linear_damping,cfg_push_stop_angular_damping," +
                    "cfg_fin_kick_enabled,cfg_fin_kick_strength," +
                    "cfg_palm_dead_zone,cfg_palm_drag_exponent,cfg_palm_edge_drag,cfg_back_of_hand_drag," +
                    "cfg_surface_tolerance,cfg_surface_disarm_height,cfg_vertical_pwm_min"
                );
                writer.Flush();

                recording = true;
                startRealtime = Time.realtimeSinceStartup;
                nextSampleRealtime = startRealtime;
                nextFlushRealtime = startRealtime + FlushInterval;
                previousPlayerPosition = Vector3.zero;
                previousPlayerSampleRealtime = startRealtime;
                hasPreviousPlayerPosition = false;

                Mod.logger?.LogInfo($"Physical swimming telemetry recording started: {currentFilePath}");
            }
            catch (Exception ex)
            {
                recording = false;
                Settings.PhysicalSwimmingDebugRecording = false;
                writer?.Dispose();
                writer = null;
                Mod.logger?.LogError($"Could not start physical swimming telemetry: {ex}");
            }
        }

        public static void Stop()
        {
            if (!recording && writer == null)
            {
                return;
            }

            string stoppedFilePath = currentFilePath;

            try
            {
                writer?.Flush();
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Mod.logger?.LogError($"Could not close physical swimming telemetry file: {ex}");
            }
            finally
            {
                writer = null;
                recording = false;
                hasPreviousPlayerPosition = false;
                currentFilePath = null;
            }

            if (!string.IsNullOrEmpty(stoppedFilePath))
            {
                Mod.logger?.LogInfo($"Physical swimming telemetry recording stopped: {stoppedFilePath}");
            }
        }

        public static void RecordFrame(
            VRCameraRig rig,
            Player player,
            float dt,
            PhysicalSwimming.HandTelemetry left,
            PhysicalSwimming.HandTelemetry right,
            Vector3 rigSpaceForce,
            Vector3 worldForce,
            PhysicalSwimming.BodyForceTelemetry bodyForce,
            Vector3 bodyVelocityAfterDrag,
            Vector3 swimInput)
        {
            if (!recording || writer == null || rig == null || player == null)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now + 0.0001f < nextSampleRealtime)
            {
                return;
            }

            // Advance by fixed intervals rather than "now + interval" so occasional
            // long frames do not permanently reduce the sampling rate.
            do
            {
                nextSampleRealtime += SampleInterval;
            }
            while (nextSampleRealtime <= now);

            Vector3 playerPosition = player.transform.position;
            Vector3 actualPlayerVelocity = Vector3.zero;
            if (hasPreviousPlayerPosition)
            {
                float playerDt = now - previousPlayerSampleRealtime;
                if (playerDt > 0.0001f)
                {
                    actualPlayerVelocity = (playerPosition - previousPlayerPosition) / playerDt;
                }
            }

            previousPlayerPosition = playerPosition;
            previousPlayerSampleRealtime = now;
            hasPreviousPlayerPosition = true;

            float opposingForce = 0f;
            if (bodyForce.velocityBefore.sqrMagnitude > 0.000001f)
            {
                opposingForce = Mathf.Max(
                    0f,
                    Vector3.Dot(worldForce, -bodyForce.velocityBefore.normalized)
                );
            }

            var line = new StringBuilder(1200);
            Add(line, now - startRealtime);
            Add(line, Time.frameCount);
            Add(line, dt);

            Add(line, playerPosition);
            Add(line, actualPlayerVelocity);

            AddHand(line, left);
            AddHand(line, right);

            Add(line, rigSpaceForce);
            Add(line, worldForce);
            Add(line, worldForce.magnitude);
            Add(line, opposingForce);
            Add(line, bodyForce.directionDot);
            Add(line, bodyForce.actualPlayerDirectionDot);
            Add(line, bodyForce.actualPlayerOpposingForce);
            Add(line, bodyForce.actualPlayerVelocity);
            Add(line, bodyForce.weakOpposingBrake);
            Add(line, bodyForce.actualPlayerReverseGuard);
            Add(line, bodyForce.intentionalReverseAllowed);
            Add(line, bodyForce.bodyVelocityDeadZoneApplied);
            Add(line, bodyForce.perpendicularDeltaVelocity);
            Add(line, bodyForce.velocityAfterRedirection);
            Add(line, bodyForce.momentumRedirectionAngleDegrees);
            Add(line, bodyForce.momentumRedirectionApplied);
            Add(line, bodyForce.maneuverabilityNaturalTurnDegrees);
            Add(line, bodyForce.maneuverabilityTurnRequestedDegrees);
            Add(line, bodyForce.maneuverabilityTurnAppliedDegrees);
            Add(line, bodyForce.maneuverabilityReverseFactor);
            Add(line, bodyForce.rawActualPlayerVelocity);
            Add(line, bodyForce.collisionRecentPeakSpeed);
            Add(line, bodyForce.collisionCurrentRealSpeed);
            Add(line, bodyForce.collisionRecentMotionSeconds);
            Add(line, bodyForce.collisionStallSeconds);
            Add(line, bodyForce.collisionSpeedDrop);
            Add(line, bodyForce.collisionHardStopCandidate);
            Add(line, bodyForce.collisionHardStopApplied);
            Add(line, bodyForce.velocityAfterCollisionRelease);

            Add(line, bodyForce.velocityBefore);
            Add(line, bodyForce.velocityAfter);
            Add(line, bodyVelocityAfterDrag);
            Add(line, swimInput);

            float physicalMoveUp = Mathf.Max(0f, swimInput.y);
            float physicalMoveDown = Mathf.Max(0f, -swimInput.y);
            Add(line, physicalMoveUp);
            Add(line, physicalMoveDown);
            Add(line, PhysicalSwimming.VerticalPwmUp);
            Add(line, PhysicalSwimming.VerticalPwmDown);
            Add(line, player.IsSwimming());
            Add(line, (int)player.motorMode);
            Add(line, PhysicalSwimming.VerticalPhysicalStrength);
            Add(line, PhysicalSwimming.VerticalPwmUp || PhysicalSwimming.VerticalPwmDown);
            Add(line, PhysicalSwimming.SurfaceDiveBridgeActive);
            Add(line, PhysicalSwimming.SurfaceSwimActive);
            Add(line, PhysicalSwimming.FullPhysicalSwimmingActive);
            Add(line, (int)PhysicalSwimming.ControlMode);
            Add(line, PhysicalSwimming.SeaglideEquipped);
            Add(line, PhysicalSwimming.SeaglidePropulsionActive);
            Add(line, PhysicalSwimming.SeaglideLeftGripHeld);
            Add(line, PhysicalSwimming.SeaglideRightGripHeld);
            Add(line, PhysicalSwimming.SeaglideLeftGripAnalog);
            Add(line, PhysicalSwimming.SeaglideRightGripAnalog);
            Add(line, PhysicalSwimming.SeaglideThrottle);
            Add(line, PhysicalSwimming.SeaglideToolDirection);
            Add(line, PhysicalSwimming.CurrentWaterLevel);
            Add(line, PhysicalSwimming.CurrentSurfaceDistance);
            Add(line, PhysicalSwimming.CurrentYawTorque);
            Add(line, PhysicalSwimming.YawAngularVelocity);
            Add(line, PhysicalSwimming.PhysicalYawOffsetDegrees);
            Add(line, PhysicalSwimming.CurrentTurnLinearConversion);
            Add(line, PhysicalSwimming.CurrentWaterResistance);
            Add(line, PhysicalSwimming.CurrentWaterDampingFactor);
            Add(line, PhysicalSwimming.EmergencyAscentCandidate);
            Add(line, PhysicalSwimming.EmergencyAscentHoldSeconds);
            Add(line, PhysicalSwimming.EmergencyAscentActive);
            Add(line, PhysicalSwimming.EmergencyAscentHandsLow);
            Add(line, PhysicalSwimming.EmergencyAscentHandsNearBody);
            Add(line, PhysicalSwimming.EmergencyAscentHandsSeparated);
            Add(line, PhysicalSwimming.EmergencyAscentPalmsInward);
            Add(line, PhysicalSwimming.EmergencyAscentHandSeparation);
            Add(line, PhysicalSwimming.EmergencyLeftHandDrop);
            Add(line, PhysicalSwimming.EmergencyRightHandDrop);
            Add(line, PhysicalSwimming.EmergencyLeftBodyDistance);
            Add(line, PhysicalSwimming.EmergencyRightBodyDistance);
            Add(line, PhysicalSwimming.EmergencyLeftPalmInward);
            Add(line, PhysicalSwimming.EmergencyRightPalmInward);

            Add(line, PhysicalSwimming.EmergencyDiveCandidate);
            Add(line, PhysicalSwimming.EmergencyDiveHoldSeconds);
            Add(line, PhysicalSwimming.EmergencyDiveActive);
            Add(line, PhysicalSwimming.EmergencyDiveHandsHigh);
            Add(line, PhysicalSwimming.EmergencyDiveHandsNearBody);
            Add(line, PhysicalSwimming.EmergencyDiveHandsSeparated);
            Add(line, PhysicalSwimming.EmergencyDivePalmsFacing);
            Add(line, PhysicalSwimming.EmergencyDiveHandSeparation);
            Add(line, PhysicalSwimming.EmergencyLeftHandRise);
            Add(line, PhysicalSwimming.EmergencyRightHandRise);
            Add(line, PhysicalSwimming.EmergencyDiveLeftBodyDistance);
            Add(line, PhysicalSwimming.EmergencyDiveRightBodyDistance);
            Add(line, PhysicalSwimming.EmergencyDiveLeftPalmFacing);
            Add(line, PhysicalSwimming.EmergencyDiveRightPalmFacing);

            Add(line, PhysicalSwimming.InertiaBrakeCandidate);
            Add(line, PhysicalSwimming.InertiaBrakeHoldSeconds);
            Add(line, PhysicalSwimming.InertiaBrakeActive);
            Add(line, PhysicalSwimming.InertiaBrakeHandsForward);
            Add(line, PhysicalSwimming.InertiaBrakeHandsNearBody);
            Add(line, PhysicalSwimming.InertiaBrakeHandsSeparated);
            Add(line, PhysicalSwimming.InertiaBrakeHandsHorizontal);
            Add(line, PhysicalSwimming.InertiaBrakePalmsForward);
            Add(line, PhysicalSwimming.InertiaBrakeLeftForwardDistance);
            Add(line, PhysicalSwimming.InertiaBrakeRightForwardDistance);
            Add(line, PhysicalSwimming.InertiaBrakeLeftBodyDistance);
            Add(line, PhysicalSwimming.InertiaBrakeRightBodyDistance);
            Add(line, PhysicalSwimming.InertiaBrakeHandSeparation);
            Add(line, PhysicalSwimming.InertiaBrakeLeftVerticalOffset);
            Add(line, PhysicalSwimming.InertiaBrakeRightVerticalOffset);
            Add(line, PhysicalSwimming.InertiaBrakeLeftPalmForward);
            Add(line, PhysicalSwimming.InertiaBrakeRightPalmForward);
            Add(line, PhysicalSwimming.InertiaBrakeLinearFactor);
            Add(line, PhysicalSwimming.InertiaBrakeAngularFactor);
            Add(line, PhysicalSwimming.InertiaBrakeVelocityBefore);
            Add(line, PhysicalSwimming.InertiaBrakeVelocityAfter);

            Add(line, PhysicalSwimming.FinKickLeftGripHeld);
            Add(line, PhysicalSwimming.FinKickRightGripHeld);
            Add(line, PhysicalSwimming.FinKickActive);
            Add(line, PhysicalSwimming.FinKickInput);
            Add(line, Settings.PhysicalSwimmingFinKickStrength);

            Add(line, Settings.PhysicalSwimmingMinStrokeSpeed);
            Add(line, Settings.PhysicalSwimmingFullStrokeSpeed);
            Add(line, Settings.PhysicalSwimmingHandForceCoefficient);
            Add(line, Settings.PhysicalSwimmingMaxSingleHandContribution);
            Add(line, Settings.PhysicalSwimmingMaxBodyVelocity);
            Add(line, Settings.PhysicalSwimmingVelocitySmoothing);
            Add(line, Settings.PhysicalSwimmingWaterResistance);
            Add(line, Settings.PhysicalSwimmingBodyVelocityDeadZone);
            Add(line, Settings.PhysicalSwimmingTurningStrength);
            Add(line, Settings.PhysicalSwimmingPlayerVelocityDeadZone);
            Add(line, Settings.PhysicalSwimmingManeuverability);
            Add(line, PhysicalSwimming.ManeuverabilitySteeringAssistFactor);
            Add(line, PhysicalSwimming.ManeuverabilityReverseForceThreshold);
            Add(line, PhysicalSwimming.ManeuverabilityWeakBrakeFloor);
            Add(line, PhysicalSwimming.ManeuverabilityMaxTurnRate);
            Add(line, PhysicalSwimming.CollisionHardStopArmSpeed);
            Add(line, PhysicalSwimming.CollisionHardStopSpeed);
            Add(line, PhysicalSwimming.CollisionHardStopConfirmTime);
            Add(line, Settings.PhysicalSwimmingInertiaBrake);
            Add(line, PhysicalSwimming.InertiaBrakeHoldTime);
            Add(line, PhysicalSwimming.InertiaBrakeMinForwardDistance);
            Add(line, PhysicalSwimming.InertiaBrakeMaxHorizontalDistance);
            Add(line, PhysicalSwimming.InertiaBrakeMinHandSeparation);
            Add(line, PhysicalSwimming.InertiaBrakeMaxVerticalOffset);
            Add(line, PhysicalSwimming.InertiaBrakePalmForwardThreshold);
            Add(line, PhysicalSwimming.InertiaBrakeLinearDamping);
            Add(line, PhysicalSwimming.InertiaBrakeAngularDamping);
            Add(line, true);
            Add(line, Settings.PhysicalSwimmingFinKickStrength);
            Add(line, PhysicalSwimming.PalmDragDeadZone);
            Add(line, PhysicalSwimming.PalmDragExponent);
            Add(line, PhysicalSwimming.PalmEdgeDrag);
            Add(line, PhysicalSwimming.BackOfHandDragMultiplier);
            Add(line, PhysicalSwimming.SurfaceSwimTolerance);
            Add(line, PhysicalSwimming.SurfaceBridgeDisarmHeight);
            Add(line, PhysicalSwimming.VerticalPwmMinimumStrength, false);

            writer.WriteLine(line.ToString());

            if (now >= nextFlushRealtime)
            {
                writer.Flush();
                nextFlushRealtime = now + FlushInterval;
            }
        }

        private static void AddHand(StringBuilder line, PhysicalSwimming.HandTelemetry hand)
        {
            Add(line, hand.position);
            Add(line, hand.rawVelocity);
            Add(line, hand.filteredVelocity);
            Add(line, hand.speed);
            Add(line, hand.palmNormal);
            Add(line, hand.palmAlignment);
            Add(line, hand.signedPalmAlignment);
            Add(line, hand.backOfHandFactor);
            Add(line, hand.effectivePalmAlignment);
            Add(line, hand.baseDragFactor);
            Add(line, hand.dragFactor);
            Add(line, hand.baseSpeedStrength);
            Add(line, hand.speedStrength);
            Add(line, hand.force);
            Add(line, hand.canStroke);
            Add(line, hand.trackingJumpRejected);
        }

        private static void Add(StringBuilder line, Vector3 value, bool commaAfter = true)
        {
            Add(line, value.x);
            Add(line, value.y);
            Add(line, value.z, commaAfter);
        }

        private static void Add(StringBuilder line, float value, bool commaAfter = true)
        {
            line.Append(value.ToString("0.#####", Invariant));
            if (commaAfter)
            {
                line.Append(',');
            }
        }

        private static void Add(StringBuilder line, int value, bool commaAfter = true)
        {
            line.Append(value.ToString(Invariant));
            if (commaAfter)
            {
                line.Append(',');
            }
        }

        private static void Add(StringBuilder line, bool value, bool commaAfter = true)
        {
            line.Append(value ? '1' : '0');
            if (commaAfter)
            {
                line.Append(',');
            }
        }
    }
}
