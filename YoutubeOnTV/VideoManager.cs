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
        private bool isPlayingFallback = false;

        // Host-only playback state. currentInput is the queue entry that has already been
        // dequeued and is loading or playing; it is never set on clients.
        private string currentInput;
        private int currentRetries;
        private float retryAtTime;

        private const int MAX_ATTEMPTS = 2;
        private const float RETRY_DELAY = 3f;

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

            if (!IsTVOn())
            {
                // TV is off, pause any playback
                if (TVController.Instance.IsPlaying())
                {
                    TVController.Instance.Pause();
                    // Keep state flags intact so we can resume when TV turns back on
                }
                return;
            }

            // Only the host decides what plays; clients follow its broadcasts.
            if (!LNetworkUtils.IsHostOrServer)
                return;

            AdvancePlayback();
            SyncPlaybackToClients();
        }

        /// <summary>
        /// The single place that decides what the TV plays next (host only).
        /// </summary>
        private void AdvancePlayback()
        {
            if (IsLoadingVideo)
                return;

            // A queued video interrupts the fallback, so this has to come before the busy check.
            if (isPlayingFallback && !VideoQueue.IsEmpty())
            {
                logger.LogInfo("Switching from fallback to queue video");
                TVController.Instance.Stop();
                isPlayingFallback = false;
            }

            if (TVController.Instance.IsBusy())
                return;

            if (currentInput != null)
            {
                if (retryAtTime > 0f)
                {
                    if (Time.time >= retryAtTime)
                    {
                        retryAtTime = 0f;
                        LoadCurrentVideo();
                    }
                    return;
                }

                // Nothing playing, nothing pending: the current entry is done with.
                ClearCurrentVideo();
            }

            if (!VideoQueue.IsEmpty())
            {
                PlayNextFromQueue();
            }
            else if (!isPlayingFallback)
            {
                PlayFallbackVideo();
            }
        }

        private void SyncPlaybackToClients()
        {
            if (isPlayingFallback || !TVController.Instance.IsPlaying())
                return;

            if (Time.time - lastSyncTime < SYNC_INTERVAL)
                return;

            lastSyncTime = Time.time;

            if (NetworkHandler.Instance != null)
            {
                NetworkHandler.Instance.BroadcastPlaybackTime((float)TVController.Instance.videoPlayer.time);
            }
        }

        /// <summary>
        /// Called from the terminal command to skip the current video
        /// </summary>
        public void OnSkipRequested()
        {
            ClearCurrentVideo();
            isPlayingFallback = false;

            if (TVController.Instance != null)
            {
                TVController.Instance.Stop();
            }

            // AdvancePlayback picks the next video on the host's next frame.
        }

        /// <summary>
        /// Takes the next video off the queue and starts loading it (host only)
        /// </summary>
        private void PlayNextFromQueue()
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

            currentInput = VideoQueue.Dequeue();
            currentRetries = 0;
            retryAtTime = 0f;

            // Keep every client's queue in step with the host's.
            if (NetworkHandler.Instance != null)
            {
                NetworkHandler.Instance.BroadcastRemoveFromQueue(currentInput);
            }

            LoadCurrentVideo();
        }

        private void LoadCurrentVideo()
        {
            string input = currentInput;
            logger.LogInfo($"Loading video: {input}");

            IsLoadingVideo = true;

            VideoStreamer.Instance.GetVideoUrl(input, (resolvedUrl) =>
            {
                IsLoadingVideo = false;

                // A skip or clear may have moved on while yt-dlp was still running.
                if (currentInput != input)
                {
                    logger.LogInfo("Video changed while loading, discarding resolved URL");
                    return;
                }

                if (string.IsNullOrEmpty(resolvedUrl))
                {
                    OnCurrentVideoFailed($"Failed to resolve video URL: {input}");
                    return;
                }

                CurrentVideoUrl = resolvedUrl;
                logger.LogInfo("Video URL resolved successfully!");

                if (TVController.Instance == null)
                {
                    logger.LogError("TVController not found!");
                    return;
                }

                TVController.Instance.PlayVideo(resolvedUrl);

                if (NetworkHandler.Instance != null)
                {
                    NetworkHandler.Instance.BroadcastPlayVideo(resolvedUrl, 0f);
                }
            });
        }

        private void OnCurrentVideoFailed(string reason)
        {
            logger.LogError(reason);

            // A half-dead player would keep IsBusy() true and stall the retry timer.
            if (TVController.Instance != null)
            {
                TVController.Instance.Stop();
            }

            CurrentVideoUrl = null;
            currentRetries++;

            if (currentRetries < MAX_ATTEMPTS)
            {
                logger.LogInfo($"Retrying in {RETRY_DELAY}s (attempt {currentRetries + 1}/{MAX_ATTEMPTS})");
                retryAtTime = Time.time + RETRY_DELAY;
                return;
            }

            logger.LogError($"Giving up on video after {currentRetries} attempts: {currentInput}");
            ClearCurrentVideo();

            if (HUDManager.Instance != null)
            {
                HUDManager.Instance.DisplayTip("Video Error",
                    "Failed to load video. Removed from queue.",
                    true, false, "LC_Tip1");
            }
        }

        private void ClearCurrentVideo()
        {
            currentInput = null;
            currentRetries = 0;
            retryAtTime = 0f;
            CurrentVideoUrl = null;
        }

        /// <summary>
        /// Called when the TV finishes playing a video
        /// </summary>
        public void OnVideoFinished()
        {
            logger.LogInfo("Video finished playing");

            // The fallback is not looped by the player, so every machine restarts its own copy.
            if (isPlayingFallback)
            {
                isPlayingFallback = false;
                StartFallbackPlayback();
                return;
            }

            CurrentVideoUrl = null;

            if (!LNetworkUtils.IsHostOrServer)
                return;

            ClearCurrentVideo();
            // AdvancePlayback plays the next queue entry, or the fallback, next frame.
        }

        /// <summary>
        /// Called when video playback encounters an error
        /// </summary>
        public void OnVideoPlaybackError(string errorMessage)
        {
            logger.LogError($"Video playback error: {errorMessage}");

            // Clients wait for the host to tell them what to play instead.
            if (!LNetworkUtils.IsHostOrServer)
                return;

            // A broken fallback would otherwise be retried every frame.
            if (isPlayingFallback || currentInput == null)
                return;

            OnCurrentVideoFailed($"Playback failed for: {currentInput}");
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
        /// Plays the fallback video and tells clients to do the same (host only)
        /// </summary>
        private void PlayFallbackVideo()
        {
            StartFallbackPlayback();

            if (LNetworkUtils.IsHostOrServer && NetworkHandler.Instance != null)
            {
                NetworkHandler.Instance.BroadcastPlayFallback();
            }
        }

        private void StartFallbackPlayback()
        {
            if (TVController.Instance == null)
            {
                logger.LogError("Cannot play fallback: TVController not found!");
                return;
            }

            string fallbackPath = GetFallbackVideoPath();
            logger.LogInfo($"Playing fallback video: {fallbackPath}");

            TVController.Instance.PlayLocalVideo(fallbackPath, shouldLoop: false);
            isPlayingFallback = true;
            CurrentVideoUrl = null;
        }

        /// <summary>
        /// Public method to trigger TV on event handling (called from patch)
        /// </summary>
        public void OnTVPoweredOn()
        {
            logger.LogInfo("TV powered on - checking what to play");

            // Resume a video that was paused when the TV was switched off.
            if (TVController.Instance != null && TVController.Instance.IsPaused())
            {
                logger.LogInfo("Resuming paused video");
                TVController.Instance.Resume();
            }
        }

        /// <summary>
        /// Called by NetworkHandler when receiving a play video command from host
        /// </summary>
        public void PlayVideoFromNetwork(string url, float startTime)
        {
            // Don't let clients trigger this - only respond to host's broadcast
            if (LNetworkUtils.IsHostOrServer)
                return;

            logger.LogInfo($"Playing video from network: {url} at {startTime}s");

            CurrentVideoUrl = url;
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
            // Don't let clients trigger this - only respond to host's broadcast
            if (LNetworkUtils.IsHostOrServer)
                return;

            logger.LogInfo("Playing fallback from network");
            StartFallbackPlayback();
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
            // Don't let host apply network state - they are the source of truth
            if (LNetworkUtils.IsHostOrServer)
                return;

            logger.LogInfo($"Applying TV state - TVOn: {state.isTVOn}, Fallback: {state.isPlayingFallback}, URL: {state.currentVideoUrl}");

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
                logger.LogInfo("Syncing to fallback video");
                StartFallbackPlayback();
            }
            else if (!string.IsNullOrEmpty(state.currentVideoUrl))
            {
                // Play the current video at the specified time
                logger.LogInfo($"Syncing to video: {state.currentVideoUrl} at {state.currentPlaybackTime}s");
                CurrentVideoUrl = state.currentVideoUrl;
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
                CurrentVideoUrl = null;
            }
        }
    }
}
