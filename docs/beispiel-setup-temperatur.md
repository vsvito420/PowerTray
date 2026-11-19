# PowerTray

Tray-Icon (3 Balken, grün/blau/rot), das Energieplan und FanControl-Konfiguration gemeinsam umschaltet.

- **Mausrad über dem Icon:** hoch = mehr Leistung, runter = leiser
- **Rechts- oder Linksklick:** Menü mit Profilen, „Nur Energieplan“, „Nur Lüfterkurve“, Autostart
- Einstellungen: `PowerTray.ini` (Profile, Plan-GUIDs, Konfig-Namen, Farben)
- Neu bauen: `powershell -File build.ps1`
- Wird der Plan woanders gewechselt (z. B. in den Windows-Einstellungen), zieht PowerTray die passende Lüfterkurve nach.

| Profil     | Energieplan                    | FanControl-Konfig |
|------------|--------------------------------|-------------------|
| Leise      | Energiesparmodus (Turbo aus*)  | `Leise.json`      |
| Ausgewogen | Ausbalanciert                  | `Ausgewogen.json` |
| Leistung   | Höchstleistung                 | `Leistung.json`   |

\* Im Energiesparmodus ist „Maximaler Leistungszustand des Prozessors“ auf 99 % gesetzt. Das schaltet den Turbo des i9-9900K ab
(fest 3,6 GHz statt 5 GHz) und senkt Verbrauch und Lüfterlärm stark. Rückgängig machen:
`powercfg /setacvalueindex a1841308-3541-4fab-bc81-f71556f20b4a SUB_PROCESSOR PROCTHROTTLEMAX 100`

## Lüfter-Setup in FanControl

Gehäuse: 6× Intake (3 unten an der GPU, 3 Seite) und 4× Exhaust (3 oben, 1 hinten). Das ergibt leichten Überdruck, gut gegen Staub.
Die Gehäuselüfter laufen mit 600–3000 RPM, über ~65 % werden sie laut.

**Zuerst:** In FanControl „Auto-Kalibrierung“ für alle Lüfter ausführen, damit Start- und Mindestwerte bekannt sind.

**Kurven (Graph-Kurven) und Zuordnung:**
- **CPU-Kühler:** Sensor *CPU Package*
- **Unten (GPU-Intake):** Sensor *GPU Temperature* (Edge, nicht Hotspot)
- **Seite (Intake):** Mix-Kurve, Funktion **Max** aus CPU- und GPU-Kurve
- **Oben und hinten (Exhaust):** Mix-Kurve **Max**, in jedem Punkt etwa 5 % unter der Seiten-Kurve

Für alle Kurven: Hysterese 3 °C, Reaktionszeit 3 s, „Step up“ 8 %/s, „Step down“ 3 %/s, damit nichts hochdreht und gleich wieder abfällt.

Die Tabellen geben Temperatur → Lüfter-%.

**CPU-Lüfter (CPU Package)**

| °C          | 40 | 60 | 70 | 80 | 88  |
|-------------|----|----|----|----|-----|
| Leise       | 25 | 32 | 45 | 70 | 100 |
| Ausgewogen  | 30 | 42 | 58 | 80 | 100 |
| Leistung    | 40 | 55 | 75 | 95 | 100 |

**GPU-Intake unten (GPU Edge)**

| °C          | 45 | 55 | 65 | 75 | 82  |
|-------------|----|----|----|----|-----|
| Leise       | 20 | 28 | 40 | 60 | 100 |
| Ausgewogen  | 25 | 35 | 50 | 70 | 100 |
| Leistung    | 35 | 50 | 70 | 90 | 100 |

**Seite und Exhaust:** Diese nutzen die Mix-Kurven oben (Max). In *Leise* zusätzlich ein Maximum von 70 % setzen, außer bei einem
Notfall-Punkt (CPU > 88 °C oder GPU > 82 °C führt zu 100 %).

Sicherheit: Jedes Profil, auch *Leise*, geht bei hohen Temperaturen auf 100 %.

**GPU-Lüfter (RX 7900 XTX, bis 3500 RPM):** Am besten in AMD Adrenalin lassen (Zero-RPM plus eigene Kurve unter *Leistung → Tuning*).
Die unteren Gehäuselüfter liefern frische Luft, dadurch müssen die GPU-Lüfter weniger arbeiten.

**Speichern:** Für jedes Profil die Konfiguration über *Menü → Konfiguration speichern unter* mit genau dem Namen
`Leise.json`, `Ausgewogen.json` bzw. `Leistung.json` speichern. In FanControl außerdem „Mit Windows starten“ aktivieren.
