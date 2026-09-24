using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using LethalNetworkAPI.Utils;

namespace YoutubeOnTV
{
    /// <summary>
    /// Decides what the TV shows.
    ///
    /// The host owns the queue: it dequeues entries, downloads them, plays them and tells
    /// clients which video (by canonical watch URL) is playing and where. Clients download
    /// that video themselves and follow the host's position. Nobody shares file paths or
    /// stream URLs, since YouTube locks those to the IP that requested them.
    /// </summary>
    public class VideoManager : MonoBehaviour
    {
        public static VideoManager Instance { get; private set; }

        private enum Mode { Idle, Fallback, Video }

        /// <summary>The queue entry being loaded or played (host), or the host's current entry (client).</summary>
        public string CurrentInput { get; private set; }

        /// <summary>Canonical watch URL of the current video, once known.</summary>
        public string CurrentVideoUrl { get; private set; }

        public bool IsLoadingVideo { get; private set; }

        public bool IsPlayingFallback => mode == Mode.Fallback;

        private ManualLogSource logger;
        private Mode mode = Mode.Idle;
        private bool fallbackBroken;

        // Host-only
        private int currentRetries;
        private float retryAtTime;
        private float lastSyncTime;

        // Client-only: where the host is, so a late download can join at the right spot.
        private float hostTime;
        private float hostTimeReceivedAt;
        private string failedVideoUrl;
        private string loadedVideoUrl;
        private bool? pendingTvOn;

        private const int MAX_ATTEMPTS = 2;
        private const float RETRY_DELAY = 3f;
        private const float SYNC_INTERVAL = 2f;
        private const float SYNC_TOLERANCE = 1.5f;

        private static bool IsHost => LNetworkUtils.IsHostOrServer;

        private static string FallbackVideoPath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "fallback.mp4");

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            logger = YoutubeOnTVBase.Log;

