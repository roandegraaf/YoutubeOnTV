using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace YoutubeOnTV
{
    public class VideoManager : MonoBehaviour
    {
        public static VideoManager Instance { get; private set; }

        public string CurrentVideoUrl { get; private set; }
        public bool IsLoadingVideo { get; private set; }

        private ManualLogSource logger;
        private bool skipRequested = false;
        private bool hasStartedCurrentVideo = false;
        private bool isPlayingFallback = false;

        // Path to fallback video file (will be resolved to absolute path)
        private static string GetFallbackVideoPath()
        {
            return Path.Combine(Paths.PluginPath, "YoutubeOnTV", "fallback.mp4");
        }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                logger = BepInEx.Logging.Logger.CreateLogSource("YoutubeOnTV");
                logger.LogInfo("VideoManager initialized!");
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            if (TVController.Instance == null)
                return;

            // Check if TV is on
            bool isTVOn = IsTVOn();

            if (!isTVOn)
            {
                // TV is off, pause any playback
                if (TVController.Instance.IsPlaying())
                {
                    TVController.Instance.Pause();
                    // Keep state flags intact so we can resume when TV turns back on
                }
                return;
            }

            // TV is on - decide what to play
            if (!IsLoadingVideo)
            {
                if (!VideoQueue.IsEmpty())
                {
                    // Queue has videos - switch from fallback if needed
                    if (isPlayingFallback)
                    {
                        logger.LogInfo("Switching from fallback to queue video");
                        TVController.Instance.Stop();
                        isPlayingFallback = false;
                        hasStartedCurrentVideo = false;
                    }

                    // Auto-play next video when queue has items and nothing is playing
                    if (!hasStartedCurrentVideo && !TVController.Instance.IsPlaying())
                    {
                        PlayNextFromQueue();
                    }
                }
                else
                {
                    // Queue is empty - play fallback if not already playing
                    if (!isPlayingFallback && !TVController.Instance.IsPlaying())
                    {
                        PlayFallbackVideo();
                    }
                }
            }
        }

        /// <summary>
        /// Called from chat command to skip current video
        /// </summary>
        public void OnSkipRequested()
        {
            skipRequested = true;
            hasStartedCurrentVideo = false; // Allow next video to start
            isPlayingFallback = false; // Reset fallback flag

            if (TVController.Instance != null)
            {
                TVController.Instance.Stop();
            }

            // The Update loop will automatically start the next video
        }

        /// <summary>
        /// Plays the next video from the queue
        /// </summary>
        public void PlayNextFromQueue()
        {
            if (VideoQueue.IsEmpty())
            {
                logger.LogInfo("Queue is empty, nothing to play");
                return;
            }

            if (IsLoadingVideo)
            {
                logger.LogInfo("Already loading a video, please wait");
                return;
            }

            string input = VideoQueue.Next();
            logger.LogInfo($"Loading video from queue: {input}");

            IsLoadingVideo = true;
            hasStartedCurrentVideo = true; // Mark that we've started this video
            skipRequested = false;

            // Use VideoStreamer to resolve the URL
            VideoStreamer.Instance.GetVideoUrl(input, (resolvedUrl) =>
            {
                IsLoadingVideo = false;

                if (string.IsNullOrEmpty(resolvedUrl))
                {
                    logger.LogError($"Failed to resolve video URL: {input}");

                    // Check if we've exceeded retry limit
                    bool maxRetriesExceeded = VideoQueue.IncrementRetry(input);

                    if (maxRetriesExceeded)
                    {
                        // Remove the failed video from queue
                        string removed = VideoQueue.RemoveCurrent();
                        logger.LogError($"Removing video from queue after max retries: {removed}");

                        // Show error message to user
                        if (HUDManager.Instance != null)
                        {
                            HUDManager.Instance.DisplayTip("Video Error",
                                "Failed to load video. Removed from queue.",
                                true, false, "LC_Tip1");
                        }

                        hasStartedCurrentVideo = false;

                        // Try next video in queue (if any)
                        if (!VideoQueue.IsEmpty())
                        {
                            Invoke(nameof(PlayNextFromQueue), 1f);
                        }
                    }
                    else
                    {
                        // Retry after a short delay
                        logger.LogInfo("Retrying video after delay...");
                        hasStartedCurrentVideo = false;
                        Invoke(nameof(PlayNextFromQueue), 3f);
                    }
                    return;
                }

                // Success - reset retry count for this video
                VideoQueue.ResetRetry(input);
                CurrentVideoUrl = resolvedUrl;
                logger.LogInfo("Video URL resolved successfully!");

                // Tell TV to play this video
                if (TVController.Instance != null)
                {
                    TVController.Instance.PlayVideo(resolvedUrl);
                }
                else
                {
                    logger.LogError("TVController not found!");
                }
            });
        }

        /// <summary>
        /// Called when TV finishes playing a video
        /// </summary>
        public void OnVideoFinished()
        {
            logger.LogInfo("Video finished playing");
            CurrentVideoUrl = null;
            hasStartedCurrentVideo = false; // Allow next video to start
            isPlayingFallback = false; // Reset fallback flag

            // Auto-advance to next video in queue, or return to fallback
            if (!VideoQueue.IsEmpty() && !skipRequested)
            {
                Invoke(nameof(PlayNextFromQueue), 1f);
            }
            else
            {
                // Queue is empty, fallback will be triggered by Update loop
                logger.LogInfo("Queue empty, will return to fallback video");
            }
        }

        /// <summary>
        /// Called when video playback encounters an error
        /// </summary>
        public void OnVideoPlaybackError(string errorMessage)
        {
            logger.LogError($"Video playback error: {errorMessage}");

            if (VideoQueue.IsEmpty())
            {
                logger.LogInfo("No videos in queue to retry");
                return;
            }

            string currentVideo = VideoQueue.Current();
            bool maxRetriesExceeded = VideoQueue.IncrementRetry(currentVideo);

            if (maxRetriesExceeded)
            {
                // Remove the failed video from queue
                string removed = VideoQueue.RemoveCurrent();
                logger.LogError($"Removing video from queue after playback errors: {removed}");

                // Reset flags
                hasStartedCurrentVideo = false;
                isPlayingFallback = false;
                CurrentVideoUrl = null;
                IsLoadingVideo = false;

                // Try next video in queue (if any)
                if (!VideoQueue.IsEmpty())
                {
                    Invoke(nameof(PlayNextFromQueue), 1f);
                }
            }
            else
            {
                // Retry the same video after a delay
                logger.LogInfo("Retrying video after playback error...");
                hasStartedCurrentVideo = false;
                isPlayingFallback = false;
                CurrentVideoUrl = null;
                IsLoadingVideo = false;

                Invoke(nameof(PlayNextFromQueue), 3f);
            }
        }

        /// <summary>
        /// Checks if the TV is currently powered on
        /// </summary>
        private bool IsTVOn()
        {
            if (TVController.Instance == null)
                return false;

            TVScript tvScript = TVController.Instance.GetTVScript();
            if (tvScript == null)
                return false;

            return tvScript.tvOn;
        }

        /// <summary>
        /// Plays the fallback video file (looping)
        /// </summary>
        private void PlayFallbackVideo()
        {
            string fallbackPath = GetFallbackVideoPath();
            logger.LogInfo($"Playing fallback video: {fallbackPath}");

            if (TVController.Instance != null)
            {
                TVController.Instance.PlayLocalVideo(fallbackPath, shouldLoop: true);
                isPlayingFallback = true;
            }
            else
            {
                logger.LogError("Cannot play fallback: TVController not found!");
            }
        }

        /// <summary>
        /// Public method to trigger TV on event handling (called from patch)
        /// </summary>
        public void OnTVPoweredOn()
        {
            logger.LogInfo("TV powered on - checking what to play");

            // Check if there's a paused video to resume
            if (TVController.Instance != null && TVController.Instance.IsPaused())
            {
                logger.LogInfo("Resuming paused video");
                TVController.Instance.Resume();
            }
            else if (!VideoQueue.IsEmpty())
            {
                logger.LogInfo("Queue has videos, will play from queue");
                // Update loop will handle playing from queue
                hasStartedCurrentVideo = false;
            }
            else
            {
                logger.LogInfo("Queue is empty, will play fallback");
                // Update loop will handle playing fallback
            }
        }
    }
}
