using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Crest.Helpers;
using Crest.Models;
using Crest.Services;
using Microsoft.Win32;

namespace Crest
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private SettingsManager? _settingsManager;
        private CrestServices? _services;
        private TrayIconService? _tray;
        private SettingsWindow? _settingsWindow;
        private TourWindow? _tour;
        private readonly List<MainWindow> _notches = new();
        private bool _notchesVisible = true;
        private string _monitorLayout = "";

        private static readonly object LogLock = new();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "crest-error.log");

        /// <summary>Location of the error log (shown in Settings → About).</summary>
        public static string LogFilePath => LogPath;

        /// <summary>Every notch window (one, or one per monitor).</summary>
        public IReadOnlyList<MainWindow> Notches => _notches;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- single instance ----
            _singleInstance = new Mutex(initiallyOwned: true, @"Local\Crest.Notch.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown();
                return;
            }

            // Modern look for the WinForms bits (tray menu, color picker).
            System.Windows.Forms.Application.EnableVisualStyles();

            // A cosmetic overlay should never crash the user's session; log and keep going.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogError("Unhandled exception", args.ExceptionObject as Exception);

            // ---- settings ----
            _settingsManager = new SettingsManager();
            AppSettings settings = _settingsManager.Settings;

            // Settings are the source of truth. Re-writing also fixes the path if the exe was moved.
            StartupManager.SetEnabled(settings.StartWithWindows);
            settings.PropertyChanged += OnSettingsPropertyChanged;

            // ---- shared services ----
            _services = new CrestServices(Dispatcher);
            _services.Media.PreferPlayingSession = settings.PreferPlayingSession;
            _services.Hotkey.Pressed += (_, _) => _notches.ForEach(n => n.ToggleFromHotkey());
            _services.Timer.Finished += (_, _) =>
            {
                if (settings.TimerSound) System.Media.SystemSounds.Exclamation.Play();
            };
            _services.Hotkey.MicPressed += (_, _) => _services.Mic.ToggleMute();
            _services.Timer.FocusPhaseChanged += (_, _) =>
            {
                if (settings.TimerSound) System.Media.SystemSounds.Asterisk.Play();
            };
            _services.Timer.FocusPhaseChanged += (_, _) =>
            {
                // A break just started → the work block before it is done.
                if (_services.Timer.IsBreak) _services.FocusStats.AddMinutes((int)_services.Timer.FocusWork.TotalMinutes);
            };
            _services.Downloads.Finished += path =>
            {
                if (settings.DownloadsToShelf && settings.PageShelf) _services.Shelf.Add(new[] { path });
            };
            _services.Hotkey.SetEnabled(settings.HotkeyEnabled);
            _services.Start();
            ApplySharedSettings(settings);

            // ---- notch window(s) ----
            RebuildNotches();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // ---- tray ----
            _tray = new TrayIconService(settings);
            _tray.SettingsRequested += (_, _) => ShowSettings();
            _tray.VisibilityToggled += (_, visible) =>
            {
                _notchesVisible = visible;
                _notches.ForEach(n => n.SetNotchVisible(visible));
            };
            _tray.ExitRequested += (_, _) => ExitApplication();

            // ---- idle memory trim ----
            var trim = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
            trim.Tick += (_, _) => TrimMemoryIfIdle();
            trim.Start();

            // ---- first run: a short tour ----
            if (!settings.TourShown)
            {
                var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                delay.Tick += (_, _) => { delay.Stop(); ShowTour(); };
                delay.Start();
            }
        }

        /// <summary>Settings that drive the shared services (not a particular notch window).</summary>
        private void ApplySharedSettings(AppSettings s)
        {
            if (_services == null) return;
            _services.Hotkey.SetMicEnabled(s.MicMuteHotkey);
            _services.LockKeys.SetEnabled(s.LockKeysActivity);
            _services.Shelf.WatchScreenshots(s.ScreenshotsToShelf);
            _services.Clipboard.SetImagesEnabled(s.ClipboardImages);
            _services.Timer.ConfigureFocus(s.FocusWorkMinutes, s.FocusBreakMinutes);
            _services.Downloads.SetEnabled(s.DownloadActivity || s.DownloadsToShelf);
        }

        /// <summary>While every notch is closed and nothing plays, hand unused memory back to Windows.</summary>
        private void TrimMemoryIfIdle()
        {
            if (_settingsManager?.Settings.TrimMemory != true || _services == null) return;
            if (_settingsWindow != null || _tour != null || _services.Media.IsPlaying) return;
            if (_notches.Any(n => n.State != NotchState.Collapsed)) return;
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
                NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch (Exception ex)
            {
                LogError("Trimming memory failed", ex);
            }
        }

        /// <summary>Shows the three-step welcome tour under the notch (first run, or Settings → About).</summary>
        public void ShowTour()
        {
            if (_settingsManager == null) return;
            if (_tour != null) { _tour.Activate(); return; }
            var s = _settingsManager.Settings;

            // Under the notch on the monitor it lives on.
            var monitor = MonitorHelper.Find(s.MonitorDevice == "*" ? "" : s.MonitorDevice);
            double centerX, top;
            if (monitor != null)
            {
                double scale = monitor.Scale > 0 ? monitor.Scale : 1.0;
                centerX = (monitor.Bounds.Left + monitor.Bounds.Right) / 2.0 / scale + s.HorizontalOffset;
                top = monitor.Bounds.Top / scale + s.TopMargin + s.CollapsedHeight + 6;
            }
            else
            {
                centerX = SystemParameters.PrimaryScreenWidth / 2 + s.HorizontalOffset;
                top = s.TopMargin + s.CollapsedHeight + 6;
            }

            _tour = new TourWindow(centerX, top);
            _tour.Finished += (_, _) => s.TourShown = true;
            _tour.Closed += (_, _) => _tour = null;
            _tour.Show();
            _tour.Activate();
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not AppSettings s) return;
            switch (e.PropertyName)
            {
                case nameof(AppSettings.StartWithWindows):
                    StartupManager.SetEnabled(s.StartWithWindows);
                    break;
                case nameof(AppSettings.HotkeyEnabled):
                    _services?.Hotkey.SetEnabled(s.HotkeyEnabled);
                    break;
                case nameof(AppSettings.MicMuteHotkey):
                case nameof(AppSettings.LockKeysActivity):
                case nameof(AppSettings.ScreenshotsToShelf):
                case nameof(AppSettings.ClipboardImages):
                case nameof(AppSettings.FocusWorkMinutes):
                case nameof(AppSettings.FocusBreakMinutes):
                case nameof(AppSettings.DownloadActivity):
                case nameof(AppSettings.DownloadsToShelf):
                    ApplySharedSettings(s);
                    break;
                case nameof(AppSettings.MonitorDevice):
                    // Switching to / from "every monitor" changes how many windows exist.
                    if (MonitorLayoutKey() != _monitorLayout) RebuildNotches();
                    break;
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            // Monitors plugged in / out while showing a notch on every monitor.
            Dispatcher.InvokeAsync(() =>
            {
                if (MonitorLayoutKey() != _monitorLayout) RebuildNotches();
            }, DispatcherPriority.Background);
        }

        /// <summary>"single" or the list of monitors when a notch is shown on each one.</summary>
        private string MonitorLayoutKey()
        {
            if (_settingsManager?.Settings.MonitorDevice != "*") return "single";
            return "all:" + string.Join("|", MonitorHelper.GetMonitors().Select(m => m.DeviceName));
        }

        private void RebuildNotches()
        {
            if (_settingsManager == null || _services == null) return;
            var settings = _settingsManager.Settings;

            foreach (var n in _notches) n.Close();
            _notches.Clear();

            _monitorLayout = MonitorLayoutKey();
            if (settings.MonitorDevice == "*")
            {
                foreach (var m in MonitorHelper.GetMonitors())
                    _notches.Add(new MainWindow(settings, _services, m.DeviceName));
            }
            if (_notches.Count == 0)
                _notches.Add(new MainWindow(settings, _services));

            foreach (var n in _notches)
            {
                n.SettingsRequested += (_, _) => ShowSettings();
                n.Show();
                if (!_notchesVisible) n.SetNotchVisible(false);
            }
        }

        public void ShowSettings()
        {
            if (_settingsManager == null || _services == null) return;

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_settingsManager, _services.Media);
                _settingsWindow.Closed += (_, _) =>
                {
                    _settingsWindow = null;
                    _settingsManager.Flush();
                };
                _settingsWindow.Show();
            }

            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        public void ExitApplication()
        {
            _tour?.Close();
            _settingsWindow?.Close();
            foreach (var n in _notches.ToList()) n.Close();
            _notches.Clear();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            try { _settingsManager?.Flush(); } catch { /* ignore */ }
            _tray?.Dispose();
            _services?.Dispose();

            if (_singleInstance != null)
            {
                try { _singleInstance.ReleaseMutex(); } catch { /* ignore */ }
                _singleInstance.Dispose();
            }
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogError("UI exception", e.Exception);
            e.Handled = true;
        }

        /// <summary>Tiny append-only error log in %LocalAppData%\Crest (capped at 256 KB).</summary>
        public static void LogError(string message, Exception? ex)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > 256 * 1024) info.Delete();
                    File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}: {ex}{Environment.NewLine}");
                }
            }
            catch
            {
                // logging must never throw
            }
            System.Diagnostics.Debug.WriteLine($"Crest: {message}: {ex}");
        }
    }
}
