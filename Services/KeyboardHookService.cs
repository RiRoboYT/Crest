using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Crest.Helpers;

namespace Crest.Services
{
    /// <summary>
    /// A shared low-level keyboard hook, used for the Caps Lock / Num Lock pop-up and for
    /// "hide while typing". It only reports which key went down or up; nothing is stored, logged
    /// or sent anywhere. Installed only while a feature needs it (reference-counted).
    /// </summary>
    public sealed class KeyboardHookService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly NativeMethods.LowLevelKeyboardProc _proc;   // kept alive for the hook's lifetime
        private IntPtr _hook;
        private int _users;

        /// <summary>A key went down (vk, true) or up (vk, false). Raised on the UI thread, after the hook returns.</summary>
        public event Action<int, bool>? Key;

        public KeyboardHookService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _proc = HookProc;
        }

        public void AddUser()
        {
            if (_users++ > 0) return;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                App.LogError("Keyboard hook could not be installed (error " + Marshal.GetLastWin32Error() + ")", null);
        }

        public void RemoveUser()
        {
            if (_users == 0) return;
            if (--_users > 0) return;
            Uninstall();
        }

        private void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
                if (down || up)
                {
                    int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode is the first field
                    // Hooks must return fast, so the work happens a moment later on the dispatcher.
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Key?.Invoke(vk, down)));
                }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            _users = 0;
            Uninstall();
        }
    }

    /// <summary>Raises <see cref="Changed"/> when Caps Lock or Num Lock is switched on or off.</summary>
    public sealed class LockKeysService : IDisposable
    {
        private readonly KeyboardHookService _hook;
        private bool _enabled;
        private bool _caps, _num;
        private bool _capsDown, _numDown;

        /// <summary>("Caps Lock" | "Num Lock", isOn)</summary>
        public event Action<string, bool>? Changed;

        public LockKeysService(KeyboardHookService hook)
        {
            _hook = hook;
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled == _enabled) return;
            _enabled = enabled;
            if (enabled)
            {
                // A background app can't re-read the toggle state reliably later, so read it once now
                // and then follow the key presses.
                _caps = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
                _num = (NativeMethods.GetKeyState(NativeMethods.VK_NUMLOCK) & 1) != 0;
                _hook.Key += OnKey;
                _hook.AddUser();
            }
            else
            {
                _hook.Key -= OnKey;
                _hook.RemoveUser();
            }
        }

        private void OnKey(int vk, bool down)
        {
            if (vk == NativeMethods.VK_CAPITAL)
            {
                if (down && !_capsDown) { _caps = !_caps; Changed?.Invoke("Caps Lock", _caps); }
                _capsDown = down;   // holding the key repeats key-downs; only the first one toggles
            }
            else if (vk == NativeMethods.VK_NUMLOCK)
            {
                if (down && !_numDown) { _num = !_num; Changed?.Invoke("Num Lock", _num); }
                _numDown = down;
            }
        }

        public void Dispose() => SetEnabled(false);
    }
}
