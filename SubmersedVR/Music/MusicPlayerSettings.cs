using BepInEx;
using System;
using System.IO;
using System.Reflection;

namespace SubmersedVR.Music
{
    internal static class MusicPlayerSettings
    {
        public static float Volume = 0.65f;
        public static bool Shuffle = true;

        // Empty uses BepInEx/plugins/SubmersedVR/Music. A custom path can be placed in the
        // generated BepInEx/config/SubmersedVR/music-library-path.txt file.
        public static string LibraryPath = string.Empty;

        private static string PathOverrideFile => Path.Combine(Paths.ConfigPath, "SubmersedVR", "music-library-path.txt");

        public static void InitializeLibraryPathOverride()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOverrideFile));
                if (!File.Exists(PathOverrideFile))
                {
                    string defaultPath = Path.Combine(Paths.PluginPath, "SubmersedVR", "Music");
                    File.WriteAllLines(PathOverrideFile, new[]
                    {
                        "# Optional custom music-library folder for SubmersedVR Radio.",
                        "# Leave the path line empty to use BepInEx/plugins/SubmersedVR/Music.",
                        "# Example: D:\\Music\\Ambient",
                        defaultPath
                    });
                    return;
                }

                foreach (string line in File.ReadAllLines(PathOverrideFile))
                {
                    string value = line.Trim();
                    if (value.Length == 0 || value.StartsWith("#"))
                    {
                        continue;
                    }

                    LibraryPath = value;
                    break;
                }
            }
            catch (Exception ex)
            {
                Mod.logger?.LogWarning($"Music Player could not read library path override: {ex.Message}");
            }
        }

        public static void Serialize(GameSettings.ISerializer serializer)
        {
            const string ns = "SubmersedVR/MusicPlayer";
            foreach (FieldInfo field in typeof(MusicPlayerSettings).GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                object value = field.GetValue(null);
                switch (value)
                {
                    case bool boolValue:
                        field.SetValue(null, serializer.Serialize($"{ns}/{field.Name}", boolValue));
                        break;
                    case float floatValue:
                        field.SetValue(null, serializer.Serialize($"{ns}/{field.Name}", floatValue));
                        break;
                    // LibraryPath is loaded from the explicit text file so the user can point at
                    // any large existing collection without typing a Windows path in VR.
                    case string:
                        break;
                }
            }
        }


    }
}
