using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Crest.Helpers;
using Crest.Models;
using Crest.Services;
using Microsoft.Win32;

namespace Crest
{
    /// <summary>
    /// v1.5 additions to the notch: tab order / start page, large player, page tint, weather colors and
    /// animation, world clocks, Recent page, karaoke, app switcher, mini player (hold Alt), more idle
    /// buddies, seasonal touches, sleep timer / laps / extra timers, alarm and reminders, wellbeing
    /// nudges, Tools box (launcher, currency, translate, QR, symbols), water counter, top apps,
    /// Wi-Fi, USB, storage and internet pop-ups, power buttons, wallpaper colors, tab tooltips.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Color Teal2 = Color.FromRgb(0x2D, 0xD4, 0xBF);
        private static Color? _wallpaperColor;
        private static DateTime _wallpaperReadAt = DateTime.MinValue;

        private bool _pagePickedBeforeOpen;
        private bool _miniMode;
        private bool _altHookInUse;
        private bool _altAlone;
        private DispatcherTimer? _altTimer;
        private bool _showTopApps;
        private bool _topAppsInUse;
        private string? _activityOpensPath;
        private string _armedPower = "";
        private DateTime _armedAt;
        private string _armedApp = "";
        private bool _buddyWaking;
        private DispatcherTimer? _translateTimer;
        private string _toolKind = "";
        private string _toolValue = "";
        private string _launchPath = "";
        private string _translateFor = "";

        private double LargeExtra => _settings.LargePlayer ? 44 : 0;

        private void InitV15()
        {
            _altTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(500) };
            _altTimer.Tick += (_, _) => { _altTimer.Stop(); if (_altAlone) ShowMiniPlayer(); };

