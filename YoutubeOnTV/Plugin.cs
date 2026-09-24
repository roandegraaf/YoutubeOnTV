using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace YoutubeOnTV
{
    [BepInPlugin(Guid, "YoutubeOnTV", "0.3.0")]
    [BepInDependency("LethalNetworkAPI")]
    public class YoutubeOnTVBase : BaseUnityPlugin
    {
        public const string Guid = "com.roandegraaf.youtubeontv";

        private readonly Harmony harmony = new Harmony(Guid);

        public static YoutubeOnTVBase Instance;
        internal static ManualLogSource Log;

        public static ConfigEntry<int> MaxVideoMinutes;
        public static ConfigEntry<int> MaxCacheMegabytes;
        public static ConfigEntry<bool> PrefetchNextVideo;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MaxVideoMinutes = Config.Bind("Videos", "MaxVideoMinutes", 60,
                "Longest video that can be queued, in minutes. Videos are downloaded in full before they play, so very long ones take a while and use disk space.");
            MaxCacheMegabytes = Config.Bind("Videos", "MaxCacheMegabytes", 1024,
                "Size limit of the downloaded-video cache in the plugin folder. The least recently played videos are removed first.");
            PrefetchNextVideo = Config.Bind("Videos", "PrefetchNextVideo", true,
                "Download the next queued video while the current one plays, so it starts without waiting.");

            string dll = Assembly.GetExecutingAssembly().Location;
            Log.LogInfo($"YoutubeOnTV {Info.Metadata.Version} loaded (build {File.GetLastWriteTimeUtc(dll):yyyy-MM-dd HH:mm:ss} UTC)");

            harmony.PatchAll(typeof(Patches.TVScriptPatch));
            harmony.PatchAll(typeof(Patches.TerminalPatch));
            harmony.PatchAll(typeof(Patches.SessionPatch));

            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            if (Chainloader.PluginInfos.ContainsKey("rattenbonkers.TVLoader"))
            {
                Log.LogWarning("TVLoader is installed. Both mods take over the ship TV, so disable one of them; with TVLoader videos present they will fight over the screen.");
            }
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (VideoManager.Instance != null)
                return;

            // Created on the first scene load because objects made during the chainloader's
            // Awake do not survive in Lethal Company.
            var go = new GameObject("YoutubeOnTVManagers");
            DontDestroyOnLoad(go);
            go.AddComponent<VideoManager>();
            go.AddComponent<NetworkHandler>();
            Log.LogInfo("Managers created");
        }

        // ===== Terminal =====

        /// <summary>
        /// Returns the terminal page for a "tv ..." command, or null when the text is not one.
        /// </summary>
        /// <param name="typed">The text as the player typed it, with its original case.</param>
        internal static TerminalNode HandleTerminalCommand(string typed)
        {
            string text = (typed ?? "").Trim();
            string lower = text.ToLowerInvariant();

            string command;
            string argument = "";
            if (lower == "tv")
                command = "";
            else if (lower.StartsWith("tv "))
            {
                string rest = text.Substring(3).Trim();
                int space = rest.IndexOf(' ');
                command = (space < 0 ? rest : rest.Substring(0, space)).ToLowerInvariant();
                argument = space < 0 ? "" : rest.Substring(space + 1).Trim();
            }
            else if (lower == "skip tv" || lower == "clear tv" || lower == "queue tv")
                command = lower.Substring(0, lower.Length - 3);
            else
                return null;

            Log.LogInfo($"Terminal command: {text}");
            return Page(RunCommand(command, argument));
        }

        private static string RunCommand(string command, string argument)
        {
            NetworkHandler network = NetworkHandler.Instance;

            switch (command)
            {
                case "add":
                {
                    string entry = VideoInput.Normalize(argument);
                    if (entry == null)
                        return "Usage: tv add [url, video id or search]\n\n"
                               + "  tv add dQw4w9WgXcQ\n"
                               + "  tv add https://youtube.com/watch?v=...\n"
                               + "  tv add never gonna give you up\n";

                    network?.RequestAddVideo(entry);
                    return $"Added to queue: {VideoInput.Describe(entry)}\n\n"
                           + "Videos download before they play, so give it a few seconds.\n";
                }

                case "skip":
                    network?.RequestSkipVideo();
                    return "Skipped the current video.\n";

                case "clear":
                    int count = VideoQueue.Count();
                    network?.RequestClearQueue();
                    return $"Cleared the queue ({count} video{(count == 1 ? "" : "s")}) and stopped the current one.\n";

                case "queue":
                    return QueueText();

                default:
                    return "TV CONTROLS\n"
                           + "===========\n\n"
                           + "  tv add [url/id/search]  Add a video to the queue\n"
                           + "  tv queue                Show what is playing and queued\n"
                           + "  tv skip                 Skip the current video\n"
                           + "  tv clear                Empty the queue\n";
            }
        }

        private static string QueueText()
        {
            var sb = new StringBuilder();
            VideoManager manager = VideoManager.Instance;

            if (manager?.CurrentInput != null)
            {
                string state = manager.IsLoadingVideo ? " (downloading)" : "";
                sb.Append($"Now playing{state}: {VideoInput.Describe(manager.CurrentVideoUrl ?? manager.CurrentInput)}\n\n");
            }
            else
            {
                sb.Append("Nothing from the queue is playing.\n\n");
            }

            if (VideoQueue.IsEmpty())
            {
                sb.Append("Queue is empty.\n");
            }
            else
            {
                sb.Append($"Videos in queue: {VideoQueue.Count()}\n");
                int i = 1;
                foreach (string entry in VideoQueue.Entries.Take(10))
                    sb.Append($"{i++}. {VideoInput.Describe(entry)}\n");
                if (VideoQueue.Count() > 10)
                    sb.Append($"...and {VideoQueue.Count() - 10} more\n");
            }

            return sb.ToString();
        }

        private static TerminalNode Page(string text)
        {
            var node = ScriptableObject.CreateInstance<TerminalNode>();
            node.displayText = "\n" + text + "\n";
            node.clearPreviousText = true;
            node.maxCharactersToType = 500;
            return node;
        }
    }
}
