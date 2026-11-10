using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Crest.Helpers;
using Crest.Services;

namespace Crest
{
    /// <summary>
    /// v1.3 additions to the notch:
    ///  • tiny cover art in the closed notch, smooth icon / clock / cover transitions
    ///  • Caps / Num Lock, battery, microphone and screenshot pop-ups, muted-mic indicator
    ///  • shuffle / repeat, Sound page (output device + per-app volume), Controls page (quick toggles)
    ///  • focus timer bits, internet speed, "hide while typing"
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Color Tomato = Color.FromRgb(0xFF, 0x6B, 0x57);
        private static readonly Color Teal = Color.FromRgb(0x2D, 0xD4, 0xBF);
        private const double CollapsedArtWidth = 30;     // 22 + 8 gap
        private const double MicIconWidth = 18;
        private const double NetTextWidth = 66;

        private DispatcherTimer? _soundRefresh;
        private DispatcherTimer? _typingTimer;
        private bool _typingHookInUse;
        private bool _typingHidden;
        private readonly HashSet<int> _modifiersDown = new();
        private bool _netInUse;
        private ImageSource? _lastThumbnail;
        private int _lyricsOffsetMs;
        private readonly Dictionary<ContentControl, string> _pendingGlyphs = new();
        private readonly Dictionary<string, (Button Tile, TextBlock Icon, TextBlock Label, Color Color)> _tiles = new();

