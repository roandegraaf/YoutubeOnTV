using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Video;

namespace YoutubeOnTV
{
    public class TVController : MonoBehaviour
    {
        public static TVController Instance { get; private set; }

        private VideoPlayer videoPlayer;
        private VideoPlayer vanillaVideoPlayer; // Reference to vanilla TV's VideoPlayer
        private RenderTexture renderTexture; // The TV screen's render texture
        private AudioSource tvAudioSource; // TV's existing AudioSource
        private TVScript tvScript;
        private ManualLogSource logger;

        private void Awake()
        {
            Instance = this;
            logger = BepInEx.Logging.Logger.CreateLogSource("YoutubeOnTV");

            // Get reference to TVScript component
            tvScript = gameObject.GetComponent<TVScript>();
            if (tvScript == null)
            {
                logger.LogError("TVScript not found on this GameObject!");
                return;
            }

            // Use the TV's existing AudioSource (tvSFX) - just like TVLoader does
            tvAudioSource = tvScript.tvSFX;
            logger.LogInfo($"Found TV AudioSource: {tvAudioSource.name}");

            // Get the vanilla VideoPlayer and capture its render texture
            vanillaVideoPlayer = tvScript.video;
            if (vanillaVideoPlayer != null)
            {
                renderTexture = vanillaVideoPlayer.targetTexture;
                logger.LogInfo($"Captured render texture: {renderTexture.width}x{renderTexture.height}");

                // Stop and disable vanilla video player
                vanillaVideoPlayer.Stop();
                vanillaVideoPlayer.enabled = false;
                logger.LogInfo("Disabled vanilla VideoPlayer");
            }
            else
            {
                logger.LogError("Vanilla VideoPlayer not found!");
            }

            // Create our custom VideoPlayer
            videoPlayer = gameObject.AddComponent<VideoPlayer>();
            logger.LogInfo("Created custom VideoPlayer");

            // Configure VideoPlayer exactly like TVLoader does
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = false;
            videoPlayer.source = VideoSource.Url;
            videoPlayer.skipOnDrop = true;
            videoPlayer.controlledAudioTrackCount = 1;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            videoPlayer.SetTargetAudioSource(0, tvAudioSource);

            // CRITICAL: Set the render texture so video displays on TV screen
            videoPlayer.targetTexture = renderTexture;
            logger.LogInfo("Configured VideoPlayer with TV's render texture");

            // Replace TVScript's video reference with our custom player
            tvScript.video = videoPlayer;
            logger.LogInfo("Replaced TVScript.video with custom VideoPlayer");

            // Register callback for when video ends
            videoPlayer.loopPointReached -= OnVideoEnd;
            videoPlayer.loopPointReached += OnVideoEnd;

            // Register error handler for when video encounters errors
            videoPlayer.errorReceived -= OnVideoError;
            videoPlayer.errorReceived += OnVideoError;

            logger.LogInfo("TVController initialized successfully!");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Plays a video from the given URL
        /// </summary>
        public void PlayVideo(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                logger.LogError("Cannot play video: URL is null or empty");
                return;
            }

            logger.LogInfo($"Playing video: {url.Substring(0, System.Math.Min(100, url.Length))}...");

            videoPlayer.url = url;

            // Subscribe to prepared event to check audio tracks
            videoPlayer.prepareCompleted -= OnVideoPrepared;
            videoPlayer.prepareCompleted += OnVideoPrepared;

            videoPlayer.Prepare();
        }

        private void OnVideoPrepared(VideoPlayer vp)
        {
            logger.LogInfo($"Video prepared! Audio tracks: {vp.audioTrackCount}");

            if (vp.audioTrackCount > 0)
            {
                logger.LogInfo($"Audio channels: {vp.GetAudioChannelCount(0)}");
            }
            else
            {
                logger.LogWarning("Video has NO audio tracks!");
            }

            logger.LogInfo($"TV AudioSource volume: {tvAudioSource.volume}");

            // Start playback
            vp.Play();
            logger.LogInfo("Video playback started!");
        }

        /// <summary>
        /// Stops the currently playing video
        /// </summary>
        public void Stop()
        {
            if (videoPlayer.isPlaying)
            {
                videoPlayer.Stop();
                logger.LogInfo("Video stopped");
            }
        }

        /// <summary>
        /// Pauses the currently playing video
        /// </summary>
        public void Pause()
        {
            if (videoPlayer.isPlaying)
            {
                videoPlayer.Pause();
                logger.LogInfo("Video paused");
            }
        }

        /// <summary>
        /// Resumes a paused video
        /// </summary>
        public void Resume()
        {
            if (!videoPlayer.isPlaying && !string.IsNullOrEmpty(videoPlayer.url))
            {
                videoPlayer.Play();
                logger.LogInfo("Video resumed");
            }
        }

        /// <summary>
        /// Checks if video is paused (has content but not playing)
        /// </summary>
        public bool IsPaused()
        {
            return !videoPlayer.isPlaying && !string.IsNullOrEmpty(videoPlayer.url) && videoPlayer.isPrepared;
        }

        /// <summary>
        /// Checks if a video is currently playing
        /// </summary>
        public bool IsPlaying()
        {
            return videoPlayer.isPlaying;
        }

        /// <summary>
        /// Sets whether the video should loop
        /// </summary>
        public void SetLooping(bool shouldLoop)
        {
            videoPlayer.isLooping = shouldLoop;
            logger.LogInfo($"Video looping set to: {shouldLoop}");
        }

        /// <summary>
        /// Plays a local video file
        /// </summary>
        public void PlayLocalVideo(string filePath, bool shouldLoop = false)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                logger.LogError("Cannot play local video: file path is null or empty");
                return;
            }

            if (!System.IO.File.Exists(filePath))
            {
                logger.LogError($"Cannot play local video: file not found at {filePath}");
                return;
            }

            logger.LogInfo($"Playing local video: {filePath}");

            videoPlayer.isLooping = shouldLoop;
            // Use file:// URL scheme for local videos (required by Unity VideoPlayer)
            videoPlayer.url = "file://" + filePath;

            // Subscribe to prepared event
            videoPlayer.prepareCompleted -= OnVideoPrepared;
            videoPlayer.prepareCompleted += OnVideoPrepared;

            videoPlayer.Prepare();
        }

        /// <summary>
        /// Gets the TVScript component reference
        /// </summary>
        public TVScript GetTVScript()
        {
            return tvScript;
        }

        /// <summary>
        /// Called when video reaches its end
        /// </summary>
        private void OnVideoEnd(VideoPlayer vp)
        {
            logger.LogInfo("Video playback completed");

            // Only notify VideoManager if video is not looping (looping videos don't trigger this)
            if (!vp.isLooping && VideoManager.Instance != null)
            {
                VideoManager.Instance.OnVideoFinished();
            }
        }

        /// <summary>
        /// Called when video encounters an error during playback
        /// </summary>
        private void OnVideoError(VideoPlayer vp, string message)
        {
            logger.LogError($"Video playback error: {message}");

            // Notify VideoManager about the error so it can handle retry logic
            if (VideoManager.Instance != null)
            {
                VideoManager.Instance.OnVideoPlaybackError(message);
            }

            // Show error to user
            if (HUDManager.Instance != null)
            {
                HUDManager.Instance.DisplayTip("Video Playback Error",
                    "Failed to play video. Trying next...",
                    true, false, "LC_Tip1");
            }
        }
    }
}
