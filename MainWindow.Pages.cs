using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Crest.Services;

namespace Crest
{
    /// <summary>Expanded view pages: Music, Lyrics, Calendar, Weather, System, Timer, Shelf, Notes.</summary>
    public partial class MainWindow
    {
        private sealed record PageInfo(string Name, Button Tab, FrameworkElement Page, Func<bool> Enabled);

        private readonly List<PageInfo> _pages = new();
        private string _currentPage = "Music";
        private bool _statsInUse;

        // lyrics
        private DispatcherTimer? _lyricsTimer;
        private CancellationTokenSource? _lyricsCts;
        private LyricsResult? _lyrics;
        private string _lyricsKey = "";
        private bool _lyricsLoading;
        private int _lyricIndex = -2;

        // notes / clipboard
        private bool _syncingNotes;
        private DispatcherTimer? _copiedTimer;

        // shelf drag-out
        private Point _shelfDragStart;
        private string? _shelfDragPath;

        private void InitPages()
        {
            _pages.Add(new PageInfo("Music", TabMusic, PageMusic, () => true));
            _pages.Add(new PageInfo("Lyrics", TabLyrics, PageLyrics, () => _settings.PageLyrics && _settings.LyricsEnabled));
            _pages.Add(new PageInfo("Calendar", TabCalendar, PageCalendar, () => _settings.PageCalendar));
            _pages.Add(new PageInfo("Weather", TabWeather, PageWeather, () => _settings.PageWeather));
            _pages.Add(new PageInfo("System", TabSystem, PageSystem, () => _settings.PageSystem));
            _pages.Add(new PageInfo("Timer", TabTimer, PageTimer, () => _settings.PageTimer));
            _pages.Add(new PageInfo("Shelf", TabShelf, PageShelf, () => _settings.PageShelf));
            _pages.Add(new PageInfo("Notes", TabNotes, PageNotes, () => _settings.PageNotes));
            _pages.Add(new PageInfo("Sound", TabSound, PageSound, () => _settings.PageSound));
            _pages.Add(new PageInfo("Controls", TabControls, PageControls, () => _settings.PageControls));
            _pages.Add(new PageInfo("Tools", TabTools, PageTools, () => _settings.PageTools));
            _pages.Add(new PageInfo("Recent", TabRecent, PageRecent, () => _settings.PageRecent));

            _lyricsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
            _lyricsTimer.Tick += (_, _) => UpdateLyricLine(force: false);

            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); RenderClipboard(); };

            EventHandler calendarUpdated = (_, _) => { if (_currentPage == "Calendar") RenderCalendar(); };
            EventHandler weatherUpdated = (_, _) => { if (_currentPage == "Weather") RenderWeather(); };
            EventHandler statsUpdated = (_, _) => { if (_currentPage == "System" && _state == NotchState.Expanded) RenderSystem(); };
            EventHandler timerChanged = (_, _) => { RenderTimer(); UpdateBubble(animate: true); };
            EventHandler shelfChanged = (_, _) => RenderShelf();
            EventHandler clipboardChanged = (_, _) => RenderClipboard();
            Action<object?> notesChanged = source =>
            {
                if (ReferenceEquals(source, this)) return;
                _syncingNotes = true;
                NotesBox.Text = _svc.Notes.Text;
                _syncingNotes = false;
                NotesPlaceholder.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            Subscribe(() => _svc.Calendar.Updated += calendarUpdated, () => _svc.Calendar.Updated -= calendarUpdated);
            Subscribe(() => _svc.Weather.Updated += weatherUpdated, () => _svc.Weather.Updated -= weatherUpdated);
            Subscribe(() => _svc.Stats.Updated += statsUpdated, () => _svc.Stats.Updated -= statsUpdated);
            Subscribe(() => _svc.Timer.Changed += timerChanged, () => _svc.Timer.Changed -= timerChanged);
            Subscribe(() => _svc.Shelf.Changed += shelfChanged, () => _svc.Shelf.Changed -= shelfChanged);
            Subscribe(() => _svc.Clipboard.Changed += clipboardChanged, () => _svc.Clipboard.Changed -= clipboardChanged);
            Subscribe(() => _svc.Notes.Changed += notesChanged, () => _svc.Notes.Changed -= notesChanged);

            _syncingNotes = true;
            NotesBox.Text = _svc.Notes.Text;
            _syncingNotes = false;
            NotesPlaceholder.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateTabs();
            RenderTimer();
            RenderShelf();
            RenderClipboard();
        }

