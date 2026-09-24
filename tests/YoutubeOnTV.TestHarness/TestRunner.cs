using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using Unity.Netcode;
using UnityEngine;

namespace YoutubeOnTV.TestHarness
{
    /// <summary>
    /// Runs the in-game scenarios once the host has spawned on the ship, writes
    /// BepInEx/youtubeontv-test-results.json and quits the game.
    /// </summary>
    public class TestRunner : MonoBehaviour
    {
        // "Rick Astley - Never Gonna Give You Up" (3:33) and "Me at the zoo" (0:19).
        private const string LongVideoId = "dQw4w9WgXcQ";
        private const string ShortVideoId = "jNQXAC9IVRw";

        // The first download also waits for yt-dlp and ffmpeg to arrive on a fresh install.
        private const float FirstVideoTimeout = 300f;
        private const float VideoTimeout = 120f;

        private readonly List<Result> results = new List<Result>();
        private readonly List<string> modExceptions = new List<string>();
        private string resultsPath;
        private string controlClipPath;
        private float startedAt;
        private AudioProbe probe;

        private class Result
        {
            public string Name;
            public bool Passed;
            public string Detail;
            public float Seconds;
        }

        private void Start()
        {
            resultsPath = Path.Combine(Paths.BepInExRootPath, "youtubeontv-test-results.json");
            controlClipPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "control.mp4");
            if (File.Exists(resultsPath))
                File.Delete(resultsPath);

            Application.logMessageReceived += OnLog;
            StartCoroutine(RunAll());
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if ((type == LogType.Exception || type == LogType.Error) && (stackTrace ?? "").Contains("YoutubeOnTV."))
                modExceptions.Add(message + " @ " + (stackTrace ?? "").Split('\n').FirstOrDefault());
        }

        private IEnumerator RunAll()
        {
            startedAt = Time.realtimeSinceStartup;

            // Everything runs inside one guarded enumerator so an exception in a scenario
            // still produces a report instead of a game that never quits.
            var stack = new Stack<IEnumerator>();
            switch (HarnessPlugin.Role)
            {
                case HarnessRole.MultiplayerHost: stack.Push(MultiplayerHostScenario()); break;
                case HarnessRole.MultiplayerClient: stack.Push(MultiplayerClientScenario()); break;
                default: stack.Push(Scenarios()); break;
            }
            while (stack.Count > 0)
            {
                bool moved;
                try
                {
                    moved = stack.Peek().MoveNext();
                }
                catch (Exception ex)
                {
                    Record("harness", false, "Unhandled exception: " + ex);
                    break;
                }

                if (!moved)
                {
                    stack.Pop();
                    continue;
                }

                if (stack.Peek().Current is IEnumerator nested)
                    stack.Push(nested);
                else
                    yield return stack.Peek().Current;
            }

            Check("no_mod_exceptions", modExceptions.Count == 0,
                modExceptions.Count == 0 ? "none logged" : string.Join(" | ", modExceptions.Take(5)));

            WriteReport();
            HarnessPlugin.Log.LogInfo("Tests finished, quitting");
            yield return new WaitForSeconds(1f);
            Application.Quit();
        }

