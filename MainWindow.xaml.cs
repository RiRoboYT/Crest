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
    /// <summary>The three looks of the notch.</summary>
    public enum NotchState
    {
        Collapsed,
        Peek,
        Expanded
    }

    /// <summary>
    /// The notch. A fixed-size transparent, topmost, non-activating tool window that hosts a shape
    /// which morphs between Collapsed (time + spectrum), Peek (song changed) and Expanded (player).
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int PeekHeightDefault = 58;
        private static readonly TimeSpan StartupPeekGrace = TimeSpan.FromSeconds(4);

        private readonly AppSettings _settings;
        private readonly MediaSessionService _media;
        private readonly VolumeService _volume;
        private readonly AudioSpectrumService _analyzer;
        private readonly SpectrumAnimator _spectrum;

        // Owned (non-frozen) brushes so album colors can fade smoothly.
        private readonly SolidColorBrush _spectrumBrush = new(Colors.White);
        private readonly SolidColorBrush _accentBrush = new(Colors.White);
        private Color? _spectrumTarget;
        private Color? _accentTarget;

        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _topmostBackupTimer;
        private readonly DispatcherTimer _topmostRetryTimer;
        private readonly DispatcherTimer _hoverTimer;
        private readonly DispatcherTimer _collapseTimer;
        private readonly DispatcherTimer _peekTimer;
        private readonly DispatcherTimer _settingsApplyTimer;

        private IntPtr _hwnd;
        private IntPtr _winEventHook;
        private NativeMethods.WinEventDelegate? _winEventProc;   // keep alive while hooked

        private NotchState _state = NotchState.Collapsed;
        private bool _previewPinned;
        private bool _notchVisible = true;
        private bool _updatingVolumeUi;
        private readonly DateTime _startedAt = DateTime.UtcNow;

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

        public NotchState State => _state;

        public MainWindow(AppSettings settings, MediaSessionService media, VolumeService volume, AudioSpectrumService analyzer)
        {
            _settings = settings;
            _media = media;
            _volume = volume;
            _analyzer = analyzer;

            InitializeComponent();

            Resources["AccentBrush"] = _accentBrush;
            _spectrum = new SpectrumAnimator(_spectrumBrush, CollapsedSpectrum, PeekSpectrum, ExpandedSpectrum);

            _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
            _clockTimer.Tick += (_, _) => OnClockTick();

            // Primary "stay on top" signal is the foreground-change hook; these are safety nets.
            _topmostBackupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _topmostBackupTimer.Tick += (_, _) => EnsureTopmost();
            _topmostRetryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
            _topmostRetryTimer.Tick += (_, _) => { _topmostRetryTimer.Stop(); EnsureTopmost(); };

            _hoverTimer = new DispatcherTimer(DispatcherPriority.Input);
            _hoverTimer.Tick += (_, _) =>
            {
                _hoverTimer.Stop();
                if (Notch.IsMouseOver && _settings.ExpandOnHover) SetState(NotchState.Expanded, animate: true);
            };

            _collapseTimer = new DispatcherTimer(DispatcherPriority.Input);
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (!Notch.IsMouseOver && !_previewPinned) SetState(NotchState.Collapsed, animate: true);
            };

            _peekTimer = new DispatcherTimer(DispatcherPriority.Background);
            _peekTimer.Tick += (_, _) =>
            {
                _peekTimer.Stop();
                if (_state == NotchState.Peek && !Notch.IsMouseOver) SetState(NotchState.Collapsed, animate: true);
            };

            // Coalesce bursts of setting changes (e.g. slider drags) into one apply per frame-ish.
            _settingsApplyTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _settingsApplyTimer.Tick += (_, _) => { _settingsApplyTimer.Stop(); ApplySettings(); };

            _settings.PropertyChanged += OnSettingsChanged;
            _media.Updated += OnMediaUpdated;
            _media.TrackChanged += OnTrackChanged;
            _volume.Changed += OnVolumeChanged;
            _volume.DefaultDeviceChanged += OnDefaultAudioDeviceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) =>
            {
                ApplySettings();
                OnMediaUpdated(this, EventArgs.Empty);
                UpdateVolumeUi();
                OnClockTick();
                _topmostBackupTimer.Start();
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

            // React instantly whenever another window comes to the front (games, fullscreen video…).
            _winEventProc = OnForegroundWindowChanged;
            _winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

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
                    // Let WPF rescale first, then re-center on the chosen monitor.
                    Dispatcher.InvokeAsync(UpdateWindowBounds, DispatcherPriority.Background);
                    break;
            }
            return IntPtr.Zero;
        }

        private void OnForegroundWindowChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            EnsureTopmost();
            // Fullscreen apps sometimes raise themselves a moment after activation → check once more.
            _topmostRetryTimer.Stop();
            _topmostRetryTimer.Start();
        }

        private void EnsureTopmost()
        {
            if (_hwnd == IntPtr.Zero || !_notchVisible || !_settings.AlwaysOnTop) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
            => Dispatcher.InvokeAsync(UpdateWindowBounds, DispatcherPriority.Background);

        /// <summary>Sizes the transparent host window and centers it at the top of the chosen monitor.</summary>
        private void UpdateWindowBounds()
        {
            var s = _settings;
            double shoulder = ShoulderSize();
            double topMargin = s.Shape == NotchShape.Pill ? s.TopMargin : 0;

            // Extra room so the slight overshoot of the expand animation is never clipped.
            double widthDip = Math.Max(s.CollapsedWidth, s.ExpandedWidth) + shoulder * 2 + 48;
            double heightDip = Math.Max(s.CollapsedHeight, s.ExpandedHeight) + topMargin + 24;
            Width = widthDip;
            Height = heightDip;

            if (_hwnd == IntPtr.Zero) return;

            var monitor = MonitorHelper.Find(s.MonitorDevice);
            if (monitor == null) return;

            // Monitor bounds are physical pixels; convert our DIP size with that monitor's scale.
            double scale = monitor.Scale;
            int widthPx = (int)Math.Round(widthDip * scale);
            var b = monitor.Bounds;

            int x = b.Left + (b.Width - widthPx) / 2 + (int)Math.Round(s.HorizontalOffset * scale);
            x = Math.Clamp(x, b.Left - widthPx / 2, b.Right - widthPx / 2);
            int y = b.Top;

            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
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
            Resources["TextBrush"] = ColorHelper.Frozen(text, 1.0);
            Resources["SubtleTextBrush"] = ColorHelper.Frozen(text, 0.6);
            Resources["TrackBrush"] = ColorHelper.Frozen(text, 0.2);
            Resources["HoverBrush"] = ColorHelper.Frozen(text, 0.14);
            ApplyDynamicColors(animate: false);

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
            PeekHost.Width = PeekWidth;
            PeekHost.Height = PeekHeight;
            ExpandedHost.Width = s.ExpandedWidth;
            ExpandedHost.Height = s.ExpandedHeight;

            // ---- display toggles ----
            DateText.Visibility = s.ShowDate ? Visibility.Visible : Visibility.Collapsed;
            ArtContainer.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            PeekArtContainer.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            ProgressRow.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            ExpandedSpectrum.Visibility = s.ShowSpectrumWhenExpanded ? Visibility.Visible : Visibility.Collapsed;
            TitleText.IsScrollingEnabled = s.ScrollLongTitles;
            PeekTitle.IsScrollingEnabled = s.ScrollLongTitles;
            UpdateVolumeUi();
            UpdateClickTargets();

            // ---- media / spectrum ----
            _media.PreferPlayingSession = s.PreferPlayingSession;
            _spectrum.Configure(s.SpectrumBarCount, s.SpectrumBarWidth, s.SpectrumBarSpacing, s.SpectrumMaxHeight, s.SpectrumSpeedMs);
            _spectrum.SetAnalyzer(s.RealSpectrum ? _analyzer : null);

            // ---- window ----
            Topmost = s.AlwaysOnTop;
            if (!s.AlwaysOnTop && _hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            UpdateWindowBounds();
            SetState(_state, animate: false);
            ApplyCornerRadius();   // shape style may have changed even if the radius didn't
            OnClockTick();
            UpdateProgress();
        }

        /// <summary>Spectrum + progress colors: settings colors, or the album color when enabled.</summary>
        private void ApplyDynamicColors(bool animate)
        {
            var s = _settings;
            Color background = ColorHelper.Parse(s.BackgroundColor, Colors.Black);
            Color spectrum = ColorHelper.Parse(s.SpectrumColor, Colors.White);
            Color accent = ColorHelper.Parse(s.AccentColor, Colors.White);

            if (s.UseAlbumColors && _media.AlbumColor is Color album)
            {
                Color adjusted = ColorHelper.AdjustForBackground(album, background);
                spectrum = adjusted;
                accent = adjusted;
            }

            FadeBrush(_spectrumBrush, spectrum, ref _spectrumTarget, animate);
            FadeBrush(_accentBrush, accent, ref _accentTarget, animate);
        }

        private static void FadeBrush(SolidColorBrush brush, Color to, ref Color? currentTarget, bool animate)
        {
            if (currentTarget == to) return;
            currentTarget = to;

            if (!animate)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = to;
                return;
            }

            var anim = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(500)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            anim.Freeze();
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private double PeekWidth => ComputePeekWidth(_settings);
        private double PeekHeight => ComputePeekHeight(_settings);

        /// <summary>Size of the "song changed" peek – shared with the settings preview.</summary>
        public static double ComputePeekWidth(AppSettings s)
        {
            double maxW = Math.Max(s.CollapsedWidth, s.ExpandedWidth);
            return Math.Clamp(Math.Max(s.CollapsedWidth + 110, 300), s.CollapsedWidth, maxW);
        }

        public static double ComputePeekHeight(AppSettings s)
            => Math.Max(s.CollapsedHeight, Math.Min(PeekHeightDefault, s.ExpandedHeight));

        public static double ComputePeekRadius(AppSettings s)
            => Math.Max(s.CornerRadius, Math.Min(s.ExpandedCornerRadius, ComputePeekHeight(s) / 2.4));

        // =====================================================================
        //  State: collapsed / peek / expanded
        // =====================================================================

        private void SetState(NotchState target, bool animate)
        {
            if (animate && target == _state) return;
            _state = target;

            var s = _settings;
            double w, h, r;
            switch (target)
            {
                case NotchState.Expanded:
                    w = s.ExpandedWidth; h = s.ExpandedHeight; r = s.ExpandedCornerRadius;
                    break;
                case NotchState.Peek:
                    w = PeekWidth; h = PeekHeight; r = ComputePeekRadius(s);
                    break;
                default:
                    w = s.CollapsedWidth; h = s.CollapsedHeight; r = s.CornerRadius;
                    break;
            }

            int ms = animate ? s.AnimationDurationMs : 0;
            bool growing = target != NotchState.Collapsed;

            // Gentle overshoot when opening (feels "alive"), smooth ease when closing.
            IEasingFunction ease = growing
                ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseInOut };

            Animate(Notch, WidthProperty, w, ms, ease);
            Animate(Notch, HeightProperty, h, ms, ease);
            Animate(this, ShapeRadiusProperty, r, ms, new CubicEase { EasingMode = EasingMode.EaseOut });

            ShowHost(CollapsedHost, target == NotchState.Collapsed, NotchState.Collapsed, ms);
            ShowHost(PeekHost, target == NotchState.Peek, NotchState.Peek, ms);
            ShowHost(ExpandedHost, target == NotchState.Expanded, NotchState.Expanded, ms);

            // Marquees only scroll while their view is visible.
            TitleText.IsActive = target == NotchState.Expanded;
            PeekTitle.IsActive = target == NotchState.Peek;

            if (target != NotchState.Peek) _peekTimer.Stop();
            if (target == NotchState.Expanded) UpdateProgress();
        }

        private void ShowHost(FrameworkElement host, bool show, NotchState owner, int ms)
        {
            if (show)
            {
                host.Visibility = Visibility.Visible;
                Fade(host, 1, ms * 0.75, ms * 0.25);
            }
            else if (host.Visibility == Visibility.Visible)
            {
                Fade(host, 0, ms * 0.35, 0, () =>
                {
                    // Fully hidden: drop it from rendering/hit-testing.
                    if (_state != owner) host.Visibility = Visibility.Collapsed;
                });
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

        /// <summary>Used by the settings window so you can see expanded-size changes live.</summary>
        public void SetPreviewExpanded(bool pinned)
        {
            _previewPinned = pinned;
            if (pinned) SetState(NotchState.Expanded, animate: true);
            else if (!Notch.IsMouseOver) SetState(NotchState.Collapsed, animate: true);
        }

        public void SetNotchVisible(bool visible)
        {
            _notchVisible = visible;
            _spectrum.SetSuspended(!visible);
            if (visible)
            {
                Show();
                _clockTimer.Start();
                _topmostBackupTimer.Start();
                OnClockTick();
                UpdateWindowBounds();
                EnsureTopmost();
            }
            else
            {
                _clockTimer.Stop();
                _topmostBackupTimer.Stop();
                _peekTimer.Stop();
                Hide();
            }
        }

        // =====================================================================
        //  Mouse
        // =====================================================================

        private void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapseTimer.Stop();
            _peekTimer.Stop();   // keep a peek open while you're reading it

            if (!_settings.ExpandOnHover || _state == NotchState.Expanded) return;

            // Hover intent: only open if the pointer actually rests on the notch.
            if (_settings.HoverDelayMs <= 0)
            {
                SetState(NotchState.Expanded, animate: true);
            }
            else
            {
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverDelayMs);
                _hoverTimer.Stop();
                _hoverTimer.Start();
            }
        }

        private void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
            if (_previewPinned) return;

            if (_state == NotchState.Peek)
            {
                _peekTimer.Interval = TimeSpan.FromMilliseconds(1200);
                _peekTimer.Start();
                return;
            }

            if (_state != NotchState.Expanded) return;

            if (_settings.CollapseDelayMs <= 0)
            {
                SetState(NotchState.Collapsed, animate: true);
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
            SetState(_state == NotchState.Expanded ? NotchState.Collapsed : NotchState.Expanded, animate: true);
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

            if (_state == NotchState.Expanded && _volume.IsAvailable)
            {
                // The slider is visible → change volume directly.
                _volume.Step(e.Delta > 0 ? 0.02f : -0.02f);
            }
            else
            {
                // Collapsed → use media keys so Windows shows its volume indicator.
                NativeMethods.PressKey(e.Delta > 0 ? NativeMethods.VK_VOLUME_UP : NativeMethods.VK_VOLUME_DOWN);
            }
        }

        private void TrackInfo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_settings.ClickToOpenApp || !_media.HasSession) return;
            e.Handled = true;
            AppActivator.Activate(_media.SourceApp);
        }

        private void UpdateClickTargets()
        {
            Cursor cursor = _settings.ClickToOpenApp && _media.HasSession ? Cursors.Hand : Cursors.Arrow;
            TrackInfo.Cursor = cursor;
            PeekContent.Cursor = cursor;
            string? tip = _settings.ClickToOpenApp && _media.HasSession ? "Open the app that's playing" : null;
            TrackInfo.ToolTip = tip;
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

        private void OnMediaUpdated(object? sender, EventArgs e)
        {
            bool hasMedia = _media.HasSession && !string.IsNullOrWhiteSpace(_media.Title);
            string title = hasMedia ? _media.Title : "Not Playing";
            string artist = hasMedia
                ? (string.IsNullOrWhiteSpace(_media.Artist) ? FriendlyAppName(_media.SourceApp) : _media.Artist)
                : "Play something to see it here";

            TitleText.Text = title;
            ArtistText.Text = artist;
            PeekTitle.Text = title;
            PeekArtist.Text = artist;

            PlayPauseButton.Content = _media.IsPlaying ? "" : "";   // Pause : Play
            PlayPauseButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = _media.HasSession;

            bool hasArt = _media.Thumbnail != null;
            ArtBrush.ImageSource = _media.Thumbnail;
            PeekArtBrush.ImageSource = _media.Thumbnail;
            ArtImage.Visibility = PeekArtImage.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
            ArtPlaceholder.Visibility = PeekArtPlaceholder.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;

            ProgressTrack.Cursor = _media.CanSeek ? Cursors.Hand : Cursors.Arrow;
            UpdateClickTargets();

            ApplyDynamicColors(animate: true);
            _spectrum.SetPlaying(_media.IsPlaying);
            UpdateProgress();
        }

        private void OnTrackChanged(object? sender, EventArgs e)
        {
            if (!_settings.PeekOnTrackChange || !_notchVisible) return;
            if (DateTime.UtcNow - _startedAt < StartupPeekGrace) return;   // not on app start
            if (_state == NotchState.Expanded || _previewPinned || Notch.IsMouseOver) return;

            SetState(NotchState.Peek, animate: true);
            _peekTimer.Stop();
            _peekTimer.Interval = TimeSpan.FromMilliseconds(_settings.PeekDurationMs);
            _peekTimer.Start();
        }

        private void UpdateProgress()
        {
            // Only visible when expanded → skip the work otherwise.
            if (_state != NotchState.Expanded || !_settings.ShowProgress) return;

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

        /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify", "chrome.exe" → "Google Chrome".</summary>
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
        //  Volume
        // =====================================================================

        private void OnVolumeChanged(object? sender, EventArgs e) => UpdateVolumeUi();

        private void OnDefaultAudioDeviceChanged(object? sender, EventArgs e) => _analyzer.Restart();

        private void UpdateVolumeUi()
        {
            VolumePanel.Visibility = _settings.ShowVolumeIcon ? Visibility.Visible : Visibility.Collapsed;

            bool available = _volume.IsAvailable;
            VolumeSlider.IsEnabled = available;
            VolumeSlider.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

            _updatingVolumeUi = true;
            try
            {
                if (available) VolumeSlider.Value = Math.Round(_volume.Volume * 100);
            }
            finally
            {
                _updatingVolumeUi = false;
            }

            VolumeButton.Content = VolumeGlyph(available ? _volume.Volume : 1f, available && _volume.IsMuted);
            VolumeButton.ToolTip = available
                ? (_volume.IsMuted ? "Unmute" : $"Volume {Math.Round(_volume.Volume * 100)}% – click to mute")
                : "Mute / unmute";
        }

        private static string VolumeGlyph(float volume, bool muted)
        {
            if (muted) return "";          // Mute
            if (volume <= 0.001f) return ""; // Volume0
            if (volume < 0.34f) return "";   // Volume1
            if (volume < 0.67f) return "";   // Volume2
            return "";                       // Volume3
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolumeUi || !IsLoaded || !_volume.IsAvailable) return;
            _volume.SetVolume((float)(e.NewValue / 100.0));
            VolumeButton.Content = VolumeGlyph(_volume.Volume, _volume.IsMuted && e.NewValue <= 0);
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_volume.IsAvailable) _volume.ToggleMute();
            else NativeMethods.PressKey(NativeMethods.VK_VOLUME_MUTE);
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================

        private void OnClosed(object? sender, EventArgs e)
        {
            _clockTimer.Stop();
            _topmostBackupTimer.Stop();
            _topmostRetryTimer.Stop();
            _hoverTimer.Stop();
            _collapseTimer.Stop();
            _peekTimer.Stop();
            _settingsApplyTimer.Stop();
            _spectrum.SetSuspended(true);

            if (_winEventHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }

            _settings.PropertyChanged -= OnSettingsChanged;
            _media.Updated -= OnMediaUpdated;
            _media.TrackChanged -= OnTrackChanged;
            _volume.Changed -= OnVolumeChanged;
            _volume.DefaultDeviceChanged -= OnDefaultAudioDeviceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;   // static event: must unsubscribe
        }
    }
}
