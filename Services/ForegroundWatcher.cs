using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>
    /// One shared hook that tells every notch window when another app comes to the front:
    ///  * <see cref="Switched"/> fires on every foreground change (used to re-assert "topmost").
    ///  * <see cref="Changed"/> fires when the foreground process or its fullscreen state changes
    ///    (used for per-app rules and "quiet in fullscreen").
    /// </summary>
    public sealed class ForegroundWatcher : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly NativeMethods.WinEventDelegate _proc;   // keep alive while hooked
        private readonly DispatcherTimer _recheck;
        private readonly Dictionary<uint, string> _names = new();
        private IntPtr _hook;
        private readonly int _ownPid = Environment.ProcessId;

        public event EventHandler? Switched;
        public event EventHandler? Changed;

        /// <summary>Process name of the foreground window, e.g. "chrome" (no .exe).</summary>
        public string ProcessName { get; private set; } = "";

        /// <summary>True when the foreground window covers its whole monitor (video / game).</summary>
        public bool IsFullscreen { get; private set; }

        /// <summary>Monitor (HMONITOR) the foreground window is on.</summary>
        public IntPtr Monitor { get; private set; }

        public ForegroundWatcher(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _proc = OnWinEvent;

            // Videos can go fullscreen without a foreground change (YouTube "F"), so re-check slowly.
            _recheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
            _recheck.Tick += (_, _) => Evaluate(raiseSwitched: false);
        }

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            _recheck.Start();
            Evaluate(raiseSwitched: false);
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            => _dispatcher.InvokeAsync(() => Evaluate(raiseSwitched: true));

        private void Evaluate(bool raiseSwitched)
        {
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                string name = "";
                bool fullscreen = false;
                IntPtr monitor = IntPtr.Zero;

                if (fg != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
                    name = pid == _ownPid ? "crest" : ProcessNameOf(pid);
                    monitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
                    fullscreen = pid != _ownPid && IsFullscreenWindow(fg, monitor);
                }

                bool changed = name != ProcessName || fullscreen != IsFullscreen || monitor != Monitor;
                ProcessName = name;
                IsFullscreen = fullscreen;
                Monitor = monitor;

                if (raiseSwitched) Switched?.Invoke(this, EventArgs.Empty);
                if (changed) Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.LogError("Foreground check failed", ex);
            }
        }

        private string ProcessNameOf(uint pid)
        {
            if (_names.TryGetValue(pid, out string? cached)) return cached;
            string name = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch
            {
                // process exited or access denied
            }
            if (_names.Count > 200) _names.Clear();
            _names[pid] = name;
            return name;
        }

        private static bool IsFullscreenWindow(IntPtr hwnd, IntPtr monitor)
        {
            if (hwnd == NativeMethods.GetShellWindow() || hwnd == NativeMethods.GetDesktopWindow()) return false;
            string cls = NativeMethods.GetWindowClass(hwnd);
            if (cls is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;

            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
            var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

            var m = info.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }

        public void Dispose()
        {
            _recheck.Stop();
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