        private IEnumerator Scenarios()
        {
            yield return EnterLobby("lobby_hosted");
            if (!LastPassed) yield break;

            Check("mod_managers_present",
                VideoManager.Instance != null && NetworkHandler.Instance != null,
                $"VideoManager={VideoManager.Instance != null}, NetworkHandler={NetworkHandler.Instance != null}");

            yield return SetUpTelevision();
            if (!LastPassed) yield break;

            TVController tv = TVController.Instance;
            TVScript tvScript = tv.GetTVScript();

            // ---- Fallback when the queue is empty ----
            tvScript.SwitchTVLocalClient();
            yield return WaitFor("tv_turns_on", 10f, () => tvScript.tvOn, () => "tvOn=" + tvScript.tvOn);

            yield return WaitFor("fallback_plays", 30f,
                () => tv.IsPlaying() && VideoManager.Instance.IsPlayingFallback && IsFile(tv, "fallback.mp4"),
                () => $"loaded={tv.LoadedPath} playing={tv.IsPlaying()} fallback={VideoManager.Instance.IsPlayingFallback}");
            // The fallback is a nearly black, quiet clip, so only motion is checked.
            yield return MeasurePlayback("fallback", tv, requirePicture: false, requireSound: false);

            // ---- Control clip: proves the picture and sound probes work at all ----
            if (File.Exists(controlClipPath))
            {
                tv.PlayFile(controlClipPath);
                yield return WaitFor("control_clip_plays", 10f, () => tv.IsPlaying() && IsFile(tv, "control.mp4"),
                    () => $"loaded={tv.LoadedPath}");
                yield return MeasurePlayback("control", tv, requirePicture: true, requireSound: true);

                // Diagnostic: does the source come alive when explicitly played?
                if (!LastPassed && tv.IsPlaying())
                {
                    tv.AudioSource.Play();
                    probe?.TakePeak();
                    yield return new WaitForSeconds(1f);
                    Record("diag_control_after_source_play", true,
                        $"peak {probe?.TakePeak():0.0000}; {DescribeAudio(tv)}");
                }
                // It ends by itself after 5s and the fallback comes back.
                yield return WaitFor("fallback_resumes_after_clip", 15f, () => IsFile(tv, "fallback.mp4") && tv.IsPlaying(),
                    () => $"loaded={tv.LoadedPath}");
            }
            else
            {
                Record("control_clip_plays", false, "control.mp4 was not deployed next to the harness");
            }

            // ---- Adding by video id (mixed case must survive the terminal) ----
            yield return Submit("tv add " + LongVideoId);
            string expectedLong = VideoInput.WatchUrl(LongVideoId);
            Check("add_by_id_queued_verbatim",
                VideoManager.Instance.CurrentInput == expectedLong || VideoQueue.Entries.Contains(expectedLong),
                $"current={VideoManager.Instance.CurrentInput} queue=[{QueueDump()}]");
            Check("terminal_confirms_add", TerminalScreen().Contains("Added to queue"),
                "screen tail: " + Tail(TerminalScreen(), 120));

            yield return WaitFor("video_starts", FirstVideoTimeout,
                () => tv.IsPlaying() && IsFile(tv, LongVideoId + ".mp4"),
                () => $"loaded={tv.LoadedPath} loading={VideoManager.Instance.IsLoadingVideo} current={VideoManager.Instance.CurrentInput}");
            if (LastPassed)
            {
                Check("video_is_cached_local_file",
                    tv.LoadedPath.StartsWith(VideoDownloader.Instance.CacheDirectory) && tv.videoPlayer.audioTrackCount >= 1,
                    $"path={tv.LoadedPath} size={tv.videoPlayer.width}x{tv.videoPlayer.height} audioTracks={tv.videoPlayer.audioTrackCount}");
                yield return MeasurePlayback("video", tv, requirePicture: true, requireSound: true);
                Check("canonical_url_known", VideoManager.Instance.CurrentVideoUrl == expectedLong,
                    "CurrentVideoUrl=" + VideoManager.Instance.CurrentVideoUrl);
            }

            // ---- TV off pauses, TV on resumes where it was ----
            if (LastPassedNamed("video_starts"))
            {
                double before = tv.Time;
                tvScript.SwitchTVLocalClient();
                yield return WaitFor("tv_off_pauses", 5f, () => !tvScript.tvOn && !tv.IsPlaying(),
                    () => $"tvOn={tvScript.tvOn} playing={tv.IsPlaying()}");

                yield return new WaitForSeconds(2f);
                double paused = tv.Time;

                tvScript.SwitchTVLocalClient();
                yield return WaitFor("tv_on_resumes", 10f, () => tvScript.tvOn && tv.IsPlaying(),
                    () => $"tvOn={tvScript.tvOn} playing={tv.IsPlaying()}");
                yield return new WaitForSeconds(2f);
                Check("resume_keeps_position", tv.Time > paused && paused >= before - 0.5 && IsFile(tv, LongVideoId + ".mp4"),
                    $"before={before:0.0}s paused={paused:0.0}s now={tv.Time:0.0}s");
            }

            // ---- Seeking, which a client joining mid-video relies on ----
            if (LastPassedNamed("video_starts") && IsFile(tv, LongVideoId + ".mp4"))
            {
                tv.Seek(60);
                yield return WaitFor("seek_jumps_forward", 8f,
                    () => tv.IsPlaying() && IsFile(tv, LongVideoId + ".mp4") && tv.Time > 59 && tv.Time < 70,
                    () => $"t={tv.Time:0.0} playing={tv.IsPlaying()} loaded={tv.LoadedPath} canSetTime={tv.videoPlayer.canSetTime} length={tv.videoPlayer.length:0.0}");
                yield return new WaitForSeconds(2f);
                Check("seek_keeps_playing", tv.IsPlaying() && IsFile(tv, LongVideoId + ".mp4") && tv.Time > 60,
                    $"t={tv.Time:0.0} playing={tv.IsPlaying()} loaded={tv.LoadedPath}");

                // Starting a file part-way in, as a late joiner does.
                tv.PlayFile(tv.LoadedPath ?? Path.Combine(VideoDownloader.Instance.CacheDirectory, LongVideoId + ".mp4"), 90);
                yield return WaitFor("start_at_offset", 10f,
                    () => tv.IsPlaying() && tv.Time > 89 && tv.Time < 100,
                    () => $"t={tv.Time:0.0} playing={tv.IsPlaying()} loaded={tv.LoadedPath}");
            }

            // ---- Adding by URL with extra parameters; prefetch while the current one plays ----
            yield return Submit($"tv add https://www.youtube.com/watch?v={ShortVideoId}&t=5s");
            string expectedShort = VideoInput.WatchUrl(ShortVideoId);
            Check("add_by_url_normalised", VideoQueue.Entries.Contains(expectedShort),
                $"queue=[{QueueDump()}]");

            yield return WaitFor("next_video_prefetched", VideoTimeout,
                () => File.Exists(Path.Combine(VideoDownloader.Instance.CacheDirectory, ShortVideoId + ".mp4")),
                () => "cache: " + string.Join(", ", Directory.GetFiles(VideoDownloader.Instance.CacheDirectory).Select(Path.GetFileName)));

            // ---- Queue listing ----
            yield return Submit("tv queue");
            string screen = TerminalScreen();
            Check("terminal_queue_lists_entries", screen.Contains("Now playing") && screen.Contains(ShortVideoId),
                "screen tail: " + Tail(screen, 200));

            // ---- Skip moves to the next entry (already downloaded, so it starts quickly) ----
            yield return Submit("tv skip");
            yield return WaitFor("skip_plays_next", 20f,
                () => VideoManager.Instance.CurrentInput == expectedShort && tv.IsPlaying() && IsFile(tv, ShortVideoId + ".mp4"),
                () => $"current={VideoManager.Instance.CurrentInput} playing={tv.IsPlaying()} loaded={tv.LoadedPath}");

            // ---- Search query, and the queue advancing on its own when a video ends ----
            yield return Submit("tv add me at the zoo");
            Check("add_by_search_queued", VideoQueue.Entries.Contains("ytsearch:me at the zoo"),
                $"queue=[{QueueDump()}]");

            if (LastPassedNamed("skip_plays_next"))
            {
                yield return WaitFor("end_of_video_advances", 30f + VideoTimeout,
                    () => VideoManager.Instance.CurrentInput == "ytsearch:me at the zoo" && tv.IsPlaying(),
                    () => $"current={VideoManager.Instance.CurrentInput} playing={tv.IsPlaying()} t={tv.Time:0.0}");
                Check("search_resolved_to_video", VideoManager.Instance.CurrentVideoUrl == expectedShort,
                    "CurrentVideoUrl=" + VideoManager.Instance.CurrentVideoUrl);
            }

            // ---- Clear empties the queue and falls back ----
            yield return Submit("tv add " + LongVideoId);
            yield return Submit("tv add " + ShortVideoId);
            yield return Submit("tv clear");
            yield return WaitFor("clear_empties_queue_and_falls_back", 20f,
                () => VideoQueue.Count() == 0 && VideoManager.Instance.CurrentInput == null
                      && tv.IsPlaying() && IsFile(tv, "fallback.mp4"),
                () => $"queue=[{QueueDump()}] current={VideoManager.Instance.CurrentInput} loaded={tv.LoadedPath}");

            // ---- A video that does not exist fails gracefully and the TV keeps working ----
            yield return Submit("tv add https://www.youtube.com/watch?v=00000000000");
            yield return WaitFor("bad_video_is_attempted", 10f,
                () => VideoManager.Instance.CurrentInput != null,
                () => $"current={VideoManager.Instance.CurrentInput}");
            yield return WaitFor("bad_video_recovers_to_fallback", 120f,
                () => VideoManager.Instance.CurrentInput == null && !VideoManager.Instance.IsLoadingVideo
                      && tv.IsPlaying() && IsFile(tv, "fallback.mp4"),
                () => $"current={VideoManager.Instance.CurrentInput} loading={VideoManager.Instance.IsLoadingVideo} loaded={tv.LoadedPath}");

            // ---- Leaving the lobby and hosting again starts from a clean slate ----
            yield return Submit("tv add " + LongVideoId);
            yield return Submit("tv add " + ShortVideoId);
            yield return new WaitForSeconds(1f);

            HarnessPlugin.Log.LogInfo("Leaving the lobby and hosting again");
            AutoHostPatches.AllowRehost();
            GameNetworkManager.Instance.Disconnect();
            yield return WaitFor("left_lobby", 60f, () => StartOfRound.Instance == null, () => "StartOfRound still present");

            yield return EnterLobby("rehosted");
            if (!LastPassed) yield break;

            Check("new_session_starts_clean",
                VideoQueue.Count() == 0 && VideoManager.Instance.CurrentInput == null && !VideoManager.Instance.IsPlayingFallback,
                $"queue=[{QueueDump()}] current={VideoManager.Instance.CurrentInput} fallback={VideoManager.Instance.IsPlayingFallback}");

            yield return SetUpTelevision();
            if (!LastPassed) yield break;

            tv = TVController.Instance;
            tvScript = tv.GetTVScript();
            if (!tvScript.tvOn)
                tvScript.SwitchTVLocalClient();

            yield return WaitFor("fallback_plays_after_rehost", 30f,
                () => tv.IsPlaying() && IsFile(tv, "fallback.mp4"),
                () => $"loaded={tv.LoadedPath} playing={tv.IsPlaying()}");

            // Cached from before: should start almost immediately.
            yield return Submit("tv add " + ShortVideoId);
            yield return WaitFor("cached_video_starts_fast", 15f,
                () => tv.IsPlaying() && IsFile(tv, ShortVideoId + ".mp4"),
                () => $"loaded={tv.LoadedPath} loading={VideoManager.Instance.IsLoadingVideo}");
        }

