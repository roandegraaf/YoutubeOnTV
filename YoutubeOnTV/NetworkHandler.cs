using System.Linq;
using Unity.Netcode;
using UnityEngine;
using LethalNetworkAPI;
using LethalNetworkAPI.Utils;

namespace YoutubeOnTV
{
    /// <summary>
    /// Host-authoritative messaging. Clients send requests to the host; the host applies
    /// them and broadcasts the result. Videos travel as canonical watch URLs, never as
    /// stream URLs (IP-locked by YouTube) or file paths.
    /// </summary>
    public class NetworkHandler : MonoBehaviour
    {
        public static NetworkHandler Instance { get; private set; }

        // The "2" marks the download-based protocol; older versions of the mod ignore these.
        private const string Prefix = "YoutubeOnTV2_";

        private LNetworkMessage<string> addVideoMessage;
        private LNetworkEvent skipVideoEvent;
        private LNetworkEvent clearQueueEvent;
        private LNetworkMessage<string> removeFromQueueMessage;
        private LNetworkMessage<string> loadVideoMessage;
        private LNetworkMessage<VideoPlayData> playVideoMessage;
        private LNetworkMessage<float> syncPlaybackMessage;
        private LNetworkEvent playFallbackEvent;
        private LNetworkEvent requestTVStateEvent;
        private LNetworkMessage<TVStateData> syncTVStateMessage;

        private static bool IsHost => LNetworkUtils.IsHostOrServer;

        // The host applies its own changes directly, so broadcasts skip it. (LethalNetworkAPI's
        // SendOtherClients is a client-to-client message that onClientReceived never sees.)
        private static ulong[] OtherClients =>
            LNetworkUtils.AllConnectedClients.Where(id => id != NetworkManager.Singleton.LocalClientId).ToArray();

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            addVideoMessage = LNetworkMessage<string>.Connect(Prefix + "AddVideo",
                onServerReceived: (entry, _) => HostAddVideo(entry),
                onClientReceived: OnClientReceivedAddVideo);

            skipVideoEvent = LNetworkEvent.Connect(Prefix + "Skip",
                onServerReceived: _ => HostSkip(),
                onClientReceived: OnClientReceivedSkip);

            clearQueueEvent = LNetworkEvent.Connect(Prefix + "Clear",
                onServerReceived: _ => HostClear(),
                onClientReceived: OnClientReceivedClear);

            removeFromQueueMessage = LNetworkMessage<string>.Connect(Prefix + "RemoveFromQueue",
                onClientReceived: OnClientReceivedRemoveFromQueue);

            loadVideoMessage = LNetworkMessage<string>.Connect(Prefix + "LoadVideo",
                onClientReceived: entry => VideoManager.Instance?.OnHostLoadingVideo(entry));

            playVideoMessage = LNetworkMessage<VideoPlayData>.Connect(Prefix + "PlayVideo",
                onClientReceived: data => VideoManager.Instance?.OnHostPlayingVideo(data.input, data.videoUrl, data.startTime));

            syncPlaybackMessage = LNetworkMessage<float>.Connect(Prefix + "SyncPlayback",
                onClientReceived: time => VideoManager.Instance?.OnHostPlaybackTime(time));

            playFallbackEvent = LNetworkEvent.Connect(Prefix + "PlayFallback",
                onClientReceived: () => VideoManager.Instance?.OnHostPlayingFallback());

            requestTVStateEvent = LNetworkEvent.Connect(Prefix + "RequestTVState",
                onServerReceived: OnServerReceivedRequestTVState);

            syncTVStateMessage = LNetworkMessage<TVStateData>.Connect(Prefix + "SyncTVState",
                onClientReceived: state => VideoManager.Instance?.ApplyTVStateFromNetwork(state));

            YoutubeOnTVBase.Log.LogInfo("Network messages registered");
        }

        // ===== Requests (any machine) =====

        /// <param name="entry">An entry already normalised with <see cref="VideoInput.Normalize"/>.</param>
        public void RequestAddVideo(string entry)
        {
            if (IsHost)
                HostAddVideo(entry);
            else
                addVideoMessage.SendServer(entry);
        }

