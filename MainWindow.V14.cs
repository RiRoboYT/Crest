using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Crest.Helpers;
using Crest.Services;

namespace Crest
{
    /// <summary>
    /// v1.4 additions to the notch:
    ///  • header toast (volume / pop-ups while the notch is open) and a volume percentage
    ///  • blurred cover backdrop, progress line, idle buddy, night dimming
    ///  • downloads, rain and device-battery pop-ups, meeting Join button
    ///  • to-do list, Tools page (calculator, color picker, countdown), pins, focus stats
    ///  • keyboard control after Win + Alt + N
    /// </summary>
    public partial class MainWindow
    {
        private const double IdleBuddyWidth = 34;   // 26 + 8 gap
        private static readonly List<Color> RecentColors = new();   // shared by every notch window
        private static event Action? RecentColorsChanged;

        private DispatcherTimer? _toastTimer;
        private string? _activityOpensUrl;        // clicking the pop-up opens this link (meeting "Join")
        private string _notesMode = "Note";
        private string _buddyKind = "";
        private double _nightTarget = 1;
        private bool _keyboardMode;
        private IntPtr _previousForeground;
        private ColorPickerWindow? _picker;
        private bool _downloadInBubble;

        private void InitV14()
        {
            _toastTimer = new DispatcherTimer(DispatcherPriority.Background);
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideHeaderToast(animate: true); };

            EventHandler downloads = (_, _) => UpdateBubble(animate: true);
            Action<string> downloadDone = OnDownloadFinished;
            Action<int> rain = OnRainSoon;
            Action<string, int> deviceLow = OnDeviceBatteryLow;
            EventHandler batteries = (_, _) => { if (_currentPage == "System" && _state == NotchState.Expanded) RenderSystem(); };
            EventHandler todo = (_, _) => RenderTodo();
            EventHandler stats = (_, _) => { if (_currentPage == "Timer") RenderTimer(); };
            Action colors = RenderColorHistory;

            Subscribe(() => _svc.Downloads.Changed += downloads, () => _svc.Downloads.Changed -= downloads);
            Subscribe(() => _svc.Downloads.Finished += downloadDone, () => _svc.Downloads.Finished -= downloadDone);
            Subscribe(() => _svc.Weather.RainSoon += rain, () => _svc.Weather.RainSoon -= rain);
            Subscribe(() => _svc.Bluetooth.DeviceBatteryLow += deviceLow, () => _svc.Bluetooth.DeviceBatteryLow -= deviceLow);
            Subscribe(() => _svc.Bluetooth.BatteriesChanged += batteries, () => _svc.Bluetooth.BatteriesChanged -= batteries);
            Subscribe(() => _svc.Todo.Changed += todo, () => _svc.Todo.Changed -= todo);
            Subscribe(() => _svc.FocusStats.Changed += stats, () => _svc.FocusStats.Changed -= stats);
            Subscribe(() => RecentColorsChanged += colors, () => RecentColorsChanged -= colors);

            PreviewKeyDown += OnNotchKeyDown;
            RenderTodo();
            RenderColorHistory();
        }

        private void ApplyV14Settings()
        {
            UpdateIdleBuddy(resize: false);
            UpdateNightDim(animate: false);
            UpdateCoverBackdrop(animate: false);
            UpdateProgressLine();
            UpdateVolumePercent();
            if (_currentPage == "Tools") RenderTools();
        }

        private void CloseV14()
        {
            _toastTimer?.Stop();
            _picker?.Close();
        }

        // =====================================================================
        //  Header toast: volume and pop-ups while the notch is open
        // =====================================================================

