using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace YoutubeOnTV
{
    public class DownloadResult
    {
        public bool Success;
        public string VideoId;
        public string FilePath;
        public string Error;

        public string CanonicalUrl => VideoId == null ? null : VideoInput.WatchUrl(VideoId);
    }

    /// <summary>
    /// Downloads videos with yt-dlp and merges them with ffmpeg into local MP4 files the
    /// TV plays from disk.
    ///
    /// Unity's VideoPlayer cannot play an audio-only stream, and YouTube rarely serves a
    /// muxed video+audio format any more, so streaming the URLs directly gave a silent TV.
    /// A local file also sidesteps YouTube stream URLs being locked to the IP that resolved
    /// them: every machine downloads its own copy.
    /// </summary>
    public class VideoDownloader : MonoBehaviour
    {
        private static VideoDownloader _instance;

        public static VideoDownloader Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("YoutubeOnTV.VideoDownloader");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<VideoDownloader>();
                }
                return _instance;
            }
        }

        // H.264 + AAC in MP4 is what Media Foundation decodes everywhere, including under
        // Proton. VP9, AV1 and Opus are left out on purpose: they fail silently on the TV.
        // 480p matches the TV's render texture (about 809x455).
        private const string FormatSelector =
            "bv*[vcodec^=avc1][height<=480]+ba[acodec^=mp4a]"
            + "/18"
            + "/bv*[vcodec^=avc1][height<=720]+ba[acodec^=mp4a]"
            + "/b[vcodec^=avc1][acodec^=mp4a]"
            + "/b[ext=mp4]";

        private static readonly TimeSpan YtDlpUpdateInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

        private enum ToolState { Pending, Ready, Failed }

        private class Request
        {
            public string Key;
            public readonly List<Action<DownloadResult>> Callbacks = new List<Action<DownloadResult>>();
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public volatile string VideoIdSeen;
        }

        private ManualLogSource _logger;
        private string _toolsDir;
        private string _cacheDir;
        private string _ytDlpPath;
        private string _ffmpegPath;

        private ToolState _toolState = ToolState.Pending;
        private string _toolError;

        // Keyed by queue entry and by canonical watch URL, so a download started for a
        // search is found again when the host broadcasts the video it resolved to.
        private readonly Dictionary<string, Request> _inFlight = new Dictionary<string, Request>();
        private readonly Dictionary<string, DownloadResult> _completed = new Dictionary<string, DownloadResult>();

        public string CacheDirectory => _cacheDir;

        private void Awake()
        {
            _logger = YoutubeOnTVBase.Log;

            _toolsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _cacheDir = Path.Combine(_toolsDir, "cache");
            _ytDlpPath = Path.Combine(_toolsDir, "yt-dlp.exe");
            _ffmpegPath = Path.Combine(_toolsDir, "ffmpeg.exe");

            Directory.CreateDirectory(_cacheDir);
            CleanCache(removeLeftovers: true);

            StartCoroutine(EnsureTools());
        }

        // ===== Tools =====

        private IEnumerator EnsureTools()
        {
            _toolState = ToolState.Pending;

            bool ytDlpExists = File.Exists(_ytDlpPath);
            bool ytDlpStale = ytDlpExists && DateTime.UtcNow - File.GetLastWriteTimeUtc(_ytDlpPath) > YtDlpUpdateInterval;

            if (!ytDlpExists || ytDlpStale)
            {
                _logger.LogInfo(ytDlpExists ? "Updating yt-dlp.exe..." : "Downloading yt-dlp.exe...");
                Task task = Utils.DownloadYtDlp(_toolsDir);
                while (!task.IsCompleted)
                    yield return null;

                if (task.IsFaulted)
                {
                    string reason = task.Exception?.GetBaseException().Message;
                    if (!ytDlpExists)
                    {
                        Fail($"Could not download yt-dlp.exe: {reason}");
                        yield break;
                    }
                    _logger.LogWarning($"Could not update yt-dlp.exe, keeping the existing one: {reason}");
                }
                else
                {
                    _logger.LogInfo("yt-dlp.exe is up to date.");
                }
            }

            if (!File.Exists(_ffmpegPath))
            {
                _logger.LogInfo("Downloading ffmpeg.exe (one-time, about 55 MB)...");
                Task task = Utils.DownloadFFmpeg(_toolsDir);
                while (!task.IsCompleted)
                    yield return null;

                if (task.IsFaulted || !File.Exists(_ffmpegPath))
                {
                    Fail($"Could not download ffmpeg.exe: {task.Exception?.GetBaseException().Message ?? "file missing after download"}");
                    yield break;
                }
                _logger.LogInfo("ffmpeg.exe downloaded.");
            }

            _toolState = ToolState.Ready;
            _logger.LogInfo("Video download tools are ready.");
        }

        private void Fail(string error)
        {
            _toolError = error;
            _toolState = ToolState.Failed;
            _logger.LogError(error);
        }

        // ===== Public API =====

        /// <summary>
        /// Gets a local file for a queue entry or watch URL. The callback runs on the main
        /// thread, possibly immediately when the file is already cached.
        /// </summary>
        public void Get(string input, Action<DownloadResult> onDone)
        {
            if (TryGetCompleted(input, out DownloadResult cached))
            {
                onDone?.Invoke(cached);
                return;
            }

            if (_inFlight.TryGetValue(input, out Request running))
            {
                if (onDone != null)
                    running.Callbacks.Add(onDone);
                return;
            }

            var request = new Request { Key = input };
            if (onDone != null)
                request.Callbacks.Add(onDone);
            _inFlight[input] = request;
            StartCoroutine(Guarded(request));
        }

        /// <summary>
        /// Starts downloading an entry ahead of time so it plays without waiting.
        /// </summary>
        public void Prefetch(string input)
        {
            Get(input, null);
        }

        public bool IsReadyOrInFlight(string input)
        {
            return _inFlight.ContainsKey(input) || TryGetCompleted(input, out _);
        }

        /// <summary>
        /// Stops a download nobody is waiting for any more (skip, clear, lobby left).
        /// </summary>
        public void Cancel(string input)
        {
            if (input != null && _inFlight.TryGetValue(input, out Request request))
            {
                _logger.LogInfo($"Cancelling download: {input}");
                request.Cancellation.Cancel();
            }
        }

        public void CancelAll()
        {
            foreach (Request request in _inFlight.Values.Distinct().ToList())
                request.Cancellation.Cancel();
        }

        // ===== Download =====

        /// <summary>
        /// Runs a download so that an exception still completes the request; otherwise the
        /// caller would wait (and the TV stay stuck on "downloading") forever.
        /// </summary>
        private IEnumerator Guarded(Request request)
        {
            IEnumerator run = Run(request);
            while (true)
            {
                object current;
                try
                {
                    if (!run.MoveNext())
                        yield break;
                    current = run.Current;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Download of {request.Key} failed unexpectedly: {ex}");
                    if (_inFlight.ContainsValue(request))
                        Finish(request, new DownloadResult { Error = "Unexpected error: " + ex.Message });
                    yield break;
                }
                yield return current;
            }
        }

        private IEnumerator Run(Request request)
        {
            if (_toolState == ToolState.Failed)
                yield return EnsureTools(); // try again, the network may be back

            while (_toolState == ToolState.Pending)
                yield return null;

            if (_toolState == ToolState.Failed)
            {
                Finish(request, new DownloadResult { Error = _toolError });
                yield break;
            }

            _logger.LogInfo($"Downloading: {request.Key}");
            float startedAt = Time.realtimeSinceStartup;

            var stdout = new List<string>();
            var stderr = new StringBuilder();
            var process = new YoutubeDLProcess(_ytDlpPath);

            process.OutputReceived += (o, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;
                lock (stdout)
                    stdout.Add(e.Data.Trim());
                // --print id comes out before the download starts.
                if (request.VideoIdSeen == null && VideoInput.ExtractVideoId(e.Data.Trim()) == e.Data.Trim())
                    request.VideoIdSeen = e.Data.Trim();
            };
            process.ErrorReceived += (o, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    lock (stderr)
                        stderr.AppendLine(e.Data);
            };

            var options = new OptionSet
            {
                Format = FormatSelector,
                MergeOutputFormat = DownloadMergeFormat.Mp4,
                FfmpegLocation = _ffmpegPath,
                Output = Path.Combine(_cacheDir, "%(id)s.%(ext)s"),
                NoPlaylist = true,
                NoSimulate = true,
                NoProgress = true,
                NoMtime = true,
                Print = new MultiValue<string>("id", "after_move:filepath"),
                MatchFilters = $"!is_live & duration <=? {YoutubeOnTVBase.MaxVideoMinutes.Value * 60}",
            };

            request.Cancellation.CancelAfter(DownloadTimeout);
            Task<int> task;
            try
            {
                task = process.RunAsync(new[] { request.Key }, options, request.Cancellation.Token);
            }
            catch (Exception ex)
            {
                Finish(request, new DownloadResult { Error = "Could not start yt-dlp: " + ex.Message });
                yield break;
            }

            bool aliased = false;
            while (!task.IsCompleted)
            {
                // Let the canonical URL join the in-flight request as soon as it is known,
                // so a host broadcast for this video does not start a second download.
                if (!aliased && request.VideoIdSeen != null)
                {
                    aliased = true;
                    string canonical = VideoInput.WatchUrl(request.VideoIdSeen);
                    if (!_inFlight.ContainsKey(canonical))
                        _inFlight[canonical] = request;
                }
                yield return null;
            }

            string errors;
            lock (stderr)
                errors = stderr.ToString().Trim();

            List<string> lines;
            lock (stdout)
                lines = new List<string>(stdout);

            if (task.IsCanceled || request.Cancellation.IsCancellationRequested)
            {
                bool timedOut = Time.realtimeSinceStartup - startedAt >= DownloadTimeout.TotalSeconds - 1;
                Finish(request, new DownloadResult { Error = timedOut ? "Download timed out" : "Download cancelled" });
                yield break;
            }

            if (task.IsFaulted)
            {
                Finish(request, new DownloadResult { Error = "yt-dlp failed to run: " + task.Exception?.GetBaseException().Message });
                yield break;
            }

            string videoId = lines.FirstOrDefault(l => VideoInput.ExtractVideoId(l) == l);
            string filePath = lines.LastOrDefault(l => l.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

            if (task.Result == 0 && filePath != null && File.Exists(filePath))
            {
                Touch(filePath);
                if (!string.IsNullOrEmpty(errors))
                    _logger.LogInfo($"yt-dlp warnings: {errors}");
                _logger.LogInfo($"Downloaded {videoId} in {Time.realtimeSinceStartup - startedAt:0.0}s: {filePath}");

                var result = new DownloadResult { Success = true, VideoId = videoId, FilePath = filePath };
                _completed[request.Key] = result;
                if (videoId != null)
                    _completed[result.CanonicalUrl] = result;

                Finish(request, result);
                CleanCache(removeLeftovers: false);
                yield break;
            }

            string reason = task.Result != 0
                ? $"yt-dlp exited with code {task.Result}"
                : "No playable file (live stream, longer than the configured limit, or no H.264/AAC format)";
            _logger.LogError($"{reason}: {request.Key}");
            if (!string.IsNullOrEmpty(errors))
                _logger.LogError($"yt-dlp output: {errors}");

            Finish(request, new DownloadResult { VideoId = videoId, Error = reason });
        }

        private void Finish(Request request, DownloadResult result)
        {
            foreach (string key in _inFlight.Where(kv => kv.Value == request).Select(kv => kv.Key).ToList())
                _inFlight.Remove(key);

            if (!result.Success)
                _logger.LogWarning($"Download failed for {request.Key}: {result.Error}");

            foreach (Action<DownloadResult> callback in request.Callbacks)
            {
                try
                {
                    callback(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Download callback threw: {ex}");
                }
            }
        }

        private bool TryGetCompleted(string input, out DownloadResult result)
        {
            if (_completed.TryGetValue(input, out result) && File.Exists(result.FilePath))
                return true;

            // A watch URL for a video downloaded earlier, maybe in a previous session.
            string videoId = input.StartsWith(VideoInput.WatchUrlPrefix) ? VideoInput.ExtractVideoId(input) : null;
            if (videoId != null)
            {
                string path = Path.Combine(_cacheDir, videoId + ".mp4");
                if (File.Exists(path))
                {
                    Touch(path);
                    result = new DownloadResult { Success = true, VideoId = videoId, FilePath = path };
                    _completed[input] = result;
                    return true;
                }
            }

            result = null;
            return false;
        }

        // ===== Cache =====

        /// <summary>
        /// Marks a file as recently used for cache eviction. Windows (and Wine) refuse to
        /// change a file the VideoPlayer has open, which is fine: it is clearly in use.
        /// </summary>
        private static void Touch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Keeps the cache under the configured size, evicting the least recently played files.
        /// </summary>
        private void CleanCache(bool removeLeftovers)
        {
            try
            {
                var files = new DirectoryInfo(_cacheDir).GetFiles();

                if (removeLeftovers)
                {
                    // Partial downloads and unmerged streams from an interrupted run.
                    foreach (FileInfo f in files.Where(f => !f.Name.EndsWith(".mp4") || f.Name.Count(c => c == '.') > 1))
                        TryDelete(f);
                    files = new DirectoryInfo(_cacheDir).GetFiles("*.mp4");
                }

                long limit = (long)YoutubeOnTVBase.MaxCacheMegabytes.Value * 1024 * 1024;
                long total = files.Sum(f => f.Length);
                string playing = TVController.Instance?.videoPlayer?.url ?? "";

                foreach (FileInfo f in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= limit)
                        break;
                    if (playing.EndsWith(f.Name) || _completed.Values.Any(r => r.FilePath == f.FullName && IsInUse(r)))
                        continue;

                    total -= f.Length;
                    TryDelete(f);
                    foreach (string key in _completed.Where(kv => kv.Value.FilePath == f.FullName).Select(kv => kv.Key).ToList())
                        _completed.Remove(key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Cache cleanup failed: {ex.Message}");
            }
        }

        private static bool IsInUse(DownloadResult result)
        {
            // Anything still queued keeps its file.
            return VideoQueue.Entries.Any(e => e == result.CanonicalUrl);
        }

        private void TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not delete {file.Name}: {ex.Message}");
            }
        }
    }
}
