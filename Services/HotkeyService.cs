using System;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>Global Win + Alt + N hotkey that opens / closes the notch.</summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 0xC5E1;
        private readonly MessageWindow _window = new("CrestHotkeys");
        private bool _registered;

        public event EventHandler? Pressed;

        public HotkeyService()
        {
            _window.Message += (msg, wParam, _) =>
            {
                if (msg != NativeMethods.WM_HOTKEY || wParam.ToInt32() != HotkeyId) return false;
                Pressed?.Invoke(this, EventArgs.Empty);
                return true;
            };
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled == _registered) return;
            if (enabled)
            {
                _registered = NativeMethods.RegisterHotKey(_window.Handle, HotkeyId,
                    NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_N);
                if (!_registered)
                    App.LogError("Win+Alt+N is already used by another app", null);
            }
            else
            {
                NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
                _registered = false;
            }
        }

        public void Dispose()
        {
            SetEnabled(false);
            _window.Dispose();
        }
    }
}