            _translateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(650) };
            _translateTimer.Tick += async (_, _) => { _translateTimer.Stop(); await RunTranslateAsync(); };

            EventHandler history = (_, _) => { if (_currentPage == "Recent") RenderRecent(); };
            Action<Reminder> reminder = r => ShowActivity("\uE787", Amber, "Reminder", r.Text, "", null, 10000, hud: true);
            EventHandler alarm = (_, _) => ShowActivity("\uE916", Orange, "Alarm", DateTime.Now.ToString("t"), "", null, 15000, hud: true);
            EventHandler sleepDone = (_, _) => ShowActivity("\uE708", Violet, "Sleep timer", "Music paused. Good night!", "", null, 4000, hud: true);
            Action<string> extraDone = name => ShowActivity("\uE916", Orange, $"{name} timer done", "", "", null, 6000, _settings.PageTimer ? "Timer" : null, hud: true);
            EventHandler eye = (_, _) => ShowActivity("\uE890", Teal2, "Rest your eyes", "Look at something far away for 20 seconds", "", null, 7000);
            EventHandler stand = (_, _) => ShowActivity("\uE805", Green, "Time to stand up", "You've been sitting for an hour – stretch a little", "", null, 7000);
            EventHandler water = (_, _) => ShowActivity("💧", Color.FromRgb(0x5A, 0xC8, 0xFA), "Drink some water",
                $"{_svc.Wellbeing.Glasses} of {_settings.WaterGoal} glasses today", "", _svc.Wellbeing.Glasses / (double)Math.Max(1, _settings.WaterGoal), 6000,
                _settings.PageTools ? "Tools" : null);
            EventHandler morning = (_, _) => ShowMorningSummary();
            EventHandler waterChanged = (_, _) => RenderWater();
            Action<string, string> usb = OnUsbConnected;
            EventHandler usbChanged = (_, _) => RenderUsbButton();
            Action<string, int> storage = (drive, pct) =>
            {
                if (_settings.StorageAlerts)
                    ShowActivity("\uEDA2", Red, $"Drive {drive} is almost full", "Free up some space soon", $"{pct}% free", 1 - pct / 100.0, 7000);
            };
            Action<bool> internet = online =>
            {
                if (!_settings.InternetAlerts) return;
                if (online) ShowActivity("\uE701", Green, "Back online", "", "", null, 2500, hud: true);
                else ShowActivity("\uE701", Gray, "Internet connection lost", "Check your Wi-Fi or cable", "", null, 5000, hud: true);
            };
            EventHandler topApps = (_, _) => { if (_showTopApps && _currentPage == "System") RenderTopApps(); };
            Action rates = () => { if (_currentPage == "Tools") ToolsInputChanged(); };
            UserPreferenceChangedEventHandler prefs = (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.Desktop || !_settings.WallpaperAccent) return;
                _wallpaperReadAt = DateTime.MinValue;
                RefreshWallpaperColor();
                ApplyDynamicColors(animate: true);
            };

            Subscribe(() => _svc.History.Changed += history, () => _svc.History.Changed -= history);
            Subscribe(() => _svc.Reminders.Due += reminder, () => _svc.Reminders.Due -= reminder);
            Subscribe(() => _svc.Reminders.AlarmRang += alarm, () => _svc.Reminders.AlarmRang -= alarm);
            Subscribe(() => _svc.Timer.SleepFinished += sleepDone, () => _svc.Timer.SleepFinished -= sleepDone);
            Subscribe(() => _svc.Timer.ExtraFinished += extraDone, () => _svc.Timer.ExtraFinished -= extraDone);
            Subscribe(() => _svc.Wellbeing.EyeBreak += eye, () => _svc.Wellbeing.EyeBreak -= eye);
            Subscribe(() => _svc.Wellbeing.StandUp += stand, () => _svc.Wellbeing.StandUp -= stand);
            Subscribe(() => _svc.Wellbeing.WaterNudge += water, () => _svc.Wellbeing.WaterNudge -= water);
            Subscribe(() => _svc.Wellbeing.MorningSummary += morning, () => _svc.Wellbeing.MorningSummary -= morning);
            Subscribe(() => _svc.Wellbeing.WaterChanged += waterChanged, () => _svc.Wellbeing.WaterChanged -= waterChanged);
            Subscribe(() => _svc.SystemWatch.UsbConnected += usb, () => _svc.SystemWatch.UsbConnected -= usb);
            Subscribe(() => _svc.SystemWatch.UsbChanged += usbChanged, () => _svc.SystemWatch.UsbChanged -= usbChanged);
            Subscribe(() => _svc.SystemWatch.StorageLow += storage, () => _svc.SystemWatch.StorageLow -= storage);
            Subscribe(() => _svc.SystemWatch.InternetChanged += internet, () => _svc.SystemWatch.InternetChanged -= internet);
            Subscribe(() => _svc.TopApps.Updated += topApps, () => _svc.TopApps.Updated -= topApps);
            Subscribe(() => CurrencyService.RatesArrived += rates, () => CurrencyService.RatesArrived -= rates);
            Subscribe(() => SystemEvents.UserPreferenceChanged += prefs, () => SystemEvents.UserPreferenceChanged -= prefs);

            // Live info in each tab's tooltip.
            foreach (var p in _pages)
            {
                var page = p;
                page.Tab.ToolTipOpening += (_, _) => page.Tab.ToolTip = TabInfo(page.Name);
            }

            // Everything inside the notch (tint, blurred cover, weather sky…) follows its rounded corners.
            Notch.SizeChanged += (_, _) => UpdateContentClip();

            BuildPowerRow();
            BuildSymbols();
            RenderUsbButton();
            RenderWater();
        }

        private void ApplyV15Settings()
        {
            var s = _settings;
            _pagePickedBeforeOpen = false;
            UiSounds.Enabled = s.UiSounds;
            ApplyTabOrder();
            SetAltHook(s.AltMiniPlayer);

            ArtContainer.Width = ArtContainer.Height = s.LargePlayer ? 76 : 48;
            if (s.LargePlayer) TitleText.SetValue(TextElement.FontSizeProperty, s.FontSize * 1.45);
            else TitleText.SetResourceReference(TextElement.FontSizeProperty, "FontLarge");
            ExpandedHost.Height = s.ExpandedHeight + LargeExtra;

            ApplyKaraoke();
            UpdatePageTint(animate: false);
            if (s.WallpaperAccent) RefreshWallpaperColor();
            RenderWorldClocks();
            UpdateSeason();
        }

        private void CloseV15()
        {
            _altTimer?.Stop();
            _translateTimer?.Stop();
            SetAltHook(false);
            SetTopAppsInUse(false);
            SeasonLayer.Children.Clear();
            WeatherFx.Children.Clear();
        }

        /// <summary>Called from SetState: start page, sounds, tint, seasonal layer, mini player.</summary>
        private void OnStateChangedV15(NotchState previous, NotchState target, bool animate)
        {
            if (target == NotchState.Expanded && previous != NotchState.Expanded) UiSounds.Open();
            if (target != NotchState.Peek && _miniMode)
            {
                _miniMode = false;
                MiniControls.Visibility = Visibility.Collapsed;
            }
            if (target != NotchState.Expanded)
            {
                SetTopAppsInUse(false);
                WeatherFx.Children.Clear();
            }
            UpdatePageTint(animate);
            UpdateSeason();
        }

        // =====================================================================
        //  Tabs: order, start page, tooltips
        // =====================================================================

        private void ApplyTabOrder()
        {
            var order = _settings.TabOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var defaults = _pages.Select(p => p.Name).ToList();
            var sorted = _pages.OrderBy(p =>
            {
                int i = order.IndexOf(p.Name);
                return i >= 0 ? i : 100 + defaults.IndexOf(p.Name);
            }).ToList();
            if (sorted.SequenceEqual(_pages) && TabStrip.Children.Count == _pages.Count) return;

            _pages.Clear();
            _pages.AddRange(sorted);
            TabStrip.Children.Clear();
            foreach (var p in _pages) TabStrip.Children.Add(p.Tab);
        }

        /// <summary>Called when the notch opens: jump to the chosen start page.</summary>
        private void ApplyStartPage()
        {
            if (_pagePickedBeforeOpen) { _pagePickedBeforeOpen = false; return; }
            string start = _settings.StartPage;
            if (start == "Last") return;
            var page = _pages.FirstOrDefault(p => p.Name == start);
            if (page != null && page.Enabled() && _currentPage != start) SelectPage(start, 0);
        }

        private string TabInfo(string page)
        {
            try
            {
                switch (page)
                {
                    case "Music":
                        return Media.HasSession && Media.Title.Length > 0 ? $"Music · {Media.Title}" : "Music";
                    case "Lyrics":
                        return _lyrics?.Synced == true ? "Lyrics · synced" : "Lyrics";
                    case "Calendar":
                        var next = _svc.Calendar.Upcoming.FirstOrDefault();
                        return next != null ? $"Calendar · next: {next.Title} at {next.Start:t}" : "Calendar";
                    case "Weather":
                        var w = _svc.Weather.Current;
                        return w != null ? $"Weather · {Math.Round(w.Temp)}° {WeatherService.Describe(w.Code, w.IsDay).Text}" : "Weather";
                    case "System":
                        return $"System · CPU {_svc.Stats.Cpu:0}% · memory {_svc.Stats.Ram:0}%";
                    case "Timer":
                        var t = _svc.Timer;
                        return t.IsActive ? $"Timer · {TimerService.Format(t.Value)}" : "Timer & focus";
                    case "Shelf":
                        return $"Shelf · {_svc.Shelf.Files.Count} item{(_svc.Shelf.Files.Count == 1 ? "" : "s")}";
                    case "Notes":
                        int todo = _svc.Todo.Items.Count(i => !i.Done);
                        return todo > 0 ? $"Notes · {todo} to do" : "Notes & to-do";
                    case "Sound":
                        return Volume.IsAvailable ? $"Sound · volume {Math.Round(Volume.Volume * 100)}%" : "Sound";
                    case "Controls":
                        return "Quick controls";
                    case "Tools":
                        return "Tools · calculator, converter, launcher, QR, symbols";
                    case "Recent":
                        return _svc.History.Songs.Count > 0 ? $"Recent · last: {_svc.History.Songs[0].Title}" : "Recently played";
                }
            }
            catch
            {
                // a service isn't ready yet
            }
            return page;
        }

        // =====================================================================
        //  Look: page tint, karaoke, wallpaper colors, seasonal touches
        // =====================================================================

        private void UpdatePageTint(bool animate)
        {
            var page = _pages.FirstOrDefault(p => p.Name == _currentPage);
            bool show = _settings.PageTint && _state == NotchState.Expanded && page != null;
            if (show && page!.Tab.Foreground is SolidColorBrush b)
                PageTintTop.Color = Color.FromArgb(0x42, b.Color.R, b.Color.G, b.Color.B);
            Fade(PageTintLayer, show ? 1 : 0, animate ? 300 : 0, 0);
        }

        private void Karaoke_Click(object sender, RoutedEventArgs e) => _settings.KaraokeLyrics = !_settings.KaraokeLyrics;

        private void ApplyKaraoke()
        {
            bool big = _settings.KaraokeLyrics;
            if (big)
            {
                LyricCurrent.FontSize = Math.Max(22, _settings.FontSize * 1.9);
                LyricCurrent.MaxHeight = 90;
            }
            else
            {
                LyricCurrent.SetResourceReference(TextBlock.FontSizeProperty, "FontLarge");
                LyricCurrent.MaxHeight = 46;
            }
            LyricPrev.Visibility = LyricNext.Visibility = big ? Visibility.Collapsed : Visibility.Visible;
            KaraokeButton.Content = big ? "Normal size" : "Karaoke";
        }

        private static void RefreshWallpaperColor()
        {
            if (DateTime.Now - _wallpaperReadAt < TimeSpan.FromMinutes(10)) return;
            _wallpaperReadAt = DateTime.Now;
            _wallpaperColor = WallpaperColor.Read();
        }

        /// <summary>Accent from the wallpaper (used when album colors don't apply).</summary>
        private Color? WallpaperAccentColor => _settings.WallpaperAccent ? _wallpaperColor : null;

        private void UpdateSeason()
        {
            string kind = "";
            var d = DateTime.Today;
            if (_settings.SeasonalTouches && _state == NotchState.Expanded && _settings.AnimationDurationMs > 0)
            {
                if (d.Month == 1 && d.Day == 1) kind = "confetti";
                else if (d.Month == 2 && d.Day == 14) kind = "hearts";
                else if (d.Month == 12) kind = "snow";
            }
            if ((string?)SeasonLayer.Tag == kind) return;
            SeasonLayer.Tag = kind;
            SeasonLayer.Children.Clear();
            if (kind.Length == 0) return;

            var rng = new Random();
            double w = _settings.ExpandedWidth, h = _settings.ExpandedHeight + LargeExtra;
            Color[] confetti = { Color.FromRgb(0xFF, 0x5E, 0x7E), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x7B, 0x6C, 0xFF) };
            for (int i = 0; i < 16; i++)
            {
                FrameworkElement p;
                if (kind == "snow")
                {
                    double size = 2 + rng.NextDouble() * 2.5;
                    p = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Opacity = 0.35 + rng.NextDouble() * 0.4 };
                }
                else if (kind == "confetti")
                {
                    p = new Rectangle
                    {
                        Width = 3, Height = 6, Fill = new SolidColorBrush(confetti[i % confetti.Length]),
                        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(rng.Next(360))
                    };
                    ((RotateTransform)p.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1 + rng.NextDouble())) { RepeatBehavior = RepeatBehavior.Forever });
                }
                else
                {
                    p = new TextBlock { Text = "♥", FontSize = 8 + rng.Next(6), Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x9E)), Opacity = 0.55 };
                }

                Canvas.SetLeft(p, rng.NextDouble() * w);
                SeasonLayer.Children.Add(p);
                var begin = TimeSpan.FromSeconds(rng.NextDouble() * 5);
                var dur = TimeSpan.FromSeconds(kind == "hearts" ? 4 + rng.NextDouble() * 3 : 3 + rng.NextDouble() * 4);
                var fall = kind == "hearts"
                    ? new DoubleAnimation(h, -14, dur)
                    : new DoubleAnimation(-8, h + 4, dur);
                fall.BeginTime = begin;
                fall.RepeatBehavior = RepeatBehavior.Forever;
                Canvas.SetTop(p, -20);
                p.BeginAnimation(Canvas.TopProperty, fall);
            }
        }

        // =====================================================================
        //  Weather: colors, sky, animation, world clocks
        // =====================================================================

        /// <summary>Icon color for a WMO weather code.</summary>
        private static Color WeatherColor(int code, bool day) => code switch
        {
            0 or 1 => day ? Color.FromRgb(0xFF, 0xC9, 0x40) : Color.FromRgb(0xC8, 0xD3, 0xFF),
            2 or 3 => Color.FromRgb(0x9F, 0xC4, 0xE8),
            45 or 48 => Color.FromRgb(0xB0, 0xBE, 0xC5),
            >= 71 and <= 77 or 85 or 86 => Color.FromRgb(0xE0, 0xF7, 0xFF),
            >= 95 => Color.FromRgb(0xB3, 0x88, 0xFF),
            >= 51 => Color.FromRgb(0x4F, 0xC3, 0xFF),
            _ => Color.FromRgb(0xFF, 0xC9, 0x40)
        };

        private static Color TempColor(double t, bool fahrenheit)
        {
            double c = fahrenheit ? (t - 32) * 5 / 9 : t;
            if (c <= 5) return Color.FromRgb(0x93, 0xC5, 0xFD);
            if (c <= 15) return Color.FromRgb(0x7D, 0xD3, 0xFC);
            if (c <= 24) return Color.FromRgb(0xA7, 0xF3, 0xD0);
            if (c <= 31) return Color.FromRgb(0xFC, 0xD3, 0x4D);
            return Color.FromRgb(0xFB, 0x92, 0x3C);
        }

        private static string WeatherKind(int code) => code switch
        {
            0 or 1 => "clear",
            2 or 3 or 45 or 48 => "cloudy",
            >= 71 and <= 77 or 85 or 86 => "snow",
            >= 95 => "storm",
            >= 51 => "rain",
            _ => "clear"
        };

        /// <summary>Colors and animates the Weather page (called after the plain render).</summary>
        private void DecorateWeather()
        {
            var w = _svc.Weather.Current;
            RenderWorldClocks();
            if (w == null)
            {
                WeatherSky.Opacity = 0;
                WeatherFx.Children.Clear();
                return;
            }

            WeatherIcon.Foreground = new SolidColorBrush(WeatherColor(w.Code, w.IsDay));
            WeatherTemp.Foreground = new SolidColorBrush(TempColor(w.Temp, w.Fahrenheit));
            foreach (var col in WeatherHours.Children.OfType<StackPanel>())
            {
                int i = WeatherHours.Children.IndexOf(col);
                if (i >= w.Hours.Count || col.Children.Count < 3) continue;
                var h = w.Hours[i];
                ((TextBlock)col.Children[1]).Foreground = new SolidColorBrush(WeatherColor(h.Code, h.IsDay));
                ((TextBlock)col.Children[2]).Foreground = new SolidColorBrush(TempColor(h.Temp, w.Fahrenheit));
            }

            string kind = WeatherKind(w.Code);
            (Color a, Color b) sky = kind switch
            {
                "clear" => w.IsDay ? (Color.FromRgb(0x0E, 0x5A, 0xA8), Color.FromRgb(0x0B, 0x22, 0x44)) : (Color.FromRgb(0x2A, 0x23, 0x6B), Color.FromRgb(0x0D, 0x0B, 0x24)),
                "cloudy" => (Color.FromRgb(0x33, 0x41, 0x55), Color.FromRgb(0x11, 0x15, 0x1C)),
                "rain" => (Color.FromRgb(0x1E, 0x3A, 0x5F), Color.FromRgb(0x0B, 0x13, 0x22)),
                "snow" => (Color.FromRgb(0x47, 0x55, 0x69), Color.FromRgb(0x16, 0x1B, 0x24)),
                _ => (Color.FromRgb(0x3B, 0x12, 0x6B), Color.FromRgb(0x12, 0x07, 0x24))
            };
            WeatherSky.Background = new LinearGradientBrush(sky.a, sky.b, new Point(0, 0), new Point(0.6, 1));
            Fade(WeatherSky, 0.75, 300, 0);

            string fxKind = kind + (w.IsDay ? "-day" : "-night");
            if ((string?)WeatherFx.Tag == fxKind && WeatherFx.Children.Count > 0) return;
            WeatherFx.Tag = fxKind;
            WeatherFx.Children.Clear();
            if (_settings.AnimationDurationMs <= 0) return;

            var rng = new Random();
            double width = _settings.ExpandedWidth - 24, height = _settings.ExpandedHeight + LargeExtra - 40;
            if (kind is "rain" or "storm" or "snow")
            {
                bool snow = kind == "snow";
                for (int i = 0; i < (snow ? 18 : 22); i++)
                {
                    FrameworkElement drop = snow
                        ? new Ellipse { Width = 3, Height = 3, Fill = Brushes.White, Opacity = 0.5 }
                        : new Rectangle { Width = 1.2, Height = 9, RadiusX = 0.6, RadiusY = 0.6, Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x9F, 0xD8, 0xFF)) };
                    Canvas.SetLeft(drop, rng.NextDouble() * width);
                    Canvas.SetTop(drop, -12);
                    WeatherFx.Children.Add(drop);
                    var dur = TimeSpan.FromSeconds(snow ? 3 + rng.NextDouble() * 3 : 0.6 + rng.NextDouble() * 0.5);
                    drop.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(-12, height, dur)
                    {
                        BeginTime = TimeSpan.FromSeconds(rng.NextDouble() * 3),
                        RepeatBehavior = RepeatBehavior.Forever
                    });
                }
                if (kind == "storm")
                {
                    // An occasional flash.
                    var flash = new Rectangle { Width = width, Height = height, Fill = Brushes.White, Opacity = 0 };
                    WeatherFx.Children.Add(flash);
                    var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(7), RepeatBehavior = RepeatBehavior.Forever };
                    k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.0)));
                    k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.8)));
                    k.KeyFrames.Add(new LinearDoubleKeyFrame(0.18, KeyTime.FromPercent(0.81)));
                    k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.84)));
                    k.KeyFrames.Add(new LinearDoubleKeyFrame(0.12, KeyTime.FromPercent(0.86)));
                    k.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.9)));
                    flash.BeginAnimation(OpacityProperty, k);
                }
            }
            else if (kind == "clear" && w.IsDay)
            {
                // A soft, slowly turning sun glow in the corner.
                var glow = new Ellipse
                {
                    Width = 140, Height = 140,
                    Fill = new RadialGradientBrush(Color.FromArgb(0x55, 0xFF, 0xD3, 0x5A), Color.FromArgb(0, 0xFF, 0xD3, 0x5A)),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1, 1)
                };
                Canvas.SetLeft(glow, -40);
                Canvas.SetTop(glow, -60);
                WeatherFx.Children.Add(glow);
                var pulse = new DoubleAnimation(0.9, 1.15, TimeSpan.FromSeconds(3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
                ((ScaleTransform)glow.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                ((ScaleTransform)glow.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
            else if (kind == "clear")
            {
                // Twinkling stars.
                for (int i = 0; i < 14; i++)
                {
                    var star = new Ellipse { Width = 2, Height = 2, Fill = Brushes.White };
                    Canvas.SetLeft(star, rng.NextDouble() * width);
                    Canvas.SetTop(star, rng.NextDouble() * height * 0.7);
                    WeatherFx.Children.Add(star);
                    star.BeginAnimation(OpacityProperty, new DoubleAnimation(0.1, 0.8, TimeSpan.FromSeconds(1 + rng.NextDouble() * 2))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(rng.NextDouble() * 2)
                    });
                }
            }
            else
            {
                // Drifting clouds.
                for (int i = 0; i < 3; i++)
                {
                    var cloud = new Ellipse { Width = 90 + i * 30, Height = 26 + i * 6, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) };
                    Canvas.SetTop(cloud, 4 + i * 26);
                    WeatherFx.Children.Add(cloud);
                    cloud.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(-160, width + 20, TimeSpan.FromSeconds(18 + i * 7))
                    {
                        BeginTime = TimeSpan.FromSeconds(-i * 6), RepeatBehavior = RepeatBehavior.Forever
                    });
                }
            }
        }

        private static readonly Dictionary<string, string> CityZones = new(StringComparer.OrdinalIgnoreCase)
        {
            ["london"] = "Europe/London", ["uk"] = "Europe/London", ["paris"] = "Europe/Paris", ["berlin"] = "Europe/Berlin",
            ["madrid"] = "Europe/Madrid", ["rome"] = "Europe/Rome", ["amsterdam"] = "Europe/Amsterdam", ["moscow"] = "Europe/Moscow",
            ["istanbul"] = "Europe/Istanbul", ["dubai"] = "Asia/Dubai", ["riyadh"] = "Asia/Riyadh", ["delhi"] = "Asia/Kolkata",
            ["new delhi"] = "Asia/Kolkata", ["mumbai"] = "Asia/Kolkata", ["kolkata"] = "Asia/Kolkata", ["bangalore"] = "Asia/Kolkata",
            ["india"] = "Asia/Kolkata", ["dhaka"] = "Asia/Dhaka", ["karachi"] = "Asia/Karachi", ["kathmandu"] = "Asia/Kathmandu",
            ["bangkok"] = "Asia/Bangkok", ["jakarta"] = "Asia/Jakarta", ["singapore"] = "Asia/Singapore", ["hong kong"] = "Asia/Hong_Kong",
            ["shanghai"] = "Asia/Shanghai", ["beijing"] = "Asia/Shanghai", ["manila"] = "Asia/Manila", ["tokyo"] = "Asia/Tokyo",
            ["seoul"] = "Asia/Seoul", ["sydney"] = "Australia/Sydney", ["melbourne"] = "Australia/Melbourne", ["perth"] = "Australia/Perth",
            ["auckland"] = "Pacific/Auckland", ["new york"] = "America/New_York", ["nyc"] = "America/New_York", ["toronto"] = "America/Toronto",
            ["chicago"] = "America/Chicago", ["denver"] = "America/Denver", ["los angeles"] = "America/Los_Angeles", ["la"] = "America/Los_Angeles",
            ["san francisco"] = "America/Los_Angeles", ["seattle"] = "America/Los_Angeles", ["vancouver"] = "America/Vancouver",
            ["mexico city"] = "America/Mexico_City", ["sao paulo"] = "America/Sao_Paulo", ["são paulo"] = "America/Sao_Paulo",
            ["buenos aires"] = "America/Argentina/Buenos_Aires", ["johannesburg"] = "Africa/Johannesburg", ["cairo"] = "Africa/Cairo",
            ["lagos"] = "Africa/Lagos", ["nairobi"] = "Africa/Nairobi", ["utc"] = "UTC", ["gmt"] = "UTC",
        };

        private static TimeZoneInfo? FindZone(string city)
        {
            if (!CityZones.TryGetValue(city.Trim(), out var iana)) return null;
            try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
            catch
            {
                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win))
                {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
                }
                return null;
            }
        }

        /// <summary>"London 10:17 · New York 05:17" under the weather, and in the date's tooltip.</summary>
        private void RenderWorldClocks()
        {
            var parts = new List<string>();
            foreach (var city in _settings.WorldClocks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4))
            {
                var zone = FindZone(city);
                if (zone == null) continue;
                var t = TimeZoneInfo.ConvertTime(DateTime.Now, zone);
                string name = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(city.ToLowerInvariant());
                parts.Add($"{name} {t.ToString(_settings.Use24HourClock ? "HH:mm" : "h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            string text = string.Join("   ·   ", parts);
            WorldClocksText.Text = "🌐  " + text;
            WorldClocksText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DateText.ToolTip = DateTime.Now.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)
                               + (parts.Count > 0 ? "\n" + text.Replace("   ·   ", "\n") : "");
        }

        // =====================================================================
        //  Music: app switcher, mini player (hold Alt)
        // =====================================================================

        /// <summary>
        /// Clips the notch's content to the notch's own rounded outline. A Border only rounds its own
        /// background, so without this the page tint, blurred cover and weather sky would show square
        /// corners outside the curve.
        /// </summary>
        private void UpdateContentClip()
        {
            if (ContentRoot == null || Notch == null) return;
            double w = Notch.ActualWidth, h = Notch.ActualHeight;
            if (w <= 0 || h <= 0) return;
            ContentRoot.Clip = RoundedRect(w, h, Notch.CornerRadius);
        }

        private static Geometry RoundedRect(double w, double h, CornerRadius r)
        {
            double max = Math.Min(w, h) / 2;
            double tl = Math.Clamp(r.TopLeft, 0, max), tr = Math.Clamp(r.TopRight, 0, max);
            double br = Math.Clamp(r.BottomRight, 0, max), bl = Math.Clamp(r.BottomLeft, 0, max);

            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(tl, 0), isFilled: true, isClosed: true);
                c.LineTo(new Point(w - tr, 0), true, false);
                if (tr > 0) c.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(w, h - br), true, false);
                if (br > 0) c.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(bl, h), true, false);
                if (bl > 0) c.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(0, tl), true, false);
                if (tl > 0) c.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        /// <summary>
        /// The date in the open notch's header, as long as fits beside the tabs:
        /// "Wed, Nov 11" → "Wed 11" → "11". The full date is in its tooltip.
        /// </summary>
        private string HeaderDate(DateTime now)
        {
            int tabs = TabStrip.Visibility == Visibility.Visible
                ? TabStrip.Children.OfType<Button>().Count(b => b.Visibility == Visibility.Visible)
                : 0;
            double tabWidth = TabStrip.Children.OfType<Button>().FirstOrDefault()?.Width is double w && w > 0 ? w : 21;
            // Header columns are *, tabs, * – each side gets half of what the tabs leave.
            double room = (_settings.ExpandedWidth - 40 - tabs * tabWidth) / 2 - 6;

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(DateText.FontFamily, DateText.FontStyle, DateText.FontWeight, DateText.FontStretch);
            foreach (var format in new[] { "ddd, MMM d", "ddd d", "%d" })
            {
                string text = now.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
                var measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                 typeface, DateText.FontSize, Brushes.White, pixelsPerDip);
                if (measured.WidthIncludingTrailingWhitespace <= room) return text;
            }
            return "";
        }

        private void UpdateAppSwitch()
        {
            var apps = Media.SessionApps;
            if (apps.Count < 2)
            {
                AppSwitchButton.Visibility = Visibility.Collapsed;
                return;
            }
            AppSwitchButton.Visibility = Visibility.Visible;
            AppSwitchButton.Content = $"⇄  {FriendlyAppName(Media.SourceApp)} · {apps.Count - 1} more playing";
        }

        private void AppSwitch_Click(object sender, RoutedEventArgs e) => Media.SwitchToNextApp();

        private void SetAltHook(bool on)
        {
            if (on == _altHookInUse) return;
            _altHookInUse = on;
            if (on) { _svc.KeyboardHook.Key += OnAltKey; _svc.KeyboardHook.AddUser(); }
            else { _svc.KeyboardHook.Key -= OnAltKey; _svc.KeyboardHook.RemoveUser(); _altTimer?.Stop(); }
        }

        private void OnAltKey(int vk, bool down)
        {
            bool alt = vk is 0x12 or 0xA4 or 0xA5;
            if (alt && down)
            {
                if (!_altAlone && !_altTimer!.IsEnabled) { _altAlone = true; _altTimer.Start(); }
                return;
            }
            if (alt)
            {
                _altAlone = false;
                _altTimer!.Stop();
                // Let go: the mini player stays a moment so you can reach it with the mouse.
                if (_miniMode && _state == NotchState.Peek) ShowTemporarily(NotchState.Peek, 2500);
                return;
            }
            if (down) { _altAlone = false; _altTimer!.Stop(); }   // Alt + key is a shortcut
        }

        private void ShowMiniPlayer()
        {
            if (!Media.HasSession || !IsEffectivelyVisible || _state == NotchState.Expanded || _typing) return;
            _miniMode = true;
            MiniControls.Visibility = Visibility.Visible;
            PeekSpectrum.Visibility = Visibility.Collapsed;
            MiniPlayButton.Content = Media.IsPlaying ? "\uE769" : "\uE768";
            ShowTemporarily(NotchState.Peek, 4000);
        }

        // =====================================================================
        //  Recent page
        // =====================================================================

        private void RenderRecent()
        {
            var h = _svc.History;
            var top = h.WeekTop();
            RecentTop.Text = top.SongPlays > 1
                ? $"★  This week: {top.Song} ({top.SongPlays}×)" + (top.ArtistPlays > 1 ? $" · top artist {top.Artist}" : "")
                : _settings.KeepHistory ? "Songs you play show up here." : "Listening history is off (Settings → Media).";

            RecentList.Children.Clear();
            foreach (var song in h.Songs.Take(30))
            {
                var title = MakeText(song.Title, bold: true);
                title.FontSize = Math.Max(8, _settings.FontSize - 1);
                var sub = MakeText((song.Artist.Length > 0 ? song.Artist + " · " : "") + Ago(song.At), subtle: true);
                sub.FontSize = Math.Max(8, _settings.FontSize - 2.5);
                var stack = new StackPanel();
                stack.Children.Add(title);
                stack.Children.Add(sub);
                var row = new Button
                {
                    Style = (Style)FindResource("TextButton"),
                    Content = stack,
                    Margin = new Thickness(2, 0, 0, 5),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    ToolTip = song.App.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ? "Search for it in Spotify" : "Search for it on YouTube"
                };
                var s = song;
                row.Click += (_, _) => SearchSong(s);
                RecentList.Children.Add(row);
            }
        }

        private static string Ago(DateTime at)
        {
            var d = DateTime.Now - at;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
            return at.ToString("ddd d MMM");
        }

        private static void SearchSong(PlayedSong s)
        {
            string q = (s.Title + " " + s.Artist).Trim();
            string url = s.App.Contains("Spotify", StringComparison.OrdinalIgnoreCase)
                ? "spotify:search:" + Uri.EscapeDataString(q)
                : "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError("Search failed", ex); }
        }

        // =====================================================================
        //  Timer page: sleep, laps, extra timers, alarm
        // =====================================================================

        private void RenderTimerExtras()
        {
            var t = _svc.Timer;
            bool sleep = t.Mode == TimerMode.Sleep, stopwatch = t.Mode == TimerMode.Stopwatch;
            ModeSleepBtn.Opacity = sleep ? 1 : 0.45;

            // Presets: minutes for a timer, longer for the sleep timer.
            int[] minutes = sleep ? new[] { 15, 30, 45, 60 } : new[] { 1, 5, 10, 25 };
            int i = 0;
            foreach (var b in TimerPresets.Children.OfType<Button>())
            {
                if (i >= minutes.Length) break;
                b.Tag = minutes[i].ToString();
                b.Content = $"{minutes[i]} min";
                i++;
            }
            if (sleep)
            {
                TimerPresets.Visibility = Visibility.Visible;
                TimerSub.Visibility = Visibility.Visible;
                TimerSub.Text = t.Running ? "Music pauses when this reaches zero" : "Pick how long, then press play";
                TimerSub.Foreground = new SolidColorBrush(Violet);
            }

            LapButton.Visibility = stopwatch ? Visibility.Visible : Visibility.Collapsed;
            LapsPanel.Visibility = stopwatch && t.Laps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LapsPanel.Children.Clear();
            for (int n = 0; n < Math.Min(3, t.Laps.Count); n++)
            {
                var lap = MakeText($"Lap {t.Laps.Count - n}    {TimerService.Format(t.Laps[n])}", subtle: n > 0, bold: n == 0);
                lap.HorizontalAlignment = HorizontalAlignment.Right;
                LapsPanel.Children.Add(lap);
            }

            TimerExtras.Children.Clear();
            foreach (var x in t.Extras)
            {
                var chip = new Button
                {
                    Style = (Style)FindResource("ChipButton"),
                    Content = $"{x.Name} · {TimerService.Format(x.Left)}  ✕",
                    ToolTip = "Extra timer – click to cancel"
                };
                var timer = x;
                chip.Click += (_, _) => t.RemoveExtra(timer);
                TimerExtras.Children.Add(chip);
            }

            AlarmText.Visibility = _settings.AlarmEnabled ? Visibility.Visible : Visibility.Collapsed;
            AlarmText.Text = $"⏰  Alarm {_settings.AlarmTime}";
        }

        private void Lap_Click(object sender, RoutedEventArgs e) => _svc.Timer.Lap();

        // =====================================================================
        //  Pop-ups that open something
        // =====================================================================

        private void OnUsbConnected(string root, string label)
        {
            RenderUsbButton();
            if (!_settings.UsbActivity) return;
            ShowActivity("\uE88E", Blue, $"{label} connected", $"Drive {root.TrimEnd('\\')} · click to open", "", null, 5000);
            if (_state == NotchState.Activity) _activityOpensPath = root;
        }

        private void RenderUsbButton()
        {
            var drives = SystemWatchService.RemovableDrives();
            if (drives.Count == 0)
            {
                UsbButton.Visibility = Visibility.Collapsed;
                return;
            }
            var d = drives[0];
            UsbButton.Visibility = Visibility.Visible;
            UsbButton.Content = $"⏏  Eject {d.Label} ({d.Root.TrimEnd('\\')})";
            UsbButton.Tag = d.Root;
            UsbButton.ToolTip = "Safely remove the drive";
        }

        private void UsbEject_Click(object sender, RoutedEventArgs e)
        {
            if (UsbButton.Tag is not string root) return;
            SystemWatchService.Eject(root);
            ShowHeaderToast("\uE88E", Green, "Safe to remove " + root.TrimEnd('\\'), "", null, 2500);
        }

        private void ShowMorningSummary()
        {
            var parts = new List<string>();
            var w = _svc.Weather.Current;
            if (w != null) parts.Add($"{Math.Round(w.Temp)}° {WeatherService.Describe(w.Code, w.IsDay).Text.ToLowerInvariant()}");
            var ev = _svc.Calendar.Upcoming.FirstOrDefault(e => e.Start.Date == DateTime.Today);
            if (ev != null) parts.Add($"{ev.Title} at {ev.Start:t}");
            int todo = _svc.Todo.Items.Count(i => !i.Done);
            if (todo > 0) parts.Add($"{todo} to-do{(todo == 1 ? "" : "s")}");
            if (parts.Count == 0) parts.Add("Have a great day!");
            ShowActivity("\uE706", Amber, "Good morning", string.Join(" · ", parts), "", null, 9000);
        }

        // =====================================================================
        //  System page: colored meters, top apps, Wi-Fi
        // =====================================================================

        private void ColorMeter(Border fill, double percent)
        {
            if (percent >= 90) fill.Background = new SolidColorBrush(Red);
            else if (percent >= 70) fill.Background = new SolidColorBrush(Orange);
            else fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        }

        private void DecorateSystem()
        {
            var st = _svc.Stats;
            ColorMeter(CpuFill, st.Cpu);
            ColorMeter(RamFill, st.Ram);
            ColorMeter(GpuFill, st.Gpu ?? 0);
            ColorMeter(DiskFill, st.Disk);

            // RenderSystem has just written the device batteries; put the Wi-Fi in front of them.
            var wifi = SystemWatchService.WiFi();
            string devices = DeviceBatteriesText();
            string net = wifi is { } w ? $"Wi-Fi {w.Ssid} {new string('●', Math.Clamp(w.Bars, 0, 5))}{new string('○', 5 - Math.Clamp(w.Bars, 0, 5))}" : "";
            DevicesFooter.Text = net.Length > 0 && devices.Length > 0 ? net + "   ·   " + devices : net + devices;
            DevicesFooter.Visibility = DevicesFooter.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TopApps_Click(object sender, RoutedEventArgs e)
        {
            _showTopApps = !_showTopApps;
            SystemMeters.Visibility = _showTopApps ? Visibility.Collapsed : Visibility.Visible;
            TopAppsPanel.Visibility = _showTopApps ? Visibility.Visible : Visibility.Collapsed;
            TopAppsButton.Content = _showTopApps ? "‹ Meters" : "Top apps ›";
            SetTopAppsInUse(_showTopApps && _state == NotchState.Expanded && _currentPage == "System");
            RenderTopApps();
        }

        private void SetTopAppsInUse(bool on)
        {
            if (on == _topAppsInUse) return;
            _topAppsInUse = on;
            if (on) _svc.TopApps.AddUser();
            else _svc.TopApps.RemoveUser();
        }

        private void RenderTopApps()
        {
            TopAppsPanel.Children.Clear();
            var top = _svc.TopApps.Top;
            if (top.Count == 0)
            {
                TopAppsPanel.Children.Add(MakeText("Measuring…", subtle: true));
                return;
            }
            foreach (var (name, cpu, mem) in top.Take(4))
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                var close = new Button
                {
                    Style = (Style)FindResource("TextButton"),
                    Content = _armedApp == name && DateTime.Now - _armedAt < TimeSpan.FromSeconds(3) ? "Close?" : "✕",
                    Foreground = _armedApp == name ? new SolidColorBrush(Red) : (Brush)FindResource("SubtleTextBrush"),
                    ToolTip = "Close " + name + " (click twice)",
                    Margin = new Thickness(10, 0, 0, 0)
                };
                string app = name;
                close.Click += async (_, _) =>
                {
                    if (_armedApp == app && DateTime.Now - _armedAt < TimeSpan.FromSeconds(3))
                    {
                        _armedApp = "";
                        await TopAppsService.CloseAsync(app);
                    }
                    else
                    {
                        _armedApp = app;
                        _armedAt = DateTime.Now;
                    }
                    RenderTopApps();
                };
                DockPanel.SetDock(close, Dock.Right);
                row.Children.Add(close);
                var stats = MakeText($"{cpu:0}% · {mem:#,0} MB", subtle: true);
                stats.Margin = new Thickness(8, 0, 0, 0);
                stats.Foreground = new SolidColorBrush(cpu >= 50 ? Red : cpu >= 20 ? Orange : Gray);
                DockPanel.SetDock(stats, Dock.Right);
                row.Children.Add(stats);
                row.Children.Add(MakeText(name, bold: true));
                TopAppsPanel.Children.Add(row);
            }
        }

        // =====================================================================
        //  Controls page: power row
        // =====================================================================

        private void BuildPowerRow()
        {
            void Add(string key, string glyph, string tip)
            {
                var b = new Button { Style = (Style)FindResource("SmallMediaButton"), Width = 24, Height = 24, FontSize = 11, Content = glyph, ToolTip = tip, Tag = key };
                b.Click += (_, _) => Power(key, b);
                PowerRow.Children.Add(b);
            }
            Add("sleep", "\uE708", "Sleep (click twice)");
            Add("lock", "\uE72E", "Lock the PC");
            Add("restart", "\uE777", "Restart (click twice)");
            Add("shutdown", "\uE7E8", "Shut down (click twice)");
        }

        private void Power(string key, Button button)
        {
            if (key == "lock")
            {
                NativeMethods.LockWorkStation();
                return;
            }
            if (_armedPower != key || DateTime.Now - _armedAt > TimeSpan.FromSeconds(3))
            {
                // First click: arm it and say so.
                _armedPower = key;
                _armedAt = DateTime.Now;
                foreach (var b in PowerRow.Children.OfType<Button>()) b.ClearValue(ForegroundProperty);
                button.Foreground = new SolidColorBrush(Red);
                string what = key == "sleep" ? "sleep" : key == "restart" ? "restart" : "shut down";
                ShowHeaderToast(button.Content as string ?? "", Red, $"Click again to {what}", "", null, 2800);
                return;
            }
            _armedPower = "";
            button.ClearValue(ForegroundProperty);
            try
            {
                if (key == "sleep") NativeMethods.SetSuspendState(false, false, false);
                else Process.Start(new ProcessStartInfo("shutdown", key == "restart" ? "/r /t 0" : "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                App.LogError("Power action failed", ex);
            }
        }

        // =====================================================================
        //  Tools box: calculator · converter · currency · reminders · translate · QR · symbols · launcher
        // =====================================================================

        private void ToolsInputChanged()
        {
            string text = CalcBox.Text.Trim();
            CalcPlaceholder.Visibility = CalcBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolsResults.Children.Clear();
            QrHost.Visibility = Visibility.Collapsed;
            ToolsSide.Visibility = Visibility.Visible;
            CalcResult.Opacity = 1;
            _toolKind = "";
            _toolValue = "";
            _translateTimer?.Stop();

            if (text.Length == 0)
            {
                CalcResult.Text = "=";
                CalcResult.Opacity = 0.4;
                CalcHint.Text = "Sums, 5 km in miles, 50 usd in inr, translate… to hindi, remind me in 20 min to…";
                return;
            }

            if (ReminderService.TryParse(text, out var what, out var due))
            {
                _toolKind = "remind";
                CalcResult.Text = "⏰ " + (due.Date == DateTime.Today ? due.ToString("t") : due.ToString("ddd t"));
                CalcHint.Text = "Enter sets it: " + what;
                return;
            }

            if (TranslateService.TryParse(text, out var phrase, out var lang, out var langName))
            {
                _toolKind = "translate";
                _translateFor = phrase + "|" + lang;
                CalcResult.Text = "…";
                CalcResult.Opacity = 0.5;
                CalcHint.Text = $"Translating to {langName} · Enter copies";
                _translateTimer?.Start();
                return;
            }

            if (text.StartsWith(':'))
            {
                var found = SymbolList.Where(x => x.Name.Contains(text[1..], StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
                _toolKind = "symbol";
                _toolValue = found.Count > 0 ? found[0].Symbol : "";
                CalcResult.Text = found.Count > 0 ? found[0].Symbol : "?";
                CalcHint.Text = found.Count > 0 ? "Enter copies it, or click another" : "No symbol with that name";
                foreach (var (name, symbol) in found.Skip(1)) ToolsResults.Children.Add(SymbolButton(symbol, name));
                return;
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                || text.StartsWith("qr ", StringComparison.OrdinalIgnoreCase))
            {
                string payload = text.StartsWith("qr ", StringComparison.OrdinalIgnoreCase) ? text[3..].Trim() : text;
                var image = QrHelper.Make(payload);
                if (image != null)
                {
                    _toolKind = "qr";
                    QrImage.Source = image;
                    QrHost.Visibility = Visibility.Visible;
                    ToolsSide.Visibility = Visibility.Collapsed;
                    CalcResult.Text = "QR code";
                    CalcHint.Text = "Point your phone's camera at it";
                    return;
                }
            }

            string? result = Calculator.Evaluate(text);
            if (result != null)
            {
                _toolKind = "calc";
                _toolValue = result;
                CalcResult.Text = "= " + result;
                CalcHint.Text = "Enter copies the answer";
                return;
            }

            var apps = AppIndex.Find(text);
            if (apps.Count > 0)
            {
                _toolKind = "launch";
                _launchPath = apps[0].Path;
                CalcResult.Text = "Open " + apps[0].Name;
                CalcHint.Text = "Enter opens it";
                foreach (var (name, path) in apps.Skip(1))
                {
                    var b = new Button { Style = (Style)FindResource("ChipButton"), Content = name, ToolTip = "Open " + name };
                    string p = path;
                    b.Click += (_, _) => { AppIndex.Open(p); CalcBox.Clear(); };
                    ToolsResults.Children.Add(b);
                }
                return;
            }

            CalcResult.Text = "=";
            CalcResult.Opacity = 0.4;
            CalcHint.Text = "Not sure what that is – try 12*7, 5 km in miles or an app name";
        }

        private async Task RunTranslateAsync()
        {
            string key = _translateFor;
            int bar = key.LastIndexOf('|');
            if (bar <= 0) return;
            string? result = await TranslateService.TranslateAsync(key[..bar], key[(bar + 1)..]);
            if (key != _translateFor || _toolKind != "translate") return;
            CalcResult.Opacity = 1;
            CalcResult.Text = result ?? "Couldn't translate right now";
            _toolValue = result ?? "";
        }

        private void ToolsInputEnter()
        {
            switch (_toolKind)
            {
                case "calc":
                case "symbol":
                case "translate":
                    if (_toolValue.Length == 0) return;
                    try { Clipboard.SetText(_toolValue); CalcHint.Text = "Copied ✓"; }
                    catch (Exception ex) { App.LogError("Copy failed", ex); }
                    break;
                case "remind":
                    if (ReminderService.TryParse(CalcBox.Text, out var what, out var due))
                    {
                        _svc.Reminders.Add(what, due);
                        CalcBox.Clear();
                        ShowHeaderToast("\uE787", Amber, $"I'll remind you at {due:t}", what, null, 2600);
                    }
                    break;
                case "launch":
                    AppIndex.Open(_launchPath);
                    CalcBox.Clear();
                    EndTyping(collapseIfOutside: false);
                    _hotkeyPinned = false;
                    SetState(NotchState.Collapsed, animate: true);
                    break;
            }
        }

        private static readonly (string Name, string Symbol)[] SymbolList =
        {
            ("heart", "♥"), ("star", "★"), ("check tick", "✓"), ("cross x", "✗"), ("arrow right", "→"), ("arrow left", "←"),
            ("arrow up", "↑"), ("arrow down", "↓"), ("degree", "°"), ("plus minus", "±"), ("times multiply", "×"), ("divide", "÷"),
            ("rupee inr", "₹"), ("euro", "€"), ("pound gbp", "£"), ("yen", "¥"), ("copyright", "©"), ("registered", "®"),
            ("trademark tm", "™"), ("bullet dot", "•"), ("ellipsis dots", "…"), ("dash", "—"), ("quote open", "“"), ("quote close", "”"),
            ("section", "§"), ("infinity", "∞"), ("approx", "≈"), ("not equal", "≠"), ("less equal", "≤"), ("greater equal", "≥"),
            ("square root", "√"), ("pi", "π"), ("micro", "µ"), ("music note", "♪"), ("sun", "☀"), ("cloud", "☁"), ("umbrella", "☂"),
            ("snow", "❄"), ("smile", "☺"), ("peace", "☮"), ("recycle", "♻"), ("warning", "⚠"), ("phone", "☎"), ("mail", "✉"),
            ("scissors", "✂"), ("pencil", "✏"), ("half", "½"), ("quarter", "¼"), ("squared", "²"), ("cubed", "³"),
            ("grin emoji", "😀"), ("laugh emoji", "😂"), ("love emoji", "😍"), ("thumbs up", "👍"), ("fire", "🔥"), ("party", "🎉"),
            ("pray thanks", "🙏"), ("cool", "😎"), ("cry", "😢"), ("rocket", "🚀"), ("eyes", "👀"), ("hundred", "💯"),
        };

        private Button SymbolButton(string symbol, string name)
        {
            var b = new Button
            {
                Style = (Style)FindResource("TileButton"),
                Content = new TextBlock { Text = symbol, FontSize = 14, Foreground = (Brush)FindResource("TextBrush"), FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI Emoji") },
                Width = 30, Height = 28, Margin = new Thickness(0, 0, 4, 4), ToolTip = name
            };
            b.Click += (_, _) =>
            {
                try { Clipboard.SetText(symbol); } catch { /* clipboard busy */ }
                ShowHeaderToast("\uE8C8", Teal2, "Copied " + symbol, "", null, 1300);
            };
            return b;
        }

        private void BuildSymbols()
        {
            SymbolsGrid.Children.Clear();
            var close = new Button { Style = (Style)FindResource("ChipButton"), Content = "Done", Margin = new Thickness(0, 0, 4, 4) };
            close.Click += (_, _) => SymbolsPanel.Visibility = Visibility.Collapsed;
            SymbolsGrid.Children.Add(close);
            foreach (var (name, symbol) in SymbolList) SymbolsGrid.Children.Add(SymbolButton(symbol, name));
        }

        private void Symbols_Click(object sender, RoutedEventArgs e)
            => SymbolsPanel.Visibility = SymbolsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        // =====================================================================
        //  Water
        // =====================================================================

        private void RenderWater()
        {
            int g = _svc.Wellbeing.Glasses, goal = _settings.WaterGoal;
            WaterText.Text = $"💧 {g} / {goal} glasses" + (g >= goal ? "  ✓" : "");
        }

        private void Water_Click(object sender, RoutedEventArgs e) => _svc.Wellbeing.AddGlass();

        private void Water_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _svc.Wellbeing.AddGlass(-1);
        }

        // =====================================================================
        //  Idle buddies (more of them) + the cat waking up for music
        // =====================================================================

        /// <summary>Builds the newer buddies; returns false for the ones the 1.4 code draws.</summary>
        private bool BuildFancyBuddy(string kind)
        {
            Brush fg = (Brush)FindResource("TextBrush");
            switch (kind)
            {
                case "Ghost":
                {
                    var body = new Path
                    {
                        Data = Geometry.Parse("M4,16 L4,7 A6,6 0 0 1 16,7 L16,16 L13.5,14 L11,16 L8.5,14 L6,16 Z"),
                        Fill = fg, Opacity = 0.9
                    };
                    var eyeL = new Ellipse { Width = 2.2, Height = 3, Fill = Brushes.Black };
                    var eyeR = new Ellipse { Width = 2.2, Height = 3, Fill = Brushes.Black };
                    Canvas.SetLeft(eyeL, 7); Canvas.SetTop(eyeL, 6);
                    Canvas.SetLeft(eyeR, 11); Canvas.SetTop(eyeR, 6);
                    var group = new Canvas { Width = 20, Height = 16 };
                    group.Children.Add(body);
                    group.Children.Add(eyeL);
                    group.Children.Add(eyeR);
                    Canvas.SetLeft(group, 3);
                    IdleBuddy.Children.Add(group);
                    group.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(1, -2, TimeSpan.FromSeconds(1.4))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
                    return true;
                }
                case "Orbit":
                {
                    var core = new Ellipse { Width = 6, Height = 6 };
                    core.SetResourceReference(Shape.FillProperty, "AccentBrush");
                    Canvas.SetLeft(core, 10); Canvas.SetTop(core, 5);
                    IdleBuddy.Children.Add(core);
                    for (int i = 0; i < 2; i++)
                    {
                        var ring = new Canvas { Width = 26, Height = 16, RenderTransformOrigin = new Point(0.5, 0.5) };
                        var moon = new Ellipse { Width = i == 0 ? 3.5 : 2.5, Height = i == 0 ? 3.5 : 2.5, Fill = fg, Opacity = 0.9 };
                        Canvas.SetLeft(moon, i == 0 ? 1 : 21);
                        Canvas.SetTop(moon, 6.5);
                        ring.Children.Add(moon);
                        var rotate = new RotateTransform();
                        ring.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 0.45), rotate } };
                        IdleBuddy.Children.Add(ring);
                        rotate.BeginAnimation(RotateTransform.AngleProperty,
                            new DoubleAnimation(i == 0 ? 0 : 360, i == 0 ? 360 : 0, TimeSpan.FromSeconds(i == 0 ? 2.4 : 3.6)) { RepeatBehavior = RepeatBehavior.Forever });
                    }
                    return true;
                }
                case "Flame":
                {
                    var outer = new Path
                    {
                        Data = Geometry.Parse("M10,16 C3,16 3,9 7,5 C7,8 9,8 9,6 C9,3 11,1 12,0 C12,4 17,6 16,11 C16,14 13,16 10,16 Z"),
                        Fill = new LinearGradientBrush(Color.FromRgb(0xFF, 0x5A, 0x1F), Color.FromRgb(0xFF, 0xD1, 0x4A), 90),
                        RenderTransformOrigin = new Point(0.5, 1)
                    };
                    var inner = new Path
                    {
                        Data = Geometry.Parse("M10,16 C7,16 7,12 9,10 C9,12 11,11 11,9 C13,11 14,16 10,16 Z"),
                        Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB0)),
                        RenderTransformOrigin = new Point(0.5, 1)
                    };
                    Canvas.SetLeft(outer, 3); Canvas.SetLeft(inner, 3);
                    IdleBuddy.Children.Add(outer);
                    IdleBuddy.Children.Add(inner);
                    var flicker = new ScaleTransform(1, 1);
                    outer.RenderTransform = flicker;
                    flicker.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1.08, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    flicker.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.04, 0.95, TimeSpan.FromMilliseconds(260)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    inner.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(220)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    return true;
                }
                case "Robot":
                {
                    var head = new Rectangle { Width = 14, Height = 11, RadiusX = 3, RadiusY = 3, Stroke = fg, StrokeThickness = 1.4 };
                    Canvas.SetLeft(head, 5); Canvas.SetTop(head, 4);
                    var antenna = new Line { X1 = 12, Y1 = 4, X2 = 12, Y2 = 1, Stroke = fg, StrokeThickness = 1.2 };
                    var tip = new Ellipse { Width = 3, Height = 3 };
                    tip.SetResourceReference(Shape.FillProperty, "AccentBrush");
                    Canvas.SetLeft(tip, 10.5); Canvas.SetTop(tip, -1);
                    IdleBuddy.Children.Add(head);
                    IdleBuddy.Children.Add(antenna);
                    IdleBuddy.Children.Add(tip);
                    tip.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    foreach (double x in new[] { 8.5, 13.5 })
                    {
                        var eye = new Ellipse { Width = 2.4, Height = 2.4, RenderTransformOrigin = new Point(0.5, 0.5) };
                        eye.SetResourceReference(Shape.FillProperty, "AccentBrush");
                        Canvas.SetLeft(eye, x); Canvas.SetTop(eye, 8);
                        var blink = new ScaleTransform(1, 1);
                        eye.RenderTransform = blink;
                        IdleBuddy.Children.Add(eye);
                        var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.2), RepeatBehavior = RepeatBehavior.Forever };
                        k.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                        k.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.92)));
                        k.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromPercent(0.95)));
                        k.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
                        blink.BeginAnimation(ScaleTransform.ScaleYProperty, k);
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>Music started: the buddy hops a few times before it makes room for the bars.</summary>
        private bool TryWakeBuddy()
        {
            if (_buddyWaking || IdleBuddy.Visibility != Visibility.Visible || _buddyKind.Length == 0 || !Smooth) return false;
            _buddyWaking = true;
            var hop = new TranslateTransform();
            IdleBuddy.RenderTransform = hop;
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1200) };
            for (int i = 0; i < 3; i++)
            {
                k.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(i / 3.0 + 0.08), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                k.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(i / 3.0 + 0.3), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            }
            k.Completed += (_, _) =>
            {
                _buddyWaking = false;
                IdleBuddy.RenderTransform = Transform.Identity;
                UpdateIdleBuddy(resize: true);
            };
            hop.BeginAnimation(TranslateTransform.YProperty, k);
            return true;
        }
    }

    /// <summary>QR codes for the Tools page (QRCoder, offline).</summary>
    internal static class QrHelper
    {
        public static BitmapSource? Make(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 1500) return null;
            try
            {
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
                byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                App.LogError("QR code failed", ex);
                return null;
            }
        }
    }
}
