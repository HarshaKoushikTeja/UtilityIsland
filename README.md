# Dynamic Island for Windows 11

An iPhone-style Dynamic Island that sits at the top centre of the screen, with a pet that lives inside it.
C# / .NET 10 / WPF.

## Build and run

1. Install the .NET 10 SDK (the SDK, not just the runtime) from https://dotnet.microsoft.com/download/dotnet/10.0
2. In this folder: `dotnet run`

To give it to testers: run `publish.bat`. It produces a single `dist\DynamicIsland.exe` that runs on a PC without .NET installed.

The control panel opens on first launch, and afterwards from the island's icon near the clock or the gear inside the island. Quit from the control panel or the tray icon's menu.

## Control panel

- **Pet**: choose and name the pet.
- **Appearance**: display, position, size, idle width, shape (pill, rounded, square), colour (black, graphite, midnight blue, glass), accent colour (including "match album art"), lock position.
- **Features**: switch each feature and tab on or off, and choose click-to-open or open-on-hover; changes apply immediately.
- **Try it out**: buttons that trigger every island state (charging, Bluetooth, call, music, timer and so on) without waiting for the real event.
- **Help and privacy**: how to use it and what it does with data.

## What it does

**Island behaviour**
- Black pill, always on top, never takes focus, hidden from Alt-Tab.
- Spring-animated morph between idle, compact and expanded.
- Click to expand (or rest the mouse on it, if open-on-hover is on); move the mouse away to collapse.
- Drag it anywhere on any display; drop it near the top centre to snap back. In the lower half of a screen it opens upwards.
- Stays on top even when other apps shuffle always-on-top windows (WhatsApp does this while it loads).
- Sharp and correctly placed on displays with different scaling (per-monitor DPI).
- A second activity appears as a separate bubble beside the pill.
- When an app goes fullscreen on the island's display, the island moves to a free display and returns when its own display is free again; with no free display it pauses (optional). Displays are tracked by name, so a game changing resolution or switching screens doesn't confuse it.

**Activities**
| Activity | Trigger |
|---|---|
| Now Playing (art, title, controls) | Anything in the Windows media flyout |
| Audio visualizer | Live spectrum of system audio while music plays |
| Call / meeting | Any app using the mic or camera (Teams, Zoom, Discord, Meet in a browser) |
| Timer | 5 / 15 / 25 minute buttons in the expanded view |
| Charging, on battery, low battery | Power events |
| Bluetooth connected | A device connects |
| Volume | Volume or mute changes |
| Lock / unlock | The pet falls asleep when the PC locks; on unlock it wakes, stretches and the lock pops open ("Welcome back") |

**Expanded tabs**
- **Now**: call controls, media controls, timer.
- **Volume**: one slider for everything, plus a slider and mute button for each app that is currently playing sound. Scrolling the mouse wheel over the closed island also changes the system volume.
- **File shelf**: drag files onto the island to park them; drag them out to any app or folder; click to open. Survives restarts.
- **Clipboard**: last 25 text copies, click to copy again. Kept in memory only, and copies flagged private by password managers are skipped.
- **System**: CPU, memory, GPU (NVIDIA), battery, network speed.

**Pet**
Cat, dog, fox, bunny or slime. Name it in Settings.
- It roams inside the pill, sits, blinks, bounces while music plays and naps after two minutes of inactivity. Click it to pet it.
- The **Now** tab has a playground: **Pet**, **Feed** (drops a treat it walks over and eats) and **Play** (throws a ball it chases).
- Three meters (Happy, Food, Energy) drift over time and change its mood line and walking pace. Nothing bad happens if you ignore it.

## Call controls: what they can and can't do

- Mic mute is system-wide: it mutes the default microphone at the Windows level, so it works in every app, but the app's own mute button won't reflect it.
- Speaker mute and "bring call window to front" work everywhere.
- There is no hang-up button: Windows has no common way to end a call across apps.

## Layout

```
App.xaml(.cs)            startup, single instance, crash log
MainWindow.xaml(.cs)     the island: layout, state, animation, all tabs
SettingsWindow.xaml(.cs) the control panel: pet, feature switches, test buttons, help
publish.bat              builds the single exe for testers
Core/Spring.cs           spring physics for the pill
Core/Native.cs           Win32 calls
Core/AppSettings.cs      settings.json in %AppData%\DynamicIslandWin
Core/Models.cs           shelf / clipboard items, icon codes
Pets/PetControl.cs       the pets, drawn from shapes
Pets/PetPlayground.cs    feeding and ball game in the expanded view
Services/MediaService.cs     now playing
Services/AudioService.cs     visualizer, volume, mic mute (NAudio)
Services/CallService.cs      mic / camera in-use detection
Services/BluetoothService.cs device connections
Services/SystemStats.cs      CPU, RAM, GPU, battery, network
```

## Before a public release

- Code-sign the exe (or ship through the Microsoft Store). Unsigned, Windows SmartScreen warns on every download.
- Add a way to deliver updates, and sign those too.
- Run `dotnet list package --vulnerable` before each release.
- Publish a short privacy statement; the app touches the clipboard, system audio and mic/camera status.

## Not done yet

- Notifications (needs MSIX packaging and a permission prompt).
- A public API so other apps and scripts can push their own activities.
