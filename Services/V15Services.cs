using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    internal static class Store
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crest");

        public static T? Load<T>(string file) where T : class
        {
            try
            {
                string path = Path.Combine(Folder, file);
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                App.LogError("Loading " + file + " failed", ex);
                return null;
            }
        }

        public static void Save<T>(string file, T value)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, file), JsonSerializer.Serialize(value));
            }
            catch (Exception ex)
            {
                App.LogError("Saving " + file + " failed", ex);
            }
        }
    }

    // =========================================================================
    //  Listening history (Recent page)
    // =========================================================================

    public sealed class PlayedSong
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string App { get; set; } = "";
        public DateTime At { get; set; }
    }

    /// <summary>The songs you played, newest first (kept on this PC only; up to 500).</summary>
    public sealed class HistoryService
    {
        private const string File = "history.json";
        private readonly List<PlayedSong> _songs;

        public event EventHandler? Changed;

        public IReadOnlyList<PlayedSong> Songs => _songs;

        public HistoryService() => _songs = Store.Load<List<PlayedSong>>(File) ?? new List<PlayedSong>();

        public void Add(string title, string artist, string app)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            // Same song again within a few minutes (pause / resume, seek) isn't a new play.
            if (_songs.Count > 0 && _songs[0].Title == title && _songs[0].Artist == artist &&
                DateTime.Now - _songs[0].At < TimeSpan.FromMinutes(10)) return;
            _songs.Insert(0, new PlayedSong { Title = title, Artist = artist, App = app, At = DateTime.Now });
            if (_songs.Count > 500) _songs.RemoveRange(500, _songs.Count - 500);
            Store.Save(File, _songs);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            _songs.Clear();
            Store.Save(File, _songs);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Most played song and artist of the last 7 days.</summary>
        public (string Song, int SongPlays, string Artist, int ArtistPlays) WeekTop()
        {
            var week = _songs.Where(s => s.At > DateTime.Now.AddDays(-7)).ToList();
            var song = week.GroupBy(s => s.Title + " – " + s.Artist).OrderByDescending(g => g.Count()).FirstOrDefault();
            var artist = week.Where(s => s.Artist.Length > 0).GroupBy(s => s.Artist).OrderByDescending(g => g.Count()).FirstOrDefault();
            return (song?.Key ?? "", song?.Count() ?? 0, artist?.Key ?? "", artist?.Count() ?? 0);
        }
    }

    // =========================================================================
    //  Reminders + alarm
    // =========================================================================

    public sealed class Reminder
    {
        public string Text { get; set; } = "";
        public DateTime Due { get; set; }
    }

    public sealed class ReminderService
    {
        private const string File = "reminders.json";
        private readonly List<Reminder> _items;
        private readonly DispatcherTimer _timer;
        private string _lastAlarmDay = "";

        public event Action<Reminder>? Due;
        public event EventHandler? AlarmRang;
        public event EventHandler? Changed;

        public IReadOnlyList<Reminder> Items => _items;
        public bool AlarmEnabled { get; set; }
        public TimeSpan AlarmTime { get; set; } = new(7, 30, 0);

        public ReminderService()
        {
            _items = Store.Load<List<Reminder>>(File) ?? new List<Reminder>();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick += (_, _) => Check();
            _timer.Start();
        }

        public void Add(string text, DateTime due)
        {
            _items.Add(new Reminder { Text = text, Due = due });
            _items.Sort((a, b) => a.Due.CompareTo(b.Due));
            Store.Save(File, _items);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(Reminder r)
        {
            if (!_items.Remove(r)) return;
            Store.Save(File, _items);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Check()
        {
            var now = DateTime.Now;
            foreach (var r in _items.Where(r => r.Due <= now).ToList())
            {
                _items.Remove(r);
                Store.Save(File, _items);
                Changed?.Invoke(this, EventArgs.Empty);
                Due?.Invoke(r);
            }

            string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (AlarmEnabled && _lastAlarmDay != today && now.TimeOfDay >= AlarmTime && now.TimeOfDay < AlarmTime + TimeSpan.FromMinutes(2))
            {
                _lastAlarmDay = today;
                AlarmRang?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>"remind me in 20 min to call mom", "remind me at 5:30pm to leave".</summary>
        public static bool TryParse(string input, out string text, out DateTime due)
        {
            text = "";
            due = default;
            var m = Regex.Match(input.Trim(), @"^remind(?: me)?\s+(?:(in)\s+(\d+(?:\.\d+)?)\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)|(at)\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)\s*(?:to\s+)?(.*)$",
                                RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            text = m.Groups[8].Value.Trim();
            if (text.Length == 0) text = "Reminder";
            if (m.Groups[1].Success)
            {
                double n = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                bool hours = m.Groups[3].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase);
                due = DateTime.Now + (hours ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n));
                return n > 0 && n < 10000;
            }
            int hour = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            int minute = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
            string ampm = m.Groups[7].Value.ToLowerInvariant();
            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;
            if (hour > 23 || minute > 59) return false;
            due = DateTime.Today.AddHours(hour).AddMinutes(minute);
            if (due <= DateTime.Now) due = due.AddDays(1);
            return true;
        }
    }

    // =========================================================================
    //  Currency rates for the calculator
    // =========================================================================

    public static class CurrencyService
    {
        private sealed class Cache
        {
            public DateTime Fetched { get; set; }
            public Dictionary<string, double> Rates { get; set; } = new();
        }

        private static Cache? _cache;
        private static bool _fetching;

        public static event Action? RatesArrived;

        /// <summary>Units per 1 USD, or null when unknown / not loaded yet.</summary>
        public static double? Rate(string code)
        {
            _cache ??= Store.Load<Cache>("currency.json") ?? new Cache();
            if (DateTime.Now - _cache.Fetched > TimeSpan.FromHours(12)) _ = FetchAsync();
            return _cache.Rates.TryGetValue(code.ToUpperInvariant(), out double r) ? r : null;
        }

        public static bool Loaded => _cache?.Rates.Count > 0;

        private static async Task FetchAsync()
        {
            if (_fetching) return;
            _fetching = true;
            try
            {
                using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync("https://open.er-api.com/v6/latest/USD"));
                var rates = new Dictionary<string, double>();
                foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject()) rates[p.Name] = p.Value.GetDouble();
                _cache = new Cache { Fetched = DateTime.Now, Rates = rates };
                Store.Save("currency.json", _cache);
                RatesArrived?.Invoke();
            }
            catch (Exception ex)
            {
                App.LogError("Currency rates unavailable", ex);
                if (_cache != null) _cache.Fetched = DateTime.Now.AddHours(-11);   // try again in an hour
            }
            finally
            {
                _fetching = false;
            }
        }
    }

    // =========================================================================
    //  Translate (MyMemory, free)
    // =========================================================================

    public static class TranslateService
    {
        private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "en", ["hindi"] = "hi", ["bengali"] = "bn", ["bangla"] = "bn", ["spanish"] = "es", ["french"] = "fr",
            ["german"] = "de", ["italian"] = "it", ["portuguese"] = "pt", ["russian"] = "ru", ["japanese"] = "ja", ["korean"] = "ko",
            ["chinese"] = "zh-CN", ["arabic"] = "ar", ["tamil"] = "ta", ["telugu"] = "te", ["marathi"] = "mr", ["gujarati"] = "gu",
            ["urdu"] = "ur", ["kannada"] = "kn", ["malayalam"] = "ml", ["punjabi"] = "pa", ["turkish"] = "tr", ["dutch"] = "nl",
            ["indonesian"] = "id", ["thai"] = "th", ["vietnamese"] = "vi", ["polish"] = "pl", ["greek"] = "el", ["nepali"] = "ne",
        };

        /// <summary>"translate good morning to hindi" → ("good morning", "hi").</summary>
        public static bool TryParse(string input, out string text, out string lang, out string langName)
        {
            text = lang = langName = "";
            var m = Regex.Match(input.Trim(), @"^(?:translate|tr)\s+(.+?)\s+(?:to|in|into)\s+([a-z]+)$", RegexOptions.IgnoreCase);
            if (!m.Success || !Languages.TryGetValue(m.Groups[2].Value, out var code)) return false;
            text = m.Groups[1].Value.Trim().Trim('"');
            lang = code;
            langName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(m.Groups[2].Value.ToLowerInvariant());
            return text.Length > 0;
        }

        public static async Task<string?> TranslateAsync(string text, string lang)
        {
            try
            {
                string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=Autodetect|" + lang;
                using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
                return doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            }
            catch (Exception ex)
            {
                App.LogError("Translate failed", ex);
                return null;
            }
        }
    }

    // =========================================================================
    //  Quick launcher: apps from the Start menu
    // =========================================================================

    public static class AppIndex
    {
        private static List<(string Name, string Path)>? _apps;
        private static DateTime _built;

        public static IReadOnlyList<(string Name, string Path)> Find(string query, int max = 3)
        {
            query = query.Trim();
            if (query.Length < 2) return Array.Empty<(string, string)>();
            if (_apps == null || DateTime.Now - _built > TimeSpan.FromMinutes(10)) Build();
            return _apps!
                .Select(a => (a, score: Score(a.Name, query)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.a.Name.Length)
                .Take(max).Select(x => x.a).ToList();
        }

        private static int Score(string name, string q)
        {
            if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 3;
            if (name.Split(' ').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 2;
            return name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private static void Build()
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                     })
            {
                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(name)) list.Add((name, lnk));
                    }
                }
                catch
                {
                    // folder not readable
                }
            }
            _apps = list;
            _built = DateTime.Now;
        }

        public static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError("Opening app failed", ex); }
        }
    }

    // =========================================================================
    //  Wellbeing: eye breaks, stand up, water, morning summary
    // =========================================================================

    public sealed class WellbeingService
    {
        private sealed class WaterDay
        {
            public string Day { get; set; } = "";
            public int Glasses { get; set; }
            public DateTime Last { get; set; }
            public string SummaryDay { get; set; } = "";
        }

        private readonly DispatcherTimer _timer;
        private readonly WaterDay _water;
        private double _activeSinceEye, _activeSinceStand;   // minutes
        private DateTime _lastWaterNudge = DateTime.MinValue;

        public event EventHandler? EyeBreak;
        public event EventHandler? StandUp;
        public event EventHandler? WaterNudge;
        public event EventHandler? MorningSummary;
        public event EventHandler? WaterChanged;

        public bool EyeBreaks { get; set; }
        public bool StandReminder { get; set; }
        public bool WaterReminder { get; set; }
        public bool DailySummary { get; set; }
        public int WaterGoal { get; set; } = 8;

        public int Glasses => _water.Day == Today ? _water.Glasses : 0;

        private static string Today => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public WellbeingService()
        {
            _water = Store.Load<WaterDay>("wellbeing.json") ?? new WaterDay();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        public void AddGlass(int delta = 1)
        {
            if (_water.Day != Today) { _water.Day = Today; _water.Glasses = 0; }
            _water.Glasses = Math.Clamp(_water.Glasses + delta, 0, 30);
            if (delta > 0) _water.Last = DateTime.Now;
            Store.Save("wellbeing.json", _water);
            WaterChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Seconds since the last mouse / keyboard input anywhere.</summary>
        public static double IdleSeconds()
        {
            var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
            if (!NativeMethods.GetLastInputInfo(ref info)) return 0;
            return Math.Max(0, (uint)Environment.TickCount - info.dwTime) / 1000.0;
        }

        private void Tick()
        {
            double idle = IdleSeconds();
            if (idle > 5 * 60)
            {
                // You were away: that counts as a break.
                _activeSinceEye = _activeSinceStand = 0;
                return;
            }

            _activeSinceEye += 0.5;
            _activeSinceStand += 0.5;

            if (EyeBreaks && _activeSinceEye >= 20)
            {
                _activeSinceEye = 0;
                EyeBreak?.Invoke(this, EventArgs.Empty);
            }
            if (StandReminder && _activeSinceStand >= 60)
            {
                _activeSinceStand = 0;
                StandUp?.Invoke(this, EventArgs.Empty);
            }

            var now = DateTime.Now;
            if (WaterReminder && now.Hour >= 9 && now.Hour < 22 && Glasses < WaterGoal &&
                now - _water.Last > TimeSpan.FromMinutes(90) && now - _lastWaterNudge > TimeSpan.FromMinutes(90))
            {
                _lastWaterNudge = now;
                WaterNudge?.Invoke(this, EventArgs.Empty);
            }

            if (DailySummary && now.Hour >= 6 && now.Hour < 12 && _water.SummaryDay != Today)
            {
                _water.SummaryDay = Today;
                Store.Save("wellbeing.json", _water);
                MorningSummary?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // =========================================================================
    //  System watch: USB drives, low storage, internet, Wi-Fi
    // =========================================================================

    public sealed class SystemWatchService
    {
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _usbTimer, _storageTimer;
        private HashSet<string> _usb = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _storageWarned = new();
        private bool? _online;
        private DateTime _startedAt = DateTime.Now;

        public event Action<string, string>? UsbConnected;     // root, label
        public event EventHandler? UsbChanged;
        public event Action<string, int>? StorageLow;          // drive, % free
        public event Action<bool>? InternetChanged;            // true = back online

        public SystemWatchService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _usb = RemovableDrives().Select(d => d.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);

            _usbTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _usbTimer.Tick += (_, _) => CheckUsb();
            _usbTimer.Start();

            _storageTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };
            _storageTimer.Tick += (_, _) => CheckStorage();
            _storageTimer.Start();

            try
            {
                _online = IsOnline();
                global::Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ =>
                    _dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(3000);   // ignore blips
                        bool online = IsOnline();
                        if (_online == online) return;
                        _online = online;
                        if (DateTime.Now - _startedAt > TimeSpan.FromSeconds(30)) InternetChanged?.Invoke(online);
                    });
            }
            catch (Exception ex)
            {
                App.LogError("Network watch unavailable", ex);
            }
        }

        public static bool IsOnline()
        {
            try
            {
                var p = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                return p != null && p.GetNetworkConnectivityLevel() == global::Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>(network name, signal 0–5) when on Wi-Fi, otherwise null.</summary>
        public static (string Ssid, int Bars)? WiFi()
        {
            try
            {
                var p = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (p == null || !p.IsWlanConnectionProfile) return null;
                string ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName ?? "Wi-Fi";
                int bars = p.GetSignalBars() ?? 0;
                return (ssid, bars);
            }
            catch
            {
                return null;
            }
        }

        public static IReadOnlyList<(string Root, string Label)> RemovableDrives()
        {
            var list = new List<(string, string)>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Removable || !d.IsReady) continue;
                    string label = "";
                    try { label = d.VolumeLabel; } catch { }
                    list.Add((d.RootDirectory.FullName, label.Length > 0 ? label : "USB drive"));
                }
            }
            catch
            {
                // ignore
            }
            return list;
        }

        private void CheckUsb()
        {
            var now = RemovableDrives();
            var roots = now.Select(d => d.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (roots.SetEquals(_usb)) return;
            foreach (var d in now.Where(d => !_usb.Contains(d.Root))) UsbConnected?.Invoke(d.Root, d.Label);
            _usb = roots;
            UsbChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Safely removes a USB drive (the same as "Eject" in File Explorer).</summary>
        public static void Eject(string root)
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic? item = shell.NameSpace(17).ParseName(root.TrimEnd('\\'));   // 17 = This PC
                item?.InvokeVerb("Eject");
            }
            catch (Exception ex)
            {
                App.LogError("Eject failed", ex);
            }
        }

        public void CheckStorage()
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                    int pct = (int)Math.Round(100.0 * d.AvailableFreeSpace / d.TotalSize);
                    if (pct >= 10) continue;
                    string name = d.Name.TrimEnd('\\');
                    if (_storageWarned.TryGetValue(name, out var day) && day == today) continue;
                    _storageWarned[name] = today;
                    StorageLow?.Invoke(name, pct);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    // =========================================================================
    //  Top apps (System page)
    // =========================================================================

    public sealed class TopAppsService
    {
        private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
            "dwm", "explorer", "fontdrvhost", "Memory Compression", "Secure System", "audiodg", "sihost", "ctfmon", "Crest"
        };

        private readonly DispatcherTimer _timer;
        private Dictionary<int, TimeSpan> _lastCpu = new();
        private DateTime _lastAt;
        private int _users;

        public event EventHandler? Updated;

        public IReadOnlyList<(string Name, double Cpu, long MemoryMb)> Top { get; private set; } = Array.Empty<(string, double, long)>();

        public TopAppsService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, _) => await SampleAsync();
        }

        public void AddUser()
        {
            if (_users++ == 0) { _ = SampleAsync(); _timer.Start(); }
        }

        public void RemoveUser()
        {
            if (_users == 0) return;
            if (--_users == 0) _timer.Stop();
        }

        private async Task SampleAsync()
        {
            var result = await Task.Run(() =>
            {
                var now = DateTime.UtcNow;
                double seconds = Math.Max(0.5, (now - _lastAt).TotalSeconds);
                var cpu = new Dictionary<int, TimeSpan>();
                var byName = new Dictionary<string, (double Cpu, long Mem)>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (Protected.Contains(p.ProcessName) || p.Id == Environment.ProcessId) continue;
                        var t = p.TotalProcessorTime;
                        cpu[p.Id] = t;
                        double pct = _lastCpu.TryGetValue(p.Id, out var before)
                            ? (t - before).TotalSeconds / seconds / Environment.ProcessorCount * 100 : 0;
                        long mem = p.WorkingSet64 / (1024 * 1024);
                        byName.TryGetValue(p.ProcessName, out var acc);
                        byName[p.ProcessName] = (acc.Cpu + pct, acc.Mem + mem);
                    }
                    catch
                    {
                        // access denied / exited
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
                _lastCpu = cpu;
                _lastAt = now;
                return byName.OrderByDescending(kv => kv.Value.Cpu).ThenByDescending(kv => kv.Value.Mem)
                             .Take(4).Select(kv => (kv.Key, Math.Min(100, kv.Value.Cpu), kv.Value.Mem)).ToList();
            });
            Top = result;
            Updated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Asks the app to close; forces it after 3 seconds if it doesn't.</summary>
        public static async Task CloseAsync(string name)
        {
            if (Protected.Contains(name)) return;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.CloseMainWindow() || !await Task.Run(() => p.WaitForExit(3000))) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    App.LogError("Closing " + name + " failed", ex);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    // =========================================================================
    //  Text snippets (";mail" → your email)
    // =========================================================================

    public sealed class SnippetService
    {
        private readonly KeyboardHookService _hook;
        private readonly Dictionary<string, string> _snippets = new(StringComparer.OrdinalIgnoreCase);
        private string _buffer = "";
        private bool _enabled;
        private int _modifiers;

        public SnippetService(KeyboardHookService hook) => _hook = hook;

        /// <summary>Lines like ";mail = me@example.com". Each trigger must start with ";".</summary>
        public void Configure(bool enabled, string lines)
        {
            _snippets.Clear();
            foreach (var raw in (lines ?? "").Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw[..eq].Trim().ToLowerInvariant();
                string value = raw[(eq + 1)..].Trim().Replace("\\n", "\n");
                if (key.Length >= 2 && key[0] == ';' && key[1..].All(char.IsLetterOrDigit) && value.Length > 0) _snippets[key] = value;
            }

            bool on = enabled && _snippets.Count > 0;
            if (on == _enabled) return;
            _enabled = on;
            if (on) { _hook.Key += OnKey; _hook.AddUser(); }
            else { _hook.Key -= OnKey; _hook.RemoveUser(); _buffer = ""; }
        }

        private void OnKey(int vk, bool down)
        {
            if (vk is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
            {
                _modifiers += down ? 1 : -1;
                _modifiers = Math.Max(0, _modifiers);
                if (down) _buffer = "";
                return;
            }
            if (!down || vk is 0x10 or 0xA0 or 0xA1) return;
            if (_modifiers > 0) { _buffer = ""; return; }

            char? c = vk switch
            {
                >= 0x41 and <= 0x5A => (char)('a' + (vk - 0x41)),
                >= 0x30 and <= 0x39 when !NativeMethods.IsShiftDown() => (char)('0' + (vk - 0x30)),
                0xBA when !NativeMethods.IsShiftDown() => ';',
                _ => null
            };
            if (vk == 0x08) { if (_buffer.Length > 0) _buffer = _buffer[..^1]; return; }
            if (c == null) { _buffer = ""; return; }

            _buffer += c.Value;
            if (_buffer.Length > 24) _buffer = _buffer[^24..];
            int semi = _buffer.LastIndexOf(';');
            if (semi < 0) return;
            string typed = _buffer[semi..];
            if (!_snippets.TryGetValue(typed, out var text)) return;

            _buffer = "";
            NativeMethods.TypeText(typed.Length, text);
        }
    }

    // =========================================================================
    //  Accent color from the desktop wallpaper
    // =========================================================================

    public static class WallpaperColor
    {
        public static Color? Read()
        {
            try
            {
                var sb = new System.Text.StringBuilder(520);
                if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)) return null;
                string path = sb.ToString();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return ColorHelper.ExtractAccent(bmp);
            }
            catch
            {
                return null;
            }
        }
    }

    // =========================================================================
    //  Soft UI sounds (generated, no files)
    // =========================================================================

    public static class UiSounds
    {
        private static SoundPlayer? _open, _pop;

        public static bool Enabled { get; set; }

        public static void Open() => Play(ref _open, 880, 1320, 0.07);
        public static void Pop() => Play(ref _pop, 660, 990, 0.09);

        private static void Play(ref SoundPlayer? player, double f1, double f2, double seconds)
        {
            if (!Enabled) return;
            try
            {
                player ??= new SoundPlayer(new MemoryStream(Tone(f1, f2, seconds)));
                player.Play();
            }
            catch
            {
                // no audio device
            }
        }

        /// <summary>A short, soft two-note chime as a 16-bit mono WAV.</summary>
        private static byte[] Tone(double f1, double f2, double seconds)
        {
            const int rate = 44100;
            int n = (int)(rate * seconds * 2);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double f = t < seconds ? f1 : f2;
                double local = t < seconds ? t : t - seconds;
                double env = Math.Exp(-local * 28) * Math.Min(1, local * 400);
                w.Write((short)(Math.Sin(2 * Math.PI * f * t) * env * 0.16 * short.MaxValue));
            }
            return ms.ToArray();
        }
    }

    // =========================================================================
    //  Remember each app's volume
    // =========================================================================

    /// <summary>
    /// Remembers the volume you set for an app on the Sound page ("Spotify 60 %, games 30 %") and puts it
    /// back whenever that app starts playing again.
    /// </summary>
    public sealed class AppVolumeMemory : IDisposable
    {
        private readonly Dictionary<string, float> _saved;
        private readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _timer;
        private AudioDevicesService? _devices;   // its own copy, so the Sound page's sliders aren't disturbed

        public AppVolumeMemory()
        {
            _saved = Store.Load<Dictionary<string, float>>("app-volumes.json") ?? new Dictionary<string, float>();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (_, _) => Apply();
        }

        public void SetEnabled(bool on)
        {
            if (on) _timer.Start();
            else _timer.Stop();
        }

        public void Remember(string appKey, float volume)
        {
            if (appKey.Length == 0) return;
            _saved[appKey] = Math.Clamp(volume, 0f, 1f);
            _applied.Add(appKey);   // you just set it – don't change it back
            Store.Save("app-volumes.json", _saved);
        }

        private void Apply()
        {
            if (_saved.Count == 0) return;
            _devices ??= new AudioDevicesService();
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in _devices.GetApps())
            {
                running.Add(app.Key);
                if (_applied.Contains(app.Key) || !_saved.TryGetValue(app.Key, out float v)) continue;
                app.Volume = v;
                _applied.Add(app.Key);
            }
            // An app that closed gets its volume again the next time it plays.
            _applied.RemoveWhere(k => !running.Contains(k));
        }

        public void Dispose()
        {
            _timer.Stop();
            _devices?.Dispose();
        }
    }
}