        private void InitExtras()
        {
            _soundRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _soundRefresh.Tick += (_, _) =>
            {
                // Don't rebuild the list under a slider that's being dragged.
                if (_currentPage == "Sound" && _state == NotchState.Expanded && Mouse.LeftButton == MouseButtonState.Released)
                    RenderSound();
            };

            _typingTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1300) };
            _typingTimer.Tick += (_, _) => { _typingTimer.Stop(); RestoreFromTyping(animate: true); };

            CollapsedNet.Width = NetTextWidth;
            CollapsedNet.TextAlignment = TextAlignment.Right;

            Action<string, bool> lockKeys = OnLockKey;
            Action<string, int> battery = OnBatteryAlert;
            Action<bool> mic = OnMicChanged;
            EventHandler focusPhase = (_, _) => OnFocusPhaseChanged();
            Action<string> screenshot = OnScreenshotAdded;
            EventHandler net = (_, _) => OnNetworkUpdated();
            EventHandler toggles = (_, _) => RenderControls();
            EventHandler timer = (_, _) => { if (_currentPage == "Controls") RenderControls(); };

            Subscribe(() => _svc.LockKeys.Changed += lockKeys, () => _svc.LockKeys.Changed -= lockKeys);
            Subscribe(() => _svc.Power.BatteryAlert += battery, () => _svc.Power.BatteryAlert -= battery);
            Subscribe(() => _svc.Mic.Changed += mic, () => _svc.Mic.Changed -= mic);
            Subscribe(() => _svc.Timer.FocusPhaseChanged += focusPhase, () => _svc.Timer.FocusPhaseChanged -= focusPhase);
            Subscribe(() => _svc.Timer.Changed += timer, () => _svc.Timer.Changed -= timer);
            Subscribe(() => _svc.Shelf.ScreenshotAdded += screenshot, () => _svc.Shelf.ScreenshotAdded -= screenshot);
            Subscribe(() => _svc.Network.Updated += net, () => _svc.Network.Updated -= net);
            Subscribe(() => _svc.Toggles.Changed += toggles, () => _svc.Toggles.Changed -= toggles);

            BuildControlTiles();
        }

        /// <summary>Per-window parts of the v1.3 settings (shared services are set up by App).</summary>
        private void ApplyExtrasSettings()
        {
            var s = _settings;
            _spectrum.SetStyle(s.SpectrumStyle);
            SetTypingHook(s.HideWhileTyping);
            if (!s.HideWhileTyping) RestoreFromTyping(animate: false);

            ShuffleButton.Visibility = RepeatModeButton.Visibility = s.ShowShuffleRepeat ? Visibility.Visible : Visibility.Collapsed;
            OutputButton.Visibility = s.ShowOutputButton ? Visibility.Visible : Visibility.Collapsed;

            UpdateShuffleRepeat();
            UpdateMicIndicator(resize: false);
            UpdateCollapsedArt(animate: false, resize: false);
            UpdateNetUsage(resize: false);
            UpdateLyricsFooter();
        }

        private void CloseExtras()
        {
            _soundRefresh?.Stop();
            _typingTimer?.Stop();
            SetTypingHook(false);
            if (_netInUse) { _netInUse = false; _svc.Network.RemoveUser(); }
        }

        /// <summary>Pop-ups are silenced by "Pause pop-ups" or a focus block (feedback HUDs still show).</summary>
        private bool PopupsSilenced
            => _svc.Toggles.PopupsPaused || (_settings.FocusQuiet && _svc.Timer.InFocusWork);

        // =====================================================================
        //  Collapsed: cover art, muted mic, internet speed
        // =====================================================================

        /// <summary>Extra width of the closed notch for the cover, the muted mic and the speed text.</summary>
        private double ExtrasCollapsedWidth
            => (CollapsedArt.Visibility == Visibility.Visible ? CollapsedArtWidth : 0)
               + (MicMutedIcon.Visibility == Visibility.Visible ? MicIconWidth : 0)
               + (CollapsedNet.Visibility == Visibility.Visible ? NetTextWidth + 10 : 0)
               + (IdleBuddy.Visibility == Visibility.Visible ? IdleBuddyWidth : 0);

        private void UpdateCollapsedArt(bool animate, bool resize)
        {
            bool show = _settings.ShowArtInCollapsed && _showPlaying && Media.Thumbnail != null;
            bool was = CollapsedArt.Visibility == Visibility.Visible;
            if (!ReferenceEquals(CollapsedArtBrush.ImageSource, Media.Thumbnail))
                CollapsedArtBrush.ImageSource = Media.Thumbnail;

            if (show == was) return;
            CollapsedArt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && animate && Smooth) PopIn(CollapsedArt);
            if (resize) ResizeCollapsed(animate);
        }

        private void UpdateMicIndicator(bool resize)
        {
            bool muted = _settings.MicMutedIndicator && _svc.Mic.HasMic && _svc.Mic.IsMuted;
            var v = muted ? Visibility.Visible : Visibility.Collapsed;
            bool changed = MicMutedIcon.Visibility != v;
            MicMutedIcon.Visibility = ExpMicMuted.Visibility = v;
            if (changed && resize) ResizeCollapsed(animate: true);
        }

        private void UpdateNetUsage(bool resize)
        {
            bool want = _settings.NetSpeedInCollapsed && IsEffectivelyVisible;
            if (want != _netInUse)
            {
                _netInUse = want;
                if (want) _svc.Network.AddUser();
                else _svc.Network.RemoveUser();
            }

            var v = want && !_showPlaying ? Visibility.Visible : Visibility.Collapsed;
            if (CollapsedNet.Visibility == v) return;
            CollapsedNet.Visibility = v;
            OnNetworkUpdated();
            if (resize) ResizeCollapsed(animate: true);
        }

        private void OnNetworkUpdated()
        {
            if (CollapsedNet.Visibility == Visibility.Visible)
            {
                CollapsedNet.Text = "↓ " + NetworkSpeedService.Format(_svc.Network.Down);
                CollapsedNet.ToolTip = "Upload ↑ " + NetworkSpeedService.Format(_svc.Network.Up);
            }
        }

        /// <summary>Called after every media update (cover, playing state, shuffle / repeat…).</summary>
        private void OnMediaUpdatedExtras()
        {
            if (!ReferenceEquals(_lastThumbnail, Media.Thumbnail))
            {
                _lastThumbnail = Media.Thumbnail;
                if (Media.Thumbnail != null && Smooth)
                {
                    // New cover → fade it in instead of a hard swap.
                    foreach (var art in new UIElement[] { ArtImage, PeekArtImage, CollapsedArt })
                    {
                        art.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(380))));
                    }
                }
            }
            UpdateCollapsedArt(animate: true, resize: true);
            UpdateCoverBackdrop(animate: true);
            UpdateShuffleRepeat();
            SwapGlyph(PlayPauseButton, Media.IsPlaying ? "\uE769" : "\uE768");
        }

        // =====================================================================
        //  Smooth transitions
        // =====================================================================

        private bool Smooth => _settings.SmoothTransitions && _settings.AnimationDurationMs > 0;

        /// <summary>Morphs an icon button to a new glyph: the old one shrinks away, the new one springs in.</summary>
        private void SwapGlyph(ContentControl control, string glyph)
        {
            if (_pendingGlyphs.ContainsKey(control)) { _pendingGlyphs[control] = glyph; return; }
            if (control.Content as string == glyph) return;
            if (!Smooth || !control.IsVisible)
            {
                control.Content = glyph;
                return;
            }

            if (control.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
            {
                scale = new ScaleTransform(1, 1);
                control.RenderTransform = scale;
                control.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            _pendingGlyphs[control] = glyph;
            var outDur = new Duration(TimeSpan.FromMilliseconds(90));
            var shrink = new DoubleAnimation(0.5, outDur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            shrink.Completed += (_, _) =>
            {
                control.Content = _pendingGlyphs[control];
                _pendingGlyphs.Remove(control);
                var inDur = new Duration(TimeSpan.FromMilliseconds(260));
                var grow = new DoubleAnimation(0.5, 1, inDur) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                control.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, inDur));
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.5, outDur));
            control.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, outDur));
        }

        /// <summary>The clock text rolls up into place when it changes.</summary>
        private void RollTime()
        {
            if (!Smooth || CollapsedTime.Visibility != Visibility.Visible || _state != NotchState.Collapsed) return;
            var d = new Duration(TimeSpan.FromMilliseconds(320));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TimeRoll.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, d) { EasingFunction = ease });
            CollapsedTime.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, d) { EasingFunction = ease });
        }

        /// <summary>Small "pop" for something that just appeared.</summary>
        private static void PopIn(FrameworkElement element)
        {
            var scale = new ScaleTransform(0.6, 0.6);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            var d = new Duration(TimeSpan.FromMilliseconds(320));
            var grow = new DoubleAnimation(0.6, 1, d) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
        }

        // =====================================================================
        //  Pop-ups: lock keys, battery, microphone, focus, screenshots
        // =====================================================================

        private void OnLockKey(string name, bool on)
        {
            if (!_settings.LockKeysActivity) return;
            ShowActivity("\uE765", on ? Blue : Gray, on ? $"{name} on" : $"{name} off",
                         name == "Caps Lock" && on ? "TYPING IN CAPITALS" : "", "", null, 1300, hud: true);
        }

        private void OnBatteryAlert(string kind, int percent)
        {
            if (!_settings.BatteryAlerts) return;
            if (kind == "full")
                ShowActivity("\uE73E", Green, "Fully charged", "You can unplug the charger", $"{percent}%", 1.0, 4000);
            else
                ShowActivity("\uE7BA", Red, "Low battery", percent <= 10 ? "Plug in soon" : "Time to find a charger",
                             $"{percent}%", percent / 100.0, 6000, hud: true);
        }

        private void OnMicChanged(bool fromCrest)
        {
            UpdateMicIndicator(resize: true);
            if (_currentPage == "Controls") RenderControls();
            if (!_settings.MicMutedIndicator || !_svc.Mic.HasMic) return;
            bool muted = _svc.Mic.IsMuted;
            ShowActivity("\uE720", muted ? Red : Green, muted ? "Microphone muted" : "Microphone on",
                         _settings.MicMuteHotkey ? "Win + Alt + M to switch" : "", "", null, 1600, hud: true);
        }

        private void OnFocusPhaseChanged()
        {
            var t = _svc.Timer;
            if (t.IsBreak)
                ShowActivity("\uE916", Green, "Break time", $"{(int)t.FocusBreak.TotalMinutes} minutes – stretch, drink some water",
                             "", null, 6000, _settings.PageTimer ? "Timer" : null, hud: true);
            else
                ShowActivity("\uE916", Tomato, "Back to focus", $"Round {t.Round} · {(int)t.FocusWork.TotalMinutes} minutes",
                             "", null, 5000, _settings.PageTimer ? "Timer" : null, hud: true);
        }

        private void OnScreenshotAdded(string path)
        {
            ShowActivity("\uE722", Amber, "Screenshot on the shelf", Path.GetFileName(path), "", null, 2600,
                         _settings.PageShelf ? "Shelf" : null);
        }

        // =====================================================================
        //  Shuffle / repeat / output
        // =====================================================================

        private void UpdateShuffleRepeat()
        {
            if (!_settings.ShowShuffleRepeat) return;
            var on = new SolidColorBrush(Green);
            var off = (Brush)FindResource("SubtleTextBrush");

            ShuffleButton.IsEnabled = Media.CanShuffle;
            ShuffleButton.Foreground = Media.ShuffleOn ? on : off;
            ShuffleButton.ToolTip = !Media.CanShuffle ? "This app doesn't allow shuffle from outside" : Media.ShuffleOn ? "Shuffle on" : "Shuffle off";

            RepeatModeButton.IsEnabled = Media.CanRepeat;
            RepeatModeButton.Foreground = Media.RepeatMode == 0 ? off : on;
            RepeatModeButton.Content = Media.RepeatMode == 1 ? "\uE8ED" : "\uE8EE";
            RepeatModeButton.ToolTip = !Media.CanRepeat ? "This app doesn't allow repeat from outside"
                : Media.RepeatMode switch { 1 => "Repeat this song", 2 => "Repeat all", _ => "Repeat off" };
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e) => _ = Media.ToggleShuffleAsync();
        private void RepeatModeButton_Click(object sender, RoutedEventArgs e) => _ = Media.CycleRepeatAsync();

        private void OutputButton_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.PageSound) SelectPage("Sound", 1);
            else OpenSoundSettings();
        }

        private static void OpenSoundSettings()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError("Opening sound settings failed", ex); }
        }

        // =====================================================================
        //  Sound page: output device + per-app volume
        // =====================================================================

        private void RenderSound()
        {
            OutputList.Children.Clear();
            var outputs = _svc.AudioDevices.GetOutputs();
            foreach (var o in outputs)
            {
                var check = new TextBlock
                {
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 11,
                    Width = 16,
                    Text = o.IsDefault ? "\uE73E" : "",
                    Foreground = new SolidColorBrush(Teal),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var name = MakeText(o.Name, subtle: !o.IsDefault, bold: o.IsDefault);
                name.FontSize = Math.Max(8, _settings.FontSize - 1.5);
                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);
                row.Children.Add(name);

                var button = new Button
                {
                    Style = (Style)FindResource("TextButton"),
                    Content = row,
                    Margin = new Thickness(0, 1, 0, 5),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    ToolTip = o.IsDefault ? o.Name + " (in use)" : "Play sound through " + o.Name
                };
                string id = o.Id;
                button.Click += (_, _) =>
                {
                    if (_svc.AudioDevices.SetDefaultOutput(id)) RenderSound();
                };
                OutputList.Children.Add(button);
            }
            if (outputs.Count == 0) OutputList.Children.Add(Wrapped(MakeText("No speakers or headphones found.", subtle: true)));

            MixerList.Children.Clear();
            var apps = _svc.AudioDevices.GetApps();
            foreach (var app in apps) MixerList.Children.Add(BuildMixerRow(app));
            if (apps.Count == 0) MixerList.Children.Add(Wrapped(MakeText("Apps playing sound show up here.", subtle: true)));
        }

        private static TextBlock Wrapped(TextBlock tb)
        {
            tb.TextWrapping = TextWrapping.Wrap;
            tb.TextTrimming = TextTrimming.None;
            return tb;
        }

        private UIElement BuildMixerRow(AppAudio app)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // App icon = mute button.
            ImageSource? icon = app.ExePath != null ? FileIcon(app.ExePath) : null;
            object iconContent = icon != null
                ? (object)new Image { Source = icon, Width = 16, Height = 16 }
                : new TextBlock { Text = "\uE8D6", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12 };
            var mute = new Button
            {
                Style = (Style)FindResource("MediaButton"),
                Width = 22,
                Height = 22,
                Content = iconContent,
                Opacity = app.Muted ? 0.35 : 1,
                ToolTip = app.Muted ? $"{app.Name} – muted (click to unmute)" : $"{app.Name} – click to mute",
                VerticalAlignment = VerticalAlignment.Center
            };
            mute.Click += (_, _) =>
            {
                app.Muted = !app.Muted;
                mute.Opacity = app.Muted ? 0.35 : 1;
                mute.ToolTip = app.Muted ? $"{app.Name} – muted (click to unmute)" : $"{app.Name} – click to mute";
            };
            grid.Children.Add(mute);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var name = MakeText(app.Name, subtle: true);
            name.FontSize = Math.Max(8, _settings.FontSize - 2.5);
            stack.Children.Add(name);
            var slider = new Slider
            {
                Style = (Style)FindResource("NotchSlider"),
                Minimum = 0,
                Maximum = 100,
                SmallChange = 2,
                LargeChange = 10,
                Value = Math.Round(app.Volume * 100),
                MinHeight = 14,
                ToolTip = $"{app.Name}: {Math.Round(app.Volume * 100)}%"
            };
            slider.ValueChanged += (_, e) =>
            {
                app.Volume = (float)(e.NewValue / 100.0);
                slider.ToolTip = $"{app.Name}: {Math.Round(e.NewValue)}%";
            };
            stack.Children.Add(slider);
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            return grid;
        }

        /// <summary>Starts / stops the Sound page's refresh while it is visible.</summary>
        private void UpdateSoundRefresh()
        {
            bool on = _currentPage == "Sound" && _state == NotchState.Expanded;
            if (on && !_soundRefresh!.IsEnabled) _soundRefresh.Start();
            else if (!on) _soundRefresh?.Stop();
        }

        // =====================================================================
        //  Controls page: quick toggles
        // =====================================================================

        private void BuildControlTiles()
        {
            AddTile("dark", "\uE708", "Dark mode", Color.FromRgb(0x8B, 0x5C, 0xF6), () => _svc.Toggles.SetDarkMode(!_svc.Toggles.DarkMode));
            AddTile("bt", "\uE702", "Bluetooth", Color.FromRgb(0x3B, 0x82, 0xF6), () =>
            {
                if (_svc.Toggles.Bluetooth is bool b) _ = _svc.Toggles.SetRadioAsync(bluetooth: true, on: !b);
            });
            AddTile("wifi", "\uE701", "Wi-Fi", Color.FromRgb(0x0E, 0xA5, 0xE9), () =>
            {
                if (_svc.Toggles.WiFi is bool w) _ = _svc.Toggles.SetRadioAsync(bluetooth: false, on: !w);
            });
            AddTile("awake", "\uE7E8", "Keep awake", Color.FromRgb(0xF5, 0x9E, 0x0B), () => _svc.Toggles.SetKeepAwake(!_svc.Toggles.KeepAwake));
            AddTile("quiet", "\uE769", "Pause pop-ups", Color.FromRgb(0xF4, 0x3F, 0x5E), () => _svc.Toggles.SetPopupsPaused(!_svc.Toggles.PopupsPaused));
            AddTile("mic", "\uE720", "Mute mic", Color.FromRgb(0xEF, 0x44, 0x44), () => _svc.Mic.ToggleMute());
            AddTile("focus", "\uE916", "Focus", Tomato, () =>
            {
                var t = _svc.Timer;
                if (t.Mode == TimerMode.Focus && t.Running) t.Pause();
                else if (t.Mode == TimerMode.Focus && t.IsActive) t.Start();
                else t.StartFocus();
                RenderControls();
            });
            AddTile("night", "\uE706", "Night light", Color.FromRgb(0xFB, 0xBF, 0x24), QuickTogglesService.OpenNightLightSettings);
        }

        private void AddTile(string key, string glyph, string label, Color color, Action click)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var text = new TextBlock
            {
                Text = label,
                FontFamily = (FontFamily)FindResource("TextFont"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(icon);
            content.Children.Add(text);

            var tile = new Button
            {
                Style = (Style)FindResource("TileButton"),
                Content = content,
                Margin = new Thickness(3)
            };
            tile.Click += (_, _) => click();
            PageControls.Children.Add(tile);
            _tiles[key] = (tile, icon, text, color);
        }

        private void RenderControls()
        {
            if (_tiles.Count == 0) return;
            var tg = _svc.Toggles;
            var t = _svc.Timer;
            SetTile("dark", tg.DarkMode, null, tg.DarkMode ? "Windows is in dark mode" : "Windows is in light mode");
            SetTile("bt", tg.Bluetooth == true, tg.Bluetooth != null, tg.Bluetooth == null ? "No Bluetooth radio found" : null);
            SetTile("wifi", tg.WiFi == true, tg.WiFi != null, tg.WiFi == null ? "No Wi-Fi radio found" : null);
            SetTile("awake", tg.KeepAwake, null, "Stops the PC and screen from going to sleep");
            SetTile("quiet", tg.PopupsPaused, null, "Crest won't show pop-ups (the volume indicator still works)");
            SetTile("mic", _svc.Mic.IsMuted, _svc.Mic.HasMic, _svc.Mic.HasMic ? "Win + Alt + M" : "No microphone found");
            bool focusRunning = t.Mode == TimerMode.Focus && t.Running;
            SetTile("focus", focusRunning, null, focusRunning ? (t.IsBreak ? "On a break – click to pause" : "Focusing – click to pause") : "Start a focus session");
            SetTile("night", false, null, "Opens Night light in Windows Settings");
        }

        private void SetTile(string key, bool on, bool? available, string? tip)
        {
            var (tile, icon, label, color) = _tiles[key];
            bool enabled = available != false;
            tile.IsEnabled = enabled;
            tile.Background = on ? new SolidColorBrush(color) : (Brush)FindResource("HoverBrush");
            var fg = on ? Brushes.White : new SolidColorBrush(color);
            icon.Foreground = fg;
            label.Foreground = on ? Brushes.White : (Brush)FindResource("TextBrush");
            tile.ToolTip = tip;
        }

        // =====================================================================
        //  Lyrics: credit + sync offset
        // =====================================================================

        private void LoadLyricsOffset() => _lyricsOffsetMs = _lyricsKey.Length > 0 ? _svc.Lyrics.GetOffset(_lyricsKey) : 0;

        private void LyricsOffset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int delta) || _lyricsKey.Length == 0) return;
            // "Lyrics are late" (−) → show them earlier → shift the lookup forward.
            _lyricsOffsetMs = Math.Clamp(_lyricsOffsetMs - delta, -10000, 10000);
            _svc.Lyrics.SetOffset(_lyricsKey, _lyricsOffsetMs);
            UpdateLyricsFooter();
            UpdateLyricLine(force: true);
        }

        private void UpdateLyricsFooter()
        {
            bool found = _lyrics != null && !(_lyrics.Instrumental && !_lyrics.Synced);
            LyricsFooter.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
            LyricsSyncPanel.Visibility = found && _lyrics!.Synced && _settings.LyricsSyncButtons ? Visibility.Visible : Visibility.Collapsed;
            double s = -_lyricsOffsetMs / 1000.0;
            LyricsOffsetText.Text = _lyricsOffsetMs == 0 ? "in sync" : (s > 0 ? $"+{s:0.0} s" : $"{s:0.0} s").Replace("-", "−");
        }

        // =====================================================================
        //  Hide while typing
        // =====================================================================

        private void SetTypingHook(bool on)
        {
            if (on == _typingHookInUse) return;
            _typingHookInUse = on;
            if (on)
            {
                _svc.KeyboardHook.Key += OnGlobalKey;
                _svc.KeyboardHook.AddUser();
            }
            else
            {
                _svc.KeyboardHook.Key -= OnGlobalKey;
                _svc.KeyboardHook.RemoveUser();
                _modifiersDown.Clear();
            }
        }

        private static bool IsModifier(int vk)
            => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

        private static bool IsTypingKey(int vk)
            => vk is >= 0x30 and <= 0x39       // digits
                  or >= 0x41 and <= 0x5A       // letters
                  or >= 0x60 and <= 0x6F       // numpad
                  or >= 0xBA and <= 0xC0       // ; = , - . / `
                  or >= 0xDB and <= 0xDF       // [ \ ] '
                  or 0x20 or 0x08 or 0x2E or 0x0D;   // space, backspace, delete, enter

        private void OnGlobalKey(int vk, bool down)
        {
            if (IsModifier(vk))
            {
                if (down) _modifiersDown.Add(vk);
                else _modifiersDown.Remove(vk);
                return;
            }
            if (!down || !IsTypingKey(vk)) return;
            // Shortcuts (Ctrl / Alt / Win + key) aren't typing; Shift is.
            foreach (int m in _modifiersDown)
                if (m is not (0x10 or 0xA0 or 0xA1)) return;

            if (!_settings.HideWhileTyping || _typing || _state != NotchState.Collapsed ||
                Notch.IsMouseOver || !IsEffectivelyVisible) return;

            EnterTypingHidden();
            _typingTimer!.Stop();
            _typingTimer.Start();
        }

        private void EnterTypingHidden()
        {
            if (_typingHidden) return;
            _typingHidden = true;
            double height = Math.Max(1, Notch.ActualHeight);
            int ms = Math.Max(120, _settings.AnimationDurationMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            Animate(ShapeScale, ScaleTransform.ScaleYProperty, Math.Min(1, 5 / height), ms, ease);
            Animate(ShapeScale, ScaleTransform.ScaleXProperty, 0.55, ms, ease);
            Fade(ContentRoot, 0, ms * 0.5, 0);
        }

        private void RestoreFromTyping(bool animate)
        {
            if (!_typingHidden) return;
            _typingHidden = false;
            _typingTimer?.Stop();
            int ms = animate ? Math.Max(160, _settings.AnimationDurationMs) : 0;
            Animate(ShapeScale, ScaleTransform.ScaleYProperty, 1, ms, GrowEase(true));
            Animate(ShapeScale, ScaleTransform.ScaleXProperty, 1, ms, GrowEase(true));
            Fade(ContentRoot, 1, ms * 0.8, ms * 0.2);
        }
    }
}
