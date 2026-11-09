using System;
using System.Collections.Generic;
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
            var palette = ExtractPalette(source, 1);
            return palette.Length > 0 ? palette[0] : null;
        }

        /// <summary>
        /// Up to <paramref name="maxColors"/> clearly different vivid colors of an image, most
        /// prominent first. Similar shades are grouped by hue, so a cover that is "mostly blue with
        /// a pink logo" gives [blue, pink]; a plain one-color cover gives a single entry; a gray
        /// cover gives none.
        /// </summary>
        public static Color[] ExtractPalette(BitmapSource? source, int maxColors = 3)
        {
            if (source == null || maxColors <= 0) return Array.Empty<Color>();
            try
            {
                BitmapSource bmp = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w <= 0 || h <= 0) return Array.Empty<Color>();

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

                // Strongest bins first, then group them by hue. Each group keeps the color of its
                // strongest bin (the most vivid one) and adds up the score of everything similar.
                var order = new List<int>();
                for (int i = 0; i < bins; i++)
                    if (score[i] > 0) order.Add(i);
                order.Sort((a, b) => score[b].CompareTo(score[a]));

                var groups = new List<(Color Color, double Hue, double Score)>();
                foreach (int bin in order)
                {
                    var c = Color.FromRgb(
                        (byte)Math.Clamp(sumR[bin] / score[bin], 0, 255),
                        (byte)Math.Clamp(sumG[bin] / score[bin], 0, 255),
                        (byte)Math.Clamp(sumB[bin] / score[bin], 0, 255));
                    ToHsv(c, out double hue, out double sat, out _);
                    if (sat < 0.18) continue;            // too gray to be interesting

                    int match = -1;
                    for (int g = 0; g < groups.Count; g++)
                    {
                        if (HueDistance(groups[g].Hue, hue) < 28) { match = g; break; }
                    }
                    if (match >= 0)
                        groups[match] = (groups[match].Color, groups[match].Hue, groups[match].Score + score[bin]);
                    else
                        groups.Add((c, hue, score[bin]));
                }
                if (groups.Count == 0) return Array.Empty<Color>();

                groups.Sort((a, b) => b.Score.CompareTo(a.Score));
                double top = groups[0].Score;
                var result = new List<Color> { groups[0].Color };
                var hues = new List<double> { groups[0].Hue };
                for (int g = 1; g < groups.Count && result.Count < maxColors; g++)
                {
                    if (groups[g].Score < top * 0.16) break;     // a few stray pixels are not a "primary" color
                    bool distinct = true;
                    foreach (double used in hues)
                        if (HueDistance(used, groups[g].Hue) < 40) { distinct = false; break; }
                    if (!distinct) continue;
                    result.Add(groups[g].Color);
                    hues.Add(groups[g].Hue);
                }
                return result.ToArray();
            }
            catch
            {
                return Array.Empty<Color>();
            }
        }

        private static double HueDistance(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>Straight RGB mix of two colors (t = 0 → a, t = 1 → b).</summary>
        public static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(
                (byte)Math.Round(a.A + (b.A - a.A) * t),
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
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
