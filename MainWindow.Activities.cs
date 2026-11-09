using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Crest.Services;

namespace Crest
{
    /// <summary>
    /// "Live activities": charging, headphones, calendar reminders, timer done, the volume /
    /// brightness indicator, the timer bubble next to the notch, mic / camera dots and the glow.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
        private static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
        private static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
        private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
        private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
        private static readonly Color Violet = Color.FromRgb(0x7B, 0x6C, 0xFF);
        private static readonly Color Amber = Color.FromRgb(0xFF, 0xC4, 0x00);

        private DispatcherTimer? _hudSuppressTimer;
        private bool _hudSuppressed;
        private float _lastVolume = -1;
        private bool _lastMuted;
        private string? _activityOpensPage;   // clicking the pop-up opens this page

        private bool _bubbleShown;
        private double _bubbleWidth;

        private void InitActivities()
        {
            _hudSuppressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _hudSuppressTimer.Tick += (_, _) => { _hudSuppressTimer.Stop(); _hudSuppressed = false; };

            _lastVolume = Volume.Volume;
            _lastMuted = Volume.IsMuted;

            EventHandler charger = (_, _) => OnChargerChanged();
            Action<string, int> headphones = OnHeadphonesConnected;
            Action<int> brightness = OnBrightnessChanged;
            Action<CalendarEvent, string> reminder = OnCalendarReminder;
            EventHandler timerDone = (_, _) => OnTimerFinished();
            EventHandler privacy = (_, _) => UpdatePrivacyDots(animate: true);

            Subscribe(() => _svc.Power.ChargerChanged += charger, () => _svc.Power.ChargerChanged -= charger);
            Subscribe(() => _svc.Bluetooth.AudioDeviceConnected += headphones, () => _svc.Bluetooth.AudioDeviceConnected -= headphones);
            Subscribe(() => _svc.Brightness.Changed += brightness, () => _svc.Brightness.Changed -= brightness);
            Subscribe(() => _svc.Calendar.Reminder += reminder, () => _svc.Calendar.Reminder -= reminder);
            Subscribe(() => _svc.Timer.Finished += timerDone, () => _svc.Timer.Finished -= timerDone);
            Subscribe(() => _svc.Privacy.Changed += privacy, () => _svc.Privacy.Changed -= privacy);
        }

        private void ApplyActivitySettings()
        {
            _svc.Privacy.SetEnabled(_settings.PrivacyDots);
            UpdatePrivacyDots(animate: false);
            UpdateBubble(animate: false);
            UpdateGlow(animate: false);
        }

        private void CloseActivities()
        {
            _hudSuppressTimer?.Stop();
            CloseGlow();
        }

        // =====================================================================
        //  Pop-up
        // =====================================================================

        private void ShowActivity(string glyph, Color color, string title, string subtitle, string value,
                                  double? level, int milliseconds, string? opensPage = null, bool hud = false)
        {
            // The volume / brightness indicator may also show while the pointer is on the notch
            // (scrolling over it changes the volume).
            bool allowed = hud
                ? IsEffectivelyVisible && !_quiet && _state != NotchState.Expanded && !_typing && !_previewPinned
                : CanPopUp || _state == NotchState.Activity;
            if (!allowed || _quiet) return;

            ActivityIcon.Text = glyph;
            ActivityIconBg.Background = new SolidColorBrush(color);
            ActivityTitle.Text = title;
            ActivitySubtitle.Text = subtitle;
            ActivitySubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            ActivityValue.Text = value;
            ActivityValue.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            _activityOpensPage = opensPage;

            if (level is double l)
            {
                ActivityLevel.Visibility = Visibility.Visible;
                Animate(ActivityLevelScale, ScaleTransform.ScaleXProperty, Math.Clamp(l, 0, 1),
                        _state == NotchState.Activity ? 160 : 0, new CubicEase { EasingMode = EasingMode.EaseOut });
            }
            else
            {
                ActivityLevel.Visibility = Visibility.Collapsed;
            }

            ShowTemporarily(NotchState.Activity, milliseconds);
        }

