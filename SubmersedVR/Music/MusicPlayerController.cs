using FMOD;
using FMODUnity;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SubmersedVR.Music
{
    internal sealed class MusicPlayerController : MonoBehaviour
    {
        public static MusicPlayerController instance;

        private readonly global::System.Random random = new global::System.Random();
        private List<MusicStation> stations = new List<MusicStation>();
        private int stationIndex;
        private int trackIndex = -1;

        private Sound sound;
        private Channel channel;
        private bool hasSound;
        private bool hasChannel;
        private bool paused;
        private bool radioActive;

        public bool IsRadioActive => radioActive;
        public bool IsPaused => paused;
        public int StationCount => stations.Count;
        public string LibraryPath => MusicLibrary.ResolveLibraryPath();
        public string CurrentStationName => stations.Count == 0 ? "No stations" : stations[stationIndex].Name;
        public string CurrentTrackName
        {
            get
            {
                if (stations.Count == 0 || trackIndex < 0 || trackIndex >= stations[stationIndex].Tracks.Count)
                {
                    return "No track selected";
                }
                return Path.GetFileNameWithoutExtension(stations[stationIndex].Tracks[trackIndex]);
            }
        }

        private void Awake()
        {
            instance = this;
            Rescan();
        }

        private void Update()
        {
            if (!hasChannel || paused)
            {
                return;
            }

            RESULT result = channel.isPlaying(out bool isPlaying);
            if (result != RESULT.OK || !isPlaying)
            {
                Mod.logger?.LogInfo($"[MUSIC DIAG] Channel ended or became invalid: result={result} isPlaying={isPlaying} track='{CurrentTrackName}' hasChannel={hasChannel}");
                if (hasChannel)
                {
                    RESULT stopResult = channel.stop();
                    Mod.logger?.LogInfo($"[MUSIC DIAG] Defensive channel.stop after playback-end detection: result={stopResult}");
                }
                CleanupPlaybackObjects();
                if (radioActive)
                {
                    PlayNext();
                }
            }
        }

        private void OnDestroy()
        {
            StopRadio();
            if (instance == this)
            {
                instance = null;
            }
        }

        public void Rescan()
        {
            string oldStation = CurrentStationName;
            bool resume = radioActive;
            StopPlaybackOnly();
            stations = MusicLibrary.Scan();
            trackIndex = -1;

            if (stations.Count == 0)
            {
                stationIndex = 0;
                radioActive = false;
                GameMusicController.Restore();
                Mod.logger?.LogInfo($"Music Player found no supported audio files in '{LibraryPath}'.");
                return;
            }

            int matchingStation = stations.FindIndex(station => string.Equals(station.Name, oldStation, StringComparison.OrdinalIgnoreCase));
            stationIndex = matchingStation >= 0 ? matchingStation : Mathf.Clamp(stationIndex, 0, stations.Count - 1);
            Mod.logger?.LogInfo($"Music Player found {stations.Count} stations and {CountTracks()} indexed tracks in '{LibraryPath}'.");

            if (resume)
            {
                PlayNext();
            }
        }

        public void TogglePlayPause()
        {
            if (!radioActive)
            {
                StartRadio();
                return;
            }

            if (!hasChannel)
            {
                PlayNext();
                return;
            }

            paused = !paused;
            channel.setPaused(paused);

            // Pausing the custom track must not resume Subnautica's background music.
            // Only an explicit Stop (or leaving the world) restores the native music volume.
            GameMusicController.Mute();
        }

        public void StopRadio()
        {
            radioActive = false;
            paused = false;
            StopPlaybackOnly();
            GameMusicController.Restore();
        }

        public void StopForWorldTransition()
        {
            StopRadio();
            stationIndex = 0;
            trackIndex = -1;
        }

        public void StartRadio()
        {
            if (stations.Count == 0)
            {
                Rescan();
            }

            if (stations.Count == 0)
            {
                ErrorMessage.AddMessage($"No music found. Add files to: {LibraryPath}");
                return;
            }

            radioActive = true;
            paused = false;
            GameMusicController.Mute();
            PlayNext();
        }

        public void PlayNext()
        {
            if (stations.Count == 0)
            {
                return;
            }

            var tracks = stations[stationIndex].Tracks;
            if (tracks.Count == 0)
            {
                return;
            }

            if (MusicPlayerSettings.Shuffle && tracks.Count > 1)
            {
                int next;
                do
                {
                    next = random.Next(tracks.Count);
                }
                while (next == trackIndex);
                trackIndex = next;
            }
            else
            {
                trackIndex = (trackIndex + 1 + tracks.Count) % tracks.Count;
            }

            PlayTrack(tracks[trackIndex]);
        }

        public void PlayPrevious()
        {
            if (stations.Count == 0)
            {
                return;
            }

            var tracks = stations[stationIndex].Tracks;
            if (tracks.Count == 0)
            {
                return;
            }

            trackIndex = trackIndex < 0 ? 0 : (trackIndex - 1 + tracks.Count) % tracks.Count;
            PlayTrack(tracks[trackIndex]);
        }

        public void NextStation()
        {
            ChangeStation(1);
        }

        public void PreviousStation()
        {
            ChangeStation(-1);
        }

        public void SetVolume(float volume)
        {
            MusicPlayerSettings.Volume = Mathf.Clamp01(volume);
            if (hasChannel)
            {
                channel.setVolume(MusicPlayerSettings.Volume);
            }
        }

        private void ChangeStation(int delta)
        {
            if (stations.Count == 0)
            {
                return;
            }

            stationIndex = (stationIndex + delta + stations.Count) % stations.Count;
            trackIndex = -1;
            if (radioActive)
            {
                PlayNext();
            }
        }

        private void PlayTrack(string path)
        {
            StopPlaybackOnly();
            GameMusicController.Mute();

            Mod.logger?.LogInfo($"[MUSIC DIAG] PlayTrack START requested: path='{path}' station='{CurrentStationName}' previousHasChannel={hasChannel} previousHasSound={hasSound}");
            RESULT createResult = RuntimeManager.CoreSystem.createStream(path, MODE.DEFAULT | MODE._2D, out sound);
            if (createResult != RESULT.OK)
            {
                Mod.logger?.LogWarning($"Music Player could not open '{path}': {createResult}");
                ErrorMessage.AddMessage($"Could not play: {Path.GetFileName(path)}");
                CleanupPlaybackObjects();
                radioActive = false;
                GameMusicController.Restore();
                return;
            }
            hasSound = true;

            RESULT playResult = RuntimeManager.CoreSystem.playSound(sound, default(ChannelGroup), false, out channel);
            if (playResult != RESULT.OK)
            {
                Mod.logger?.LogWarning($"Music Player could not start '{path}': {playResult}");
                CleanupPlaybackObjects();
                radioActive = false;
                GameMusicController.Restore();
                return;
            }

            hasChannel = true;
            paused = false;
            radioActive = true;
            RESULT setVolumeResult = channel.setVolume(MusicPlayerSettings.Volume);
            Mod.logger?.LogInfo($"[MUSIC DIAG] PlayTrack STARTED: track='{CurrentTrackName}' createResult={createResult} playResult={playResult} volume={MusicPlayerSettings.Volume:0.###} setVolumeResult={setVolumeResult}");
        }

        private void StopPlaybackOnly()
        {
            if (hasChannel)
            {
                RESULT stopResult = channel.stop();
                Mod.logger?.LogInfo($"[MUSIC DIAG] StopPlaybackOnly channel.stop: result={stopResult} track='{CurrentTrackName}'");
            }
            CleanupPlaybackObjects();
        }

        private void CleanupPlaybackObjects()
        {
            hasChannel = false;
            if (hasSound)
            {
                sound.release();
            }
            hasSound = false;
            sound = default(Sound);
            channel = default(Channel);
        }

        private int CountTracks()
        {
            int count = 0;
            foreach (var station in stations)
            {
                count += station.Tracks.Count;
            }
            return count;
        }

        [HarmonyPatch(typeof(uGUI_OptionsPanel), "OnEnable")]
        private static class OptionsPanelOnEnableMusicPatch
        {
            public static void Postfix()
            {
                if (instance?.IsRadioActive == true)
                {
                    GameMusicController.Mute();
                }
            }
        }

        [HarmonyPatch(typeof(uGUI_OptionsPanel), "OnDisable")]
        private static class OptionsPanelOnDisableMusicPatch
        {
            public static void Postfix()
            {
                if (instance?.IsRadioActive == true)
                {
                    GameMusicController.Mute();
                }
            }
        }

        [HarmonyPatch(typeof(IngameMenu), "QuitToMainMenuAsync")]
        private static class QuitToMainMenuPatch
        {
            public static void Prefix()
            {
                instance?.StopForWorldTransition();
            }
        }
    }
}
