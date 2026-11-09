using System;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>Global hotkeys: Win + Alt + N opens / closes the notch, Win + Alt + M mutes the microphone.</summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 0xC5E1;
        private const int MicHotkeyId = 0xC5E2;
        private bool _micRegistered;

        public event EventHandler? MicPressed;
        private readonly MessageWindow _window = new("CrestHotkeys");
        private bool _registered;

        public event EventHandler? Pressed;

        public HotkeyService()
        {
            _window.Message += (msg, wParam, _) =>
            {
                if (msg != NativeMethods.WM_HOTKEY) return false;
                int id = wParam.ToInt32();
                if (id == HotkeyId) Pressed?.Invoke(this, EventArgs.Empty);
                else if (id == MicHotkeyId) MicPressed?.Invoke(this, EventArgs.Empty);
                else return false;
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

        public void SetMicEnabled(bool enabled)
        {
            if (enabled == _micRegistered) return;
            if (enabled)
            {
                _micRegistered = NativeMethods.RegisterHotKey(_window.Handle, MicHotkeyId,
                    NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_M);
                if (!_micRegistered)
                    App.LogError("Win+Alt+M is already used by another app", null);
            }
            else
            {
                NativeMethods.UnregisterHotKey(_window.Handle, MicHotkeyId);
                _micRegistered = false;
            }
        }

        public void Dispose()
        {
            SetEnabled(false);
            SetMicEnabled(false);
            _window.Dispose();
        }
    }
}