        private void Activity_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activityOpensPage != null)
            {
                SelectPage(_activityOpensPage, 0);
                _hotkeyPinned = true;   // stay open until the mouse leaves
                SetState(NotchState.Expanded, animate: true);
            }
            else
            {
                SetState(NotchState.Collapsed, animate: true);
            }
        }

        // =====================================================================
        //  Volume / brightness indicator
        // =====================================================================

        /// <summary>Our own slider / scroll changes shouldn't pop the indicator while the player is open.</summary>
        private void SuppressVolumeHud()
        {
            _hudSuppressed = true;
            _hudSuppressTimer?.Stop();
            _hudSuppressTimer?.Start();
        }

        private void OnVolumeChanged(object? sender, EventArgs e)
        {
            UpdateVolumeUi();

            float v = Volume.Volume;
            bool muted = Volume.IsMuted;
            bool changed = _lastVolume >= 0 && (Math.Abs(v - _lastVolume) > 0.004f || muted != _lastMuted);
            _lastVolume = v;
            _lastMuted = muted;

            if (!changed || !_settings.VolumeHud || _hudSuppressed) return;
            if (DateTime.UtcNow - _startedAt < StartupPeekGrace) return;

            ShowActivity(VolumeGlyph(v, muted), Violet, "Volume", "",
                         muted ? "Muted" : $"{Math.Round(v * 100)}%", muted ? 0 : v, 1500, hud: true);
        }

        private void OnBrightnessChanged(int value)
        {
            if (!_settings.BrightnessHud) return;
            ShowActivity("\uE706", Amber, "Brightness", "", $"{value}%", value / 100.0, 1500, hud: true);
        }

        // =====================================================================
        //  Charging, headphones, calendar, timer
        // =====================================================================

        private void OnChargerChanged()
        {
            if (!_settings.ChargingActivity) return;
            var p = _svc.Power;
            ShowActivity("\uE945", p.OnAc ? Green : Gray,
                         p.OnAc ? "Charging" : "On battery", "",
                         p.Percent >= 0 ? $"{p.Percent}%" : "",
                         p.Percent >= 0 ? p.Percent / 100.0 : null, 2500);
        }

        private void OnHeadphonesConnected(string name, int battery)
        {
            if (!_settings.HeadphonesActivity) return;
            ShowActivity("\uE7F6", Blue, string.IsNullOrWhiteSpace(name) ? "Headphones" : name, "Connected",
                         battery >= 0 ? $"{battery}%" : "", battery >= 0 ? battery / 100.0 : null, 3000);
        }

        private void OnCalendarReminder(CalendarEvent ev, string when)
        {
            if (!_settings.CalendarActivity) return;
            bool now = when == "now";
            ShowActivity("\uE787", Red, ev.Title,
                         now ? $"Starting now · {ev.Start:t}" : $"Starts {when} · {ev.Start:t}",
                         now ? "Now" : when.Replace("in ", ""), null, 6000,
                         _settings.PageCalendar ? "Calendar" : null);
        }

        private void OnTimerFinished()
        {
            ShowActivity("\uE916", Orange, "Timer done",
                         $"{TimerService.Format(_svc.Timer.Duration)} timer", "", null, 6000,
                         _settings.PageTimer ? "Timer" : null);
        }

        // =====================================================================
        //  Timer bubble (split island)
        // =====================================================================

        private void UpdateBubble(bool animate)
        {
            if (Bubble == null) return;
            var t = _svc.Timer;
            bool show = t.IsActive && _state != NotchState.Expanded && IsEffectivelyVisible;
            int ms = animate ? _settings.AnimationDurationMs : 0;

            string text = TimerService.Format(t.Value);
            BubbleText.Text = text;
            BubbleIcon.Foreground = new SolidColorBrush(!t.Running ? Gray
                : t.Mode == TimerMode.Focus ? (t.IsBreak ? Green : Tomato)
                : Orange);
            Bubble.ToolTip = t.Mode == TimerMode.Focus ? (t.IsBreak ? "Break – open the timer" : $"Focus round {t.Round} – open the timer") : "Open the timer";
            double width = Math.Min(BubbleMaxWidth, 34 + text.Length * (_settings.FontSize * 0.62));

            if (show)
            {
                if (!_bubbleShown || Math.Abs(width - _bubbleWidth) > 0.5)
                {
                    _bubbleShown = true;
                    _bubbleWidth = width;
                    Bubble.Visibility = Visibility.Visible;
                    BubbleSpacer.Visibility = Visibility.Visible;
                    Animate(Bubble, WidthProperty, width, ms, GrowEase(true));
                    Animate(BubbleSpacer, WidthProperty, width + BubbleGap, ms, new CubicEase { EasingMode = EasingMode.EaseOut });
                    Fade(Bubble, 1, ms * 0.6, 0);
                }
            }
            else if (_bubbleShown)
            {
                _bubbleShown = false;
                _bubbleWidth = 0;
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                Animate(Bubble, WidthProperty, 0, ms, ease);
                Animate(BubbleSpacer, WidthProperty, 0, ms, ease);
                Fade(Bubble, 0, ms * 0.8, 0, () =>
                {
                    if (_bubbleShown) return;
                    Bubble.Visibility = Visibility.Collapsed;
                    BubbleSpacer.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void Bubble_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_settings.PageTimer) SelectPage("Timer", 0);
            _hotkeyPinned = true;   // stay open until the mouse enters and leaves the notch
            SetState(NotchState.Expanded, animate: true);
        }

        // =====================================================================
        //  Privacy dots
        // =====================================================================

        private double PrivacyDotsWidth
            => 6 + (CamDot.Visibility == Visibility.Visible ? 10 : 0) + (MicDot.Visibility == Visibility.Visible ? 7 : 0);

        private void UpdatePrivacyDots(bool animate)
        {
            var p = _svc.Privacy;
            bool cam = _settings.PrivacyDots && p.CameraApps.Count > 0;
            bool mic = _settings.PrivacyDots && p.MicApps.Count > 0;
            bool any = cam || mic;
            bool wasVisible = PrivacyDots.Visibility == Visibility.Visible;
            bool sizeChanged = wasVisible != any ||
                               (CamDot.Visibility == Visibility.Visible) != cam ||
                               (MicDot.Visibility == Visibility.Visible) != mic;

            CamDot.Visibility = ExpCamDot.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
            MicDot.Visibility = ExpMicDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
            PrivacyDots.Visibility = ExpandedPrivacyDots.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

            string tip = "";
            if (cam) tip += "Camera: " + string.Join(", ", p.CameraApps);
            if (mic) tip += (tip.Length > 0 ? "\n" : "") + "Microphone: " + string.Join(", ", p.MicApps);
            ExpandedPrivacyDots.ToolTip = any ? tip : null;

            if (sizeChanged) ResizeCollapsed(animate);
        }
    }
}
