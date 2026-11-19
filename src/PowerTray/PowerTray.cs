// PowerTray - Energieplan + FanControl-Lüfterkurve per Tray-Icon umschalten.
// Mausrad über dem Icon: hoch = mehr Leistung, runter = leiser.
// Rechts-/Linksklick: Menü. Einstellungen in PowerTray.ini neben der EXE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PowerTray")]
[assembly: AssemblyVersion("1.2.0.0")]

namespace PowerTray
{
    static class L
    {
        public static readonly bool De = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        public static string T(string de, string en) { return De ? de : en; }
    }

    class Profile
    {
        public string Name;
        public Guid Plan;
        public string FanConfig;
        public Color Color;
    }

    static class Native
    {
        [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] public static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint flags, uint index, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")] public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }
        [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public int x, y; public int mouseData; public uint flags, time; public IntPtr extra; }
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    }

    static class Power
    {
        const uint ACCESS_SCHEME = 16;

        public static Guid Active()
        {
            IntPtr p;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return Guid.Empty;
            Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            Native.LocalFree(p);
            return g;
        }

        public static bool SetActive(Guid g)
        {
            return Native.PowerSetActiveScheme(IntPtr.Zero, ref g) == 0;
        }

        public static string Name(Guid g)
        {
            uint size = 0;
            Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size == 0) return g.ToString();
            byte[] buf = new byte[size];
            if (Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return g.ToString();
            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }

