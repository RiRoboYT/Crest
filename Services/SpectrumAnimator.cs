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
    /// Fake "audio spectrum": N thin bars whose heights randomly change every ~100 ms
    /// while media is playing. Not real audio analysis (by design – zero audio capture cost).
    ///
    /// Performance notes:
    ///  * Bars animate a ScaleTransform (render-only, no layout passes).
    ///  * The same ScaleTransform instances are shared by every host panel, so the
    ///    collapsed and expanded views stay in sync for free.
    ///  * When nothing plays the timer is stopped and bars settle to small dots; WPF stops
    ///    rendering once the last animation finishes → ~0% CPU idle.
    /// </summary>
    public sealed class SpectrumAnimator
    {
        private static readonly IEasingFunction Ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        private readonly List<Panel> _hosts = new();
        private readonly List<ScaleTransform> _transforms = new();
        private readonly DispatcherTimer _timer;
        private readonly Random _rng = new();

        private int _barCount = 6;
        private double _barWidth = 3;
        private double _spacing = 2.5;
        private double _maxHeight = 16;
        private Brush _brush = Brushes.White;
        private int _intervalMs = 100;

        private bool _playing;
        private bool _suspended;

        public SpectrumAnimator(params Panel[] hosts)
        {
            _hosts.AddRange(hosts);
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(_intervalMs) };
            _timer.Tick += (_, _) => Step();
            Rebuild();
        }

        private double RestScale => Math.Clamp(_barWidth / _maxHeight, 0.08, 0.5);

        public void Configure(int barCount, double barWidth, double spacing, double maxHeight, Brush brush, int intervalMs)
        {
            bool rebuild = barCount != _barCount || barWidth != _barWidth || spacing != _spacing
                           || maxHeight != _maxHeight || !ReferenceEquals(brush, _brush);

            _barCount = barCount;
            _barWidth = barWidth;
            _spacing = spacing;
            _maxHeight = maxHeight;
            _brush = brush;
            _intervalMs = intervalMs;
            _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);

            if (rebuild) Rebuild();
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
            if (shouldRun)
            {
                if (!_timer.IsEnabled)
                {
                    Step();
                    _timer.Start();
                }
            }
            else
            {
                _timer.Stop();
                Settle();
            }
        }

        private void Rebuild()
        {
            _transforms.Clear();
            for (int i = 0; i < _barCount; i++)
                _transforms.Add(new ScaleTransform(1, RestScale));

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
        }

        private void Step()
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

        private void Settle()
        {
            var anim = new DoubleAnimation(RestScale, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = Ease };
            anim.Freeze();
            foreach (var t in _transforms)
                t.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }
}
