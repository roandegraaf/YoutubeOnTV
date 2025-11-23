using UnityEngine;

namespace YoutubeOnTV
{
    public class NetworkHandler : MonoBehaviour
    {
        public static NetworkHandler Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                YoutubeOnTVBase.Instance.mls.LogInfo("NetworkHandler Awake complete!");
            }
            else
            {
                Destroy(gameObject);
            }
        }

        public void SyncAddVideo(string url)
        {
            Debug.Log($"[Network] SyncAddVideo: {url}");
            VideoQueue.Add(url);
        }

        public void SyncRemoveVideo(int index)
        {
            Debug.Log($"[Network] SyncRemoveVideo: {index} (not supported in new system)");
        }

        public void SyncMoveVideo(int from, int to)
        {
            Debug.Log($"[Network] SyncMoveVideo: {from} -> {to} (not supported in new system)");
        }
    }
}
