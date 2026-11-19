// Automatik: wählt das Profil nach Zustand. Reihenfolge = Priorität, die erste zutreffende Regel gewinnt:
//   1. Spiel     - Steam meldet ein laufendes Spiel (oder eine Exe aus SpielExe läuft)
//   2. Leerlauf  - keine Maus-/Tastatureingabe seit IdleMinuten
//   3. Desktop   - alles andere
// Dazu Aufzeichnung der Messwerte und Entscheidungen für das Dashboard.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PowerTray
{
    enum AutoState { Game = 0, Idle = 1, Desktop = 2 }

    class Sample
    {
        public DateTime T;
        public float? Cpu, Gpu, Load, CpuTemp, GpuTemp;
        public int Profile;
        public Dictionary<string, float?> Fan = new Dictionary<string, float?>();
        public Dictionary<string, float?> Demand = new Dictionary<string, float?>();   // "Pos/Kurve" -> %
    }

    class Decision
    {
        public DateTime T;
        public string From, To, Reason;
        public Color Color;
        public bool Manual;
    }

    class AutoSettings
    {
        public bool Enabled = true;
        public int IdleMinutes = 10;
        public int GameProfile = 3, IdleProfile = 1, DesktopProfile = 2;   // 1-basiert wie [ProfilN]
        public string[] GameExes = new string[0];
        public HashSet<int> SteamIgnore = new HashSet<int> { 431960 };    // Wallpaper Engine läuft dauerhaft über Steam
    }

    class Automation
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public AutoSettings S = new AutoSettings();
        public AutoState State = AutoState.Desktop;
        public bool HasState;
        public TimeSpan Idle;
        public string Game;            // Name des erkannten Spiels oder null
        public string GameSource;      // "Steam" / "Exe"
        public bool ManualOverride;    // Nutzer hat manuell gewählt -> gilt bis zum nächsten Zustandswechsel

        public static TimeSpan IdleTime()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            uint ms = unchecked((uint)Environment.TickCount - li.dwTime);
            return TimeSpan.FromMilliseconds(ms);
        }

        string SteamGame()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k == null) return null;
                    int id = Convert.ToInt32(k.GetValue("RunningAppID", 0));
                    if (id <= 0 || S.SteamIgnore.Contains(id)) return null;
                    using (var a = k.OpenSubKey(@"Apps\" + id))
                    {
                        string n = a != null ? a.GetValue("Name") as string : null;
                        return string.IsNullOrEmpty(n) ? "Steam-App " + id : n;
                    }
                }
            }
            catch { return null; }
        }

        string ExeGame()
        {
            foreach (string e in S.GameExes)
            {
                string n = Path.GetFileNameWithoutExtension(e.Trim());
                if (n.Length == 0) continue;
                var ps = Process.GetProcessesByName(n);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (any) return n;
            }
            return null;
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return null;
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { CloseHandle(h); }
        }

        /// <summary>Prozesse des laufenden Spiels: alles aus steamapps\common plus SpielExe-Einträge.</summary>
        public List<Process> GameProcesses()
        {
            var list = new List<Process>();
            var exes = new HashSet<string>(S.GameExes.Select(e => Path.GetFileNameWithoutExtension(e.Trim())), StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                string path = ImagePath(p.Id);
                bool hit = (path != null && path.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0) || exes.Contains(p.ProcessName);
                if (hit) list.Add(p); else p.Dispose();
            }
            return list;
        }

        /// <summary>Bewertet die Regeln. Gibt true zurück, wenn sich der Zustand geändert hat.</summary>
        public bool Evaluate(out string reason)
        {
            Idle = IdleTime();
            Game = SteamGame(); GameSource = "Steam";
            if (Game == null) { Game = ExeGame(); GameSource = "Exe"; }

            AutoState next;
            if (Game != null) { next = AutoState.Game; reason = L.T("Spiel läuft: ", "Game running: ") + Game + " (" + GameSource + ")"; }
            else if (Idle.TotalMinutes >= S.IdleMinutes) { next = AutoState.Idle; reason = L.T("Keine Eingabe seit ", "No input for ") + S.IdleMinutes + " min"; }
            else { next = AutoState.Desktop; reason = L.T("Desktop, kein Spiel", "Desktop, no game"); }

            bool changed = !HasState || next != State;
            HasState = true;
            State = next;
            if (changed) ManualOverride = false;
            return changed;
        }

        public int ProfileFor(AutoState s)
        {
            return (s == AutoState.Game ? S.GameProfile : s == AutoState.Idle ? S.IdleProfile : S.DesktopProfile) - 1;
        }

        public static string StateName(AutoState s)
        {
            return s == AutoState.Game ? L.T("SPIEL", "GAME") : s == AutoState.Idle ? L.T("LEERLAUF", "IDLE") : "DESKTOP";
        }
    }
}
