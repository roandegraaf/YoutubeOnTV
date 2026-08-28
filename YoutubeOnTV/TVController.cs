using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Video;

namespace YoutubeOnTV
{
    public class TVController : MonoBehaviour
    {
        public static TVController Instance { get; private set; }

        public VideoPlayer videoPlayer; // Made public for network sync access
        private VideoPlayer audioPlayer; // Plays the audio-only stream when it is served separately
        private VideoPlayer vanillaVideoPlayer; // Reference to vanilla TV's VideoPlayer
        private RenderTexture renderTexture; // The TV screen's render texture
        private AudioSource tvAudioSource; // TV's existing AudioSource
        private TVScript tvScript;
        private ManualLogSource logger;
        private bool isPreparing;

        private bool usingSeparateAudio;
        private bool videoPrepared;
        private bool audioPrepared;
        private float audioWaitStartedAt;
        private float lastAudioResyncAt;

        private const float AUDIO_DRIFT_TOLERANCE = 0.35f;
        private const float AUDIO_RESYNC_COOLDOWN = 2f;
        private const float AUDIO_PREPARE_TIMEOUT = 10f;

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

            audioPlayer = gameObject.AddComponent<VideoPlayer>();
            audioPlayer.playOnAwake = false;
            audioPlayer.isLooping = false;
            audioPlayer.source = VideoSource.Url;
            audioPlayer.skipOnDrop = true;
            audioPlayer.renderMode = VideoRenderMode.APIOnly;
            audioPlayer.controlledAudioTrackCount = 1;
            audioPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            audioPlayer.SetTargetAudioSource(0, tvAudioSource);
            audioPlayer.errorReceived -= OnAudioError;
            audioPlayer.errorReceived += OnAudioError;
            logger.LogInfo("Created companion VideoPlayer for separate audio streams");

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

        private void Update()
        {
            if (!usingSeparateAudio)
                return;

            if (isPreparing && videoPrepared && !audioPrepared
                && Time.time - audioWaitStartedAt > AUDIO_PREPARE_TIMEOUT)
            {
                logger.LogWarning("Audio stream did not prepare in time, playing video without sound");
                usingSeparateAudio = false;
                StartWhenReady();
                return;
            }

            if (!videoPlayer.isPlaying || !audioPlayer.isPlaying)
                return;

            if (Time.time - lastAudioResyncAt < AUDIO_RESYNC_COOLDOWN)
                return;

            double drift = audioPlayer.time - videoPlayer.time;
            if (System.Math.Abs(drift) > AUDIO_DRIFT_TOLERANCE)
            {
                lastAudioResyncAt = Time.time;
                audioPlayer.time = videoPlayer.time;
                logger.LogInfo($"Resynced audio stream ({drift:0.00}s drift)");
            }
        }

        /// <summary>
        /// Plays a video from the given URL. Pass an audioUrl when the video URL carries no
        /// audio track of its own, which is what YouTube serves when no muxed format exists.
        /// </summary>
        public void PlayVideo(string url, string audioUrl = null)
        {
            if (string.IsNullOrEmpty(url))
            {
                logger.LogError("Cannot play video: URL is null or empty");
                return;
            }

            logger.LogInfo($"Playing video: {url.Substring(0, System.Math.Min(100, url.Length))}...");

            BeginPlayback(url, audioUrl, shouldLoop: false);
        }

        private void BeginPlayback(string url, string audioUrl, bool shouldLoop)
        {
            audioPlayer.prepareCompleted -= OnAudioPrepared;
            audioPlayer.Stop();

            usingSeparateAudio = !string.IsNullOrEmpty(audioUrl);
            videoPrepared = false;
            audioPrepared = false;
            lastAudioResyncAt = 0f;

            videoPlayer.isLooping = shouldLoop;
            videoPlayer.url = url;

            videoPlayer.prepareCompleted -= OnVideoPrepared;
            videoPlayer.prepareCompleted += OnVideoPrepared;

            isPreparing = true;
            videoPlayer.Prepare();

            if (usingSeparateAudio)
            {
                logger.LogInfo("Preparing separate audio stream");
                audioPlayer.isLooping = shouldLoop;
                audioPlayer.url = audioUrl;
                audioPlayer.prepareCompleted += OnAudioPrepared;
                audioPlayer.Prepare();
            }
        }

