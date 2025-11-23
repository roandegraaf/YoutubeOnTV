using UnityEngine;
using LethalNetworkAPI;
using LethalNetworkAPI.Utils;

namespace YoutubeOnTV
{
    public class NetworkHandler : MonoBehaviour
    {
        public static NetworkHandler Instance { get; private set; }

        // Network messages
        private LNetworkMessage<string> addVideoMessage;
        private LNetworkEvent skipVideoEvent;
        private LNetworkEvent clearQueueEvent;
        private LNetworkMessage<VideoPlayData> playVideoMessage;
        private LNetworkMessage<float> syncPlaybackMessage;
        private LNetworkEvent playFallbackEvent;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);

                InitializeNetworkMessages();

                YoutubeOnTVBase.Instance.mls.LogInfo("NetworkHandler initialized with LethalNetworkAPI!");
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void InitializeNetworkMessages()
        {
            // Add video to queue
            addVideoMessage = LNetworkMessage<string>.Connect(
                identifier: "YoutubeOnTV_AddVideo",
                onServerReceived: OnServerReceivedAddVideo,
                onClientReceived: OnClientReceivedAddVideo
            );

            // Skip current video
            skipVideoEvent = LNetworkEvent.Connect(
                identifier: "YoutubeOnTV_Skip",
                onServerReceived: OnServerReceivedSkip,
                onClientReceived: OnClientReceivedSkip
            );

            // Clear queue
            clearQueueEvent = LNetworkEvent.Connect(
                identifier: "YoutubeOnTV_Clear",
                onServerReceived: OnServerReceivedClear,
                onClientReceived: OnClientReceivedClear
            );

            // Play video (host broadcasts to clients)
            playVideoMessage = LNetworkMessage<VideoPlayData>.Connect(
                identifier: "YoutubeOnTV_PlayVideo",
                onClientReceived: OnClientReceivedPlayVideo
            );

            // Sync playback position (host broadcasts to clients)
            syncPlaybackMessage = LNetworkMessage<float>.Connect(
                identifier: "YoutubeOnTV_SyncPlayback",
                onClientReceived: OnClientReceivedSyncPlayback
            );

            // Play fallback video (host broadcasts to clients)
            playFallbackEvent = LNetworkEvent.Connect(
                identifier: "YoutubeOnTV_PlayFallback",
                onClientReceived: OnClientReceivedPlayFallback
            );

            YoutubeOnTVBase.Instance.mls.LogInfo("Network messages initialized!");
        }

        // ===== PUBLIC API =====

        /// <summary>
        /// Request to add a video to the queue (called by client)
        /// </summary>
        public void RequestAddVideo(string input)
        {
            if (LNetworkUtils.IsHostOrServer)
            {
                // If we're the host, add directly and broadcast
                OnServerReceivedAddVideo(input, 0);
            }
            else
            {
                // Send to server
                addVideoMessage.SendServer(input);
            }
        }

        /// <summary>
        /// Request to skip the current video (called by client)
        /// </summary>
        public void RequestSkipVideo()
        {
            if (LNetworkUtils.IsHostOrServer)
            {
                // If we're the host, skip directly and broadcast
                OnServerReceivedSkip(0);
            }
            else
            {
                // Send to server
                skipVideoEvent.InvokeServer();
            }
        }

        /// <summary>
        /// Request to clear the queue (called by client)
        /// </summary>
        public void RequestClearQueue()
        {
            if (LNetworkUtils.IsHostOrServer)
            {
                // If we're the host, clear directly and broadcast
                OnServerReceivedClear(0);
            }
            else
            {
                // Send to server
                clearQueueEvent.InvokeServer();
            }
        }

        /// <summary>
        /// Broadcast video playback to all clients (host only)
        /// </summary>
        public void BroadcastPlayVideo(string url, float startTime)
        {
            if (!LNetworkUtils.IsHostOrServer)
            {
                YoutubeOnTVBase.Instance.mls.LogWarning("Only host can broadcast play video!");
                return;
            }

            var data = new VideoPlayData { url = url, startTime = startTime };
            playVideoMessage.SendClients(data);

            YoutubeOnTVBase.Instance.mls.LogInfo($"Broadcasting play video: {url} at {startTime}s");
        }

        /// <summary>
        /// Broadcast playback position to keep clients in sync (host only)
        /// </summary>
        public void BroadcastPlaybackTime(float time)
        {
            if (!LNetworkUtils.IsHostOrServer)
                return;

            syncPlaybackMessage.SendClients(time);
        }

        /// <summary>
        /// Broadcast to all clients to play fallback video (host only)
        /// </summary>
        public void BroadcastPlayFallback()
        {
            if (!LNetworkUtils.IsHostOrServer)
            {
                YoutubeOnTVBase.Instance.mls.LogWarning("Only host can broadcast play fallback!");
                return;
            }

            playFallbackEvent.InvokeClients();
            YoutubeOnTVBase.Instance.mls.LogInfo("Broadcasting play fallback");
        }

        // ===== SERVER CALLBACKS =====

        private void OnServerReceivedAddVideo(string input, ulong clientId)
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Host] Received add video request: {input}");

            // Broadcast to all clients to add the video
            addVideoMessage.SendClients(input);
        }

        private void OnServerReceivedSkip(ulong clientId)
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Host] Received skip request");

            // Broadcast to all clients to skip
            skipVideoEvent.InvokeClients();
        }

        private void OnServerReceivedClear(ulong clientId)
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Host] Received clear queue request");

            // Broadcast to all clients to clear
            clearQueueEvent.InvokeClients();
        }

        // ===== CLIENT CALLBACKS =====

        private void OnClientReceivedAddVideo(string input)
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Client] Adding video to queue: {input}");
            VideoQueue.Add(input);
        }

        private void OnClientReceivedSkip()
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Client] Skipping video");

            VideoQueue.Skip();

            if (VideoManager.Instance != null)
            {
                VideoManager.Instance.OnSkipRequested();
            }
        }

        private void OnClientReceivedClear()
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Client] Clearing queue");

            VideoQueue.Clear();

            if (VideoManager.Instance != null)
            {
                VideoManager.Instance.OnSkipRequested();
            }
        }

        private void OnClientReceivedPlayVideo(VideoPlayData data)
        {
            YoutubeOnTVBase.Instance.mls.LogInfo($"[Client] Received play video: {data.url} at {data.startTime}s");

            if (VideoManager.Instance != null)
            {
                VideoManager.Instance.PlayVideoFromNetwork(data.url, data.startTime);
            }
        }

        private void OnClientReceivedSyncPlayback(float time)
        {
            if (VideoManager.Instance != null)
            {
                VideoManager.Instance.SyncPlaybackTime(time);
            }
        }

        private void OnClientReceivedPlayFallback()
        {
            YoutubeOnTVBase.Instance.mls.LogInfo("[Client] Received play fallback command");

            if (VideoManager.Instance != null)
            {
                VideoManager.Instance.PlayFallbackFromNetwork();
            }
        }
    }

    // Data structure for video playback sync
    [System.Serializable]
    public struct VideoPlayData
    {
        public string url;
        public float startTime;
    }
}
