using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    // =========================================================================
    //  To-do list (Notes page)
    // =========================================================================

    public sealed class TodoItem
    {
        public string Text { get; set; } = "";
        public bool Done { get; set; }
    }

    /// <summary>A small checklist, saved next to the quick note.</summary>
    public sealed class TodoService
    {
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "todo.json");
        private readonly List<TodoItem> _items = new();

        public event EventHandler? Changed;

        public IReadOnlyList<TodoItem> Items => _items;

        public TodoService()
        {
            try
            {
                if (File.Exists(_path))
                    _items.AddRange(JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(_path)) ?? new List<TodoItem>());
            }
            catch (Exception ex)
            {
                App.LogError("Loading to-do list failed", ex);
            }
        }

        public void Add(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 200) text = text[..200];
            _items.Insert(0, new TodoItem { Text = text });
            if (_items.Count > 50) _items.RemoveRange(50, _items.Count - 50);
            Save();
        }

        public void Toggle(TodoItem item)
        {
            item.Done = !item.Done;
            // Finished things sink to the bottom.
            var ordered = _items.OrderBy(i => i.Done ? 1 : 0).ToList();
            _items.Clear();
            _items.AddRange(ordered);
            Save();
        }

        public void Remove(TodoItem item)
        {
            if (_items.Remove(item)) Save();
        }

        public void ClearDone()
        {
            if (_items.RemoveAll(i => i.Done) > 0) Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_items));
            }
            catch (Exception ex)
            {
                App.LogError("Saving to-do list failed", ex);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // =========================================================================
    //  Focus statistics
    // =========================================================================

    /// <summary>Minutes of finished focus blocks per day (for "today", "this week" and the streak).</summary>
    public sealed class FocusStatsService
    {
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest", "focus-stats.json");
        private readonly Dictionary<string, int> _days = new();

        public event EventHandler? Changed;

        public FocusStatsService()
        {
            try
            {
                if (File.Exists(_path))
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path)) ?? new Dictionary<string, int>())
                        _days[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                App.LogError("Loading focus stats failed", ex);
            }
        }

        private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public int Today => _days.TryGetValue(Key(DateTime.Today), out int m) ? m : 0;

        /// <summary>The last 7 days including today.</summary>
        public int Week
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < 7; i++)
                    if (_days.TryGetValue(Key(DateTime.Today.AddDays(-i)), out int m)) sum += m;
                return sum;
            }
        }

        /// <summary>Days in a row with at least one finished focus block (today may still be open).</summary>
        public int Streak
        {
            get
            {
                int streak = 0;
                var day = DateTime.Today;
                if (!_days.ContainsKey(Key(day))) day = day.AddDays(-1);   // today not done yet doesn't break it
                while (_days.TryGetValue(Key(day), out int m) && m > 0)
                {
                    streak++;
                    day = day.AddDays(-1);
                }
                return streak;
            }
        }

        public void AddMinutes(int minutes)
        {
            if (minutes <= 0) return;
            string key = Key(DateTime.Today);
            _days[key] = (_days.TryGetValue(key, out int m) ? m : 0) + minutes;

            // Keep a year at most.
            string cutoff = Key(DateTime.Today.AddDays(-370));
            foreach (var old in _days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) _days.Remove(old);
            Save();
        }

        public void Reset()
        {
            _days.Clear();
            Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_days));
            }
            catch (Exception ex)
            {
                App.LogError("Saving focus stats failed", ex);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public static string FormatMinutes(int minutes)
            => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    // =========================================================================
    //  Browser downloads
    // =========================================================================

    /// <summary>
    /// Watches the Downloads folder. Browsers write to a temporary file first (".crdownload" for
    /// Chrome / Edge / Brave / Opera, ".part" for Firefox) and rename it when done – that is how a
    /// download in progress and its end are recognised. The total size isn't known from outside,
    /// so progress is "how much has arrived so far".
    /// </summary>
    public sealed class DownloadWatcherService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _poll;
        private FileSystemWatcher? _watcher;
        private readonly Dictionary<string, DateTime> _active = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Something started, grew or stopped (UI thread).</summary>
        public event EventHandler? Changed;
        /// <summary>A download finished: the final file path (UI thread).</summary>
        public event Action<string>? Finished;

        public bool IsDownloading => _active.Count > 0;
        /// <summary>Bytes received so far, all running downloads together.</summary>
        public long ReceivedBytes { get; private set; }
        public int Count => _active.Count;

        public DownloadWatcherService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _poll.Tick += (_, _) => Poll();
        }

        public static string DownloadsFolder()
        {
            try
            {
                if (NativeMethods.SHGetKnownFolderPath(NativeMethods.FOLDERID_Downloads, 0, IntPtr.Zero, out var path) == 0
                    && !string.IsNullOrWhiteSpace(path))
                    return path;
            }
            catch
            {
                // fall back below
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled == (_watcher != null)) return;
            if (!enabled)
            {
                _watcher!.Dispose();
                _watcher = null;
                _poll.Stop();
                _active.Clear();
                ReceivedBytes = 0;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            try
            {
                string folder = DownloadsFolder();
                if (!Directory.Exists(folder)) return;
                _watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName
                };
                _watcher.Created += (_, e) => _dispatcher.InvokeAsync(() => OnCreated(e.FullPath));
                _watcher.Renamed += (_, e) => _dispatcher.InvokeAsync(() => OnRenamed(e.OldFullPath, e.FullPath));
                _watcher.Deleted += (_, e) => _dispatcher.InvokeAsync(() => OnDeleted(e.FullPath));
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                App.LogError("Watching the Downloads folder failed", ex);
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        private static bool IsTemp(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".crdownload" or ".part" or ".opdownload" or ".download";
        }

        private void OnCreated(string path)
        {
            if (!IsTemp(path)) return;
            _active[path] = DateTime.UtcNow;
            if (!_poll.IsEnabled) _poll.Start();
            Poll();
        }

        private void OnRenamed(string oldPath, string newPath)
        {
            bool wasActive = _active.Remove(oldPath);
            if (IsTemp(newPath))
            {
                // Chrome: "Unconfirmed 1234.crdownload" → "movie.mp4.crdownload"
                _active[newPath] = DateTime.UtcNow;
                if (!_poll.IsEnabled) _poll.Start();
                Poll();
                return;
            }
            if (wasActive || IsTemp(oldPath))
            {
                Poll();
                if (File.Exists(newPath)) Finished?.Invoke(newPath);
            }
        }

        private void OnDeleted(string path)
        {
            if (!_active.Remove(path)) return;
            Poll();
            // Firefox: "file.zip.part" disappears and "file.zip" is the finished file.
            string final = path[..^Path.GetExtension(path).Length];
            try
            {
                if (File.Exists(final) && new FileInfo(final).Length > 0) Finished?.Invoke(final);
            }
            catch
            {
                // cancelled download
            }
        }

        private void Poll()
        {
            long total = 0;
            foreach (var path in _active.Keys.ToList())
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) { _active.Remove(path); continue; }
                    total += info.Length;
                }
                catch
                {
                    // locked for a moment – keep it
                }
            }
            ReceivedBytes = total;
            if (_active.Count == 0) _poll.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>"820 KB", "45 MB", "1.2 GB".</summary>
        public static string FormatSize(long bytes)
        {
            double kb = bytes / 1024.0;
            if (kb < 1000) return $"{kb:0} KB";
            double mb = kb / 1024;
            if (mb < 1000) return $"{mb:0} MB";
            return $"{mb / 1024:0.0} GB";
        }

        public void Dispose()
        {
            _poll.Stop();
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
