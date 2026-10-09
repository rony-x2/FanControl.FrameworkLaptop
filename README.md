# FanControl.FrameworkLaptop

**English** | [Deutsch](README.de.md)

Plugin for [FanControl](https://github.com/Rem0o/FanControl.Releases) that lets you control the fans of Framework laptops (12, 13, 16) and the Framework Desktop directly in FanControl.

Version 2.0: The plugin talks to the Embedded Controller (EC) **directly through the Framework driver**. It needs no additional service and no external program. Only if the driver cannot be reached does it fall back to the command-line tool `framework_tool`.

## What the plugin adds to FanControl

| Section | Content |
|---|---|
| Temperatures | all EC sensors, named as reported by the EC, e.g. `Framework APU`, `Framework F75303_CPU` |
| Fans | speed of each fan, e.g. `Framework Lüfter links` / `Framework Lüfter rechts` (16), `Framework APU-Lüfter` (13/12) |
| Controls | one control per fan (0-100 %), automatically paired with the matching speed sensor |

Curves, mix curves, hysteresis, profiles and calibration are handled by FanControl, just like for any other hardware.

Note: the sensor names in FanControl are currently in German (e.g. "Lüfter links" = left fan, "Lüfter rechts" = right fan).

## Requirements

1. **Framework driver bundle** installed (includes the EC driver "CrosEC").
2. **Up-to-date BIOS.** Depending on the model, the Windows driver is only enabled from a certain BIOS version on. On the Framework 16, fan control requires at least BIOS 3.07.
3. **Run FanControl as administrator** (FanControl's default).
4. **No second fan controller:** close Framework Control, YAFI or similar tools, or set them to "Auto".

## Installation

1. Download `FanControl.FrameworkLaptop-<version>.zip` from [Releases](../../releases/latest) and extract it.
2. Close FanControl.
3. Copy the matching DLL into FanControl's `Plugins` folder:
   - FanControl **.NET 4.8** → `net48\FanControl.FrameworkLaptop.dll`
   - FanControl **.NET 10** → `net10.0\FanControl.FrameworkLaptop.dll`
4. If the DLL was downloaded from the internet: right-click → Properties → tick "Unblock".
5. Start FanControl.

FanControl's error log then shows under `[Framework Laptop]` which access path is active:
- `Zugriff über CrosEC-Treiber (direkt).` (access via CrosEC driver) → normal case
- `Rückfall: Zugriff über framework_tool (...)` (fallback via framework_tool) → driver not reachable, framework_tool is used

## Access paths

**1. CrosEC driver (default).** The plugin opens `\\.\GLOBALROOT\Device\CrosEC` and uses two driver calls:
- read the EC memory map: temperatures (0x00-0x0E) and fan speeds (0x10-0x17)
- send host commands: `PwmSetFanDuty` (0x24), `AutoFanCtrl` (0x52), `TempSensorGetInfo` (0x70)

A read takes microseconds, no process is started.

**2. framework_tool (fallback).** Only used if the driver cannot be opened or read. It is searched for in this order:
1. environment variable `FRAMEWORK_TOOL_PATH`
2. file `FanControl.FrameworkLaptop.path.txt` in the Plugins folder (first line = full path to `framework_tool.exe`)
3. `framework_tool.exe` in the Plugins folder or the FanControl folder
4. all folders in `PATH`
5. `%LOCALAPPDATA%\Microsoft\WinGet\Links` (default for `winget install framework_tool --source winget`)
6. subfolders with "Framework" in their name under `%LOCALAPPDATA%\Microsoft\WinGet\Packages` and the program folders

If neither path works, FanControl shows a one-time notice with the reason.

## Behavior

- Values are read once per second on a dedicated background thread, so FanControl never waits for the EC.
- A duty value is only sent when it changes, and additionally re-sent every 30 s (the EC may fall back to automatic mode, e.g. after standby).
- "Reset" on a control in FanControl hands only that fan back to the EC's automatic control. Older EC firmware only supports this for all fans at once; in that case the other fans are immediately set back to their values.
- When FanControl exits, all controlled fans are handed back to the EC's automatic control.
- If fans or sensors change, or the EC was not yet reachable at startup, the plugin reloads itself.
- Sensor IDs are identical for both access paths, so curves are kept when switching.

## Building

```
cd src\FanControl.FrameworkLaptop
dotnet build -c Release
```

First copy `FanControl.Plugins.dll` from your FanControl folder into `src\FanControl.FrameworkLaptop\lib\`. The build produces `bin\Release\net48\` and `bin\Release\net10.0\`.

The `test/` folder contains a test run with a simulated CrosEC driver (at the level of the IOCTL buffers) and a simulated `framework_tool` (a bash script, so run it on Linux or WSL): build the plugin first, then `cd test` and `dotnet run -c Release`.

## License

[MIT](LICENSE). Protocol, constants and buffer layout for EC access follow [framework-system](https://github.com/FrameworkComputer/framework-system), Copyright (c) 2023, Framework Computer Inc, BSD 3-Clause License; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This is an independent community project, not affiliated with Framework Computer Inc or the author of FanControl.
