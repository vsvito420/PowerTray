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

### 5. Lüfterkurven in FanControl

Pro Profil eine FanControl-Konfiguration, gespeichert unter dem Namen aus `FanConfig=` (Standard `Leise.json`,
`Ausgewogen.json`, `Leistung.json`, `Turbo.json`) im Ordner `<FanControl>\Configurations`.

Empfohlener Weg (sicher):
1. Der Nutzer führt in FanControl für alle Lüfter die **Auto-Kalibrierung** aus und benennt die Lüfter (CPU, Front, Oben …).
2. Er legt Graph-Kurven an. Schlage Punkte vor (siehe `docs/beispiel-setup-watt.md` als Vorlage, auf seine
   Watt-Werte aus Schritt 3 skalieren).
3. **Sicherheit:** Jede Watt-Kurve mit einer Temperatur-Notfallkurve in eine Mix-Kurve (Funktion *Max*) packen
   (z. B. CPU > 85 °C → 100 %, GPU-Hotspot > 95 °C → 100 %). Watt sagen nicht, ob die Kühlung reicht.
4. Speichern über *Menü → Konfiguration speichern unter* mit dem exakten Namen, dann Kurvenpunkte anpassen und
   für das nächste Profil erneut speichern.

Direktes Bearbeiten der JSON-Dateien ist möglich, aber nur mit beendetem FanControl und nach einer Sicherungskopie.
Aufbau: `FanControl.FanCurves[]` mit `Name`, `SelectedTempSource.Identifier`
(Plugin-Sensoren: `Watt-Sensoren (CPU/GPU)/WattSensors/cpu/package`, `.../gpu/board`, `.../total`)
und `Points` als `"Watt,Prozent"`-Strings. `FanControl.Controls[]` verweist per `SelectedFanCurve` auf eine Kurve.
Kopiere am besten eine vom Nutzer gespeicherte Datei und ändere nur `Points` – Lüfter-IDs und Kalibrierung niemals erfinden.

### 6. Automatik und Erkenntnisse

- `[Automatik]` in `PowerTray.ini`: `ProfilSpiel`, `ProfilIdle`, `ProfilDesktop` auf die passenden `[ProfilN]`-Nummern setzen.
  Frag nach Spielen außerhalb von Steam (Epic, Battle.net, Riot …) und trag deren Exe-Namen in `SpielExe=` ein.
  Steam-Apps, die dauerhaft laufen und kein Spiel sind, in `SteamIgnorieren=` (AppID aus `HKCU\Software\Valve\Steam\RunningAppID`).
- `[Gehaeuse]` in `PowerTray.ini` beschreibt, wo welcher Lüfter sitzt (für die PC-Ansicht und die Druckanzeige im Dashboard).
  Schlüssel: `CPU`, `Unten`, `Seite`, `Vorne` (= Zuluft), `Hinten`, `Oben` (= Abluft), `GPU`. Wert: FanControl-NickName,
  optional `|Anzahl` bei Splitter-Kabeln. Beispiel: `Unten=Fan #4 UNTEN|3`. Frag den Nutzer, wo seine Lüfter sitzen
  und wie viele an einem Anschluss hängen. Ohne Abschnitt rät PowerTray anhand der NickNames.

### 6b. Messen statt raten (Optimier-Session)

Das Plugin schreibt alle Board-/CPU-Sensoren (Temperaturen, Lüfter-RPM und -%) nach `%ProgramData%\PowerTray\live.json`.
`tools\Measure-Cooling.ps1` setzt die Lüfter über temporäre FanControl-Konfigurationen auf feste Stufen, erzeugt CPU-Last
und speichert Mittelwerte in `%ProgramData%\PowerTray\messung.json`; das Dashboard zeigt sie als Balken.
Vor dem Start den Nutzer fragen (laut, ~20 min). Ab 90 °C wird ein Schritt übersprungen, am Ende immer das Profil wiederhergestellt.
Leite Kurven aus den Messwerten ab: Bringt mehr Drehzahl kaum weniger °C, die Kurve dort flach halten (leiser).
Welcher Board-Temperatursensor die Gehäuseluft misst, erkennt man daran, dass er bei CPU-Last nur langsam und wenig steigt.

### 7. Abschluss prüfen

- Mausrad über dem Tray-Icon: Energieplan (`powercfg /getactivescheme`) und FanControl-Konfiguration wechseln.
- Autostart: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PowerTray` existiert; in FanControl „Mit Windows starten“ aktiv.
- Unter Last (Spiel oder Stresstest) kurz prüfen, dass die Lüfter hochdrehen und die Temperaturen im grünen Bereich bleiben.

## Entwicklung

- Bauen: `powershell -File build.ps1` → `bin\` und `dist\PowerTray-Setup.zip`. Kein NuGet, kein SDK.
- Code muss mit dem C#-5-Compiler aus .NET Framework 4 kompilieren (keine `$"..."`, kein `?.`, keine `out var`).
- UI-Texte zweisprachig über `L.T("deutsch", "english")`.