        // ===== Multiplayer =====
        //
        // Two instances on one machine: the host (launched through Steam) and a client
        // (launched directly through Proton) with its own r2modman profile, so its own
        // plugin folder and video cache. Each side checks what it can observe and they
        // pace each other through the shared game state.

        private const string MpSearch = "ytsearch:me at the zoo";

        private IEnumerator MultiplayerHostScenario()
        {
            yield return EnterLobby("mp_host_lobby");
            if (!LastPassed) yield break;

            yield return SetUpTelevision();
            if (!LastPassed) yield break;

            TVController tv = TVController.Instance;
            TVScript tvScript = tv.GetTVScript();
            tvScript.SwitchTVLocalClient();

            // A video is already playing when the client joins, so the join sync is tested.
            yield return Submit("tv add " + LongVideoId);
            yield return WaitFor("mp_host_video_starts", FirstVideoTimeout,
                () => tv.IsPlaying() && IsFile(tv, LongVideoId + ".mp4"),
                () => $"loaded={tv.LoadedPath}");

            yield return WaitFor("mp_client_connected", 300f,
                () => NetworkManager.Singleton.ConnectedClientsIds.Count >= 2,
                () => $"clients={NetworkManager.Singleton.ConnectedClientsIds.Count}");
            if (!LastPassed) yield break;

            // The client adds a search and then skips to it.
            yield return WaitFor("mp_host_receives_client_add", 240f,
                () => VideoQueue.Entries.Contains(MpSearch) || VideoManager.Instance.CurrentInput == MpSearch,
                () => $"queue=[{QueueDump()}] current={VideoManager.Instance.CurrentInput}");
            Check("mp_client_add_single_prefix", !VideoQueue.Entries.Any(e => e.StartsWith("ytsearch:ytsearch:")),
                $"queue=[{QueueDump()}]");

            yield return WaitFor("mp_host_applies_client_skip", 180f,
                () => VideoManager.Instance.CurrentInput == MpSearch && tv.IsPlaying(),
                () => $"current={VideoManager.Instance.CurrentInput} playing={tv.IsPlaying()}");

            // Give the client time to follow, then switch the TV off and on for it to observe.
            yield return new WaitForSeconds(20f);
            HarnessPlugin.Log.LogInfo("Switching the TV off for the client");
            tvScript.SwitchTVLocalClient();
            yield return new WaitForSeconds(10f);
            HarnessPlugin.Log.LogInfo("Switching the TV on for the client");
            tvScript.SwitchTVLocalClient();
            // Keep replaying something so the client can confirm it resumes.
            yield return Submit("tv add " + ShortVideoId);

            yield return WaitFor("mp_client_finished", 180f,
                () => NetworkManager.Singleton.ConnectedClientsIds.Count < 2,
                () => "client still connected");
        }

