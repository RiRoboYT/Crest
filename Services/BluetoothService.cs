using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace Crest.Services
{
    /// <summary>
    /// Watches paired Bluetooth devices and reports when headphones / speakers connect,
    /// including their battery level when Windows knows it.
    /// </summary>
    public sealed class BluetoothService : IDisposable
    {
        // Paired classic Bluetooth devices.
        private const string Selector =
            "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
        private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
        private const string MajorClassKey = "System.Devices.Aep.Bluetooth.Cod.Major";
        private const string ContainerKey = "System.Devices.Aep.ContainerId";
        // DEVPKEY_Bluetooth_Battery – the % Windows shows in Settings → Bluetooth.
        private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private const int AudioVideoMajorClass = 4;

        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<string, Device> _devices = new();
        private DeviceWatcher? _watcher;
        private bool _enumerated;

        private sealed class Device
        {
            public string Name = "";
            public bool Connected;
            public int Major = -1;
            public Guid? Container;
        }

        /// <summary>(device name, battery % or -1)</summary>
        public event Action<string, int>? AudioDeviceConnected;

        public BluetoothService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void Start()
        {
            if (_watcher != null) return;
            try
            {
                _watcher = DeviceInformation.CreateWatcher(Selector,
                    new[] { IsConnectedKey, MajorClassKey, ContainerKey },
                    DeviceInformationKind.AssociationEndpoint);
                _watcher.Added += (_, info) => _dispatcher.InvokeAsync(() => OnAdded(info));
                _watcher.Updated += (_, update) => _dispatcher.InvokeAsync(() => OnUpdated(update));
                _watcher.Removed += (_, update) => _dispatcher.InvokeAsync(() => _devices.Remove(update.Id));
                _watcher.EnumerationCompleted += (_, _) => _dispatcher.InvokeAsync(() => _enumerated = true);
                _watcher.Start();
            }
            catch (Exception ex)
            {
                App.LogError("Bluetooth watcher unavailable", ex);
                _watcher = null;
            }
        }

        private void OnAdded(DeviceInformation info)
        {
            var d = new Device { Name = info.Name ?? "" };
            Apply(d, info.Properties);
            _devices[info.Id] = d;
        }

        private void OnUpdated(DeviceInformationUpdate update)
        {
            if (!_devices.TryGetValue(update.Id, out var d)) return;
            bool wasConnected = d.Connected;
            Apply(d, update.Properties);

            if (_enumerated && !wasConnected && d.Connected && (d.Major == -1 || d.Major == AudioVideoMajorClass))
                _ = AnnounceAsync(d);
        }

        private static void Apply(Device d, IReadOnlyDictionary<string, object> props)
        {
            if (props.TryGetValue(IsConnectedKey, out var c) && c is bool connected) d.Connected = connected;
            if (props.TryGetValue(MajorClassKey, out var m) && m != null)
            {
                try { d.Major = Convert.ToInt32(m); } catch { /* ignore */ }
            }
            if (props.TryGetValue(ContainerKey, out var g) && g is Guid guid) d.Container = guid;
        }

        private async Task AnnounceAsync(Device d)
        {
            int battery = -1;
            // Battery is reported a moment after the connection on many headsets.
            await Task.Delay(1500);
            if (d.Container is Guid container)
            {
                try
                {
                    var found = await DeviceInformation.FindAllAsync(
                        $"System.Devices.ContainerId:=\"{{{container}}}\"",
                        new[] { BatteryKey }, DeviceInformationKind.Device);
                    foreach (var dev in found)
                    {
                        if (dev.Properties.TryGetValue(BatteryKey, out var v) && v != null)
                        {
                            battery = Convert.ToInt32(v);
                            break;
                        }
                    }
                }
                catch
                {
                    // battery not exposed by this device
                }
            }
            AudioDeviceConnected?.Invoke(d.Name, battery);
        }

        public void Dispose()
        {
            try
            {
                if (_watcher != null && (_watcher.Status == DeviceWatcherStatus.Started || _watcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                    _watcher.Stop();
            }
            catch
            {
                // ignore on shutdown
            }
            _watcher = null;
        }
    }
}