            // Fetch yt-dlp and ffmpeg at startup rather than on the first "tv add".
            _ = VideoDownloader.Instance;
        }

        /// <summary>
        /// Forgets everything from the previous lobby. Called when a round scene loads and
        /// when the local player disconnects.
        /// </summary>
        public void ResetSession()
        {
            logger.LogInfo("Resetting TV state for a new session");
            VideoDownloader.Instance.CancelAll();
            VideoQueue.Clear();
            TVController.Instance?.Stop();

            mode = Mode.Idle;
            fallbackBroken = false;
            IsLoadingVideo = false;
            CurrentInput = null;
            CurrentVideoUrl = null;
            currentRetries = 0;
            retryAtTime = 0f;
            failedVideoUrl = null;
            loadedVideoUrl = null;
            pendingTvOn = null;
        }

        private void Update()
        {
            TVController tv = TVController.Instance;
            if (tv == null)
                return;

            if (pendingTvOn.HasValue)
            {
                SetTVPower(pendingTvOn.Value);
                pendingTvOn = null;
            }

            if (!IsTVOn())
            {
                tv.Pause();
                return;
            }

            if (IsHost)
            {
                AdvanceHostPlayback(tv);
                SyncPlaybackToClients(tv);
            }
            else
            {
                FollowHost(tv);
            }

            PrefetchNext();
        }

        // ===== Host =====

        private void AdvanceHostPlayback(TVController tv)
        {
            if (IsLoadingVideo)
                return;

            // A queued video interrupts the fallback.
            if (mode == Mode.Fallback && !VideoQueue.IsEmpty())
            {
                logger.LogInfo("Switching from fallback to queue video");
                tv.Stop();
                mode = Mode.Idle;
            }

            if (tv.IsBusy())
                return;

            if (CurrentInput != null)
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

                // Nothing playing and nothing pending: the current entry is done with.
                ClearCurrentVideo();
            }

            if (!VideoQueue.IsEmpty())
                PlayNextFromQueue();
            else if (mode != Mode.Fallback && !fallbackBroken)
                PlayFallback(broadcast: true);
        }

        private void PlayNextFromQueue()
        {
            CurrentInput = VideoQueue.Dequeue();
            CurrentVideoUrl = null;
            currentRetries = 0;
            retryAtTime = 0f;
            mode = Mode.Video;

            NetworkHandler.Instance?.BroadcastRemoveFromQueue(CurrentInput);
            NetworkHandler.Instance?.BroadcastLoadVideo(CurrentInput);

            LoadCurrentVideo();
        }

        private void LoadCurrentVideo()
        {
            string input = CurrentInput;
            logger.LogInfo($"Loading video: {input}");
            IsLoadingVideo = true;

            VideoDownloader.Instance.Get(input, result =>
            {
                // A skip or clear may have moved on while the download ran.
                if (CurrentInput != input)
                    return;

                IsLoadingVideo = false;

                if (!result.Success)
                {
                    OnCurrentVideoFailed(result.Error);
                    return;
                }

                CurrentVideoUrl = result.CanonicalUrl ?? input;

                if (TVController.Instance == null)
                {
                    logger.LogError("TVController not found!");
                    return;
                }

                TVController.Instance.PlayFile(result.FilePath);
                lastSyncTime = Time.time;
                NetworkHandler.Instance?.BroadcastPlayVideo(input, CurrentVideoUrl, 0f);
            });
        }

        private void OnCurrentVideoFailed(string reason)
        {
            logger.LogError($"Video failed: {CurrentInput} ({reason})");
            TVController.Instance?.Stop();
            currentRetries++;

            if (currentRetries < MAX_ATTEMPTS)
            {
                logger.LogInfo($"Retrying in {RETRY_DELAY}s (attempt {currentRetries + 1}/{MAX_ATTEMPTS})");
                retryAtTime = Time.time + RETRY_DELAY;
                return;
            }

            logger.LogError($"Giving up on {CurrentInput} after {currentRetries} attempts");
            ShowTip("Video Error", $"Could not play {VideoInput.Describe(CurrentInput)}. {reason}");
            ClearCurrentVideo();
            mode = Mode.Idle;
        }

        private void ClearCurrentVideo()
        {
            CurrentInput = null;
            CurrentVideoUrl = null;
            currentRetries = 0;
            retryAtTime = 0f;
        }

        private void SyncPlaybackToClients(TVController tv)
        {
            if (mode != Mode.Video || !tv.IsPlaying() || Time.time - lastSyncTime < SYNC_INTERVAL)
                return;

            lastSyncTime = Time.time;
            NetworkHandler.Instance?.BroadcastPlaybackTime((float)tv.Time);
        }

        /// <summary>
        /// Skip (and the tail end of clear), on every machine.
        /// </summary>
        public void OnSkipRequested()
        {
            string skipped = CurrentInput;
            if (skipped != null && !VideoQueue.Entries.Contains(skipped))
                VideoDownloader.Instance.Cancel(skipped);

            ClearCurrentVideo();
            IsLoadingVideo = false;
            mode = Mode.Idle;
            TVController.Instance?.Stop();
            // The host picks the next entry (or the fallback) on its next frame.
        }

        public void OnClearRequested()
        {
            VideoQueue.Clear();
            VideoDownloader.Instance.CancelAll();
            OnSkipRequested();
        }

        // ===== Client =====

        private void FollowHost(TVController tv)
        {
            if (mode == Mode.Fallback)
            {
                if (!tv.IsBusy() && !fallbackBroken)
                    StartFallbackPlayback();
                return;
            }

            if (mode != Mode.Video || CurrentVideoUrl == null || IsLoadingVideo || CurrentVideoUrl == failedVideoUrl)
                return;

            string wanted = CurrentVideoUrl;
            if (loadedVideoUrl == wanted && tv.LoadedPath != null)
                return; // already playing it

            IsLoadingVideo = true;
            VideoDownloader.Instance.Get(wanted, result =>
            {
                if (CurrentVideoUrl != wanted)
                    return;

                IsLoadingVideo = false;

                if (!result.Success)
                {
                    failedVideoUrl = wanted;
                    ShowTip("Video Error", $"Could not download the host's video. {result.Error}");
                    return;
                }

                loadedVideoUrl = wanted;
                TVController.Instance?.PlayFile(result.FilePath, EstimatedHostTime());
            });
        }

        /// <summary>Client-side estimate of where the host is in the current video.</summary>
        public float HostPositionEstimate => EstimatedHostTime();

        private float EstimatedHostTime()
        {
            return hostTime + (Time.realtimeSinceStartup - hostTimeReceivedAt);
        }

        /// <summary>The host started loading an entry: start downloading it too.</summary>
        public void OnHostLoadingVideo(string input)
        {
            if (IsHost)
                return;
            VideoDownloader.Instance.Prefetch(input);
        }

        /// <summary>The host started playing a video.</summary>
        public void OnHostPlayingVideo(string input, string videoUrl, float startTime)
        {
            if (IsHost)
                return;

            logger.LogInfo($"Host is playing {videoUrl} ({input})");
            CurrentInput = input;
            CurrentVideoUrl = videoUrl;
            IsLoadingVideo = false;
            mode = Mode.Video;
            hostTime = startTime;
            hostTimeReceivedAt = Time.realtimeSinceStartup;
        }

        public void OnHostPlaybackTime(float time)
        {
            if (IsHost)
                return;

            hostTime = time;
            hostTimeReceivedAt = Time.realtimeSinceStartup;

            TVController tv = TVController.Instance;
            if (tv == null || !tv.IsPlaying() || mode != Mode.Video)
                return;

            float drift = Mathf.Abs((float)tv.Time - time);
            if (drift > SYNC_TOLERANCE)
            {
                logger.LogInfo($"Resyncing to host: {tv.Time:0.0}s -> {time:0.0}s");
                tv.Seek(time);
            }
        }

        public void OnHostPlayingFallback()
        {
            if (IsHost)
                return;

            mode = Mode.Fallback;
            CurrentInput = null;
            CurrentVideoUrl = null;
            IsLoadingVideo = false;
            TVController.Instance?.Stop();
        }

        // ===== Shared =====

        private void PrefetchNext()
        {
            if (IsLoadingVideo || !YoutubeOnTVBase.PrefetchNextVideo.Value)
                return;

            string next = VideoQueue.Peek();
            if (next != null && !VideoDownloader.Instance.IsReadyOrInFlight(next))
            {
                logger.LogInfo($"Prefetching next video: {next}");
                VideoDownloader.Instance.Prefetch(next);
            }
        }

        private void PlayFallback(bool broadcast)
        {
            StartFallbackPlayback();
            if (broadcast)
                NetworkHandler.Instance?.BroadcastPlayFallback();
        }

        private void StartFallbackPlayback()
        {
            if (TVController.Instance == null)
                return;

            mode = Mode.Fallback;
            loadedVideoUrl = null;
            TVController.Instance.PlayFile(FallbackVideoPath);
        }

        public void OnVideoFinished()
        {
            if (mode == Mode.Fallback)
            {
                // Every machine loops its own copy of the fallback.
                StartFallbackPlayback();
                return;
            }

            if (IsHost)
                ClearCurrentVideo(); // the next frame plays the next entry or the fallback

            mode = Mode.Idle;
        }

        public void OnVideoPlaybackError(string message)
        {
            if (mode == Mode.Fallback)
            {
                logger.LogError("The fallback video cannot be played; the TV stays dark until a video is queued");
                fallbackBroken = true;
                mode = Mode.Idle;
                return;
            }

            if (!IsHost)
            {
                failedVideoUrl = CurrentVideoUrl;
                return;
            }

            if (CurrentInput != null)
                OnCurrentVideoFailed("playback failed: " + message);
        }

        public void OnTVPoweredOn()
        {
            TVController.Instance?.Resume();
        }

        private bool IsTVOn()
        {
            TVScript tvScript = TVController.Instance?.GetTVScript();
            return tvScript != null && tvScript.tvOn;
        }

        private void SetTVPower(bool on)
        {
            TVScript tvScript = TVController.Instance?.GetTVScript();
            if (tvScript != null && tvScript.tvOn != on)
                tvScript.TurnTVOnOff(on);
        }

        private static void ShowTip(string header, string body)
        {
            HUDManager.Instance?.DisplayTip(header, body, true, false, "LC_Tip1");
        }

        // ===== Late join =====

        public TVStateData GetCurrentTVState()
        {
            TVController tv = TVController.Instance;
            return new TVStateData
            {
                isTVOn = IsTVOn(),
                mode = (int)mode,
                input = CurrentInput,
                videoUrl = CurrentVideoUrl,
                playbackTime = tv != null && mode == Mode.Video && CurrentVideoUrl != null ? (float)tv.Time : 0f,
                queue = VideoQueue.Entries.ToArray(),
            };
        }

        public void ApplyTVStateFromNetwork(TVStateData state)
        {
            if (IsHost)
                return;

            logger.LogInfo($"Applying host TV state: on={state.isTVOn} mode={(Mode)state.mode} video={state.videoUrl} t={state.playbackTime:0.0} queue={state.queue?.Length ?? 0}");

            VideoQueue.ReplaceWith(state.queue);

            // The TV may not have spawned on this client yet; Update applies it once it has.
            pendingTvOn = state.isTVOn;

            switch ((Mode)state.mode)
            {
                case Mode.Fallback:
                    OnHostPlayingFallback();
                    break;
                case Mode.Video when state.videoUrl != null:
                    OnHostPlayingVideo(state.input, state.videoUrl, state.playbackTime);
                    break;
                default:
                    mode = Mode.Idle;
                    CurrentInput = state.input;
                    CurrentVideoUrl = null;
                    // The host is still downloading this entry; get a head start.
                    if (state.input != null)
                        VideoDownloader.Instance.Prefetch(state.input);
                    break;
            }
        }
    }
}
