using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>
    /// CPU, RAM, GPU and disk usage for the System page. Only samples while a System page is
    /// visible (reference-counted), once per second.
    /// </summary>
    public sealed class SystemStatsService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private int _users;
        private ulong _lastIdle, _lastTotal;
        private List<PerformanceCounter>? _gpu;
        private DateTime _gpuBuilt = DateTime.MinValue;
        private bool _gpuUnavailable;

        public event EventHandler? Updated;

        public double Cpu { get; private set; }
        public double Ram { get; private set; }
        public double RamUsedGb { get; private set; }
        public double RamTotalGb { get; private set; }
        public double? Gpu { get; private set; }
        public double Disk { get; private set; }
        public double DiskFreeGb { get; private set; }
        public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

        public SystemStatsService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Sample();
        }

        public void AddUser()
        {
            if (_users++ == 0)
            {
                Sample();
                _timer.Start();
            }
        }

        public void RemoveUser()
        {
            if (_users == 0) return;
            if (--_users == 0) _timer.Stop();
        }

        private void Sample()
        {
            // ---- CPU (system-wide, from idle vs total time) ----
            if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong idleT = idle.Value, total = kernel.Value + user.Value;   // kernel time includes idle
                if (_lastTotal != 0 && total > _lastTotal)
                    Cpu = Math.Clamp(100.0 * (1 - (double)(idleT - _lastIdle) / (total - _lastTotal)), 0, 100);
                _lastIdle = idleT;
                _lastTotal = total;
            }

            // ---- RAM ----
            var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
            {
                RamTotalGb = mem.ullTotalPhys / 1073741824.0;
                RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                Ram = 100.0 * RamUsedGb / RamTotalGb;
            }

            // ---- Disk (system drive) ----
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    DiskFreeGb = drive.AvailableFreeSpace / 1073741824.0;
                    Disk = 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
                }
            }
            catch
            {
                // drive not readable
            }

            Gpu = SampleGpu();
            Updated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>3D engine utilisation – the same number Task Manager shows for "GPU".</summary>
        private double? SampleGpu()
        {
            if (_gpuUnavailable) return null;
            try
            {
                // Engine instances come and go with processes – rebuild the list every 10 s.
                if (_gpu == null || DateTime.Now - _gpuBuilt > TimeSpan.FromSeconds(10))
                {
                    DisposeGpu();
                    var category = new PerformanceCounterCategory("GPU Engine");
                    _gpu = category.GetInstanceNames()
                        .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                        .ToList();
                    foreach (var c in _gpu) c.NextValue();   // first read is always 0
                    _gpuBuilt = DateTime.Now;
                    return Gpu;
                }

                double sum = 0;
                foreach (var c in _gpu)
                {
                    try { sum += c.NextValue(); }
                    catch { /* instance ended */ }
                }
                return Math.Clamp(sum, 0, 100);
            }
            catch
            {
                _gpuUnavailable = true;   // counters not available on this machine
                DisposeGpu();
                return null;
            }
        }

        private void DisposeGpu()
        {
            if (_gpu == null) return;
            foreach (var c in _gpu) c.Dispose();
            _gpu = null;
        }

        public void Dispose()
        {
            _timer.Stop();
            DisposeGpu();
        }
    }
}
