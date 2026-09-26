using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

#pragma warning disable Harmony003
namespace SubmersedVR
{
    extern alias SteamVRRef;
    extern alias SteamVRActions;
    using SteamVRRef.Valve.VR;
    using SteamVRActions.Valve.VR;
    using System.Collections.Generic;
    using System.Reflection;

    #region Patches
    static class SteamVrGameInput
    {
        public static bool InputLocked = false;
        public static bool IsSteamVrReady = false;
        public static bool SnapTurned = false;

        public static bool ShouldIgnore(GameInput.Button action)
        {
            return !IsSteamVrReady || InputLocked
                || action == GameInput.Button.Slot1
                || action == GameInput.Button.Slot2
                || action == GameInput.Button.Slot3
                || action == GameInput.Button.Slot4
                || action == GameInput.Button.Slot5
                || action == GameInput.Button.AutoMove
                || action.ToString() == "45" || action.ToString() == "46";
        }

        public static Vector2 GetScrollDelta()
        {
            if (!IsSteamVrReady || InputLocked)
            {
                return Vector2.zero;
            }
            return SteamVR_Actions.subnautica.UIScroll.GetAxis(SteamVR_Input_Sources.Any);
        }

        public static void GetFinKickGripState(out bool leftGripHeld, out bool rightGripHeld)
        {
            leftGripHeld = false;
            rightGripHeld = false;

            if (!IsSteamVrReady || InputLocked)
            {
                return;
            }

            // Current SubmersedVR bindings map the lower/middle-finger grip controls to
            // MoveDown on the left hand and MoveUp on the right hand. Query each action from
            // its specific input source so holding both grips can be recognized unambiguously.
            leftGripHeld = SteamVR_Actions.subnautica.MoveDown.GetState(SteamVR_Input_Sources.LeftHand);
            rightGripHeld = SteamVR_Actions.subnautica.MoveUp.GetState(SteamVR_Input_Sources.RightHand);
        }

        private const string LeftGripAnalogActionPath = "/actions/subnautica/in/LeftGripAnalog";
        private const string RightGripAnalogActionPath = "/actions/subnautica/in/RightGripAnalog";
        private const float SkeletonGripFallbackDeadZone = 0.33f;
        private static ulong leftGripAnalogActionHandle;
        private static ulong rightGripAnalogActionHandle;

        public static void GetSeaglideGripAnalog(out float leftGrip, out float rightGrip)
        {
            leftGrip = 0f;
            rightGrip = 0f;

            if (!IsSteamVrReady || InputLocked)
            {
                return;
            }

            // Read the physical Oculus/SteamVR grip axis directly. Skeleton finger curls are a
            // hand-pose estimate, not a grip axis; using them as throttle caused low-level motor
            // creep when the hand/tool orientation changed even though the user was not squeezing.
            bool leftAnalogAvailable = TryReadAnalogAction(
                LeftGripAnalogActionPath,
                ref leftGripAnalogActionHandle,
                out leftGrip
            );
            bool rightAnalogAvailable = TryReadAnalogAction(
                RightGripAnalogActionPath,
                ref rightGripAnalogActionHandle,
                out rightGrip
            );

            if (leftAnalogAvailable && rightAnalogAvailable)
            {
                return;
            }

            // Keep a degraded fallback for runtimes/controllers without the new scalar bindings.
            // Telemetry from Oculus Touch showed orientation-related skeleton values reaching ~0.30
            // with no intended throttle, so the fallback deliberately ignores the lower third.
            bool leftGripHeld = SteamVR_Actions.subnautica.MoveDown.GetState(SteamVR_Input_Sources.LeftHand);
            bool rightGripHeld = SteamVR_Actions.subnautica.MoveUp.GetState(SteamVR_Input_Sources.RightHand);

            if (!leftAnalogAvailable)
            {
                leftGrip = ReadSkeletonGripFallback("LeftHandSkeleton", leftGripHeld);
            }

            if (!rightAnalogAvailable)
            {
                rightGrip = ReadSkeletonGripFallback("RightHandSkeleton", rightGripHeld);
            }
        }