        /// <summary>
        /// The open notch has no room for a pop-up, so the header row (date · tabs · time) briefly
        /// turns into a one-line message instead: icon, text, optional level bar and value.
        /// </summary>
        private void ShowHeaderToast(string glyph, Color color, string text, string value, double? level, int milliseconds)
        {
            if (_state != NotchState.Expanded || _toastTimer == null) return;

            HeaderToastIcon.Text = glyph;
            HeaderToastIconBg.Background = new SolidColorBrush(color);
            HeaderToastText.Text = text;
            HeaderToastValue.Text = value;
            HeaderToastValue.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

            if (level is double l)
            {
                HeaderToastLevel.Visibility = Visibility.Visible;
                Animate(HeaderToastScale, ScaleTransform.ScaleXProperty, Math.Clamp(l, 0, 1), 140, new CubicEase { EasingMode = EasingMode.EaseOut });
            }
            else
            {
                HeaderToastLevel.Visibility = Visibility.Collapsed;
            }

            // (Also when it was just fading out: bring it straight back.)
            HeaderToast.Visibility = Visibility.Visible;
            Fade(HeaderToast, 1, 160, 0);
            Fade(DateText, 0, 110, 0);
            Fade(TabStrip, 0, 110, 0);
            Fade(HeaderRight, 0, 110, 0);

            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 900, 4000));
            _toastTimer.Start();
        }

        private void HideHeaderToast(bool animate)
        {
            if (HeaderToast.Visibility != Visibility.Visible) return;
            _toastTimer?.Stop();
            int ms = animate ? 180 : 0;
            Fade(HeaderToast, 0, ms, 0, () =>
            {
                if (_toastTimer?.IsEnabled != true) HeaderToast.Visibility = Visibility.Collapsed;
            });
            Fade(DateText, 1, ms, 0);
            Fade(TabStrip, 1, ms, 0);
            Fade(HeaderRight, 1, ms, 0);
        }

        /// <summary>Volume changed while the notch is open → show the percentage in the header.</summary>
        private bool TryShowVolumeToast(float volume, bool muted)
        {
            if (_state != NotchState.Expanded || !IsEffectivelyVisible) return false;
            // Dragging the slider itself already shows the number next to it.
            if (_currentPage == "Music" && (VolumeSlider.IsMouseOver || VolumeButton.IsMouseOver)) return true;
            ShowHeaderToast(VolumeGlyph(volume, muted), Violet, "Volume", muted ? "Muted" : $"{Math.Round(volume * 100)}%",
                            muted ? 0 : volume, 1400);
            return true;
        }

        private void UpdateVolumePercent()
        {
            if (!Volume.IsAvailable) { VolumePercent.Text = ""; return; }
            VolumePercent.Text = Volume.IsMuted ? "Mute" : $"{Math.Round(Volume.Volume * 100)}%";
        }

        // =====================================================================
        //  Look: cover backdrop, progress line, idle buddy, night dimming
        // =====================================================================

        private void UpdateCoverBackdrop(bool animate)
        {
            bool show = _settings.CoverBackdrop && _state == NotchState.Expanded && _currentPage == "Music" && Media.Thumbnail != null;
            if (show && !ReferenceEquals(CoverBackdropBrush.ImageSource, Media.Thumbnail))
                CoverBackdropBrush.ImageSource = Media.Thumbnail;
            Fade(CoverBackdrop, show ? 0.5 : 0, animate ? 450 : 0, 0);
        }

        private void UpdateProgressLine()
        {
            bool show = _settings.ProgressLine && _showPlaying && Media.HasSession && Media.Duration > TimeSpan.Zero;
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (CollapsedProgress.Visibility != v) CollapsedProgress.Visibility = v;
            if (!show || _state != NotchState.Collapsed) return;

            double target = Math.Clamp(Media.Position.TotalSeconds / Media.Duration.TotalSeconds, 0, 1);
            double current = CollapsedProgressScale.ScaleX;
            // Glide over the next second; jump on seeks / new songs.
            bool glide = Media.IsPlaying && target > current && target - current < 0.05 && _settings.AnimationDurationMs > 0;
            if (glide)
            {
                var anim = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(1000)));
                anim.Freeze();
                CollapsedProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                CollapsedProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CollapsedProgressScale.ScaleX = target;
            }
        }

        private void UpdateIdleBuddy(bool resize)
        {
            string kind = _settings.IdleBuddy;
            bool show = kind != "Off" && !_showPlaying && CollapsedNet.Visibility != Visibility.Visible;

            if (show && kind != _buddyKind) BuildIdleBuddy(kind);
            if (!show && _buddyKind.Length > 0)
            {
                IdleBuddy.Children.Clear();   // also stops its endless animations
                _buddyKind = "";
            }

            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (IdleBuddy.Visibility == v) return;
            IdleBuddy.Visibility = v;
            if (show) Fade(IdleBuddy, 1, 400, 0);
            if (resize) ResizeCollapsed(animate: true);
        }

        private void BuildIdleBuddy(string kind)
        {
            IdleBuddy.Children.Clear();
            _buddyKind = kind;

            if (kind == "Pulse")
            {
                var dot = new Ellipse { Width = 8, Height = 8, RenderTransformOrigin = new Point(0.5, 0.5) };
                dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
                var scale = new ScaleTransform(1, 1);
                dot.RenderTransform = scale;
                Canvas.SetLeft(dot, 9);
                Canvas.SetTop(dot, 4);
                IdleBuddy.Children.Add(dot);

                var breathe = new DoubleAnimation(0.7, 1.25, new Duration(TimeSpan.FromSeconds(1.8)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, new Duration(TimeSpan.FromSeconds(1.8)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
                return;
            }

            // A curled-up sleeping cat: body, head, two ears, tail, and little z's drifting up.
            Shape Part(Shape s)
            {
                s.SetResourceReference(Shape.FillProperty, "TextBrush");
                s.Opacity = 0.85;
                IdleBuddy.Children.Add(s);
                return s;
            }
            var body = Part(new Ellipse { Width = 17, Height = 9 });
            Canvas.SetLeft(body, 0);
            Canvas.SetTop(body, 7);
            var head = Part(new Ellipse { Width = 9, Height = 8 });
            Canvas.SetLeft(head, 11);
            Canvas.SetTop(head, 6);
            Part(new Polygon { Points = new PointCollection { new Point(11.5, 8), new Point(12.5, 3.5), new Point(15, 6.5) } });
            Part(new Polygon { Points = new PointCollection { new Point(16, 6.5), new Point(18.5, 3.5), new Point(19.5, 8) } });
            var tail = new Path
            {
                Data = Geometry.Parse("M1,13 C-2,10 0,6 4,7"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.85
            };
            tail.SetResourceReference(Shape.StrokeProperty, "TextBrush");
            IdleBuddy.Children.Add(tail);

            // Slow breathing.
            var breath = new ScaleTransform(1, 1);
            body.RenderTransformOrigin = new Point(0.5, 1);
            body.RenderTransform = breath;
            breath.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.12, new Duration(TimeSpan.FromSeconds(2.2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

            for (int i = 0; i < 2; i++)
            {
                var z = new TextBlock { Text = "z", FontSize = i == 0 ? 7 : 5.5, FontWeight = FontWeights.Bold, Opacity = 0 };
                z.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
                z.SetResourceReference(TextBlock.FontFamilyProperty, "TextFont");
                Canvas.SetLeft(z, 20 + i * 3);
                IdleBuddy.Children.Add(z);

                var begin = TimeSpan.FromSeconds(i * 1.4);
                var total = new Duration(TimeSpan.FromSeconds(2.8));
                z.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(4, -5, total) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
                var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = total, RepeatBehavior = RepeatBehavior.Forever };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                z.BeginAnimation(OpacityProperty, fade);
            }
        }

        private bool IsNightNow()
        {
            if (!_settings.NightDim) return false;
            int h = DateTime.Now.Hour, from = _settings.NightDimFrom, to = _settings.NightDimTo;
            if (from == to) return false;
            return from < to ? h >= from && h < to : h >= from || h < to;
        }

        /// <summary>At night the closed notch dims; it's back to full brightness whenever you use it.</summary>
        private void UpdateNightDim(bool animate)
        {
            bool dim = IsNightNow() && _state == NotchState.Collapsed && !Notch.IsMouseOver;
            double target = dim ? _settings.NightDimLevel / 100.0 : 1;
            if (Math.Abs(target - _nightTarget) < 0.001) return;
            _nightTarget = target;
            Animate(Root, OpacityProperty, target, animate ? (dim ? 900 : 200) : 0, new CubicEase { EasingMode = EasingMode.EaseInOut });
        }

        // =====================================================================
        //  Pop-ups: downloads, rain, device batteries
        // =====================================================================

        private void OnDownloadFinished(string path)
        {
            if (!_settings.DownloadActivity) return;
            ShowActivity("\uE896", Blue, "Download finished", System.IO.Path.GetFileName(path), "", null, 4500,
                         _settings.PageShelf && _settings.DownloadsToShelf ? "Shelf" : null);
        }

        private void OnRainSoon(int minutes)
        {
            if (!_settings.RainAlerts) return;
            ShowActivity("\uE753", Blue, "Rain on the way", $"Starting in about {minutes} minutes", "", null, 6000,
                         _settings.PageWeather ? "Weather" : null);
        }

        private void OnDeviceBatteryLow(string name, int percent)
        {
            if (!_settings.DeviceBatteryAlerts) return;
            ShowActivity("\uE7BA", Red, name, "Battery is running low", $"{percent}%", percent / 100.0, 6000,
                         _settings.PageSystem ? "System" : null);
        }

        /// <summary>"WH-1000XM4 82%  ·  MX Master 40%" for the System page.</summary>
        private string DeviceBatteriesText()
        {
            var list = _svc.Bluetooth.Batteries;
            if (list.Count == 0) return "";
            return string.Join("   ·   ", list.Take(3).Select(b =>
                (b.Name.Length > 16 ? b.Name[..15].TrimEnd() + "…" : b.Name) + $" {b.Percent}%"));
        }

        // =====================================================================
        //  Calendar: Join button
        // =====================================================================

        private static void OpenLink(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError("Opening the meeting link failed", ex); }
        }

        /// <summary>Adds a blue "Join" chip to a calendar row when the meeting is about to start or running.</summary>
        private bool TryAddJoinChip(Grid row, CalendarEvent ev)
        {
            if (!_settings.MeetingJoinButton || string.IsNullOrEmpty(ev.JoinUrl)) return false;
            var now = DateTime.Now;
            if ((ev.Start - now).TotalMinutes > 15 || ev.End < now) return false;

            var join = new Button
            {
                Style = (Style)FindResource("ChipButton"),
                Content = "Join",
                Background = new SolidColorBrush(Blue),
                Foreground = Brushes.White,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Open the meeting"
            };
            string url = ev.JoinUrl;
            join.Click += (_, _) => OpenLink(url);
            Grid.SetColumn(join, 3);
            row.Children.Add(join);
            return true;
        }

        // =====================================================================
        //  Notes page: note / to-do
        // =====================================================================

        private void NotesMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string mode }) return;
            _notesMode = mode;
            if (_typing) EndTyping(collapseIfOutside: false);
            RenderTodo();
        }

        private void RenderTodo()
        {
            bool todo = _notesMode == "Todo";
            NotePanel.Visibility = todo ? Visibility.Collapsed : Visibility.Visible;
            TodoPanel.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
            NotesModeNote.Opacity = todo ? 0.45 : 1;
            NotesModeTodo.Opacity = todo ? 1 : 0.45;

            var items = _svc.Todo.Items;
            int open = items.Count(i => !i.Done);
            TodoCount.Text = open > 0 ? $"{open} to do" : "";

            TodoList.Children.Clear();
            if (items.Count == 0)
            {
                TodoList.Children.Add(MakeText("Nothing to do. Nice.", subtle: true));
                return;
            }

            foreach (var item in items)
            {
                var check = new TextBlock
                {
                    Text = item.Done ? "\uE73A" : "\uE739",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 12,
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = item.Done ? new SolidColorBrush(Green) : (Brush)FindResource("TextBrush")
                };
                var label = MakeText(item.Text, subtle: item.Done);
                label.FontSize = Math.Max(8, _settings.FontSize - 1.5);
                if (item.Done) label.TextDecorations = TextDecorations.Strikethrough;

                var row = new DockPanel();
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);
                row.Children.Add(label);

                var button = new Button
                {
                    Style = (Style)FindResource("TextButton"),
                    Content = row,
                    Margin = new Thickness(2, 1, 0, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    ToolTip = item.Done ? "Click: not done · Right-click: remove" : "Click: done · Right-click: remove"
                };
                var it = item;
                button.Click += (_, _) => _svc.Todo.Toggle(it);
                button.MouseRightButtonUp += (_, ev) => { ev.Handled = true; _svc.Todo.Remove(it); };
                TodoList.Children.Add(button);
            }
        }

        private void TypingBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox box) BeginTyping(box);
        }

        private void TodoBox_TextChanged(object sender, TextChangedEventArgs e)
            => TodoPlaceholder.Visibility = TodoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void TodoBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _svc.Todo.Add(TodoBox.Text);
                TodoBox.Clear();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndTyping(collapseIfOutside: true);
            }
        }

        // =====================================================================
        //  Tools page: calculator, color picker, countdown
        // =====================================================================

        private void RenderTools()
        {
            RenderColorHistory();

            string title = _settings.CountdownTitle;
            if (!TryParseDay(_settings.CountdownDate, out var day))
            {
                CountdownBig.Text = "";
                CountdownSub.Text = "Add a countdown in Settings → Widgets.";
                return;
            }

            int days = (day.Date - DateTime.Today).Days;
            string what = title.Length > 0 ? title : day.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            if (days > 0)
            {
                CountdownBig.Text = days == 1 ? "1 day" : $"{days} days";
                CountdownSub.Text = "until " + what;
            }
            else if (days == 0)
            {
                CountdownBig.Text = "Today!";
                CountdownSub.Text = what;
            }
            else
            {
                CountdownBig.Text = days == -1 ? "1 day ago" : $"{-days} days ago";
                CountdownSub.Text = what;
            }
        }

        private static bool TryParseDay(string text, out DateTime day)
        {
            day = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyy/MM/dd", "dd-MM-yyyy", "dd/MM/yyyy", "d MMM yyyy" },
                       CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
                   || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out day);
        }

        private void CalcBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            CalcPlaceholder.Visibility = CalcBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            string? result = Calculator.Evaluate(CalcBox.Text);
            CalcResult.Text = result != null ? "= " + result : "=";
            CalcResult.Opacity = result != null ? 1 : 0.4;
            CalcHint.Text = "Type a sum or a conversion. Enter copies the answer.";
        }

        private void CalcBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                string? result = Calculator.Evaluate(CalcBox.Text);
                if (result == null) return;
                try
                {
                    Clipboard.SetText(result);
                    CalcHint.Text = "Copied ✓";
                }
                catch (Exception ex)
                {
                    App.LogError("Copying the result failed", ex);
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndTyping(collapseIfOutside: true);
            }
        }

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            if (_picker != null) return;
            if (_typing) EndTyping(collapseIfOutside: false);

            _picker = new ColorPickerWindow();
            _picker.Picked += color =>
            {
                _picker = null;
                if (color is not Color c) return;
                string hex = ColorHelper.ToHex(c);
                try { Clipboard.SetText(hex); }
                catch (Exception ex) { App.LogError("Copying the color failed", ex); }

                RecentColors.Remove(c);
                RecentColors.Insert(0, c);
                if (RecentColors.Count > 6) RecentColors.RemoveRange(6, RecentColors.Count - 6);
                RecentColorsChanged?.Invoke();

                ShowActivity("\uE790", c, "Color copied", hex, "", null, 2600, _settings.PageTools ? "Tools" : null, hud: true);
            };
            _picker.Closed += (_, _) => _picker = null;
            _picker.Show();
        }

        private void RenderColorHistory()
        {
            ColorHistory.Children.Clear();
            foreach (var c in RecentColors)
            {
                string hex = ColorHelper.ToHex(c);
                var swatch = new Button
                {
                    Style = (Style)FindResource("TileButton"),
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 4, 0),
                    Background = new SolidColorBrush(c),
                    ToolTip = hex + " – click to copy"
                };
                swatch.Click += (_, _) =>
                {
                    try { Clipboard.SetText(hex); } catch { /* clipboard busy */ }
                    ShowHeaderToast("\uE790", c, "Copied " + hex, "", null, 1300);
                };
                ColorHistory.Children.Add(swatch);
            }
        }

        // =====================================================================
        //  Keyboard control (after Win + Alt + N)
        // =====================================================================

        /// <summary>Gives the open notch the keyboard so arrows / Space / Esc work.</summary>
        private void EnterKeyboardMode()
        {
            if (!_settings.KeyboardControl || _keyboardMode) return;
            _keyboardMode = true;
            _previousForeground = NativeMethods.GetForegroundWindow();
            SetNoActivate(false);
            Activate();
            Focus();
        }

        /// <summary>Hands the keyboard back to the app you were in.</summary>
        private void LeaveKeyboardMode()
        {
            if (!_keyboardMode) return;
            _keyboardMode = false;
            if (!_typing) SetNoActivate(true);
            if (_previousForeground != IntPtr.Zero && _previousForeground != _hwnd)
                NativeMethods.SetForegroundWindow(_previousForeground);
            _previousForeground = IntPtr.Zero;
        }

        private void OnNotchKeyDown(object sender, KeyEventArgs e)
        {
            if (!_keyboardMode || _typing || _state != NotchState.Expanded) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.Left:
                    if (ctrl) _ = Media.PreviousAsync(); else SwitchPage(-1);
                    break;
                case Key.Right:
                    if (ctrl) _ = Media.NextAsync(); else SwitchPage(1);
                    break;
                case Key.Up:
                    if (Volume.IsAvailable) Volume.Step(0.02f);
                    break;
                case Key.Down:
                    if (Volume.IsAvailable) Volume.Step(-0.02f);
                    break;
                case Key.Space:
                case Key.Enter:
                    if (Media.HasSession) _ = Media.TogglePlayPauseAsync();
                    break;
                case Key.M:
                    if (Volume.IsAvailable) Volume.ToggleMute();
                    break;
                case Key.Escape:
                    _hotkeyPinned = false;
                    SetState(NotchState.Collapsed, animate: true);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }
    }
}
