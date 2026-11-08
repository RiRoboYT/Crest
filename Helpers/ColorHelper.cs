using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        public static SolidColorBrush Frozen(Color c, double opacity = 1.0)
        {
            c.A = (byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1));
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static string ToHex(Color c)
            => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Perceived brightness 0..1.</summary>
        public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        // ------------------------------------------------------------------ HSV

        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            v = max;
            s = max <= 0 ? 0 : d / max;

            if (d <= 0) { h = 0; return; }
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        public static Color FromHsv(double h, double s, double v, byte alpha = 255)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r, g, b;

            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromArgb(alpha,
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        // ------------------------------------------------------------------ album art

        /// <summary>
        /// Picks the most prominent *vivid* color of an image (a weighted color histogram that
        /// favours saturated, reasonably bright pixels). Returns null for gray-ish covers so the
        /// caller can fall back to the normal colors.
        /// </summary>
        public static Color? ExtractAccent(BitmapSource? source)
        {
            if (source == null) return null;
            try
            {
                BitmapSource bmp = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w <= 0 || h <= 0) return null;

                int stride = w * 4;
                var pixels = new byte[stride * h];
                bmp.CopyPixels(pixels, stride, 0);

                const int bins = 4096;                    // 4 bits per channel
                var score = new double[bins];
                var sumR = new double[bins];
                var sumG = new double[bins];
                var sumB = new double[bins];

                int total = w * h;
                int step = Math.Max(1, total / 6000);     // ~6k samples is plenty

                for (int i = 0; i < total; i += step)
                {
                    int o = i * 4;
                    byte b = pixels[o], g = pixels[o + 1], r = pixels[o + 2], a = pixels[o + 3];
                    if (a < 128) continue;

                    var c = Color.FromRgb(r, g, b);
                    ToHsv(c, out _, out double s, out double v);
                    if (v < 0.18) continue;               // ignore near-black

                    double weight = 0.04 + s * s * (0.4 + v);
                    int bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    score[bin] += weight;
                    sumR[bin] += r * weight;
                    sumG[bin] += g * weight;
                    sumB[bin] += b * weight;
                }

                int best = -1;
                double bestScore = 0;
                for (int i = 0; i < bins; i++)
                {
                    if (score[i] > bestScore)
                    {
                        bestScore = score[i];
                        best = i;
                    }
                }
                if (best < 0 || bestScore <= 0) return null;

                var result = Color.FromRgb(
                    (byte)Math.Clamp(sumR[best] / bestScore, 0, 255),
                    (byte)Math.Clamp(sumG[best] / bestScore, 0, 255),
                    (byte)Math.Clamp(sumB[best] / bestScore, 0, 255));

                ToHsv(result, out _, out double rs, out _);
                return rs < 0.18 ? null : result;        // too gray to be interesting
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Makes an accent readable on the given background (bright on dark, deep on light).</summary>
        public static Color AdjustForBackground(Color accent, Color background)
        {
            ToHsv(accent, out double h, out double s, out double v);
            bool darkBackground = Luminance(background) < 0.5 || background.A < 140;

            if (darkBackground)
            {
                v = Math.Max(v, 0.88);
                s = Math.Min(s, 0.78);
            }
            else
            {
                v = Math.Min(v, 0.58);
                s = Math.Max(s, 0.55);
            }
            return FromHsv(h, s, v);
        }

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
