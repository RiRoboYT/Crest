using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Crest.Services
{
    public sealed record LyricLine(TimeSpan Time, string Text);

    /// <summary>Lyrics for one song: time-synced lines when available, otherwise plain text.</summary>
    public sealed class LyricsResult
    {
        public bool Synced => Lines.Count > 0;
        public List<LyricLine> Lines { get; init; } = new();
        public string Plain { get; init; } = "";
        public bool Instrumental { get; init; }

        /// <summary>Index of the line being sung at <paramref name="position"/> (-1 before the first line).</summary>
        public int IndexAt(TimeSpan position)
        {
            int lo = 0, hi = Lines.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Lines[mid].Time <= position) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }
    }

    /// <summary>
    /// Fetches lyrics from LRCLIB (free, no key). Tries an exact match first (title + artist +
    /// duration), then a search. Results are cached for the session.
    /// </summary>
    public sealed class LyricsService
    {
        private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
        private static readonly Regex Noise = new(
            @"\s*[\(\[](official|lyrics?|audio|video|music video|visuali[sz]er|hd|hq|4k|remaster(ed)?[^\)\]]*|explicit|clean)[^\)\]]*[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Dictionary<string, LyricsResult?> _cache = new();

        public async Task<LyricsResult?> GetAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
        {
            (title, artist) = Clean(title, artist);
            if (string.IsNullOrWhiteSpace(title)) return null;

            string key = (title + "|" + artist).ToLowerInvariant();
            if (_cache.TryGetValue(key, out var cached)) return cached;

            LyricsResult? result = null;
            try
            {
                result = await GetExactAsync(title, artist, album, duration, ct) ?? await SearchAsync(title, artist, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.LogError("Lyrics lookup failed", ex);
                return null;   // don't cache network errors
            }

            _cache[key] = result;
            if (_cache.Count > 200) _cache.Remove(_cache.Keys.First());
            return result;
        }

        private static async Task<LyricsResult?> GetExactAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(artist)) return null;
            string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
                       + "&artist_name=" + Uri.EscapeDataString(artist);
            if (!string.IsNullOrWhiteSpace(album)) url += "&album_name=" + Uri.EscapeDataString(album);
            if (duration.TotalSeconds > 1) url += "&duration=" + (int)Math.Round(duration.TotalSeconds);

            using var resp = await Http.Client.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return FromJson(doc.RootElement);
        }

        private static async Task<LyricsResult?> SearchAsync(string title, string artist, CancellationToken ct)
        {
            string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
            if (!string.IsNullOrWhiteSpace(artist)) url += "&artist_name=" + Uri.EscapeDataString(artist);

            using var resp = await Http.Client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            LyricsResult? plainOnly = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var r = FromJson(item);
                if (r == null) continue;
                if (r.Synced) return r;
                plainOnly ??= r;
            }
            return plainOnly;
        }

        private static LyricsResult? FromJson(JsonElement e)
        {
            string synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            string plain = e.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;

            var lines = ParseLrc(synced);
            if (lines.Count == 0 && string.IsNullOrWhiteSpace(plain) && !instrumental) return null;
            return new LyricsResult { Lines = lines, Plain = plain.Trim(), Instrumental = instrumental };
        }

        /// <summary>Parses "[mm:ss.xx] text" (several stamps per line allowed).</summary>
        public static List<LyricLine> ParseLrc(string lrc)
        {
            var lines = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(lrc)) return lines;

            foreach (string raw in lrc.Split('\n'))
            {
                var matches = Stamp.Matches(raw);
                if (matches.Count == 0) continue;
                string text = Stamp.Replace(raw, "").Trim();
                foreach (Match m in matches)
                {
                    int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    lines.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), text));
                }
            }
            lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return lines;
        }

        /// <summary>
        /// Makes video titles searchable: "Artist - Song (Official Video)" by "Artist - Topic"
        /// becomes ("Song", "Artist").
        /// </summary>
        private static (string title, string artist) Clean(string title, string artist)
        {
            title = Noise.Replace(title ?? "", "").Trim();
            artist = (artist ?? "").Trim();
            if (artist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) artist = artist[..^8].Trim();
            if (artist.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase)) artist = artist[..^4].Trim();

            int dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                string left = title[..dash].Trim();
                string right = title[(dash + 3)..].Trim();
                if (artist.Length == 0 || left.Contains(artist, StringComparison.OrdinalIgnoreCase)
                    || artist.Contains(left, StringComparison.OrdinalIgnoreCase))
                {
                    artist = left;
                    title = right;
                }
            }

            int feat = title.IndexOf(" (feat", StringComparison.OrdinalIgnoreCase);
            if (feat > 0) title = title[..feat];
            return (title.Trim(), artist);
        }
    }
}