        private void OnVideoPrepared(VideoPlayer vp)
        {
            videoPrepared = true;
            audioWaitStartedAt = Time.time;

            logger.LogInfo($"Video prepared! Audio tracks: {vp.audioTrackCount}");

            // Both players feed the same AudioSource, so the companion has to go if this
            // stream turned out to carry its own audio.
            if (usingSeparateAudio && vp.audioTrackCount > 0)
            {
                logger.LogInfo("Video stream already carries audio, dropping the companion stream");
                usingSeparateAudio = false;
                audioPlayer.prepareCompleted -= OnAudioPrepared;
                audioPlayer.Stop();
            }

            if (usingSeparateAudio)
            {
                logger.LogInfo("Audio comes from the companion stream");
            }
            else if (vp.audioTrackCount > 0)
            {
                logger.LogInfo($"Audio channels: {vp.GetAudioChannelCount(0)}");
            }
            else
            {
                logger.LogWarning("Video has NO audio tracks!");
            }

            logger.LogInfo($"TV AudioSource volume: {tvAudioSource.volume}");

            StartWhenReady();
        }

        private void OnAudioPrepared(VideoPlayer vp)
        {
            audioPrepared = true;
            logger.LogInfo($"Audio stream prepared! Audio tracks: {vp.audioTrackCount}");

            if (vp.audioTrackCount == 0)
            {
                logger.LogWarning("Audio stream carries no audio track, playing video without sound");
                usingSeparateAudio = false;
            }

            StartWhenReady();
        }

        private void StartWhenReady()
        {
            if (!videoPrepared)
                return;

            if (usingSeparateAudio && !audioPrepared)
                return;

            isPreparing = false;

            videoPlayer.Play();

            if (usingSeparateAudio)
            {
                audioPlayer.time = videoPlayer.time;
                audioPlayer.Play();
            }

            logger.LogInfo("Video playback started!");
        }

        /// <summary>
        /// Moves both streams to the given playback position
        /// </summary>
        public void Seek(double time)
        {
            videoPlayer.time = time;

            if (usingSeparateAudio)
            {
                audioPlayer.time = time;
            }
        }

        /// <summary>
        /// Stops the currently playing video
        /// </summary>
        public void Stop()
        {
            // Unsubscribe first: a Prepare() already in flight would otherwise start
            // playing the video we just stopped.
            videoPlayer.prepareCompleted -= OnVideoPrepared;
            audioPlayer.prepareCompleted -= OnAudioPrepared;
            isPreparing = false;
            usingSeparateAudio = false;
            videoPrepared = false;
            audioPrepared = false;
            videoPlayer.Stop();
            audioPlayer.Stop();
            logger.LogInfo("Video stopped");
        }

        /// <summary>
        /// Pauses the currently playing video
        /// </summary>
        public void Pause()
        {
            if (videoPlayer.isPlaying)
            {
                videoPlayer.Pause();

                if (usingSeparateAudio)
                {
                    audioPlayer.Pause();
                }

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

                if (usingSeparateAudio)
                {
                    audioPlayer.time = videoPlayer.time;
                    audioPlayer.Play();
                }

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
        /// Checks if the player is playing or still preparing a video
        /// </summary>
        public bool IsBusy()
        {
            return isPreparing || videoPlayer.isPlaying;
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

            // Use file:// URL scheme for local videos (required by Unity VideoPlayer)
            BeginPlayback("file://" + filePath, null, shouldLoop);
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

            if (usingSeparateAudio)
            {
                audioPlayer.Stop();
            }

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
            isPreparing = false;

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

        // A dead audio stream must not take the picture down with it, so this never reaches
        // VideoManager's retry logic the way OnVideoError does.
        private void OnAudioError(VideoPlayer vp, string message)
        {
            logger.LogWarning($"Audio stream error, playing video without sound: {message}");

            audioPlayer.prepareCompleted -= OnAudioPrepared;
            usingSeparateAudio = false;
            audioPrepared = true;
            StartWhenReady();
        }
    }
}
