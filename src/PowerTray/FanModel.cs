// Modell des PCs fürs Dashboard:
//  - LiveData: Board-/CPU-Sensoren aus %ProgramData%\PowerTray\live.json (geschrieben vom WattSensors-Plugin)
//  - FanConfig: liest die aktive FanControl-Konfiguration und rechnet aus, was jede Einzelkurve gerade "will"
//  - CaseLayout: welcher Lüfter wo sitzt (unten/seite/vorne = rein, oben/hinten = raus)
//  - HardwareInfo: CPU, GPU, Board, RAM
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PowerTray
{
    class LiveData
    {
        static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerTray", "live.json");
        static readonly Regex SensorRx = new Regex("\\{\"id\":\"(?<id>[^\"]*)\",\"n\":\"(?<n>[^\"]*)\",\"k\":\"(?<k>[^\"]*)\",\"v\":(?<v>-?[0-9.]+)\\}");
        static readonly Regex HwRx = new Regex("\"name\":\"(?<n>[^\"]*)\",\"type\":\"(?<t>[^\"]*)\"");

        public readonly Dictionary<string, double> Values = new Dictionary<string, double>();
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>();
        public string BoardName;
        public bool Fresh;

        public void Read()
        {
            Fresh = false;
            try
            {
                var fi = new FileInfo(Path_);
                if (!fi.Exists || (DateTime.Now - fi.LastWriteTime).TotalSeconds > 15) return;
                string s;
                using (var fs = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) s = r.ReadToEnd();
                Values.Clear();
                foreach (Match m in SensorRx.Matches(s))
                {
                    Values[m.Groups["id"].Value] = double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
                    Names[m.Groups["id"].Value] = m.Groups["n"].Value;
                }
                foreach (Match m in HwRx.Matches(s)) if (m.Groups["t"].Value == "Motherboard") BoardName = m.Groups["n"].Value;
                Fresh = Values.Count > 0;
            }
            catch { }
        }

        /// <summary>FanControl-IDs ("/lpc/nct6797d/fan/3") auf LHM-IDs ("/lpc/nct6797d/0/fan/3") abbilden.</summary>
        public double? Get(string fcId)
        {
            if (string.IsNullOrEmpty(fcId)) return null;
            double v;
            if (Values.TryGetValue(fcId, out v)) return v;
            var m = Regex.Match(fcId, "^(/lpc/[^/]+)/(\\w+/\\d+)$");
            if (m.Success && Values.TryGetValue(m.Groups[1].Value + "/0/" + m.Groups[2].Value, out v)) return v;
            return null;
        }
    }

    class FanCurve
    {
        public string Name, Source;
        public bool IsMix;
        public int MixFunction;               // 0 Max, 1 Min, 2 Average, 3 Sum, 4 Subtract
        public List<string> Children = new List<string>();
        public List<float[]> Points = new List<float[]>();

        public float? Eval(float x)
        {
            if (Points.Count == 0) return null;
            if (x <= Points[0][0]) return Points[0][1];
            for (int i = 1; i < Points.Count; i++)
                if (x <= Points[i][0])
                {
                    float[] a = Points[i - 1], b = Points[i];
                    return a[1] + (b[1] - a[1]) * (x - a[0]) / Math.Max(0.001f, b[0] - a[0]);
                }
            return Points[Points.Count - 1][1];
        }
    }

    class FanControlInfo
    {
        public string NickName, ControlId, CurveName;
        public bool Enabled;
    }

    class FanConfig
    {
        public readonly Dictionary<string, FanCurve> Curves = new Dictionary<string, FanCurve>(StringComparer.OrdinalIgnoreCase);
        public readonly List<FanControlInfo> Controls = new List<FanControlInfo>();
        public string File_;
        DateTime stamp;

        public static string Find(IEnumerable<string> folders, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
            return folders.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        }

        /// <summary>Lädt neu, wenn sich Datei oder Zeitstempel geändert haben.</summary>
        public void Load(string path)
        {
            if (path == null) { Curves.Clear(); Controls.Clear(); File_ = null; return; }
            DateTime t = File.GetLastWriteTime(path);
            if (path == File_ && t == stamp) return;
            File_ = path; stamp = t;
            Curves.Clear(); Controls.Clear();
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                var fc = root["FanControl"] as Dictionary<string, object>;
                foreach (var o in (fc["FanCurves"] as System.Collections.ArrayList) ?? new System.Collections.ArrayList())
                {
                    var c = o as Dictionary<string, object>;
                    var curve = new FanCurve { Name = S(c, "Name") };
                    object v;
                    if (c.TryGetValue("SelectedFanCurves", out v) && v is System.Collections.ArrayList)
                    {
                        curve.IsMix = true;
                        foreach (var ch in (System.Collections.ArrayList)v) curve.Children.Add(S(ch as Dictionary<string, object>, "Name"));
                        curve.MixFunction = Convert.ToInt32(c.ContainsKey("SelectedMixFunction") ? c["SelectedMixFunction"] : 0);
                    }
                    else
                    {
                        if (c.TryGetValue("SelectedTempSource", out v) && v is Dictionary<string, object>) curve.Source = S((Dictionary<string, object>)v, "Identifier");
                        if (c.TryGetValue("Points", out v) && v is System.Collections.ArrayList)
                            foreach (var p in (System.Collections.ArrayList)v)
                            {
                                string[] xy = Convert.ToString(p).Split(',');
                                float x, y;
                                if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                                    curve.Points.Add(new[] { x, y });
                            }
                        curve.Points.Sort((a, b) => a[0].CompareTo(b[0]));
                    }
                    if (curve.Name != null) Curves[curve.Name] = curve;
                }
                foreach (var o in (fc["Controls"] as System.Collections.ArrayList) ?? new System.Collections.ArrayList())
                {
                    var c = o as Dictionary<string, object>;
                    object sel;
                    c.TryGetValue("SelectedFanCurve", out sel);
                    Controls.Add(new FanControlInfo
                    {
                        NickName = S(c, "NickName"),
                        ControlId = S(c, "Identifier"),
                        CurveName = sel is Dictionary<string, object> ? S((Dictionary<string, object>)sel, "Name") : null,
                        Enabled = c.ContainsKey("Enable") && Convert.ToBoolean(c["Enable"])
                    });
                }
            }
            catch { }
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        /// <summary>Alle Graph-Kurven unterhalb einer (Mix-)Kurve, mit ihrem aktuellen Wunschwert.</summary>
        public void Leaves(string curve, Func<string, float?> sensor, List<KeyValuePair<FanCurve, float?>> into, int depth = 0)
        {
            FanCurve c;
            if (curve == null || depth > 6 || !Curves.TryGetValue(curve, out c)) return;
            if (c.IsMix) { foreach (string ch in c.Children) Leaves(ch, sensor, into, depth + 1); return; }
            float? x = sensor(c.Source);
            into.Add(new KeyValuePair<FanCurve, float?>(c, x.HasValue ? c.Eval(x.Value) : null));
        }
    }

    enum FanPos { Cpu, Unten, Seite, Vorne, Hinten, Oben, Gpu }

    class FanGroup
    {
        public FanPos Pos;
        public string Label, NickName;
        public int Count = 1;
        public string ControlId;        // FanControl-ID der Steuerung
        public string Id;                // eindeutig je Gruppe (mehrere Gruppen pro Position möglich)
        public bool? Dir;               // true = rein, false = raus, null = Standard der Position
        public bool Radiator;           // AIO-Radiator: kühlt die CPU direkt
        public bool Intake { get { return Dir ?? (Pos == FanPos.Unten || Pos == FanPos.Seite || Pos == FanPos.Vorne); } }
        public bool Case { get { return Pos != FanPos.Cpu && Pos != FanPos.Gpu; } }
        public string FanId { get { return ControlId == null ? null : ControlId.Replace("/control/", "/fan/"); } }

        // Live
        public float? Percent, Rpm;
        public readonly List<KeyValuePair<FanCurve, float?>> Demands = new List<KeyValuePair<FanCurve, float?>>();
    }

    static class CaseLayout
    {
        public static readonly string[] Keys = { "CPU", "Unten", "Seite", "Vorne", "Hinten", "Oben", "GPU" };

        static FanPos Parse(string k)
        {
            return (FanPos)Array.IndexOf(Keys, k);
        }

        /// <summary>Position auch englisch/kleingeschrieben erkennen.</summary>
        static FanPos? ParsePos(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "cpu": return FanPos.Cpu;
                case "gpu": return FanPos.Gpu;
                case "unten": case "bottom": return FanPos.Unten;
                case "seite": case "side": return FanPos.Seite;
                case "vorne": case "front": return FanPos.Vorne;
                case "hinten": case "rear": case "back": return FanPos.Hinten;
                case "oben": case "top": return FanPos.Oben;
                default: return null;
            }
        }

        public static string Label(FanPos p)
        {
            switch (p)
            {
                case FanPos.Cpu: return L.T("CPU-Kühler", "CPU cooler");
                case FanPos.Unten: return L.T("Unten", "Bottom");
                case FanPos.Seite: return L.T("Seite", "Side");
                case FanPos.Vorne: return L.T("Vorne", "Front");
                case FanPos.Hinten: return L.T("Hinten", "Rear");
                case FanPos.Oben: return L.T("Oben", "Top");
                default: return "GPU";
            }
        }

        /// <summary>
        /// Aus [Gehaeuse] oder per Namensraten aus der FanControl-Konfig. Formate:
        ///   LuefterN=Position|NickName|Anzahl|rein/raus|Radiator   (beliebig viele Gruppen, auch mehrere pro Position)
        ///   Position=NickName|Anzahl                               (Kurzform, eine Gruppe pro Position)
        /// </summary>
        public static List<FanGroup> Build(Dictionary<string, string> ini, FanConfig cfg)
        {
            var list = new List<FanGroup>();
            if (ini != null)
                foreach (var kv in ini.Where(x => x.Key.StartsWith("Luefter", StringComparison.OrdinalIgnoreCase) || x.Key.StartsWith("Fan", StringComparison.OrdinalIgnoreCase))
                                      .OrderBy(x => { int n; int.TryParse(Regex.Replace(x.Key, "\\D", ""), out n); return n; }))
                {
                    string[] p = kv.Value.Split('|').Select(x => x.Trim()).ToArray();
                    FanPos? pos = p.Length > 1 ? ParsePos(p[0]) : null;
                    if (!pos.HasValue) continue;
                    int n = 1;
                    if (p.Length > 2) int.TryParse(p[2], out n);
                    string dir = p.Length > 3 ? p[3].ToLowerInvariant() : "";
                    bool rad = p.Skip(3).Any(x => x.Equals("Radiator", StringComparison.OrdinalIgnoreCase) || x.Equals("AIO", StringComparison.OrdinalIgnoreCase));
                    list.Add(new FanGroup
                    {
                        Pos = pos.Value, NickName = p[1], Count = Math.Max(1, n), Radiator = rad,
                        Dir = dir == "rein" || dir == "in" ? true : dir == "raus" || dir == "out" ? (bool?)false : null,
                        Label = Label(pos.Value) + (rad ? " AIO" : "")
                    });
                }
            if (list.Count == 0 && ini != null && ini.Count > 0)
            {
                foreach (string k in Keys)
                {
                    string v;
                    if (!ini.TryGetValue(k, out v) || v.Length == 0) continue;
                    string[] parts = v.Split('|');
                    int n = 1;
                    if (parts.Length > 1) int.TryParse(parts[1].Trim(), out n);
                    list.Add(new FanGroup { Pos = Parse(k), NickName = parts[0].Trim(), Count = Math.Max(1, n), Label = Label(Parse(k)) });
                }
            }
            if (list.Count == 0)
            {
                foreach (var c in cfg.Controls)
                {
                    if (!c.Enabled || c.NickName == null) continue;
                    string n = c.NickName.ToUpperInvariant();
                    FanPos? p = n.Contains("CPU") ? FanPos.Cpu : n.Contains("RADEON") || n.Contains("GEFORCE") || n.Contains("GPU") ? FanPos.Gpu
                              : n.Contains("UNTEN") || n.Contains("BOTTOM") ? FanPos.Unten : n.Contains("SIDE") || n.Contains("SEITE") ? FanPos.Seite
                              : n.Contains("FRONT") || n.Contains("VORNE") ? FanPos.Vorne : n.Contains("BACK") || n.Contains("REAR") || n.Contains("HINTEN") ? FanPos.Hinten
                              : n.Contains("TOP") || n.Contains("OBEN") ? FanPos.Oben : (FanPos?)null;
                    if (p.HasValue && !list.Any(g => g.Pos == p.Value)) list.Add(new FanGroup { Pos = p.Value, NickName = c.NickName, Label = Label(p.Value) });
                }
            }
            for (int i = 0; i < list.Count; i++) list[i].Id = "g" + i + ":" + list[i].NickName;
            // Gleichnamige Gruppen an einer Position unterscheidbar machen ("Vorne", "Vorne 2")
            foreach (var same in list.GroupBy(x => x.Label).Where(x => x.Count() > 1))
            {
                int k = 1;
                foreach (var g in same) { if (k > 1) g.Label += " " + k; k++; }
            }
            foreach (var g in list)
            {
                var c = cfg.Controls.FirstOrDefault(x => string.Equals(x.NickName, g.NickName, StringComparison.OrdinalIgnoreCase));
                if (c != null) g.ControlId = c.ControlId;
            }
            return list;
        }
    }

    static class HardwareInfo
    {
        public static string Cpu = "", Gpu = "", Board = "", Ram = "", GpuVendor = "";

        public static void Load()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get()) { Cpu = Clean(Convert.ToString(o["Name"])); break; }
                using (var s = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (n.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        Gpu = n;
                        GpuVendor = n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ? "NVIDIA" : n.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ? "AMD" : n.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ? "Intel" : "";
                        if (GpuVendor != "Intel") break;
                    }
                using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                    foreach (ManagementObject o in s.Get()) { Board = (Convert.ToString(o["Manufacturer"]).Replace("Micro-Star International Co., Ltd.", "MSI") + " " + Convert.ToString(o["Product"])).Trim(); break; }
                double bytes = 0;
                using (var s = new ManagementObjectSearcher("SELECT Capacity, Speed FROM Win32_PhysicalMemory"))
                {
                    int speed = 0;
                    foreach (ManagementObject o in s.Get()) { bytes += Convert.ToDouble(o["Capacity"]); speed = Math.Max(speed, Convert.ToInt32(o["Speed"] ?? 0)); }
                    Ram = Math.Round(bytes / (1L << 30)) + " GB" + (speed > 0 ? " · " + speed + " MT/s" : "");
                }
            }
            catch { }
        }

        static string Clean(string s) { return Regex.Replace(Regex.Replace(s.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", ""), "@.*$", ""), "\\s+", " ").Trim(); }
    }
}