        public static List<KeyValuePair<Guid, string>> List()
        {
            var list = new List<KeyValuePair<Guid, string>>();
            for (uint i = 0; i < 64; i++)
            {
                byte[] buf = new byte[16];
                uint size = 16;
                if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buf, ref size) != 0) break;
                Guid g = new Guid(buf);
                list.Add(new KeyValuePair<Guid, string>(g, Name(g)));
            }
            return list;
        }
    }

    class TrayApp : ApplicationContext
    {
        const string AppName = "PowerTray";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly string exePath = Application.ExecutablePath;
        readonly string iniPath;
        List<Profile> profiles = new List<Profile>();
        string fanControlExe;
        string fanConfigFolder;
        bool watchdogEnabled = true;
        string currentFanConfig;   // zuletzt von uns gesetzte Lüfterkonfig
        Guid lastPlan = Guid.Empty;

        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        readonly Control invoker = new Control();
        Icon currentIcon;

        Native.HookProc hookProc;  // Referenz halten, sonst sammelt der GC den Delegate ein
        IntPtr hook = IntPtr.Zero;
        DateTime lastStep = DateTime.MinValue;
        int wheelAccum;

        // ---------- Automatik, Messwerte, Dashboard ----------
        public readonly Automation Auto = new Automation();
        public readonly List<Sample> Samples = new List<Sample>();
        public readonly List<Decision> Decisions = new List<Decision>();
        public Rectangle DashboardBounds;
        public bool DashboardMaximized;
        readonly PowerTray.Sensors.CpuPower cpuSensor;
        readonly PowerTray.Sensors.GpuPower gpuSensor;
        Dashboard dash;
        public readonly LiveData Live = new LiveData();
        public readonly FanConfig FanCfg = new FanConfig();
        public List<FanGroup> Groups = new List<FanGroup>();
        Dictionary<string, string> caseIni;
        string groupsFor;
        public float? CpuTemp, GpuTemp, GpuHotspot, GpuLoad;
        public string MeasurePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerTray", "messung.json");

        public List<Profile> Profiles { get { return profiles; } }
        public int ActiveIndex { get { return CurrentIndex(); } }
        public string FanConfig { get { return currentFanConfig; } }
        public string CpuSource { get { return cpuSensor.Ok ? "RAPL" : "n/a"; } }
        public string GpuSource { get { return gpuSensor.Source; } }

        public TrayApp()
        {
            iniPath = Path.Combine(Path.GetDirectoryName(exePath), "PowerTray.ini");
            invoker.CreateControl();
            cpuSensor = new PowerTray.Sensors.CpuPower();
            gpuSensor = new PowerTray.Sensors.GpuPower();
            LoadSettings();

            tray.ContextMenuStrip = menu;
            menu.Opening += delegate { BuildMenu(); };
            tray.MouseUp += OnTrayClick;
            tray.Visible = true;

            hookProc = HookCallback;
            hook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, hookProc, Native.GetModuleHandle(null), 0);

            poll.Interval = 2000;
            poll.Tick += delegate { Refresh(false); Watchdog(); Tick(); };
            poll.Start();

            // Beim Start Lüfterkurve passend zum aktiven Energieplan setzen (FanControl merkt sich sonst die letzte)
            int startIdx = CurrentIndex();
            if (startIdx >= 0) ApplyFanConfig(profiles[startIdx].FanConfig, true);
            Refresh(true);
            Tick();
        }

        // Alle 2 s: messen, Automatik auswerten, Dashboard neu zeichnen.
        void Tick()
        {
            float? p = null, k, l = null;
            if (cpuSensor.Ok) cpuSensor.Sample(out p, out k, out l);
            var smp = new Sample { T = DateTime.Now, Cpu = p, Gpu = gpuSensor.Ok ? gpuSensor.Watts() : null, Load = l, Profile = CurrentIndex() };
            UpdateFanModel(smp);
            Samples.Add(smp);
            if (Samples.Count > 1900) Samples.RemoveRange(0, Samples.Count - 1800);   // ~60 min

            string reason;
            bool changed = Auto.Evaluate(out reason);
            if (changed && Auto.S.Enabled)
            {
                int target = Auto.ProfileFor(Auto.State);
                if (target >= 0 && target < profiles.Count && target != CurrentIndex()) ApplyProfile(target, reason, false);
                else if (target >= 0 && target < profiles.Count && !profiles[target].FanConfig.Equals(currentFanConfig ?? "", StringComparison.OrdinalIgnoreCase))
                    ApplyFanConfig(profiles[target].FanConfig, true);
            }
            if (dash != null && !dash.IsDisposed && dash.Visible && dash.WindowState != FormWindowState.Minimized) dash.Invalidate();
        }

        // Live-Sensoren, aktive FanControl-Kurven und Lüftergruppen aktualisieren.
        void UpdateFanModel(Sample smp)
        {
            Live.Read();
            GpuTemp = gpuSensor.EdgeTemp(); GpuHotspot = gpuSensor.HotspotTemp(); GpuLoad = gpuSensor.Load();
            CpuTemp = (float?)Live.Get("/intelcpu/0/temperature/10") ?? (float?)Live.Get("/amdcpu/0/temperature/2");
            smp.CpuTemp = CpuTemp; smp.GpuTemp = GpuHotspot ?? GpuTemp;

            string path = FanControlInfo_Path();
            FanCfg.Load(path);
            if (path != groupsFor) { Groups = CaseLayout.Build(caseIni, FanCfg); groupsFor = path; }

            foreach (var g in Groups)
            {
                if (g.Pos == FanPos.Gpu) { g.Percent = gpuSensor.FanPct(); g.Rpm = gpuSensor.FanRpm(); }
                else { g.Percent = (float?)Live.Get(g.ControlId); g.Rpm = (float?)Live.Get(g.FanId); }
                g.Demands.Clear();
                var ctl = FanCfg.Controls.FirstOrDefault(c => string.Equals(c.NickName, g.NickName, StringComparison.OrdinalIgnoreCase));
                if (ctl != null && ctl.Enabled) FanCfg.Leaves(ctl.CurveName, id => SensorValue(id, smp), g.Demands);
                smp.Fan[g.Pos] = g.Percent;
                foreach (var d in g.Demands) smp.Demand[g.Pos + "/" + d.Key.Name] = d.Value;
            }
        }

        string FanControlInfo_Path()
        {
            return global::PowerTray.FanConfig.Find(ConfigFolders(), currentFanConfig);
        }

        /// <summary>Wert einer FanControl-Sensor-ID, so gut PowerTray ihn kennt.</summary>
        float? SensorValue(string id, Sample s)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.Contains("WattSensors/"))
            {
                if (id.EndsWith("/cpu/package") || id.EndsWith("/cpu/cores")) return s.Cpu;
                if (id.EndsWith("/gpu/board")) return s.Gpu;
                if (id.EndsWith("/total")) return s.Cpu.HasValue && s.Gpu.HasValue ? s.Cpu + s.Gpu : null;
                return null;
            }
            if (id.StartsWith("ADLX/") || id.IndexOf("gpu", StringComparison.OrdinalIgnoreCase) >= 0 && id.IndexOf("temp", StringComparison.OrdinalIgnoreCase) >= 0)
                return id.IndexOf("Hotspot", StringComparison.OrdinalIgnoreCase) >= 0 ? GpuHotspot : GpuTemp;
            return (float?)Live.Get(id);
        }

        /// <summary>Laufendes Spiel hart beenden (nach Rückfrage).</summary>
        public void KillGame()
        {
            var procs = Auto.GameProcesses();
            if (procs.Count == 0) { Balloon(L.T("Kein Spiel gefunden", "No game found"), L.T("Es läuft kein Prozess aus steamapps\\common.", "No process from steamapps\\common is running."), ToolTipIcon.Info); return; }
            string names = string.Join(", ", procs.Select(x => x.ProcessName).Distinct().ToArray());
            if (MessageBox.Show(L.T("Spiel hart beenden?\n\n", "Force-quit the game?\n\n") + names + L.T("\n\nNicht gespeicherter Fortschritt geht verloren.", "\n\nUnsaved progress will be lost."),
                    "PowerTray", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            int failed = 0;
            foreach (var x in procs)
            {
                try { x.Kill(); } catch { failed++; }
                x.Dispose();
            }
            if (failed > 0)
                Balloon(L.T("Nicht alles beendet", "Not everything closed"), failed + L.T(" Prozess(e) brauchen Adminrechte (z. B. Anti-Cheat). Task-Manager als Admin nutzen.", " process(es) need admin rights (e.g. anti-cheat). Use Task Manager as admin."), ToolTipIcon.Warning);
        }

        void LogDecision(int from, int to, string reason, bool manual)
        {
            Decisions.Add(new Decision
            {
                T = DateTime.Now,
                From = from >= 0 && from < profiles.Count ? profiles[from].Name : null,
                To = to >= 0 && to < profiles.Count ? profiles[to].Name : Power.Name(Power.Active()),
                Color = to >= 0 && to < profiles.Count ? profiles[to].Color : Color.Gray,
                Reason = reason,
                Manual = manual
            });
            if (Decisions.Count > 200) Decisions.RemoveAt(0);
        }

        public void InvokeShowDashboard()
        {
            invoker.BeginInvoke((MethodInvoker)ShowDashboard);
        }

        public void ShowDashboard()
        {
            if (HardwareInfo.Cpu.Length == 0) HardwareInfo.Load();
            if (dash == null || dash.IsDisposed) dash = new Dashboard(this);
            dash.Show();
            if (dash.WindowState == FormWindowState.Minimized) dash.WindowState = FormWindowState.Normal;
            dash.Activate();
        }

        public void SaveDashboardBounds(Rectangle b, bool max)
        {
            DashboardBounds = b; DashboardMaximized = max;
            try { SaveIniValue("Dashboard", "Position", b.X + "," + b.Y + "," + b.Width + "," + b.Height + "," + (max ? 1 : 0)); } catch { }
        }

        // ---------- Einstellungen ----------

        void LoadSettings()
        {
            if (!File.Exists(iniPath)) WriteDefaultIni();
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> cur = null;
            foreach (string raw in File.ReadAllLines(iniPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[line.Substring(1, line.Length - 2).Trim()] = cur;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (cur != null && eq > 0) cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            Dictionary<string, string> general;
            sections.TryGetValue("Allgemein", out general);
            fanControlExe = Get(general, "FanControlExe", "");
            if (!File.Exists(fanControlExe)) fanControlExe = FindFanControl() ?? fanControlExe;
            fanConfigFolder = Get(general, "FanConfigFolder", "");
            watchdogEnabled = Get(general, "FanControlWaechter", "1") != "0";

            Dictionary<string, string> au;
            sections.TryGetValue("Automatik", out au);
            var st = Auto.S;
            int n;
            st.Enabled = Get(au, "Aktiv", "1") != "0";
            if (int.TryParse(Get(au, "IdleMinuten", "10"), out n) && n > 0) st.IdleMinutes = n;
            if (int.TryParse(Get(au, "ProfilSpiel", "3"), out n)) st.GameProfile = n;
            if (int.TryParse(Get(au, "ProfilIdle", "1"), out n)) st.IdleProfile = n;
            if (int.TryParse(Get(au, "ProfilDesktop", "2"), out n)) st.DesktopProfile = n;
            st.GameExes = Get(au, "SpielExe", "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            st.SteamIgnore = new HashSet<int>(Get(au, "SteamIgnorieren", "431960").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => { int v; return int.TryParse(x, out v) ? v : 0; }).Where(v => v > 0));

            Dictionary<string, string> db;
            sections.TryGetValue("Dashboard", out db);
            sections.TryGetValue("Gehaeuse", out caseIni);
            groupsFor = "\0";   // Lüftergruppen neu aufbauen
            string[] pos = Get(db, "Position", "").Split(',');
            int px, py, pw, ph;
            if (pos.Length >= 4 && int.TryParse(pos[0], out px) && int.TryParse(pos[1], out py) && int.TryParse(pos[2], out pw) && int.TryParse(pos[3], out ph))
            { DashboardBounds = new Rectangle(px, py, pw, ph); DashboardMaximized = pos.Length > 4 && pos[4] == "1"; }

            profiles = new List<Profile>();
            for (int i = 1; i <= 9; i++)
            {
                Dictionary<string, string> s;
                if (!sections.TryGetValue("Profil" + i, out s)) continue;
                Guid plan;
                if (!Guid.TryParse(Get(s, "Plan", ""), out plan)) continue;
                Color c;
                try { c = ColorTranslator.FromHtml(Get(s, "Farbe", "#4A90E2")); } catch { c = Color.SteelBlue; }
                profiles.Add(new Profile { Name = Get(s, "Name", "Profil " + i), Plan = plan, FanConfig = Get(s, "FanConfig", ""), Color = c });
            }
        }

        static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d != null && d.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        // Sucht FanControl an üblichen Orten (Installer, winget, portable, laufender Prozess).
        static string FindFanControl()
        {
            var c = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FanControl", "FanControl.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FanControl", "FanControl.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FanControl", "FanControl.exe"),
            };
            try { foreach (var p in Process.GetProcessesByName("FanControl")) c.Insert(0, p.MainModule.FileName); } catch { }
            return c.FirstOrDefault(File.Exists);
        }

        // Erster Start: Profile aus den vorhandenen Windows-Energieplänen erzeugen.
        void WriteDefaultIni()
        {
            var known = new[]
            {
                new { G = "a1841308-3541-4fab-bc81-f71556f20b4a", N = L.T("Leise", "Quiet"),          F = "Leise.json",      C = "#3CB371" },
                new { G = "381b4222-f694-41f0-9685-ff5bb260df2e", N = L.T("Ausgewogen", "Balanced"),   F = "Ausgewogen.json", C = "#4A90E2" },
                new { G = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", N = L.T("Leistung", "Performance"), F = "Leistung.json",   C = "#F5A623" },
                new { G = "e9a42b02-d5df-448d-aa00-03f14749eb61", N = "Turbo",                         F = "Turbo.json",      C = "#E5533D" },
            };
            var plans = Power.List();
            var present = new HashSet<Guid>(plans.Select(kv => kv.Key));
            string[] colors = { "#3CB371", "#4A90E2", "#F5A623", "#E5533D" };
            var sb = new StringBuilder();
            sb.AppendLine(L.T("; PowerTray Einstellungen - nach Änderungen im Menü \"Einstellungen neu laden\" klicken.",
                              "; PowerTray settings - click \"Reload settings\" in the menu after editing."));
            sb.AppendLine(L.T("; Profile von leise (oben) nach Leistung (unten). Mausrad hoch = nächstes Profil.",
                              "; Profiles from quiet (top) to performance (bottom). Mouse wheel up = next profile."));
            sb.AppendLine(L.T("; Plan = GUID aus 'powercfg /list'. FanConfig = in FanControl gespeicherte Konfiguration (leer = Lüfter nicht umschalten).",
                              "; Plan = GUID from 'powercfg /list'. FanConfig = configuration saved in FanControl (empty = don't switch fans)."));
            sb.AppendLine();
            sb.AppendLine("[Allgemein]");
            sb.AppendLine(L.T("; Leer = automatisch suchen", "; Empty = auto-detect"));
            sb.AppendLine("FanControlExe=");
            sb.AppendLine("FanConfigFolder=");
            sb.AppendLine(L.T("; 1 = FanControl neu starten, falls es beendet wird", "; 1 = restart FanControl if it exits"));
            sb.AppendLine("FanControlWaechter=1");
            int n = 0;
            foreach (var k in known)
            {
                if (!present.Contains(new Guid(k.G))) continue;
                n++;
                sb.AppendLine().AppendLine("[Profil" + n + "]").AppendLine("Name=" + k.N).AppendLine("Plan=" + k.G)
                  .AppendLine("FanConfig=" + k.F).AppendLine("Farbe=" + k.C);
            }
            if (n < 2) // z. B. Modern Standby / OEM-Pläne: alle vorhandenen Pläne übernehmen
            {
                n = 0;
                sb.Length = sb.ToString().IndexOf("[Profil") < 0 ? sb.Length : sb.ToString().IndexOf("\r\n[Profil");
                foreach (var kv in plans)
                {
                    n++;
                    sb.AppendLine().AppendLine("[Profil" + n + "]").AppendLine("Name=" + kv.Value).AppendLine("Plan=" + kv.Key)
                      .AppendLine("FanConfig=").AppendLine("Farbe=" + colors[Math.Min(n - 1, 3)]);
                }
            }
            sb.AppendLine();
            sb.AppendLine(L.T("; Automatik: Spiel > Leerlauf > Desktop. Werte = Nummer aus [ProfilN].",
                              "; Automation: game > idle > desktop. Values = number from [ProfilN]."));
            sb.AppendLine("[Automatik]");
            sb.AppendLine("Aktiv=1");
            sb.AppendLine("IdleMinuten=10");
            sb.AppendLine("ProfilSpiel=" + Math.Min(3, Math.Max(1, n)));
            sb.AppendLine("ProfilIdle=1");
            sb.AppendLine("ProfilDesktop=" + Math.Min(2, Math.Max(1, n)));
            sb.AppendLine(L.T("; Zusätzliche Spiele außerhalb von Steam (Exe-Namen, Komma-getrennt)", "; Extra non-Steam games (exe names, comma-separated)"));
            sb.AppendLine("SpielExe=");
            sb.AppendLine(L.T("; Steam-AppIDs, die nicht als Spiel zählen (431960 = Wallpaper Engine)", "; Steam AppIDs that don't count as games (431960 = Wallpaper Engine)"));
            sb.AppendLine("SteamIgnorieren=431960");
            File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(true));
        }

        void SaveIniValue(string section, string key, string value)
        {
            var lines = File.ReadAllLines(iniPath, Encoding.UTF8).ToList();
            int sec = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (sec < 0) { lines.Add(""); lines.Add("[" + section + "]"); lines.Add(key + "=" + value); }
            else
            {
                int i = sec + 1, insertAt = sec + 1;
                bool done = false;
                for (; i < lines.Count && !lines[i].Trim().StartsWith("["); i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length > 0) insertAt = i + 1;
                    int eq = t.IndexOf('=');
                    if (eq > 0 && !t.StartsWith(";") && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    { lines[i] = key + "=" + value; done = true; break; }
                }
                if (!done) lines.Insert(insertAt, key + "=" + value);
            }
            File.WriteAllLines(iniPath, lines, new UTF8Encoding(true));
        }

        // ---------- FanControl ----------

        IEnumerable<string> ConfigFolders()
        {
            var dirs = new List<string>();
            if (fanConfigFolder.Length > 0) dirs.Add(fanConfigFolder);
            string fcDir = Path.GetDirectoryName(fanControlExe);
            if (!string.IsNullOrEmpty(fcDir)) dirs.Add(Path.Combine(fcDir, "Configurations"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FanControl", "Configurations"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControl", "Configurations"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FanControl", "Configurations"));
            return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        List<string> FanConfigs()
        {
            var result = new List<string>();
            foreach (string d in ConfigFolders())
                foreach (string f in Directory.GetFiles(d, "*.json"))
                {
                    string n = Path.GetFileName(f);
                    if (n.Equals("userConfig.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!result.Contains(n, StringComparer.OrdinalIgnoreCase)) result.Add(n);
                }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        bool FanConfigExists(string name)
        {
            if (Path.IsPathRooted(name)) return File.Exists(name);
            return ConfigFolders().Any(d => File.Exists(Path.Combine(d, name)));
        }

        bool ApplyFanConfig(string name, bool quiet)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (!File.Exists(fanControlExe))
            {
                if (!quiet) Balloon(L.T("FanControl nicht gefunden", "FanControl not found"), fanControlExe, ToolTipIcon.Warning);
                return false;
            }
            if (!FanConfigExists(name))
            {
                if (!quiet) Balloon(L.T("Lüfterkurve fehlt", "Fan profile missing"), "\"" + name + "\" " + L.T("gibt es noch nicht. In FanControl anlegen und unter diesem Namen speichern.", "does not exist yet. Create it in FanControl and save it under this name."), ToolTipIcon.Warning);
                return false;
            }
            bool running = Process.GetProcessesByName("FanControl").Length > 0;
            try
            {
                var psi = new ProcessStartInfo(fanControlExe, "-c \"" + name + "\"" + (running ? "" : " -m"));
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(fanControlExe);
                Process.Start(psi);
                currentFanConfig = name;
                return true;
            }
            catch (Exception ex)
            {
                Balloon(L.T("FanControl-Fehler", "FanControl error"), ex.Message, ToolTipIcon.Error);
                return false;
            }
        }

        // Wächter: FanControl muss laufen, sonst bleiben z. B. GPU-Lüfter auf dem letzten Wert hängen.
        DateTime lastWatchdogStart = DateTime.MinValue;
        int watchdogMisses;

        void Watchdog()
        {
            if (!watchdogEnabled || !File.Exists(fanControlExe)) return;
            if (Process.GetProcessesByName("FanControl").Length > 0) { watchdogMisses = 0; return; }
            if (++watchdogMisses < 3) return;                                        // ~6 s Gnadenfrist (z. B. Update/Neustart)
            if ((DateTime.Now - lastWatchdogStart).TotalSeconds < 60) return;        // nicht im Kreis neu starten
            lastWatchdogStart = DateTime.Now;
            watchdogMisses = 0;
            int idx = CurrentIndex();
            string cfg = idx >= 0 ? profiles[idx].FanConfig : currentFanConfig;
            try
            {
                var psi = new ProcessStartInfo(fanControlExe, (string.IsNullOrEmpty(cfg) ? "" : "-c \"" + cfg + "\" ") + "-m");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(fanControlExe);
                Process.Start(psi);
                if (!string.IsNullOrEmpty(cfg)) currentFanConfig = cfg;
                Balloon(L.T("FanControl neu gestartet", "FanControl restarted"), L.T("FanControl lief nicht mehr und wurde automatisch neu gestartet.", "FanControl had stopped and was restarted automatically."), ToolTipIcon.Warning);
            }
            catch (Exception ex) { Balloon("FanControl watchdog", ex.Message, ToolTipIcon.Error); }
        }

        // ---------- Profile ----------

        int CurrentIndex()
        {
            Guid a = Power.Active();
            return profiles.FindIndex(p => p.Plan == a);
        }

        void ApplyProfile(int idx)
        {
            ApplyProfile(idx, L.T("Manuell gewählt", "Chosen manually"), true);
        }

        void ApplyProfile(int idx, string reason, bool manual)
        {
            if (idx < 0 || idx >= profiles.Count) return;
            if (manual) Auto.ManualOverride = Auto.S.Enabled;
            LogDecision(CurrentIndex(), idx, reason, manual);
            lastPlan = profiles[idx].Plan;   // eigener Wechsel, nicht als "extern" werten
            Profile p = profiles[idx];
            if (!Power.SetActive(p.Plan))
                Balloon(L.T("Energieplan", "Power plan"), "\"" + p.Name + "\": " + L.T("Plan konnte nicht aktiviert werden (GUID prüfen).", "plan could not be activated (check GUID)."), ToolTipIcon.Error);
            ApplyFanConfig(p.FanConfig, false);
            Refresh(true);
        }

        void Step(int dir)
        {
            if (profiles.Count == 0) return;
            int idx = CurrentIndex();
            int next = idx < 0 ? (dir > 0 ? 0 : profiles.Count - 1) : Math.Max(0, Math.Min(profiles.Count - 1, idx + dir));
            if (next != idx) ApplyProfile(next);
        }

        void Refresh(bool force)
        {
            Guid active = Power.Active();
            if (!force && active == lastPlan) return;

            // Plan wurde extern gewechselt (z.B. Windows-Einstellungen) -> passende Lüfterkurve nachziehen
            if (!force && lastPlan != Guid.Empty)
            {
                int prevIdx = profiles.FindIndex(p => p.Plan == lastPlan);
                LogDecision(prevIdx, profiles.FindIndex(p => p.Plan == active), L.T("Energieplan außerhalb von PowerTray gewechselt", "Power plan changed outside PowerTray"), true);
                if (Auto.S.Enabled) Auto.ManualOverride = true;
                Profile ext = profiles.FirstOrDefault(p => p.Plan == active);
                if (ext != null && !string.IsNullOrEmpty(ext.FanConfig) && !ext.FanConfig.Equals(currentFanConfig, StringComparison.OrdinalIgnoreCase))
                    ApplyFanConfig(ext.FanConfig, true);
            }
            lastPlan = active;

            int idx = profiles.FindIndex(p => p.Plan == active);
            Icon old = currentIcon;
            currentIcon = DrawIcon(idx);
            tray.Icon = currentIcon;
            if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }

            string text = idx >= 0 ? profiles[idx].Name : Power.Name(active);
            string fan = string.IsNullOrEmpty(currentFanConfig) ? "" : L.T("\nLüfter: ", "\nFans: ") + Path.GetFileNameWithoutExtension(currentFanConfig);
            string tip = "PowerTray: " + text + fan + L.T("\nMausrad = wechseln", "\nScroll = switch");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        Icon DrawIcon(int idx)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int n = Math.Max(1, profiles.Count);
                Color on = idx >= 0 ? profiles[idx].Color : Color.Gray;
                Color off = Color.FromArgb(110, 128, 128, 128);
                float gap = size / 10f;
                float w = (size - gap * (n + 1)) / n;
                for (int i = 0; i < n; i++)
                {
                    float h = size * (0.35f + 0.6f * (n == 1 ? 1 : (float)i / (n - 1)));
                    var r = new RectangleF(gap + i * (w + gap), size - h - gap / 2, w, h);
                    using (var b = new SolidBrush(i <= idx ? on : off))
                    using (var path = Rounded(r, Math.Min(w, h) / 3))
                        g.FillPath(b, path);
                }
                if (idx < 0)
                    using (var f = new Font("Segoe UI", size * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(Color.White))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("?", f, b, new RectangleF(0, 0, size, size), sf);
                    }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.5f, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- Mausrad über dem Tray-Icon ----------

        bool TryGetIconRect(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                var t = typeof(NotifyIcon);
                var win = (NativeWindow)t.GetField("window", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tray);
                int id = (int)t.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tray);
                var nid = new Native.NOTIFYICONIDENTIFIER();
                nid.cbSize = (uint)Marshal.SizeOf(typeof(Native.NOTIFYICONIDENTIFIER));
                nid.hWnd = win.Handle;
                nid.uID = (uint)id;
                Native.RECT r;
                if (Native.Shell_NotifyIconGetRect(ref nid, out r) != 0) return false;
                rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                return true;
            }
            catch { return false; }
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == 0x020A /*WM_MOUSEWHEEL*/)
            {
                var ms = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                Rectangle rect;
                if (TryGetIconRect(out rect) && rect.Contains(ms.x, ms.y))
                {
                    int delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                    invoker.BeginInvoke((MethodInvoker)delegate { OnWheel(delta); });
                    return (IntPtr)1; // Event schlucken
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void OnWheel(int delta)
        {
            wheelAccum += delta;
            if (Math.Abs(wheelAccum) < 120) return;   // Präzisions-Touchpads: erst ab einer vollen Rastung
            int dir = wheelAccum > 0 ? 1 : -1;
            wheelAccum = 0;
            if ((DateTime.Now - lastStep).TotalMilliseconds < 250) return; // Freilauf-Mausräder bremsen
            lastStep = DateTime.Now;
            Step(dir);
        }

        // ---------- Menü ----------

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(tray, null);
            }
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            Guid active = Power.Active();

            var title = new ToolStripMenuItem(L.T("PowerTray  (Mausrad über dem Icon = wechseln)", "PowerTray  (scroll over the icon = switch)")) { Enabled = false };
            menu.Items.Add(title);
            var dashItem = new ToolStripMenuItem(L.T("Dashboard öffnen", "Open dashboard")) { Font = new Font(menu.Font, FontStyle.Bold) };
            dashItem.Click += delegate { ShowDashboard(); };
            menu.Items.Add(dashItem);
            string autoTxt = L.T("Automatik (Spiel → ", "Automation (game → ") + ProfName(Auto.S.GameProfile) + ", " +
                             L.T("Leerlauf → ", "idle → ") + ProfName(Auto.S.IdleProfile) + ", " +
                             L.T("sonst → ", "else → ") + ProfName(Auto.S.DesktopProfile) + ")";
            var autoItem = new ToolStripMenuItem(autoTxt) { Checked = Auto.S.Enabled };
            autoItem.Click += delegate
            {
                Auto.S.Enabled = !Auto.S.Enabled;
                Auto.HasState = false; Auto.ManualOverride = false;   // beim Einschalten sofort neu bewerten
                SaveIniValue("Automatik", "Aktiv", Auto.S.Enabled ? "1" : "0");
                Tick();
            };
            menu.Items.Add(autoItem);
            if (Auto.Game != null)
            {
                var kill = new ToolStripMenuItem(L.T("Spiel beenden: ", "Quit game: ") + Auto.Game) { ForeColor = Color.Firebrick };
                kill.Click += delegate { KillGame(); };
                menu.Items.Add(kill);
            }
            menu.Items.Add(new ToolStripSeparator());

            for (int i = 0; i < profiles.Count; i++)
            {
                Profile p = profiles[i];
                int idx = i;
                string fan = string.IsNullOrEmpty(p.FanConfig) ? "" : " · " + Path.GetFileNameWithoutExtension(p.FanConfig);
                var item = new ToolStripMenuItem(p.Name + "   (" + Power.Name(p.Plan) + fan + ")");
                item.Checked = p.Plan == active;
                item.Image = Swatch(p.Color);
                item.Click += delegate { ApplyProfile(idx); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());

            // Energieplan einzeln
            var plans = new ToolStripMenuItem(L.T("Nur Energieplan", "Power plan only"));
            foreach (var kv in Power.List())
            {
                Guid g = kv.Key;
                var it = new ToolStripMenuItem(kv.Value) { Checked = g == active };
                it.Click += delegate { Power.SetActive(g); lastPlan = g; Refresh(true); };
                plans.DropDownItems.Add(it);
            }
            menu.Items.Add(plans);

            // Lüfterkurve einzeln
            var fans = new ToolStripMenuItem(L.T("Nur Lüfterkurve", "Fan profile only"));
            var cfgs = FanConfigs();
            if (cfgs.Count == 0) fans.DropDownItems.Add(new ToolStripMenuItem(L.T("(keine FanControl-Konfigurationen gefunden)", "(no FanControl configurations found)")) { Enabled = false });
            foreach (string c in cfgs)
            {
                string name = c;
                var it = new ToolStripMenuItem(Path.GetFileNameWithoutExtension(c)) { Checked = c.Equals(currentFanConfig, StringComparison.OrdinalIgnoreCase) };
                it.Click += delegate { ApplyFanConfig(name, false); Refresh(true); };
                fans.DropDownItems.Add(it);
            }
            fans.DropDownItems.Add(new ToolStripSeparator());
            var openFolder = new ToolStripMenuItem(L.T("Konfig-Ordner öffnen", "Open config folder"));
            openFolder.Click += delegate
            {
                string d = ConfigFolders().FirstOrDefault();
                if (d != null) Process.Start("explorer.exe", "\"" + d + "\"");
                else Balloon(L.T("Kein Ordner gefunden", "No folder found"), L.T("Erst in FanControl eine Konfiguration speichern oder den Ordner wählen.", "Save a configuration in FanControl first or choose the folder."), ToolTipIcon.Info);
            };
            fans.DropDownItems.Add(openFolder);
            var chooseFolder = new ToolStripMenuItem(L.T("Konfig-Ordner wählen …", "Choose config folder …"));
            chooseFolder.Click += delegate { ChooseFolder(); };
            fans.DropDownItems.Add(chooseFolder);
            menu.Items.Add(fans);

            menu.Items.Add(new ToolStripSeparator());

            var openFc = new ToolStripMenuItem(L.T("FanControl öffnen", "Open FanControl")) { Enabled = File.Exists(fanControlExe) };
            openFc.Click += delegate { Process.Start(new ProcessStartInfo(fanControlExe) { WorkingDirectory = Path.GetDirectoryName(fanControlExe) }); };
            menu.Items.Add(openFc);

            var powerOpts = new ToolStripMenuItem(L.T("Windows-Energieoptionen", "Windows power options"));
            powerOpts.Click += delegate { Process.Start("control.exe", "powercfg.cpl"); };
            menu.Items.Add(powerOpts);

            var editIni = new ToolStripMenuItem(L.T("Einstellungen bearbeiten (PowerTray.ini)", "Edit settings (PowerTray.ini)"));
            editIni.Click += delegate { Process.Start("notepad.exe", "\"" + iniPath + "\""); };
            menu.Items.Add(editIni);

            var reload = new ToolStripMenuItem(L.T("Einstellungen neu laden", "Reload settings"));
            reload.Click += delegate { LoadSettings(); Refresh(true); };
            menu.Items.Add(reload);

            var autostart = new ToolStripMenuItem(L.T("Mit Windows starten", "Start with Windows")) { Checked = IsAutostart() };
            autostart.Click += delegate { SetAutostart(!IsAutostart()); };
            menu.Items.Add(autostart);

            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem(L.T("Beenden", "Exit"));
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);
        }

        string ProfName(int oneBased)
        {
            return oneBased >= 1 && oneBased <= profiles.Count ? profiles[oneBased - 1].Name : "?";
        }

        static Image Swatch(Color c)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(c))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(b, 3, 3, 10, 10);
            }
            return bmp;
        }

        void ChooseFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = L.T("Ordner mit den FanControl-Konfigurationen (*.json) wählen", "Choose the folder with FanControl configurations (*.json)");
                string d = ConfigFolders().FirstOrDefault();
                if (d != null) dlg.SelectedPath = d;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    fanConfigFolder = dlg.SelectedPath;
                    SaveIniValue("Allgemein", "FanConfigFolder", fanConfigFolder);
                }
            }
        }

        bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }

        void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + exePath + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            tray.ShowBalloonTip(4000, title, text, icon);
        }

        protected override void ExitThreadCore()
        {
            poll.Stop();
            if (dash != null && !dash.IsDisposed) dash.Close();
            cpuSensor.Dispose();
            gpuSensor.Dispose();
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool wantDash = args.Any(a => a.Equals("--dashboard", StringComparison.OrdinalIgnoreCase));
            bool created;
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PowerTray_ShowDashboard"))
            using (var m = new Mutex(true, @"Local\PowerTray_SingleInstance", out created))
            {
                if (!created) { if (wantDash) show.Set(); return; }   // läuft schon -> dort Dashboard öffnen
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var app = new TrayApp();
                ThreadPool.RegisterWaitForSingleObject(show, delegate { app.InvokeShowDashboard(); }, null, -1, false);
                if (wantDash) app.ShowDashboard();
                Application.Run(app);
            }
        }
    }
}
