using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Crest.Services
{
    /// <summary>
    /// Optional "real" spectrum: captures what Windows is playing (WASAPI loopback – nothing is
    /// recorded or stored), runs a small FFT and turns it into N log-spaced bar levels (0..1).
    ///
    /// Only runs while the notch actually needs it (real mode on + media playing).
    /// </summary>
    public sealed class AudioSpectrumService : IDisposable
    {
        private const int FftSize = 2048;
        private const double MinFrequency = 45;
        private const double MaxFrequency = 14000;
        private const double FloorDb = -62;
        private const double CeilingDb = -12;

        private readonly object _lock = new();
        private readonly float[] _ring = new float[FftSize];
        private int _ringPos;
        private long _lastDataTick;

        private readonly double[] _window = new double[FftSize];
        private readonly double[] _re = new double[FftSize];
        private readonly double[] _im = new double[FftSize];

        private WasapiLoopbackCapture? _capture;
        private int _sampleRate = 48000;
        private int _channels = 2;
        private int _bytesPerSample = 4;
        private bool _isFloat = true;

        public AudioSpectrumService()
        {
            for (int i = 0; i < FftSize; i++)
                _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // Hann
        }

        public bool IsRunning => _capture != null;

        public void Start()
        {
            if (_capture != null) return;
            try
            {
                var capture = new WasapiLoopbackCapture();
                var fmt = capture.WaveFormat;
                _sampleRate = fmt.SampleRate;
                _channels = Math.Max(1, fmt.Channels);
                _bytesPerSample = Math.Max(1, fmt.BitsPerSample / 8);
                _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                           || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null) App.LogError("Loopback capture stopped", e.Exception);
                };
                capture.StartRecording();
                _capture = capture;
            }
            catch (Exception ex)
            {
                App.LogError("Starting loopback capture failed", ex);
                _capture = null;
            }
        }

        public void Stop()
        {
            var capture = _capture;
            _capture = null;
            if (capture == null) return;

            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.StopRecording();
            }
            catch
            {
                // ignore
            }

            // Dispose joins the capture thread – keep that off the UI thread.
            Task.Run(() =>
            {
                try { capture.Dispose(); } catch { /* ignore */ }
            });

            lock (_lock)
            {
                Array.Clear(_ring);
                _ringPos = 0;
            }
        }

        /// <summary>Restart capture (e.g. after the default playback device changed).</summary>
        public void Restart()
        {
            if (_capture == null) return;
            Stop();
            Start();
        }

        // Capture thread.
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int frameBytes = _bytesPerSample * _channels;
            if (frameBytes <= 0 || e.BytesRecorded < frameBytes) return;

            byte[] buf = e.Buffer;
            lock (_lock)
            {
                for (int i = 0; i + frameBytes <= e.BytesRecorded; i += frameBytes)
                {
                    float sum = 0;
                    for (int c = 0; c < _channels; c++)
                    {
                        int o = i + c * _bytesPerSample;
                        if (_isFloat && _bytesPerSample == 4) sum += BitConverter.ToSingle(buf, o);
                        else if (_bytesPerSample == 2) sum += BitConverter.ToInt16(buf, o) / 32768f;
                        else if (_bytesPerSample == 4) sum += BitConverter.ToInt32(buf, o) / 2147483648f;
                    }
                    _ring[_ringPos] = sum / _channels;
                    _ringPos = (_ringPos + 1) % FftSize;
                }
            }
            Interlocked.Exchange(ref _lastDataTick, Environment.TickCount64);
        }

        /// <summary>Fills <paramref name="levels"/> with 0..1 band levels. Called from the UI timer.</summary>
        public void ComputeBands(double[] levels)
        {
            int n = levels.Length;
            if (n == 0) return;

            // Loopback delivers nothing while Windows is silent → fall to zero.
            if (_capture == null || Environment.TickCount64 - Interlocked.Read(ref _lastDataTick) > 250)
            {
                Array.Clear(levels);
                return;
            }

            lock (_lock)
            {
                for (int i = 0; i < FftSize; i++)
                {
                    _re[i] = _ring[(_ringPos + i) % FftSize] * _window[i];
                    _im[i] = 0;
                }
            }

            Fft(_re, _im);

            double nyquist = _sampleRate / 2.0;
            double maxF = Math.Min(MaxFrequency, nyquist * 0.95);
            double ratio = maxF / MinFrequency;
            double binHz = (double)_sampleRate / FftSize;

            for (int b = 0; b < n; b++)
            {
                double f0 = MinFrequency * Math.Pow(ratio, (double)b / n);
                double f1 = MinFrequency * Math.Pow(ratio, (double)(b + 1) / n);
                int k0 = Math.Max(1, (int)Math.Floor(f0 / binHz));
                int k1 = Math.Min(FftSize / 2 - 1, Math.Max(k0 + 1, (int)Math.Ceiling(f1 / binHz)));

                double peak = 0;
                for (int k = k0; k <= k1; k++)
                {
                    double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                    if (mag > peak) peak = mag;
                }

                // Normalise: a full-scale sine ≈ 0 dB (Hann window gain 0.5 → ×4/N).
                double amplitude = peak * 4.0 / FftSize;
                double db = 20 * Math.Log10(amplitude + 1e-9);
                // Gentle tilt so treble bars aren't always flat.
                db += 3.0 * b / Math.Max(1, n - 1);
                levels[b] = Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
            }
        }

        /// <summary>In-place iterative radix-2 FFT.</summary>
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                double wRe = Math.Cos(angle), wIm = Math.Sin(angle);
                int half = len >> 1;

                for (int i = 0; i < n; i += len)
                {
                    double curRe = 1, curIm = 0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double tRe = re[b] * curRe - im[b] * curIm;
                        double tIm = re[b] * curIm + im[b] * curRe;
                        re[b] = re[a] - tRe;
                        im[b] = im[a] - tIm;
                        re[a] += tRe;
                        im[a] += tIm;

                        double nextRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                    }
                }
            }
        }

        public void Dispose() => Stop();
    }
}
