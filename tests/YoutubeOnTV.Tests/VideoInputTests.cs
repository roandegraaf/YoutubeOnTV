using Xunit;

namespace YoutubeOnTV.Tests
{
    public class VideoInputTests
    {
        private const string Rick = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

        [Theory]
        [InlineData("dQw4w9WgXcQ")]
        [InlineData("  dQw4w9WgXcQ  ")]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
        [InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
        [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&list=PL123&index=2")]
        [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
        [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=abc")]
        [InlineData("https://youtu.be/dQw4w9WgXcQ")]
        [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abcdef&t=10")]
        [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/v/dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=share")]
        [InlineData("youtube.com/watch?v=dQw4w9WgXcQ")]
        public void YouTubeLinksAndIdsBecomeACanonicalWatchUrl(string input)
        {
            Assert.Equal(Rick, VideoInput.Normalize(input));
        }

        [Fact]
        public void VideoIdCaseIsPreserved()
        {
            // The vanilla terminal lowercases input; the mod reads the raw text instead.
            Assert.Equal("https://www.youtube.com/watch?v=AbCdEfGhIjK", VideoInput.Normalize("AbCdEfGhIjK"));
        }

        [Theory]
        [InlineData("never gonna give you up", "ytsearch:never gonna give you up")]
        [InlineData("me at the zoo", "ytsearch:me at the zoo")]
        [InlineData("  lofi  ", "ytsearch:lofi")]
        [InlineData("\"never gonna\" give", "ytsearch:never gonna give")]
        // Eleven lowercase letters: a word, not an id.
        [InlineData("programming", "ytsearch:programming")]
        // Too long to be an id.
        [InlineData("dQw4w9WgXcQx", "ytsearch:dQw4w9WgXcQx")]
        public void AnythingElseIsASearch(string input, string expected)
        {
            Assert.Equal(expected, VideoInput.Normalize(input));
        }

        [Theory]
        [InlineData("https://vimeo.com/76979871")]
        [InlineData("https://www.youtube.com/watch?v=short")]
        [InlineData("http://example.com/video.mp4")]
        public void OtherLinksPassThroughForYtDlp(string input)
        {
            Assert.Equal(input, VideoInput.Normalize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyInputIsRejected(string input)
        {
            Assert.Null(VideoInput.Normalize(input));
        }

        [Fact]
        public void NormalizingIsIdempotent()
        {
            // The host normalises again what clients send.
            foreach (string input in new[] { "dQw4w9WgXcQ", "https://youtu.be/dQw4w9WgXcQ?t=1", "some search", "https://vimeo.com/1" })
            {
                string once = VideoInput.Normalize(input);
                Assert.Equal(once, VideoInput.Normalize(once));
            }
        }

        [Theory]
        [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("jNQXAC9IVRw", "jNQXAC9IVRw")]
        [InlineData("[youtube] jNQXAC9IVRw: Downloading", null)]
        [InlineData("Z:\\cache\\jNQXAC9IVRw.mp4", null)]
        public void ExtractVideoIdOnlyMatchesWholeIdsAndLinks(string line, string expected)
        {
            // yt-dlp's --print id line is recognised by this, so log lines must not match.
            Assert.Equal(expected, VideoInput.ExtractVideoId(line));
        }

        [Theory]
        [InlineData("ytsearch:me at the zoo", "search: me at the zoo")]
        [InlineData(Rick, "youtu.be/dQw4w9WgXcQ")]
        [InlineData("https://vimeo.com/1", "https://vimeo.com/1")]
        public void DescribeIsReadable(string entry, string expected)
        {
            Assert.Equal(expected, VideoInput.Describe(entry));
        }
    }
}
