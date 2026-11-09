using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Crest.Helpers;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Crest.Services
{
    /// <summary>
    /// Wrapper around the Windows "Global System Media Transport Controls" API (the same data the
    /// Windows volume flyout shows). Works with Spotify, browsers (YouTube etc.), Windows Media
    /// Player, and anything else that integrates with it.
    ///
    /// Every media session is watched, so with <see cref="PreferPlayingSession"/> the notch follows
    /// whichever app is actually playing instead of the one Windows happens to call "current".
    ///
    /// WinRT events arrive on background threads; everything is marshalled to the UI dispatcher so
    /// consumers can touch WPF objects directly from <see cref="Updated"/> / <see cref="TrackChanged"/>.
    /// </summary>
    public sealed class MediaSessionService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();

        private int _mediaVersion;          // guards against out-of-order async property loads
        private TimeSpan _startTime;
        private TimeSpan _basePosition;
        private DateTimeOffset _baseTime = DateTimeOffset.Now;
        private DateTimeOffset _lastTimelineStamp;
        private bool _preferPlaying = true;
        private bool _disposed;

        /// <summary>Raised on the UI thread whenever anything about the current media changes.</summary>
        public event EventHandler? Updated;

        /// <summary>Raised on the UI thread when a different song (title/artist) starts showing.</summary>
        public event EventHandler? TrackChanged;

        public bool HasSession => _session != null;
        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public string Album { get; private set; } = "";
        public bool IsPlaying { get; private set; }
        public TimeSpan Duration { get; private set; }
        public double PlaybackRate { get; private set; } = 1.0;
        public ImageSource? Thumbnail { get; private set; }
        /// <summary>Most prominent vivid color of the cover art, or null.</summary>
        public Color? AlbumColor { get; private set; }
        /// <summary>The cover's main vivid colors (1–3, most prominent first), or empty.</summary>
        public IReadOnlyList<Color> AlbumPalette { get; private set; } = Array.Empty<Color>();
        public bool CanSeek { get; private set; }
        /// <summary>The app supports shuffle / repeat from outside (many do, some don't).</summary>
        public bool CanShuffle { get; private set; }
        public bool CanRepeat { get; private set; }
        public bool ShuffleOn { get; private set; }
        /// <summary>0 = off, 1 = repeat this song, 2 = repeat the list.</summary>
        public int RepeatMode { get; private set; }
        /// <summary>App User Model ID of the app that owns the session (used to bring it to front).</summary>
        public string SourceApp { get; private set; } = "";

        public MediaSessionService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>When true, show whichever app is playing; otherwise follow Windows' "current" session.</summary>
        public bool PreferPlayingSession
        {
            get => _preferPlaying;
            set
            {
                if (_preferPlaying == value) return;
                _preferPlaying = value;
                Reevaluate();
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(RefreshSessions);
                _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(Reevaluate);
                RefreshSessions();
            }
            catch (Exception ex)
            {
                App.LogError("Media session API unavailable", ex);
            }
        }

        /// <summary>Estimated current position, interpolated between (often infrequent) app timeline updates.</summary>
        public TimeSpan Position
        {
            get
            {
                TimeSpan pos = _basePosition;
                if (IsPlaying)
                {
                    double elapsed = (DateTimeOffset.Now - _baseTime).TotalSeconds * PlaybackRate;
                    if (elapsed > 0) pos += TimeSpan.FromSeconds(elapsed);
                }
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
                return pos;
            }
        }

        // ================================================================== commands

        public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
        public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
        public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

        public Task ToggleShuffleAsync()
        {
            if (!CanShuffle) return Task.CompletedTask;
            bool next = !ShuffleOn;
            return Run(s => s.TryChangeShuffleActiveAsync(next).AsTask());
        }

        /// <summary>Off → repeat list → repeat song → off (like most players).</summary>
        public Task CycleRepeatAsync()
        {
            if (!CanRepeat) return Task.CompletedTask;
            var next = RepeatMode switch
            {
                0 => global::Windows.Media.MediaPlaybackAutoRepeatMode.List,
                2 => global::Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => global::Windows.Media.MediaPlaybackAutoRepeatMode.None
            };
            return Run(s => s.TryChangeAutoRepeatModeAsync(next).AsTask());
        }

        public Task SeekAsync(TimeSpan position)
        {
            if (!CanSeek) return Task.CompletedTask;
            // optimistic local update so the bar doesn't jump back while the app catches up
            _basePosition = position;
            _baseTime = DateTimeOffset.Now;
            Updated?.Invoke(this, EventArgs.Empty);
            return Run(s => s.TryChangePlaybackPositionAsync((_startTime + position).Ticks).AsTask());
        }

        private async Task Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
        {
            var s = _session;
            if (s == null) return;
            try { await action(s); }
            catch (Exception ex) { App.LogError("Media command failed", ex); }
        }

        // ================================================================== session tracking

        /// <summary>Re-subscribes to every open session (called when apps open/close sessions).</summary>
        private void RefreshSessions()
        {
            if (_disposed || _manager == null) return;

            foreach (var s in _watched)
            {
                try { s.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged; } catch { /* session gone */ }
            }
            _watched.Clear();

            try
            {
                foreach (var s in _manager.GetSessions())
                {
                    s.PlaybackInfoChanged += OnAnyPlaybackInfoChanged;
                    _watched.Add(s);
                }
            }
            catch (Exception ex)
            {
                App.LogError("Listing media sessions failed", ex);
            }

            Reevaluate();
        }

        private void OnAnyPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => _dispatcher.InvokeAsync(Reevaluate);

        /// <summary>Picks the session to display and refreshes playback state.</summary>
        private void Reevaluate()
        {
            if (_disposed || _manager == null) return;

            var next = ChooseSession();
            if (next != null && ReferenceEquals(next, _session))
            {
                UpdatePlayback();
                UpdateTimeline();
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }
            Attach(next);
        }

        private GlobalSystemMediaTransportControlsSession? ChooseSession()
        {
            GlobalSystemMediaTransportControlsSession? current = null;
            try { current = _manager?.GetCurrentSession(); } catch { /* ignore */ }

            if (!_preferPlaying) return current;

            if (IsSessionPlaying(current)) return current;
            if (_session != null && _watched.Contains(_session) && IsSessionPlaying(_session)) return _session;

            foreach (var s in _watched)
                if (IsSessionPlaying(s)) return s;

            // Nothing is playing: keep showing what we had (if it still exists) instead of jumping around.
            if (_session != null && _watched.Contains(_session)) return _session;
            return current ?? (_watched.Count > 0 ? _watched[0] : null);
        }

        private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession? s)
        {
            if (s == null) return false;
            try
            {
                return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        private void Attach(GlobalSystemMediaTransportControlsSession? next)
        {
            if (_session != null)
            {
                try
                {
                    _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }
                catch { /* session already gone */ }
            }

            _session = next;

            if (_session == null)
            {
                ResetState();
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }

            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

            try { SourceApp = _session.SourceAppUserModelId ?? ""; } catch { SourceApp = ""; }

            _lastTimelineStamp = default;
            UpdatePlayback();
            UpdateTimeline();
            Updated?.Invoke(this, EventArgs.Empty);
            _ = UpdateMediaPropertiesAsync();
        }

        private void ResetState()
        {
            Title = "";
            Artist = "";
            Album = "";
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            Thumbnail = null;
            AlbumColor = null;
            AlbumPalette = Array.Empty<Color>();
            CanSeek = false;
            CanShuffle = CanRepeat = ShuffleOn = false;
            RepeatMode = 0;
            SourceApp = "";
            _basePosition = TimeSpan.Zero;
            _baseTime = DateTimeOffset.Now;
            _mediaVersion++;   // drop any in-flight property load
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => _dispatcher.InvokeAsync(async () =>
            {
                if (_session == null) return;
                await UpdateMediaPropertiesAsync();
            });

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
            => _dispatcher.InvokeAsync(() =>
            {
                if (_session == null) return;
                UpdateTimeline();
                Updated?.Invoke(this, EventArgs.Empty);
            });

        private void UpdatePlayback()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var info = s.GetPlaybackInfo();
                bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (playing != IsPlaying)
                {
                    // Re-base the interpolation at the moment the state flips.
                    _basePosition = Position;
                    _baseTime = DateTimeOffset.Now;
                }

                IsPlaying = playing;
                PlaybackRate = info.PlaybackRate is double r && r > 0 ? r : 1.0;
                CanSeek = info.Controls?.IsPlaybackPositionEnabled ?? false;
                CanShuffle = info.Controls?.IsShuffleEnabled ?? false;
                CanRepeat = info.Controls?.IsRepeatEnabled ?? false;
                ShuffleOn = info.IsShuffleActive ?? false;
                RepeatMode = info.AutoRepeatMode switch
                {
                    global::Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1,
                    global::Windows.Media.MediaPlaybackAutoRepeatMode.List => 2,
                    _ => 0
                };
            }
            catch (Exception ex)
            {
                App.LogError("Reading playback info failed", ex);
            }
        }

        private void UpdateTimeline()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var t = s.GetTimelineProperties();
                _startTime = t.StartTime;
                var end = t.EndTime > TimeSpan.Zero ? t.EndTime : t.MaxSeekTime;
                Duration = end > t.StartTime ? end - t.StartTime : TimeSpan.Zero;

                // Some apps never refresh their timeline while paused; ignore unchanged stamps
                // so we don't re-apply a stale position after resuming.
                if (t.LastUpdatedTime == _lastTimelineStamp) return;
                _lastTimelineStamp = t.LastUpdatedTime;

                var now = DateTimeOffset.Now;
                var stamp = t.LastUpdatedTime;
                bool stampValid = stamp.Year > 2000 && stamp <= now.AddSeconds(5) && stamp > now.AddHours(-12);

                _basePosition = t.Position - t.StartTime;
                _baseTime = stampValid ? stamp : now;
            }
            catch (Exception ex)
            {
                App.LogError("Reading timeline failed", ex);
            }
        }

        private async Task UpdateMediaPropertiesAsync()
        {
            var s = _session;
            if (s == null) return;
            int version = ++_mediaVersion;

            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (version != _mediaVersion || !ReferenceEquals(s, _session) || props == null) return;

                string newTitle = props.Title ?? "";
                string newArtist = string.IsNullOrWhiteSpace(props.Artist) ? (props.AlbumArtist ?? "") : props.Artist;
                bool trackChanged = newTitle != Title || newArtist != Artist;

                Title = newTitle;
                Artist = newArtist;
                Album = props.AlbumTitle ?? "";
                if (trackChanged)
                {
                    Thumbnail = null;
                    AlbumColor = null;
                    AlbumPalette = Array.Empty<Color>();
                }
                Updated?.Invoke(this, EventArgs.Empty);

                if (trackChanged && !string.IsNullOrWhiteSpace(newTitle))
                    TrackChanged?.Invoke(this, EventArgs.Empty);

                var thumb = await LoadThumbnailAsync(props.Thumbnail);
                if (version != _mediaVersion || !ReferenceEquals(s, _session)) return;

                Thumbnail = thumb;
                var palette = ColorHelper.ExtractPalette(thumb as BitmapSource, 3);
                AlbumPalette = palette;
                AlbumColor = palette.Length > 0 ? palette[0] : null;
                Updated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Common when the source app closes mid-request.
                App.LogError("Reading media properties failed", ex);
            }
        }

        private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
        {
            if (reference == null) return null;
            try
            {
                using IRandomAccessStreamWithContentType ras = await reference.OpenReadAsync();
                using Stream src = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                if (ms.Length == 0) return null;
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now, release the stream
                bmp.DecodePixelWidth = 160;                     // tiny decode = tiny memory
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                ms.Dispose();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var s in _watched)
            {
                try { s.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged; } catch { /* ignore */ }
            }
            _watched.Clear();

            if (_session != null)
            {
                try
                {
                    _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }
                catch { /* ignore */ }
                _session = null;
            }
            _manager = null;
        }
    }
}
