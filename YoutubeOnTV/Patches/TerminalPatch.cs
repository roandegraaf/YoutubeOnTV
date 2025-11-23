using HarmonyLib;
using TMPro;

namespace YoutubeOnTV.Patches
{
    [HarmonyPatch(typeof(Terminal))]
    internal class TerminalPatch
    {
        // Patch the TextChanged method to allow longer input for YouTube URLs
        [HarmonyPatch("TextChanged")]
        [HarmonyPrefix]
        private static bool TextChangedPatch(Terminal __instance, string newText, ref int ___textAdded, ref string ___currentText, ref bool ___modifyingText)
        {
            if (__instance.currentNode == null)
            {
                return false;
            }

            if (___modifyingText)
            {
                ___modifyingText = false;
                return false;
            }

            ___textAdded += newText.Length - ___currentText.Length;

            if (___textAdded < 0)
            {
                __instance.screenText.text = ___currentText;
                ___textAdded = 0;
                return false;
            }

            // Increase the character limit from the default (usually 35-50) to 500 to accommodate YouTube URLs
            int maxCharacterLimit = 500;

            if (___textAdded > maxCharacterLimit)
            {
                __instance.screenText.text = ___currentText;
                ___textAdded = maxCharacterLimit;
                return false;
            }

            ___currentText = newText;
            return false; // Skip original method
        }
    }
}
