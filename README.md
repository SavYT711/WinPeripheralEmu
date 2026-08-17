# WinPeripheralEmu

A Windows mouse and keyboard emulated as a Bluetooth peripheral for use across
devices. Slide the pointer into a chosen screen edge and your real trackpad and
keyboard are redirected to a nearby device (an iPad, in practice); a hotkey or a
swipe back toward the edge returns control to Windows.

The app ships as **BlePeripheralEmu** — a tray application with no console window.

## How it works

The PC advertises itself as a BLE HID-over-GATT peripheral exposing a combo
keyboard (Report ID 1) and mouse (Report ID 2), alongside Device Information and
Battery services. Once the iPad pairs with it, the app watches for the pointer
reaching your configured screen edge:

- **Before handoff** — a low-level mouse hook only watches for the edge crossing.
- **After handoff** — mouse and keyboard input are suppressed locally and
  forwarded over BLE instead. Movement comes from Raw Input, so deltas aren't
  clamped by the screen boundary the cursor is parked against.
- **Returning** — press the configured hotkey (F9 by default), move the pointer
  back across the edge it left from, or let the app notice the iPad
  disconnecting.

Because the iPad never reports its pointer position back, the position on the
far side is dead-reckoned from the deltas already sent, and clamped to a
configurable travel distance. Scrolling never moves that estimate.

Corner calibration lets you mark the four screen corners so the handoff boundary
matches the physical display rather than the reported resolution.

## Requirements

- Windows 10 2004 (build 19041) or newer
- A Bluetooth adapter that supports the peripheral role
- .NET 8 SDK to build

## Settings and diagnostics

- Settings and calibration: `%APPDATA%\BlePeripheralEmu\settings.json`.
  Reachable any time from the tray icon; the setup dialog only appears on first run.
- Diagnostic log: `debug.log` next to the executable, rotated to `debug.log.old`
  past 2 MB.

## Source layout

| File | Contents |
| --- | --- |
| [Program.cs](Program.cs) | Entry point: GATT services, HID report descriptor, advertising |
| [InputBridgeForm.cs](InputBridgeForm.cs) | Hooks, Raw Input, handoff/return, calibration, tray icon |
| [HidHelpers.cs](HidHelpers.cs) | Characteristic creation and the serialised report pumps |
| [TouchpadParser.cs](TouchpadParser.cs) | Precision Touchpad raw HID contact decoding |
| [AppSettings.cs](AppSettings.cs) | Persisted configuration |
| [SettingsForm.cs](SettingsForm.cs) | Setup dialog |
| [CalibrationOverlayForm.cs](CalibrationOverlayForm.cs) | Corner calibration overlay |
| [NativeMethods.cs](NativeMethods.cs) | Win32/HID P/Invoke declarations |

## Known limitations

- Writes to the HID Protocol Mode characteristic aren't persisted; it's a static
  Report-Protocol value. This only matters to a host that explicitly switches to
  Boot Protocol, which iOS does not.
- The battery level is read once at startup rather than tracked continuously.
- The Device Information service can't be published on some systems (Windows
  reports `DisabledByPolicy` for the reserved 0x180A UUID), so the manufacturer
  and PnP ID characteristics may be absent. HID itself is unaffected.
