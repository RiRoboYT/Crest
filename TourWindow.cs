using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Crest
{
    /// <summary>
    /// The short first-run tour: three cards just under the notch (hover · scroll &amp; swipe ·
    /// right-click). Shown once; Settings → About can replay it.
    /// </summary>
    public sealed class TourWindow : Window
    {
        private sealed record Step(string Glyph, Color Color, string Title, string Body);

        private static readonly Step[] Steps =
        {
            new("\uE7C9", Color.FromRgb(0x7B, 0x6C, 0xFF), "Hover to open",
                "Move your mouse onto the notch at the top of the screen. It opens into a player with lyrics, weather, a timer, your file shelf and more – the colored icons at the top switch pages."),
            new("\uE767", Color.FromRgb(0x2D, 0xD4, 0xBF), "Scroll & swipe",
                "Scroll on the notch to change the volume. Swipe sideways with two fingers to skip songs, or to flip pages while it's open. Double-click to play or pause."),
            new("\uE713", Color.FromRgb(0xFF, 0x9F, 0x0A), "Make it yours",
                "Right-click the notch (or the tray icon) for settings: colors, size, glow, pop-ups and pages. Win + Alt + N opens the notch, Win + Alt + M mutes your microphone."),
        };

        private int _index;
        private readonly TextBlock _icon = new();
        private readonly Border _iconTile = new();
        private readonly TextBlock _title = new();
        private readonly TextBlock _body = new();
        private readonly TextBlock _counter = new();
        private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _nextLabel = new();
        private readonly StackPanel _content = new();

        /// <summary>The user finished or skipped the tour.</summary>
        public event EventHandler? Finished;

        public TourWindow(double centerX, double top)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Title = "Welcome to Crest";
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = centerX - 190;
            Top = top;

            var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
            var iconFont = (FontFamily)Application.Current.FindResource("IconFont");

            _icon.FontFamily = iconFont;
            _icon.FontSize = 18;
            _icon.Foreground = Brushes.White;
            _icon.HorizontalAlignment = HorizontalAlignment.Center;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            _iconTile.Width = _iconTile.Height = 40;
            _iconTile.CornerRadius = new CornerRadius(12);
            _iconTile.Child = _icon;

            _counter.FontFamily = font;
            _counter.FontSize = 11.5;
            _counter.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            _counter.VerticalAlignment = VerticalAlignment.Center;
            _counter.Margin = new Thickness(12, 0, 0, 0);

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(_iconTile);
            head.Children.Add(_counter);

            _title.FontFamily = font;
            _title.FontSize = 17;
            _title.FontWeight = FontWeights.SemiBold;
            _title.Foreground = Brushes.White;
            _title.Margin = new Thickness(0, 14, 0, 6);

            _body.FontFamily = font;
            _body.FontSize = 13;
            _body.LineHeight = 19;
            _body.TextWrapping = TextWrapping.Wrap;
            _body.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var skip = MakeButton("Skip", primary: false, out _);
            skip.MouseLeftButtonUp += (_, _) => Finish();
            var next = MakeButton("Next", primary: true, out var nextLabel);
            next.MouseLeftButtonUp += (_, _) => Next();
            _nextLabel = nextLabel;

            var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            DockPanel.SetDock(_dots, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            DockPanel.SetDock(skip, Dock.Right);
            footer.Children.Add(_dots);
            footer.Children.Add(next);
            footer.Children.Add(skip);

            _content.Children.Add(head);
            _content.Children.Add(_title);
            _content.Children.Add(_body);
            _content.Children.Add(footer);

            var card = new Border
            {
                Width = 380,
                Padding = new Thickness(22, 20, 22, 18),
                Margin = new Thickness(24),
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x16, 0x1C)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black },
                Child = _content
            };
            Content = card;

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Finish();
                else if (e.Key is Key.Enter or Key.Right or Key.Space) Next();
            };

            Render(animate: false);
            Loaded += (_, _) =>
            {
                var d = new Duration(TimeSpan.FromMilliseconds(380));
                var shift = new TranslateTransform(0, -14);
                card.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, d) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
            };
        }

        private static Border MakeButton(string text, bool primary, out TextBlock label)
        {
            label = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF))
            };
            var b = new Border
            {
                Child = label,
                Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(8, 0, 0, 0),
                Background = primary
                    ? new LinearGradientBrush(Color.FromRgb(0x7B, 0x6C, 0xFF), Color.FromRgb(0x4F, 0xC3, 0xFF), 0)
                    : Brushes.Transparent
            };
            b.MouseEnter += (_, _) => b.Opacity = 0.85;
            b.MouseLeave += (_, _) => b.Opacity = 1;
            return b;
        }

        private void Next()
        {
            if (_index >= Steps.Length - 1) { Finish(); return; }
            _index++;
            Render(animate: true);
        }

        private void Render(bool animate)
        {
            var step = Steps[_index];
            _icon.Text = step.Glyph;
            _iconTile.Background = new SolidColorBrush(step.Color);
            _title.Text = step.Title;
            _body.Text = step.Body;
            _counter.Text = $"Welcome to Crest · {_index + 1} of {Steps.Length}";
            _nextLabel.Text = _index == Steps.Length - 1 ? "Got it" : "Next";

            _dots.Children.Clear();
            for (int i = 0; i < Steps.Length; i++)
            {
                _dots.Children.Add(new Border
                {
                    Width = i == _index ? 18 : 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 5, 0),
                    Background = i == _index ? new SolidColorBrush(step.Color) : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
                });
            }

            if (animate)
            {
                var d = new Duration(TimeSpan.FromMilliseconds(260));
                var shift = new TranslateTransform(16, 0);
                _content.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(16, 0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                _content.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, d));
            }
        }

        private void Finish()
        {
            Finished?.Invoke(this, EventArgs.Empty);
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(180)));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
