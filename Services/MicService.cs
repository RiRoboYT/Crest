using System;
using System.Collections.Generic;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Crest.Services
{
    /// <summary>
    /// Mutes / unmutes the default microphone(s) – the same switch as the mute box in Windows'
    /// sound settings, so it works in every app (Discord, Teams, Zoom, games…).
    /// </summary>
    public sealed class MicService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private MMDeviceEnumerator? _enumerator;
        private readonly List<MMDevice> _devices = new();
        private DispatcherTimer? _refresh;

        /// <summary>Mute state changed (UI thread). The bool says whether Crest itself changed it.</summary>
        public event Action<bool>? Changed;

        public bool HasMic => _devices.Count > 0;
        public bool IsMuted { get; private set; }

        public MicService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void Initialize()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                Bind();
            }
            catch (Exception ex)
            {
                App.LogError("Microphone init failed", ex);
            }

            // The default microphone can change (headset plugged in); re-check now and then.
            _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
            _refresh.Tick += (_, _) => Bind();
            _refresh.Start();
        }

        /// <summary>Follows the default "communications" and "console" microphones.</summary>
        private void Bind()
        {
            if (_enumerator == null) return;
            var ids = new List<string>();
            var fresh = new List<MMDevice>();
            foreach (var role in new[] { Role.Communications, Role.Console })
            {
                try
                {
                    var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    if (ids.Contains(d.ID)) { (d as IDisposable)?.Dispose(); continue; }
                    ids.Add(d.ID);
                    fresh.Add(d);
                }
                catch
                {
                    // no microphone for this role
                }
            }

            bool same = fresh.Count == _devices.Count;
            for (int i = 0; same && i < fresh.Count; i++) same = fresh[i].ID == _devices[i].ID;
            if (same)
            {
                foreach (var d in fresh) (d as IDisposable)?.Dispose();
                ReadState(fromCrest: false);
                return;
            }

            Unbind();
            _devices.AddRange(fresh);
            foreach (var d in _devices)
            {
                try { d.AudioEndpointVolume.OnVolumeNotification += OnNotification; }
                catch { /* device went away */ }
            }
            ReadState(fromCrest: false);
        }

        private void Unbind()
        {
            foreach (var d in _devices)
            {
                try { d.AudioEndpointVolume.OnVolumeNotification -= OnNotification; } catch { }
                (d as IDisposable)?.Dispose();
            }
            _devices.Clear();
        }

        private void OnNotification(AudioVolumeNotificationData data)
            => _dispatcher.BeginInvoke(new Action(() => ReadState(fromCrest: false)));

        private void ReadState(bool fromCrest)
        {
            bool muted = false;
            try { muted = _devices.Count > 0 && _devices[0].AudioEndpointVolume.Mute; }
            catch { /* device went away */ }
            if (muted == IsMuted && !fromCrest) return;
            IsMuted = muted;
            Changed?.Invoke(fromCrest);
        }

        public void ToggleMute() => SetMuted(!IsMuted);

        public void SetMuted(bool muted)
        {
            Bind();
            if (_devices.Count == 0) return;
            foreach (var d in _devices)
            {
                try { d.AudioEndpointVolume.Mute = muted; }
                catch (Exception ex) { App.LogError("Muting the microphone failed", ex); }
            }
            IsMuted = !muted;   // force a change notification
            ReadState(fromCrest: true);
        }

        public void Dispose()
        {
            _refresh?.Stop();
            Unbind();
            (_enumerator as IDisposable)?.Dispose();
        }
    }
}