        private static bool TryReadAnalogAction(string actionPath, ref ulong actionHandle, out float value)
        {
            value = 0f;

            if (OpenVR.Input == null)
            {
                return false;
            }

            if (actionHandle == 0UL)
            {
                EVRInputError handleError = OpenVR.Input.GetActionHandle(actionPath, ref actionHandle);
                if (handleError != EVRInputError.None || actionHandle == 0UL)
                {
                    actionHandle = 0UL;
                    return false;
                }
            }

            InputAnalogActionData_t actionData = new InputAnalogActionData_t();
            uint actionDataSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
            EVRInputError readError = OpenVR.Input.GetAnalogActionData(
                actionHandle,
                ref actionData,
                actionDataSize,
                0UL
            );

            if (readError != EVRInputError.None || !actionData.bActive)
            {
                return false;
            }

            value = Mathf.Clamp01(actionData.x);
            return true;
        }

        private static float ReadSkeletonGripFallback(string actionName, bool booleanFallback)
        {
            SteamVR_Action_Skeleton skeleton = SteamVR_Input.GetSkeletonAction(actionName);
            if (skeleton == null)
            {
                return booleanFallback ? 1f : 0f;
            }

            float grip = Mathf.Clamp01(
                (skeleton.middleCurl + skeleton.ringCurl + skeleton.pinkyCurl) / 3f
            );

            if (grip <= SkeletonGripFallbackDeadZone)
            {
                return 0f;
            }

            return Mathf.InverseLerp(SkeletonGripFallbackDeadZone, 1f, grip);
        }

        public static bool TryGetLandGripJumpState(out bool held, out bool down, out bool up)
        {
            held = false;
            down = false;
            up = false;

            if (!IsSteamVrReady || InputLocked || !PhysicalSwimming.CanUseLandGripJump)
            {
                return false;
            }

            var leftGrip = SteamVR_Actions.subnautica.MoveDown;
            var rightGrip = SteamVR_Actions.subnautica.MoveUp;

            bool leftHeld = leftGrip.GetState(SteamVR_Input_Sources.LeftHand);
            bool rightHeld = rightGrip.GetState(SteamVR_Input_Sources.RightHand);
            held = leftHeld && rightHeld;

            down = held && (
                leftGrip.GetStateDown(SteamVR_Input_Sources.LeftHand)
                || rightGrip.GetStateDown(SteamVR_Input_Sources.RightHand)
            );

            up = !held && (
                leftGrip.GetStateUp(SteamVR_Input_Sources.LeftHand)
                || rightGrip.GetStateUp(SteamVR_Input_Sources.RightHand)
            );

            return true;
        }
    }

