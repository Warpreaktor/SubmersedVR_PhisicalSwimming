namespace SubmersedVR
{
    internal enum SwimmingControlMode
    {
        HandSwimming = 0,
        Seaglide = 1
    }

    internal static class SwimmingControlModeResolver
    {
        internal static SwimmingControlMode Resolve(out Seaglide seaglide)
        {
            seaglide = null;

            if (Inventory.main == null)
            {
                return SwimmingControlMode.HandSwimming;
            }

            Pickupable held = Inventory.main.GetHeld();
            if (!held || !held.gameObject)
            {
                return SwimmingControlMode.HandSwimming;
            }

            seaglide = held.GetComponent<Seaglide>();
            return seaglide != null
                ? SwimmingControlMode.Seaglide
                : SwimmingControlMode.HandSwimming;
        }
    }
}
