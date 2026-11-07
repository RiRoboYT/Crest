using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Crest.Helpers;
using Crest.Models;
using Crest.Services;
using Microsoft.Win32;

namespace Crest
{
    /// <summary>
    /// The notch. A fixed-size transparent, topmost, non-activating tool window that hosts
    /// a black shape which morphs between a collapsed (time + spectrum) and an expanded
    /// (full media player) state.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly MediaSessionService _media;
        private readonly SpectrumAnimator _spectrum;

        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _topmostTimer;
        private readonly DispatcherTimer _collapseTimer;
        private readonly DispatcherTimer _settingsApplyTimer;

        private IntPtr _hwnd;
        private bool _isExpanded;
        private bool _previewPinned;
        private bool _notchVisible = true;
        private string _spectrumColorKey = "";
        private Brush _spectrumBrush = Brushes.White;

        /// <summary>Raised when the user right-clicks the notch or presses the gear button.</summary>
        public event EventHandler? SettingsRequested;

        // Animatable corner radius (CornerRadius itself isn't a double, so animate this instead).
        public static readonly DependencyProperty ShapeRadiusProperty = DependencyProperty.Register(
            nameof(ShapeRadius), typeof(double), typeof(MainWindow),
            new PropertyMetadata(14.0, (d, _) => ((MainWindow)d).ApplyCornerRadius()));

        public double ShapeRadius
        {
            get => (double)GetValue(ShapeRadiusProperty);
            set => SetValue(ShapeRadiusProperty, value);
        }