    // Implement Snap turning for the player
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetLookDelta))]
    public static class SnapTurning
    {
        public static void Postfix(ref Vector2 __result)
        {
            if (PhysicalSwimming.ShouldSuppressNativeTurnInput)
            {
                __result = Vector2.zero;
                SteamVrGameInput.SnapTurned = false;
                return;
            }

            bool isInVehicle = Player.main?.currentMountedVehicle != null;
            if (Settings.IsSnapTurningEnabled && !isInVehicle) {
                float lookX = __result.x;
                float absX = Mathf.Abs(lookX);
                float threshold = 0.5f;
                if (absX > threshold && !SteamVrGameInput.SnapTurned) {
                    __result.x = Settings.SnapTurningAngle * Mathf.Sign(lookX);
                    SteamVrGameInput.SnapTurned = true;
                } else  {
                    __result.x = 0;
                    if (absX <= threshold) {
                        SteamVrGameInput.SnapTurned = false;
                    }
                }
            }
        }
    }

    // The following three patches map the steamvr actions to the button states
    // TODO: They could be optimized by using a switch instead of the GetStateDown(string) lookup
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetButtonDown))]
    public static class SteamVrGetButtonDown
    {
        static bool Prefix(GameInput.Button action, ref bool __result)
        {
            if (SteamVrGameInput.ShouldIgnore(action))
            {
                return false;
            }

            if (action == GameInput.Button.Jump
                && SteamVrGameInput.TryGetLandGripJumpState(out _, out bool gripJumpDown, out _))
            {
                __result = gripJumpDown;
                return false;
            }

            String actionName = action.ToString();
            __result = SteamVR_Input.GetStateDown(actionName, SteamVR_Input_Sources.Any);
            if (PhysicalSwimming.ShouldSuppressNativeSwimmingAction(action))
            {
                __result = false;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetButtonUp))]
    public static class SteamVrGetButtonUp
    {
        static bool Prefix(GameInput.Button action, ref bool __result)
        {
            if (SteamVrGameInput.ShouldIgnore(action))
            {
                return false;
            }

            if (action == GameInput.Button.Jump
                && SteamVrGameInput.TryGetLandGripJumpState(out _, out _, out bool gripJumpUp))
            {
                __result = gripJumpUp;
                return false;
            }

            String actionName = action.ToString();
            __result = SteamVR_Input.GetStateUp(actionName, SteamVR_Input_Sources.Any);
            if (PhysicalSwimming.ShouldSuppressNativeSwimmingAction(action))
            {
                __result = false;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetButtonHeld))]
    public static class SteamVrGetButtonHeld
    {
        static bool Prefix(GameInput.Button action, ref bool __result)
        {
            if (SteamVrGameInput.ShouldIgnore(action))
            {
                return false;
            }

            if (action == GameInput.Button.Jump
                && SteamVrGameInput.TryGetLandGripJumpState(out bool gripJumpHeld, out _, out _))
            {
                __result = gripJumpHeld;
                return false;
            }

            String actionName = action.ToString();
            bool vanillaHeld = SteamVR_Input.GetState(actionName, SteamVR_Input_Sources.Any);
            if (PhysicalSwimming.ShouldSuppressNativeSwimmingAction(action))
            {
                vanillaHeld = false;
            }
            __result = PhysicalSwimming.CombineButtonHeld(action, vanillaHeld);
            return false;
        }
    }

    // Make the game believe to be controllerd by controllers only
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.IsPrimaryDeviceGamepad))]
    public static class ControllerOnly
    {
        public static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInputSystem), nameof(GameInput.PrimaryDevice), MethodType.Getter)]
    public static class GameInputSystemControllerOnly
    {
        public static bool Prefix(ref GameInput.Device __result)
        {
            __result = GameInput.Device.Controller;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetFloat))]
    public static class SteamVrGetFloat
    {
        public static bool Prefix(GameInput.Button action, ref float __result)
        {
            if (SteamVrGameInput.InputLocked || !SteamVrGameInput.IsSteamVrReady || VRHands.instance == null)
            {
                __result = 0.0f;
                return false;
            }
            Vector2 vec;
            bool isPressed = false;
            float value = 0.0f;
            switch (action)
            {
                case GameInput.Button.MoveForward:
                    vec = SteamVR_Actions.subnautica.Move.GetAxis(SteamVR_Input_Sources.Any);
                    value = vec.y > 0.0f ? vec.y : 0.0f;
                    break;
                case GameInput.Button.MoveBackward:
                    vec = SteamVR_Actions.subnautica.Move.GetAxis(SteamVR_Input_Sources.Any);
                    value = vec.y < 0.0f ? -vec.y : 0.0f;
                    break;
                case GameInput.Button.MoveRight:
                    vec = SteamVR_Actions.subnautica.Move.GetAxis(SteamVR_Input_Sources.Any);
                    value = vec.x > 0.0f ? vec.x : 0.0f;
                    break;
                case GameInput.Button.MoveLeft:
                    vec = SteamVR_Actions.subnautica.Move.GetAxis(SteamVR_Input_Sources.Any);
                    value = vec.x < 0.0f ? -vec.x : 0.0f;
                    break;
                case GameInput.Button.MoveUp:
                    isPressed = SteamVR_Actions.subnautica.MoveUp.GetState(SteamVR_Input_Sources.Any);
                    value = isPressed ? 1.0f : 0.0f;
                    break;
                case GameInput.Button.MoveDown:
                    isPressed = SteamVR_Actions.subnautica.MoveDown.GetState(SteamVR_Input_Sources.Any);
                    value = isPressed ? 1.0f : 0.0f;
                    break;
                case GameInput.Button.LookUp:
                    if (PhysicalSwimming.ShouldSuppressNativeTurnInput)
                    {
                        value = 0f;
                        break;
                    }
                    vec = SteamVR_Actions.subnautica.Look.GetAxis(SteamVR_Input_Sources.Any);
                    if (Settings.InvertYAxis)
                    {
                        value = vec.y < 0.0f ? -vec.y : 0.0f;
                    }
                    else
                    {
                        value = vec.y > 0.0f ? vec.y : 0.0f;
                    }
                    break;
                case GameInput.Button.LookDown:
                    if (PhysicalSwimming.ShouldSuppressNativeTurnInput)
                    {
                        value = 0f;
                        break;
                    }
                    vec = SteamVR_Actions.subnautica.Look.GetAxis(SteamVR_Input_Sources.Any);
                    if (Settings.InvertYAxis)
                    {
                        value = vec.y > 0.0f ? vec.y : 0.0f;
                    }
                    else
                    {
                        value = vec.y < 0.0f ? -vec.y : 0.0f;
                    }
                    break;
                case GameInput.Button.LookRight:
                    if (PhysicalSwimming.ShouldSuppressNativeTurnInput)
                    {
                        value = 0f;
                        break;
                    }
                    vec = SteamVR_Actions.subnautica.Look.GetAxis(SteamVR_Input_Sources.Any);
                    value = vec.x > 0.0f ? vec.x : 0.0f;
                    break;
                case GameInput.Button.LookLeft:
                    if (PhysicalSwimming.ShouldSuppressNativeTurnInput)
                    {
                        value = 0f;
                        break;
                    }
                    vec = SteamVR_Actions.subnautica.Look.GetAxis(SteamVR_Input_Sources.Any);
                    value = vec.x < 0.0f ? -vec.x : 0.0f;
                    break;
            }

            value = PhysicalSwimming.CombineFloatInput(action, value);

            // Stock controller input is limited to 1.0, but Physical Swimming deliberately lets
            // Fin Kick add on top of the independently capped hand-swim input. Preserve that
            // extra range for locomotion buttons instead of clipping 1.25/2.0 back to 1.0 here.
            bool locomotionAction =
                action == GameInput.Button.MoveForward
                || action == GameInput.Button.MoveBackward
                || action == GameInput.Button.MoveRight
                || action == GameInput.Button.MoveLeft
                || action == GameInput.Button.MoveUp
                || action == GameInput.Button.MoveDown;

            float limit = Settings.PhysicalSwimming && locomotionAction
                ? PhysicalSwimming.GetCurrentCombinedInputLimit()
                : 1f;
            __result = Mathf.Clamp(value, -limit, limit);

            return false;
        }
    }

    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetVector2))]
    public static class SteamVrGetVector2
    {
        public static bool Prefix(GameInput.Button action, ref Vector2 __result)
        {
            Vector2 vec = Vector2.zero;
            if (SteamVrGameInput.InputLocked || !SteamVrGameInput.IsSteamVrReady || VRHands.instance == null)
            {
                return false;
            }

            switch (action)
            {
                case GameInput.Button.Look:
                    if (PhysicalSwimming.ShouldSuppressNativeTurnInput)
                    {
                        vec = Vector2.zero;
                        break;
                    }

                    vec = SteamVR_Actions.subnautica.Look.GetAxis(SteamVR_Input_Sources.Any);

                    // TODO: Add new setting
                    Vector2 sensitivity = new Vector2(0.405f, 0.405f);

                    float mag = vec.magnitude;
                    Vector2 normVec = ((mag > 0f) ? (vec / mag) : Vector2.zero);
                    mag = Mathf.Pow(mag, 2f) * 500f;
                    vec = normVec * mag;
                    vec *= sensitivity * Time.deltaTime;

                    if (Settings.InvertYAxis)
                    {
                        vec.y = -vec.y;
                    }
                    break;
                case GameInput.Button.Move:
                    vec = SteamVR_Actions.subnautica.Move.GetAxis(SteamVR_Input_Sources.Any);
                    vec = PhysicalSwimming.CombineMoveAxis(vec);
                    break;
            }

            __result = vec;

            return false;
        }
    }


    // This makes GameInput.AnyKeyDown() return true incase any boolean action is pressed. Is needed for the intro skip and credits.
    // But hmm, where is the any key on the controllers? (https://www.youtube.com/watch?v=st6-DgWeuos)
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.AnyKeyDown), MethodType.Getter)]
    public static class SteamVRPressAnyKey
    {
        static void Postfix(ref bool __result)
        {
            if (__result)
            {
                return;
            }

            foreach (var action in SteamVR_Input.actionsBoolean)
            {
                if (action.GetStateDown(SteamVR_Input_Sources.Any))
                {
                    __result = true;
                    break;
                }
            }
        }
    }

    // This makes it so the crafting menu from the fabricators actually use the controller buttons
    // [HarmonyPatch(typeof(uGUI_CraftingMenu), "OnPointerClick")]
    [HarmonyPatch(typeof(uGUI_CraftingMenu))]
    public static class CraftingMenuUseControllerButtons
    {
        public static MethodBase TargetMethod()
        {
            var type = typeof(uGUI_CraftingMenu);
            return AccessTools.FirstMethod(type, method => method.Name.Contains("OnPointerClick"));
        }

        static bool Prefix(ref bool __result, uGUI_CraftingMenu __instance, uGUI_ItemIcon icon, int button)
        {
            Mod.logger.LogInfo($"uGUI_CraftingMenu OnPointerClick called {button} ");
            if (__instance.interactable)
            {
                uGUI_CraftingMenu.Node node = __instance.GetNode(icon);
                switch (button)
                {
                    case 0: // uGUI.button0 => UISubmit
                        __instance.Action(node);
                        __result = true;
                        break;
                    case 1: // uGUI.button1 => UICancel
                        __instance.Deselect();
                        __result = true;
                        break;
                    case 2: // uGUI.button2 => UIClear => Pinning
                        if (node.action == TreeAction.Craft)
                        {
                            TechType techType = node.techType;
                            if (CrafterLogic.IsCraftRecipeUnlocked(techType))
                            {
                                PinManager.TogglePin(techType);
                            }
                        }
                        __result = true;
                        break;
                    default:
                        __result = false;
                        break;
                }
            }
            return false;
        }
    }

    // Make the builder gun rotation use custom steamvr actions
    [HarmonyPatch(typeof(Builder), nameof(Builder.CalculateAdditiveRotationFromInput))]
    public static class BuilderRotateUseCustomActions
    {
        static bool Prefix(float additiveRotation, ref float __result)
        {
            if (SteamVR_Actions.subnautica_BuilderRotateRight.GetState(SteamVR_Input_Sources.Any))
            {
                additiveRotation = MathExtensions.RepeatAngle(additiveRotation - Builder.GetDeltaTimeForAdditiveRotation() * Builder.additiveRotationSpeed);
            }
            else if (SteamVR_Actions.subnautica_BuilderRotateLeft.GetState(SteamVR_Input_Sources.Any))
            {
                additiveRotation = MathExtensions.RepeatAngle(additiveRotation + Builder.GetDeltaTimeForAdditiveRotation() * Builder.additiveRotationSpeed);
            }
            __result = additiveRotation;
            return false;
        }
    }

    // Rotate base pieces using custom steamvr actions
    [HarmonyPatch(typeof(Builder), nameof(Builder.UpdateRotation))]
    public static class BuilderUpdateRotationUseCustomActions
    {
        static bool Prefix(int max, ref bool __result)
        {
            if (SteamVR_Actions.subnautica_BuilderRotateRight.GetStateDown(SteamVR_Input_Sources.Any))
            {
                Builder.lastRotation = (Builder.lastRotation + max - 1) % max;
                __result = true;
                return false;
            }
            if (SteamVR_Actions.subnautica_BuilderRotateLeft.GetStateDown(SteamVR_Input_Sources.Any))
            {
                Builder.lastRotation = (Builder.lastRotation + 1) % max;
                __result = true;
                return false;
            }
            __result = false;
            return false;
        }
    }

    // Force the gaze based cursor, since we use it for the laserpointer
    [HarmonyPatch(typeof(VROptions), nameof(VROptions.GetUseGazeBasedCursor))]
    public static class ForceGazeBasedCursor
    {
        public static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }

    // Use Action vector as scroll delta to enable scrolling in the UI
    [HarmonyPatch(typeof(Input), nameof(Input.mouseScrollDelta), MethodType.Getter)]
    static class EmulateUnityScrollDelta
    {
        static bool Prefix(ref Vector2 __result)
        {
            __result = SteamVrGameInput.GetScrollDelta();
            return false;
        }
    }

