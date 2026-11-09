using System;
using System.Windows.Threading;

namespace Crest.Services
{
    /// <summary>All shared background services, created once and handed to every notch window.</summary>
    public sealed class CrestServices : IDisposable
    {
        public MediaSessionService Media { get; }
        public VolumeService Volume { get; }
        public AudioSpectrumService Analyzer { get; }
        public ForegroundWatcher Foreground { get; }
        public HotkeyService Hotkey { get; }
        public ClipboardService Clipboard { get; }
        public PowerService Power { get; }
        public BluetoothService Bluetooth { get; }
        public BrightnessService Brightness { get; }
        public PrivacyService Privacy { get; }
        public CalendarService Calendar { get; }
        public WeatherService Weather { get; }
        public LyricsService Lyrics { get; }
        public SystemStatsService Stats { get; }
        public TimerService Timer { get; }
        public ShelfService Shelf { get; }
        public NotesService Notes { get; }

        public CrestServices(Dispatcher dispatcher)
        {
            Media = new MediaSessionService(dispatcher);
            Volume = new VolumeService(dispatcher);
            Analyzer = new AudioSpectrumService();
            Foreground = new ForegroundWatcher(dispatcher);
            Hotkey = new HotkeyService();
            Clipboard = new ClipboardService(dispatcher);
            Power = new PowerService(dispatcher);
            Bluetooth = new BluetoothService(dispatcher);
            Brightness = new BrightnessService(dispatcher);
            Privacy = new PrivacyService();
            Calendar = new CalendarService();
            Weather = new WeatherService();
            Lyrics = new LyricsService();
            Stats = new SystemStatsService();
            Timer = new TimerService();
            Shelf = new ShelfService();
            Notes = new NotesService();
        }

        /// <summary>Starts everything that runs on its own.</summary>
        public void Start()
        {
            Volume.Initialize();
            Foreground.Start();
            Bluetooth.Start();
            Brightness.Start();
            _ = Media.InitializeAsync();
        }

        public void Dispose()
        {
            void Safe(Action a)
            {
                try { a(); } catch (Exception ex) { App.LogError("Shutdown", ex); }
            }

            Safe(() => Notes.Flush());
            Safe(Analyzer.Dispose);
            Safe(Volume.Dispose);
            Safe(Media.Dispose);
            Safe(Foreground.Dispose);
            Safe(Hotkey.Dispose);
            Safe(Clipboard.Dispose);
            Safe(Power.Dispose);
            Safe(Bluetooth.Dispose);
            Safe(Brightness.Dispose);
            Safe(Privacy.Dispose);
            Safe(Stats.Dispose);
        }
    }
}
