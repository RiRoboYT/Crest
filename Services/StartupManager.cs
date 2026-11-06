using System;
using Microsoft.Win32;

namespace Crest.Services
{
    /// <summary>
    /// "Start with Windows" via the per-user Run key (HKCU\...\Run).
    /// No admin rights, no COM shortcut creation, and it shows up in
    /// Task Manager → Startup apps where the user can also toggle it.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Crest";

        private static string ExePath =>
            Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return;

                if (enabled)
                {
                    if (string.IsNullOrEmpty(ExePath)) return;
                    // Always rewrite so the entry follows the exe if it was moved.
                    key.SetValue(ValueName, $"\"{ExePath}\"");
                }
                else if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                App.LogError("Updating startup registration failed", ex);
            }
        }
    }
}
