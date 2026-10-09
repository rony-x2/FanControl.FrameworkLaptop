# FanControl.FrameworkLaptop

[English](README.md) | **Deutsch**

Plugin für [FanControl](https://github.com/Rem0o/FanControl.Releases), mit dem sich die Lüfter von Framework-Laptops (12, 13, 16) und des Framework Desktop direkt in FanControl steuern lassen.

Version 2.0: Das Plugin spricht den Embedded Controller (EC) **direkt über den Framework-Treiber** an. Es braucht keinen zusätzlichen Dienst und kein externes Programm. Nur wenn der Treiber nicht erreichbar ist, nutzt es als Rückfall das Kommandozeilenwerkzeug `framework_tool`.

## Was das Plugin in FanControl anlegt

| Bereich | Inhalt |
|---|---|
| Temperaturen | alle Fühler des EC mit den Namen, die der EC meldet, z. B. `Framework APU`, `Framework F75303_CPU` |
| Lüfter | Drehzahl je Lüfter, z. B. `Framework Lüfter links` / `Framework Lüfter rechts` (16), `Framework APU-Lüfter` (13/12) |
| Steuerungen | eine Steuerung je Lüfter (0-100 %), automatisch mit dem passenden Drehzahlsensor gekoppelt |

Kurven, Mix-Kurven, Hysterese, Profile und die Kalibrierung übernimmt FanControl wie bei jeder anderen Hardware.

## Voraussetzungen

1. **Treiberpaket von Framework** installiert (enthält den EC-Treiber „CrosEC“).
2. **Aktuelles BIOS.** Der Windows-Treiber ist je nach Modell erst ab einer bestimmten BIOS-Version freigeschaltet, beim Framework 16 ist für die Lüftersteuerung mindestens BIOS 3.07 nötig.
3. **FanControl als Administrator** starten (Standard bei FanControl).
4. **Keine zweite Lüftersteuerung:** Framework Control, YAFI o. Ä. beenden oder auf „Auto“ stellen.

## Installation

1. `FanControl.FrameworkLaptop-<version>.zip` unter [Releases](../../releases/latest) herunterladen und entpacken.
2. FanControl beenden.
3. Die passende DLL in den Ordner `Plugins` von FanControl kopieren:
   - FanControl **.NET 4.8** → `net48\FanControl.FrameworkLaptop.dll`
   - FanControl **.NET 10** → `net10.0\FanControl.FrameworkLaptop.dll`
4. Wurde die DLL aus dem Internet geladen: Rechtsklick → Eigenschaften → „Zulassen“ anhaken.
5. FanControl starten.

Im FanControl-Fehlerprotokoll steht danach unter `[Framework Laptop]`, welcher Zugriffsweg aktiv ist:
- `Zugriff über CrosEC-Treiber (direkt).` → Normalfall
- `Rückfall: Zugriff über framework_tool (...)` → Treiber nicht erreichbar, framework_tool wird genutzt

## Zugriffswege

**1. CrosEC-Treiber (Standard).** Das Plugin öffnet `\\.\GLOBALROOT\Device\CrosEC` und nutzt zwei Treiberaufrufe:
- Speicherabbild des EC lesen: Temperaturen (0x00-0x0E) und Lüfterdrehzahlen (0x10-0x17)
- Host-Befehle senden: `PwmSetFanDuty` (0x24), `AutoFanCtrl` (0x52), `TempSensorGetInfo` (0x70)

Ein Lesevorgang dauert Mikrosekunden, es wird kein Prozess gestartet.

**2. framework_tool (Rückfall).** Wird nur verwendet, wenn der Treiber nicht geöffnet oder nicht gelesen werden kann. Gesucht wird in dieser Reihenfolge:
1. Umgebungsvariable `FRAMEWORK_TOOL_PATH`
2. Datei `FanControl.FrameworkLaptop.path.txt` im Plugins-Ordner (erste Zeile = vollständiger Pfad zur `framework_tool.exe`)
3. `framework_tool.exe` im Plugins-Ordner oder im FanControl-Ordner
4. alle Ordner im `PATH`
5. `%LOCALAPPDATA%\Microsoft\WinGet\Links` (Standard bei `winget install framework_tool --source winget`)
6. Unterordner mit „Framework“ im Namen unter `%LOCALAPPDATA%\Microsoft\WinGet\Packages` und den Programmordnern

Funktioniert keiner der beiden Wege, zeigt FanControl einmalig einen Hinweis mit dem Grund.

## Verhalten

- Werte werden einmal pro Sekunde in einem eigenen Hintergrund-Thread gelesen, FanControl wartet nie auf den EC.
- Ein Duty-Wert wird nur gesendet, wenn er sich ändert, und zusätzlich alle 30 s erneut (der EC kann z. B. nach dem Standby auf Automatik zurückfallen).
- „Zurücksetzen“ einer Steuerung in FanControl gibt nur diesen Lüfter an die Automatik des EC zurück. Ältere EC-Firmware kennt das nur für alle Lüfter gemeinsam, dann werden die übrigen Lüfter sofort wieder auf ihren Wert gesetzt.
- Beim Beenden von FanControl gehen alle gesteuerten Lüfter an die Automatik des EC zurück.
- Ändern sich Lüfter oder Fühler, oder war der EC beim Start noch nicht erreichbar, lädt sich das Plugin selbst neu.
- Die Sensor-IDs sind bei beiden Zugriffswegen gleich, Kurven bleiben beim Wechsel erhalten.

## Selbst bauen

```
cd src\FanControl.FrameworkLaptop
dotnet build -c Release
```

Vorher die `FanControl.Plugins.dll` aus dem FanControl-Ordner nach `src\FanControl.FrameworkLaptop\lib\` kopieren. Erzeugt `bin\Release\net48\` und `bin\Release\net10.0\`.

Der Ordner `test/` enthält einen Testlauf mit einem simulierten CrosEC-Treiber (auf Ebene der IOCTL-Puffer) und einem simulierten `framework_tool` (ein Bash-Skript, also unter Linux oder WSL ausführen): erst das Plugin bauen, dann `cd test` und `dotnet run -c Release`.

## Lizenz

[MIT](LICENSE). Protokoll, Konstanten und Pufferaufbau für den EC-Zugriff folgen [framework-system](https://github.com/FrameworkComputer/framework-system), Copyright (c) 2023, Framework Computer Inc, BSD-3-Clause-Lizenz; siehe [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Dies ist ein unabhängiges Community-Projekt ohne Verbindung zu Framework Computer Inc oder zum Autor von FanControl.