        private IEnumerator MultiplayerClientScenario()
        {
            yield return EnterLobby("mp_client_joined");
            if (!LastPassed) yield break;

            Check("mp_client_is_not_host", !NetworkManager.Singleton.IsServer, "IsServer=" + NetworkManager.Singleton.IsServer);

            string expectedLong = VideoInput.WatchUrl(LongVideoId);
            yield return WaitFor("mp_client_receives_host_state", 60f,
                () => VideoManager.Instance.CurrentVideoUrl == expectedLong,
                () => $"CurrentVideoUrl={VideoManager.Instance.CurrentVideoUrl} queue=[{QueueDump()}]");

            yield return WaitFor("mp_client_tv_present", 60f,
                () => TVController.Instance != null && TVController.Instance.GetTVScript().tvOn,
                () => $"tv={(TVController.Instance != null)} on={TVController.Instance?.GetTVScript().tvOn}");
            if (!LastPassed) yield break;

            AttachProbe();

            yield return WaitFor("mp_client_plays_host_video", VideoTimeout + 180f,
                () => TVController.Instance.IsPlaying() && IsFile(TVController.Instance, LongVideoId + ".mp4"),
                () => $"loaded={TVController.Instance.LoadedPath} loading={VideoManager.Instance.IsLoadingVideo}");
            if (LastPassed)
            {
                Check("mp_client_downloaded_own_copy", TVController.Instance.LoadedPath.StartsWith(VideoDownloader.Instance.CacheDirectory),
                    $"path={TVController.Instance.LoadedPath} cache={VideoDownloader.Instance.CacheDirectory}");

                // Joined mid-video: must be near the host's position, not at the start.
                yield return new WaitForSeconds(4f);
                float host = VideoManager.Instance.HostPositionEstimate;
                Check("mp_client_in_sync_with_host", Mathf.Abs((float)TVController.Instance.Time - host) < 2.5f && TVController.Instance.Time > 5,
                    $"client={TVController.Instance.Time:0.0}s host~{host:0.0}s");

                yield return MeasurePlayback("mp_client_video", TVController.Instance, requirePicture: true, requireSound: true);
            }

            yield return Submit("tv add me at the zoo");
            yield return WaitFor("mp_client_add_roundtrip", 20f,
                () => VideoQueue.Entries.Contains(MpSearch),
                () => $"queue=[{QueueDump()}]");

            yield return Submit("tv skip");
            yield return WaitFor("mp_client_follows_skip", VideoTimeout + 60f,
                () => VideoManager.Instance.CurrentInput == MpSearch && TVController.Instance.IsPlaying() && IsFile(TVController.Instance, ShortVideoId + ".mp4"),
                () => $"current={VideoManager.Instance.CurrentInput} loaded={TVController.Instance.LoadedPath} playing={TVController.Instance.IsPlaying()}");

            yield return WaitFor("mp_client_sees_tv_off", 90f, () => !TVController.Instance.GetTVScript().tvOn && !TVController.Instance.IsPlaying(),
                () => $"tvOn={TVController.Instance.GetTVScript().tvOn} playing={TVController.Instance.IsPlaying()}");
            yield return WaitFor("mp_client_sees_tv_on", 30f, () => TVController.Instance.GetTVScript().tvOn,
                () => $"tvOn={TVController.Instance.GetTVScript().tvOn}");
            yield return WaitFor("mp_client_plays_after_tv_on", 60f, () => TVController.Instance.IsPlaying(),
                () => $"loaded={TVController.Instance.LoadedPath} playing={TVController.Instance.IsPlaying()}");
        }

