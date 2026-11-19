# AGENTS.md – Einrichtung von PowerTray durch einen KI-Agenten

Diese Datei richtet sich an KI-Assistenten (Claude Code, Claude Desktop, ChatGPT/Codex, Copilot …), die PowerTray
auf dem Windows-PC eines Nutzers einrichten. Antworte in der Sprache des Nutzers.

## Was das Projekt ist

- **PowerTray** (`src/PowerTray`): Tray-App. Ein Profil = Windows-Energieplan + FanControl-Konfiguration.
  Mausrad über dem Icon wechselt das Profil. Einstellungen: `%LOCALAPPDATA%\Programs\PowerTray\PowerTray.ini`.
- **FanControl.WattSensors** (`src/FanControl.WattSensors`): Plugin für [FanControl](https://getfancontrol.com).
  Stellt CPU-/GPU-Leistung in Watt als „Temperatur“-Sensoren bereit (1 W = 1 °C), damit Lüfter nach Last statt nach Temperatur laufen.
  CPU über den Windows-Energy-Meter (RAPL, Intel und neuere AMD), GPU über AMD ADL oder NVIDIA NVML.
  Konfig: `<FanControl>\Plugins\FanControl.WattSensors.ini`.

Beides funktioniert unabhängig: Ohne FanControl schaltet PowerTray nur Energiepläne.

## Ablauf

Arbeite die Schritte der Reihe nach ab. Frag den Nutzer vor allem, was Software installiert, Systemeinstellungen ändert
oder Lüfter beeinflusst. Kannst du selbst keine Befehle ausführen (reiner Chat), gib dem Nutzer die Befehle einzeln
zum Kopieren und lass dir die Ausgabe zurückgeben.

### 1. Hardware erfassen

```powershell
Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, MaxClockSpeed
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion
powercfg /list
Get-Process FanControl -ErrorAction SilentlyContinue | Select-Object Path
(Get-Counter -ListSet 'Energy Meter' -ErrorAction SilentlyContinue).Counter | Select-Object -First 3
```

Notiere: CPU-Modell und typische Maximalleistung (PL2/PPT), GPU-Modell und Board-Power (TBP), ob FanControl läuft,
welche Energiepläne existieren und ob der Energy Meter verfügbar ist (sonst gibt es keine CPU-Watt-Sensoren).

### 2. Installieren

Aus dem Repo-Ordner (oder dem entpackten Release-ZIP):

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Yes
```

- `-Yes` installiert FanControl per winget, falls es fehlt. Vorher den Nutzer fragen; sonst `-NoFanControl` anhängen.
- Für den Plugin-Ordner unter `Program Files` erscheint eine UAC-Abfrage, die der Nutzer bestätigen muss.
- Fehlt `bin\`, baut der Installer selbst über `build.ps1` (nutzt nur das in Windows enthaltene .NET Framework 4).

### 3. Plugin anpassen

In `FanControl.WattSensors.ini` (im Plugins-Ordner, wird beim ersten FanControl-Start angelegt):

| Schlüssel | Wert |
|---|---|
| `CpuMaxWatt` | Maximale Paketleistung der CPU unter Volllast (z. B. i9-9900K ≈ 200, Ryzen 7 7800X3D ≈ 120, Laptop ≈ 45–90) |
| `GpuMaxWatt` | Board-Power der GPU (z. B. RX 7900 XTX ≈ 400, RTX 4070 ≈ 200) |
| `SmoothingSeconds` | 3 lassen |
| `GpuPowerSensorIndex` | nur AMD: 73 bei RX 7000 (RDNA3), 23 bei älteren. Bei NVIDIA egal. |

Danach FanControl neu starten und prüfen, ob die Sensoren `CPU Package [W]`, `GPU Board [W]` und `CPU+GPU [W]`
plausible Werte zeigen (Leerlauf grob 10–40 W CPU, 10–80 W GPU). Fehlen sie, steht der Grund im FanControl-Log
(`[WattSensors]`).

### 4. Energiepläne und Profile

PowerTray legt beim ersten Start `PowerTray.ini` aus den vorhandenen Plänen an (Energiesparmodus, Ausbalanciert,
Höchstleistung, ggf. Ultimative Leistung). Prüfe die Datei:

- Jede `[ProfilN]`-Sektion braucht eine gültige `Plan=`-GUID aus `powercfg /list`.
- Auf Laptops mit Modern Standby gibt es oft nur „Ausbalanciert“. Dann eigene Pläne anlegen:
  `powercfg -duplicatescheme 381b4222-f694-41f0-9685-ff5bb260df2e` und mit `powercfg /changename <GUID> "Leise"` benennen.
- Optional (nur nach Rückfrage): im leisen Plan den CPU-Turbo abschalten:
  `powercfg /setacvalueindex <GUID> SUB_PROCESSOR PROCTHROTTLEMAX 99` – rückgängig mit `100`.
- Nach Änderungen: Tray-Menü → „Einstellungen neu laden“.

### 5. Gehäuse-Layout erfassen

PowerTray und die Werkzeuge in `tools\` arbeiten mit jedem Layout. Ziel ist der Abschnitt `[Gehaeuse]` in `PowerTray.ini`:

```ini
[Gehaeuse]
; LuefterN=Position|FanControl-NickName|Anzahl am Anschluss|rein/raus|Radiator
; Positionen: CPU, GPU, Vorne, Oben, Hinten, Unten, Seite  (englisch geht auch: Front, Top, Rear, Bottom, Side)
Luefter1=CPU|Fan #2 CPU|1
Luefter2=Vorne|Fan #6|3|rein
Luefter3=Hinten|Fan #3|1|raus
Luefter4=GPU|Control 1 - AMD Radeon RX 7900 XTX|1
```

Weitere typische Layouts:

| Aufbau | Einträge |
|---|---|
| Klassisch: 3 vorne rein, 1 hinten raus | `Vorne|…|3|rein`, `Hinten|…|1|raus` |
| AIO oben: Radiator saugt aus dem Gehäuse | `Oben|…|3|raus|Radiator`, `Vorne|…|3|rein`, `Hinten|…|1|raus` (kein `CPU`-Eintrag, die Pumpe nicht eintragen) |
| AIO vorne als Zuluft | `Vorne|…|2|rein|Radiator`, `Oben|…|2|raus`, `Hinten|…|1|raus` |
| Boden + Seite rein, oben + hinten raus | `Unten|…|3|rein`, `Seite|…|3|rein`, `Oben|…|3|raus`, `Hinten|…|1|raus` |
| Mehrere Gruppen an einer Position | einfach mehrere `LuefterN` mit derselben Position (z. B. zwei Anschlüsse vorne) |

So kommst du dahin:
1. In FanControl **Auto-Kalibrierung** für alle Lüfter ausführen lassen (macht der Nutzer in der Oberfläche).
2. `tools\Detect-Fans.ps1` ausführen: zeigt, welche Anschlüsse belegt sind (RPM bei 30 % und 100 %).
3. Für jeden belegten Anschluss `tools\Detect-Fans.ps1 -Only "<NickName>"` ausführen und den Nutzer fragen,
   welcher Lüfter gerade laut wurde, wo er sitzt, ob er rein- oder rausbläst und wie viele an diesem Anschluss hängen.
   Bei Unsicherheit über die Richtung: Taschentuch/Papier davorhalten lassen.
4. Den Anschlüssen in FanControl sprechende NickNames geben lassen (optional) und `[Gehaeuse]` schreiben.

### 6. Lüfterprofile erzeugen

`[Sensoren]` in `PowerTray.ini` mit FanControl-Sensor-IDs füllen (IDs stehen in `%ProgramData%\PowerTray\live.json`;
für FanControl das `/0/` nach dem Chipnamen weglassen, z. B. `/lpc/nct6797d/temperature/2`):

```ini
[Sensoren]
CpuTemp=/intelcpu/0/temperature/10                     ; AMD: meist /amdcpu/0/temperature/2 (Tctl/Tdie)
GpuHotspot=ADLX/AMD Radeon RX 7900 XTX/768/temp/Hotspot  ; aus einer in FanControl gespeicherten Kurve ablesen
Gehaeuseluft=/lpc/nct6797d/temperature/2               ; Board-Sensor, der unter CPU-Last kaum steigt (~30-35 °C)
```

Dann `tools\Make-FanProfiles.ps1 -WhatIf` (zeigt, welche Gruppe auf welche Quellen reagiert) und ohne `-WhatIf` schreiben.
Das Skript sichert vorher alle Konfigurationen, skaliert die Watt-Achsen auf `CpuMaxWatt`/`GpuMaxWatt` aus der Plugin-ini
und erzeugt pro Lüftergruppe eine Mix-Kurve (Maximum) passend zur Rolle:

| Rolle | reagiert auf |
|---|---|
| CPU-Kühler, AIO-Radiator | CPU-Watt, CPU-Notfall |
| Zuluft unten | GPU-Watt, Gehäuseluft, GPU-Notfall |
| Zuluft vorne/seite/oben | CPU-Watt, GPU-Watt, Gehäuseluft, Notfälle |
| Abluft hinten | CPU-Watt, Gehäuseluft, CPU-Notfall |
| Abluft oben/sonst | CPU-Watt, Gesamt-Watt, Gehäuseluft, Notfälle |

Abluft liegt immer `-Pressure` (6) Prozentpunkte unter Zuluft → Überdruck gegen Staub.
Fehlt ein Sensor in `[Sensoren]`, wird die zugehörige Quelle weggelassen. Lüfter-IDs und Kalibrierung niemals erfinden;
sie kommen aus `userConfig.json`.

### 6b. Messen statt raten (Optimier-Session)

Vor jedem Test den Nutzer fragen (laut, CPU-Last). Ab 90 °C bricht jedes Werkzeug den Schritt ab und stellt das Profil wieder her.

- `tools\Measure-Cooling.ps1`: feste Lüfterstufen (CPU 30/60/100 %, Gehäuse 20/70 %) unter CPU-Last (halbe Threadzahl,
  ~Spielelast) → `%ProgramData%\PowerTray\messung.json`, im Dashboard als Balken. Ca. 22 min.
- `tools\Test-Profile.ps1 -Profile Ausgewogen.json`: prüft ein erzeugtes Profil unter derselben Last (4 min).
- Auswertung: Bringt mehr Drehzahl kaum weniger °C, die Kurve dort flacher machen (leiser). Wird es zu heiß, die Punkte
  in `Make-FanProfiles.ps1` für die betroffene Rolle anheben und neu erzeugen. Erkenntnis aus dem Referenz-PC: bei CPU-Last
  ist Gehäuse-Luftstrom mindestens so wichtig wie der CPU-Lüfter.

### 6c. Automatik

- `[Automatik]` in `PowerTray.ini`: `ProfilSpiel`, `ProfilIdle`, `ProfilDesktop` auf die passenden `[ProfilN]`-Nummern setzen.
  Frag nach Spielen außerhalb von Steam (Epic, Battle.net, Riot …) und trag deren Exe-Namen in `SpielExe=` ein.
  Steam-Apps, die dauerhaft laufen und kein Spiel sind, in `SteamIgnorieren=` (AppID aus `HKCU\Software\Valve\Steam\RunningAppID`).
### 7. Abschluss prüfen

- Mausrad über dem Tray-Icon: Energieplan (`powercfg /getactivescheme`) und FanControl-Konfiguration wechseln.
- Autostart: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PowerTray` existiert; in FanControl „Mit Windows starten“ aktiv.
- Unter Last (Spiel oder Stresstest) kurz prüfen, dass die Lüfter hochdrehen und die Temperaturen im grünen Bereich bleiben.

## Entwicklung

- Bauen: `powershell -File build.ps1` → `bin\` und `dist\PowerTray-Setup.zip`. Kein NuGet, kein SDK.
- Code muss mit dem C#-5-Compiler aus .NET Framework 4 kompilieren (keine `$"..."`, kein `?.`, keine `out var`).
- UI-Texte zweisprachig über `L.T("deutsch", "english")`.
