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
being pushed against your configured screen edge:

- **Before handoff** — a low-level mouse hook watches for the pointer touching
  the edge. Touching it does nothing on its own: control only crosses over once
  you keep pushing outward for a configurable distance. Because the cursor is
  clamped at the screen boundary, that continued push still registers as raw
  movement while the pointer sits still. Without this, screen edges would be
  unusable for what they're normally for — the taskbar sits on one, and
  maximised windows put their controls on another.
- **After handoff** — mouse and keyboard input are suppressed locally and
  forwarded over BLE instead. Movement comes from Raw Input, so deltas aren't
  clamped by the screen boundary the cursor is parked against. An invisible
  full-desktop window is placed under the cursor and the cursor is hidden; see
  below for why.
- **Returning** — press the configured hotkey (F9 by default), push the pointer
  back against the edge it left from, or let the app notice the iPad
  disconnecting.

Because the iPad never reports its pointer position back, the position on the
far side is dead-reckoned from the deltas already sent, and clamped to a
configurable travel distance. Scrolling never moves that estimate.

### Clipboard

A second hotkey (F10 by default) types the Windows clipboard out on the iPad.
This is one-directional and deliberately not a real clipboard sync — over BLE
HID the PC is a *peripheral*, and the only host-to-device channel the profile
offers is the keyboard output report, one byte of LED state. The iPad has no way
to hand anything back, so copying from the iPad to Windows would need software
running on the iPad and a separate transport.

Because it works by synthesising keystrokes: plain ASCII only (the keyboard
collection declares usages 0–101, so accented characters, emoji and non-Latin
scripts are dropped), capped at 2000 characters, sent at typing speed, and the
mapping assumes the iPad is set to a US keyboard layout. The return hotkey
cancels a paste in progress. Ctrl+V is left alone, so the iPad's own clipboard
still works normally.

### Why the invisible overlay

A `WH_MOUSE_LL` hook can only suppress the legacy message pipeline. Windows
routes precision-touchpad panning through the modern pointer / DirectManipulation
stack for apps that support it — Edge, Chrome, File Explorer, most UWP — and that
input is neither visible to the hook nor blockable by it. `RIDEV_INPUTSINK`
doesn't help either: raw input is a sink, copying input to us without consuming
it. The result was two-finger scrolling reaching the iPad and the laptop at the
same time.

Pointer input is routed to the window under the cursor, so while redirected the
app parks a transparent, non-activating, full-virtual-desktop window there. The
leaked gestures land on a window that does nothing with them.

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
