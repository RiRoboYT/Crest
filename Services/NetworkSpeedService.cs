using System;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace Crest.Services
{
    /// <summary>Live download / upload speed of all network adapters. Runs only while something shows it.</summary>
    public sealed class NetworkSpeedService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private int _users;
        private long _lastRx = -1, _lastTx = -1;
        private DateTime _lastAt;

        public event EventHandler? Updated;

        /// <summary>Bytes per second.</summary>
        public double Down { get; private set; }
        public double Up { get; private set; }

        public NetworkSpeedService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Sample();
        }

        public void AddUser()
        {
            if (_users++ > 0) return;
            _lastRx = _lastTx = -1;
            Sample();
            _timer.Start();
        }

        public void RemoveUser()
        {
            if (_users == 0) return;
            if (--_users > 0) return;
            _timer.Stop();
        }

        private void Sample()
        {
            long rx = 0, tx = 0;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var st = nic.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
            }
            catch
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (_lastRx >= 0)
            {
                double seconds = Math.Max(0.2, (now - _lastAt).TotalSeconds);
                Down = Math.Max(0, (rx - _lastRx) / seconds);
                Up = Math.Max(0, (tx - _lastTx) / seconds);
                Updated?.Invoke(this, EventArgs.Empty);
            }
            _lastRx = rx;
            _lastTx = tx;
            _lastAt = now;
        }

        /// <summary>"820 KB/s", "12.4 MB/s".</summary>
        public static string Format(double bytesPerSecond)
        {
            if (bytesPerSecond < 1000) return "0 KB/s";
            double kb = bytesPerSecond / 1024;
            if (kb < 1000) return $"{kb:0} KB/s";
            double mb = kb / 1024;
            return mb < 100 ? $"{mb:0.0} MB/s" : $"{mb:0} MB/s";
        }

        public void Dispose() => _timer.Stop();
    }
}
