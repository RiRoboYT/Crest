using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Crest.Services
{
    /// <summary>
    /// Thin wrapper around the Windows "Global System Media Transport Controls" API
    /// (the same data the Windows volume flyout shows). Works with Spotify, browsers
    /// (YouTube etc.), Windows Media Player, Groove, VLC (recent), and anything else
    /// that integrates with SMTC.
    ///
    /// WinRT events arrive on background threads; everything is marshalled to the
    /// UI dispatcher so consumers can touch WPF objects directly in <see cref="Updated"/>.
    /// </summary>
    public sealed class MediaSessionService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;

        private int _mediaVersion;          // guards against out-of-order async property loads
        private TimeSpan _startTime;
        private TimeSpan _basePosition;
        private DateTimeOffset _baseTime = DateTimeOffset.Now;
        private DateTimeOffset _lastTimelineStamp;
        private bool _disposed;

        /// <summary>Raised on the UI thread whenever anything about the current media changes.</summary>
        public event EventHandler? Updated;

        public bool HasSession => _session != null;
        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public string Album { get; private set; } = "";
        public bool IsPlaying { get; private set; }
        public TimeSpan Duration { get; private set; }
        public double PlaybackRate { get; private set; } = 1.0;
        public ImageSource? Thumbnail { get; private set; }
        public bool CanSeek { get; private set; }
        public string SourceApp { get; private set; } = "";

        public MediaSessionService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(AttachCurrentSession);
                _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(AttachCurrentSession);
                AttachCurrentSession();
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

        // ------------------------------------------------------------------ commands

        public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
        public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
        public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

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

        // ------------------------------------------------------------------ session tracking

        private void AttachCurrentSession()
        {
            if (_disposed || _manager == null) return;

            GlobalSystemMediaTransportControlsSession? next;
            try { next = _manager.GetCurrentSession(); }
            catch { next = null; }

            if (ReferenceEquals(next, _session) && next != null)
                return;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _session = next;

            if (_session == null)
            {
                ResetState();
                Updated?.Invoke(this, EventArgs.Empty);
                return;
            }

            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
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
            CanSeek = false;
            SourceApp = "";
            _basePosition = TimeSpan.Zero;
            _baseTime = DateTimeOffset.Now;
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => _dispatcher.InvokeAsync(async () =>
            {
                if (_session == null) return;
                await UpdateMediaPropertiesAsync();
            });

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => _dispatcher.InvokeAsync(() =>
            {
                if (_session == null) return;
                UpdatePlayback();
                UpdateTimeline();
                Updated?.Invoke(this, EventArgs.Empty);
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
                bool trackChanged = newTitle != Title || (props.Artist ?? "") != Artist;

                Title = newTitle;
                Artist = string.IsNullOrWhiteSpace(props.Artist) ? (props.AlbumArtist ?? "") : props.Artist;
                Album = props.AlbumTitle ?? "";
                if (trackChanged) Thumbnail = null;
                Updated?.Invoke(this, EventArgs.Empty);

                var thumb = await LoadThumbnailAsync(props.Thumbnail);
                if (version != _mediaVersion || !ReferenceEquals(s, _session)) return;
                Thumbnail = thumb;
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
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _session = null;
            }
            _manager = null;
        }
    }
}
