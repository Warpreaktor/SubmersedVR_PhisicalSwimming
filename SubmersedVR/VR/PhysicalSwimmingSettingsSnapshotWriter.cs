using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace SubmersedVR
{
    static class PhysicalSwimmingSettingsSnapshotWriter
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static void WriteTelemetrySnapshot(TextWriter writer)
        {
            if (writer == null)
            {
                return;
            }

            FieldInfo[] fields = typeof(Settings).GetFields(BindingFlags.Static | BindingFlags.Public);
            Array.Sort(fields, (left, right) => left.MetadataToken.CompareTo(right.MetadataToken));

            writer.WriteLine("# physical_swimming_settings_begin");
            writer.WriteLine($"# snapshot_time = {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (FieldInfo field in fields)
            {
                if (!IsPhysicalSwimmingField(field))
                {
                    continue;
                }

                writer.Write("# ");
                writer.Write(field.Name);
                writer.Write(" = ");
                writer.WriteLine(FormatReadableValue(field.GetValue(null)));
            }
            writer.WriteLine($"# PhysicalSwimmingBodyVelocityDeadZone = {Settings.PhysicalSwimmingBodyVelocityDeadZone.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingPlayerVelocityDeadZone = {Settings.PhysicalSwimmingPlayerVelocityDeadZone.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingManeuverabilitySteeringAssist = {PhysicalSwimming.ManeuverabilitySteeringAssistFactor.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingManeuverabilityReverseForceThreshold = {PhysicalSwimming.ManeuverabilityReverseForceThreshold.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingManeuverabilityWeakBrakeFloor = {PhysicalSwimming.ManeuverabilityWeakBrakeFloor.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingManeuverabilityMaxTurnRate = {PhysicalSwimming.ManeuverabilityMaxTurnRate.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingPalmDragDeadZone = {PhysicalSwimming.PalmDragDeadZone.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingPalmDragExponent = {PhysicalSwimming.PalmDragExponent.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingPalmEdgeDrag = {PhysicalSwimming.PalmEdgeDrag.ToString("0.########", Invariant)}");
            writer.WriteLine($"# PhysicalSwimmingBackOfHandDragMultiplier = {PhysicalSwimming.BackOfHandDragMultiplier.ToString("0.########", Invariant)}");
            writer.WriteLine("# PhysicalSwimmingFinKick = true");
            writer.WriteLine($"# PhysicalSwimmingDebugRecording = {(Settings.PhysicalSwimmingDebugRecording ? "true" : "false")}");
            writer.WriteLine("# physical_swimming_settings_end");
            writer.WriteLine("# CSV data begins below. Parsers may ignore metadata lines with comment character '#'.");
        }

        private static bool IsPhysicalSwimmingField(FieldInfo field)
        {
            return field != null
                && field.IsStatic
                && field.IsPublic
                && field.Name.StartsWith("PhysicalSwimming", StringComparison.Ordinal);
        }

        private static string FormatReadableValue(object value)
        {
            switch (value)
            {
                case bool boolValue:
                    return boolValue ? "true" : "false";
                case float floatValue:
                    return floatValue.ToString("0.########", Invariant);
                case int intValue:
                    return intValue.ToString(Invariant);
                case string stringValue:
                    return stringValue ?? string.Empty;
                default:
                    return Convert.ToString(value, Invariant);
            }
        }
    }
}
