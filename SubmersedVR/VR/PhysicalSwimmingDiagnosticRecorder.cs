using BepInEx;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Temporary diagnostic trace for two ambiguous VR states:
    /// 1) which controller / anatomical axis really represents a forward-facing palm for Push Stop;
    /// 2) which native Subnautica locomotion state distinguishes swimming, ocean-surface walking,
    ///    and walking inside a submerged base.
    ///
    /// It follows the existing Telemetry Recording toggle, but writes its own compact CSV so it
    /// keeps sampling even when PhysicalSwimming itself becomes inactive on land / inside a base.
    /// </summary>
    static class PhysicalSwimmingDiagnosticRecorder
    {
        private const float SampleInterval = 1f / 30f;
        private const float FlushInterval = 1f;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static StreamWriter writer;
        private static bool recording;
        private static float startRealtime;
        private static float nextSampleRealtime;
        private static float nextFlushRealtime;
        private static string currentFilePath;

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

                string fileName = $"physical-swimming-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv";
                currentFilePath = Path.Combine(directory, fileName);
                writer = new StreamWriter(currentFilePath, false, new UTF8Encoding(false));
                writer.WriteLine(
                    "time_s,frame," +
                    "player_pos_x,player_pos_y,player_pos_z,player_is_swimming,motor_mode,motor_mode_name," +
                    "surface_dive_bridge,surface_swim_active,water_level,surface_distance," +
                    "native_is_underwater_for_swimming,native_is_underwater,native_is_inside,native_is_inside_walkable,native_is_in_base," +
                    "player_controller_found,pc_under_water,pc_active_controller_type,pc_ground_controller_type,pc_underwater_controller_type,pc_active_is_ground,pc_active_is_underwater,ground_is_grounded," +
                    "body_forward_x,body_forward_y,body_forward_z," +
                    "left_ctrl_right_dot_body,left_ctrl_up_dot_body,left_ctrl_forward_dot_body,left_anatomical_palm_dot_body,left_anatomical_palm_dot_up,left_finger_dir_dot_body,left_finger_dir_dot_up," +
                    "right_ctrl_right_dot_body,right_ctrl_up_dot_body,right_ctrl_forward_dot_body,right_anatomical_palm_dot_body,right_anatomical_palm_dot_up,right_finger_dir_dot_body,right_finger_dir_dot_up," +
                    "push_stop_candidate,push_stop_hold_s,push_stop_active,push_stop_palms_forward,push_stop_left_palm_forward,push_stop_right_palm_forward"
                );
                writer.Flush();

                recording = true;
                startRealtime = Time.realtimeSinceStartup;
                nextSampleRealtime = startRealtime;
                nextFlushRealtime = startRealtime + FlushInterval;
                Mod.logger?.LogInfo($"Physical swimming diagnostic recording started: {currentFilePath}");
            }
            catch (Exception ex)
            {
                recording = false;
                writer?.Dispose();
                writer = null;
                currentFilePath = null;
                Mod.logger?.LogError($"Could not start physical swimming diagnostic recording: {ex}");
            }
        }

        private static void Stop()
        {
            string stoppedFilePath = currentFilePath;
            try
            {
                writer?.Flush();
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Mod.logger?.LogError($"Could not close physical swimming diagnostic file: {ex}");
            }
            finally
            {
                writer = null;
                recording = false;
                currentFilePath = null;
            }

            if (!string.IsNullOrEmpty(stoppedFilePath))
            {
                Mod.logger?.LogInfo($"Physical swimming diagnostic recording stopped: {stoppedFilePath}");
            }
        }

        public static void RecordFrame(VRCameraRig rig, Player player, bool playerIsSwimming)
        {
            if (!recording || writer == null || rig == null || player == null || rig.vrCamera == null)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now + 0.0001f < nextSampleRealtime)
            {
                return;
            }

            do
            {
                nextSampleRealtime += SampleInterval;
            }
            while (nextSampleRealtime <= now);

            int isUnderwaterForSwimming = ReadBoolState(
                player,
                "IsUnderwaterForSwimming",
                "isUnderwaterForSwimming"
            );
            int isUnderwater = ReadBoolState(player, "IsUnderwater", "isUnderwater");
            int isInside = ReadBoolState(player, "IsInside", "isInside");
            int isInsideWalkable = ReadBoolState(player, "IsInsideWalkable", "isInsideWalkable");
            int isInBase = ReadBoolState(player, "IsInBase", "isInBase");

            Component playerController = FindComponentByTypeName(player.gameObject, "PlayerController");
            object activeController = ReadMember(playerController, "activeController");
            object groundController = ReadMember(playerController, "groundController");
            object underwaterController = ReadMember(playerController, "underWaterController");
            int pcUnderWater = ReadBoolState(playerController, null, "underWater");
            if (pcUnderWater < 0)
            {
                pcUnderWater = ReadBoolState(playerController, null, "underwater");
            }
            int groundIsGrounded = ReadBoolState(groundController, "IsGrounded", "isGrounded");

            Vector3 bodyForward = rig.transform.InverseTransformDirection(
                rig.vrCamera.transform.forward
            );
            bodyForward = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
            if (bodyForward.sqrMagnitude > 0.0001f)
            {
                bodyForward.Normalize();
            }

            HandOrientationDiagnostic left = GetHandOrientationDiagnostic(
                rig,
                true,
                rig.leftController != null ? rig.leftController.transform : null,
                bodyForward
            );
            HandOrientationDiagnostic right = GetHandOrientationDiagnostic(
                rig,
                false,
                rig.rightController != null ? rig.rightController.transform : null,
                bodyForward
            );

            var line = new StringBuilder(900);
            Add(line, now - startRealtime);
            Add(line, Time.frameCount);
            Add(line, player.transform.position);
            Add(line, playerIsSwimming);
            Add(line, (int)player.motorMode);
            Add(line, player.motorMode.ToString());
            Add(line, PhysicalSwimming.SurfaceDiveBridgeActive);
            Add(line, PhysicalSwimming.SurfaceSwimActive);
            Add(line, PhysicalSwimming.CurrentWaterLevel);
            Add(line, PhysicalSwimming.CurrentSurfaceDistance);
            Add(line, isUnderwaterForSwimming);
            Add(line, isUnderwater);
            Add(line, isInside);
            Add(line, isInsideWalkable);
            Add(line, isInBase);
            Add(line, playerController != null);
            Add(line, pcUnderWater);
            Add(line, TypeName(activeController));
            Add(line, TypeName(groundController));
            Add(line, TypeName(underwaterController));
            Add(line, ReferenceEquals(activeController, groundController));
            Add(line, ReferenceEquals(activeController, underwaterController));
            Add(line, groundIsGrounded);
            Add(line, bodyForward);

            Add(line, left.controllerRightDotBody);
            Add(line, left.controllerUpDotBody);
            Add(line, left.controllerForwardDotBody);
            Add(line, left.anatomicalPalmDotBody);
            Add(line, left.anatomicalPalmDotUp);
            Add(line, left.fingerDirectionDotBody);
            Add(line, left.fingerDirectionDotUp);

            Add(line, right.controllerRightDotBody);
            Add(line, right.controllerUpDotBody);
            Add(line, right.controllerForwardDotBody);
            Add(line, right.anatomicalPalmDotBody);
            Add(line, right.anatomicalPalmDotUp);
            Add(line, right.fingerDirectionDotBody);
            Add(line, right.fingerDirectionDotUp);

            Add(line, PhysicalSwimming.InertiaBrakeCandidate);
            Add(line, PhysicalSwimming.InertiaBrakeHoldSeconds);
            Add(line, PhysicalSwimming.InertiaBrakeActive);
            Add(line, PhysicalSwimming.InertiaBrakePalmsForward);
            Add(line, PhysicalSwimming.InertiaBrakeLeftPalmForward);
            Add(line, PhysicalSwimming.InertiaBrakeRightPalmForward, false);

            writer.WriteLine(line.ToString());

            if (now >= nextFlushRealtime)
            {
                writer.Flush();
                nextFlushRealtime = now + FlushInterval;
            }
        }

        private struct HandOrientationDiagnostic
        {
            public float controllerRightDotBody;
            public float controllerUpDotBody;
            public float controllerForwardDotBody;
            public float anatomicalPalmDotBody;
            public float anatomicalPalmDotUp;
            public float fingerDirectionDotBody;
            public float fingerDirectionDotUp;
        }

        private static HandOrientationDiagnostic GetHandOrientationDiagnostic(
            VRCameraRig rig,
            bool isLeft,
            Transform controller,
            Vector3 bodyForward)
        {
            HandOrientationDiagnostic result = new HandOrientationDiagnostic
            {
                controllerRightDotBody = -2f,
                controllerUpDotBody = -2f,
                controllerForwardDotBody = -2f,
                anatomicalPalmDotBody = -2f,
                anatomicalPalmDotUp = -2f,
                fingerDirectionDotBody = -2f,
                fingerDirectionDotUp = -2f
            };

            if (rig == null || controller == null || bodyForward.sqrMagnitude <= 0.0001f)
            {
                return result;
            }

            Vector3 controllerRight = rig.transform.InverseTransformDirection(controller.right).normalized;
            Vector3 controllerUp = rig.transform.InverseTransformDirection(controller.up).normalized;
            Vector3 controllerForward = rig.transform.InverseTransformDirection(controller.forward).normalized;
            result.controllerRightDotBody = Vector3.Dot(controllerRight, bodyForward);
            result.controllerUpDotBody = Vector3.Dot(controllerUp, bodyForward);
            result.controllerForwardDotBody = Vector3.Dot(controllerForward, bodyForward);

            VRHands hands = VRHands.instance;
            if (hands == null)
            {
                return result;
            }

            Transform wrist = isLeft ? hands.leftHand : hands.rightHand;
            Transform[] fingers = isLeft ? hands.leftHandFingers : hands.rightHandFingers;
            int indexFinger = (int)VRHands.HandSkeletonBone.eBone_IndexFinger1;
            int pinkyFinger = (int)VRHands.HandSkeletonBone.eBone_PinkyFinger1;
            if (wrist == null
                || fingers == null
                || fingers.Length <= pinkyFinger
                || fingers[indexFinger] == null
                || fingers[pinkyFinger] == null)
            {
                return result;
            }

            Vector3 wristToIndex = fingers[indexFinger].position - wrist.position;
            Vector3 wristToPinky = fingers[pinkyFinger].position - wrist.position;
            Vector3 anatomicalPalmNormal = Vector3.Cross(wristToIndex, wristToPinky);
            if (!isLeft)
            {
                anatomicalPalmNormal = -anatomicalPalmNormal;
            }

            if (anatomicalPalmNormal.sqrMagnitude > 0.0001f)
            {
                Vector3 palmRig = rig.transform.InverseTransformDirection(
                    anatomicalPalmNormal.normalized
                );
                result.anatomicalPalmDotBody = Vector3.Dot(palmRig, bodyForward);
                result.anatomicalPalmDotUp = Vector3.Dot(palmRig, Vector3.up);
            }

            Vector3 fingersMidpoint = (fingers[indexFinger].position + fingers[pinkyFinger].position) * 0.5f;
            Vector3 fingerDirection = fingersMidpoint - wrist.position;
            if (fingerDirection.sqrMagnitude > 0.0001f)
            {
                Vector3 fingerRig = rig.transform.InverseTransformDirection(
                    fingerDirection.normalized
                );
                result.fingerDirectionDotBody = Vector3.Dot(fingerRig, bodyForward);
                result.fingerDirectionDotUp = Vector3.Dot(fingerRig, Vector3.up);
            }

            return result;
        }

        private static Component FindComponentByTypeName(GameObject gameObject, string typeName)
        {
            if (gameObject == null)
            {
                return null;
            }

            Component[] components = gameObject.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component != null && component.GetType().Name == typeName)
                {
                    return component;
                }
            }
            return null;
        }

        private static int InvokeBoolMethod(object target, string methodName)
        {
            if (target == null)
            {
                return -1;
            }

            try
            {
                MethodInfo method = target.GetType().GetMethod(
                    methodName,
                    InstanceFlags,
                    null,
                    Type.EmptyTypes,
                    null
                );
                if (method == null || method.ReturnType != typeof(bool))
                {
                    return -1;
                }
                return (bool)method.Invoke(target, null) ? 1 : 0;
            }
            catch
            {
                return -1;
            }
        }

        private static int ReadBoolState(object target, string methodName, string memberName)
        {
            if (!string.IsNullOrEmpty(methodName))
            {
                int methodValue = InvokeBoolMethod(target, methodName);
                if (methodValue >= 0)
                {
                    return methodValue;
                }
            }

            if (!string.IsNullOrEmpty(memberName))
            {
                object value = ReadMember(target, memberName);
                if (value is bool flag)
                {
                    return flag ? 1 : 0;
                }
            }

            return -1;
        }

        private static object ReadMember(object target, string memberName)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                Type type = target.GetType();
                FieldInfo field = type.GetField(memberName, InstanceFlags);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                PropertyInfo property = type.GetProperty(memberName, InstanceFlags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }
            }
            catch
            {
            }
            return null;
        }

        private static string TypeName(object value)
        {
            return value != null ? value.GetType().Name : string.Empty;
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

        private static void Add(StringBuilder line, string value, bool commaAfter = true)
        {
            string safe = (value ?? string.Empty).Replace("\"", "\"\"");
            line.Append('"').Append(safe).Append('"');
            if (commaAfter)
            {
                line.Append(',');
            }
        }
    }
}
