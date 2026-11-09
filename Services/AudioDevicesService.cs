using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Crest.Services
{
    /// <summary>A playback device (speakers, headphones, monitor…).</summary>
    public sealed record OutputDevice(string Id, string Name, bool IsDefault);

    /// <summary>One app in the volume mixer (all of its audio sessions together).</summary>
    public sealed class AppAudio
    {
        internal readonly List<AudioSessionControl> Sessions = new();

        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string? ExePath { get; init; }

        public float Volume
        {
            get
            {
                foreach (var s in Sessions)
                {
                    try { return s.SimpleAudioVolume.Volume; } catch { }
                }
                return 0;
            }
            set
            {
                foreach (var s in Sessions)
                {
                    try { s.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f); } catch { }
                }
            }
        }

        public bool Muted
        {
            get
            {
                foreach (var s in Sessions)
                {
                    try { return s.SimpleAudioVolume.Mute; } catch { }
                }
                return false;
            }
            set
            {
                foreach (var s in Sessions)
                {
                    try { s.SimpleAudioVolume.Mute = value; } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Lists playback devices, switches the default one, and gives per-app volume control
    /// (the Windows volume mixer) for the Sound page.
    /// </summary>
    public sealed class AudioDevicesService : IDisposable
    {
        private MMDeviceEnumerator? _enumerator;
        private readonly List<AppAudio> _apps = new();
        private MMDevice? _mixerDevice;

        private MMDeviceEnumerator? Enumerator
        {
            get
            {
                try { return _enumerator ??= new MMDeviceEnumerator(); }
                catch (Exception ex) { App.LogError("Audio devices unavailable", ex); return null; }
            }
        }

        public IReadOnlyList<OutputDevice> GetOutputs()
        {
            var list = new List<OutputDevice>();
            var en = Enumerator;
            if (en == null) return list;
            try
            {
                string defaultId = "";
                try
                {
                    var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def.ID;
                    (def as IDisposable)?.Dispose();
                }
                catch { }

                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    string name;
                    try { name = d.FriendlyName; } catch { name = "Speakers"; }
                    list.Add(new OutputDevice(d.ID, name, d.ID == defaultId));
                    (d as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex)
            {
                App.LogError("Listing audio outputs failed", ex);
            }
            return list.OrderByDescending(o => o.IsDefault).ThenBy(o => o.Name).ToList();
        }

        /// <summary>Makes <paramref name="id"/> the default playback device for every app.</summary>
        public bool SetDefaultOutput(string id)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                try
                {
                    // eConsole, eMultimedia, eCommunications
                    for (int role = 0; role <= 2; role++)
                        Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
                }
                finally
                {
                    Marshal.ReleaseComObject(policy);
                }
                return true;
            }
            catch (Exception ex)
            {
                App.LogError("Switching the audio output failed", ex);
                return false;
            }
        }

        /// <summary>Apps currently using the default playback device, loudest-relevant first.</summary>
        public IReadOnlyList<AppAudio> GetApps()
        {
            ReleaseApps();
            var en = Enumerator;
            if (en == null) return _apps;
            try
            {
                _mixerDevice = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var manager = _mixerDevice.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                var byKey = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                        {
                            (s as IDisposable)?.Dispose();
                            continue;
                        }
                        int pid = (int)s.GetProcessID;
                        var (key, name, path) = Describe(pid, s.DisplayName);
                        if (!byKey.TryGetValue(key, out var app))
                        {
                            app = new AppAudio { Key = key, Name = name, ExePath = path };
                            byKey[key] = app;
                            _apps.Add(app);
                        }
                        app.Sessions.Add(s);
                    }
                    catch
                    {
                        (s as IDisposable)?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError("Reading the volume mixer failed", ex);
            }
            _apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return _apps;
        }

        private static (string Key, string Name, string? Path) Describe(int pid, string displayName)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string? path = null;
                string name = p.ProcessName;
                try
                {
                    path = p.MainModule?.FileName;
                    var description = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) name = description!;
                }
                catch
                {
                    // elevated / protected process – keep the process name
                }
                if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@")) name = displayName;
                return (p.ProcessName, Tidy(name), path);
            }
            catch
            {
                string n = string.IsNullOrWhiteSpace(displayName) ? "App " + pid : displayName;
                return ("pid" + pid, n, null);
            }
        }

        private static string Tidy(string name)
        {
            name = name.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
            return name.Length == 0 ? "App" : char.ToUpper(name[0]) + name[1..];
        }

        private void ReleaseApps()
        {
            foreach (var a in _apps)
                foreach (var s in a.Sessions) (s as IDisposable)?.Dispose();
            _apps.Clear();
            (_mixerDevice as IDisposable)?.Dispose();
            _mixerDevice = null;
        }

        public void Dispose()
        {
            ReleaseApps();
            (_enumerator as IDisposable)?.Dispose();
        }

        // ---------------------------------------------------------------- undocumented but stable
        // IPolicyConfig is what Windows' own sound settings use to change the default device.
        // Only SetDefaultEndpoint is called; the other slots just keep the vtable layout right.

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, int useDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, int visible);
        }
    }
}
