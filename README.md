# MouseBat

A small Windows tray app that shows the battery level of a Logitech G502 Lightspeed as a
number in the system tray and notifies you when it runs low. It talks to the mouse directly,
so G HUB doesn't need to be running.

## Build

Requires the .NET 10 SDK. To run the result, you need the .NET 10 Desktop Runtime.

```
dotnet publish -c Release -o publish
```

This produces `publish\MouseBat.exe`. If "Start with Windows" is on, it points at the exe's
current location, so turn it off and on again after moving the exe.

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
| Green | Charging |
| Amber | At or below the alert threshold (default 20%) |
| Red | At or below half the alert threshold |
| Grey | Mouse asleep or off, showing the last known value (`?` if there isn't one) |

Hover over the icon for the voltage and status. Right-click for refresh, the alert threshold,
a test notification, Start with Windows, and exit.

You get one notification at the threshold and one at half of it. They re-arm after the mouse
charges or recovers by 5 points. Windows Do Not Disturb can hide them.

## How it works

The app polls every 60 seconds, using HID++ 2.0 feature `0x1001` (battery voltage) on the
receiver's vendor-specific HID interface. The mouse reports only a voltage, so the percentage
is an estimate from a discharge curve in `src/Battery.cs`, taken from a median of the last
five readings. Expect it to be a little less steady and less accurate while charging.

Settings are stored in `%APPDATA%\MouseBat\settings.json`.