        // ===== Game helpers =====

        private IEnumerator EnterLobby(string name)
        {
            yield return WaitFor(name, 180f,
                () => StartOfRound.Instance != null && GameNetworkManager.Instance?.localPlayerController != null
                      && FindObjectOfType<Terminal>() != null,
                () => "host spawned on the ship");

            // Let late Start() calls and network spawns settle.
            yield return new WaitForSeconds(3f);
        }

        private IEnumerator SetUpTelevision()
        {
            StartOfRound round = StartOfRound.Instance;
            int id = round.unlockablesList.unlockables.FindIndex(u => u.unlockableName == "Television");
            if (id < 0)
            {
                Record("tv_unlocked", false, "No 'Television' in the unlockables list");
                yield break;
            }

            if (FindObjectOfType<TVScript>() == null)
                round.BuyShipUnlockableServerRpc(id, FindObjectOfType<Terminal>().groupCredits);

            yield return WaitFor("tv_controller_attached", 30f,
                () => TVController.Instance != null && TVController.Instance.GetTVScript() != null,
                () => "waiting for TVController on the spawned TV");

            if (LastPassed)
                AttachProbe();
        }

        private void AttachProbe()
        {
            AudioSource source = TVController.Instance.AudioSource;
            probe = source.GetComponent<AudioProbe>() ?? source.gameObject.AddComponent<AudioProbe>();

            // With the listener at zero Unity pulls no samples through any source, so
            // nothing could be measured. The mod never touches the listener volume.
            float master = IngamePlayerSettings.Instance.settings.masterVolume;
            string before = $"AudioListener.volume={AudioListener.volume:0.00} masterVolume setting={master:0.00}";
            if (AudioListener.volume < 0.05f)
            {
                AudioListener.volume = 0.5f;
                before += " -> raised the listener to 0.50 for the audio checks";
            }
            Record("diag_audio_listener", true, before);
        }

