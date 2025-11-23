using HarmonyLib;
using UnityEngine;

namespace YoutubeOnTV.Patches
{
    [HarmonyPatch(typeof(TVScript))]
    internal class TVScriptPatch
    {
        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        private static bool UpdatePatch(TVScript __instance)
        {
            // Attach TVController if it doesn't exist on this instance
            if (__instance.gameObject.GetComponent<TVController>() == null)
            {
                __instance.gameObject.AddComponent<TVController>();
                Debug.Log("Attached TVController to TVScript object.");
            }

            // Block vanilla Update - we handle everything in our custom components
            return false;
        }

        // Patch: Replace TV power state changes to prevent vanilla video playback
        [HarmonyPatch("TurnTVOnOff")]
        [HarmonyPrefix]
        private static bool TurnTVOnOffPatch(TVScript __instance, bool on)
        {
            __instance.tvOn = on;

            if (on)
            {
                Debug.Log("TV turned ON - triggering mod video playback");

                // Play switch-on sound
                __instance.tvSFX.PlayOneShot(__instance.switchTVOn);
                WalkieTalkie.TransmitOneShotAudio(__instance.tvSFX, __instance.switchTVOn, 1f);

                // Notify VideoManager that TV is now on
                if (VideoManager.Instance != null)
                {
                    VideoManager.Instance.OnTVPoweredOn();
                }
            }
            else
            {
                Debug.Log("TV turned OFF");

                // VideoManager now handles pausing the video when TV is off
                // No need to stop the video here - it will be paused in VideoManager.Update()

                // Play switch-off sound
                __instance.tvSFX.PlayOneShot(__instance.switchTVOff);
                WalkieTalkie.TransmitOneShotAudio(__instance.tvSFX, __instance.switchTVOff, 1f);
            }

            // Call SetTVScreenMaterial to update the TV screen display
            var setMatMethod = typeof(TVScript).GetMethod("SetTVScreenMaterial",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            setMatMethod?.Invoke(__instance, new object[] { on });

            return false; // Skip vanilla method
        }
    }
}
