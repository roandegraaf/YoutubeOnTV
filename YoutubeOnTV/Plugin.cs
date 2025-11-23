using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using TerminalApi;
using static TerminalApi.TerminalApi;
using static TerminalApi.Events.Events;

namespace YoutubeOnTV
{
    [BepInPlugin("com.roandegraaf.youtubeontv", "YoutubeOnTV", "0.2.3")]
    [BepInDependency("atomic.terminalapi")]
    public class YoutubeOnTVBase : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony("com.roandegraaf.youtubeontv");

        public static YoutubeOnTVBase Instance;

        internal ManualLogSource mls;

        private static bool initialized = false;

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }

            mls = BepInEx.Logging.Logger.CreateLogSource("YoutubeOnTV");

            mls.LogInfo("YoutubeOnTV loaded");

            harmony.PatchAll(typeof(YoutubeOnTVBase));
            harmony.PatchAll(typeof(Patches.TVScriptPatch));
            harmony.PatchAll(typeof(Patches.TerminalPatch));

            // Register Terminal Commands
            RegisterTerminalCommands();

            // Initialize immediately in Awake, but with scene load callback
            if (!initialized)
            {
                initialized = true;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            }
        }

        void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (VideoManager.Instance == null)
            {
                mls.LogInfo($"Scene loaded: {scene.name} - initializing managers...");

                // Initialize Managers
                var go = new GameObject("YoutubeOnTVManagers");
                DontDestroyOnLoad(go);
                mls.LogInfo("Adding VideoManager...");
                go.AddComponent<VideoManager>();
                mls.LogInfo("Adding NetworkHandler...");
                go.AddComponent<NetworkHandler>();
                mls.LogInfo("Adding UIWindow...");
                go.AddComponent<UIWindow>();
                mls.LogInfo("All components added successfully!");
            }
        }

        void RegisterTerminalCommands()
        {
            mls.LogInfo("Registering terminal commands...");

            // Create TV as a verb keyword
            TerminalKeyword tvVerb = CreateTerminalKeyword("tv", true);

            // Create subcommands as noun keywords
            TerminalKeyword addNoun = CreateTerminalKeyword("add");
            TerminalKeyword clearNoun = CreateTerminalKeyword("clear");
            TerminalKeyword skipNoun = CreateTerminalKeyword("skip");
            TerminalKeyword queueNoun = CreateTerminalKeyword("queue");

            // Create nodes with success messages (will be overridden by event handler for dynamic content)
            TerminalNode addNode = CreateTerminalNode("Video added to queue.\n\n", true);
            TerminalNode clearNode = CreateTerminalNode("Queue cleared.\n\n", true);
            TerminalNode skipNode = CreateTerminalNode("Skipped to next video.\n\n", true);
            TerminalNode queueNode = CreateTerminalNode("Queue status displayed.\n\n", true);
            TerminalNode tvHelpNode = CreateTerminalNode(
                "TV CONTROLS\n" +
                "===========\n" +
                "Usage: tv <command> [args]\n\n" +
                "Commands:\n" +
                "  tv add [url/query]  - Add video to queue\n" +
                "  tv clear            - Clear video queue\n" +
                "  tv skip             - Skip current video\n" +
                "  tv queue            - Show queue status\n\n",
                true
            );

            // Set up TV verb with compatible nouns
            tvVerb = tvVerb.AddCompatibleNoun(addNoun, addNode);
            tvVerb = tvVerb.AddCompatibleNoun(clearNoun, clearNode);
            tvVerb = tvVerb.AddCompatibleNoun(skipNoun, skipNode);
            tvVerb = tvVerb.AddCompatibleNoun(queueNoun, queueNode);

            // Set the help node as the special keyword result (when just "tv" is typed)
            tvVerb.specialKeywordResult = tvHelpNode;

            // Set default verb for nouns
            addNoun.defaultVerb = tvVerb;
            clearNoun.defaultVerb = tvVerb;
            skipNoun.defaultVerb = tvVerb;
            queueNoun.defaultVerb = tvVerb;

            // Add all keywords to terminal
            AddTerminalKeyword(tvVerb);
            AddTerminalKeyword(addNoun);
            AddTerminalKeyword(clearNoun);
            AddTerminalKeyword(skipNoun);
            AddTerminalKeyword(queueNoun);

            // Subscribe to terminal parse event to override responses
            TerminalParsedSentence += OnTerminalParsedSentence;

            mls.LogInfo("Terminal commands registered successfully!");
        }

        private void OnTerminalParsedSentence(object sender, TerminalParseSentenceEventArgs e)
        {
            string input = e.SubmittedText.Trim().ToLower();

            // Check if this is a TV command
            if (!input.StartsWith("tv"))
                return;

            mls.LogInfo($"Processing TV command: {e.SubmittedText}");

            // Handle "tv add [url/query]" - needs special handling for arguments
            if (input.StartsWith("tv add"))
            {
                // Extract everything after "tv add "
                string query = "";
                if (e.SubmittedText.Length > 7) // "tv add ".Length
                {
                    int addIndex = e.SubmittedText.ToLower().IndexOf("tv add");
                    if (addIndex != -1)
                    {
                        query = e.SubmittedText.Substring(addIndex + 7).Trim();
                    }
                }

                if (string.IsNullOrEmpty(query))
                {
                    e.ReturnedNode = CreateTerminalNode(
                        "Usage: tv add [url or search query]\n" +
                        "Example: tv add dQw4w9WgXcQ\n" +
                        "Example: tv add https://youtube.com/watch?v=...\n" +
                        "Example: tv add never gonna give you up\n\n",
                        true
                    );
                }
                else
                {
                    VideoQueue.Add(query);
                    e.ReturnedNode = CreateTerminalNode(
                        $"Added to queue: {query}\n" +
                        $"Queue size: {VideoQueue.Count()}\n\n",
                        true
                    );
                }
            }
            // Handle "tv clear"
            else if (input == "tv clear" || input == "clear tv")
            {
                int previousCount = VideoQueue.Count();
                VideoQueue.Clear();

                if (VideoManager.Instance != null)
                {
                    VideoManager.Instance.OnSkipRequested();
                }

                e.ReturnedNode = CreateTerminalNode(
                    $"Cleared {previousCount} video(s) from queue.\n\n",
                    true
                );
            }
            // Handle "tv skip"
            else if (input == "tv skip" || input == "skip tv")
            {
                if (VideoQueue.IsEmpty())
                {
                    e.ReturnedNode = CreateTerminalNode(
                        "Queue is empty. Nothing to skip.\n\n",
                        true
                    );
                }
                else
                {
                    VideoQueue.Skip();

                    if (VideoManager.Instance != null)
                    {
                        VideoManager.Instance.OnSkipRequested();
                    }

                    e.ReturnedNode = CreateTerminalNode(
                        $"Skipped to next video.\n" +
                        $"Queue size: {VideoQueue.Count()}\n\n",
                        true
                    );
                }
            }
            // Handle "tv queue"
            else if (input == "tv queue" || input == "queue tv")
            {
                int count = VideoQueue.Count();

                if (count == 0)
                {
                    e.ReturnedNode = CreateTerminalNode(
                        "Queue is empty.\n\n",
                        true
                    );
                }
                else
                {
                    e.ReturnedNode = CreateTerminalNode(
                        $"Videos in queue: {count}\n\n",
                        true
                    );
                }
            }
            // Just "tv" - help is already shown via specialKeywordResult
        }
    }
}
