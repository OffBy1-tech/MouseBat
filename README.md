# MouseBat

A small Windows tray app that shows your Logitech mouse's battery level as a number in the
system tray and in a floating widget, and notifies you when it runs low. It talks to the mouse
directly, so G HUB doesn't need to be running.

> **Unofficial project.** MouseBat is not made by, affiliated with, or endorsed by Logitech.
> Logitech, G HUB, LIGHTSPEED, POWERPLAY and related names are trademarks of their owners.

## Supported mice

**Logitech wireless mice and trackballs that use HID++ 2.0, on Windows, connected through a
USB receiver or a USB cable.** That includes LIGHTSPEED, Unifying, Logi Bolt and POWERPLAY
receivers. The mouse needs to report its battery through one of these HID++ features:

| Feature | Reports | Accuracy |
| ------- | ------- | -------- |
| `0x1000` Battery status | Percentage | As reported, but some mice use only a few coarse levels |
| `0x1001` Battery voltage | Voltage | Estimated from a discharge curve |
| `0x1004` Unified battery | Percentage or coarse level | As reported, but coarse on some mice |
| `0x1F20` ADC measurement | Voltage | Estimated from a discharge curve |

### What has been tested

| Status | What |
| ------ | ---- |
| **Tested on real hardware** | G502 LIGHTSPEED on a POWERPLAY receiver (`0x1001`) |
| **Written from protocol details, tested only against simulated mice** | Every other mouse, the other three battery features, other receiver types, and a USB cable connection |

Other mice should work, but that hasn't been confirmed. If you try one, please open an issue
saying whether it worked.

### Not supported

- **Bluetooth connections.** Use the mouse's USB receiver instead.
- **Older Logitech mice that only use HID++ 1.0.**
- **Other Logitech devices**, such as keyboards and headsets, even if they have a battery.
- **Other brands of mouse.**
- **macOS and Linux.**

### If you have more than one mouse

MouseBat shows one mouse at a time: the first one that answers, starting from the lowest
receiver slot. You can't currently choose between them.

## Install

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download `MouseBat.exe` from the [latest release](https://github.com/OffBy1-tech/MouseBat/releases/latest).
3. Run it. There's no installer.

The exe isn't code-signed, so Windows SmartScreen may warn you the first time. Each release also
includes a SHA-256 checksum so you can verify the download.

If "Start with Windows" is on, it points at the exe's current location, so turn it off and on
again after moving the exe.

## Show the number in the tray

Windows 11 hides new tray icons. To keep it visible, go to Settings > Personalization >
Taskbar > Other system tray icons and turn on MouseBat, or drag the icon out of the `^` menu.

## Floating widget

A small always-on-top pill shows the same reading, for monitors without a system tray. Drag it
anywhere, including to another monitor, and it stays there across restarts. If that monitor is
disconnected, it moves to the main monitor and returns when the monitor does. Right-click it
for the same menu as the tray icon, where "Show floating widget" turns it on and off.

It can't appear over games running in exclusive fullscreen; use borderless windowed mode to
keep it visible.

## Reading the icon

| Colour | Meaning |
| ------ | ------- |
| White or black (follows the taskbar theme) | Normal |
| Green | Charging, with a lightning bolt in the widget's battery. Mice that only report voltage show `+` in the tray and `CHG` on the widget until unplugged |
| Amber | At or below the alert threshold (default 20%) |
| Red | At or below half the alert threshold |
| Grey | Mouse asleep or off, showing the last known value (`?` if there isn't one) |

Hover over the icon for the mouse's name and status. Right-click for refresh, the alert
threshold, a test notification, Start with Windows, and exit.

You get one notification at the threshold and one at half of it. They re-arm after the mouse
charges or recovers by 5 points. Windows Do Not Disturb can hide them.

## How it works

The app polls every 60 seconds over the receiver's vendor-specific HID interface. It looks for
a mouse or trackball in receiver slots 1-6, or connected directly by cable, and reads whichever
battery feature it supports.

Percentages from voltage-reporting mice come from a median of the last five readings and one
discharge curve (`src/Battery.cs`), so they're estimates and can vary by model. The curve's
values come from the [Solaar](https://github.com/pwr-Solaar/Solaar) project.

The charger raises the voltage these mice report, so no percentage is shown while they charge.
Expect the first readings after unplugging to run high until the voltage settles.

Settings are stored in `%APPDATA%\MouseBat\settings.json`.

## Build from source

Requires the .NET 10 SDK.

```
dotnet test tests/MouseBat.Tests
dotnet publish MouseBat.csproj -c Release -o publish
```

This produces `publish\MouseBat.exe`.

## Releases

Every push and pull request is built and tested by GitHub Actions. Pushing a version tag also
publishes a GitHub release with the exe and its checksum:

```
git tag v1.2.3
git push origin v1.2.3
```

## License

MouseBat is released under the [BSD Zero Clause License](LICENSE) (0BSD).

The discharge curve values in `src/Battery.cs` come from
[Solaar](https://github.com/pwr-Solaar/Solaar), which is licensed under GPL-2.0-or-later.
MouseBat's license doesn't change the terms of anything taken from Solaar, so check those terms
if they matter for how you use this code.
