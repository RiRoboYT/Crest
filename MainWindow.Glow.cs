using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest
{
    /// <summary>
    /// The ambient glow under the notch while music plays:
    ///  • colored like the cover art (violet when there is no colorful cover),
    ///  • drifts ("breathes") between the cover's main colors when it has more than one,
    ///  • pulses with the beat of whatever is playing (WASAPI loopback, shared with the real spectrum),
    ///  • size / brightness set by the "Glow intensity" slider.
    /// While the glow is visible the window is made click-through everywhere except over the notch
    /// itself, so the soft glow never blocks clicks on the app underneath.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Room (DIP) left around the shape so the biggest glow isn't cut off.</summary>
        private const double GlowRoom = 110;
        private static readonly Color DefaultGlowColor = Color.FromRgb(0x7B, 0x6C, 0xFF);
        /// <summary>Seconds to drift from one cover color to the next (a bit faster on loud parts).</summary>
        private const double GlowColorSeconds = 4.0;
        private static readonly TimeSpan GlowFastInterval = TimeSpan.FromMilliseconds(33);
        private static readonly TimeSpan GlowIdleInterval = TimeSpan.FromMilliseconds(120);

        private readonly LinearGradientBrush _glowGradient = CreateGlowGradient();
        private DispatcherTimer? _glowTimer;
        private Color[] _glowPalette = { DefaultGlowColor };
        private bool _glowPaletteFromAlbum;
        private long _glowDefaultDue;                          // pending fall-back to violet (tick count), 0 = none
        private readonly double[,] _glowRgb = new double[3, 3]; // colors the 3 gradient stops show right now
        private bool _glowOn;                                  // should be visible
        private double _glowFade;                              // 0..1 fade in / out
        private bool _glowListening;                           // holds a reference on the loopback analyzer
        private readonly double[] _glowBands = new double[8];
        private double _glowLevel;                             // smoothed music level 0..1
        private double _glowAverage = 0.4;                     // slow average energy; beats are measured against it
        private long _glowLastSignal;
        private double _glowColorPhase;
        private long _glowLastTick;
        private bool _clickThrough;

        private static LinearGradientBrush CreateGlowGradient()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            brush.GradientStops.Add(new GradientStop(DefaultGlowColor, 0));
            brush.GradientStops.Add(new GradientStop(DefaultGlowColor, 0.5));
            brush.GradientStops.Add(new GradientStop(DefaultGlowColor, 1));
            return brush;
        }

        private void InitGlow()
        {
            for (int i = 0; i < 3; i++)
            {
                _glowRgb[i, 0] = DefaultGlowColor.R;
                _glowRgb[i, 1] = DefaultGlowColor.G;
                _glowRgb[i, 2] = DefaultGlowColor.B;
            }
            GlowLayer.BeginAnimation(OpacityProperty, null);
            GlowLayer.Opacity = 0;

            _glowTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = GlowFastInterval };
            _glowTimer.Tick += (_, _) => OnGlowTick();
        }

        private void CloseGlow()
        {
            _glowTimer?.Stop();
            if (_glowListening)
            {
                _glowListening = false;
                _svc.Analyzer.Stop();
            }
            SetClickThrough(false);
        }

        // =====================================================================
        //  Colors
        // =====================================================================

        /// <summary>Called whenever the media info changes. Takes the cover's main colors.</summary>
        private void SetGlowPalette(IReadOnlyList<Color> album, bool animate)
        {
            var colors = new List<Color>();
            foreach (var c in album)
            {
                // Bright and fairly saturated reads best as light on a dark desktop.
                ColorHelper.ToHsv(c, out double h, out double s, out double v);
                colors.Add(ColorHelper.FromHsv(h, Math.Clamp(s * 1.1, 0.45, 0.9), Math.Max(v, 0.9)));
            }

            if (colors.Count == 0)
            {
                // Between two songs the cover is missing for a moment. Keep the old colors briefly
                // instead of flashing violet; fall back only if no colorful cover turns up.
                if (_glowPaletteFromAlbum && Media.Thumbnail == null && Media.HasSession && animate && _glowFade > 0)
                {
                    if (_glowDefaultDue == 0) _glowDefaultDue = Environment.TickCount64 + 1500;
                    UpdateGlow(animate);
                    return;
                }
                colors.Add(DefaultGlowColor);
                _glowPaletteFromAlbum = false;
            }
            else
            {
                _glowPaletteFromAlbum = true;
            }
            _glowDefaultDue = 0;

            bool changed = colors.Count != _glowPalette.Length;
            for (int i = 0; !changed && i < colors.Count; i++)
                changed = colors[i] != _glowPalette[i];
            if (changed)
            {
                _glowPalette = colors.ToArray();
                _glowColorPhase = 0;
            }

            if (!animate || _glowFade <= 0) SnapGlowColors();
            UpdateGlow(animate);
        }

        private bool GlowBlending => _settings.GlowBlendColors && _glowPalette.Length > 1;

        /// <summary>Target color of gradient stop <paramref name="stop"/> (0 = left, 1 = middle, 2 = right).</summary>
        private Color GlowTargetColor(int stop)
        {
            if (!GlowBlending) return _glowPalette[0];

            // Each stop is a little ahead of the previous one, so the colors flow sideways while
            // they slowly breathe from one to the next. Smoothstep lingers on every real color.
            int n = _glowPalette.Length;
            double x = _glowColorPhase + stop * 0.45;
            double floor = Math.Floor(x);
            int a = ((int)floor % n + n) % n;
            double t = x - floor;
            t = t * t * (3 - 2 * t);
            return ColorHelper.Lerp(_glowPalette[a], _glowPalette[(a + 1) % n], t);
        }

        private void SnapGlowColors()
        {
            for (int i = 0; i < 3; i++)
            {
                var c = GlowTargetColor(i);
                _glowRgb[i, 0] = c.R;
                _glowRgb[i, 1] = c.G;
                _glowRgb[i, 2] = c.B;
            }
            PushGlowColors();
        }

        /// <summary>Eases the shown colors toward their targets. Returns true once they have arrived.</summary>
        private bool StepGlowColors(double dt)
        {
            double k = 1 - Math.Pow(0.86, dt / 0.033);   // ~0.6 s to change color
            bool settled = true;
            for (int i = 0; i < 3; i++)
            {
                var c = GlowTargetColor(i);
                double[] to = { c.R, c.G, c.B };
                for (int ch = 0; ch < 3; ch++)
                {
                    double d = to[ch] - _glowRgb[i, ch];
                    if (Math.Abs(d) < 0.5) { _glowRgb[i, ch] = to[ch]; continue; }
                    _glowRgb[i, ch] += d * k;
                    settled = false;
                }
            }
            PushGlowColors();
            return settled;
        }

        private void PushGlowColors()
        {
            for (int i = 0; i < 3; i++)
            {
                var c = Color.FromRgb(
                    (byte)Math.Round(Math.Clamp(_glowRgb[i, 0], 0, 255)),
                    (byte)Math.Round(Math.Clamp(_glowRgb[i, 1], 0, 255)),
                    (byte)Math.Round(Math.Clamp(_glowRgb[i, 2], 0, 255)));
                var stop = _glowGradient.GradientStops[i];
                if (stop.Color != c) stop.Color = c;
            }
        }

        // =====================================================================
        //  On / off, music
        // =====================================================================

        private void UpdateGlow(bool animate)
        {
            if (GlowLayer == null || _glowTimer == null) return;

            _glowOn = _settings.AlbumGlow && _settings.GlowIntensity > 0 && _showPlaying && IsEffectivelyVisible;

            double blur = 16 + 34 * (_settings.GlowIntensity / 100.0);
            if (Math.Abs(GlowBlur.Radius - blur) > 0.1) GlowBlur.Radius = blur;

            if (!animate)
            {
                _glowFade = _glowOn ? 1 : 0;
                SnapGlowColors();
            }

            UpdateGlowListening();

            if (_glowOn || _glowFade > 0)
            {
                if (!_glowTimer.IsEnabled)
                {
                    _glowLastTick = 0;
                    _glowTimer.Interval = GlowFastInterval;
                    _glowTimer.Start();
                }
                else if (_glowTimer.Interval != GlowFastInterval)
                {
                    _glowTimer.Interval = GlowFastInterval;   // something changed: animate it smoothly
                }
            }
            else
            {
                _glowTimer.Stop();
                SetClickThrough(false);
            }
            RenderGlow();
        }

        private void UpdateGlowListening()
        {
            bool want = _glowOn && _settings.GlowReactsToMusic && Media.IsPlaying;
            if (want == _glowListening) return;
            _glowListening = want;
            if (want)
            {
                _svc.Analyzer.Start();     // reference-counted, shared with the real spectrum
                _glowLastSignal = Environment.TickCount64;
            }
            else
            {
                _svc.Analyzer.Stop();
            }
        }

        private void OnGlowTick()
        {
            long now = Environment.TickCount64;
            double dt = _glowLastTick == 0 ? 0.033 : Math.Clamp((now - _glowLastTick) / 1000.0, 0.001, 0.25);
            _glowLastTick = now;

            if (_glowDefaultDue != 0 && now >= _glowDefaultDue)
            {
                _glowDefaultDue = 0;
                _glowPaletteFromAlbum = false;
                _glowPalette = new[] { DefaultGlowColor };
                _glowColorPhase = 0;
            }

            // Fade in / out (~0.5 s).
            double fadeTarget = _glowOn ? 1 : 0;
            double fadeStep = dt / 0.5;
            _glowFade = _glowFade < fadeTarget
                ? Math.Min(fadeTarget, _glowFade + fadeStep)
                : Math.Max(fadeTarget, _glowFade - fadeStep);

            // Music level.
            double target = 0;
            if (_glowListening)
            {
                _svc.Analyzer.ComputeBands(_glowBands);
                double bass = (_glowBands[0] + _glowBands[1] + _glowBands[2]) / 3;
                double rest = 0;
                for (int i = 3; i < _glowBands.Length; i++) rest += _glowBands[i];
                rest /= _glowBands.Length - 3;
                double energy = 0.65 * bass + 0.35 * rest;

                if (energy > 0.02) _glowLastSignal = now;
                if (now - _glowLastSignal > 1500)
                {
                    // Nothing from the loopback (exclusive-mode app, unusual device…): a calm fake beat.
                    double beat = (now % 508) / 508.0;    // ~118 BPM
                    target = 0.3 + 0.7 * Math.Exp(-5 * beat);
                }
                else
                {
                    _glowAverage += (energy - _glowAverage) * Math.Min(1, dt * 1.5);
                    double punch = Math.Clamp((energy - _glowAverage) * 3.5 + 0.45, 0, 1);
                    target = Math.Clamp(0.45 * energy + 0.55 * punch, 0, 1);
                }
            }
            double k = target > _glowLevel ? 0.55 : 0.12;   // quick to flare, slow to settle
            _glowLevel += (target - _glowLevel) * (1 - Math.Pow(1 - k, dt / 0.033));
            if (_glowLevel < 0.001) _glowLevel = 0;

            // Colors.
            bool blending = GlowBlending;
            if (blending)
                _glowColorPhase = (_glowColorPhase + dt / GlowColorSeconds * (1 + 0.8 * _glowLevel)) % _glowPalette.Length;
            bool settled = StepGlowColors(dt);

            RenderGlow();
            UpdateClickThrough();

            if (!_glowOn && _glowFade <= 0)
            {
                _glowTimer!.Stop();
                SetClickThrough(false);
                return;
            }

            // Nothing moving: keep a slow tick for the click-through check only.
            bool busy = _glowListening || blending || !settled || _glowFade != fadeTarget || _glowLevel > 0 || _glowDefaultDue != 0;
            var interval = busy ? GlowFastInterval : GlowIdleInterval;
            if (_glowTimer!.Interval != interval) _glowTimer.Interval = interval;
        }

        /// <summary>Applies size, offset and brightness from the intensity setting and the music level.</summary>
        private void RenderGlow()
        {
            double i = _settings.GlowIntensity / 100.0;
            bool react = _settings.GlowReactsToMusic;
            double pulse = react ? _glowLevel : 0;

            double baseOpacity = 0.35 + 0.65 * i;
            double opacity = Math.Clamp(baseOpacity * (react ? 0.6 + 0.55 * pulse : 1), 0, 1) * _glowFade;
            if (Math.Abs(GlowLayer.Opacity - opacity) > 0.002) GlowLayer.Opacity = opacity;
            if (opacity <= 0) return;

            double w = Math.Max(1, Notch.ActualWidth);
            double h = Math.Max(1, Notch.ActualHeight);
            double extraX = 4 + 26 * i + 18 * i * pulse;   // DIP on each side
            double extraY = 6 + 18 * i + 16 * i * pulse;   // DIP below
            GlowScale.ScaleX = 1 + 2 * extraX / w;
            GlowScale.ScaleY = 1 + extraY / h;
            GlowShift.Y = 2 + 6 * i + 4 * i * pulse;
        }

        // =====================================================================
        //  Click-through outside the notch
        // =====================================================================

        /// <summary>
        /// A layered window catches clicks on every pixel that isn't fully transparent, which would
        /// include the whole soft glow. So while the cursor is away from the notch shape the window
        /// becomes click-through; it turns solid again as soon as the cursor comes back.
        /// </summary>
        private void UpdateClickThrough()
        {
            bool want = false;
            if (_glowFade > 0.01 && _hwnd != IntPtr.Zero && ShapeRow.IsVisible && ShapeRow.ActualWidth > 0 &&
                NativeMethods.GetCursorPos(out var p))
            {
                try
                {
                    var topLeft = ShapeRow.PointToScreen(new Point(0, 0));
                    var bottomRight = ShapeRow.PointToScreen(new Point(ShapeRow.ActualWidth, ShapeRow.ActualHeight));
                    const double pad = 6;
                    // No top limit: the strip above the notch (top margin) still counts as "on it".
                    bool inside = p.X >= topLeft.X - pad && p.X <= bottomRight.X + pad && p.Y <= bottomRight.Y + pad;
                    want = !inside;
                }
                catch (InvalidOperationException)
                {
                    // Not connected to a presentation source right now.
                }
            }
            SetClickThrough(want);
        }

        private void SetClickThrough(bool on)
        {
            if (on == _clickThrough || _hwnd == IntPtr.Zero) return;
            _clickThrough = on;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex = on ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }
    }
}
