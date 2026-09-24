using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Video;

namespace YoutubeOnTV
{
    /// <summary>
    /// Owns the VideoPlayer on the ship's TV. Plays local files only: VideoDownloader turns
    /// every video into an MP4 on disk first.
    /// </summary>
    public class TVController : MonoBehaviour
    {
        public static TVController Instance { get; private set; }

        public VideoPlayer videoPlayer;
        private VideoPlayer vanillaVideoPlayer;
        private RenderTexture renderTexture;
        private AudioSource tvAudioSource;
        private TVScript tvScript;
        private ManualLogSource logger;

        private bool isPreparing;
        private double pendingSeek = -1;
        private float prepareStartedAt;
        private int prepareAttempts;

        // Media Foundation under Proton occasionally aborts a read (E_ABORT) and the
        // VideoPlayer then never finishes preparing and never raises an error.
        private const float PREPARE_TIMEOUT = 6f;
        private const int MAX_PREPARE_ATTEMPTS = 3;

        /// <summary>
        /// The file currently loaded into the player, or null.
        /// </summary>
        public string LoadedPath { get; private set; }

        public AudioSource AudioSource => tvAudioSource;

        private void Awake()
        {
            Instance = this;
            logger = YoutubeOnTVBase.Log;

            tvScript = gameObject.GetComponent<TVScript>();
            if (tvScript == null)
            {
                logger.LogError("TVScript not found on this GameObject!");
                return;
            }

            tvAudioSource = tvScript.tvSFX;

            vanillaVideoPlayer = tvScript.video;
            if (vanillaVideoPlayer != null)
            {
                renderTexture = vanillaVideoPlayer.targetTexture;
                vanillaVideoPlayer.Stop();
                vanillaVideoPlayer.enabled = false;
            }
            else
            {
                logger.LogError("Vanilla VideoPlayer not found!");
            }

            videoPlayer = gameObject.AddComponent<VideoPlayer>();
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = false;
            videoPlayer.source = VideoSource.Url;
            videoPlayer.skipOnDrop = true;
            videoPlayer.controlledAudioTrackCount = 1;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            videoPlayer.SetTargetAudioSource(0, tvAudioSource);
            videoPlayer.targetTexture = renderTexture;

            tvScript.video = videoPlayer;

            videoPlayer.prepareCompleted += OnVideoPrepared;
            videoPlayer.loopPointReached += OnVideoEnd;
            videoPlayer.errorReceived += OnVideoError;
            videoPlayer.seekCompleted += vp => logger.LogInfo($"Seek completed at {vp.time:0.0}s");

            logger.LogInfo($"TVController attached (screen {renderTexture?.width}x{renderTexture?.height})");
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Loads and plays a local video file, optionally starting at a given position.
        /// </summary>
        public void PlayFile(string filePath, double startAt = 0)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                logger.LogError($"Cannot play video: file not found at {filePath}");
                OnVideoError(videoPlayer, "file not found: " + filePath);
                return;
            }

            logger.LogInfo($"Playing {System.IO.Path.GetFileName(filePath)}" + (startAt > 0 ? $" from {startAt:0.0}s" : ""));

            videoPlayer.Stop();
            LoadedPath = filePath;
            pendingSeek = startAt > 0 ? startAt : -1;
            videoPlayer.url = "file://" + filePath;
            prepareAttempts = 0;
            StartPrepare();
        }

        private void StartPrepare()
        {
            isPreparing = true;
            prepareAttempts++;
            prepareStartedAt = UnityEngine.Time.realtimeSinceStartup;
            videoPlayer.Prepare();
        }

        private void Update()
        {
            if (!isPreparing || UnityEngine.Time.realtimeSinceStartup - prepareStartedAt < PREPARE_TIMEOUT)
                return;

            if (prepareAttempts < MAX_PREPARE_ATTEMPTS)
            {
                logger.LogWarning($"Video did not finish preparing in {PREPARE_TIMEOUT}s, retrying ({prepareAttempts + 1}/{MAX_PREPARE_ATTEMPTS})");
                string url = videoPlayer.url;
                videoPlayer.Stop();
                videoPlayer.url = url;
                StartPrepare();
            }
            else
            {
                OnVideoError(videoPlayer, $"the video did not finish preparing after {MAX_PREPARE_ATTEMPTS} attempts");
            }
        }

        private void OnVideoPrepared(VideoPlayer vp)
        {
            if (!isPreparing)
                return; // stopped while preparing

            isPreparing = false;

            if (vp.audioTrackCount == 0)
                logger.LogWarning("Video has no audio track");
            logger.LogInfo($"Prepared: length={vp.length:0.0}s frames={vp.frameCount} canSetTime={vp.canSetTime} audioTracks={vp.audioTrackCount}");

            if (pendingSeek > 0 && pendingSeek < vp.length - 1)
                vp.time = pendingSeek;
            pendingSeek = -1;

            vp.Play();
        }

        public void Seek(double time)
        {
            logger.LogInfo($"Seeking from {videoPlayer.time:0.0}s to {time:0.0}s");
            if (isPreparing)
                pendingSeek = time;
            else
                videoPlayer.time = time;
        }

        public void Stop()
        {
            isPreparing = false;
            pendingSeek = -1;
            LoadedPath = null;
            videoPlayer.Stop();
        }

        public void Pause()
        {
            if (videoPlayer.isPlaying)
                videoPlayer.Pause();
        }

        public void Resume()
        {
            if (IsPaused())
                videoPlayer.Play();
        }

        /// <summary>
        /// Has a prepared video that is not playing (the TV was switched off).
        /// </summary>
        public bool IsPaused()
        {
            return LoadedPath != null && !isPreparing && !videoPlayer.isPlaying && videoPlayer.isPrepared;
        }

        public bool IsPlaying()
        {
            return videoPlayer.isPlaying;
        }

        /// <summary>
        /// Playing, preparing, or paused: something is loaded that has not finished.
        /// </summary>
        public bool IsBusy()
        {
            return isPreparing || videoPlayer.isPlaying || IsPaused();
        }

        public double Time => videoPlayer.time;

        public TVScript GetTVScript()
        {
            return tvScript;
        }

        private void OnVideoEnd(VideoPlayer vp)
        {
            logger.LogInfo("Video playback completed");
            LoadedPath = null;
            VideoManager.Instance?.OnVideoFinished();
        }

        private void OnVideoError(VideoPlayer vp, string message)
        {
            isPreparing = false;
            LoadedPath = null;
            logger.LogError($"Video playback error: {message}");

            try
            {
                VideoManager.Instance?.OnVideoPlaybackError(message);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error handling playback failure: {ex}");
            }
        }
    }
}
