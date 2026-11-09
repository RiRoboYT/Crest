# Crest

A lightweight, Nusa-style notch for Windows 10/11, written in C# + WPF (.NET 8).

* **Collapsed:** a black notch at the top of the screen showing the time and a small spectrum. The bars only move while something is playing.
* **Peek:** when a new song starts, the notch briefly opens to show the cover and title, then closes again.
* **Expanded (on hover):** date, time, album art, a scrolling title and artist, a seekable progress bar, previous / play-pause / next, a real volume slider and a settings button.
* **Media:** uses the Windows media session API, so it works with Spotify, YouTube in Chrome, Edge or Firefox, Windows Media Player, and any other app that shows up in the Windows volume flyout. It follows whichever app is actually playing.
* **Settings window:** dark or light design with a sidebar, live preview, toggle switches and one-click theme presets. Changes apply live and are auto-saved to `settings.json`.
* **Tray icon:** left-click opens Settings. Right-click gives Settings, Show notch, Start with Windows and Exit.
* **Topmost:** the notch stays on top, including over fullscreen video and borderless-fullscreen games.

## What's new in 1.3

| Area | Change | Where in Settings |
|---|---|---|
| Closed notch | Tiny album cover on the left while music plays. | Media → Now playing |
| Pop-ups | Caps Lock / Num Lock, low battery (20 % / 10 %) and fully charged. | Activities → Pop-ups |
| Microphone | **Win + Alt + M** mutes the mic in every app; a red mic shows in the notch while muted. | Activities → Microphone |
| Smooth transitions | Play/pause icons morph, the clock rolls, new covers and lyric lines fade in. | Appearance → Effects |
| Player | Shuffle and repeat buttons, plus a speaker button for the Sound page. | Media → Expanded player |
| Sound page | Switch speakers / headphones and set the volume of each app. | Widgets → Pages |
| Spectrum styles | Mirrored, Bars, bouncing Dots or Wave. | Spectrum → Mode |
| Lyrics | "Lyrics by LRCLIB" credit and − / + 0.5 s timing buttons, remembered per song. | Media → Lyrics |
| Controls page | Dark mode, Bluetooth, Wi-Fi, keep awake, pause pop-ups, mute mic, focus, night light. | Widgets → Pages |
| Focus timer | Work / break cycles on the Timer page and in the side bubble; quiet while focusing. | Widgets → Timer & focus |
| Screenshots | New screenshots land on the shelf automatically. | Widgets → Shelf & internet |
| Clipboard | Copied images show up too (newest 3, memory only). | Widgets → Clipboard |
| Internet speed | On the System page, and optionally in the closed notch. | Widgets → Shelf & internet |
| Backup | Export / import all settings to a file. | About → Files |
| Welcome tour | Three quick cards on first launch; can be replayed. | About → Tips |
| Presets | New: Neon, Sunset, Ocean, Minimal; and "a different cover color each song". | Appearance / Media |
| Hide while typing | The notch shrinks to a line while you type. Off by default. | Behavior → General |
| Colors | Colored page icons in the notch and colored icons in the settings sidebar. | – |

## What's new in 1.2

| Area | Change |
|---|---|
| Lyrics | Synced lyrics (LRCLIB, free) on a Lyrics page, optionally as a line in the closed notch. |
| Activities | Pop-ups when the charger is plugged in or out, Bluetooth headphones connect (with battery), a meeting is about to start, or a timer ends. |
| Split island | A running timer or stopwatch shows as a small bubble next to the notch. |
| Volume / brightness | A level indicator in the notch whenever volume (or laptop brightness) changes. |
| Pages | The open notch has pages: Music · Lyrics · Calendar · Weather · System · Timer · Shelf · Notes. Swipe sideways or click the icons. |
| File shelf | Drag files onto the notch to park them; drag them out again later. |
| Privacy dots | Green / orange dot while any app uses the camera / microphone. |
| Gestures | Swipe sideways to skip songs, double-click to play/pause, middle-click to mute, Win + Alt + N to open. |
| Notes & clipboard | A quick note, and (optional) your last few copied texts. |
| Look | Ambient glow in the cover's colors that pulses with the beat and breathes between the cover's main colors (adjustable intensity), plus springy animations. |
| Monitors | A notch on every monitor, if you like. |
| Per-app rules | Hide Crest, or keep it quiet, while specific apps are in front. Also quiet during fullscreen. |

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
* **Download button:** it serves `docs/downloads/Crest.zip` (the whole project, without the website) straight from the site. After changing the code, refresh it from the repo root with `git archive --format=zip --prefix=Crest/ -o docs/downloads/Crest.zip HEAD -- . ":(exclude)docs"` and commit it.
* **Change links or version:** edit the `CONFIG` block at the top of the `<script>` in `docs/index.html`.

