using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using LethalNetworkAPI;
using LethalNetworkAPI.Utils;

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

        // Network sync
        private float lastSyncTime = 0f;
        private const float SYNC_INTERVAL = 2f; // Sync every 2 seconds

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

        private void Start()
        {
            // Clients automatically request TV state when they join
            // Delay slightly to ensure network is fully initialized
            if (!LNetworkUtils.IsHostOrServer)
            {
                logger.LogInfo("Client joining - will request TV state from host");
                Invoke(nameof(RequestTVStateFromHost), 0.5f);
            }
        }

        /// <summary>
        /// Requests the current TV state from the host (called automatically when client joins)
        /// </summary>
        private void RequestTVStateFromHost()
        {
            if (NetworkHandler.Instance != null)
            {
                NetworkHandler.Instance.RequestTVState();
            }
            else
            {
                logger.LogWarning("NetworkHandler not found, cannot request TV state");
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

            // TV is on - decide what to play (only host manages this)
            if (LNetworkUtils.IsHostOrServer && !IsLoadingVideo)
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

            // Host periodically syncs playback position to all clients
            if (LNetworkUtils.IsHostOrServer && TVController.Instance.IsPlaying() && !isPlayingFallback)
            {
                if (Time.time - lastSyncTime >= SYNC_INTERVAL)
                {
                    lastSyncTime = Time.time;
                    float currentTime = (float)TVController.Instance.videoPlayer.time;

                    if (NetworkHandler.Instance != null)
                    {
                        NetworkHandler.Instance.BroadcastPlaybackTime(currentTime);
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

                // Host tells TV to play this video AND broadcasts to all clients
                if (TVController.Instance != null)
                {
                    TVController.Instance.PlayVideo(resolvedUrl);

                    // Host broadcasts to all clients to play this video
                    if (LNetworkUtils.IsHostOrServer && NetworkHandler.Instance != null)
                    {
                        NetworkHandler.Instance.BroadcastPlayVideo(resolvedUrl, 0f);
                    }
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

                // Host broadcasts to all clients to play fallback
                if (LNetworkUtils.IsHostOrServer && NetworkHandler.Instance != null)
                {
                    NetworkHandler.Instance.BroadcastPlayFallback();
                }
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

        /// <summary>
        /// Called by NetworkHandler when receiving a play video command from host
        /// </summary>
        public void PlayVideoFromNetwork(string url, float startTime)
        {
            logger.LogInfo($"Playing video from network: {url} at {startTime}s");

            // Don't let clients trigger this - only respond to host's broadcast
            if (LNetworkUtils.IsHostOrServer)
                return;

            CurrentVideoUrl = url;
            hasStartedCurrentVideo = true;
            isPlayingFallback = false;

            if (TVController.Instance != null)
            {
                TVController.Instance.PlayVideo(url);

                // Set playback position after video is prepared
                if (startTime > 0f)
                {
                    StartCoroutine(SetPlaybackTimeWhenReady(startTime));
                }
            }
        }

        /// <summary>
        /// Called by NetworkHandler to sync playback position from host
        /// </summary>
        public void SyncPlaybackTime(float time)
        {
            // Don't sync if we're the host or not playing
            if (LNetworkUtils.IsHostOrServer || TVController.Instance == null)
                return;

            if (!TVController.Instance.IsPlaying())
                return;

            // Only sync if the time difference is significant (more than 1 second off)
            float currentTime = (float)TVController.Instance.videoPlayer.time;
            float timeDiff = Mathf.Abs(currentTime - time);

            if (timeDiff > 1f)
            {
                logger.LogInfo($"Syncing playback time: {currentTime}s -> {time}s (diff: {timeDiff}s)");
                TVController.Instance.videoPlayer.time = time;
            }
        }

        /// <summary>
        /// Coroutine to set playback time once video is ready
        /// </summary>
        private System.Collections.IEnumerator SetPlaybackTimeWhenReady(float startTime)
        {
            // Wait until video is prepared
            while (TVController.Instance != null && !TVController.Instance.videoPlayer.isPrepared)
            {
                yield return new WaitForSeconds(0.1f);
            }

            // Set the playback position
            if (TVController.Instance != null)
            {
                TVController.Instance.videoPlayer.time = startTime;
                logger.LogInfo($"Set playback start time to {startTime}s");
            }
        }

        /// <summary>
        /// Called by NetworkHandler when receiving a play fallback command from host
        /// </summary>
        public void PlayFallbackFromNetwork()
        {
            logger.LogInfo("Playing fallback from network");

            // Don't let clients trigger this - only respond to host's broadcast
            if (LNetworkUtils.IsHostOrServer)
                return;

            string fallbackPath = GetFallbackVideoPath();

            if (TVController.Instance != null)
            {
                TVController.Instance.PlayLocalVideo(fallbackPath, shouldLoop: true);
                isPlayingFallback = true;
                hasStartedCurrentVideo = false;
                CurrentVideoUrl = null;
            }
        }

        /// <summary>
        /// Gets the current TV state for network synchronization (host only)
        /// </summary>
        public TVStateData GetCurrentTVState()
        {
            var state = new TVStateData
            {
                isTVOn = IsTVOn(),
                isPlayingFallback = isPlayingFallback,
                currentVideoUrl = CurrentVideoUrl,
                currentPlaybackTime = 0f,
                isPlaying = false
            };

            if (TVController.Instance != null)
            {
                state.isPlaying = TVController.Instance.IsPlaying();

                if (state.isPlaying && !isPlayingFallback)
                {
                    state.currentPlaybackTime = (float)TVController.Instance.videoPlayer.time;
                }
            }

            logger.LogInfo($"Gathering TV state - TVOn: {state.isTVOn}, Fallback: {state.isPlayingFallback}, Playing: {state.isPlaying}, URL: {state.currentVideoUrl}");
            return state;
        }

        /// <summary>
        /// Applies received TV state from network (client only)
        /// </summary>
        public void ApplyTVStateFromNetwork(TVStateData state)
        {
            logger.LogInfo($"Applying TV state - TVOn: {state.isTVOn}, Fallback: {state.isPlayingFallback}, URL: {state.currentVideoUrl}");

            // Don't let host apply network state - they are the source of truth
            if (LNetworkUtils.IsHostOrServer)
                return;

            if (TVController.Instance == null)
            {
                logger.LogError("Cannot apply TV state: TVController not found!");
                return;
            }

            TVScript tvScript = TVController.Instance.GetTVScript();
            if (tvScript == null)
            {
                logger.LogError("Cannot apply TV state: TVScript not found!");
                return;
            }

            // If TV is off, make sure it's off and stop playback
            if (!state.isTVOn)
            {
                logger.LogInfo("TV is off, ensuring TV is off and stopping playback");

                if (tvScript.tvOn)
                {
                    tvScript.TurnTVOnOff(false);
                }

                TVController.Instance.Stop();
                isPlayingFallback = false;
                hasStartedCurrentVideo = false;
                CurrentVideoUrl = null;
                return;
            }

            // TV should be on - turn it on if it's not already
            if (!tvScript.tvOn)
            {
                logger.LogInfo("Turning on TV to sync with host");
                tvScript.TurnTVOnOff(true);
            }

            // TV is on - apply the appropriate state
            if (state.isPlayingFallback)
            {
                // Play fallback video
                logger.LogInfo("Syncing to fallback video");
                string fallbackPath = GetFallbackVideoPath();
                TVController.Instance.PlayLocalVideo(fallbackPath, shouldLoop: true);
                isPlayingFallback = true;
                hasStartedCurrentVideo = false;
                CurrentVideoUrl = null;
            }
            else if (!string.IsNullOrEmpty(state.currentVideoUrl))
            {
                // Play the current video at the specified time
                logger.LogInfo($"Syncing to video: {state.currentVideoUrl} at {state.currentPlaybackTime}s");
                CurrentVideoUrl = state.currentVideoUrl;
                hasStartedCurrentVideo = true;
                isPlayingFallback = false;

                TVController.Instance.PlayVideo(state.currentVideoUrl);

                // Set playback position after video is prepared
                if (state.currentPlaybackTime > 0f)
                {
                    StartCoroutine(SetPlaybackTimeWhenReady(state.currentPlaybackTime));
                }
            }
            else
            {
                // TV is on but nothing is playing
                logger.LogInfo("TV is on but nothing is playing");
                TVController.Instance.Stop();
                isPlayingFallback = false;
                hasStartedCurrentVideo = false;
                CurrentVideoUrl = null;
            }
        }
    }
}
