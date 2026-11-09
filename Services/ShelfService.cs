using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace Crest.Services
{
    /// <summary>
    /// The file shelf: files dragged onto the notch are parked here (by path – nothing is copied)
    /// until you drag them out again. Remembered between restarts; missing files drop off.
    /// </summary>
    public sealed class ShelfService
    {
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "shelf.json");
        private readonly List<string> _files = new();

        public event EventHandler? Changed;
        /// <summary>A new screenshot was put on the shelf (UI thread).</summary>
        public event Action<string>? ScreenshotAdded;

        public IReadOnlyList<string> Files => _files;

        private FileSystemWatcher? _screenshots;

        /// <summary>Puts every new screenshot (Win + PrtScn, Snipping Tool…) on the shelf.</summary>
        public void WatchScreenshots(bool enabled)
        {
            if (enabled == (_screenshots != null)) return;
            if (!enabled)
            {
                _screenshots!.Dispose();
                _screenshots = null;
                return;
            }

            string folder = ScreenshotsFolder();
            try
            {
                Directory.CreateDirectory(folder);
                _screenshots = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                };
                _screenshots.Created += OnScreenshotCreated;
                _screenshots.Renamed += (s, e) => OnScreenshotCreated(s, e);
                _screenshots.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                App.LogError("Watching the Screenshots folder failed", ex);
                _screenshots?.Dispose();
                _screenshots = null;
            }
        }

        private void OnScreenshotCreated(object sender, FileSystemEventArgs e)
        {
            string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            // Give the app a moment to finish writing the file.
            _ = System.Threading.Tasks.Task.Delay(700).ContinueWith(_ => dispatcher.InvokeAsync(() =>
            {
                if (!File.Exists(e.FullPath) || _files.Contains(e.FullPath, StringComparer.OrdinalIgnoreCase)) return;
                Add(new[] { e.FullPath });
                ScreenshotAdded?.Invoke(e.FullPath);
            }));
        }

        public static string ScreenshotsFolder()
        {
            try
            {
                if (Helpers.NativeMethods.SHGetKnownFolderPath(Helpers.NativeMethods.FOLDERID_Screenshots, 0, IntPtr.Zero, out var path) == 0
                    && !string.IsNullOrWhiteSpace(path))
                    return path;
            }
            catch
            {
                // older Windows – use the usual place
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }

        public ShelfService()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new List<string>();
                    _files.AddRange(saved.Where(Exists));
                }
            }
            catch (Exception ex)
            {
                App.LogError("Loading shelf failed", ex);
            }
        }

        public void Add(IEnumerable<string> paths)
        {
            bool changed = false;
            foreach (var p in paths)
            {
                if (!Exists(p) || _files.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                _files.Add(p);
                changed = true;
            }
            if (changed) Save();
        }

        public void Remove(string path)
        {
            if (_files.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
        }

        public void Clear()
        {
            if (_files.Count == 0) return;
            _files.Clear();
            Save();
        }

        /// <summary>Drops files that were moved / deleted elsewhere.</summary>
        public void Prune()
        {
            if (_files.RemoveAll(f => !Exists(f)) > 0) Save();
        }

        private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_files));
            }
            catch (Exception ex)
            {
                App.LogError("Saving shelf failed", ex);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The quick note on the Notes page, saved to %LocalAppData%\Crest\notes.txt.</summary>
    public sealed class NotesService
    {
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "notes.txt");
        private readonly DispatcherTimer _save;
        private string _text = "";

        /// <summary>(sender) – lets several notch windows stay in sync.</summary>
        public event Action<object?>? Changed;

        public NotesService()
        {
            try { if (File.Exists(_path)) _text = File.ReadAllText(_path); }
            catch (Exception ex) { App.LogError("Loading notes failed", ex); }

            _save = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
            _save.Tick += (_, _) => { _save.Stop(); Flush(); };
        }

        public string Text => _text;

        public void SetText(string text, object? source)
        {
            if (text == _text) return;
            _text = text;
            _save.Stop();
            _save.Start();
            Changed?.Invoke(source);
        }

        public void Flush()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, _text);
            }
            catch (Exception ex)
            {
                App.LogError("Saving notes failed", ex);
            }
        }
    }
}
