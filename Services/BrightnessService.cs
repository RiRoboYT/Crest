using System;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Crest.Services
{
    /// <summary>
    /// Built-in display brightness changes (laptops / tablets) via WMI. On desktop monitors
    /// Windows doesn't expose brightness this way, so the service simply stays inactive.
    /// </summary>
    public sealed class BrightnessService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private ManagementEventWatcher? _watcher;

        /// <summary>Brightness 0..100 (UI thread).</summary>
        public event Action<int>? Changed;

        public bool IsAvailable { get; private set; }

        public BrightnessService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>Starts in the background – WMI can take a moment to answer.</summary>
        public void Start()
        {
            Task.Run(() =>
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                    using (var results = searcher.Get())
                    {
                        if (results.Count == 0) return;
                    }

                    var watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                        new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                    watcher.EventArrived += (_, e) =>
                    {
                        try
                        {
                            int value = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                            _dispatcher.InvokeAsync(() => Changed?.Invoke(value));
                        }
                        catch
                        {
                            // malformed event
                        }
                    };
                    watcher.Start();
                    _watcher = watcher;
                    IsAvailable = true;
                }
                catch
                {
                    // No WMI brightness (desktop monitor) – nothing to do.
                    IsAvailable = false;
                }
            });
        }

        public void Dispose()
        {
            try
            {
                _watcher?.Stop();
                _watcher?.Dispose();
            }
            catch
            {
                // ignore on shutdown
            }
            _watcher = null;
        }
    }
}
