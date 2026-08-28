using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace YoutubeOnTV
{
    public class VideoStreamer : MonoBehaviour
    {
        private static VideoStreamer _instance;
        private string _ytDlpPath;
        private bool _isResolving;
        private ManualLogSource _logger;
        private YoutubeDL _ytdl;
        private bool _isInitialized;

        private static readonly TimeSpan YtDlpUpdateInterval = TimeSpan.FromHours(24);

        public static VideoStreamer Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("VideoStreamer");
                    _instance = go.AddComponent<VideoStreamer>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            _logger = BepInEx.Logging.Logger.CreateLogSource("YoutubeOnTV");

            // Set path to where yt-dlp.exe will be downloaded (plugin folder)
            string dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _ytDlpPath = Path.Combine(dllPath, "yt-dlp.exe");

            _logger.LogInfo($"VideoStreamer initialized. yt-dlp path: {_ytDlpPath}");

            // Initialize YoutubeDL instance
            _ytdl = new YoutubeDL();
            _ytdl.YoutubeDLPath = _ytDlpPath;
            _ytdl.OutputFolder = Path.Combine(dllPath, "temp");

            // Start initialization coroutine
            StartCoroutine(InitializeYtDlp());
        }

        private IEnumerator InitializeYtDlp()
        {
            bool exists = File.Exists(_ytDlpPath);
            bool stale = exists && DateTime.UtcNow - File.GetLastWriteTimeUtc(_ytDlpPath) > YtDlpUpdateInterval;

            if (exists && !stale)
            {
                _logger.LogInfo("yt-dlp.exe found at path and is up to date.");
                _isInitialized = true;
                yield break;
            }

            _logger.LogInfo(exists
                ? "yt-dlp.exe is out of date. Downloading the latest release..."
                : "yt-dlp.exe not found. Downloading the latest release...");

            // DownloadYtDlp expects a directory path, not a file path
            string dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Task downloadTask = YoutubeDLSharp.Utils.DownloadYtDlp(dllPath);

            while (!downloadTask.IsCompleted)
            {
                yield return null;
            }

            if (downloadTask.IsFaulted)
            {
                string reason = downloadTask.Exception?.GetBaseException().Message;

                if (exists)
                {
                    _logger.LogWarning($"Failed to update yt-dlp.exe, keeping the existing version: {reason}");
                    _isInitialized = true;
                }
                else
                {
                    _logger.LogError($"Failed to download yt-dlp.exe: {reason}");
                    _isInitialized = false;
                }

                yield break;
            }

            _logger.LogInfo("yt-dlp.exe is up to date.");
            _isInitialized = true;
        }

        /// <summary>
        /// Resolves a YouTube URL or search query to direct stream URLs
        /// </summary>
        /// <param name="input">YouTube URL, video ID, or search term</param>
        /// <param name="onResolved">Callback with the resolved streams (VideoUrl null if failed)</param>
        public void GetVideoUrl(string input, Action<ResolvedStreams> onResolved)
        {
            StartCoroutine(GetVideoUrlCoroutine(input, onResolved));
        }

        private IEnumerator GetVideoUrlCoroutine(string input, Action<ResolvedStreams> onResolved)
        {
            // Wait for initialization if not ready
            while (!_isInitialized)
            {
                yield return null;
            }

            _isResolving = true;
            _logger.LogInfo($"Resolving video URL for: {input}");

            // Use YoutubeDLProcess for direct URL extraction (like the original implementation)
            // This is simpler and works better for getting streaming URLs
            var ytdlProc = new YoutubeDLProcess(_ytDlpPath);

            StringBuilder outputBuilder = new StringBuilder();
            StringBuilder errorBuilder = new StringBuilder();

            ytdlProc.OutputReceived += (o, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            ytdlProc.ErrorReceived += (o, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            // 18/22 are the pre-muxed formats YouTube is retiring per-video; once they are
            // gone a bare "mp4" selector silently degrades to a video-only format, which left
            // the TV playing in silence. The later branches ask for an explicit avc1+AAC pair
            // instead, avc1 because Unity's VideoPlayer cannot be relied on to decode AV1.
            // 480p is capped to the TV's render texture (roughly 809x455); the 720p60 stream
            // it used to pick was five times the bytes for no visible gain.
            var options = new OptionSet()
            {
                Format = "18/22"
                    + "/bv*[vcodec^=avc1][height<=480]+ba[acodec^=mp4a][audio_channels<=2]"
                    + "/bv*[height<=480]+ba"
                    + "/b",
                GetUrl = true  // This tells yt-dlp to output the URL instead of downloading
            };

            Task<int> processTask = null;

            // Start the process
            try
            {
                processTask = Task.Run(async () =>
                {
                    return await ytdlProc.RunAsync(new[] { input }, options);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start yt-dlp process: {ex.Message}");
                onResolved(default(ResolvedStreams));
                _isResolving = false;
                yield break;
            }

            // Wait for process to complete
            while (!processTask.IsCompleted)
            {
                yield return null;
            }

            // Give it a moment to finish reading output
            yield return new WaitForSeconds(0.2f);

            if (processTask.IsFaulted)
            {
                _logger.LogError($"yt-dlp process failed: {processTask.Exception?.GetBaseException().Message}");
                onResolved(default(ResolvedStreams));
                _isResolving = false;
                yield break;
            }

            int exitCode = processTask.Result;
            string output = outputBuilder.ToString().Trim();
            string error = errorBuilder.ToString().Trim();

            _logger.LogInfo($"yt-dlp exit code: {exitCode}");
            _logger.LogInfo($"yt-dlp stdout length: {output.Length}");

            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogInfo($"yt-dlp stderr length: {error.Length}");
            }

            if (exitCode != 0 || string.IsNullOrEmpty(output))
            {
                _logger.LogError($"yt-dlp failed (Exit Code: {exitCode})");
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogError($"Error: {error}");
                }
                onResolved(default(ResolvedStreams));
            }
            else
            {
                // A muxed format prints one URL; a video+audio pair prints the video URL
                // first and the audio URL second.
                string[] urls = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                if (urls.Length > 0)
                {
                    var streams = new ResolvedStreams
                    {
                        VideoUrl = urls[0].Trim(),
                        AudioUrl = urls.Length > 1 ? urls[1].Trim() : null
                    };

                    _logger.LogInfo(streams.HasSeparateAudio
                        ? "Resolved a separate video and audio stream."
                        : "Resolved a single stream carrying both video and audio.");
                    _logger.LogInfo($"Video URL: {streams.VideoUrl.Substring(0, Math.Min(100, streams.VideoUrl.Length))}...");

                    if (urls.Length > 2)
                    {
                        _logger.LogWarning($"yt-dlp returned {urls.Length} URLs, using the first two.");
                    }

                    onResolved(streams);
                }
                else
                {
                    _logger.LogError("yt-dlp returned empty output");
                    onResolved(default(ResolvedStreams));
                }
            }

            _isResolving = false;
        }

        public bool IsResolving()
        {
            return _isResolving;
        }
    }

    public struct ResolvedStreams
    {
        public string VideoUrl;
        public string AudioUrl;

        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(VideoUrl); }
        }

        public bool HasSeparateAudio
        {
            get { return !string.IsNullOrEmpty(AudioUrl); }
        }
    }
}
