using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Crest.Helpers;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Crest.Services
{
    /// <summary>
    /// The switches on the Controls page: dark mode, Bluetooth, Wi-Fi, keep awake and "pause pop-ups".
    /// Shared by every notch window so they all show the same state.
    /// </summary>
    public sealed class QuickTogglesService : IDisposable
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public event EventHandler? Changed;

        public bool KeepAwake { get; private set; }
        /// <summary>Crest's own "do not disturb": no pop-ups (the volume indicator still works).</summary>
        public bool PopupsPaused { get; private set; }
        public bool? Bluetooth { get; private set; }   // null = no radio / not allowed
        public bool? WiFi { get; private set; }

        public bool DarkMode
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void SetDarkMode(bool dark)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
                key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                // Tell Explorer and apps to repaint with the new theme.
                NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE, IntPtr.Zero,
                    "ImmersiveColorSet", NativeMethods.SMTO_ABORTIFHUNG, 200, out _);
            }
            catch (Exception ex)
            {
                App.LogError("Switching dark mode failed", ex);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetKeepAwake(bool on)
        {
            KeepAwake = on;
            NativeMethods.SetThreadExecutionState(on
                ? NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED
                : NativeMethods.ES_CONTINUOUS);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetPopupsPaused(bool paused)
        {
            PopupsPaused = paused;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ radios

        public async Task RefreshRadiosAsync()
        {
            Bluetooth = await ReadRadioAsync(RadioKind.Bluetooth);
            WiFi = await ReadRadioAsync(RadioKind.WiFi);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public async Task SetRadioAsync(bool bluetooth, bool on)
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed) return;
                var kind = bluetooth ? RadioKind.Bluetooth : RadioKind.WiFi;
                var radios = await Radio.GetRadiosAsync();
                foreach (var r in radios.Where(r => r.Kind == kind))
                    await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
            }
            catch (Exception ex)
            {
                App.LogError("Switching a radio failed", ex);
            }
            await RefreshRadiosAsync();
        }

        private static async Task<bool?> ReadRadioAsync(RadioKind kind)
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                var r = radios.FirstOrDefault(x => x.Kind == kind);
                return r == null ? null : r.State == RadioState.On;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Night light has no public switch, so this opens its page in Windows Settings.</summary>
        public static void OpenNightLightSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError("Opening Night light settings failed", ex); }
        }

        public void Dispose()
        {
            if (KeepAwake) NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        }
    }
}
