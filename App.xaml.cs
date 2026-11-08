using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Crest.Models;
using Crest.Services;

namespace Crest
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private SettingsManager? _settingsManager;
        private MediaSessionService? _media;
        private VolumeService? _volume;
        private AudioSpectrumService? _analyzer;
        private TrayIconService? _tray;
        private MainWindow? _notch;
        private SettingsWindow? _settingsWindow;

        private static readonly object LogLock = new();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "crest-error.log");

        /// <summary>Location of the error log (shown in Settings → About).</summary>
        public static string LogFilePath => LogPath;

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

            // ---- media, audio + notch ----
            _media = new MediaSessionService(Dispatcher) { PreferPlayingSession = settings.PreferPlayingSession };
            _volume = new VolumeService(Dispatcher);
            _volume.Initialize();
            _analyzer = new AudioSpectrumService();

            _notch = new MainWindow(settings, _media, _volume, _analyzer);
            _notch.SettingsRequested += (_, _) => ShowSettings();
            _notch.Show();

            // ---- tray ----
            _tray = new TrayIconService(settings);
            _tray.SettingsRequested += (_, _) => ShowSettings();
            _tray.VisibilityToggled += (_, visible) => _notch?.SetNotchVisible(visible);
            _tray.ExitRequested += (_, _) => ExitApplication();

            _ = _media.InitializeAsync();
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppSettings.StartWithWindows) && sender is AppSettings s)
                StartupManager.SetEnabled(s.StartWithWindows);
        }

        public void ShowSettings()
        {
            if (_settingsManager == null || _media == null) return;

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_settingsManager, _media);
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
            _settingsWindow?.Close();
            _notch?.Close();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _settingsManager?.Flush(); } catch { /* ignore */ }
            _tray?.Dispose();
            _analyzer?.Dispose();
            _volume?.Dispose();
            _media?.Dispose();

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
