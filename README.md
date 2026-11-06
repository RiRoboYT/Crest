# Crest

A lightweight, Nusa-style notch for Windows 10/11, written in C# + WPF (.NET 8).

* **Collapsed:** a black notch at the top center of the screen showing the time and a small fake spectrum. The bars only move while something is playing.
* **Expanded (on hover):** date, time, album art, title and artist, a progress bar you can click to seek, previous / play-pause / next, a mute button and a settings button.
* **Media:** uses the Windows media session API (`Windows.Media.Control`), so it works with Spotify, YouTube in Chrome, Edge or Firefox, Windows Media Player, and any other app that shows up in the Windows volume flyout.
* **Settings window:** size, corner radius, shape (notch or pill), offset, colors, font, spectrum, animation and behavior. Changes apply live and are auto-saved to `settings.json`.
* **Tray icon:** left-click opens Settings. Right-click gives Settings, Show notch, Start with Windows and Exit.
* **Topmost:** the notch stays on top, including over fullscreen video and borderless-fullscreen games.

## Project layout

```
Crest/
├── Crest.csproj
├── app.manifest
├── AssemblyInfo.cs
├── App.xaml / App.xaml.cs                  ← startup, single instance, tray, wiring
├── MainWindow.xaml / .cs                   ← the notch
├── SettingsWindow.xaml / .cs               ← settings UI (live-bound)
├── Models/AppSettings.cs                   ← all options (INotifyPropertyChanged)
├── Services/
│   ├── MediaSessionService.cs              ← Windows media session wrapper
│   ├── SpectrumAnimator.cs                 ← fake spectrum bars
│   ├── SettingsManager.cs                  ← JSON load/save (debounced)
│   ├── StartupManager.cs                   ← "Start with Windows" (HKCU Run key)
│   └── TrayIconService.cs                  ← NotifyIcon + menu
└── Helpers/
    ├── NativeMethods.cs                    ← Win32 interop
    └── ColorHelper.cs                      ← hex parsing + XAML converter
```

## Build

You need the .NET 8 SDK on Windows.

You can build this folder as it is. Or, to start from a template:

```bash
dotnet new wpf -n Crest
```

Then copy **every** file from this repo into the new folder and overwrite what's there. You must replace `Crest.csproj`, because it targets `net8.0-windows10.0.19041.0` for the media API and turns on WinForms for the tray icon. You must also replace `App.xaml`, because it has no `StartupUri`.

Run it:

```bash
dotnet run
```

Publish a single `.exe`:

```bash
dotnet publish -c Release
```

The output is `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Crest.exe`. The Release settings in the csproj already make it self-contained, single-file and compressed. For a much smaller exe that needs the .NET 8 Desktop Runtime installed, run this instead:

```bash
dotnet publish -c Release -p:SelfContained=false
```

## Usage

| Action | What it does |
|---|---|
| Hover the notch | Expands it into the player |
| Right-click the notch | Opens Settings |
| Scroll on the notch | Changes the system volume (you can turn this off) |
| Click the progress bar | Seeks, if the app supports it |
| Tray icon | Settings, Show/hide notch, Start with Windows, Exit |

Settings are saved to `settings.json` next to `Crest.exe`. If that folder isn't writable, they go to `%AppData%\Crest\settings.json` instead. Errors are logged to `%LocalAppData%\Crest\crest-error.log`.

## Notes and limitations

* **Fullscreen:** the notch is a topmost tool window, and Crest re-asserts `HWND_TOPMOST` every 2 s. That keeps it above browser fullscreen video, media players and borderless or windowed-fullscreen games. Games running in true *exclusive* fullscreen (DirectX exclusive mode) own the display, and no normal window can draw over them. Switch those games to borderless mode.
* **Focus:** the window is `WS_EX_NOACTIVATE`, so clicking the notch never takes focus away from your game or app.
* **Spectrum:** the bars are fake on purpose. Nothing captures audio, which keeps CPU use near zero.
* **Memory:** WPF's baseline working set is usually about 40–60 MB. To keep it low, Crest uses workstation non-concurrent GC, decodes thumbnails at 160 px, and stops all timers and animations when idle. Task Manager's "Memory" column shows the private working set, which is usually well below the total.
* **Start with Windows:** writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Crest`. This needs no admin rights, and you can also toggle it in Task Manager → Startup apps.
* **Multiple monitors:** the notch is placed on the primary monitor.
