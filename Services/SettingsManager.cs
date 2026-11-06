using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Crest.Models;

namespace Crest.Services
{
    /// <summary>
    /// Loads / saves <see cref="AppSettings"/> as settings.json next to Crest.exe.
    /// Falls back to %AppData%\Crest if the exe folder is not writable (e.g. Program Files).
    /// Changes are auto-saved with a short debounce so dragging a slider doesn't hammer the disk.
    /// </summary>
    public sealed class SettingsManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly DispatcherTimer _saveTimer;

        public AppSettings Settings { get; }
        public string SettingsPath { get; }

        public SettingsManager()
        {
            SettingsPath = ResolveSettingsPath();
            Settings = Load(SettingsPath);

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };

            Settings.PropertyChanged += OnSettingChanged;
        }

        private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            // restart debounce
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Settings, JsonOptions);
                string tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                App.LogError("Saving settings failed", ex);
            }
        }

        /// <summary>Flushes any pending debounced save.</summary>
        public void Flush()
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                Save();
            }
        }

        private static AppSettings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                App.LogError("Loading settings failed, using defaults", ex);
            }
            return new AppSettings();
        }

        private static string ResolveSettingsPath()
        {
            string exeDir = AppContext.BaseDirectory;
            if (IsDirectoryWritable(exeDir))
                return Path.Combine(exeDir, "settings.json");

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crest");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "settings.json");
        }

        private static bool IsDirectoryWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".crest_write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
