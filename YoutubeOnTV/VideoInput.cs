using System.Text.RegularExpressions;

namespace YoutubeOnTV
{
    /// <summary>
    /// Turns what a player typed into the queue entry yt-dlp is given. Pure string logic
    /// with no Unity dependency, so it is covered by the unit tests in tests/.
    /// </summary>
    public static class VideoInput
    {
        public const string WatchUrlPrefix = "https://www.youtube.com/watch?v=";
        public const string SearchPrefix = "ytsearch:";

        private static readonly Regex VideoIdPattern = new Regex(@"^[a-zA-Z0-9_-]{11}$");

        private static readonly Regex[] UrlPatterns =
        {
            new Regex(@"youtube\.com/.*[?&]v=([a-zA-Z0-9_-]{11})(?![a-zA-Z0-9_-])"),
            new Regex(@"youtu\.be/([a-zA-Z0-9_-]{11})(?![a-zA-Z0-9_-])"),
            new Regex(@"youtube\.com/(?:embed|v|shorts|live)/([a-zA-Z0-9_-]{11})(?![a-zA-Z0-9_-])"),
        };

        /// <summary>
        /// YouTube links and bare video ids become a canonical watch URL, other links pass
        /// through untouched, and anything else becomes a YouTube search.
        /// </summary>
        public static string Normalize(string input)
        {
            // yt-dlp gets the entry as one quoted command-line argument, which a stray double
            // quote would split.
            input = (input ?? "").Replace("\"", "").Trim();
            if (input.Length == 0)
                return null;

            string videoId = ExtractVideoId(input);
            if (videoId != null)
                return WatchUrlPrefix + videoId;

            // Already-normalised entries pass through, so normalising twice is harmless.
            if (input.StartsWith("http://") || input.StartsWith("https://") || input.StartsWith(SearchPrefix))
                return input;

            return SearchPrefix + input;
        }

        /// <summary>
        /// The 11-character video id in a YouTube link or bare id, or null.
        /// </summary>
        public static string ExtractVideoId(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            // An all-lowercase word like "programming" is far more likely a search than one
            // of the rare ids without a digit, capital, dash or underscore.
            if (VideoIdPattern.IsMatch(input) && !IsLowercaseWord(input))
                return input;

            foreach (Regex pattern in UrlPatterns)
            {
                Match match = pattern.Match(input);
                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }

        private static bool IsLowercaseWord(string s)
        {
            foreach (char c in s)
            {
                if (c < 'a' || c > 'z')
                    return false;
            }
            return true;
        }

        public static string WatchUrl(string videoId)
        {
            return WatchUrlPrefix + videoId;
        }

        /// <summary>
        /// A short human-readable label for terminal output.
        /// </summary>
        public static string Describe(string entry)
        {
            if (string.IsNullOrEmpty(entry))
                return "";

            if (entry.StartsWith(SearchPrefix))
                return "search: " + entry.Substring(SearchPrefix.Length);

            if (entry.StartsWith(WatchUrlPrefix))
                return "youtu.be/" + entry.Substring(WatchUrlPrefix.Length);

            return entry;
        }
    }
}
