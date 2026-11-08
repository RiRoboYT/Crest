using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Crest.Services
{
    /// <summary>
    /// The spectrum bars. Two modes:
    ///  * Fake (default): random heights every ~100 ms – zero audio cost.
    ///  * Real: levels from <see cref="AudioSpectrumService"/> (WASAPI loopback + FFT) at ~30 fps.
    ///
    /// Bars only move while media is playing. Performance notes:
    ///  * Bars animate a ScaleTransform (render-only, no layout passes).
    ///  * The same ScaleTransform instances are shared by every host panel, so all views stay in sync.
    ///  * The brush is owned by the caller and can be recolored/animated without rebuilding bars.
    ///  * When idle the timer is stopped and WPF stops rendering → ~0% CPU.
    /// </summary>
    public sealed class SpectrumAnimator
    {
        private static readonly IEasingFunction Ease = CreateEase();
        private const int RealFrameMs = 33;

        private readonly List<Panel> _hosts = new();
        private readonly List<ScaleTransform> _transforms = new();
        private readonly DispatcherTimer _timer;
        private readonly Random _rng = new();

        private int _barCount = 6;
        private double _barWidth = 3;
        private double _spacing = 2.5;
        private double _maxHeight = 16;
        private Brush _brush;
        private int _intervalMs = 100;

        private AudioSpectrumService? _analyzer;
        private double[] _levels = Array.Empty<double>();
        private double[] _smoothed = Array.Empty<double>();

        private bool _playing;
        private bool _suspended;
        private bool _running;
        private bool _runningReal;

        public SpectrumAnimator(Brush brush, params Panel[] hosts)
        {
            _brush = brush;
            _hosts.AddRange(hosts);
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(_intervalMs) };
            _timer.Tick += (_, _) => OnTick();
            Rebuild();
        }

        private static IEasingFunction CreateEase()
        {
            var e = new SineEase { EasingMode = EasingMode.EaseInOut };
            e.Freeze();
            return e;
        }

        private double RestScale => Math.Clamp(_barWidth / _maxHeight, 0.08, 0.5);

        public void Configure(int barCount, double barWidth, double spacing, double maxHeight, int intervalMs)
        {
            bool rebuild = barCount != _barCount || barWidth != _barWidth || spacing != _spacing || maxHeight != _maxHeight;

            _barCount = barCount;
            _barWidth = barWidth;
            _spacing = spacing;
            _maxHeight = maxHeight;
            _intervalMs = intervalMs;

            if (rebuild) Rebuild();
            if (_running && !_runningReal) _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
        }

        /// <summary>Real mode when an analyzer is given, fake mode when null.</summary>
        public void SetAnalyzer(AudioSpectrumService? analyzer)
        {
            if (ReferenceEquals(_analyzer, analyzer)) return;

            bool wasRunning = _running;
            if (wasRunning) StopRunning(settle: false);
            _analyzer = analyzer;
            UpdateRunning();
        }

        /// <summary>Animate only while media is actually playing.</summary>
        public void SetPlaying(bool playing)
        {
            if (_playing == playing) return;
            _playing = playing;
            UpdateRunning();
        }

        /// <summary>Stop everything while the notch is hidden.</summary>
        public void SetSuspended(bool suspended)
        {
            if (_suspended == suspended) return;
            _suspended = suspended;
            UpdateRunning();
        }

        private void UpdateRunning()
        {
            bool shouldRun = _playing && !_suspended;
            if (shouldRun && !_running) StartRunning();
            else if (!shouldRun && _running) StopRunning(settle: true);
        }

        private void StartRunning()
        {
            _running = true;
            _runningReal = _analyzer != null;

            if (_runningReal)
            {
                _analyzer!.Start();
                // Real mode drives ScaleY directly every frame → drop any running animations.
                foreach (var t in _transforms) t.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                Array.Clear(_smoothed);
                _timer.Interval = TimeSpan.FromMilliseconds(RealFrameMs);
            }
            else
            {
                _timer.Interval = TimeSpan.FromMilliseconds(_intervalMs);
                FakeStep();
            }
            _timer.Start();
        }

        private void StopRunning(bool settle)
        {
            _timer.Stop();
            if (_runningReal) _analyzer?.Stop();
            _running = false;
            _runningReal = false;
            if (settle) Settle();
        }

        private void OnTick()
        {
            if (_runningReal) RealStep();
            else FakeStep();
        }

        private void Rebuild()
        {
            _transforms.Clear();
            for (int i = 0; i < _barCount; i++)
                _transforms.Add(new ScaleTransform(1, RestScale));

            _levels = new double[_barCount];
            _smoothed = new double[_barCount];

            foreach (var host in _hosts)
            {
                host.Children.Clear();
                host.Height = _maxHeight;
                for (int i = 0; i < _barCount; i++)
                {
                    var bar = new Rectangle
                    {
                        Width = _barWidth,
                        Height = _maxHeight,
                        RadiusX = _barWidth / 2,
                        RadiusY = _barWidth / 2,
                        Fill = _brush,
                        Margin = new Thickness(i == 0 ? 0 : _spacing, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        RenderTransformOrigin = new Point(0.5, 0.5), // grow from the middle
                        RenderTransform = _transforms[i],
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = false
                    };
                    host.Children.Add(bar);
                }
            }

            // A rebuild while playing must not leave the new bars frozen.
            if (_running && _runningReal)
                foreach (var t in _transforms) t.ScaleY = RestScale;
        }

        private void FakeStep()
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(_intervalMs * 0.95));
            double rest = RestScale;

            for (int i = 0; i < _transforms.Count; i++)
            {
                // Middle bars tend to be taller → looks more like a real spectrum.
                double center = 1 - Math.Abs(i - (_transforms.Count - 1) / 2.0) / Math.Max(1, _transforms.Count);
                double target = rest + (1 - rest) * Math.Pow(_rng.NextDouble(), 1.3) * (0.55 + 0.45 * center);

                var anim = new DoubleAnimation(target, duration) { EasingFunction = Ease };
                anim.Freeze();
                _transforms[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        }

        private void RealStep()
        {
            if (_analyzer == null || _levels.Length != _transforms.Count) return;

            _analyzer.ComputeBands(_levels);
            double rest = RestScale;

            for (int i = 0; i < _transforms.Count; i++)
            {
                double target = _levels[i];
                double current = _smoothed[i];
                // Fast attack, slower release → punchy but not jittery.
                current += (target - current) * (target > current ? 0.65 : 0.2);
                _smoothed[i] = current;
                _transforms[i].ScaleY = rest + (1 - rest) * current;
            }
        }

        private void Settle()
        {
            var anim = new DoubleAnimation(RestScale, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = Ease };
            anim.Freeze();
            foreach (var t in _transforms)
                t.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }
}
