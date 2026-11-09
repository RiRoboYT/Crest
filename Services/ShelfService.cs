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

        public IReadOnlyList<string> Files => _files;

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