## Project layout

```
Crest/
├── Crest.csproj
├── app.manifest                         ← per-monitor DPI
├── AssemblyInfo.cs
├── App.xaml / App.xaml.cs               ← startup, single instance, tray, wiring
├── MainWindow.xaml / .cs                ← the notch: window, states, media, volume
├── MainWindow.Pages.cs                  ← expanded pages (lyrics, calendar, weather, system, timer, shelf, notes)
├── MainWindow.Activities.cs             ← pop-ups, volume/brightness indicator, timer bubble, privacy dots
├── MainWindow.Glow.cs                   ← ambient album glow: colors, breathing, beat pulse, click-through
├── MainWindow.Extras.cs                 ← 1.3: cover in the closed notch, Sound & Controls pages, mic / lock keys / battery pop-ups, smooth transitions, hide while typing
├── TourWindow.cs                        ← first-run welcome tour
├── MainWindow.Input.cs                  ← gestures, hotkey, drag & drop, typing
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
│   ├── LyricsService.cs                 ← synced lyrics from LRCLIB
│   ├── CalendarService.cs               ← iCal (.ics) calendar + reminders
│   ├── WeatherService.cs                ← Open-Meteo weather
│   ├── SystemStatsService.cs            ← CPU / RAM / GPU / disk
│   ├── TimerService.cs                  ← timer + stopwatch
│   ├── ShelfService.cs                  ← file shelf + notes
│   ├── PowerService.cs, BluetoothService.cs, BrightnessService.cs, PrivacyService.cs
│   ├── ForegroundWatcher.cs             ← stay-on-top, fullscreen detection, per-app rules
│   ├── HotkeyService.cs, ClipboardService.cs, MessageWindow.cs
│   ├── MicService.cs                    ← microphone mute (Win + Alt + M)
│   ├── AudioDevicesService.cs           ← output device switching + per-app volume
│   ├── KeyboardHookService.cs           ← Caps / Num Lock + "hide while typing" (key down/up only)
│   ├── NetworkSpeedService.cs, QuickTogglesService.cs
│   ├── CrestServices.cs                 ← creates all shared services
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

You need the .NET 8 SDK on Windows. NuGet restores **NAudio.Wasapi**, **System.Management** and **System.Diagnostics.PerformanceCounter** automatically on the first build (internet needed once).

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
* **Calendar:** Windows doesn't let normal desktop apps read its built-in calendar, so Crest uses your calendar's private iCal link instead. Repeating events (daily / weekly / monthly) are supported; all-day events are skipped.
* **Volume indicator:** Windows' own volume pop-up can't be hidden from outside, so it may still appear when you use the volume keys. Scrolling on the notch changes the volume directly and only shows Crest's indicator.
* **System page:** shows CPU, memory, GPU usage and disk. Temperatures need vendor tools and admin rights, so they aren't included.
* **Album glow:** while the glow is showing, the overlay turns click-through whenever the cursor is away from the notch, so the light never blocks clicks on the app below. "Pulse with the music" shares the loopback listener with the real spectrum; when no sound can be read (for example an app using exclusive audio mode) it falls back to a calm built-in beat.
* **Sound output switching:** Windows has no public API for changing the default speakers, so Crest uses the same internal interface as Windows' own sound settings (also used by tools like SoundSwitch). Some apps keep playing on the old device until they're restarted.
* **Controls page:** Bluetooth and Wi-Fi switches may ask Windows for permission the first time. Night light has no public switch, so its tile opens the Night light page in Settings. "Pause pop-ups" is Crest's own do-not-disturb – Windows' Focus / Do Not Disturb can't be switched by other apps.
* **Caps Lock and "hide while typing":** these use a low-level keyboard hook that only notices that a key went down. Nothing you type is read, stored or sent. The hook only runs while one of those two options is on.
* **Shuffle / repeat:** only works when the playing app allows it from outside (Spotify does; many web players don't – the buttons are greyed out then).
* **Memory:** WPF's baseline working set is usually about 40–60 MB.