        public MainWindow(AppSettings settings, MediaSessionService media)
        {
            _settings = settings;
            _media = media;

            InitializeComponent();

            _spectrum = new SpectrumAnimator(CollapsedSpectrum, ExpandedSpectrum);

            _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
            _clockTimer.Tick += (_, _) => OnClockTick();

            // Re-asserts HWND_TOPMOST so the notch stays above borderless-fullscreen games/videos
            // and above other topmost windows that grab the top of the z-order.
            _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _topmostTimer.Tick += (_, _) => EnsureTopmost();

            _collapseTimer = new DispatcherTimer(DispatcherPriority.Input);
            _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!Notch.IsMouseOver) Collapse(); };

            // Coalesce bursts of setting changes (e.g. slider drags) into one apply per frame-ish.
            _settingsApplyTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _settingsApplyTimer.Tick += (_, _) => { _settingsApplyTimer.Stop(); ApplySettings(); };

            _settings.PropertyChanged += OnSettingsChanged;
            _media.Updated += (_, _) => OnMediaUpdated();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) =>
            {
                ApplySettings();
                OnMediaUpdated();
                OnClockTick();
                _topmostTimer.Start();
            };
            Closed += OnClosed;
        }

        // =====================================================================
        //  Win32 setup
        // =====================================================================

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // Tool window (no Alt+Tab entry) + never activate (doesn't steal focus from games).
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            UpdateWindowBounds();
            EnsureTopmost();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEACTIVATE:
                    handled = true;
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);

                case NativeMethods.WM_DISPLAYCHANGE:
                case NativeMethods.WM_DPICHANGED:
                    Dispatcher.InvokeAsync(UpdateWindowBounds, DispatcherPriority.Background);
                    break;
            }
            return IntPtr.Zero;
        }

        private void EnsureTopmost()
        {
            if (_hwnd == IntPtr.Zero || !_notchVisible || !_settings.AlwaysOnTop) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
            => Dispatcher.InvokeAsync(UpdateWindowBounds, DispatcherPriority.Background);

        /// <summary>Sizes the transparent host window and centers it on the primary screen.</summary>
        private void UpdateWindowBounds()
        {
            var s = _settings;
            double shoulder = ShoulderSize();
            double topMargin = s.Shape == NotchShape.Pill ? s.TopMargin : 0;

            // Extra room so the slight overshoot of the expand animation is never clipped.
            double width = Math.Max(s.CollapsedWidth, s.ExpandedWidth) + shoulder * 2 + 48;
            double height = Math.Max(s.CollapsedHeight, s.ExpandedHeight) + topMargin + 24;

            double screenWidth = SystemParameters.PrimaryScreenWidth;   // DIPs → DPI scaling handled by WPF
            double left = (screenWidth - width) / 2 + s.HorizontalOffset;
            left = Math.Max(-width / 2, Math.Min(screenWidth - width / 2, left));

            Width = width;
            Height = height;
            Left = left;
            Top = 0;
        }

        // =====================================================================
        //  Settings
        // =====================================================================

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_settingsApplyTimer.IsEnabled) _settingsApplyTimer.Start();
        }

        private double ShoulderSize()
            => _settings.Shape == NotchShape.Notch
                ? Math.Min(_settings.ShoulderSize, _settings.CollapsedHeight / 2)
                : 0;

        private void ApplySettings()
        {
            var s = _settings;

            // ---- colors ----
            Color text = ColorHelper.Parse(s.TextColor, Colors.White);
            Resources["NotchBackground"] = ColorHelper.Brush(s.BackgroundColor, Colors.Black, s.BackgroundOpacity);
            Resources["TextBrush"] = Frozen(text, 1.0);
            Resources["SubtleTextBrush"] = Frozen(text, 0.6);
            Resources["TrackBrush"] = Frozen(text, 0.2);
            Resources["HoverBrush"] = Frozen(text, 0.14);
            Resources["AccentBrush"] = ColorHelper.Brush(s.AccentColor, Colors.White);

            // ---- fonts ----
            try { Resources["TextFont"] = new FontFamily(s.FontFamily); }
            catch { Resources["TextFont"] = new FontFamily("Segoe UI"); }
            Resources["FontBase"] = s.FontSize;
            Resources["FontLarge"] = s.FontSize + 2;
            Resources["FontSmall"] = Math.Max(8, s.FontSize - 2);

            // ---- shape ----
            double shoulder = ShoulderSize();
            bool notchShape = s.Shape == NotchShape.Notch;
            LeftShoulder.Visibility = RightShoulder.Visibility = notchShape && shoulder > 0 ? Visibility.Visible : Visibility.Collapsed;
            LeftShoulder.Width = LeftShoulder.Height = RightShoulder.Width = RightShoulder.Height = Math.Max(shoulder, 0.1);
            ShapeRow.Margin = new Thickness(0, notchShape ? 0 : s.TopMargin, 0, 0);

            CollapsedHost.Width = s.CollapsedWidth;
            CollapsedHost.Height = s.CollapsedHeight;
            ExpandedHost.Width = s.ExpandedWidth;
            ExpandedHost.Height = s.ExpandedHeight;

            // ---- display toggles ----
            DateText.Visibility = s.ShowDate ? Visibility.Visible : Visibility.Collapsed;
            ArtContainer.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            ProgressRow.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            VolumeButton.Visibility = s.ShowVolumeIcon ? Visibility.Visible : Visibility.Collapsed;
            ExpandedSpectrum.Visibility = s.ShowSpectrumWhenExpanded ? Visibility.Visible : Visibility.Collapsed;

            // ---- spectrum (only rebuild the brush when the color actually changed) ----
            string colorKey = s.SpectrumColor ?? "";
            if (colorKey != _spectrumColorKey)
            {
                _spectrumColorKey = colorKey;
                _spectrumBrush = ColorHelper.Brush(colorKey, Colors.White);
            }
            _spectrum.Configure(s.SpectrumBarCount, s.SpectrumBarWidth, s.SpectrumBarSpacing,
                                s.SpectrumMaxHeight, _spectrumBrush, s.SpectrumSpeedMs);

            // ---- window ----
            Topmost = s.AlwaysOnTop;
            if (!s.AlwaysOnTop && _hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            UpdateWindowBounds();
            SetShape(_isExpanded, animate: false);
            ApplyCornerRadius();   // shape style may have changed even if the radius didn't
            OnClockTick();
            UpdateProgress();
        }

        private static SolidColorBrush Frozen(Color c, double opacity)
        {
            c.A = (byte)Math.Round(c.A * opacity);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // =====================================================================
        //  Expand / collapse
        // =====================================================================

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapseTimer.Stop();
            if (_settings.ExpandOnHover) Expand();
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isExpanded || _previewPinned) return;

            if (_settings.CollapseDelayMs <= 0)
            {
                Collapse();
            }
            else
            {
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);
                _collapseTimer.Start();
            }
        }

        private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // With hover-expansion disabled, a click toggles the player instead.
            if (_settings.ExpandOnHover || ProgressTrack.IsMouseOver) return;
            if (_isExpanded) Collapse(); else Expand();
        }

        private void Notch_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Notch_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_settings.ScrollChangesVolume) return;
            e.Handled = true;
            NativeMethods.PressKey(e.Delta > 0 ? NativeMethods.VK_VOLUME_UP : NativeMethods.VK_VOLUME_DOWN);
        }

        /// <summary>Used by the settings window so you can see expanded-size changes live.</summary>
        public void SetPreviewExpanded(bool pinned)
        {
            _previewPinned = pinned;
            if (pinned) Expand();
            else if (!Notch.IsMouseOver) Collapse();
        }

        public void SetNotchVisible(bool visible)
        {
            _notchVisible = visible;
            _spectrum.SetSuspended(!visible);
            if (visible)
            {
                Show();
                _clockTimer.Start();
                _topmostTimer.Start();
                OnClockTick();
                EnsureTopmost();
            }
            else
            {
                _clockTimer.Stop();
                _topmostTimer.Stop();
                Hide();
            }
        }

        private void Expand()
        {
            if (_isExpanded) return;
            _isExpanded = true;

            UpdateProgress();
            SetShape(true, animate: true);

            int ms = _settings.AnimationDurationMs;
            ExpandedHost.Visibility = Visibility.Visible;
            Fade(CollapsedHost, 0, ms * 0.4, 0);
            Fade(ExpandedHost, 1, ms * 0.8, ms * 0.25);
        }

        private void Collapse()
        {
            if (!_isExpanded) return;
            _isExpanded = false;

            SetShape(false, animate: true);

            int ms = _settings.AnimationDurationMs;
            Fade(ExpandedHost, 0, ms * 0.4, 0, () =>
            {
                // Fully collapsed: drop the expanded tree from rendering/hit-testing.
                if (!_isExpanded) ExpandedHost.Visibility = Visibility.Collapsed;
            });
            Fade(CollapsedHost, 1, ms * 0.6, ms * 0.4);
        }

        private void SetShape(bool expanded, bool animate)
        {
            var s = _settings;
            double w = expanded ? s.ExpandedWidth : s.CollapsedWidth;
            double h = expanded ? s.ExpandedHeight : s.CollapsedHeight;
            double r = expanded ? s.ExpandedCornerRadius : s.CornerRadius;
            int ms = animate ? s.AnimationDurationMs : 0;

            // Gentle overshoot when opening (feels "alive"), smooth ease when closing.
            IEasingFunction ease = expanded
                ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseInOut };

            Animate(Notch, WidthProperty, w, ms, ease);
            Animate(Notch, HeightProperty, h, ms, ease);
            Animate(this, ShapeRadiusProperty, r, ms, new CubicEase { EasingMode = EasingMode.EaseOut });

            if (!animate)
            {
                CollapsedHost.BeginAnimation(OpacityProperty, null);
                ExpandedHost.BeginAnimation(OpacityProperty, null);
                CollapsedHost.Opacity = expanded ? 0 : 1;
                ExpandedHost.Opacity = expanded ? 1 : 0;
                ExpandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static void Animate(DependencyObject target, DependencyProperty property, double to, int ms, IEasingFunction ease)
        {
            if (target is not IAnimatable animatable) return;

            if (ms <= 0)
            {
                animatable.BeginAnimation(property, null);
                target.SetValue(property, to);
                return;
            }

            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
            anim.Freeze();
            animatable.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private static void Fade(UIElement element, double to, double ms, double delayMs, Action? completed = null)
        {
            if (ms <= 0)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
                completed?.Invoke();
                return;
            }

            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            if (completed != null) anim.Completed += (_, _) => completed();
            element.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private void ApplyCornerRadius()
        {
            if (Notch == null) return;
            double r = Math.Max(0, ShapeRadius);
            Notch.CornerRadius = _settings?.Shape == NotchShape.Pill
                ? new CornerRadius(r)
                : new CornerRadius(0, 0, r, r);
        }

        // =====================================================================
        //  Clock
        // =====================================================================

        private void OnClockTick()
        {
            DateTime now = DateTime.Now;

            string format = _settings.Use24HourClock
                ? (_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
                : (_settings.ShowSeconds ? "hh:mm:ss tt" : "hh:mm tt");
            string time = now.ToString(format, CultureInfo.InvariantCulture);

            if (CollapsedTime.Text != time) CollapsedTime.Text = time;
            if (ExpandedTime.Text != time) ExpandedTime.Text = time;

            string date = now.ToString("dddd, MMM d", CultureInfo.CurrentCulture);
            if (DateText.Text != date) DateText.Text = date;

            UpdateProgress();

            // Re-align to the next wall-clock second so the display never lags by up to 1 s.
            _clockTimer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 5);
            if (_notchVisible && !_clockTimer.IsEnabled) _clockTimer.Start();
        }

        // =====================================================================
        //  Media
        // =====================================================================

        private void OnMediaUpdated()
        {
            bool hasMedia = _media.HasSession && !string.IsNullOrWhiteSpace(_media.Title);

            TitleText.Text = hasMedia ? _media.Title : "Not Playing";
            ArtistText.Text = hasMedia
                ? (string.IsNullOrWhiteSpace(_media.Artist) ? FriendlyAppName(_media.SourceApp) : _media.Artist)
                : "Play something to see it here";

            PlayPauseButton.Content = _media.IsPlaying ? "\uE769" : "\uE768";   // Pause : Play
            PlayPauseButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = _media.HasSession;

            if (_media.Thumbnail != null)
            {
                ArtBrush.ImageSource = _media.Thumbnail;
                ArtImage.Visibility = Visibility.Visible;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                ArtBrush.ImageSource = null;
                ArtImage.Visibility = Visibility.Collapsed;
                ArtPlaceholder.Visibility = Visibility.Visible;
            }

            ProgressTrack.Cursor = _media.CanSeek ? Cursors.Hand : Cursors.Arrow;

            _spectrum.SetPlaying(_media.IsPlaying);
            UpdateProgress();
        }

        private void UpdateProgress()
        {
            // Only visible when expanded → skip the work otherwise.
            if (!_isExpanded || !_settings.ShowProgress) return;

            TimeSpan duration = _media.Duration;
            TimeSpan position = _media.Position;

            if (!_media.HasSession || duration <= TimeSpan.Zero)
            {
                ProgressScale.ScaleX = 0;
                PositionText.Text = "0:00";
                DurationText.Text = "--:--";
                return;
            }

            ProgressScale.ScaleX = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
            PositionText.Text = FormatTime(position);
            DurationText.Text = FormatTime(duration);
        }

        private static string FormatTime(TimeSpan t)
            => t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";

        /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify", "chrome.exe" → "chrome".</summary>
        private static string FriendlyAppName(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return "";
            string name = aumid;
            int bang = name.LastIndexOf('!');
            if (bang >= 0 && bang < name.Length - 1) name = name[(bang + 1)..];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
            if (name.Equals("chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
            if (name.Equals("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
            return name;
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
        private async void NextButton_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
        private async void PreviousButton_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
            => NativeMethods.PressKey(NativeMethods.VK_VOLUME_MUTE);

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
            => SettingsRequested?.Invoke(this, EventArgs.Empty);

        private async void ProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_media.CanSeek || _media.Duration <= TimeSpan.Zero || ProgressTrack.ActualWidth <= 0) return;

            double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
            var target = TimeSpan.FromSeconds(_media.Duration.TotalSeconds * ratio);
            ProgressScale.ScaleX = ratio;
            await _media.SeekAsync(target);
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================

        private void OnClosed(object? sender, EventArgs e)
        {
            _clockTimer.Stop();
            _topmostTimer.Stop();
            _collapseTimer.Stop();
            _settingsApplyTimer.Stop();
            _spectrum.SetSuspended(true);
            _settings.PropertyChanged -= OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;   // static event: must unsubscribe
        }
    }
}
