# FanControl.WattSensors

Plugin für FanControl. Es stellt die Leistungsaufnahme als „Temperatur“-Sensoren bereit (**1 W = 1 °C**), damit Lüfterkurven nach Watt laufen.

| Sensor | Quelle |
|---|---|
| `CPU Package [W]` | Windows Energy Meter (Intel RAPL), ohne Adminrechte |
| `CPU Kerne [W]` | RAPL PP0 (nur die Kerne) |
| `CPU Package [% von max W]` | Package-W ÷ `CpuMaxWatt` × 100 |
| `GPU Board [W]` | AMD ADL PMLog, Sensor 73 (Board Power, RX 7900 XTX) |
| `GPU Board [% von max W]` | Board-W ÷ `GpuMaxWatt` × 100 |
| `CPU+GPU [W]` | Summe, gedacht für Gehäuselüfter |

- Alle Werte sind geglättet (Standard 3 s), damit kurze Lastspitzen die Lüfter nicht hochjagen.
- Konfiguration: `FanControl.WattSensors.ini` im Plugins-Ordner. Die Datei wird beim ersten Start angelegt.
- Installiert in: `C:\Program Files (x86)\FanControl\Plugins\FanControl.WattSensors.dll`
- Neu bauen: `powershell -File build.ps1`, danach die DLL aus `bin\` in den Plugins-Ordner kopieren (FanControl vorher schließen).

Falls der Graph-Editor keine Werte über ~120 zulässt, nimm die `% von max W`-Sensoren und setze `CpuMaxWatt`/`GpuMaxWatt` in der ini
auf deine echten Maximalwerte (Prime95/AVX bzw. GPU-Stresstest messen).

## Empfohlene Zuordnung

| Lüfter | Kurve nach |
|---|---|
| CPU-Kühler | `CPU Package [W]` |
| 3× unten (Intake an der GPU) | `GPU Board [W]` |
| 3× Seite (Intake) | `CPU+GPU [W]` |
| 3× oben + 1× hinten (Exhaust) | `CPU+GPU [W]`, etwa 5 % weniger |

**Wichtig wegen der Übertaktung:** Jede Watt-Kurve mit einer Temperatur-Notfallkurve in eine **Mix-Kurve (Max)** packen.
Watt sagt, wie viel Wärme kommt; nur die Temperatur zeigt, ob die Kühlung tatsächlich hinterherkommt.
Notfall-Trigger: CPU Package > 85 °C → 100 % und GPU Hotspot > 95 °C → 100 %.

## Startwerte (Watt → Lüfter-%)

**CPU-Lüfter (`CPU Package [W]`)**

| W          | 40 | 80 | 130 | 180 | 220 |
|------------|----|----|-----|-----|-----|
| Leise      | 25 | 35 | 55  | 80  | 100 |
| Ausgewogen | 30 | 45 | 65  | 90  | 100 |
| Leistung   | 40 | 60 | 85  | 100 | 100 |

**GPU-Intake unten (`GPU Board [W]`)** – Die Karte zieht ~80 W schon im Leerlauf (Speichertakt hängt oben).

| W          | 100 | 200 | 300 | 380 | 430 |
|------------|-----|-----|-----|-----|-----|
| Leise      | 20  | 30  | 50  | 75  | 100 |
| Ausgewogen | 25  | 40  | 60  | 85  | 100 |
| Leistung   | 35  | 55  | 80  | 100 | 100 |

**Seite und Exhaust (`CPU+GPU [W]`)**

| W          | 150 | 300 | 450 | 550 |
|------------|-----|-----|-----|-----|
| Leise      | 20  | 35  | 55  | 80  |
| Ausgewogen | 25  | 45  | 70  | 100 |
| Leistung   | 35  | 60  | 90  | 100 |

Diese Werte sind Startpunkte. Miss mit HWiNFO oder dem Adrenalin-Overlay deine echten Spitzen in Spiel und Stresstest und
verschiebe die Punkte entsprechend.
