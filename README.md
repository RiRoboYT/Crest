# Crest

A lightweight, Nusa-style notch for Windows 10/11, written in C# + WPF (.NET 8).

* **Collapsed:** a black notch at the top of the screen showing the time and a small spectrum. The bars only move while something is playing.
* **Peek:** when a new song starts, the notch briefly opens to show the cover and title, then closes again.
* **Expanded (on hover):** date, time, album art, a scrolling title and artist, a seekable progress bar, previous / play-pause / next, a real volume slider and a settings button.
* **Media:** uses the Windows media session API, so it works with Spotify, YouTube in Chrome, Edge or Firefox, Windows Media Player, and any other app that shows up in the Windows volume flyout. It follows whichever app is actually playing.
* **Settings window:** dark or light design with a sidebar, live preview, toggle switches and one-click theme presets. Changes apply live and are auto-saved to `settings.json`.
* **Tray icon:** left-click opens Settings. Right-click gives Settings, Show notch, Start with Windows and Exit.
* **Topmost:** the notch stays on top, including over fullscreen video and borderless-fullscreen games.

## What's new in 1.1

| Area | Change |
|---|---|
| Hover | Waits about 150 ms before opening, so passing over it on the way to browser tabs doesn't open it. Adjustable. |
| Media | Follows the app that is actually playing, not just the one Windows calls "current". |
| Stay on top | Reacts the moment another window comes to the front, plus a slow backup timer. |
| Screens | Per-monitor DPI (sharp at any scaling) and a **Show on monitor** option. |
| Volume | Real Windows volume slider and a mute icon that updates live. Scroll on the open notch to change it. |
| Titles | Long titles scroll slowly instead of being cut off. |
| Peek | The notch briefly opens to show the new song. |
| Album colors | Spectrum and progress bar can take the main color of the cover art. |
| Click to open | Click the song to bring Spotify or your browser to the front. |
| Settings | Completely redesigned, with Dark / Light / Follow Windows themes and a live preview. |
| Presets | Classic, Glass, Album and Light. |
| Real spectrum | Optional mode where the bars follow the actual sound. Off by default. |

## Website

`docs/index.html` is the Crest landing page: a single self-contained file with a live, interactive notch demo.

* **Host it free with GitHub Pages:** on GitHub, open **Settings → Pages**, choose **Deploy from a branch**, pick your branch and the **`/docs`** folder.
* **Make the Download button work:** publish a GitHub **Release** and attach the built `Crest.exe` as an asset. The button links to `releases/latest/download/Crest.exe`, so it always serves the newest release.
* **Change links or version:** edit the `CONFIG` block at the top of the `<script>` in `docs/index.html`.

## Project layout

```
Crest/
├── Crest.csproj
├── app.manifest                         ← per-monitor DPI
├── AssemblyInfo.cs
├── App.xaml / App.xaml.cs               ← startup, single instance, tray, wiring
├── MainWindow.xaml / .cs                ← the notch (collapsed / peek / expanded)
├── SettingsWindow.xaml / .cs            ← settings UI with live preview
├── Themes/SettingsStyles.xaml           ← dark/light control styles for the settings window
├── Controls/MarqueeText.cs              ← scrolling title
├── Models/
│   ├── AppSettings.cs                   ← all options
│   └── ThemePresets.cs                  ← Classic / Glass / Album / Light
├── Services/
│   ├── MediaSessionService.cs           ← Windows media sessions (follows the playing app)
│   ├── VolumeService.cs                 ← Windows volume via NAudio
│   ├── AudioSpectrumService.cs          ← optional real spectrum (loopback + FFT)
│   ├── SpectrumAnimator.cs              ← fake / real bars
│   ├── AppActivator.cs                  ← "click the song to open the app"
│   ├── SettingsManager.cs               ← JSON load/save (debounced)
│   ├── StartupManager.cs                ← "Start with Windows" (HKCU Run key)
│   └── TrayIconService.cs               ← NotifyIcon + menu
└── Helpers/
    ├── NativeMethods.cs                 ← Win32 interop
    ├── MonitorHelper.cs                 ← monitor list, DPI
    ├── ColorHelper.cs                   ← hex parsing, HSV, album-art color
    ├── SettingsTheme.cs                 ← dark/light palettes
    └── Converters.cs
```

## Build

You need the .NET 8 SDK on Windows. NuGet restores **NAudio.Wasapi** automatically on the first build.

Run it:

```bash
dotnet run
```

Publish a single `.exe`:

```bash
dotnet publish -c Release
```

The output is `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Crest.exe`. The Release settings in the csproj already make it self-contained, single-file and compressed.

## Usage

| Action | What it does |
|---|---|
| Hover the notch | Opens the player (after the hover delay) |
| Right-click the notch | Opens Settings |
| Scroll on the notch | Changes the volume |
| Click the song title or cover | Brings the playing app to the front |
| Click the progress bar | Seeks, if the app supports it |
| Tray icon | Settings, Show/hide notch, Start with Windows, Exit |

Settings are saved to `settings.json` next to `Crest.exe`. If that folder isn't writable, they go to `%AppData%\Crest\settings.json` instead. Errors are logged to `%LocalAppData%\Crest\crest-error.log`.

## Notes and limitations

* **Fullscreen:** games running in true *exclusive* fullscreen own the display, and no normal window can draw over them. Use borderless mode.
* **Glass preset:** it's translucent, not blurred. Real background blur isn't possible on a click-through overlay window like this.
* **Click to open:** for browsers, the browser window comes to the front, but Windows doesn't say which tab is playing.
* **Real spectrum:** it listens to what Windows is playing (loopback) without recording or saving anything. It only runs while music plays, and uses a little more CPU than the fake bars.
* **Memory:** WPF's baseline working set is usually about 40–60 MB.
