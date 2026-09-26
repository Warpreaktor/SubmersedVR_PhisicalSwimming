using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SubmersedVR.Music
{
    internal sealed class MusicStation
    {
        public string Name { get; }
        public List<string> Tracks { get; }

        public MusicStation(string name, IEnumerable<string> tracks)
        {
            Name = name;
            Tracks = tracks.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    internal static class MusicLibrary
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".ogg", ".flac", ".wma", ".aif", ".aiff"
        };

        public static string ResolveLibraryPath()
        {
            if (!string.IsNullOrWhiteSpace(MusicPlayerSettings.LibraryPath))
            {
                return Environment.ExpandEnvironmentVariables(MusicPlayerSettings.LibraryPath.Trim());
            }

            return Path.Combine(Paths.PluginPath, "SubmersedVR", "Music");
        }

        public static List<MusicStation> Scan()
        {
            string root = ResolveLibraryPath();
            Directory.CreateDirectory(root);

            var allFiles = EnumerateMusicFiles(root).ToList();
            var stations = new List<MusicStation>();

            if (allFiles.Count > 0)
            {
                stations.Add(new MusicStation("All Music", allFiles));
            }

            foreach (string directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var stationFiles = EnumerateMusicFiles(directory).ToList();
                if (stationFiles.Count == 0)
                {
                    continue;
                }

                stations.Add(new MusicStation(Path.GetFileName(directory), stationFiles));
            }

            return stations;
        }

        private static IEnumerable<string> EnumerateMusicFiles(string root)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Mod.logger?.LogWarning($"Music Player could not scan '{root}': {ex.Message}");
                yield break;
            }

            foreach (string file in files)
            {
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }
        }
    }
}
