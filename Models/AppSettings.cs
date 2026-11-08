using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Crest.Models
{
    public enum NotchShape
    {
        /// <summary>Flat top flush with the screen edge, rounded bottom corners, concave "shoulders".</summary>
        Notch,
        /// <summary>Fully rounded floating pill.</summary>
        Pill
    }

    /// <summary>Theme of the settings window (the notch has its own colors).</summary>
    public enum AppTheme
    {
        Dark,
        Light,
        System
    }

    /// <summary>
    /// All user-configurable options. Every setter raises PropertyChanged so the
    /// notch can apply changes live while the settings window is open.
    /// </summary>
    public sealed class AppSettings : INotifyPropertyChanged
    {
        // ---------- Size ----------
        private double _collapsedWidth = 220;
        private double _collapsedHeight = 36;
        private double _expandedWidth = 420;
        private double _expandedHeight = 170;
        private double _cornerRadius = 14;
        private double _expandedCornerRadius = 26;

        // ---------- Shape / position ----------
        private NotchShape _shape = NotchShape.Notch;
        private double _shoulderSize = 10;
        private double _topMargin = 0;
        private double _horizontalOffset = 0;
        private string _monitorDevice = "";          // "" = primary monitor

        // ---------- Appearance ----------
        private string _backgroundColor = "#000000";
        private double _backgroundOpacity = 1.0;
        private string _textColor = "#FFFFFF";
        private string _accentColor = "#FFFFFF";
        private double _fontSize = 13;
        private string _fontFamily = "Segoe UI";
        private bool _use24HourClock = false;
        private bool _showSeconds = false;
        private AppTheme _settingsTheme = AppTheme.Dark;

        // ---------- Spectrum ----------
        private int _spectrumBarCount = 6;
        private string _spectrumColor = "#FFFFFF";
        private int _spectrumSpeedMs = 100;
        private double _spectrumBarWidth = 3;
        private double _spectrumBarSpacing = 2.5;
        private double _spectrumMaxHeight = 16;
        private bool _realSpectrum = false;

        // ---------- Media ----------
        private bool _preferPlayingSession = true;
        private bool _peekOnTrackChange = true;
        private int _peekDurationMs = 3000;
        private bool _clickToOpenApp = true;
        private bool _scrollLongTitles = true;
        private bool _useAlbumColors = false;
        private bool _shrinkWhenIdle = true;

        // ---------- Behavior ----------
        private bool _startWithWindows = false;
        private bool _alwaysOnTop = true;
        private bool _expandOnHover = true;
        private int _hoverDelayMs = 150;
        private int _animationDurationMs = 250;
        private int _collapseDelayMs = 200;
        private bool _scrollChangesVolume = true;

        // ---------- Display ----------
        private bool _showDate = true;
        private bool _showAlbumArt = true;
        private bool _showProgress = true;
        private bool _showVolumeIcon = true;
        private bool _showSpectrumWhenExpanded = true;

        // ================================================================ Size
        public double CollapsedWidth { get => _collapsedWidth; set => Set(ref _collapsedWidth, Clamp(value, 80, 800)); }
        public double CollapsedHeight { get => _collapsedHeight; set => Set(ref _collapsedHeight, Clamp(value, 20, 120)); }
        public double ExpandedWidth { get => _expandedWidth; set => Set(ref _expandedWidth, Clamp(value, 260, 900)); }
        public double ExpandedHeight { get => _expandedHeight; set => Set(ref _expandedHeight, Clamp(value, 120, 400)); }
        public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Clamp(value, 0, 60)); }
        public double ExpandedCornerRadius { get => _expandedCornerRadius; set => Set(ref _expandedCornerRadius, Clamp(value, 0, 80)); }

        // ================================================================ Shape / position
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public NotchShape Shape { get => _shape; set => Set(ref _shape, value); }
        public double ShoulderSize { get => _shoulderSize; set => Set(ref _shoulderSize, Clamp(value, 0, 30)); }
        public double TopMargin { get => _topMargin; set => Set(ref _topMargin, Clamp(value, 0, 60)); }
        public double HorizontalOffset { get => _horizontalOffset; set => Set(ref _horizontalOffset, Clamp(value, -3000, 3000)); }
        public string MonitorDevice { get => _monitorDevice; set => Set(ref _monitorDevice, value ?? ""); }

        // ================================================================ Appearance
        public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value ?? "#000000"); }
        public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, Clamp(value, 0.1, 1.0)); }
        public string TextColor { get => _textColor; set => Set(ref _textColor, value ?? "#FFFFFF"); }
        public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value ?? "#FFFFFF"); }
        public double FontSize { get => _fontSize; set => Set(ref _fontSize, Clamp(value, 8, 28)); }
        public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value); }
        public bool Use24HourClock { get => _use24HourClock; set => Set(ref _use24HourClock, value); }
        public bool ShowSeconds { get => _showSeconds; set => Set(ref _showSeconds, value); }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AppTheme SettingsTheme { get => _settingsTheme; set => Set(ref _settingsTheme, value); }

        // ================================================================ Spectrum
        public int SpectrumBarCount { get => _spectrumBarCount; set => Set(ref _spectrumBarCount, (int)Clamp(value, 3, 12)); }
        public string SpectrumColor { get => _spectrumColor; set => Set(ref _spectrumColor, value ?? "#FFFFFF"); }
        public int SpectrumSpeedMs { get => _spectrumSpeedMs; set => Set(ref _spectrumSpeedMs, (int)Clamp(value, 40, 400)); }
        public double SpectrumBarWidth { get => _spectrumBarWidth; set => Set(ref _spectrumBarWidth, Clamp(value, 1, 10)); }
        public double SpectrumBarSpacing { get => _spectrumBarSpacing; set => Set(ref _spectrumBarSpacing, Clamp(value, 0, 10)); }
        public double SpectrumMaxHeight { get => _spectrumMaxHeight; set => Set(ref _spectrumMaxHeight, Clamp(value, 6, 60)); }
        public bool RealSpectrum { get => _realSpectrum; set => Set(ref _realSpectrum, value); }

        // ================================================================ Media
        public bool PreferPlayingSession { get => _preferPlayingSession; set => Set(ref _preferPlayingSession, value); }
        public bool PeekOnTrackChange { get => _peekOnTrackChange; set => Set(ref _peekOnTrackChange, value); }
        public int PeekDurationMs { get => _peekDurationMs; set => Set(ref _peekDurationMs, (int)Clamp(value, 1000, 10000)); }
        public bool ClickToOpenApp { get => _clickToOpenApp; set => Set(ref _clickToOpenApp, value); }
        public bool ScrollLongTitles { get => _scrollLongTitles; set => Set(ref _scrollLongTitles, value); }
        public bool UseAlbumColors { get => _useAlbumColors; set => Set(ref _useAlbumColors, value); }
        /// <summary>Dynamic-Island style: when nothing plays, the notch shrinks to just the time.</summary>
        public bool ShrinkWhenIdle { get => _shrinkWhenIdle; set => Set(ref _shrinkWhenIdle, value); }

        // ================================================================ Behavior
        public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
        public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }
        public bool ExpandOnHover { get => _expandOnHover; set => Set(ref _expandOnHover, value); }
        public int HoverDelayMs { get => _hoverDelayMs; set => Set(ref _hoverDelayMs, (int)Clamp(value, 0, 1000)); }
        public int AnimationDurationMs { get => _animationDurationMs; set => Set(ref _animationDurationMs, (int)Clamp(value, 0, 1000)); }
        public int CollapseDelayMs { get => _collapseDelayMs; set => Set(ref _collapseDelayMs, (int)Clamp(value, 0, 2000)); }
        public bool ScrollChangesVolume { get => _scrollChangesVolume; set => Set(ref _scrollChangesVolume, value); }

        // ================================================================ Display
        public bool ShowDate { get => _showDate; set => Set(ref _showDate, value); }
        public bool ShowAlbumArt { get => _showAlbumArt; set => Set(ref _showAlbumArt, value); }
        public bool ShowProgress { get => _showProgress; set => Set(ref _showProgress, value); }
        public bool ShowVolumeIcon { get => _showVolumeIcon; set => Set(ref _showVolumeIcon, value); }
        public bool ShowSpectrumWhenExpanded { get => _showSpectrumWhenExpanded; set => Set(ref _showSpectrumWhenExpanded, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Copies every public read/write property from <paramref name="other"/> (raises change events).</summary>
        public void CopyFrom(AppSettings other)
        {
            foreach (PropertyInfo p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.CanRead && p.CanWrite)
                    p.SetValue(this, p.GetValue(other));
            }
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static double Clamp(double v, double min, double max)
            => double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
    }
}