        /// <summary>
        /// Types a command into the ship terminal and submits it the way a player does.
        /// </summary>
        private IEnumerator Submit(string command)
        {
            Terminal terminal = FindObjectOfType<Terminal>();
            HarnessPlugin.Log.LogInfo($"> {command}");

            if (terminal.currentNode == null)
                terminal.LoadNewNode(terminal.terminalNodes.specialNodes[1]);

            terminal.terminalInUse = true;
            terminal.screenText.text += command; // fires Terminal.TextChanged, which counts textAdded
            yield return null;

            if (terminal.textAdded != command.Length)
            {
                HarnessPlugin.Log.LogWarning($"textAdded={terminal.textAdded}, expected {command.Length}; correcting");
                terminal.textAdded = command.Length;
            }

            terminal.OnSubmit();
            terminal.terminalInUse = false;

            yield return new WaitForSeconds(0.5f);
        }

        private static string TerminalScreen()
        {
            Terminal terminal = FindObjectOfType<Terminal>();
            return terminal?.screenText?.text ?? "";
        }

        private static bool IsFile(TVController tv, string fileName)
        {
            return tv.LoadedPath != null && Path.GetFileName(tv.LoadedPath) == fileName;
        }

        private static string QueueDump() => string.Join(", ", VideoQueue.Entries);

        // ===== Picture and sound checks =====

        private IEnumerator MeasurePlayback(string label, TVController tv, bool requirePicture, bool requireSound)
        {
            // Skip the switch-on click and the first moments of decoding.
            yield return new WaitForSeconds(0.5f);
            probe?.TakePeak();

            long frameStart = tv.videoPlayer.frame;
            double timeStart = tv.Time;
            yield return new WaitForSeconds(3f);

            long frames = tv.videoPlayer.frame - frameStart;
            double advanced = tv.Time - timeStart;
            Check(label + "_frames_advance", frames > 10 && advanced > 1.0,
                $"{frames} frames and {advanced:0.00}s in 3s");

            float brightness = SampleBrightness(tv.videoPlayer.targetTexture, out float spread);
            string pictureDetail = $"mean={brightness:0.000} spread={spread:0.000}";
            if (requirePicture)
                Check(label + "_picture_not_blank", brightness > 0.02f && spread > 0.01f, pictureDetail);
            else
                Record(label + "_picture_level", true, pictureDetail);

            float peak = probe != null ? probe.TakePeak() : -1f;
            string soundDetail = probe == null ? "no audio probe attached" : $"peak sample {peak:0.0000}; {DescribeAudio(tv)}";
            if (requireSound)
                Check(label + "_sound_audible", peak > 0.002f, soundDetail); // silence reads exactly 0
            else
                Record(label + "_sound_level", true, soundDetail);
        }

