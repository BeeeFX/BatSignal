# Developing BatSignal

Everything the README deliberately leaves out, so the front page can stay a front page.

## Building from source

You'll need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build BatSignal.sln
dotnet test BatSignal.sln
dotnet run --project src/BatSignal.App
```

| Command | What it does |
| --- | --- |
| `dotnet run --project src/BatSignal.App` | Run the app |
| `dotnet run --project src/BatSignal.Probe` | List receivers, devices and battery readings in a console |
| `... BatSignal.Probe -- --raw` | Same, printing every HID++ report |
| `... BatSignal.Probe -- --bluetooth` | List Bluetooth devices and their battery |
| `... BatSignal.Probe -- --monitor` | Watch the app's polling loop for 40 seconds |
| `BatSignal.exe --preview <folder>` | Render the widget, tray icons, panel and Settings as PNGs |
| `powershell -File scripts/make-icon.ps1` | Regenerate `assets/icon.png` and `assets/icon.ico` |
| `powershell -File scripts/make-banner.ps1` | Regenerate `docs/banner.png` (after `--preview` into `docs/images`) |

The app writes a log to `%APPDATA%\BatSignal\log.txt`, next to `settings.json` (settings and last
readings) and `history.json` (battery history for the time-left estimate).

## How it's put together

```
src/
  BatSignal.Core/
    Hidpp/            Logitech HID++: receivers, devices, battery features 0x1000/0x1001/0x1004
    Bluetooth/        Bluetooth batteries: live GATT reads, Windows' stored value as a fallback
    Gamepads/         XInput, and DualSense / DualShock 4 / Switch Pro input reports
    Monitoring/       DeviceMonitor (polling thread), BatteryHistory (time left)
  BatSignal.App/
    TaskbarWidget*    The widget inside each taskbar, TaskbarLayout (UI Automation), WidgetTooltip
    TrayIcons         Notification-area fallback
    FlyoutWindow      The panel and its Settings page (WPF, all controls styled by hand)
    TrayAppContext    Wires it all together; alerts, history, updates
  BatSignal.Probe/    Console diagnostics
tests/BatSignal.Tests Parsers, history, wording, customization (no hardware needed)
```

## Reading batteries

**Logitech receivers** speak HID++. On Windows each receiver exposes two HID collections (short
reports `0x10`, long `0x11`); BatSignal opens both, shared, so G HUB and Options+ keep working. It
pings device slots 1–6, asks each device for its battery feature and reads it:

- `0x1004` Unified Battery gives a percentage and charging state.
- `0x1000` Battery Status gives a percentage, but some devices (MX Keys) only step 100/50/20/5. The
  "next level" field gives that away, and the UI shows `~50%`.
- `0x1001` Battery Voltage gives millivolts, mapped to a percentage on a Li-ion discharge curve.

Devices announce battery changes and (dis)connections, which trigger an early refresh; otherwise it
polls every 2 minutes. A sleeping device keeps its last reading.

**Bluetooth**: connected Bluetooth LE devices are read live from the GATT Battery service. For
everything else BatSignal reads the value Windows stores on the device node
(`{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`, what Settings shows), which also covers classic headsets.
Paired devices are looked up across every Bluetooth adapter, not just the default one.

**Gamepads**: XInput gives Xbox controllers' coarse level. PlayStation and Nintendo controllers are read
from their input reports (offsets in `GamepadReports.cs`, after the Linux drivers). BatSignal never
switches a controller into its full report mode, because games reading the basic mode would stop
getting input.

## The taskbar widget

Windows 11 has no API for adding things to the taskbar, so the widget is a layered child window
created inside `Shell_TrayWnd` (and each `Shell_SecondaryTrayWnd`). Things to know:

- Creating a child of Explorer's window attaches our UI thread's input to the taskbar's. A blocked UI
  thread would freeze the taskbar, so all device I/O runs on the monitor thread, and the UI Automation
  queries that find the app buttons and tray run on their own background thread.
- Every second it checks the taskbar still exists (Explorer restarts destroy it), re-asserts its place
  above the XAML content, and follows DPI and theme changes.
- If the app buttons would run under it, or it can't attach at all, the app falls back to tray icons.

## Releasing

```bash
git tag v0.2.0 && git push origin v0.2.0
```

The tag triggers `.github/workflows/release.yml`: tests, `dotnet publish` with the tag's version,
`vpk pack` (Velopack), and `vpk upload` to a **draft** GitHub release.

**Publish the draft** to ship it. Installed copies check `releases/latest` and never see drafts. The
README's download button links to `releases/latest/download/BatSignal-win-Setup.exe`, so it only works
once a release is published.

Installers aren't code-signed, so SmartScreen warns on first run.
