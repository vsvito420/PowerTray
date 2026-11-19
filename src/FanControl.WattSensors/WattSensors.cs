// FanControl.WattSensors - liefert Leistungsaufnahme (Watt) als "Temperatur"-Sensoren an FanControl,
// damit Lüfterkurven nach Watt statt nach °C laufen können. Sensoren: src/Shared/PowerSensors.cs
// Konfiguration: FanControl.WattSensors.ini im selben Ordner wie diese DLL.
using System;
using System.Globalization;
using System.IO;
using FanControl.Plugins;
using PowerTray.Sensors;

namespace FanControl.WattSensors
{
    public class WattSensor : IPluginSensor
    {
        readonly string id, name;
        public float? Current;
        public WattSensor(string id, string name) { this.id = id; this.name = name; }
        public string Id { get { return id; } }
        public string Name { get { return name; } }
        public float? Value { get { return Current; } }
        public void Update() { }
    }

    public class WattSensorsPlugin : IPlugin2
    {
        readonly IPluginLogger log;
        CpuPower cpu;
        GpuPower gpu;

        float cpuMax = 250, gpuMax = 400, smoothSec = 3;
        int gpuSensorIndex = 73;
        bool liveData = true;
        LiveBridge bridge;

        readonly WattSensor cpuPkg = new WattSensor("WattSensors/cpu/package", "CPU Package [W]");
        readonly WattSensor cpuCores = new WattSensor("WattSensors/cpu/cores", "CPU Kerne [W]");
        readonly WattSensor cpuPct = new WattSensor("WattSensors/cpu/percent", "CPU Package [% von max W]");
        readonly WattSensor gpuBoard = new WattSensor("WattSensors/gpu/board", "GPU Board [W]");
        readonly WattSensor gpuPct = new WattSensor("WattSensors/gpu/percent", "GPU Board [% von max W]");
        readonly WattSensor sysTotal = new WattSensor("WattSensors/total", "CPU+GPU [W]");

        DateTime lastUpdate = DateTime.MinValue;

        public WattSensorsPlugin(IPluginLogger logger) { log = logger; }

        public string Name { get { return "Watt-Sensoren (CPU/GPU)"; } }

        public void Initialize()
        {
            LoadConfig();
            cpu = new CpuPower();
            gpu = new GpuPower { AmdSensorIndex = gpuSensorIndex };
            if (liveData) { bridge = new LiveBridge(); Log("LiveBridge: " + (bridge.Ok ? "ok" : bridge.Error)); }
            Log("CPU Energy Meter: " + (cpu.Ok ? "ok" : "n/a") + ", GPU: " + gpu.Source
                + ", CpuMaxW=" + cpuMax + ", GpuMaxW=" + gpuMax + ", Glättung=" + smoothSec + "s");
        }

        public void Load(IPluginSensorsContainer c)
        {
            if (cpu != null && cpu.Ok) { c.TempSensors.Add(cpuPkg); c.TempSensors.Add(cpuCores); c.TempSensors.Add(cpuPct); }
            if (gpu != null && gpu.Ok) { c.TempSensors.Add(gpuBoard); c.TempSensors.Add(gpuPct); }
            if (cpu != null && cpu.Ok && gpu != null && gpu.Ok) c.TempSensors.Add(sysTotal);
            Update();
        }

        public void Update()
        {
            DateTime now = DateTime.UtcNow;
            double dt = lastUpdate == DateTime.MinValue ? 0 : (now - lastUpdate).TotalSeconds;
            lastUpdate = now;
            // Exponentielle Glättung: nach "smoothSec" sind ~63 % eines Sprungs erreicht
            float a = (smoothSec <= 0 || dt <= 0) ? 1f : (float)(1 - Math.Exp(-dt / smoothSec));

            if (cpu != null && cpu.Ok)
            {
                float? p, k, l;
                cpu.Sample(out p, out k, out l);
                Smooth(cpuPkg, p, a);
                Smooth(cpuCores, k, a);
                cpuPct.Current = cpuPkg.Current.HasValue ? (float?)Math.Min(150f, cpuPkg.Current.Value / cpuMax * 100f) : null;
            }
            if (gpu != null && gpu.Ok)
            {
                Smooth(gpuBoard, gpu.Watts(), a);
                gpuPct.Current = gpuBoard.Current.HasValue ? (float?)Math.Min(150f, gpuBoard.Current.Value / gpuMax * 100f) : null;
            }
            sysTotal.Current = (cpuPkg.Current.HasValue && gpuBoard.Current.HasValue) ? (float?)(cpuPkg.Current.Value + gpuBoard.Current.Value) : null;
            if (bridge != null) bridge.Tick();
        }

        static void Smooth(WattSensor s, float? raw, float a)
        {
            if (!raw.HasValue) return; // letzten Wert behalten, falls ein Lesevorgang ausfällt
            s.Current = s.Current.HasValue ? s.Current.Value + a * (raw.Value - s.Current.Value) : raw.Value;
            s.Current = (float)Math.Round(s.Current.Value, 1);
        }

        public void Close()
        {
            if (cpu != null) cpu.Dispose();
            if (gpu != null) gpu.Dispose();
            if (bridge != null) bridge.Dispose();
            cpu = null; gpu = null; bridge = null;
            lastUpdate = DateTime.MinValue;
        }

        void LoadConfig()
        {
            string dir = Path.GetDirectoryName(typeof(WattSensorsPlugin).Assembly.Location);
            string ini = Path.Combine(dir, "FanControl.WattSensors.ini");
            try
            {
                if (!File.Exists(ini))
                    File.WriteAllText(ini,
                        "; FanControl.WattSensors - nach Änderung FanControl neu starten\r\n" +
                        "; Watt, die 100 % im \"% von max W\"-Sensor entsprechen\r\n" +
                        "CpuMaxWatt=250\r\nGpuMaxWatt=400\r\n" +
                        "; Glättung in Sekunden (0 = aus). Verhindert Hochdrehen bei kurzen Lastspitzen.\r\n" +
                        "SmoothingSeconds=3\r\n" +
                        "; Nur AMD: ADL-PMLog-Sensorindex (73 = Board Power bei RX 7000, 23 = ASIC Power bei älteren)\r\n" +
                        "GpuPowerSensorIndex=73\r\n");
                foreach (string raw in File.ReadAllLines(ini))
                {
                    string l = raw.Trim();
                    int eq = l.IndexOf('=');
                    if (l.StartsWith(";") || eq < 0) continue;
                    string k = l.Substring(0, eq).Trim().ToLowerInvariant();
                    float v;
                    if (!float.TryParse(l.Substring(eq + 1).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    if (k == "cpumaxwatt" && v > 0) cpuMax = v;
                    else if (k == "gpumaxwatt" && v > 0) gpuMax = v;
                    else if (k == "smoothingseconds" && v >= 0) smoothSec = v;
                    else if (k == "gpupowersensorindex") gpuSensorIndex = (int)v;
                    else if (k == "livedaten") liveData = v != 0;
                }
            }
            catch (Exception ex) { Log("Konfig-Fehler: " + ex.Message); }
        }

        void Log(string msg) { if (log != null) log.Log("[WattSensors] " + msg); }
    }
}
