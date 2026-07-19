# Internet Speed Widget

A lightweight Windows 11 desktop widget (C# / WPF, .NET 8) that shows your
**live network throughput** — download (↓) and upload (↑) — for the active
physical network adapter, updated in real time.

It is **not** a speed test. It reads the byte counters of your network
interface(s) via `System.Net.NetworkInformation` and reports the delta over
time, so it reflects whatever your PC is actually sending/receiving right now.

![widget shows ↓ and ↑ speeds in a small rounded always-on-top card]

## Features

- **Borderless, transparent, rounded** card, optionally always-on-top.
- **Live ↓ / ↑ speeds** sampled from the active up / non-loopback / non-virtual
  adapters (all matching physical interfaces are summed), or one adapter of
  your choice.
- **Ping / latency** to a configurable host (default `8.8.8.8`).
- **Data usage totals** — bytes transferred today and this month, persisted to
  `usage.json` so they survive restarts.
- **Speed history sparkline** — the last 60 samples of ↓/↑ as a mini graph.
- **Themes** (Dark / Light / Black) and a **compact one-line layout**.
- **Drag anywhere** with the left mouse button; position is remembered across
  restarts. Or **dock to any screen corner**.
- **Click-through mode** — the widget ignores the mouse entirely (toggle from
  the tray menu).
- **Global hotkey** `Ctrl+Alt+S` shows/hides the widget.
- **Auto-hide during fullscreen apps** (games, videos) on the same monitor.
- **Live speeds drawn in the tray icon** (NetSpeedMonitor style) plus a live
  tooltip, or a static arrows icon if you prefer.
- **Hidden from Alt-Tab** and the taskbar (tool-window style, `ShowInTaskbar=false`).
- **Runs in the tray**: hiding/closing the widget keeps it running; **Exit**
  fully quits.
- **Single-instance** guard (named Mutex) — launching twice just reminds you
  it's already running.
- **Graceful no-network** handling: shows `—` when no usable adapter is up.
- **On-demand speed test** (Cloudflare) and a **24-hour history chart**
  (speed + ping), both in the tray menu.

### Experimental features (Settings → Experimental, all off by default)

| Feature | What it does |
|---|---|
| **Pin to desktop** | Glues the widget to the bottom of the z-order — behind every app window, resting on the desktop like a Rainmeter skin. Stays clickable and draggable. |
| **Traffic particles** | Animated particles drift down/up across the card at a rate proportional to real traffic. |
| **Geiger counter sound** | Subtle clicks whose rate follows your traffic (Poisson-random, like real radiation). |
| **Outage logger** | Pings every 5 s; 3 consecutive failures = outage. Logs to `outages.log`, shows `⚠ offline` on the widget, and pops a tray notification when the connection recovers (with the outage duration). |
| **Per-app top talker** | Shows which process is using the most bandwidth right now (ETW kernel-network tracing, same source Task Manager uses). **Requires running the app as Administrator.** |

## Settings

Right-click the widget (or use the tray menu → **Settings…**). Everything is
saved to `settings.json` and applied immediately.

| Setting | Options | Notes |
|---|---|---|
| **Opacity** | slider 20%–100% | Window opacity. |
| **Font size** | Small (12) / Medium (14) / Large (18) / Extra Large (22) | Base text size. |
| **Refresh interval** | 0.5s / 1s / 2s / 5s | Sampling / redraw cadence. |
| **Speed unit** | Mbps / MB/s / Auto | `Mbps` = megabits/sec, `MB/s` = megabytes/sec, `Auto` auto-scales bits (Kbps↔Mbps↔Gbps). |
| **Widget size** | Small / Medium / Large | Scales font, padding and corner radius. |
| **Theme** | Dark / Light / Black | Card background and text colors. |
| **Network adapter** | All (automatic) / specific adapter | Which interface's counters to measure. |
| **Dock to corner** | None / any of the 4 corners | Snaps to that corner of the work area (above the taskbar). Dragging cancels docking. |
| **Ping host** | any hostname/IP | Target for the latency measurement (default `8.8.8.8`). |
| **Compact one-line layout** | on / off | `↓ 24.5 Mbps  ↑ 3.2 Mbps` on a single line. |
| **Show ping / data usage / graph** | on / off each | Extra info rows on the widget. |
| **Show speeds in tray icon** | on / off | Live numbers drawn into the tray icon (download top, upload bottom). |
| **Always on top** | on / off | Keep the widget above all other windows (default on). |
| **Click-through** | on / off | Widget ignores all mouse input — use the tray icon to reach the menu. |
| **Hotkey Ctrl+Alt+S** | on / off | Global shortcut to show/hide the widget. |
| **Hide during fullscreen apps** | on / off | Auto-hides while a fullscreen app is active on the widget's monitor. |
| **Start with Windows** | on / off | See below. |

### Where settings are stored

```
%APPDATA%\InternetSpeedWidget\settings.json
```

(e.g. `C:\Users\<you>\AppData\Roaming\InternetSpeedWidget\settings.json`)

Delete this file to reset to defaults.

### How "Start with Windows" works

Two mechanisms, picked automatically:

- **Running elevated** (e.g. the exe has a "Run as administrator" compat
  flag): registers a logon **Scheduled Task** named `InternetSpeedWidget`
  with `RunLevel=HighestAvailable`. This starts the app elevated at logon
  with **no UAC prompt** — necessary because Windows silently ignores Run-key
  entries for elevated programs. The task is defined via XML so the default
  72-hour execution time limit is disabled.
- **Running normally**: writes the classic per-user Run key
  (`HKCU\...\CurrentVersion\Run`, value `InternetSpeedWidget`).

A legacy Run-key entry is auto-upgraded to the Scheduled Task form on the
first elevated launch. The exe path comes from `Environment.ProcessPath`.

### Taskbar docking caveat

Windows 11 **removed the deskband / DeskBand COM API**, so a widget can no
longer be embedded *inside* the taskbar the way old gadgets were. **Dock to
taskbar corner** is the practical substitute: it snaps the floating window to
the bottom-right corner of the work area, just above the taskbar near the
system clock. Combined with the tray icon, this gives a taskbar-adjacent
experience without any unsupported/deprecated APIs.

## Build & run

Requires the **.NET 8 SDK** (target framework `net8.0-windows`).

```powershell
# from the project root
dotnet build                 # Debug build
dotnet run                   # build + launch
```

### Publish a single-file exe (framework-dependent)

The .NET 8 desktop runtime must be present on the machine (it is on the dev
box). This produces one small `.exe`:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Output:

```
bin\Release\net8.0-windows\win-x64\publish\InternetSpeedWidget.exe
```

To make a fully self-contained build (no runtime needed on the target), add
`--self-contained true` instead — the exe will be much larger.

## Project layout

| File | Purpose |
|---|---|
| `InternetSpeedWidget.csproj` | WPF + WinForms (`UseWPF` / `UseWindowsForms`), net8.0-windows. |
| `app.manifest` | Per-monitor-V2 DPI awareness, Win10/11 compat. |
| `App.xaml` / `App.xaml.cs` | Startup, single-instance Mutex, explicit-shutdown lifetime. |
| `MainWindow.xaml` / `.cs` | The widget card, dragging, positioning/docking, tray icon, context menus. |
| `SettingsWindow.xaml` / `.cs` | Live settings UI. |
| `Settings.cs` | Settings model + JSON load/save in `%APPDATA%`. |
| `SpeedMonitor.cs` | Adapter byte-counter sampling, adapter list, speed formatting. |
| `UsageTracker.cs` | Daily/monthly data-usage totals, persisted to `usage.json`. |
| `FullscreenDetector.cs` | Detects a fullscreen foreground app (for auto-hide). |
| `TrayIconRenderer.cs` | Draws the tray icon (arrows or live speed numbers). |
| `CrashLogger.cs` | Appends unhandled exceptions to `error.log`. |
| `StartupManager.cs` | HKCU Run-key read/write for "Start with Windows". |

## Notes / limitations

- Throughput is measured from adapter counters, so VPN/virtual adapters are
  filtered out by name to avoid double-counting.
- The tray icon is drawn programmatically (no external asset), so there are no
  image dependencies.
- On very first launch (no saved position) the widget docks to the bottom-right
  corner by default.
- The widget always shows itself on launch. Hiding (tray menu, tray-icon
  double-click, or Alt+F4) lasts only for the current session.

## Troubleshooting

If the app ever exits unexpectedly, unhandled exceptions are logged to:

```
%APPDATA%\InternetSpeedWidget\error.log
```

Recoverable UI-thread exceptions are logged and swallowed so the widget keeps
running instead of silently disappearing. If the widget is gone but the tray
icon is still there, it was only hidden — double-click the tray icon to bring
it back.
