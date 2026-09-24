using System.Collections.Generic;

namespace YoutubeOnTV
{
    /// <summary>
    /// The queue of videos waiting to play. Every machine keeps its own copy; the host
    /// broadcasts adds, removals and clears so they stay in step.
    /// </summary>
    public static class VideoQueue
    {
        private static readonly List<string> _entries = new List<string>();

        public static IReadOnlyList<string> Entries => _entries;

        /// <summary>
        /// Queues an entry that has already been through <see cref="VideoInput.Normalize"/>.
        /// </summary>
        public static void Add(string entry)
        {
            if (string.IsNullOrEmpty(entry))
                return;

            _entries.Add(entry);
            YoutubeOnTVBase.Log.LogInfo($"[VideoQueue] Added '{entry}'. Queue size: {_entries.Count}");
        }

        public static void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// Replaces the whole queue, used when a joining client receives the host's state.
        /// </summary>
        public static void ReplaceWith(IEnumerable<string> entries)
        {
            _entries.Clear();
            if (entries != null)
                _entries.AddRange(entries);
        }

        public static bool IsEmpty()
        {
            return _entries.Count == 0;
        }

        public static int Count()
        {
            return _entries.Count;
        }

        public static string Peek()
        {
            return _entries.Count == 0 ? null : _entries[0];
        }

        /// <summary>
        /// Removes and returns the entry at the front of the queue.
        /// </summary>
        public static string Dequeue()
        {
            if (_entries.Count == 0)
                return null;

            string removed = _entries[0];
            _entries.RemoveAt(0);
            YoutubeOnTVBase.Log.LogInfo($"[VideoQueue] Dequeued '{removed}'. Remaining: {_entries.Count}");
            return removed;
        }

        /// <summary>
        /// Removes the first entry matching the given one. No-op when it is not queued,
        /// which keeps a client with a desynced queue from corrupting its own ordering.
        /// </summary>
        public static void RemoveFirstMatch(string entry)
        {
            int index = _entries.IndexOf(entry);
            if (index >= 0)
                _entries.RemoveAt(index);
        }
    }
}
