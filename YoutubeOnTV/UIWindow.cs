using UnityEngine;
using UnityEngine.InputSystem;

namespace YoutubeOnTV
{
    public class UIWindow : MonoBehaviour
    {
        private Rect windowRect = new Rect(20, 20, 450, 250);
        private bool showWindow = false;

        private void Awake()
        {
            YoutubeOnTVBase.Instance.mls.LogInfo("UIWindow initialized!");
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[Key.F1].wasPressedThisFrame)
            {
                showWindow = !showWindow;
                if (showWindow)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                else
                {
                    Cursor.visible = false;
                    Cursor.lockState = CursorLockMode.Locked;
                }
            }
        }

        private void OnGUI()
        {
            if (showWindow)
            {
                windowRect = GUI.Window(0, windowRect, DrawWindow, "YoutubeOnTV - Help");
            }
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("YoutubeOnTV - YouTube Videos on TV", GUI.skin.GetStyle("boldLabel"));
            GUILayout.Space(10);

            GUILayout.Label("Use chat commands to control the mod:");
            GUILayout.Space(5);

            GUILayout.Label("/add <url or search>  - Add video to queue");
            GUILayout.Label("   Examples:");
            GUILayout.Label("   /add https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            GUILayout.Label("   /add never gonna give you up");
            GUILayout.Space(5);

            GUILayout.Label("/clear  - Clear the queue");
            GUILayout.Space(5);

            GUILayout.Label("/skip  - Skip to next video");
            GUILayout.Space(5);

            GUILayout.Label("/queue  - Show queue status");
            GUILayout.Space(10);

            int queueCount = VideoQueue.Count();
            bool isLoading = VideoManager.Instance != null && VideoManager.Instance.IsLoadingVideo;

            GUILayout.Label($"Current queue size: {queueCount}");
            GUILayout.Label($"Status: {(isLoading ? "Loading video..." : "Ready")}");

            GUILayout.Space(10);
            GUILayout.Label("Press F1 to close this window");

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
