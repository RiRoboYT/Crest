using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Crest.Services
{
    public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, string Location);

    /// <summary>
    /// Reads the user's calendar from its private iCal (.ics) address – Google Calendar, Outlook and
    /// iCloud all provide one – and raises reminders before meetings.
    ///
    /// Supported: single events, daily / weekly (BYDAY) / monthly repeats with INTERVAL, COUNT,
    /// UNTIL, EXDATE and moved occurrences (RECURRENCE-ID). All-day events are skipped.
    /// </summary>
    public sealed class CalendarService
    {
        private readonly DispatcherTimer _refresh;
        private readonly DispatcherTimer _check;
        private readonly HashSet<string> _announced = new();
        private string _url = "";
        private int _reminderMinutes = 10;
        private List<CalendarEvent> _events = new();

        public event EventHandler? Updated;

        /// <summary>(event, "in 5 min" / "now")</summary>
        public event Action<CalendarEvent, string>? Reminder;

        public string Status { get; private set; } = "Add your calendar link in Settings → Activities.";

        public IReadOnlyList<CalendarEvent> Upcoming
        {
            get
            {
                var now = DateTime.Now;
                return _events.Where(e => e.End > now).OrderBy(e => e.Start).Take(12).ToList();
            }
        }

        public CalendarService()
        {
            _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
            _refresh.Tick += async (_, _) => await RefreshAsync();
            _check = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
            _check.Tick += (_, _) => CheckReminders();
        }

        public void Configure(string url, int reminderMinutes)
        {
            _reminderMinutes = reminderMinutes;
            url = (url ?? "").Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
            if (url == _url) return;
            _url = url;
            _events = new List<CalendarEvent>();

            if (_url.Length == 0)
            {
                _refresh.Stop();
                _check.Stop();
                Status = "Add your calendar link in Settings → Activities.";
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }
            _refresh.Start();
            _check.Start();
            _ = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            if (_url.Length == 0) return;
            try
            {
                string ics = await Http.Client.GetStringAsync(_url);
                var now = DateTime.Now;
                _events = Parse(ics, now.AddDays(-1), now.AddDays(8));
                Status = _events.Count == 0 ? "No upcoming events this week." : "";
            }
            catch (Exception ex)
            {
                App.LogError("Calendar download failed", ex);
                Status = "Couldn't load your calendar link.";
            }
            Updated?.Invoke(this, EventArgs.Empty);
            CheckReminders();
        }

        private void CheckReminders()
        {
            var now = DateTime.Now;
            foreach (var e in _events)
            {
                var until = e.Start - now;
                string key = e.Title + "|" + e.Start.Ticks;

                if (until > TimeSpan.Zero && until <= TimeSpan.FromMinutes(_reminderMinutes) && _announced.Add(key + "|soon"))
                {
                    int mins = Math.Max(1, (int)Math.Ceiling(until.TotalMinutes));
                    Reminder?.Invoke(e, $"in {mins} min");
                }
                else if (until <= TimeSpan.Zero && until > TimeSpan.FromMinutes(-2) && _announced.Add(key + "|now"))
                {
                    Reminder?.Invoke(e, "now");
                }
            }
            if (_announced.Count > 500) _announced.Clear();
        }

        // ================================================================== iCal parsing

        private sealed class RawEvent
        {
            public string Uid = "", Summary = "", Location = "", Rrule = "";
            public DateTime? Start, End, RecurrenceId;
            public bool AllDay, Cancelled;
            public List<DateTime> ExDates = new();
        }

        public static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to)
        {
            var raws = new List<RawEvent>();
            RawEvent? cur = null;

            foreach (var (name, pars, value) in Unfold(ics))
            {
                if (name == "BEGIN" && value == "VEVENT") { cur = new RawEvent(); continue; }
                if (name == "END" && value == "VEVENT") { if (cur != null) raws.Add(cur); cur = null; continue; }
                if (cur == null) continue;

                switch (name)
                {
                    case "UID": cur.Uid = value; break;
                    case "SUMMARY": cur.Summary = Unescape(value); break;
                    case "LOCATION": cur.Location = Unescape(value); break;
                    case "RRULE": cur.Rrule = value; break;
                    case "STATUS": cur.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                    case "DTSTART":
                        cur.Start = ParseDate(value, pars, out bool allDay);
                        cur.AllDay = allDay;
                        break;
                    case "DTEND": cur.End = ParseDate(value, pars, out _); break;
                    case "RECURRENCE-ID": cur.RecurrenceId = ParseDate(value, pars, out _); break;
                    case "EXDATE":
                        foreach (var part in value.Split(','))
                            if (ParseDate(part, pars, out _) is DateTime ex) cur.ExDates.Add(ex);
                        break;
                }
            }

            // Moved / edited occurrences replace the original slot of their series.
            var overrides = raws.Where(r => r.RecurrenceId.HasValue)
                                .Select(r => r.Uid + "|" + r.RecurrenceId!.Value.Ticks).ToHashSet();

            var result = new List<CalendarEvent>();
            foreach (var r in raws)
            {
                if (r.Cancelled || r.AllDay || r.Start == null) continue;
                var start = r.Start.Value;
                var length = (r.End ?? start.AddHours(1)) - start;
                if (length <= TimeSpan.Zero) length = TimeSpan.FromMinutes(30);
                string title = string.IsNullOrWhiteSpace(r.Summary) ? "Event" : r.Summary;

                if (string.IsNullOrEmpty(r.Rrule) || r.RecurrenceId.HasValue)
                {
                    if (start + length >= from && start <= to)
                        result.Add(new CalendarEvent(title, start, start + length, r.Location));
                    continue;
                }

                foreach (var occ in Expand(start, r.Rrule, from, to))
                {
                    if (r.ExDates.Any(x => Math.Abs((x - occ).TotalMinutes) < 1)) continue;
                    if (overrides.Contains(r.Uid + "|" + occ.Ticks)) continue;
                    result.Add(new CalendarEvent(title, occ, occ + length, r.Location));
                }
            }
            return result.OrderBy(e => e.Start).ToList();
        }

        private static IEnumerable<DateTime> Expand(DateTime start, string rrule, DateTime from, DateTime to)
        {
            var parts = rrule.Split(';')
                .Select(p => p.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].ToUpperInvariant(), kv => kv[1], StringComparer.OrdinalIgnoreCase);

            string freq = parts.TryGetValue("FREQ", out var f) ? f.ToUpperInvariant() : "";
            int interval = parts.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out int ivn) && ivn > 0 ? ivn : 1;
            int count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, out int cn) ? cn : int.MaxValue;
            DateTime until = parts.TryGetValue("UNTIL", out var u) && ParseDate(u, new Dictionary<string, string>(), out _) is DateTime ud ? ud : DateTime.MaxValue;

            var byDay = new List<DayOfWeek>();
            if (parts.TryGetValue("BYDAY", out var bd))
            {
                foreach (var d in bd.Split(','))
                {
                    string code = d.Length >= 2 ? d[^2..].ToUpperInvariant() : "";
                    var day = code switch
                    {
                        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
                        "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
                        "SU" => DayOfWeek.Sunday, _ => (DayOfWeek?)null
                    };
                    if (day.HasValue) byDay.Add(day.Value);
                }
            }

            int produced = 0;
            const int SafetyLimit = 2000;
            int steps = 0;

            if (freq == "DAILY")
            {
                for (var t = start; t <= to && t <= until && produced < count && steps++ < SafetyLimit; t = t.AddDays(interval))
                {
                    if (byDay.Count > 0 && !byDay.Contains(t.DayOfWeek)) continue;
                    produced++;
                    if (t >= from) yield return t;
                }
            }
            else if (freq == "WEEKLY")
            {
                if (byDay.Count == 0) byDay.Add(start.DayOfWeek);
                // Start of the week containing the first occurrence (weeks start on Monday per RFC default).
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var w = weekStart; w <= to && produced < count && steps++ < SafetyLimit; w = w.AddDays(7 * interval))
                {
                    foreach (var day in byDay.OrderBy(d => ((int)d + 6) % 7))
                    {
                        var t = w.AddDays(((int)day + 6) % 7) + start.TimeOfDay;
                        if (t < start) continue;
                        if (t > until || t > to || produced >= count) yield break;
                        produced++;
                        if (t >= from) yield return t;
                    }
                }
            }
            else if (freq == "MONTHLY")
            {
                for (var t = start; t <= to && t <= until && produced < count && steps++ < SafetyLimit; t = t.AddMonths(interval))
                {
                    produced++;
                    if (t >= from) yield return t;
                }
            }
            else
            {
                if (start >= from && start <= to) yield return start;
            }
        }

        private static DateTime? ParseDate(string value, Dictionary<string, string> pars, out bool allDay)
        {
            allDay = false;
            value = value.Trim();
            if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                allDay = true;
                return day;
            }

            bool utc = value.EndsWith('Z');
            string v = utc ? value[..^1] : value;
            if (!DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return null;

            if (utc) return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();

            if (pars.TryGetValue("TZID", out var tzid))
            {
                var tz = FindZone(tzid.Trim('"'));
                if (tz != null)
                {
                    try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local); }
                    catch { /* invalid local time – fall through */ }
                }
            }
            return t;   // "floating" time = local
        }

        private static TimeZoneInfo? FindZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch
            {
                try
                {
                    if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win))
                        return TimeZoneInfo.FindSystemTimeZoneById(win);
                }
                catch { /* unknown zone */ }
            }
            return null;
        }

        /// <summary>Yields (NAME, params, value) after joining folded lines.</summary>
        private static IEnumerable<(string, Dictionary<string, string>, string)> Unfold(string ics)
        {
            var lines = new List<string>();
            foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
            {
                if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0)
                    lines[^1] += raw[1..];
                else
                    lines.Add(raw);
            }

            foreach (var line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string head = line[..colon];
                string value = line[(colon + 1)..].Trim();
                var segs = head.Split(';');
                var pars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < segs.Length; i++)
                {
                    var kv = segs[i].Split('=', 2);
                    if (kv.Length == 2) pars[kv[0]] = kv[1];
                }
                yield return (segs[0].ToUpperInvariant(), pars, value);
            }
        }

        private static string Unescape(string s)
            => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    }
}
