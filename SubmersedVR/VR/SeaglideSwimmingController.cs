using HarmonyLib;
using UnityEngine;

namespace SubmersedVR
{
    /// <summary>
    /// Owns only the control intent of the Seaglide swimming mode.
    /// PhysicalSwimming still owns body inertia, water resistance and collision reconciliation.
    /// </summary>
    internal static class SeaglideSwimmingController
    {
        private static Transform movementReference;
        private static bool referenceInitialized;
        private static Seaglide currentSeaglide;
        private const float GripNoiseDeadZone = 0.04f;

        private static bool leftGripHeld;
        private static bool rightGripHeld;
        private static float leftGripAnalog;
        private static float rightGripAnalog;
        private static float throttle;
        private static bool propulsionActive;
        private static Vector3 toolDirection;

        internal static bool IsEquipped => currentSeaglide != null;
        internal static bool IsControlling(Seaglide seaglide) =>
            currentSeaglide != null && currentSeaglide == seaglide;
        internal static bool LeftGripHeld => leftGripHeld;
        internal static bool RightGripHeld => rightGripHeld;
        internal static float LeftGripAnalog => leftGripAnalog;
        internal static float RightGripAnalog => rightGripAnalog;
        internal static bool PropulsionActive => propulsionActive;
        internal static float Throttle => throttle;
        internal static Vector3 ToolDirection => toolDirection;
        internal static Transform MovementReference => movementReference;

        internal static void Update(
            Seaglide seaglide,
            bool playerIsSwimming,
            bool surfaceBridgeActive,
            Vector3 storedWorldVelocity)
        {
            currentSeaglide = seaglide;
            SteamVrGameInput.GetSeaglideGripAnalog(out leftGripAnalog, out rightGripAnalog);

            // Each hand contributes at most half of the motor power. One fully squeezed grip is
            // therefore 50% throttle, while squeezing both to 100% produces full Seaglide power.
            // A tiny dead zone removes XR sensor noise without reshaping the rest of the curve.
            leftGripAnalog = ApplyGripNoiseDeadZone(leftGripAnalog);
            rightGripAnalog = ApplyGripNoiseDeadZone(rightGripAnalog);
            leftGripHeld = leftGripAnalog > 0f;
            rightGripHeld = rightGripAnalog > 0f;
            throttle = Mathf.Clamp01((leftGripAnalog + rightGripAnalog) * 0.5f);

            Transform toolReference = VRCameraRig.GetTargetTansform();
            toolDirection = ResolveToolDirection(toolReference);

            if (!playerIsSwimming && surfaceBridgeActive)
            {
                // At the ocean surface the bridge exists only to let the player get back under.
                // A Seaglide may point horizontally or downward here, but it must not become a
                // handheld jetpack merely because Player.IsSwimming() has already flipped false.
                toolDirection.y = Mathf.Min(0f, toolDirection.y);
                if (toolDirection.sqrMagnitude > 0.0001f)
                {
                    toolDirection.Normalize();
                }
            }

            bool swimmingAvailable = playerIsSwimming || surfaceBridgeActive;
            propulsionActive =
                currentSeaglide != null
                && swimmingAvailable
                && currentSeaglide.HasEnergy()
                && throttle > 0f
                && toolDirection.sqrMagnitude > 0.0001f;

            EnsureMovementReference();
            if (movementReference == null)
            {
                propulsionActive = false;
                return;
            }

            if (toolReference != null)
            {
                movementReference.position = toolReference.position;
            }

            if (propulsionActive && toolReference != null)
            {
                // The Seaglide aim transform is already calibrated by VRHands for the equipped tool.
                // Follow its forward direction only while the motor is driven. Once the grips are
                // released, changing hand orientation cannot steer the stored inertial glide.
                SetReferenceForward(toolDirection, toolReference);
                return;
            }

            if (storedWorldVelocity.sqrMagnitude > 0.000001f)
            {
                SetReferenceForward(storedWorldVelocity.normalized, toolReference);
                return;
            }

            if (!referenceInitialized && toolReference != null)
            {
                movementReference.rotation = toolReference.rotation;
                referenceInitialized = true;
            }
        }

        internal static void Reset()
        {
            currentSeaglide = null;
            leftGripHeld = false;
            rightGripHeld = false;
            leftGripAnalog = 0f;
            rightGripAnalog = 0f;
            throttle = 0f;
            propulsionActive = false;
            toolDirection = Vector3.zero;
            referenceInitialized = false;
        }

        private static float ApplyGripNoiseDeadZone(float value)
        {
            value = Mathf.Clamp01(value);
            return value <= GripNoiseDeadZone ? 0f : value;
        }

        private static Vector3 ResolveToolDirection(Transform toolReference)
        {
            if (toolReference == null)
            {
                return Vector3.zero;
            }

            Vector3 forward = toolReference.forward;
            return forward.sqrMagnitude > 0.0001f
                ? forward.normalized
                : Vector3.zero;
        }

        private static void EnsureMovementReference()
        {
            if (movementReference != null)
            {
                return;
            }

            GameObject referenceObject = new GameObject("SeaglideMovementReference");
            referenceObject.hideFlags = HideFlags.HideAndDontSave;
            movementReference = referenceObject.transform;
        }

        private static void SetReferenceForward(Vector3 forward, Transform preferredOrientation)
        {
            if (forward.sqrMagnitude <= 0.0001f || movementReference == null)
            {
                return;
            }

            Vector3 up = preferredOrientation != null
                ? preferredOrientation.up
                : Vector3.up;

            // Quaternion.LookRotation needs an up vector that is not parallel to forward.
            up -= Vector3.Project(up, forward);
            if (up.sqrMagnitude <= 0.0001f)
            {
                up = Vector3.up - Vector3.Project(Vector3.up, forward);
            }
            if (up.sqrMagnitude <= 0.0001f)
            {
                up = Vector3.forward - Vector3.Project(Vector3.forward, forward);
            }

            movementReference.rotation = Quaternion.LookRotation(forward, up.normalized);
            referenceInitialized = true;
        }

        // Seaglide motor consumption is separate from ToggleLights energy consumption in vanilla.
        // During Physical Swimming, only non-zero analog grip throttle is allowed to power the
        // motor. Stored inertial movement after release must not continue draining the battery.
        [HarmonyPatch(typeof(Seaglide), "UpdateEnergy")]
        private static class SeaglideMotorEnergyPatch
        {
            private static bool Prefix(Seaglide __instance)
            {
                return PhysicalSwimming.ShouldConsumeSeaglideMotorEnergy(__instance);
            }
        }
    }
}
