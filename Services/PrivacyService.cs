using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Crest.Services
{
    /// <summary>
    /// Microphone / camera "in use" indicator. Windows records every app's mic and camera use under
    /// HKCU\...\CapabilityAccessManager\ConsentStore; an app is using it right now when its
    /// LastUsedTimeStop is 0. A light registry poll every 1.5 s is plenty.
    /// </summary>
    public sealed class PrivacyService : IDisposable
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
        private readonly DispatcherTimer _timer;

        public event EventHandler? Changed;

        public IReadOnlyList<string> MicApps { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> CameraApps { get; private set; } = Array.Empty<string>();

        public PrivacyService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += (_, _) => Poll();
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                Poll();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                if (MicApps.Count > 0 || CameraApps.Count > 0)
                {
                    MicApps = Array.Empty<string>();
                    CameraApps = Array.Empty<string>();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void Poll()
        {
            var mic = ActiveApps("microphone");
            var cam = ActiveApps("webcam");
            if (mic.SequenceEqual(MicApps) && cam.SequenceEqual(CameraApps)) return;
            MicApps = mic;
            CameraApps = cam;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static List<string> ActiveApps(string capability)
        {
            var result = new List<string>();
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
                if (root == null) return result;

                foreach (string name in root.GetSubKeyNames())
                {
                    if (name == "NonPackaged")
                    {
                        using var np = root.OpenSubKey(name);
                        if (np == null) continue;
                        foreach (string exe in np.GetSubKeyNames())
                        {
                            using var k = np.OpenSubKey(exe);
                            if (InUse(k)) result.Add(FriendlyExe(exe));
                        }
                    }
                    else
                    {
                        using var k = root.OpenSubKey(name);
                        if (InUse(k)) result.Add(FriendlyPackage(name));
                    }
                }
            }
            catch
            {
                // registry not readable – report nothing
            }
            return result.Distinct().OrderBy(n => n).ToList();
        }

        private static bool InUse(RegistryKey? key)
        {
            if (key == null) return false;
            long start = Convert.ToInt64(key.GetValue("LastUsedTimeStart") ?? 0L);
            long stop = Convert.ToInt64(key.GetValue("LastUsedTimeStop") ?? 1L);
            return start > 0 && stop == 0;
        }

        /// <summary>"C:#Program Files#Zoom#bin#Zoom.exe" → "Zoom".</summary>
        private static string FriendlyExe(string key)
            => Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));

        /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".</summary>
        private static string FriendlyPackage(string key)
        {
            string n = key;
            int us = n.IndexOf('_');
            if (us > 0) n = n[..us];
            int dot = n.LastIndexOf('.');
            return dot >= 0 && dot < n.Length - 1 ? n[(dot + 1)..] : n;
        }

        public void Dispose() => _timer.Stop();
    }
}
