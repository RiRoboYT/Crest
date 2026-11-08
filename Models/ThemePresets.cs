using System.Collections.Generic;

namespace Crest.Models
{
    /// <summary>A one-click look for the notch (colors only – sizes are left alone).</summary>
    public sealed record ThemePreset(
        string Name,
        string Description,
        string BackgroundColor,
        double BackgroundOpacity,
        string TextColor,
        string AccentColor,
        string SpectrumColor,
        bool UseAlbumColors)
    {
        public void ApplyTo(AppSettings s)
        {
            s.BackgroundColor = BackgroundColor;
            s.BackgroundOpacity = BackgroundOpacity;
            s.TextColor = TextColor;
            s.AccentColor = AccentColor;
            s.SpectrumColor = SpectrumColor;
            s.UseAlbumColors = UseAlbumColors;
        }

        /// <summary>True when the settings currently match this preset.</summary>
        public bool Matches(AppSettings s)
            => Eq(s.BackgroundColor, BackgroundColor)
               && System.Math.Abs(s.BackgroundOpacity - BackgroundOpacity) < 0.011
               && Eq(s.TextColor, TextColor)
               && Eq(s.AccentColor, AccentColor)
               && Eq(s.SpectrumColor, SpectrumColor)
               && s.UseAlbumColors == UseAlbumColors;

        private static bool Eq(string a, string b) => string.Equals(a?.Trim(), b, System.StringComparison.OrdinalIgnoreCase);
    }

    public static class ThemePresets
    {
        public static readonly ThemePreset Classic = new(
            "Classic", "Pure black, white text",
            "#000000", 1.0, "#FFFFFF", "#FFFFFF", "#FFFFFF", false);

        /// <remarks>
        /// Real blur isn't possible on a per-pixel transparent overlay window, so "Glass" is a
        /// translucent smoked look instead.
        /// </remarks>
        public static readonly ThemePreset Glass = new(
            "Glass", "Smoked, see-through",
            "#14141A", 0.72, "#FFFFFF", "#FFFFFF", "#E6FFFFFF", false);

        public static readonly ThemePreset Album = new(
            "Album", "Colors from the cover art",
            "#000000", 1.0, "#FFFFFF", "#FFFFFF", "#FFFFFF", true);

        public static readonly ThemePreset Light = new(
            "Light", "Bright notch, dark text",
            "#F4F4F7", 1.0, "#111114", "#111114", "#111114", false);

        public static IReadOnlyList<ThemePreset> All { get; } = new[] { Classic, Glass, Album, Light };
    }
}
