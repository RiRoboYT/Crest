using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Crest.Helpers;
using Crest.Models;
using Crest.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Crest
{
    /// <summary>
    /// Settings UI: sidebar + pages, live preview, Dark / Light / Follow-Windows theme.
    /// Controls are bound directly to the live <see cref="AppSettings"/> instance, so every change
    /// is applied to the notch immediately and auto-saved by <see cref="SettingsManager"/>.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsManager _manager;
        private readonly AppSettings _settings;
        private readonly MediaSessionService _media;

        // Preview spectrum (fake animation, only while this window is open).
        private readonly SolidColorBrush _previewSpectrumBrush = new(Colors.White);
        private readonly SpectrumAnimator _previewSpectrum;
        private readonly DispatcherTimer _previewClock;
        private readonly DispatcherTimer _previewRefresh;
        private NotchState _previewState = NotchState.Collapsed;
        private bool _previewIdle;   // "Idle" segment: collapsed with nothing playing

        private bool _syncingControls;

        private static readonly (string Tag, string Title, string Subtitle)[] Pages =
        {
            ("Appearance", "Appearance", "Presets, colors and text."),
            ("Size", "Size & Shape", "How big the notch is, its corners and where it sits."),
            ("Spectrum", "Spectrum", "The little bars that dance while music plays."),
            ("Media", "Media", "What the notch shows about the song that's playing."),
            ("Behavior", "Behavior", "Startup, hover and animation."),
            ("About", "About", "Version, files and tips."),
        };

        public SettingsWindow(SettingsManager manager, MediaSessionService media)
        {
            _manager = manager;
            _settings = manager.Settings;
            _media = media;

            InitializeComponent();

            _previewSpectrum = new SpectrumAnimator(_previewSpectrumBrush, PvCollapsedBars, PvPeekBars, PvExpandedBars);

            _previewClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _previewClock.Tick += (_, _) => UpdatePreviewTexts();

            // Settings arrive in bursts (slider drags) → refresh the preview once per frame at most.
            _previewRefresh = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _previewRefresh.Tick += (_, _) => { _previewRefresh.Stop(); UpdatePreview(animate: true); };

            // Lists
            FontCombo.ItemsSource = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            RefreshMonitors();
            BuildPresetCards();

            // DataContext only after the lists exist, so bound selections (font) aren't reset.
            DataContext = _settings;

            SettingsPathText.Text = manager.SettingsPath;
            VersionText.Text = "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0");

            // Theme
            ApplyTheme();
            SyncSegments();

            _settings.PropertyChanged += OnSettingsChanged;
            _media.Updated += OnMediaUpdated;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            SourceInitialized += (_, _) => ApplyTheme();
            Loaded += (_, _) =>
            {
                Nav.SelectedIndex = 0;
                UpdatePreview(animate: false);
                _previewSpectrum.SetPlaying(true);
                _previewClock.Start();
            };
            StateChanged += OnStateChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            Closed += OnClosed;
        }

        private static MainWindow? Notch => Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

        // =====================================================================
        //  Theme
        // =====================================================================

        private void ApplyTheme() => SettingsTheme.Apply(this, SettingsTheme.ResolveDark(_settings.SettingsTheme));

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_settings.SettingsTheme == AppTheme.System)
                Dispatcher.InvokeAsync(ApplyTheme);
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingControls || sender is not RadioButton { Tag: string tag }) return;
            if (Enum.TryParse(tag, out AppTheme theme)) _settings.SettingsTheme = theme;
        }

        private void Shape_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingControls || sender is not RadioButton { Tag: string tag }) return;
            if (Enum.TryParse(tag, out NotchShape shape)) _settings.Shape = shape;
        }

        /// <summary>Radio-button groups aren't bound; keep them in step with the settings.</summary>
        private void SyncSegments()
        {
            _syncingControls = true;
            try
            {
                ThemeDark.IsChecked = _settings.SettingsTheme == AppTheme.Dark;
                ThemeLight.IsChecked = _settings.SettingsTheme == AppTheme.Light;
                ThemeSystem.IsChecked = _settings.SettingsTheme == AppTheme.System;
                ShapeNotch.IsChecked = _settings.Shape == NotchShape.Notch;
                ShapePill.IsChecked = _settings.Shape == NotchShape.Pill;
            }
            finally
            {
                _syncingControls = false;
            }
        }

        // =====================================================================
        //  Navigation
        // =====================================================================

        /// <summary>Select a sidebar item on click even if WPF couldn't give it keyboard focus.</summary>
        private void Nav_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null && d is not ListBoxItem)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

            if (d is ListBoxItem item && !item.IsSelected)
                item.IsSelected = true;
        }

        private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Nav.SelectedItem is not ListBoxItem { Tag: string tag }) return;

            var page = Pages.FirstOrDefault(p => p.Tag == tag);
            PageTitle.Text = page.Title ?? tag;
            PageSubtitle.Text = page.Subtitle ?? "";

            PageAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PageSize.Visibility = tag == "Size" ? Visibility.Visible : Visibility.Collapsed;
            PageSpectrum.Visibility = tag == "Spectrum" ? Visibility.Visible : Visibility.Collapsed;
            PageMedia.Visibility = tag == "Media" ? Visibility.Visible : Visibility.Collapsed;
            PageBehavior.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
            PreviewCard.Visibility = tag == "About" ? Visibility.Collapsed : Visibility.Visible;

            // Show the matching notch state for the page you're on.
            if (tag == "Media") SetPreviewState(NotchState.Peek);
            else if (tag == "Size" && _previewState == NotchState.Peek) SetPreviewState(NotchState.Expanded);

            PageScroller.ScrollToTop();

            // Subtle slide + fade in.
            var duration = new Duration(TimeSpan.FromMilliseconds(240));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PagesHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            PagesShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        }

        // =====================================================================
        //  Live preview
        // =====================================================================

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.SettingsTheme):
                    ApplyTheme();
                    SyncSegments();
                    break;
                case nameof(AppSettings.Shape):
                    SyncSegments();
                    break;
                case nameof(AppSettings.MonitorDevice):
                    SyncMonitorSelection();
                    break;
            }

            if (!_previewRefresh.IsEnabled) _previewRefresh.Start();
        }

        private void OnMediaUpdated(object? sender, EventArgs e)
        {
            if (!_previewRefresh.IsEnabled) _previewRefresh.Start();
        }

        private void PreviewState_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingControls || !IsLoaded) return;
            _previewIdle = sender == PvStateIdle;
            if (sender == PvStatePeek) _previewState = NotchState.Peek;
            else if (sender == PvStateExpanded) _previewState = NotchState.Expanded;
            else _previewState = NotchState.Collapsed;
            UpdatePreview(animate: true);
        }

        private void SetPreviewState(NotchState state)
        {
            _previewState = state;
            _previewIdle = false;
            _syncingControls = true;
            try
            {
                PvStateIdle.IsChecked = false;
                PvStateCollapsed.IsChecked = state == NotchState.Collapsed;
                PvStatePeek.IsChecked = state == NotchState.Peek;
                PvStateExpanded.IsChecked = state == NotchState.Expanded;
            }
            finally
            {
                _syncingControls = false;
            }
            UpdatePreview(animate: true);
        }

        private void UpdatePreview(bool animate)
        {
            if (!IsInitialized) return;
            var s = _settings;

            // ---- colors (same rules as the real notch) ----
            Color background = ColorHelper.Parse(s.BackgroundColor, Colors.Black);
            Color text = ColorHelper.Parse(s.TextColor, Colors.White);
            Color spectrum = ColorHelper.Parse(s.SpectrumColor, Colors.White);
            Color accent = ColorHelper.Parse(s.AccentColor, Colors.White);
            if (s.UseAlbumColors && _media.AlbumColor is Color album)
            {
                spectrum = accent = ColorHelper.AdjustForBackground(album, background);
            }

            Resources["Pv.Bg"] = ColorHelper.Frozen(background, s.BackgroundOpacity);
            Resources["Pv.Text"] = ColorHelper.Frozen(text);
            Resources["Pv.Sub"] = ColorHelper.Frozen(text, 0.6);
            Resources["Pv.Track"] = ColorHelper.Frozen(text, 0.2);
            Resources["Pv.Accent"] = ColorHelper.Frozen(accent);
            _previewSpectrumBrush.Color = spectrum;

            try { Resources["Pv.Font"] = new FontFamily(s.FontFamily); }
            catch { Resources["Pv.Font"] = new FontFamily("Segoe UI"); }

            double fBase = s.FontSize, fLarge = s.FontSize + 2, fSmall = Math.Max(8, s.FontSize - 2);
            PvTime.FontSize = fBase;
            PvPeekTitle.FontSize = fBase;
            PvPeekArtist.FontSize = fSmall;
            PvDate.FontSize = fSmall;
            PvExpandedTime.FontSize = fSmall;
            PvTitle.FontSize = fLarge;
            PvArtist.FontSize = fBase;

            // ---- shape ----
            bool notchShape = s.Shape == NotchShape.Notch;
            double shoulder = notchShape ? Math.Min(s.ShoulderSize, s.CollapsedHeight / 2) : 0;
            PvLeftShoulder.Visibility = PvRightShoulder.Visibility = shoulder > 0 ? Visibility.Visible : Visibility.Collapsed;
            PvLeftShoulder.Width = PvLeftShoulder.Height = PvRightShoulder.Width = PvRightShoulder.Height = Math.Max(shoulder, 0.1);
            PvShapeRow.Margin = new Thickness(0, notchShape ? 0 : s.TopMargin, 0, 0);

            double collapsedWidth = _previewIdle && s.ShrinkWhenIdle
                ? MainWindow.ComputeIdleWidth(s, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                : s.CollapsedWidth;
            PvCollapsed.Width = collapsedWidth;
            PvCollapsedBars.Visibility = _previewIdle ? Visibility.Collapsed : Visibility.Visible;
            PvCollapsed.Height = s.CollapsedHeight;
            PvPeek.Width = MainWindow.ComputePeekWidth(s);
            PvPeek.Height = MainWindow.ComputePeekHeight(s);
            PvExpanded.Width = s.ExpandedWidth;
            PvExpanded.Height = s.ExpandedHeight;

            double w, h, r;
            switch (_previewState)
            {
                case NotchState.Expanded:
                    w = s.ExpandedWidth; h = s.ExpandedHeight; r = s.ExpandedCornerRadius; break;
                case NotchState.Peek:
                    w = MainWindow.ComputePeekWidth(s); h = MainWindow.ComputePeekHeight(s); r = MainWindow.ComputePeekRadius(s); break;
                default:
                    w = collapsedWidth; h = s.CollapsedHeight; r = s.CornerRadius; break;
            }

            AnimateTo(PvNotch, WidthProperty, w, animate);
            AnimateTo(PvNotch, HeightProperty, h, animate);
            PvNotch.CornerRadius = notchShape ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);

            PvCollapsed.Visibility = _previewState == NotchState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            PvPeek.Visibility = _previewState == NotchState.Peek ? Visibility.Visible : Visibility.Collapsed;
            PvExpanded.Visibility = _previewState == NotchState.Expanded ? Visibility.Visible : Visibility.Collapsed;

            // ---- toggles ----
            PvDate.Visibility = s.ShowDate ? Visibility.Visible : Visibility.Collapsed;
            PvArt.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            PvPeekArt.Visibility = s.ShowAlbumArt ? Visibility.Visible : Visibility.Collapsed;
            PvProgress.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            PvVolume.Visibility = s.ShowVolumeIcon ? Visibility.Visible : Visibility.Collapsed;
            PvExpandedBars.Visibility = s.ShowSpectrumWhenExpanded ? Visibility.Visible : Visibility.Collapsed;

            _previewSpectrum.Configure(s.SpectrumBarCount, s.SpectrumBarWidth, s.SpectrumBarSpacing, s.SpectrumMaxHeight, s.SpectrumSpeedMs);

            UpdatePreviewTexts();
            UpdatePreviewMedia();
            UpdatePresetSelection();
        }

        private void UpdatePreviewTexts()
        {
            var now = DateTime.Now;
            string time = now.ToString(MainWindow.TimeFormat(_settings), CultureInfo.InvariantCulture);
            PvTime.Text = time;
            PvExpandedTime.Text = time;
            PvDate.Text = now.ToString("dddd, MMM d", CultureInfo.CurrentCulture);
        }

        /// <summary>Use the song that's actually playing in the preview when there is one.</summary>
        private void UpdatePreviewMedia()
        {
            bool hasMedia = _media.HasSession && !string.IsNullOrWhiteSpace(_media.Title);
            string title = hasMedia ? _media.Title : "Midnight City";
            string artist = hasMedia ? (string.IsNullOrWhiteSpace(_media.Artist) ? "Now playing" : _media.Artist) : "M83";

            PvTitle.Text = PvPeekTitle.Text = title;
            PvArtist.Text = PvPeekArtist.Text = artist;

            Brush art = _media.Thumbnail != null
                ? new ImageBrush(_media.Thumbnail) { Stretch = Stretch.UniformToFill }
                : (Brush)FindResource("Pv.ArtFallback");
            PvArt.Background = art;
            PvPeekArt.Background = art;
        }

        private static void AnimateTo(FrameworkElement element, DependencyProperty property, double to, bool animate)
        {
            if (!animate)
            {
                element.BeginAnimation(property, null);
                element.SetValue(property, to);
                return;
            }
            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(260)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private void PinCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool pinned = PinCheck.IsChecked == true;
            Notch?.SetPreviewExpanded(pinned);
            if (pinned) SetPreviewState(NotchState.Expanded);
        }

        // =====================================================================
        //  Presets
        // =====================================================================

        private void BuildPresetCards()
        {
            PresetHost.Children.Clear();
            foreach (var preset in ThemePresets.All)
            {
                var button = new Button
                {
                    Style = (Style)FindResource("PresetCard"),
                    Tag = preset,
                    Margin = new Thickness(0, 0, 10, 0),
                    Content = BuildPresetVisual(preset),
                    ToolTip = preset.Description
                };
                button.Click += (_, _) => preset.ApplyTo(_settings);
                PresetHost.Children.Add(button);
            }
            UpdatePresetSelection();
        }

        private UIElement BuildPresetVisual(ThemePreset preset)
        {
            Color bg = ColorHelper.Parse(preset.BackgroundColor, Colors.Black);
            Color text = ColorHelper.Parse(preset.TextColor, Colors.White);
            Color bars = ColorHelper.Parse(preset.SpectrumColor, Colors.White);

            var notch = new Border
            {
                Width = 86,
                Height = 22,
                CornerRadius = new CornerRadius(0, 0, 9, 9),
                Background = ColorHelper.Frozen(bg, preset.BackgroundOpacity),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var inner = new Grid { Margin = new Thickness(9, 0, 9, 0) };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.Children.Add(new TextBlock
            {
                Text = "9:41",
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ColorHelper.Frozen(text),
                VerticalAlignment = VerticalAlignment.Center
            });

            var barPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            double[] heights = { 5, 10, 7, 12, 6 };
            Color[] albumColors = { Color.FromRgb(0xFF, 0x7A, 0x59), Color.FromRgb(0xFF, 0x5F, 0xA2), Color.FromRgb(0xC1, 0x3B, 0xFF), Color.FromRgb(0x3B, 0x8C, 0xFF), Color.FromRgb(0x3D, 0xDC, 0x84) };
            for (int i = 0; i < heights.Length; i++)
            {
                barPanel.Children.Add(new Rectangle
                {
                    Width = 2.4,
                    Height = heights[i],
                    RadiusX = 1.2,
                    RadiusY = 1.2,
                    Margin = new Thickness(i == 0 ? 0 : 1.6, 0, 0, 0),
                    Fill = ColorHelper.Frozen(preset.UseAlbumColors ? albumColors[i] : bars)
                });
            }
            Grid.SetColumn(barPanel, 1);
            inner.Children.Add(barPanel);
            notch.Child = inner;

            var screen = new Border
            {
                Height = 64,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("S.Wall"),
                ClipToBounds = true,
                Child = notch
            };

            var name = new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 9, 0, 0) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "S.Text");
            var desc = new TextBlock { Text = preset.Description, FontSize = 11.5, Margin = new Thickness(2, 1, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "S.SubText");

            var stack = new StackPanel();
            stack.Children.Add(screen);
            stack.Children.Add(name);
            stack.Children.Add(desc);
            return stack;
        }

        private void UpdatePresetSelection()
        {
            foreach (var child in PresetHost.Children.OfType<Button>())
            {
                bool active = child.Tag is ThemePreset p && p.Matches(_settings);
                child.SetResourceReference(BorderBrushProperty, active ? "S.Accent" : "S.ControlBorder");
            }
        }

        // =====================================================================
        //  Monitors
        // =====================================================================

        private void RefreshMonitors()
        {
            var options = MonitorHelper.GetOptions();
            var current = MonitorCombo.ItemsSource as System.Collections.Generic.List<MonitorOption>;

            // Only swap the list when monitors actually changed (avoids flicker / reselection).
            if (current == null || !current.SequenceEqual(options))
            {
                _syncingControls = true;
                try { MonitorCombo.ItemsSource = options; }
                finally { _syncingControls = false; }
            }
            SyncMonitorSelection();
        }

        private void SyncMonitorSelection()
        {
            _syncingControls = true;
            try
            {
                MonitorCombo.SelectedValue = _settings.MonitorDevice;
                if (MonitorCombo.SelectedIndex < 0) MonitorCombo.SelectedIndex = 0;   // unplugged → "Primary"
            }
            finally
            {
                _syncingControls = false;
            }
        }

        private void MonitorCombo_DropDownOpened(object? sender, EventArgs e) => RefreshMonitors();

        private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingControls || MonitorCombo.SelectedValue is not string id) return;
            _settings.MonitorDevice = id;
        }

        // =====================================================================
        //  Buttons
        // =====================================================================

        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string propertyName }) return;

            PropertyInfo? prop = typeof(AppSettings).GetProperty(propertyName);
            if (prop == null || prop.PropertyType != typeof(string)) return;

            var current = ColorHelper.Parse(prop.GetValue(_settings) as string, Colors.White);

            using var dialog = new Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                var c = dialog.Color;
                prop.SetValue(_settings, ColorHelper.ToHex(Color.FromRgb(c.R, c.G, c.B)));
            }
        }

        private void CenterOffset_Click(object sender, RoutedEventArgs e) => _settings.HorizontalOffset = 0;

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this,
                "Reset all Crest settings to their defaults?",
                "Crest", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _settings.CopyFrom(new AppSettings());
                SyncSegments();
                SyncMonitorSelection();
            }
        }

        private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _manager.Save();
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_manager.SettingsPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError("Opening settings folder failed", ex);
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = System.IO.Path.GetDirectoryName(App.LogFilePath)!;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogError("Opening log folder failed", ex);
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // =====================================================================
        //  Window plumbing
        // =====================================================================

        private void OnStateChanged(object? sender, EventArgs e)
        {
            // WindowChrome windows overhang the screen by the frame size when maximized.
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            _previewSpectrum.SetSuspended(WindowState == WindowState.Minimized);
        }

        /// <summary>Enter in a text box commits it immediately (hex colors).</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (Keyboard.FocusedElement is TextBox tb)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _previewClock.Stop();
            _previewRefresh.Stop();
            _previewSpectrum.SetSuspended(true);
            _settings.PropertyChanged -= OnSettingsChanged;
            _media.Updated -= OnMediaUpdated;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;   // static event: must unsubscribe
            Notch?.SetPreviewExpanded(false);
        }
    }
}
