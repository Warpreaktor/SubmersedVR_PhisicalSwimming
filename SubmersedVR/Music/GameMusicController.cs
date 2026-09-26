using FMOD;
using FMOD.Studio;
using FMODUnity;
using UnityEngine;

namespace SubmersedVR.Music
{
    /// <summary>
    /// Temporarily overrides Subnautica's runtime music VCA while the custom radio is active.
    /// The native setting itself is deliberately not persisted as zero: if the game or mod crashes,
    /// the user's saved music volume remains untouched.
    /// </summary>
    internal static class GameMusicController
    {
        private const string MusicVcaPath = "vca:/Music";
        private static VCA musicVca;
        private static bool hasMusicVca;
        private static bool mutedByUs;
        private static float previousMusicVolume = 1f;

        public static void Mute()
        {
            if (!TryResolveMusicVca())
            {
                Mod.logger?.LogWarning($"Music Player could not resolve Subnautica music VCA '{MusicVcaPath}'. Game music will not be muted.");
                return;
            }

            if (!mutedByUs)
            {
                RESULT readResult = musicVca.getVolume(out float currentVolume);
                if (readResult != RESULT.OK)
                {
                    Mod.logger?.LogWarning($"Music Player could not read Subnautica music VCA volume: {readResult}");
                    return;
                }

                previousMusicVolume = Mathf.Clamp01(currentVolume);
                mutedByUs = true;
                Mod.logger?.LogInfo($"Music Player captured native music volume {previousMusicVolume:0.###} and is muting '{MusicVcaPath}'.");
            }

            ApplyMutedVolume();
        }

        public static void Restore()
        {
            if (!mutedByUs)
            {
                return;
            }

            try
            {
                if (!TryResolveMusicVca())
                {
                    Mod.logger?.LogWarning($"Music Player could not restore Subnautica music because '{MusicVcaPath}' is unavailable.");
                    return;
                }

                RESULT result = musicVca.setVolume(previousMusicVolume);
                if (result != RESULT.OK)
                {
                    Mod.logger?.LogWarning($"Music Player failed to restore Subnautica music VCA volume: {result}");
                    return;
                }

                Mod.logger?.LogInfo($"Music Player restored native music volume to {previousMusicVolume:0.###}.");
            }
            finally
            {
                mutedByUs = false;
            }
        }

        private static void ApplyMutedVolume()
        {
            RESULT result = musicVca.setVolume(0f);
            if (result != RESULT.OK)
            {
                Mod.logger?.LogWarning($"Music Player failed to mute Subnautica music VCA: {result}");
            }
        }

        private static bool TryResolveMusicVca()
        {
            if (hasMusicVca && musicVca.isValid())
            {
                return true;
            }

            hasMusicVca = false;
            musicVca = default(VCA);

            RESULT result = RuntimeManager.StudioSystem.getVCA(MusicVcaPath, out VCA candidate);
            if (result != RESULT.OK || !candidate.isValid())
            {
                return false;
            }

            musicVca = candidate;
            hasMusicVca = true;
            Mod.logger?.LogInfo($"Music Player using Subnautica music VCA '{MusicVcaPath}'.");
            return true;
        }
    }
}