        private static string DescribeAudio(TVController tv)
        {
            AudioSource s = tv.AudioSource;
            AudioListener listener = FindObjectOfType<AudioListener>();
            float distance = listener != null ? Vector3.Distance(listener.transform.position, s.transform.position) : -1f;
            return $"source[go={s.gameObject.name} active={s.isActiveAndEnabled} playing={s.isPlaying} virtual={s.isVirtual} "
                   + $"vol={s.volume:0.00} mute={s.mute} clip={(s.clip != null ? s.clip.name : "none")} blend={s.spatialBlend:0.0} "
                   + $"range={s.minDistance:0.0}-{s.maxDistance:0.0} rolloff={s.rolloffMode} dist={distance:0.0} "
                   + $"mixer={s.outputAudioMixerGroup?.name}] listener[vol={AudioListener.volume:0.00} pause={AudioListener.pause}] "
                   + $"vp[mode={tv.videoPlayer.audioOutputMode} tracks={tv.videoPlayer.audioTrackCount} "
                   + $"enabled0={tv.videoPlayer.IsAudioTrackEnabled(0)} target0={tv.videoPlayer.GetTargetAudioSource(0)?.name}]";
        }

        private static float SampleBrightness(RenderTexture rt, out float spread)
        {
            spread = 0f;
            if (rt == null)
                return 0f;

            RenderTexture previous = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
            }
            finally
            {
                RenderTexture.active = previous;
            }

            Color32[] pixels = tex.GetPixels32();
            Destroy(tex);

            float sum = 0f, sumSq = 0f;
            int n = 0;
            for (int i = 0; i < pixels.Length; i += 37)
            {
                float l = (0.299f * pixels[i].r + 0.587f * pixels[i].g + 0.114f * pixels[i].b) / 255f;
                sum += l;
                sumSq += l * l;
                n++;
            }

            float mean = sum / n;
            spread = Mathf.Sqrt(Mathf.Max(0f, sumSq / n - mean * mean));
            return mean;
        }

        // ===== Result bookkeeping =====

        private bool LastPassed => results.Count > 0 && results[results.Count - 1].Passed;

        private bool LastPassedNamed(string name) => results.Any(r => r.Name == name && r.Passed);

        private IEnumerator WaitFor(string name, float timeout, Func<bool> condition, Func<string> describe)
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < timeout)
            {
                bool met;
                try
                {
                    met = condition();
                }
                catch (Exception)
                {
                    met = false; // e.g. the TV object was destroyed and respawned
                }

                if (met)
                {
                    string found;
                    try { found = describe(); } catch (Exception ex) { found = ex.GetType().Name; }
                    Record(name, true, found, Time.realtimeSinceStartup - start);
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }

            string detail;
            try { detail = describe(); } catch (Exception ex) { detail = ex.GetType().Name; }
            Record(name, false, $"timed out after {timeout}s: {detail}", timeout);
        }

        private void Check(string name, bool passed, string detail) => Record(name, passed, detail);

        private void Record(string name, bool passed, string detail, float seconds = 0f)
        {
            results.Add(new Result { Name = name, Passed = passed, Detail = detail, Seconds = seconds });
            string line = $"[{(passed ? "PASS" : "FAIL")}] {name} ({seconds:0.0}s): {detail}";
            if (passed)
                HarnessPlugin.Log.LogInfo(line);
            else
                HarnessPlugin.Log.LogError(line);

            // Written after every step so a crash or hang still leaves partial results.
            WriteReport(complete: false);
        }

        private void WriteReport(bool complete = true)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"complete\": {(complete ? "true" : "false")},\n");
            sb.Append($"  \"passed\": {results.Count(r => r.Passed)},\n");
            sb.Append($"  \"failed\": {results.Count(r => !r.Passed)},\n");
            sb.Append($"  \"elapsedSeconds\": {Time.realtimeSinceStartup - startedAt:0.0},\n");
            sb.Append("  \"results\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                Result r = results[i];
                sb.Append($"    {{\"name\": {Json(r.Name)}, \"passed\": {(r.Passed ? "true" : "false")}, "
                          + $"\"seconds\": {r.Seconds:0.0}, \"detail\": {Json(r.Detail)}}}");
                sb.Append(i < results.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");

            File.WriteAllText(resultsPath + ".tmp", sb.ToString());
            if (File.Exists(resultsPath))
                File.Delete(resultsPath);
            File.Move(resultsPath + ".tmp", resultsPath);
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string Tail(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);
    }
}
