using System;
using System.Collections.Generic;
using System.Linq;

namespace Crest.Helpers
{
    /// <summary>One physical display. Bounds are in physical pixels (the app is per-monitor DPI aware).</summary>
    public sealed class MonitorDescriptor
    {
        public string DeviceName { get; init; } = "";
        public NativeMethods.RECT Bounds { get; init; }
        public bool IsPrimary { get; init; }
        public uint Dpi { get; init; } = 96;
        public int Number { get; init; }

        public double Scale => Dpi / 96.0;

        public string Label =>
            $"Display {Number}  ·  {Bounds.Width} × {Bounds.Height}  ·  {Math.Round(Scale * 100)}%" + (IsPrimary ? "  (primary)" : "");
    }

    /// <summary>Option shown in the settings "Show on monitor" list.</summary>
    public sealed record MonitorOption(string Id, string Label);

    public static class MonitorHelper
    {
        public static List<MonitorDescriptor> GetMonitors()
        {
            var list = new List<MonitorDescriptor>();

            bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
            {
                var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) return true;

                uint dpi = 96;
                try
                {
                    if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                        dpi = dx;
                }
                catch
                {
                    // shcore missing (very old Windows) → assume 100%
                }

                list.Add(new MonitorDescriptor
                {
                    DeviceName = info.szDevice ?? "",
                    Bounds = info.rcMonitor,
                    IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    Dpi = dpi,
                    Number = ParseNumber(info.szDevice, list.Count + 1)
                });
                return true;
            }

            try
            {
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                App.LogError("Enumerating monitors failed", ex);
            }

            return list.OrderBy(m => m.Number).ToList();
        }

        /// <summary>Finds the monitor by device name ("" or not found → primary).</summary>
        public static MonitorDescriptor? Find(string? deviceName)
        {
            var all = GetMonitors();
            if (all.Count == 0) return null;

            if (!string.IsNullOrEmpty(deviceName))
            {
                var match = all.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
        }

        public static List<MonitorOption> GetOptions()
        {
            var options = new List<MonitorOption> { new("", "Primary monitor"), new("*", "Every monitor (one notch each)") };
            options.AddRange(GetMonitors().Select(m => new MonitorOption(m.DeviceName, m.Label)));
            return options;
        }

        /// <summary>"\\.\DISPLAY2" → 2.</summary>
        private static int ParseNumber(string? device, int fallback)
        {
            if (string.IsNullOrEmpty(device)) return fallback;
            int i = device.Length;
            while (i > 0 && char.IsDigit(device[i - 1])) i--;
            return int.TryParse(device[i..], out int n) ? n : fallback;
        }
    }
}
