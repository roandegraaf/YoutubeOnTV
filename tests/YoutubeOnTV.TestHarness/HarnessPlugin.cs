using System;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace YoutubeOnTV.TestHarness
{
    public enum HarnessRole
    {
        /// <summary>The single-player suite: host alone and exercise everything.</summary>
        Suite,
        /// <summary>Multiplayer test, hosting side.</summary>
        MultiplayerHost,
        /// <summary>Multiplayer test, joining side.</summary>
        MultiplayerClient,
    }

    /// <summary>
    /// Drives the real game through the mod's features and writes a JSON report.
    /// Inert unless the game was launched with one of the autotest arguments.
    /// </summary>
    [BepInPlugin("com.roandegraaf.youtubeontv.testharness", "YoutubeOnTV.TestHarness", "1.0.0")]
    [BepInDependency("com.roandegraaf.youtubeontv")]
    public class HarnessPlugin : BaseUnityPlugin
    {
        public const string AutotestArg = "--youtubeontv-autotest";
        public const string MultiplayerHostArg = "--youtubeontv-autotest-mp-host";
        public const string MultiplayerClientArg = "--youtubeontv-autotest-mp-client";

        // Its own save file, so hosting and unlocking the TV never touch the player's saves.
        public const string TestSaveFile = "LCSaveFileYoutubeOnTVTest";

        internal static ManualLogSource Log;
        internal static HarnessRole Role;

        private void Awake()
        {
            Log = Logger;

            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains(MultiplayerClientArg))
                Role = HarnessRole.MultiplayerClient;
            else if (args.Contains(MultiplayerHostArg))
                Role = HarnessRole.MultiplayerHost;
            else if (args.Contains(AutotestArg))
                Role = HarnessRole.Suite;
            else
            {
                Log.LogInfo($"Inactive (launch with {AutotestArg} to run the in-game tests)");
                return;
            }

            Log.LogInfo($"Autotest mode: {Role}");

            new Harmony("com.roandegraaf.youtubeontv.testharness").PatchAll(typeof(AutoHostPatches));

            var go = new GameObject("YoutubeOnTV.TestRunner");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<TestRunner>();
        }
    }

    /// <summary>
    /// Skips the launch-mode screen and the main menu by hosting (or joining) a LAN lobby.
    /// </summary>
    internal static class AutoHostPatches
    {
        private static bool choseLaunchMode;
        private static bool startedHost;

        /// <summary>Lets the next main menu host again, for the leave-and-rehost scenario.</summary>
        internal static void AllowRehost() => startedHost = false;

        [HarmonyPatch(typeof(PreInitSceneScript), "Start")]
        [HarmonyPostfix]
        private static void ChooseLan(PreInitSceneScript __instance)
        {
            if (choseLaunchMode)
                return;

            choseLaunchMode = true;
            HarnessPlugin.Log.LogInfo("Loading LAN mode");
            // Straight to the scene ChooseLaunchOption(false) would load: that method also
            // saves the player's settings, which a test run has no business touching.
            UnityEngine.SceneManagement.SceneManager.LoadScene("InitSceneLANMode");
        }

        [HarmonyPatch(typeof(MenuManager), "Start")]
        [HarmonyPostfix]
        private static void HostLobby(MenuManager __instance)
        {
            // A failed join lands back in the main menu, so the client keeps retrying from here.
            if (__instance.isInitScene || (startedHost && HarnessPlugin.Role != HarnessRole.MultiplayerClient))
                return;

            startedHost = true;
            __instance.StartCoroutine(HarnessPlugin.Role == HarnessRole.MultiplayerClient
                ? JoinWhenHostIsUp(__instance)
                : HostAfterMenuSettles());
        }

        private static System.Collections.IEnumerator HostAfterMenuSettles()
        {
            yield return new WaitForSeconds(2f);

            HarnessPlugin.Log.LogInfo($"Hosting LAN lobby on save file {HarnessPlugin.TestSaveFile}");
            GameNetworkManager.Instance.currentSaveFileName = HarnessPlugin.TestSaveFile;
            GameNetworkManager.Instance.lobbyHostSettings = new HostSettings("YoutubeOnTV autotest", false, "");
            GameNetworkManager.Instance.StartHost();
        }

        private static System.Collections.IEnumerator JoinWhenHostIsUp(MenuManager menu)
        {
            yield return new WaitForSeconds(2f);

            for (int attempt = 1; attempt <= 30 && StartOfRound.Instance == null; attempt++)
            {
                if (!NetworkManager.Singleton.IsClient)
                {
                    HarnessPlugin.Log.LogInfo($"Joining 127.0.0.1 (attempt {attempt})");
                    NetworkManager.Singleton.GetComponent<UnityTransport>().ConnectionData.Address = "127.0.0.1";
                    menu.StartAClient();
                }
                yield return new WaitForSeconds(10f);
            }
        }
    }
}
