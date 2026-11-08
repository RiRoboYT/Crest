using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Crest.Models;
using Microsoft.Win32;

namespace Crest.Helpers
{
    /// <summary>
    /// Dark / Light palettes for the settings window. Styles in Themes/SettingsStyles.xaml use
    /// DynamicResource "S.*" keys, so swapping the brushes here re-themes everything instantly.
    /// </summary>
    public static class SettingsTheme
    {
        private static readonly Dictionary<string, string> Dark = new()
        {
            ["S.Bg"] = "#0E0E12",
            ["S.Sidebar"] = "#121217",
            ["S.Card"] = "#17171D",
            ["S.CardBorder"] = "#24242C",
            ["S.Control"] = "#22222A",
            ["S.ControlHover"] = "#2B2B35",
            ["S.ControlBorder"] = "#30303A",
            ["S.Popup"] = "#1C1C23",
            ["S.Divider"] = "#202027",
            ["S.Text"] = "#F3F3F7",
            ["S.SubText"] = "#9C9CA8",
            ["S.Faint"] = "#5E5E6A",
            ["S.Accent"] = "#7B6CFF",
            ["S.AccentSoft"] = "#2A7B6CFF",
            ["S.Danger"] = "#FF5C6C",
        };

        private static readonly Dictionary<string, string> Light = new()
        {
            ["S.Bg"] = "#F3F3F7",
            ["S.Sidebar"] = "#EBEBF1",
            ["S.Card"] = "#FFFFFF",
            ["S.CardBorder"] = "#E2E2EA",
            ["S.Control"] = "#EEEEF3",
            ["S.ControlHover"] = "#E3E3EB",
            ["S.ControlBorder"] = "#D6D6E0",
            ["S.Popup"] = "#FFFFFF",
            ["S.Divider"] = "#ECECF2",
            ["S.Text"] = "#16161C",
            ["S.SubText"] = "#686876",
            ["S.Faint"] = "#A2A2B0",
            ["S.Accent"] = "#6A5AF9",
            ["S.AccentSoft"] = "#226A5AF9",
            ["S.Danger"] = "#E5394B",
        };

        /// <summary>True when Windows apps are set to dark mode.</summary>
        public static bool IsWindowsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
            }
            catch
            {
                return true;
            }
        }

        public static bool ResolveDark(AppTheme theme) => theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsWindowsDark()
        };

        public static void Apply(Window window, bool dark)
        {
            var palette = dark ? Dark : Light;
            foreach (var (key, hex) in palette)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                window.Resources[key] = brush;
            }

            var gradient = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString(dark ? "#7B6CFF" : "#6A5AF9"), 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString(dark ? "#4FC3FF" : "#2FA8F5"), 1)
                },
                new Point(0, 0),
                new Point(1, 1));
            gradient.Freeze();
            window.Resources["S.AccentGradient"] = gradient;

            ApplyTitleBar(window, dark);
        }

        /// <summary>Dark window border + rounded corners on Windows 11 (ignored where unsupported).</summary>
        public static void ApplyTitleBar(Window window, bool dark)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int value = dark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

                int corners = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
            }
            catch
            {
                // dwmapi attributes not supported on this Windows build
            }
        }
    }
}
