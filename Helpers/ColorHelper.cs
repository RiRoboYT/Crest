using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Crest.Helpers
{
    public static class ColorHelper
    {
        /// <summary>Parses "#RGB", "#RRGGBB", "#AARRGGBB" or a named color. Returns fallback on failure.</summary>
        public static Color Parse(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try
            {
                string v = value.Trim();
                if (!v.StartsWith('#') && IsHex(v)) v = "#" + v;
                return (Color)ColorConverter.ConvertFromString(v);
            }
            catch
            {
                return fallback;
            }
        }

        public static SolidColorBrush Brush(string? value, Color fallback, double opacity = 1.0)
        {
            var c = Parse(value, fallback);
            c.A = (byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1));
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static string ToHex(Color c)
            => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static bool IsHex(string s)
        {
            if (s.Length is not (3 or 6 or 8)) return false;
            foreach (char ch in s)
                if (!Uri.IsHexDigit(ch)) return false;
            return true;
        }
    }

    /// <summary>XAML converter: hex string → SolidColorBrush (used by the settings swatches).</summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => ColorHelper.Brush(value as string, Colors.Transparent);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
