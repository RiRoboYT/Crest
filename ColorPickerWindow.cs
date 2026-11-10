using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest
{
    /// <summary>
    /// The screen color picker (Tools page): an almost invisible window over every monitor.
    /// A small swatch follows the cursor and shows the color under it; click to pick, Esc to cancel.
    /// </summary>
    public sealed class ColorPickerWindow : Window
    {
        private readonly Border _swatch = new();
        private readonly TextBlock _hex = new();
        private readonly Border _bubble;
        private readonly DispatcherTimer _timer;
        private Color _current = Colors.White;
        private bool _done;

        /// <summary>The picked color, or null when cancelled.</summary>
        public event Action<Color?>? Picked;

        public ColorPickerWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            // Alpha 1/255: enough to receive the mouse, far too little to change what you see or pick.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;
            Title = "Crest color picker";

            _swatch.Width = _swatch.Height = 26;
            _swatch.CornerRadius = new CornerRadius(8);
            _swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            _swatch.BorderThickness = new Thickness(1);

            _hex.Foreground = Brushes.White;
            _hex.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            _hex.FontSize = 13;
            _hex.FontWeight = FontWeights.SemiBold;
            _hex.VerticalAlignment = VerticalAlignment.Center;
            _hex.Margin = new Thickness(9, 0, 4, 0);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_swatch);
            row.Children.Add(_hex);

            _bubble = new Border
            {
                Child = row,
                Padding = new Thickness(7),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x16, 0x1C)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.4 },
                RenderTransform = new TranslateTransform()
            };
            Content = new Grid { Children = { _bubble } };

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += (_, _) => Sample();

            SourceInitialized += (_, _) =>
            {
                // Cover every monitor (physical pixels).
                var hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN),
                    0);
            };
            Loaded += (_, _) => { Activate(); Sample(); _timer.Start(); };
            MouseLeftButtonDown += (_, _) => Finish(_current);
            MouseRightButtonDown += (_, _) => Finish(null);
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
            Deactivated += (_, _) => Finish(null);
            Closed += (_, _) => _timer.Stop();
        }

        private void Sample()
        {
            if (_done || !NativeMethods.GetCursorPos(out var p)) return;

            IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                uint pixel = NativeMethods.GetPixel(dc, p.X, p.Y);   // 0x00BBGGRR
                if (pixel != 0xFFFFFFFF)
                    _current = Color.FromRgb((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, dc);
            }

            _swatch.Background = new SolidColorBrush(_current);
            _hex.Text = ColorHelper.ToHex(_current);

            // Keep the bubble next to the cursor, flipping sides near the screen edges.
            try
            {
                var local = PointFromScreen(new Point(p.X, p.Y));
                double x = local.X + 18, y = local.Y + 18;
                if (x + 130 > ActualWidth) x = local.X - 130;
                if (y + 50 > ActualHeight) y = local.Y - 50;
                var shift = (TranslateTransform)_bubble.RenderTransform;
                shift.X = x;
                shift.Y = y;
            }
            catch (InvalidOperationException)
            {
                // window not ready yet
            }
        }

        private void Finish(Color? color)
        {
            if (_done) return;
            _done = true;
            _timer.Stop();
            Picked?.Invoke(color);
            Close();
        }
    }
}
