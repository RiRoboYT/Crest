using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>
    /// Brings the app that owns the current media session to the front.
    ///
    /// The media API only gives us an "App User Model ID", e.g.
    ///   "Spotify.exe", "chrome", "MSEdge", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify".
    /// We first look for a running process with a matching name and focus its main window;
    /// for Store apps we fall back to activating them through the shell (shell:AppsFolder).
    ///
    /// Browsers: Windows doesn't expose which tab is playing, so the browser window is focused,
    /// not the exact tab.
    /// </summary>
    public static class AppActivator
    {
        public static bool Activate(string? appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId)) return false;

            try
            {
                foreach (string name in CandidateProcessNames(appUserModelId))
                {
                    if (TryFocusProcess(name)) return true;
                }

                // Packaged (Store) apps can be activated by AUMID; this also focuses them if already running.
                if (appUserModelId.Contains('!'))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appUserModelId}")
                    {
                        UseShellExecute = true
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                App.LogError("Bringing media app to front failed", ex);
            }
            return false;
        }

        private static IEnumerable<string> CandidateProcessNames(string aumid)
        {
            var names = new List<string>();

            void Add(string? n)
            {
                if (string.IsNullOrWhiteSpace(n)) return;
                n = n.Trim();
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
                if (n.Length > 0 && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            }

            string id = aumid;

            // Full path AUMIDs (some Win32 apps): take the file name.
            if (id.Contains('\\')) Add(Path.GetFileNameWithoutExtension(id));

            int bang = id.IndexOf('!');
            if (bang >= 0)
            {
                Add(id[(bang + 1)..]);                               // "...!Spotify" → Spotify
                string family = id[..bang];                           // "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"
                int underscore = family.IndexOf('_');
                if (underscore > 0) family = family[..underscore];
                int dot = family.LastIndexOf('.');
                Add(dot >= 0 ? family[(dot + 1)..] : family);         // → SpotifyMusic
            }
            else
            {
                Add(id);                                              // "chrome", "Spotify.exe", "MSEdge"
            }

            return names;
        }

        private static bool TryFocusProcess(string processName)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { return false; }

            try
            {
                foreach (var p in processes)
                {
                    IntPtr hwnd;
                    try { hwnd = p.MainWindowHandle; }
                    catch { continue; }
                    if (hwnd == IntPtr.Zero) continue;

                    if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(hwnd);
                    return true;
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
            return false;
        }
    }
}
