using GameNetcodeStuff;
using HarmonyLib;

namespace YoutubeOnTV.Patches
{
    /// <summary>
    /// Hooks the start and end of a lobby so no queue or playback state leaks from one
    /// session into the next, and so joining clients catch up with the host.
    /// </summary>
    internal static class SessionPatch
    {
        [HarmonyPatch(typeof(StartOfRound), "Awake")]
        [HarmonyPostfix]
        private static void OnRoundLoaded()
        {
            VideoManager.Instance?.ResetSession();
        }

        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Disconnect))]
        [HarmonyPostfix]
        private static void OnDisconnected()
        {
            VideoManager.Instance?.ResetSession();
        }

        /// <summary>
        /// Runs on each machine when its own player object is ready, which is the first point
        /// where a joining client can talk to the host.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.ConnectClientToPlayerObject))]
        [HarmonyPostfix]
        private static void OnLocalPlayerConnected()
        {
            NetworkHandler.Instance?.RequestTVState();
        }
    }
}
