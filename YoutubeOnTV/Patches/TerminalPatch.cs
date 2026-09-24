using HarmonyLib;
using TMPro;

namespace YoutubeOnTV.Patches
{
    [HarmonyPatch(typeof(Terminal))]
    internal class TerminalPatch
    {
        /// <summary>
        /// Answers "tv ..." commands before the vanilla parser sees them. The vanilla parser
        /// lowercases and strips punctuation (which would mangle URLs and video ids) and
        /// could match words of a search query against door codes or other keywords.
        /// </summary>
        [HarmonyPatch("ParsePlayerSentence")]
        [HarmonyPrefix]
        private static bool ParsePlayerSentencePrefix(Terminal __instance, ref TerminalNode __result)
        {
            string text = __instance.screenText.text;
            int typed = __instance.textAdded;
            if (typed <= 0 || typed > text.Length)
                return true;

            TerminalNode node = YoutubeOnTVBase.HandleTerminalCommand(text.Substring(text.Length - typed));
            if (node == null)
                return true;

            __result = node;
            return false;
        }

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