#if false 
    // Use the ui camera for tooltip scaling instead of controller event camera
    [HarmonyPatch(typeof(uGUI_Tooltip), nameof(uGUI_Tooltip.ExtractParams))]
    static class UseCameraForTooltapScaling
    {
        public static Camera GetUiCamera()
        {
            return VRCameraRig.instance.uiCamera;
        }

        public static void Postfix(uGUI_Tooltip __instance, ref bool __result) {
            if (__result) {
                Transform tf = GetUiCamera().transform;
                __instance.aimingPosition = tf.position;
                __instance.aimingForward = tf.forward;
            }
        }

    }

    // Use the ui camera for tooltip scaling instead of controller event camera
    [HarmonyPatch(typeof(uGUI_Tooltip), nameof(uGUI_Tooltip.UpdatePosition))]
    static class ScaleDownTooltip
    {
        public static PDA pda;
        public const float PDA_ScaleFactor = 0.5f;

        public static float GetTooltipScaler()
        {
            if (pda == null) {
                pda = Player.main.GetPDA();
            }
            return pda.isInUse ? PDA_ScaleFactor : 1.0f;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var m = new CodeMatcher(instructions);
            m.MatchForward(false, new CodeMatch[] {
                new CodeMatch(OpCodes.Stloc_0)
            }).Insert(new CodeInstruction[] {
                CodeInstruction.Call(typeof(ScaleDownTooltip), nameof(ScaleDownTooltip.GetTooltipScaler)),
                new CodeInstruction(OpCodes.Mul),
            });
            return m.InstructionEnumeration();
        }
    }
#endif

    // Don't scale the tooltips with the controller distance
    [HarmonyPatch(typeof(uGUI_Tooltip), nameof(uGUI_Tooltip.UpdatePosition))]
    [HarmonyDebug]
    static class DontScaleToolTips
    {
        public static PDA pda;
        public const float PDA_ScaleFactor = 0.25f;

        public static float GetTooltipScaler()
        {
            if (pda == null)
            {
                pda = Player.main.GetPDA();
            }
            return pda.isInUse ? PDA_ScaleFactor : 1.0f;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var m = new CodeMatcher(instructions);
            var pos = m.MatchForward(false, new CodeMatch[] {
                // new CodeMatch(OpCodes.Stloc_0)
                new CodeMatch(ci => ci.Calls(AccessTools.DeclaredMethod(typeof(Vector3), nameof(Vector3.Dot))))
            }).Pos;
            m.Start().RemoveInstructionsInRange(0, pos).Insert(new CodeInstruction[] {
                CodeInstruction.Call(typeof(DontScaleToolTips), nameof(DontScaleToolTips.GetTooltipScaler)),
            });
            return m.InstructionEnumeration();
        }
    }

    #endregion

}
