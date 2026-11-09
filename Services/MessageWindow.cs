using System;
using System.Windows.Interop;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>
    /// Invisible message-only window used by services that need Windows messages (hotkeys, clipboard)
    /// without depending on any notch window (there can be one notch per monitor).
    /// </summary>
    public sealed class MessageWindow : IDisposable
    {
        private readonly HwndSource _source;

        public IntPtr Handle => _source.Handle;

        /// <summary>(msg, wParam, lParam) → true if handled.</summary>
        public event Func<int, IntPtr, IntPtr, bool>? Message;

        public MessageWindow(string name)
        {
            var p = new HwndSourceParameters(name)
            {
                ParentWindow = NativeMethods.HWND_MESSAGE,
                WindowStyle = 0,
                Width = 0,
                Height = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            var h = Message;
            if (h != null && h(msg, wParam, lParam)) handled = true;
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
