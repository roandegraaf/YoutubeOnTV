using UnityEngine;

namespace YoutubeOnTV.TestHarness
{
    /// <summary>
    /// Sits in the TV AudioSource's filter chain and records the loudest sample that
    /// passed through, which is what actually reaches the speakers.
    /// </summary>
    public class AudioProbe : MonoBehaviour
    {
        private readonly object gate = new object();
        private float peak;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i] < 0 ? -data[i] : data[i];
                if (v > max)
                    max = v;
            }

            lock (gate)
            {
                if (max > peak)
                    peak = max;
            }
        }

        /// <summary>Returns the peak since the last call and starts a new window.</summary>
        public float TakePeak()
        {
            lock (gate)
            {
                float p = peak;
                peak = 0f;
                return p;
            }
        }
    }
}
