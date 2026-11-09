using System;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Crest.Services
{
    /// <summary>Battery / charger state. Raises <see cref="ChargerChanged"/> when the charger is plugged in or out.</summary>
    public sealed class PowerService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private bool _lastOnAc;

        public event EventHandler? ChargerChanged;

        public bool HasBattery { get; private set; }
        public bool OnAc { get; private set; }
        public int Percent { get; private set; } = -1;

        public PowerService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Read();
            _lastOnAc = OnAc;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        public void Read()
        {
            try
            {
                var ps = Forms.SystemInformation.PowerStatus;
                var status = ps.BatteryChargeStatus;
                HasBattery = !status.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) && status != Forms.BatteryChargeStatus.Unknown;
                OnAc = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
                Percent = HasBattery ? (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 100) : -1;
            }
            catch
            {
                HasBattery = false;
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.StatusChange) return;
            _dispatcher.InvokeAsync(() =>
            {
                Read();
                if (!HasBattery || OnAc == _lastOnAc) return;
                _lastOnAc = OnAc;
                ChargerChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