        public void RequestSkipVideo()
        {
            if (IsHost)
                HostSkip();
            else
                skipVideoEvent.InvokeServer();
        }

        public void RequestClearQueue()
        {
            if (IsHost)
                HostClear();
            else
                clearQueueEvent.InvokeServer();
        }

        /// <summary>
        /// Asks the host for the TV and queue state; called once the local client has joined.
        /// </summary>
        public void RequestTVState()
        {
            if (IsHost)
                return;

            YoutubeOnTVBase.Log.LogInfo("Requesting TV state from host");
            requestTVStateEvent.InvokeServer();
        }

        // ===== Host handlers =====

        private void HostAddVideo(string entry)
        {
            // Normalise again: a client could run an older parser.
            entry = VideoInput.Normalize(entry);
            if (entry == null)
                return;

            YoutubeOnTVBase.Log.LogInfo($"[Host] Adding to queue: {entry}");
            VideoQueue.Add(entry);
            addVideoMessage.SendClients(entry, OtherClients);
        }

        private void HostSkip()
        {
            YoutubeOnTVBase.Log.LogInfo("[Host] Skip");
            VideoManager.Instance?.OnSkipRequested();
            skipVideoEvent.InvokeClients(OtherClients);
        }

        private void HostClear()
        {
            YoutubeOnTVBase.Log.LogInfo("[Host] Clear queue");
            VideoManager.Instance?.OnClearRequested();
            clearQueueEvent.InvokeClients(OtherClients);
        }

        private void OnServerReceivedRequestTVState(ulong clientId)
        {
            if (VideoManager.Instance == null)
                return;

            YoutubeOnTVBase.Log.LogInfo($"[Host] Sending TV state to client {clientId}");
            // Only the client that asked; everyone else is already in sync.
            syncTVStateMessage.SendClient(VideoManager.Instance.GetCurrentTVState(), clientId);
        }

        // ===== Host broadcasts =====

        public void BroadcastRemoveFromQueue(string entry)
        {
            if (IsHost)
                removeFromQueueMessage.SendClients(entry, OtherClients);
        }

        public void BroadcastLoadVideo(string entry)
        {
            if (IsHost)
                loadVideoMessage.SendClients(entry, OtherClients);
        }

        public void BroadcastPlayVideo(string entry, string videoUrl, float startTime)
        {
            if (!IsHost)
                return;

            YoutubeOnTVBase.Log.LogInfo($"Broadcasting play: {videoUrl} ({entry}) at {startTime:0.0}s");
            playVideoMessage.SendClients(new VideoPlayData { input = entry, videoUrl = videoUrl, startTime = startTime }, OtherClients);
        }

        public void BroadcastPlaybackTime(float time)
        {
            if (IsHost)
                syncPlaybackMessage.SendClients(time, OtherClients);
        }

        public void BroadcastPlayFallback()
        {
            if (IsHost)
                playFallbackEvent.InvokeClients(OtherClients);
        }

        // ===== Client handlers =====

        private void OnClientReceivedAddVideo(string entry)
        {
            YoutubeOnTVBase.Log.LogInfo($"[Client] Adding to queue: {entry}");
            VideoQueue.Add(entry);
        }

        private void OnClientReceivedRemoveFromQueue(string entry)
        {
            VideoQueue.RemoveFirstMatch(entry);
        }

        private void OnClientReceivedSkip()
        {
            VideoManager.Instance?.OnSkipRequested();
        }

        private void OnClientReceivedClear()
        {
            VideoManager.Instance?.OnClearRequested();
        }
    }

    [System.Serializable]
    public struct VideoPlayData
    {
        public string input;
        public string videoUrl;
        public float startTime;
    }

    /// <summary>
    /// Everything a joining client needs to catch up.
    /// </summary>
    [System.Serializable]
    public struct TVStateData
    {
        public bool isTVOn;
        public int mode;
        public string input;
        public string videoUrl;
        public float playbackTime;
        public string[] queue;
    }
}
