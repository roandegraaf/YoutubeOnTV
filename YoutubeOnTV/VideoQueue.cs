using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace YoutubeOnTV
{
    public static class VideoQueue
    {
        private static readonly List<string> _inputs = new List<string>();
        private static readonly Dictionary<string, int> _retryCount = new Dictionary<string, int>();
        private static int _ptr = 0;
        private const int MAX_RETRIES = 2;

        public static void Add(string input)
        {
            UnityEngine.Debug.Log($"[VideoQueue] Add called with: '{input}' (length: {input.Length})");

            string toAdd = "";

            // Try to extract YouTube video ID from URL
            string videoId = ExtractYouTubeVideoId(input);

            if (!string.IsNullOrEmpty(videoId))
            {
                // Successfully extracted video ID - construct clean URL
                toAdd = "https://www.youtube.com/watch?v=" + videoId;
                UnityEngine.Debug.Log($"[VideoQueue] Extracted video ID '{videoId}', constructed URL: '{toAdd}'");
            }
            // If it's a YouTube video ID (11 chars, no spaces, alphanumeric + dashes/underscores)
            else if (input.Length == 11 && !input.Contains(" ") && IsValidVideoId(input))
            {
                toAdd = "https://www.youtube.com/watch?v=" + input;
                UnityEngine.Debug.Log($"[VideoQueue] Detected as video ID, expanded to: '{toAdd}'");
            }
            // If it's a URL but not YouTube (or couldn't extract ID), pass as-is to yt-dlp
            else if (input.StartsWith("http://") || input.StartsWith("https://"))
            {
                toAdd = input;
                UnityEngine.Debug.Log($"[VideoQueue] Detected as URL, adding as-is: '{toAdd}'");
            }
            // Otherwise, treat as search query
            else
            {
                toAdd = "ytsearch:" + input;
                UnityEngine.Debug.Log($"[VideoQueue] Detected as search query, prefixed: '{toAdd}'");
            }

            _inputs.Add(toAdd);
            UnityEngine.Debug.Log($"[VideoQueue] Successfully added. Queue size: {_inputs.Count}");
        }

        /// <summary>
        /// Extracts YouTube video ID from various URL formats
        /// Supports:
        /// - https://www.youtube.com/watch?v=VIDEO_ID&other=params
        /// - https://youtu.be/VIDEO_ID
        /// - https://m.youtube.com/watch?v=VIDEO_ID
        /// - https://youtube.com/watch?v=VIDEO_ID
        /// </summary>
        private static string ExtractYouTubeVideoId(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            // Pattern 1: youtube.com/watch?v=VIDEO_ID (with optional additional parameters)
            // Matches: ?v=VIDEO_ID or &v=VIDEO_ID
            Match match = Regex.Match(input, @"[?&]v=([a-zA-Z0-9_-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Pattern 2: youtu.be/VIDEO_ID (short URL format)
            match = Regex.Match(input, @"youtu\.be/([a-zA-Z0-9_-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Pattern 3: youtube.com/embed/VIDEO_ID
            match = Regex.Match(input, @"youtube\.com/embed/([a-zA-Z0-9_-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Pattern 4: youtube.com/v/VIDEO_ID
            match = Regex.Match(input, @"youtube\.com/v/([a-zA-Z0-9_-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        /// <summary>
        /// Validates if a string is a valid YouTube video ID format
        /// </summary>
        private static bool IsValidVideoId(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length != 11)
                return false;

            // YouTube video IDs are 11 characters long and contain only alphanumeric, dash, and underscore
            return Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{11}$");
        }

        public static void Clear()
        {
            _inputs.Clear();
            _retryCount.Clear();
            _ptr = 0;
        }

        public static bool IsEmpty()
        {
            return _inputs.Count == 0;
        }

        public static string Next()
        {
            if (_inputs.Count == 0) return null;

            string result = _inputs[_ptr];
            _ptr = (_ptr + 1) % _inputs.Count;
            return result;
        }

        public static string Current()
        {
            if (_inputs.Count == 0) return null;
            return _inputs[_ptr];
        }

        public static void Skip()
        {
            if (_inputs.Count > 0)
            {
                _ptr = (_ptr + 1) % _inputs.Count;
            }
        }

        public static int Count()
        {
            return _inputs.Count;
        }

        /// <summary>
        /// Increments the retry count for a video. Returns true if max retries exceeded.
        /// </summary>
        public static bool IncrementRetry(string input)
        {
            if (!_retryCount.ContainsKey(input))
            {
                _retryCount[input] = 0;
            }

            _retryCount[input]++;
            UnityEngine.Debug.Log($"[VideoQueue] Retry count for '{input}': {_retryCount[input]}/{MAX_RETRIES}");

            return _retryCount[input] >= MAX_RETRIES;
        }

        /// <summary>
        /// Removes the current video from the queue (used when it fails permanently)
        /// </summary>
        public static string RemoveCurrent()
        {
            if (_inputs.Count == 0) return null;

            string removed = _inputs[_ptr];
            _inputs.RemoveAt(_ptr);
            _retryCount.Remove(removed);

            // Adjust pointer after removal
            if (_inputs.Count > 0)
            {
                _ptr = _ptr % _inputs.Count;
            }
            else
            {
                _ptr = 0;
            }

            UnityEngine.Debug.Log($"[VideoQueue] Removed '{removed}' from queue. Remaining: {_inputs.Count}");
            return removed;
        }

        /// <summary>
        /// Resets retry count for a video (used when it succeeds)
        /// </summary>
        public static void ResetRetry(string input)
        {
            if (_retryCount.ContainsKey(input))
            {
                _retryCount.Remove(input);
                UnityEngine.Debug.Log($"[VideoQueue] Reset retry count for '{input}'");
            }
        }
    }
}
