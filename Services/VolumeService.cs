using System;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Crest.Services
{
    /// <summary>
    /// Reads and sets the Windows master volume of the default playback device (Core Audio via NAudio).
    /// Follows the default device when it changes (e.g. plugging in headphones) and reports volume /
    /// mute changes made anywhere in Windows.
    /// </summary>
    public sealed class VolumeService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private DeviceNotificationClient? _client;
        private bool _disposed;

        /// <summary>Volume or mute changed (UI thread).</summary>
        public event EventHandler? Changed;

        /// <summary>The default playback device changed (UI thread).</summary>
        public event EventHandler? DefaultDeviceChanged;

        public bool IsAvailable => _device != null;

        /// <summary>0..1</summary>
        public float Volume { get; private set; }
        public bool IsMuted { get; private set; }

        public VolumeService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void Initialize()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _client = new DeviceNotificationClient(this);
                _enumerator.RegisterEndpointNotificationCallback(_client);
                AttachDefaultDevice();
            }
            catch (Exception ex)
            {
                App.LogError("Core Audio unavailable", ex);
            }
        }

        public void SetVolume(float value)
        {
            var d = _device;
            if (d == null) return;
            try
            {
                value = Math.Clamp(value, 0f, 1f);
                d.AudioEndpointVolume.MasterVolumeLevelScalar = value;
                if (value > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
                Volume = value;
            }
            catch (Exception ex)
            {
                App.LogError("Setting volume failed", ex);
            }
        }

        public void Step(float delta) => SetVolume(Volume + delta);

        public void ToggleMute()
        {
            var d = _device;
            if (d == null) return;
            try
            {
                d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                App.LogError("Toggling mute failed", ex);
            }
        }

        private void AttachDefaultDevice()
        {
            DetachDevice();
            if (_disposed || _enumerator == null) return;

            try
            {
                if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                    Volume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                    IsMuted = _device.AudioEndpointVolume.Mute;
                }
            }
            catch (Exception ex)
            {
                App.LogError("Opening default audio device failed", ex);
                DetachDevice();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void DetachDevice()
        {
            var d = _device;
            _device = null;
            if (d == null) return;
            try
            {
                d.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                d.Dispose();
            }
            catch
            {
                // device may already be gone
            }
        }

        // Called on a Core Audio worker thread.
        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            float volume = data.MasterVolume;
            bool muted = data.Muted;
            _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                Volume = volume;
                IsMuted = muted;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }

        private void OnDefaultChanged()
        {
            if (_disposed) return;
            AttachDefaultDevice();
            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _disposed = true;
            DetachDevice();
            try
            {
                if (_enumerator != null && _client != null)
                    _enumerator.UnregisterEndpointNotificationCallback(_client);
                _enumerator?.Dispose();
            }
            catch
            {
                // ignore on shutdown
            }
            _enumerator = null;
        }

        /// <summary>Receives Core Audio device notifications (background thread).</summary>
        private sealed class DeviceNotificationClient : IMMNotificationClient
        {
            private readonly VolumeService _owner;
            public DeviceNotificationClient(VolumeService owner) => _owner = owner;

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && role == Role.Multimedia)
                    _owner._dispatcher.InvokeAsync(_owner.OnDefaultChanged);
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
