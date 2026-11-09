using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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
    /// <summary>The looks of the notch.</summary>
    public enum NotchState
    {
        Collapsed,
        Peek,
        Activity,
        Expanded
    }

    /// <summary>
    /// The notch. A fixed-size transparent, topmost, non-activating tool window that hosts a shape
    /// which morphs between Collapsed, Peek, Activity (pop-ups) and Expanded (tabbed pages).
    ///
    /// The class is split over several files:
    ///   MainWindow.xaml.cs        – window plumbing, settings, states, media, volume
    ///   MainWindow.Pages.cs       – expanded pages (lyrics, calendar, weather, system, timer, shelf, notes)
    ///   MainWindow.Activities.cs  – pop-ups, volume / brightness indicator, timer bubble, privacy dots, glow
    ///   MainWindow.Input.cs       – gestures, hotkey, drag &amp; drop, typing
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int PeekHeightDefault = 58;
        private const double BubbleMaxWidth = 100;
        private const double BubbleGap = 8;
        private static readonly TimeSpan StartupPeekGrace = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan PeekWaitForPlay = TimeSpan.FromSeconds(2);

        private readonly AppSettings _settings;
        private readonly CrestServices _svc;
        private readonly string? _fixedMonitor;
        private readonly SpectrumAnimator _spectrum;
        private readonly List<Action> _unsubscribe = new();

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
        private readonly DispatcherTimer _popupTimer;      // ends a peek or an activity
        private readonly DispatcherTimer _settingsApplyTimer;
        private readonly DispatcherTimer _idleTimer;

        private IntPtr _hwnd;
        private NotchState _state = NotchState.Collapsed;
        private bool _previewPinned;
        private bool _notchVisible = true;    // tray "Show notch"
        private bool _hiddenByRule;           // per-app rule "Hide notch"
        private bool _quiet;                  // per-app rule "No pop-ups" or fullscreen
        private bool _updatingVolumeUi;
        private readonly DateTime _startedAt = DateTime.UtcNow;

        // "Playing" as shown on screen. Lags the real state by a moment when music stops, so
        // skipping tracks (which briefly reports "paused") doesn't make the notch flicker.
        private bool _showPlaying;
        private bool _peekPending;
        private DateTime _peekRequestedAt;

        private MediaSessionService Media => _svc.Media;
        private VolumeService Volume => _svc.Volume;

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

        /// <param name="fixedMonitor">Device name of the monitor this notch lives on (one notch per monitor mode), or null to follow the setting.</param>
        public MainWindow(AppSettings settings, CrestServices services, string? fixedMonitor = null)
        {
            _settings = settings;
            _svc = services;
            _fixedMonitor = fixedMonitor;

            InitializeComponent();

            Resources["AccentBrush"] = _accentBrush;
            Resources["GlowBrush"] = _glowGradient;
            _spectrum = new SpectrumAnimator(_spectrumBrush, CollapsedSpectrum, PeekSpectrum, ExpandedSpectrum);

            _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
            _clockTimer.Tick += (_, _) => OnClockTick();

            // Primary "stay on top" signal is the shared foreground hook; these are safety nets.
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
                if (!Notch.IsMouseOver && !KeepOpen) SetState(NotchState.Collapsed, animate: true);
            };

            _popupTimer = new DispatcherTimer(DispatcherPriority.Background);
            _popupTimer.Tick += (_, _) =>
            {
                _popupTimer.Stop();
                if ((_state == NotchState.Peek || _state == NotchState.Activity) && !Notch.IsMouseOver)
                    SetState(NotchState.Collapsed, animate: true);
            };

            _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdleDelay };
            _idleTimer.Tick += (_, _) =>
            {
                _idleTimer.Stop();
                if (!Media.IsPlaying) SetShowPlaying(false, animate: true);
            };

            // Coalesce bursts of setting changes (e.g. slider drags) into one apply per frame-ish.
            _settingsApplyTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _settingsApplyTimer.Tick += (_, _) => { _settingsApplyTimer.Stop(); ApplySettings(); };

            Subscribe(() => _settings.PropertyChanged += OnSettingsChanged, () => _settings.PropertyChanged -= OnSettingsChanged);
            Subscribe(() => Media.Updated += OnMediaUpdated, () => Media.Updated -= OnMediaUpdated);
            Subscribe(() => Media.TrackChanged += OnTrackChanged, () => Media.TrackChanged -= OnTrackChanged);
            Subscribe(() => Volume.Changed += OnVolumeChanged, () => Volume.Changed -= OnVolumeChanged);
            Subscribe(() => Volume.DefaultDeviceChanged += OnDefaultAudioDeviceChanged, () => Volume.DefaultDeviceChanged -= OnDefaultAudioDeviceChanged);
            Subscribe(() => _svc.Foreground.Switched += OnForegroundSwitched, () => _svc.Foreground.Switched -= OnForegroundSwitched);
            Subscribe(() => _svc.Foreground.Changed += OnForegroundChanged, () => _svc.Foreground.Changed -= OnForegroundChanged);
            Subscribe(() => SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged, () => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged);

            InitPages();
            InitGlow();
            InitActivities();
            InitExtras();
            InitInput();

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) =>
            {
                _showPlaying = Media.IsPlaying;
                ApplySettings();
                OnMediaUpdated(this, EventArgs.Empty);
                UpdateVolumeUi();
                OnClockTick();
                ApplyRules();
                _topmostBackupTimer.Start();
            };
            Closed += OnClosed;
        }

        /// <summary>Subscribes now and remembers how to unsubscribe when the window closes.</summary>
        private void Subscribe(Action subscribe, Action unsubscribe)
        {
            subscribe();
            _unsubscribe.Add(unsubscribe);
        }

        /// <summary>Reasons the notch must stay open even when the mouse leaves.</summary>
        private bool KeepOpen => _previewPinned || _typing || _dragActive || _hotkeyPinned;

        // =====================================================================
        //  Win32 setup
        // =====================================================================

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // Tool window (no Alt+Tab entry) + never activate (doesn't steal focus from games).
            SetNoActivate(true);

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            UpdateWindowBounds();
            EnsureTopmost();
        }

        private void SetNoActivate(bool noActivate)
        {
            if (_hwnd == IntPtr.Zero) return;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW;
            ex = noActivate ? ex | NativeMethods.WS_EX_NOACTIVATE : ex & ~NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEACTIVATE:
                    if (_typing) break;   // the notes box needs real focus
                    handled = true;
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);

                case NativeMethods.WM_DISPLAYCHANGE:
                case NativeMethods.WM_DPICHANGED:
                    // Let WPF rescale first, then re-center on the chosen monitor.
                    Dispatcher.InvokeAsync(UpdateWindowBounds, DispatcherPriority.Background);
                    break;

                case NativeMethods.WM_MOUSEHWHEEL:
                    OnHorizontalWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private void OnForegroundSwitched(object? sender, EventArgs e)
        {
            EnsureTopmost();
            // Fullscreen apps sometimes raise themselves a moment after activation → check once more.
            _topmostRetryTimer.Stop();
            _topmostRetryTimer.Start();
        }

        private void OnForegroundChanged(object? sender, EventArgs e) => ApplyRules();

        private void EnsureTopmost()
        {
            if (_hwnd == IntPtr.Zero || !IsEffectivelyVisible || !_settings.AlwaysOnTop) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
            => Dispatcher.InvokeAsync(UpdateWindowBounds, DispatcherPriority.Background);

        private string MonitorDevice => _fixedMonitor ?? (_settings.MonitorDevice == "*" ? "" : _settings.MonitorDevice);

        /// <summary>Sizes the transparent host window and centers it at the top of its monitor.</summary>
        private void UpdateWindowBounds()
        {
            var s = _settings;
            double shoulder = ShoulderSize();
            double topMargin = s.Shape == NotchShape.Pill ? s.TopMargin : 0;

            // Room for the widest state, the timer bubble on both sides (spacer), the glow and the
            // slight overshoot of the expand animation.
            double widest = Math.Max(Math.Max(s.CollapsedWidth, s.ExpandedWidth), ComputePeekWidth(s));
            // Extra room around the shape for the album glow (it spreads up to ~110 px).
            double widthDip = widest + shoulder * 2 + 2 * (BubbleMaxWidth + BubbleGap) + 2 * GlowRoom;
            double heightDip = Math.Max(s.CollapsedHeight, s.ExpandedHeight) + topMargin + GlowRoom;
            Width = widthDip;
            Height = heightDip;

            if (_hwnd == IntPtr.Zero) return;

            var monitor = MonitorHelper.Find(MonitorDevice);
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
        //  Visibility: tray toggle + per-app rules + quiet mode
        // =====================================================================

        private bool IsEffectivelyVisible => _notchVisible && !_hiddenByRule;

        public void SetNotchVisible(bool visible)
        {
            _notchVisible = visible;
            UpdateWindowVisibility();
        }

        private void ApplyRules()
        {
            var fg = _svc.Foreground;
            var rule = _settings.AppRules.FirstOrDefault(r => r.Matches(fg.ProcessName));

            IntPtr myMonitor = _hwnd != IntPtr.Zero
                ? NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;
            bool fullscreenHere = fg.IsFullscreen && fg.Monitor == myMonitor;

            _quiet = rule?.Action == RuleAction.NoPopups || (_settings.QuietInFullscreen && fullscreenHere);

            bool hide = rule?.Action == RuleAction.HideNotch;
            if (hide != _hiddenByRule)
            {
                _hiddenByRule = hide;
                UpdateWindowVisibility();
            }

            if (_quiet && (_state == NotchState.Peek || _state == NotchState.Activity))
                SetState(NotchState.Collapsed, animate: true);
        }

        private void UpdateWindowVisibility()
        {
            bool visible = IsEffectivelyVisible;
            _spectrum.SetSuspended(!visible);
            UpdateNetUsage(resize: true);
            UpdateGlow(animate: visible);
            if (visible)
            {
                if (!IsVisible) Show();
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
                _popupTimer.Stop();
                if (IsVisible) Hide();
            }
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
            Bubble.Height = s.CollapsedHeight;
            double br = Math.Min(s.CornerRadius, s.CollapsedHeight / 2);
            Bubble.CornerRadius = notchShape ? new CornerRadius(0, 0, br, br) : new CornerRadius(s.CollapsedHeight / 2);

            CollapsedHost.Height = s.CollapsedHeight;
            PeekHost.Width = ActivityHost.Width = PeekWidth;
            PeekHost.Height = ActivityHost.Height = PeekHeight;
            ExpandedHost.Width = s.ExpandedWidth;
            ExpandedHost.Height = s.ExpandedHeight;

            // ---- display toggles ----
            DateText.Visibility = s.ShowDate && s.ExpandedWidth >= 380 ? Visibility.Visible : Visibility.Collapsed;
            ArtContainer.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            PeekArtContainer.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            ProgressRow.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            TitleText.IsScrollingEnabled = s.ScrollLongTitles;
            PeekTitle.IsScrollingEnabled = s.ScrollLongTitles;
            CollapsedLyric.IsScrollingEnabled = s.ScrollLongTitles;
            UpdateVolumeUi();
            UpdateClickTargets();

            // ---- media / spectrum ----
            Media.PreferPlayingSession = s.PreferPlayingSession;
            _spectrum.Configure(s.SpectrumBarCount, s.SpectrumBarWidth, s.SpectrumBarSpacing, s.SpectrumMaxHeight, s.SpectrumSpeedMs);
            _spectrum.SetAnalyzer(s.RealSpectrum ? _svc.Analyzer : null);

            // ---- window ----
            Topmost = s.AlwaysOnTop;
            if (!s.AlwaysOnTop && _hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            ApplyPagesSettings();
            ApplyActivitySettings();
            ApplyExtrasSettings();
            ApplyRules();

            UpdateWindowBounds();
            UpdatePlayingPresentation(animate: false);
            SetState(_state, animate: false);
            ApplyCornerRadius();   // shape style may have changed even if the radius didn't
            OnClockTick();
            UpdateProgress();
        }

        /// <summary>Spectrum, progress and glow colors: settings colors, or the album color when enabled.</summary>
        private void ApplyDynamicColors(bool animate)
        {
            var s = _settings;
            Color background = ColorHelper.Parse(s.BackgroundColor, Colors.Black);
            Color spectrum = ColorHelper.Parse(s.SpectrumColor, Colors.White);
            Color accent = ColorHelper.Parse(s.AccentColor, Colors.White);

            if (Media.AlbumColor is Color album)
            {
                // "A different cover color each song": pick one of the cover's colors per track.
                var palette = Media.AlbumPalette;
                if (s.AlbumColorShuffle && palette.Count > 1)
                {
                    int hash = 17;
                    foreach (char ch in Media.Title + "|" + Media.Artist) hash = unchecked(hash * 31 + ch);
                    album = palette[(hash & 0x7FFFFFFF) % palette.Count];
                }
                Color adjusted = ColorHelper.AdjustForBackground(album, background);
                if (s.UseAlbumColors)
                {
                    spectrum = adjusted;
                    accent = adjusted;
                }
            }

            FadeBrush(_spectrumBrush, spectrum, ref _spectrumTarget, animate);
            FadeBrush(_accentBrush, accent, ref _accentTarget, animate);
            SetGlowPalette(Media.AlbumPalette, animate);
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

        /// <summary>Size of the "song changed" peek / pop-ups – shared with the settings preview.</summary>
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
        //  Playing vs idle (Dynamic Island style)
        // =====================================================================

        /// <summary>Collapsed width right now: just the time when idle (if enabled), wider for lyrics / privacy dots.</summary>
        private double CurrentCollapsedWidth
        {
            get
            {
                var s = _settings;
                double width = s.ShrinkWhenIdle && !_showPlaying
                    ? ComputeIdleWidth(s, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                    : s.CollapsedWidth;
                if (ShowingCollapsedLyric) width = Math.Max(width, Math.Min(Math.Max(s.CollapsedWidth, 300), s.ExpandedWidth));
                if (PrivacyDots.Visibility == Visibility.Visible) width += PrivacyDotsWidth;
                width += ExtrasCollapsedWidth;
                return width;
            }
        }

        /// <summary>Width of the bars area in the collapsed notch (incl. its left gap).</summary>
        private double BarsWidth
        {
            get
            {
                var s = _settings;
                return 10 + s.SpectrumBarCount * s.SpectrumBarWidth + (s.SpectrumBarCount - 1) * s.SpectrumBarSpacing;
            }
        }

        public static string TimeFormat(AppSettings s)
            => s.Use24HourClock
                ? (s.ShowSeconds ? "HH:mm:ss" : "HH:mm")
                : (s.ShowSeconds ? "hh:mm:ss tt" : "hh:mm tt");

        /// <summary>Idle notch width: the widest possible time text plus padding (never wider than the normal width).</summary>
        public static double ComputeIdleWidth(AppSettings s, double pixelsPerDip)
        {
            string sample = new DateTime(2000, 1, 1, 12, 58, 58).ToString(TimeFormat(s), CultureInfo.InvariantCulture);

            FontFamily family;
            try { family = new FontFamily(s.FontFamily); }
            catch { family = new FontFamily("Segoe UI"); }

            var text = new FormattedText(sample, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                s.FontSize, Brushes.White, pixelsPerDip > 0 ? pixelsPerDip : 1.0);

            double width = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 40;
            double min = Math.Min(s.CollapsedWidth, s.CollapsedHeight * 2.2);
            return Math.Clamp(width, min, s.CollapsedWidth);
        }

        private void SetShowPlaying(bool playing, bool animate)
        {
            if (_showPlaying == playing) return;
            _showPlaying = playing;
            UpdatePlayingPresentation(animate);
            UpdateGlow(animate);
        }

        /// <summary>Shows / hides the bars everywhere and resizes the collapsed notch.</summary>
        private void UpdatePlayingPresentation(bool animate)
        {
            int ms = animate ? _settings.AnimationDurationMs : 0;
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            UpdateCollapsedContent();
            UpdateCollapsedArt(animate, resize: false);
            UpdateNetUsage(resize: false);

            // Collapsed: bars slide open / closed and fade.
            Animate(CollapsedBarsHost, WidthProperty, _showPlaying ? BarsWidth : 0, ms, ease);
            Fade(CollapsedBarsHost, _showPlaying ? 1 : 0, ms * (_showPlaying ? 0.8 : 0.5), _showPlaying ? ms * 0.2 : 0);

            ResizeCollapsed(animate);

            // Peek / expanded: bars only while playing.
            PeekSpectrum.Visibility = _showPlaying ? Visibility.Visible : Visibility.Collapsed;
            ExpandedSpectrum.Visibility = _showPlaying && _settings.ShowSpectrumWhenExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Re-sizes the collapsed notch (idle / playing / lyrics / privacy dots changed).</summary>
        private void ResizeCollapsed(bool animate)
        {
            int ms = animate ? _settings.AnimationDurationMs : 0;
            double collapsedWidth = CurrentCollapsedWidth;
            Animate(CollapsedHost, WidthProperty, collapsedWidth, ms, new CubicEase { EasingMode = EasingMode.EaseInOut });
            if (_state == NotchState.Collapsed)
                Animate(Notch, WidthProperty, collapsedWidth, ms, GrowEase(true));
        }

        // =====================================================================
        //  States
        // =====================================================================

        /// <summary>Opening ease: springy when "bouncy animations" is on.</summary>
        private IEasingFunction GrowEase(bool growing)
        {
            if (!growing) return new CubicEase { EasingMode = EasingMode.EaseInOut };
            return _settings.BouncyAnimations
                ? new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut }
                : new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        }

        private void SetState(NotchState target, bool animate)
        {
            if (animate && target == _state) return;
            if (target != NotchState.Collapsed) RestoreFromTyping(animate);
            var previous = _state;
            _state = target;

            var s = _settings;
            double w, h, r;
            switch (target)
            {
                case NotchState.Expanded:
                    w = s.ExpandedWidth; h = s.ExpandedHeight; r = s.ExpandedCornerRadius;
                    break;
                case NotchState.Peek:
                case NotchState.Activity:
                    w = PeekWidth; h = PeekHeight; r = ComputePeekRadius(s);
                    break;
                default:
                    w = CurrentCollapsedWidth; h = s.CollapsedHeight; r = s.CornerRadius;
                    break;
            }

            bool growing = target != NotchState.Collapsed;
            int ms = animate ? s.AnimationDurationMs : 0;
            int shapeMs = animate && growing && s.BouncyAnimations ? (int)(ms * 1.6) : ms;
            IEasingFunction ease = GrowEase(growing);

            Animate(Notch, WidthProperty, w, shapeMs, ease);
            Animate(Notch, HeightProperty, h, shapeMs, ease);
            Animate(this, ShapeRadiusProperty, r, ms, new CubicEase { EasingMode = EasingMode.EaseOut });

            ShowHost(CollapsedHost, target == NotchState.Collapsed, NotchState.Collapsed, ms);
            ShowHost(PeekHost, target == NotchState.Peek, NotchState.Peek, ms);
            ShowHost(ActivityHost, target == NotchState.Activity, NotchState.Activity, ms);
            ShowHost(ExpandedHost, target == NotchState.Expanded, NotchState.Expanded, ms);

            // Marquees only scroll while their view is visible.
            TitleText.IsActive = target == NotchState.Expanded;
            PeekTitle.IsActive = target == NotchState.Peek;
            CollapsedLyric.IsActive = target == NotchState.Collapsed;

            if (target != NotchState.Peek && target != NotchState.Activity) _popupTimer.Stop();
            if (target == NotchState.Expanded) UpdateProgress();

            if (previous == NotchState.Expanded && target != NotchState.Expanded) OnExpandedClosed();
            if (target == NotchState.Expanded && previous != NotchState.Expanded) OnExpandedOpened();

            UpdateBubble(animate);
            UpdateGlow(animate);
            UpdateSoundRefresh();
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
            else if (!Notch.IsMouseOver && !KeepOpen) SetState(NotchState.Collapsed, animate: true);
        }

        /// <summary>Shows a temporary state (peek / activity) for a while.</summary>
        private void ShowTemporarily(NotchState state, int milliseconds)
        {
            SetState(state, animate: true);
            _popupTimer.Stop();
            _popupTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(800, milliseconds));
            _popupTimer.Start();
        }

        /// <summary>True when a pop-up may interrupt right now.</summary>
        private bool CanPopUp
            => IsEffectivelyVisible && !_quiet && !PopupsSilenced && _state != NotchState.Expanded && !KeepOpen && !Notch.IsMouseOver;

        // =====================================================================
        //  Clock
        // =====================================================================

        private void OnClockTick()
        {
            DateTime now = DateTime.Now;
            string time = now.ToString(TimeFormat(_settings), CultureInfo.InvariantCulture);

            if (CollapsedTime.Text != time)
            {
                bool first = CollapsedTime.Text.Length == 0 || CollapsedTime.Text == "12:00 PM";
                CollapsedTime.Text = time;
                if (!first) RollTime();
            }
            if (ExpandedTime.Text != time) ExpandedTime.Text = time;

            string date = now.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
            if (DateText.Text != date) DateText.Text = date;

            UpdateProgress();

            // Re-align to the next wall-clock second so the display never lags by up to 1 s.
            _clockTimer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 5);
            if (IsEffectivelyVisible && !_clockTimer.IsEnabled) _clockTimer.Start();
        }

        // =====================================================================
        //  Media
        // =====================================================================

        private void OnMediaUpdated(object? sender, EventArgs e)
        {
            bool hasMedia = Media.HasSession && !string.IsNullOrWhiteSpace(Media.Title);
            string title = hasMedia ? Media.Title : "Not Playing";
            string artist = hasMedia
                ? (string.IsNullOrWhiteSpace(Media.Artist) ? FriendlyAppName(Media.SourceApp) : Media.Artist)
                : "Play something to see it here";

            TitleText.Text = title;
            ArtistText.Text = artist;
            PeekTitle.Text = title;
            PeekArtist.Text = artist;

            PlayPauseButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = Media.HasSession;

            bool hasArt = Media.Thumbnail != null;
            ArtBrush.ImageSource = Media.Thumbnail;
            PeekArtBrush.ImageSource = Media.Thumbnail;
            ArtImage.Visibility = PeekArtImage.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
            ArtPlaceholder.Visibility = PeekArtPlaceholder.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;

            ProgressTrack.Cursor = Media.CanSeek ? Cursors.Hand : Cursors.Arrow;
            UpdateClickTargets();

            ApplyDynamicColors(animate: true);
            _spectrum.SetPlaying(Media.IsPlaying);

            if (Media.IsPlaying)
            {
                _idleTimer.Stop();
                SetShowPlaying(true, animate: true);
                TryShowPendingPeek();
            }
            else if (_showPlaying && !_idleTimer.IsEnabled)
            {
                _idleTimer.Start();   // go idle only if it stays stopped for a moment
            }

            OnMediaUpdatedForLyrics(hasMedia);
            OnMediaUpdatedExtras();
            UpdateProgress();
        }

        private void OnTrackChanged(object? sender, EventArgs e)
        {
            RequestLyrics();
            if (!_settings.PeekOnTrackChange) return;
            if (DateTime.UtcNow - _startedAt < StartupPeekGrace) return;   // not on app start

            // Only peek for songs that actually start playing. The playing status often arrives a
            // moment after the new title, so wait briefly for it instead of deciding right away.
            _peekPending = true;
            _peekRequestedAt = DateTime.UtcNow;
            TryShowPendingPeek();
        }

        private void TryShowPendingPeek()
        {
            if (!_peekPending) return;
            if (DateTime.UtcNow - _peekRequestedAt > PeekWaitForPlay)
            {
                _peekPending = false;   // track changed while paused → no peek
                return;
            }
            if (!Media.IsPlaying) return;

            _peekPending = false;
            if (!CanPopUp) return;
            ShowTemporarily(NotchState.Peek, _settings.PeekDurationMs);
        }

        private void UpdateProgress()
        {
            // Only visible when expanded on the Music page → skip the work otherwise.
            if (_state != NotchState.Expanded || !_settings.ShowProgress || _currentPage != "Music") return;

            TimeSpan duration = Media.Duration;
            TimeSpan position = Media.Position;

            if (!Media.HasSession || duration <= TimeSpan.Zero)
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

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) => await Media.TogglePlayPauseAsync();
        private async void NextButton_Click(object sender, RoutedEventArgs e) => await Media.NextAsync();
        private async void PreviousButton_Click(object sender, RoutedEventArgs e) => await Media.PreviousAsync();

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
            => SettingsRequested?.Invoke(this, EventArgs.Empty);

        private async void ProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!Media.CanSeek || Media.Duration <= TimeSpan.Zero || ProgressTrack.ActualWidth <= 0) return;

            double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
            var target = TimeSpan.FromSeconds(Media.Duration.TotalSeconds * ratio);
            ProgressScale.ScaleX = ratio;
            await Media.SeekAsync(target);
        }

        private void TrackInfo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_settings.ClickToOpenApp || !Media.HasSession) return;
            e.Handled = true;
            AppActivator.Activate(Media.SourceApp);
        }

        private void UpdateClickTargets()
        {
            bool clickable = _settings.ClickToOpenApp && Media.HasSession;
            TrackInfo.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
            PeekContent.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
            TrackInfo.ToolTip = clickable ? "Open the app that's playing" : null;
        }

        // =====================================================================
        //  Volume
        // =====================================================================

        private void OnDefaultAudioDeviceChanged(object? sender, EventArgs e) => _svc.Analyzer.Restart();

        private void UpdateVolumeUi()
        {
            VolumePanel.Visibility = _settings.ShowVolumeIcon ? Visibility.Visible : Visibility.Collapsed;

            bool available = Volume.IsAvailable;
            VolumeSlider.IsEnabled = available;
            VolumeSlider.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

            _updatingVolumeUi = true;
            try
            {
                if (available) VolumeSlider.Value = Math.Round(Volume.Volume * 100);
            }
            finally
            {
                _updatingVolumeUi = false;
            }

            VolumeButton.Content = VolumeGlyph(available ? Volume.Volume : 1f, available && Volume.IsMuted);
            VolumeButton.ToolTip = available
                ? (Volume.IsMuted ? "Unmute" : $"Volume {Math.Round(Volume.Volume * 100)}% – click to mute")
                : "Mute / unmute";
        }

        public static string VolumeGlyph(float volume, bool muted)
        {
            if (muted) return "\uE74F";          // Mute
            if (volume <= 0.001f) return "\uE992"; // Volume0
            if (volume < 0.34f) return "\uE993";   // Volume1
            if (volume < 0.67f) return "\uE994";   // Volume2
            return "\uE995";                       // Volume3
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolumeUi || !IsLoaded || !Volume.IsAvailable) return;
            SuppressVolumeHud();
            Volume.SetVolume((float)(e.NewValue / 100.0));
            VolumeButton.Content = VolumeGlyph(Volume.Volume, Volume.IsMuted && e.NewValue <= 0);
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            SuppressVolumeHud();
            if (Volume.IsAvailable) Volume.ToggleMute();
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
            _popupTimer.Stop();
            _idleTimer.Stop();
            _settingsApplyTimer.Stop();
            _spectrum.SetSuspended(true);

            ClosePages();
            CloseActivities();
            CloseExtras();

            foreach (var unsubscribe in _unsubscribe)
            {
                try { unsubscribe(); } catch { /* ignore */ }
            }
            _unsubscribe.Clear();
        }
    }
}
