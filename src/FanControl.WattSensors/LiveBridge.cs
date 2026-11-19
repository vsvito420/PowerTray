// LiveBridge: liest Board- und CPU-Sensoren (Temperaturen, Lüfter-RPM, Lüfter-%) über die LibreHardwareMonitor-
// Bibliothek, die FanControl ohnehin geladen hat, und schreibt sie alle 2 s nach %ProgramData%\PowerTray\live.json.
// Nur lesend - es werden keine Lüfter angesteuert. PowerTray (ohne Adminrechte) liest die Datei fürs Dashboard.
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace FanControl.WattSensors
{
    class LiveBridge : IDisposable
    {
        object computer;
        DateTime last = DateTime.MinValue;
        readonly string path;
        public string Error;

        public LiveBridge()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerTray");
            path = Path.Combine(dir, "live.json");
            try
            {
                Directory.CreateDirectory(dir);
                Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "LibreHardwareMonitorLib");
                if (asm == null)
                {
                    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LibreHardwareMonitorLib.dll");
                    if (File.Exists(p)) asm = Assembly.LoadFrom(p);
                }
                if (asm == null) { Error = "LibreHardwareMonitorLib nicht gefunden"; return; }
                Type t = asm.GetType("LibreHardwareMonitor.Hardware.Computer");
                computer = Activator.CreateInstance(t);
                t.GetProperty("IsMotherboardEnabled").SetValue(computer, true, null);
                t.GetProperty("IsCpuEnabled").SetValue(computer, true, null);
                t.GetMethod("Open", Type.EmptyTypes).Invoke(computer, null);
            }
            catch (Exception ex) { Error = (ex.InnerException ?? ex).Message; computer = null; }
        }

        public bool Ok { get { return computer != null; } }

        static object P(object o, string name) { return o.GetType().GetProperty(name).GetValue(o, null); }

        public void Tick()
        {
            if (computer == null || (DateTime.UtcNow - last).TotalSeconds < 2) return;
            last = DateTime.UtcNow;
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"t\":\"").Append(DateTime.Now.ToString("o")).Append("\",\"hw\":[");
                bool firstHw = true;
                foreach (object hw in (IEnumerable)P(computer, "Hardware"))
                {
                    hw.GetType().GetMethod("Update").Invoke(hw, null);
                    foreach (object sub in (IEnumerable)P(hw, "SubHardware")) sub.GetType().GetMethod("Update").Invoke(sub, null);
                    if (!firstHw) sb.Append(',');
                    firstHw = false;
                    sb.Append("{\"name\":").Append(Q((string)P(hw, "Name"))).Append(",\"type\":").Append(Q(P(hw, "HardwareType").ToString())).Append(",\"s\":[");
                    bool first = true;
                    AppendSensors(sb, hw, ref first);
                    foreach (object sub in (IEnumerable)P(hw, "SubHardware")) AppendSensors(sb, sub, ref first);
                    sb.Append("]}");
                }
                sb.Append("]}");
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex) { Error = (ex.InnerException ?? ex).Message; }
        }

        static void AppendSensors(StringBuilder sb, object hw, ref bool first)
        {
            foreach (object s in (IEnumerable)P(hw, "Sensors"))
            {
                string type = P(s, "SensorType").ToString();
                if (type != "Temperature" && type != "Fan" && type != "Control" && type != "Power" && type != "Load") continue;
                object v = P(s, "Value");
                if (v == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(Q(P(s, "Identifier").ToString()))
                  .Append(",\"n\":").Append(Q((string)P(s, "Name")))
                  .Append(",\"k\":").Append(Q(type))
                  .Append(",\"v\":").Append(((float)v).ToString("0.#", CultureInfo.InvariantCulture)).Append('}');
            }
        }

        static string Q(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public void Dispose()
        {
            try { if (computer != null) computer.GetType().GetMethod("Close").Invoke(computer, null); } catch { }
            computer = null;
        }
    }
}