        private void ApplyPagesSettings()
        {
            var s = _settings;
            _svc.Weather.Configure(s.WeatherCity, s.WeatherFahrenheit);
            _svc.Calendar.Configure(s.CalendarUrl, s.CalendarReminderMinutes);
            _svc.Clipboard.SetEnabled(s.ClipboardHistory);
            ClipboardPanel.Visibility = s.ClipboardHistory && s.ExpandedWidth >= 380 ? Visibility.Visible : Visibility.Collapsed;

            if (!s.LyricsEnabled) ClearLyrics();
            else if (_lyricsKey.Length == 0) RequestLyrics();

            var current = _pages.FirstOrDefault(p => p.Name == _currentPage);
            if (current == null || !current.Enabled()) SelectPage("Music", 0);
            UpdateTabs();
            RenderCurrentPage();
            UpdateLyricsTimer();
        }

        private void ClosePages()
        {
            _lyricsTimer?.Stop();
            _copiedTimer?.Stop();
            _lyricsCts?.Cancel();
            SetStatsInUse(false);
        }

        // =====================================================================
        //  Tabs
        // =====================================================================

        private void UpdateTabs()
        {
            int visible = 0;
            foreach (var p in _pages)
            {
                bool enabled = p.Enabled();
                p.Tab.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
                bool selected = p.Name == _currentPage;
                p.Tab.Opacity = selected ? 1.0 : 0.6;
                // The selected tab gets a soft tint of its own color.
                p.Tab.Background = selected && p.Tab.Foreground is SolidColorBrush fg
                    ? new SolidColorBrush(Color.FromArgb(0x38, fg.Color.R, fg.Color.G, fg.Color.B))
                    : Brushes.Transparent;
                if (enabled) visible++;
            }
            // Only the Music page → no tab strip at all.
            TabStrip.Visibility = visible > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string name }) return;
            int from = _pages.FindIndex(p => p.Name == _currentPage);
            int to = _pages.FindIndex(p => p.Name == name);
            SelectPage(name, Math.Sign(to - from));
        }

        /// <summary>Moves to the next / previous enabled page (horizontal scroll in the expanded view).</summary>
        private void SwitchPage(int delta)
        {
            var enabled = _pages.Where(p => p.Enabled()).ToList();
            int i = enabled.FindIndex(p => p.Name == _currentPage);
            int next = Math.Clamp(i + delta, 0, enabled.Count - 1);
            if (next != i && next >= 0) SelectPage(enabled[next].Name, Math.Sign(delta));
        }

        private void SelectPage(string name, int direction)
        {
            if (name == _currentPage && direction != 0) return;
            if (_state != NotchState.Expanded) _pagePickedBeforeOpen = true;   // e.g. clicking a pop-up opens its page
            if (_currentPage == "Weather" && name != "Weather") WeatherFx.Children.Clear();
            _currentPage = name;

            foreach (var p in _pages)
                p.Page.Visibility = p.Name == name ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabs();

            if (direction != 0 && _settings.AnimationDurationMs > 0)
            {
                var d = new Duration(TimeSpan.FromMilliseconds(260));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                PagesShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * direction, 0, d) { EasingFunction = ease });
                PagesHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            }

            SetStatsInUse(name == "System" && _state == NotchState.Expanded);
            if (name != "Notes" && name != "Tools" && _typing) EndTyping(collapseIfOutside: false);
            UpdateCoverBackdrop(animate: true);
            RenderCurrentPage();
            UpdateLyricsTimer();
            UpdateProgress();
            UpdateSoundRefresh();
            UpdatePageTint(animate: true);
            SetTopAppsInUse(_showTopApps && name == "System" && _state == NotchState.Expanded);
        }

        private void OnExpandedOpened()
        {
            ApplyStartPage();
            if (!_pages.First(p => p.Name == _currentPage).Enabled()) SelectPage("Music", 0);
            SetStatsInUse(_currentPage == "System");
            if (_currentPage == "Shelf") _svc.Shelf.Prune();
            RenderCurrentPage();
            UpdateLyricsTimer();
        }

        private void OnExpandedClosed()
        {
            SetStatsInUse(false);
            if (_typing) EndTyping(collapseIfOutside: false);
            UpdateLyricsTimer();
        }

        private void SetStatsInUse(bool inUse)
        {
            if (inUse == _statsInUse) return;
            _statsInUse = inUse;
            if (inUse) { _svc.Stats.AddUser(); _svc.Network.AddUser(); }
            else { _svc.Stats.RemoveUser(); _svc.Network.RemoveUser(); }
        }

        private void RenderCurrentPage()
        {
            switch (_currentPage)
            {
                case "Lyrics": RenderLyrics(); break;
                case "Calendar": RenderCalendar(); break;
                case "Weather": RenderWeather(); break;
                case "System": RenderSystem(); break;
                case "Timer": RenderTimer(); break;
                case "Shelf": RenderShelf(); break;
                case "Notes": RenderClipboard(); break;
                case "Sound": RenderSound(); break;
                case "Tools": RenderTools(); break;
                case "Recent": RenderRecent(); break;
                case "Controls":
                    RenderControls();
                    _ = _svc.Toggles.RefreshRadiosAsync();
                    break;
            }
        }

        /// <summary>A TextBlock that follows the notch's theme resources.</summary>
        private TextBlock MakeText(string text, bool subtle = false, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource(subtle ? "SubText" : "NotchText"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (bold) tb.FontWeight = FontWeights.SemiBold;
            return tb;
        }

        // =====================================================================
        //  Lyrics
        // =====================================================================

        /// <summary>The current lyric line replaces the time in the collapsed notch.</summary>
        private bool ShowingCollapsedLyric
            => _settings.LyricsEnabled && _settings.LyricsInCollapsed && _showPlaying && _lyrics?.Synced == true;

        private void UpdateCollapsedContent()
        {
            bool lyric = ShowingCollapsedLyric;
            CollapsedLyric.Visibility = lyric ? Visibility.Visible : Visibility.Collapsed;
            CollapsedTime.Visibility = lyric ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnMediaUpdatedForLyrics(bool hasMedia)
        {
            if (!hasMedia)
            {
                if (_lyricsKey.Length > 0) ClearLyrics();
            }
            else if (_settings.LyricsEnabled && Media.Title + "|" + Media.Artist != _lyricsKey)
            {
                RequestLyrics();
            }
            UpdateLyricsTimer();
        }

        private void RequestLyrics()
        {
            if (!_settings.LyricsEnabled) { ClearLyrics(); return; }

            string key = Media.Title + "|" + Media.Artist;
            if (key == _lyricsKey) return;

            _lyricsCts?.Cancel();
            _lyricsKey = key;
            _lyrics = null;
            _lyricIndex = -2;
            LoadLyricsOffset();

            if (string.IsNullOrWhiteSpace(Media.Title))
            {
                _lyricsLoading = false;
                RenderLyrics();
                return;
            }

            var cts = new CancellationTokenSource();
            _lyricsCts = cts;
            _lyricsLoading = true;
            RenderLyrics();
            _ = LoadLyricsAsync(key, Media.Title, Media.Artist, Media.Album, Media.Duration, cts.Token);
        }

        private async Task LoadLyricsAsync(string key, string title, string artist, string album, TimeSpan duration, CancellationToken ct)
        {
            LyricsResult? result;
            try
            {
                result = await _svc.Lyrics.GetAsync(title, artist, album, duration, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (ct.IsCancellationRequested || key != _lyricsKey) return;

            _lyrics = result;
            _lyricsLoading = false;
            _lyricIndex = -2;
            RenderLyrics();
            UpdateCollapsedContent();
            ResizeCollapsed(animate: true);
            UpdateLyricsTimer();
        }

        private void ClearLyrics()
        {
            _lyricsCts?.Cancel();
            _lyricsKey = "";
            _lyrics = null;
            _lyricsLoading = false;
            _lyricIndex = -2;
            CollapsedLyric.Text = "";
            UpdateCollapsedContent();
            RenderLyrics();
            UpdateLyricsTimer();
        }

        private void UpdateLyricsTimer()
        {
            if (_lyricsTimer == null) return;
            bool needed = Media.IsPlaying && _lyrics?.Synced == true && IsEffectivelyVisible &&
                          ((_state == NotchState.Expanded && _currentPage == "Lyrics") || ShowingCollapsedLyric);
            if (needed && !_lyricsTimer.IsEnabled) { UpdateLyricLine(force: true); _lyricsTimer.Start(); }
            else if (!needed && _lyricsTimer.IsEnabled) _lyricsTimer.Stop();
        }

        private void RenderLyrics()
        {
            SyncedLyricsPanel.Visibility = Visibility.Collapsed;
            PlainLyricsScroller.Visibility = Visibility.Collapsed;
            LyricsStatus.Visibility = Visibility.Visible;
            UpdateLyricsFooter();

            if (!_settings.LyricsEnabled) { LyricsStatus.Text = "Lyrics are turned off in Settings → Media."; return; }
            if (string.IsNullOrWhiteSpace(Media.Title)) { LyricsStatus.Text = "Play a song to see its lyrics."; return; }
            if (_lyricsLoading) { LyricsStatus.Text = "Looking for lyrics…"; return; }
            if (_lyrics == null) { LyricsStatus.Text = "No lyrics found for this song."; return; }
            if (_lyrics.Instrumental && !_lyrics.Synced) { LyricsStatus.Text = "♪  Instrumental"; return; }

            LyricsStatus.Visibility = Visibility.Collapsed;
            if (_lyrics.Synced)
            {
                SyncedLyricsPanel.Visibility = Visibility.Visible;
                UpdateLyricLine(force: true);
            }
            else
            {
                PlainLyricsScroller.Visibility = Visibility.Visible;
                PlainLyricsText.Text = _lyrics.Plain;
                PlainLyricsScroller.ScrollToTop();
            }
        }

        private void UpdateLyricLine(bool force)
        {
            if (_lyrics?.Synced != true) return;

            // A small lead so the line appears as it's being sung, not after.
            int idx = _lyrics.IndexAt(Media.Position + TimeSpan.FromMilliseconds(250 + _lyricsOffsetMs));
            if (idx == _lyricIndex && !force) return;
            bool changed = idx != _lyricIndex;
            _lyricIndex = idx;

            var lines = _lyrics.Lines;
            string Line(int i) => i >= 0 && i < lines.Count ? (string.IsNullOrWhiteSpace(lines[i].Text) ? "♪" : lines[i].Text) : "";

            string current = idx < 0 ? "♪" : Line(idx);
            LyricPrev.Text = Line(idx - 1);
            LyricCurrent.Text = current;
            LyricNext.Text = idx < 0 ? Line(0) : Line(idx + 1);
            CollapsedLyric.Text = current;

            if (changed && _settings.AnimationDurationMs > 0)
            {
                var d = new Duration(TimeSpan.FromMilliseconds(220));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                LyricShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, d) { EasingFunction = ease });
                LyricCurrent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, d));
                if (Smooth)
                {
                    // The whole block glides up together: previous line fades out, next fades in.
                    var slow = new Duration(TimeSpan.FromMilliseconds(320));
                    LyricPrevShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, slow) { EasingFunction = ease });
                    LyricPrev.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.75, slow));
                    LyricNextShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, slow) { EasingFunction = ease });
                    LyricNext.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, slow));
                }
            }
        }

        // =====================================================================
        //  Calendar
        // =====================================================================

        private void RenderCalendar()
        {
            CalendarList.Children.Clear();
            var events = _svc.Calendar.Upcoming;
            if (events.Count == 0)
            {
                CalendarStatus.Visibility = Visibility.Visible;
                CalendarStatus.Text = string.IsNullOrEmpty(_svc.Calendar.Status) ? "Nothing coming up." : _svc.Calendar.Status;
                return;
            }
            CalendarStatus.Visibility = Visibility.Collapsed;
            foreach (var ev in events.Take(3))
                CalendarList.Children.Add(BuildEventRow(ev));
        }

        private UIElement BuildEventRow(CalendarEvent ev)
        {
            var now = DateTime.Now;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 1, 9, 1) };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            row.Children.Add(bar);

            string day = ev.Start.Date == now.Date ? "Today" : ev.Start.Date == now.Date.AddDays(1) ? "Tomorrow" : ev.Start.ToString("ddd");
            var when = new StackPanel();
            when.Children.Add(MakeText(ev.Start.ToString("t"), bold: true));
            when.Children.Add(MakeText(day, subtle: true));
            Grid.SetColumn(when, 1);
            row.Children.Add(when);

            var what = new StackPanel { Margin = new Thickness(4, 0, 6, 0) };
            what.Children.Add(MakeText(ev.Title, bold: true));
            what.Children.Add(MakeText(string.IsNullOrWhiteSpace(ev.Location) ? $"{ev.Start:t} – {ev.End:t}" : ev.Location, subtle: true));
            Grid.SetColumn(what, 2);
            row.Children.Add(what);

            if (TryAddJoinChip(row, ev)) return row;

            string rel = ev.Start <= now ? "now" : (ev.Start - now).TotalMinutes < 60 ? $"in {Math.Ceiling((ev.Start - now).TotalMinutes)} min" : "";
            if (rel.Length > 0)
            {
                var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
                chip.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                chip.Child = MakeText(rel, subtle: false, bold: true);
                ((TextBlock)chip.Child).FontSize = Math.Max(8, _settings.FontSize - 3);
                Grid.SetColumn(chip, 3);
                row.Children.Add(chip);
            }
            return row;
        }

        // =====================================================================
        //  Weather
        // =====================================================================

        private void RenderWeather()
        {
            var w = _svc.Weather.Current;
            if (w == null)
            {
                WeatherContent.Visibility = Visibility.Collapsed;
                WeatherStatus.Visibility = Visibility.Visible;
                WeatherStatus.Text = string.IsNullOrEmpty(_svc.Weather.Status) ? "Loading weather…" : _svc.Weather.Status;
                return;
            }

            WeatherStatus.Visibility = Visibility.Collapsed;
            WeatherContent.Visibility = Visibility.Visible;

            var (symbol, text) = WeatherService.Describe(w.Code, w.IsDay);
            WeatherIcon.Text = symbol;
            WeatherTemp.Text = $"{Math.Round(w.Temp)}°";
            WeatherDesc.Text = $"{text} · H {Math.Round(w.High)}°  L {Math.Round(w.Low)}°";
            WeatherCityText.Text = w.City;

            WeatherHours.Children.Clear();
            WeatherHours.Visibility = _settings.ExpandedWidth >= 380 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var h in w.Hours.Take(4))
            {
                var col = new StackPanel { Width = 38, Margin = new Thickness(2, 0, 2, 0) };
                var hour = MakeText(h.Time.ToString("HH"), subtle: true);
                hour.HorizontalAlignment = HorizontalAlignment.Center;
                var icon = MakeText(WeatherService.Describe(h.Code, h.IsDay).Symbol);
                icon.FontFamily = new FontFamily("Segoe UI Symbol");
                icon.FontSize = 15;
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var temp = MakeText($"{Math.Round(h.Temp)}°", bold: true);
                temp.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(hour);
                col.Children.Add(icon);
                col.Children.Add(temp);
                WeatherHours.Children.Add(col);
            }
            DecorateWeather();
        }

        // =====================================================================
        //  System
        // =====================================================================

        private void RenderSystem()
        {
            var st = _svc.Stats;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            Animate(CpuScale, ScaleTransform.ScaleXProperty, st.Cpu / 100, 400, ease);
            CpuText.Text = $"{st.Cpu:0}%";

            Animate(RamScale, ScaleTransform.ScaleXProperty, st.Ram / 100, 400, ease);
            RamText.Text = st.RamTotalGb > 0 ? $"{st.RamUsedGb:0.0} / {st.RamTotalGb:0} GB" : "—";

            Animate(GpuScale, ScaleTransform.ScaleXProperty, (st.Gpu ?? 0) / 100, 400, ease);
            GpuText.Text = st.Gpu is double g ? $"{g:0}%" : "n/a";

            Animate(DiskScale, ScaleTransform.ScaleXProperty, st.Disk / 100, 400, ease);
            DiskText.Text = $"{st.DiskFreeGb:0} GB free";

            _svc.Power.Read();
            var up = st.Uptime;
            string uptime = up.TotalDays >= 1 ? $"Up {(int)up.TotalDays}d {up.Hours}h" : $"Up {up.Hours}h {up.Minutes}m";
            string battery = _svc.Power.HasBattery
                ? $"Battery {_svc.Power.Percent}%{(_svc.Power.OnAc ? " · charging" : "")}  ·  "
                : "";
            string net = $"↓ {NetworkSpeedService.Format(_svc.Network.Down)}  ↑ {NetworkSpeedService.Format(_svc.Network.Up)}  ·  ";
            SystemFooter.Text = net + battery + uptime;
            string devices = DeviceBatteriesText();
            DevicesFooter.Text = devices;
            DevicesFooter.Visibility = devices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            DecorateSystem();
            if (_showTopApps) RenderTopApps();
        }

        // =====================================================================
        //  Timer
        // =====================================================================

        private void RenderTimer()
        {
            var t = _svc.Timer;
            TimerBig.Text = TimerService.Format(t.Value);
            SwapGlyph(TimerToggleBtn, t.Running ? "\uE769" : "\uE768");
            bool timerMode = t.Mode == TimerMode.Timer;
            bool focus = t.Mode == TimerMode.Focus;
            ModeTimerBtn.Opacity = timerMode ? 1 : 0.45;
            ModeStopwatchBtn.Opacity = t.Mode == TimerMode.Stopwatch ? 1 : 0.45;
            ModeFocusBtn.Opacity = focus ? 1 : 0.45;
            TimerPresets.Visibility = timerMode ? Visibility.Visible : Visibility.Collapsed;
            TimerPlusBtn.Visibility = timerMode || focus ? Visibility.Visible : Visibility.Collapsed;
            TimerSub.Visibility = focus ? Visibility.Visible : Visibility.Collapsed;
            if (focus)
            {
                TimerSub.Text = t.IsBreak
                    ? $"Break · then round {t.Round + 1}"
                    : $"Focus · round {t.Round} · {(int)t.FocusBreak.TotalMinutes} min break next";
                TimerSub.Foreground = new SolidColorBrush(t.IsBreak ? Green : Tomato);
            }

            RenderTimerExtras();
            bool stats = focus && _settings.FocusStats;
            FocusStatsText.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
            if (stats)
            {
                var fs = _svc.FocusStats;
                FocusStatsText.Text = $"Today {FocusStatsService.FormatMinutes(fs.Today)} · 7 days {FocusStatsService.FormatMinutes(fs.Week)}"
                                      + (fs.Streak > 1 ? $" · {fs.Streak}-day streak" : "");
            }
        }

        private void TimerMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } && Enum.TryParse(tag, out TimerMode mode)) _svc.Timer.SetMode(mode);
        }

        private void TimerPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int minutes)) return;
            var t = _svc.Timer;
            // A timer is already running: start another one next to it instead of replacing it.
            if (t.Mode == TimerMode.Timer && t.Running) t.AddExtra(TimeSpan.FromMinutes(minutes));
            else t.StartTimer(TimeSpan.FromMinutes(minutes));
        }

        private void TimerPlus_Click(object sender, RoutedEventArgs e) => _svc.Timer.AddMinute();
        private void TimerReset_Click(object sender, RoutedEventArgs e) => _svc.Timer.Reset();
        private void TimerToggle_Click(object sender, RoutedEventArgs e) => _svc.Timer.Toggle();

        // =====================================================================
        //  Shelf
        // =====================================================================

        private void RenderShelf()
        {
            ShelfPanel.Children.Clear();
            var files = _svc.Shelf.Files;
            foreach (var path in files) ShelfPanel.Children.Add(BuildShelfTile(path));

            bool empty = files.Count == 0;
            ShelfEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ShelfDropHint.Visibility = empty || _dragActive ? Visibility.Visible : Visibility.Collapsed;
            ShelfClearBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            ShelfCount.Text = empty ? "" : files.Count == 1 ? "1 item" : $"{files.Count} items";
        }

        private UIElement BuildShelfTile(string path)
        {
            var tile = new Border
            {
                Width = 70,
                Height = 74,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = path
            };

            var grid = new Grid();
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var thumb = ShelfThumbnail(path);
            var icon = thumb == null ? FileIcon(path) : null;
            if (thumb != null)
                stack.Children.Add(new Border
                {
                    Width = 58,
                    Height = 38,
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(0, 2, 0, 5),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill }
                });
            else if (icon != null)
                stack.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 0, 5) });
            else
            {
                var glyph = MakeText("\uE8B7");
                glyph.FontFamily = (FontFamily)FindResource("IconFont");
                glyph.FontSize = 26;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.Margin = new Thickness(0, 0, 0, 5);
                stack.Children.Add(glyph);
            }

            var name = MakeText(ShelfName(path), subtle: true);
            name.TextAlignment = TextAlignment.Center;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MaxWidth = 64;
            stack.Children.Add(name);
            grid.Children.Add(stack);

            var remove = new Button
            {
                Content = "\uE711",
                Style = (Style)FindResource("MediaButton"),
                Width = 18,
                Height = 18,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "Remove from shelf"
            };
            remove.Click += (_, _) => _svc.Shelf.Remove(path);
            grid.Children.Add(remove);

            // Favourites: right-click pins / unpins. Pinned items stay, and open with one click.
            bool pinned = _svc.Shelf.IsPinned(path);
            if (pinned)
            {
                var pin = MakeText("\uE718");
                pin.FontFamily = (FontFamily)FindResource("IconFont");
                pin.FontSize = 9;
                pin.Foreground = new SolidColorBrush(Amber);
                pin.HorizontalAlignment = HorizontalAlignment.Left;
                pin.VerticalAlignment = VerticalAlignment.Top;
                pin.Margin = new Thickness(5, 4, 0, 0);
                grid.Children.Add(pin);
            }
            tile.ToolTip = path + (pinned ? "\nPinned – click to open · right-click to unpin" : "\nDouble-click to open · right-click to pin");
            tile.MouseRightButtonUp += (_, e) => { e.Handled = true; _svc.Shelf.TogglePin(path); };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                if (!pinned || _shelfDragPath != path) return;
                _shelfDragPath = null;
                e.Handled = true;
                OpenPath(path);
            };
            tile.Child = grid;

            tile.MouseEnter += (_, _) => { tile.SetResourceReference(Border.BackgroundProperty, "HoverBrush"); remove.Visibility = Visibility.Visible; };
            tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; remove.Visibility = Visibility.Collapsed; };
            tile.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenPath(path);
                    e.Handled = true;
                    return;
                }
                _shelfDragStart = e.GetPosition(this);
                _shelfDragPath = path;
            };
            tile.MouseMove += (_, e) =>
            {
                if (_shelfDragPath != path || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _shelfDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _shelfDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _shelfDragPath = null;
                DragOutOfShelf(tile, path);
            };
            return tile;
        }

        private static readonly Dictionary<string, ImageSource?> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A small preview for pictures (screenshots…), null for other files.</summary>
        private static ImageSource? ShelfThumbnail(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")) return null;
            if (ThumbCache.TryGetValue(path, out var cached)) return cached;
            ImageSource? result = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 140;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
            catch
            {
                // not a readable picture – the file icon is used instead
            }
            if (ThumbCache.Count > 60) ThumbCache.Clear();
            ThumbCache[path] = result;
            return result;
        }

        /// <summary>"Screenshot 2026-11-11 154733.png" → "Screenshot · 3:47 PM"; other names unchanged.</summary>
        private static string ShelfName(string path)
        {
            string file = Path.GetFileName(path.TrimEnd('\\'));
            if (file.Length == 0) return path;   // a drive root
            if (file.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase) && ShelfThumbnail(path) != null)
            {
                try
                {
                    var t = File.GetLastWriteTime(path);
                    return "Screenshot · " + (t.Date == DateTime.Today ? t.ToString("t") : t.ToString("d MMM"));
                }
                catch
                {
                    return "Screenshot";
                }
            }
            return file;
        }

        private void DragOutOfShelf(DependencyObject source, string path)
        {
            _dragOut = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                App.LogError("Dragging from the shelf failed", ex);
            }
            finally
            {
                _dragOut = false;
            }
            _svc.Shelf.Prune();   // the file was moved somewhere else
            if (!Notch.IsMouseOver && !KeepOpen) _collapseTimer.Start();
        }

        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError("Opening shelf item failed", ex); }
        }

        private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

        private static ImageSource? FileIcon(string path)
        {
            if (Directory.Exists(path)) return null;   // folder glyph instead
            string key = Path.GetExtension(path);
            if (key is ".exe" or ".lnk" or ".ico") key = path;   // these have their own icons
            if (IconCache.TryGetValue(key, out var cached)) return cached;

            ImageSource? source = null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    source = bmp;
                }
            }
            catch
            {
                // no icon available
            }
            IconCache[key] = source;
            return source;
        }

        private void ShelfClear_Click(object sender, RoutedEventArgs e) => _svc.Shelf.Clear();

        private void ShelfScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Vertical wheel scrolls the shelf sideways.
            ShelfScroller.ScrollToHorizontalOffset(ShelfScroller.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        }

        // =====================================================================
        //  Notes + clipboard
        // =====================================================================

        private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            NotesPlaceholder.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_syncingNotes) return;
            _svc.Notes.SetText(NotesBox.Text, this);
        }

        private void NotesBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginTyping();

        private void NotesBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndTyping(collapseIfOutside: true);
            }
        }

        private void RenderClipboard()
        {
            ClipboardList.Children.Clear();
            var clip = _svc.Clipboard;

            // Pinned texts first, then the newest copies that aren't pinned.
            var rows = new List<(ClipItem Item, bool Pinned)>();
            foreach (var text in clip.Pinned) rows.Add((new ClipItem { Text = text }, true));
            foreach (var item in clip.Items)
            {
                if (rows.Count >= Math.Max(4, clip.Pinned.Count + 2)) break;
                if (item.IsImage || item.Text == null) continue;   // images belong on the shelf, not here
                if (clip.IsPinned(item.Text)) continue;
                rows.Add((item, false));
            }

            if (rows.Count == 0)
            {
                ClipboardList.Children.Add(MakeText("Copy some text and it shows up here.", subtle: true));
                ((TextBlock)ClipboardList.Children[0]).TextWrapping = TextWrapping.Wrap;
                return;
            }

            foreach (var (item, isPinned) in rows.Take(5))
            {
                FrameworkElement content;
                TextBlock? label = null;
                if (item.IsImage)
                {
                    content = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Height = 30,
                        Width = 52,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = new ImageBrush(item.Thumbnail) { Stretch = Stretch.UniformToFill }
                    };
                }
                else
                {
                    string oneLine = (item.Text ?? "").Replace("\r", " ").Replace("\n", " ");
                    label = MakeText(oneLine);
                    label.FontSize = Math.Max(8, _settings.FontSize - 2);
                    if (isPinned)
                    {
                        var pin = MakeText("\uE718");
                        pin.FontFamily = (FontFamily)FindResource("IconFont");
                        pin.FontSize = 9;
                        pin.Foreground = new SolidColorBrush(Amber);
                        pin.VerticalAlignment = VerticalAlignment.Center;
                        pin.Margin = new Thickness(0, 0, 4, 0);
                        var line = new DockPanel();
                        DockPanel.SetDock(pin, Dock.Left);
                        line.Children.Add(pin);
                        line.Children.Add(label);
                        content = line;
                    }
                    else
                    {
                        content = label;
                    }
                }

                var button = new Button
                {
                    Style = (Style)FindResource("TextButton"),
                    Content = content,
                    Margin = new Thickness(2, 1, 0, 3),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    ToolTip = item.IsImage ? "Copy this image again"
                            : isPinned ? "Click: copy · Right-click: unpin"
                            : "Click: copy again · Right-click: pin (keeps it here)"
                };
                var value = item;
                button.Click += (_, _) =>
                {
                    _svc.Clipboard.Copy(value);
                    if (label != null) label.Text = "Copied ✓";
                    else button.Opacity = 0.5;
                    _copiedTimer?.Stop();
                    _copiedTimer?.Start();
                };
                if (value.Text != null)
                {
                    string text = value.Text;
                    button.MouseRightButtonUp += (_, e) => { e.Handled = true; _svc.Clipboard.TogglePin(text); };
                }
                ClipboardList.Children.Add(button);
            }
        }
    }
}
