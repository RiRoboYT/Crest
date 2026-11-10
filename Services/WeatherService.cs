using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Crest.Services
{
    public sealed record WeatherHour(DateTime Time, double Temp, int Code, bool IsDay);

    public sealed class WeatherSnapshot
    {
        public string City { get; init; } = "";
        public double Temp { get; init; }
        public int Code { get; init; }
        public bool IsDay { get; init; } = true;
        public double High { get; init; }
        public double Low { get; init; }
        public bool Fahrenheit { get; init; }
        public List<WeatherHour> Hours { get; init; } = new();
        public DateTime UpdatedAt { get; init; } = DateTime.Now;
    }

    /// <summary>
    /// Weather from Open-Meteo (free, no API key). The city is geocoded once, then the forecast is
    /// refreshed every 30 minutes – only while a city is set.
    /// </summary>
    public sealed class WeatherService
    {
        private readonly DispatcherTimer _timer;
        private string _city = "";
        private bool _fahrenheit;
        private (double lat, double lon, string name)? _place;
        private string _placeFor = "";
        private bool _configured;

        public event EventHandler? Updated;
        /// <summary>Rain is expected to start in about this many minutes (UI thread, at most every 3 hours).</summary>
        public event Action<int>? RainSoon;
        private DateTime _lastRainAlert = DateTime.MinValue;

        public WeatherSnapshot? Current { get; private set; }
        public string Status { get; private set; } = "Add your city in Settings → Widgets.";

        public WeatherService()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };
            _timer.Tick += async (_, _) => await RefreshAsync();
        }

        public void Configure(string city, bool fahrenheit)
        {
            city = (city ?? "").Trim();
            // Called on every settings change – only act when the city or unit actually changed.
            if (_configured && city == _city && fahrenheit == _fahrenheit) return;
            _configured = true;
            _city = city;
            _fahrenheit = fahrenheit;

            if (_city.Length == 0)
            {
                _timer.Stop();
                Current = null;
                Status = "Add your city in Settings → Widgets.";
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }
            _timer.Start();
            _ = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            if (_city.Length == 0) return;
            try
            {
                if (_place == null || _placeFor != _city)
                {
                    _place = await GeocodeAsync(_city);
                    _placeFor = _city;
                    if (_place == null)
                    {
                        Current = null;
                        Status = $"Couldn't find \"{_city}\".";
                        Updated?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }

                var (lat, lon, name) = _place.Value;
                string unit = _fahrenheit ? "fahrenheit" : "celsius";
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CultureInfo.InvariantCulture)
                           + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                           + "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day"
                           + "&minutely_15=precipitation&forecast_minutely_15=8&past_minutely_15=1"
                           + "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2&temperature_unit=" + unit;

                string json = await Http.Client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var current = root.GetProperty("current");
                var daily = root.GetProperty("daily");
                var hourly = root.GetProperty("hourly");

                var hours = new List<WeatherHour>();
                var times = hourly.GetProperty("time");
                var temps = hourly.GetProperty("temperature_2m");
                var codes = hourly.GetProperty("weather_code");
                var days = hourly.GetProperty("is_day");
                DateTime now = DateTime.Now;
                for (int i = 0; i < times.GetArrayLength() && hours.Count < 5; i++)
                {
                    // times are local to the city (timezone=auto)
                    if (!DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    if (t <= now.AddMinutes(-30)) continue;
                    hours.Add(new WeatherHour(t, temps[i].GetDouble(), codes[i].GetInt32(), days[i].GetInt32() == 1));
                }

                Current = new WeatherSnapshot
                {
                    City = name,
                    Temp = current.GetProperty("temperature_2m").GetDouble(),
                    Code = current.GetProperty("weather_code").GetInt32(),
                    IsDay = current.GetProperty("is_day").GetInt32() == 1,
                    High = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                    Low = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                    Fahrenheit = _fahrenheit,
                    Hours = hours
                };
                Status = "";
                CheckRain(root);
            }
            catch (Exception ex)
            {
                App.LogError("Weather update failed", ex);
                if (Current == null) Status = "Weather is unavailable right now.";
            }
            Updated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Dry now, wet within the next ~90 minutes → one "rain soon" alert.</summary>
        private void CheckRain(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("minutely_15", out var m)) return;
                var times = m.GetProperty("time");
                var rain = m.GetProperty("precipitation");
                DateTime now = DateTime.Now;
                bool rainingNow = false;
                int? startsIn = null;
                for (int i = 0; i < times.GetArrayLength(); i++)
                {
                    if (!DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                    if (rain[i].ValueKind != JsonValueKind.Number) continue;
                    double mm = rain[i].GetDouble();
                    if (t <= now)
                    {
                        if (t > now.AddMinutes(-16)) rainingNow = mm >= 0.1;   // the slot we're in
                        continue;
                    }
                    if (mm >= 0.1 && startsIn == null && (t - now).TotalMinutes <= 90)
                        startsIn = Math.Max(5, (int)Math.Round((t - now).TotalMinutes / 5) * 5);
                }
                if (rainingNow || startsIn == null) return;
                if (DateTime.Now - _lastRainAlert < TimeSpan.FromHours(3)) return;
                _lastRainAlert = DateTime.Now;
                RainSoon?.Invoke(startsIn.Value);
            }
            catch
            {
                // rain data is optional
            }
        }

        private static async Task<(double, double, string)?> GeocodeAsync(string city)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" + Uri.EscapeDataString(city);
            using var doc = JsonDocument.Parse(await Http.Client.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var r = results[0];
            return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                    r.TryGetProperty("name", out var n) ? n.GetString() ?? city : city);
        }

        /// <summary>WMO weather code → (symbol, description). Symbols come from "Segoe UI Symbol".</summary>
        public static (string Symbol, string Text) Describe(int code, bool isDay) => code switch
        {
            0 => (isDay ? "☀" : "☾", "Clear"),
            1 => (isDay ? "☀" : "☾", "Mostly clear"),
            2 => ("⛅", "Partly cloudy"),
            3 => ("☁", "Cloudy"),
            45 or 48 => ("☁", "Fog"),
            51 or 53 or 55 or 56 or 57 => ("☂", "Drizzle"),
            61 or 63 or 65 or 66 or 67 => ("☂", "Rain"),
            71 or 73 or 75 or 77 => ("❄", "Snow"),
            80 or 81 or 82 => ("☂", "Showers"),
            85 or 86 => ("❄", "Snow showers"),
            95 or 96 or 99 => ("⚡", "Thunderstorm"),
            _ => ("☁", "—")
        };
    }
}
