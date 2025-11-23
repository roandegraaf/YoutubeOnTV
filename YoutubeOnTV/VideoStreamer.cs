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
            // Check if yt-dlp.exe exists, if not download it
            if (!File.Exists(_ytDlpPath))
            {
                _logger.LogInfo("yt-dlp.exe not found. Downloading...");

                // DownloadYtDlp expects a directory path, not a file path
                string dllPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                Task downloadTask = YoutubeDLSharp.Utils.DownloadYtDlp(dllPath);

                // Wait for download to complete
                while (!downloadTask.IsCompleted)
                {
                    yield return null;
                }

                if (downloadTask.IsFaulted)
                {
                    _logger.LogError($"Failed to download yt-dlp.exe: {downloadTask.Exception?.GetBaseException().Message}");
                    _isInitialized = false;
                    yield break;
                }

                _logger.LogInfo("yt-dlp.exe downloaded successfully!");
            }
            else
            {
                _logger.LogInfo("yt-dlp.exe found at path.");
            }

            _isInitialized = true;
        }

        /// <summary>
        /// Resolves a YouTube URL or search query to a direct video URL
        /// </summary>
        /// <param name="input">YouTube URL, video ID, or search term</param>
        /// <param name="onUrlFound">Callback with the resolved URL (null if failed)</param>
        public void GetVideoUrl(string input, Action<string> onUrlFound)
        {
            StartCoroutine(GetVideoUrlCoroutine(input, onUrlFound));
        }

        private IEnumerator GetVideoUrlCoroutine(string input, Action<string> onUrlFound)
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

            // Configure options for format selection and URL extraction
            // Unity VideoPlayer needs a single URL with both audio and video (pre-muxed)
            // 18 = 360p MP4 with audio (common pre-muxed format)
            // 22 = 720p MP4 with audio (fallback)
            var options = new OptionSet()
            {
                Format = "18/22/(mp4)[height<=480]/worst",
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
                onUrlFound(null);
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
                onUrlFound(null);
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
                onUrlFound(null);
            }
            else
            {
                // yt-dlp returns the URL on stdout
                string[] urls = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                if (urls.Length > 0)
                {
                    string videoUrl = urls[0].Trim();
                    _logger.LogInfo($"Video URL resolved successfully!");
                    _logger.LogInfo($"URL: {videoUrl.Substring(0, Math.Min(100, videoUrl.Length))}...");
                    onUrlFound(videoUrl);
                }
                else
                {
                    _logger.LogError("yt-dlp returned empty output");
                    onUrlFound(null);
                }
            }

            _isResolving = false;
        }

        public bool IsResolving()
        {
            return _isResolving;
        }
    }
}
