// Dashboard: zeigt den PC als Modell - Lüfter mit Live-%/RPM, Wärmequellen, Luftdruck -, die Automatik-Regeln
// und pro Lüftergruppe, welche FanControl-Kurve gerade wie viel will (Überlagerung, Ergebnis dick).
// Zwei Layouts: breit, oder schmal/hoch (< 760 px, z. B. am Bildschirmrand angedockt).
// Ruhig: keine Animationen, Neuzeichnen nur alle 2 s und nur wenn sichtbar. F11 = Vollbild.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PowerTray
{
    class Dashboard : Form
    {
        static readonly Color Bg = Hex("#0D1117"), Panel = Hex("#151B23"), Line = Hex("#2A3340"),
            Ink = Hex("#E6EDF3"), Muted = Hex("#8B949E"), Dim = Hex("#4D5663"),
            Cool = Hex("#79C0FF"), Warm = Hex("#FFA657"), Hot = Hex("#FF7B72"), Good = Hex("#7EE787"), Air = Hex("#56D4DD");
        static readonly Color[] Series = { Hex("#79C0FF"), Hex("#FFA657"), Hex("#D2A8FF"), Hex("#7EE787"), Hex("#FF7B72"), Hex("#E3B341") };

        readonly TrayApp app;
        readonly Font fTitle = new Font("Segoe UI Semibold", 8.5f), fBody = new Font("Segoe UI", 9f), fBold = new Font("Segoe UI Semibold", 9.5f),
            fBig = new Font("Segoe UI Semibold", 18f), fNum = new Font("Consolas", 18f), fNumS = new Font("Consolas", 9f), fNumM = new Font("Consolas", 11f, FontStyle.Bold);
        bool fullscreen, docked;
        Rectangle restoreBounds;
        Rectangle killBtn, dockBtn;
        List<MeasureStep> measure; DateTime measureStamp;

        class MeasureStep { public string Name, Sub; public int CpuFan, CaseFan; public double? C, W; public bool Hot, Profile; }

        public Dashboard(TrayApp app)
        {
            this.app = app;
            base.Text = "PowerTray Dashboard";
            BackColor = Bg; ForeColor = Ink;
            MinimumSize = new Size(340, 560);
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F11) ToggleFullscreen(); else if (e.KeyCode == Keys.Escape && fullscreen) ToggleFullscreen(); };
            MouseClick += OnClick;
            MouseMove += (s, e) => Cursor = killBtn.Contains(e.Location) || dockBtn.Contains(e.Location) ? Cursors.Hand : Cursors.Default;

            Rectangle b = app.DashboardBounds;
            if (b.Width > 0 && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(b))) Bounds = b;
            else { var wa = Screen.PrimaryScreen.WorkingArea; Bounds = new Rectangle(wa.X + 80, wa.Y + 60, 1280, 820); }
            if (app.DashboardMaximized) WindowState = FormWindowState.Maximized;
            FormClosing += delegate { if (!fullscreen) app.SaveDashboardBounds(WindowState == FormWindowState.Normal ? Bounds : RestoreBounds, WindowState == FormWindowState.Maximized); };
        }

        void OnClick(object s, MouseEventArgs e)
        {
            if (killBtn.Contains(e.Location)) app.KillGame();
            else if (dockBtn.Contains(e.Location)) ToggleDock();
        }

        void ToggleFullscreen()
        {
            if (!fullscreen) { restoreBounds = Bounds; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds; }
            else { FormBorderStyle = FormBorderStyle.Sizable; Bounds = restoreBounds; }
            fullscreen = !fullscreen;
        }

        // Schmal und hoch an den rechten Rand des aktuellen Monitors andocken (und zurück).
        void ToggleDock()
        {
            if (fullscreen) ToggleFullscreen();
            WindowState = FormWindowState.Normal;
            if (!docked) { restoreBounds = Bounds; var wa = Screen.FromControl(this).WorkingArea; Bounds = new Rectangle(wa.Right - 400, wa.Top, 400, wa.Height); }
            else Bounds = restoreBounds;
            docked = !docked;
        }

        static Color Hex(string h) { return ColorTranslator.FromHtml(h); }
        static Color Mix(Color a, Color b, float t) { t = Math.Max(0, Math.Min(1, t)); return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }
        static Color TempColor(float? c) { if (!c.HasValue) return Dim; return c < 60 ? Cool : c < 80 ? Mix(Cool, Warm, (c.Value - 60) / 20f) : Mix(Warm, Hot, (c.Value - 80) / 12f); }
        static string N(float? v, string fmt = "0") { return v.HasValue ? v.Value.ToString(fmt) : "–"; }

        // ================================================================ Layout

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            killBtn = dockBtn = Rectangle.Empty;
            LoadMeasure();
            if (ClientSize.Width < 760) PaintNarrow(g); else PaintWide(g);
        }

        void PaintWide(Graphics g)
        {
            int m = 16, gap = 12, W = ClientSize.Width, H = ClientSize.Height;
            int y = m;
            DrawHeader(g, new Rectangle(m, y, W - 2 * m, 40), false); y += 44;
            DrawRules(g, new Rectangle(m, y, W - 2 * m, 40), false); y += 40 + gap;

            int mainH = Math.Max(300, (int)((H - y - m) * 0.52));
            int pcW = Math.Min((int)(mainH * 1.25), (int)((W - 2 * m) * 0.5));
            DrawPc(g, new Rectangle(m, y, pcW, mainH));
            var right = new Rectangle(m + pcW + gap, y, W - 2 * m - pcW - gap, mainH);
            bool showMeasure = measure != null && measure.Count > 0;
            int mw = showMeasure ? Math.Max(260, right.Width / 2 - gap / 2) : 0;
            DrawNow(g, new Rectangle(right.X, right.Y, right.Width - (showMeasure ? mw + gap : 0), right.Height));
            if (showMeasure) DrawMeasure(g, new Rectangle(right.Right - mw, right.Y, mw, right.Height));
            y += mainH + gap;

            DrawGroups(g, new Rectangle(m, y, W - 2 * m, H - y - m), false);
        }

        void PaintNarrow(Graphics g)
        {
            int m = 10, W = ClientSize.Width, H = ClientSize.Height;
            int y = m;
            DrawHeader(g, new Rectangle(m, y, W - 2 * m, 40), true); y += 44;
            y = DrawRules(g, new Rectangle(m, y, W - 2 * m, 0), true) + 8;
            int pcH = Math.Min((int)((W - 2 * m) * 0.85), (int)(H * 0.36));
            DrawPc(g, new Rectangle(m, y, W - 2 * m, pcH)); y += pcH + 8;
            int nowH = 150;
            DrawNowCompact(g, new Rectangle(m, y, W - 2 * m, nowH)); y += nowH + 8;
            DrawGroups(g, new Rectangle(m, y, W - 2 * m, H - y - m), true);
        }

        Rectangle PanelBox(Graphics g, Rectangle r, string title)
        {
            using (var p = Round(r, 8)) using (var b = new SolidBrush(Panel)) using (var pen = new Pen(Line)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            if (title != null) TextRenderer.DrawText(g, title.ToUpperInvariant(), fTitle, new Rectangle(r.X + 12, r.Y + 8, r.Width - 24, 16), Muted, TextFormatFlags.EndEllipsis);
            return new Rectangle(r.X + 12, r.Y + (title != null ? 28 : 10), r.Width - 24, r.Height - (title != null ? 36 : 20));
        }

        // ================================================================ Kopf + Hardware

        void DrawHeader(Graphics g, Rectangle r, bool narrow)
        {
            int idx = app.ActiveIndex;
            Profile p = idx >= 0 ? app.Profiles[idx] : null;
            using (var b = new SolidBrush(p != null ? p.Color : Muted)) g.FillEllipse(b, r.X, r.Y + 8, 18, 18);
            string name = p != null ? p.Name : Power.Name(Power.Active());
            TextRenderer.DrawText(g, name, fBig, new Rectangle(r.X + 26, r.Y, r.Width - 140, 34), Ink, TextFormatFlags.EndEllipsis);

            // Andock-Knopf
            dockBtn = new Rectangle(r.Right - 30, r.Y + 4, 28, 26);
            using (var pen = new Pen(Muted, 1.5f))
            {
                g.DrawRectangle(pen, dockBtn.X + 6, dockBtn.Y + 6, 16, 14);
                using (var b = new SolidBrush(docked ? Ink : Muted)) g.FillRectangle(b, dockBtn.X + 16, dockBtn.Y + 6, 6, 14);
            }
            TextRenderer.DrawText(g, DateTime.Now.ToString("HH:mm"), fNumS, new Point(r.Right - 76, r.Y + 10), Muted);

            if (!narrow)
            {
                string hw = HwLine();
                var hs = TextRenderer.MeasureText(name, fBig);
                TextRenderer.DrawText(g, hw, fBody, new Rectangle(r.X + 40 + hs.Width, r.Y + 12, r.Width - hs.Width - 170, 18), Muted, TextFormatFlags.EndEllipsis);
            }
        }

        static string HwLine()
        {
            var parts = new List<string>();
            if (HardwareInfo.Cpu.Length > 0) parts.Add(HardwareInfo.Cpu);
            if (HardwareInfo.Gpu.Length > 0) parts.Add(HardwareInfo.Gpu.Replace("AMD ", "").Replace("NVIDIA ", "") + (HardwareInfo.GpuVendor.Length > 0 ? " (" + HardwareInfo.GpuVendor + ")" : ""));
            if (HardwareInfo.Board.Length > 0) parts.Add(HardwareInfo.Board);
            if (HardwareInfo.Ram.Length > 0) parts.Add(HardwareInfo.Ram);
            return string.Join("  ·  ", parts.ToArray());
        }

        // ================================================================ Regeln 1-2-3

        int DrawRules(Graphics g, Rectangle r, bool vertical)
        {
            var a = app.Auto;
            AutoState[] order = { AutoState.Game, AutoState.Idle, AutoState.Desktop };
            int chipH = 34, gap = 8;
            int chipW = vertical ? r.Width : (r.Width - 2 * gap) / 3;
            int y = r.Y;
            for (int i = 0; i < 3; i++)
            {
                var st = order[i];
                var box = vertical ? new Rectangle(r.X, y, r.Width, chipH) : new Rectangle(r.X + i * (chipW + gap), r.Y, chipW, chipH);
                bool active = a.S.Enabled && a.State == st;
                int pIdx = a.ProfileFor(st);
                Color pc = pIdx >= 0 && pIdx < app.Profiles.Count ? app.Profiles[pIdx].Color : Muted;
                using (var path = Round(box, 6))
                {
                    using (var b = new SolidBrush(active ? Mix(Panel, pc, 0.22f) : Panel)) g.FillPath(b, path);
                    using (var pen = new Pen(active ? pc : Line, active ? 2f : 1f)) g.DrawPath(pen, path);
                }
                using (var b = new SolidBrush(active ? pc : Dim)) g.FillEllipse(b, box.X + 8, box.Y + 8, 18, 18);
                TextRenderer.DrawText(g, (i + 1).ToString(), fTitle, new Rectangle(box.X + 8, box.Y + 8, 18, 18), active ? Bg : Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, Automation.StateName(st), fBold, new Point(box.X + 32, box.Y + 8), active ? Ink : Muted);
                int tx = box.X + 32 + TextRenderer.MeasureText(Automation.StateName(st), fBold).Width + 8;
                int right = box.Right - 10;

                if (st == AutoState.Game && a.Game != null)
                {
                    killBtn = new Rectangle(box.Right - 92, box.Y + 5, 84, box.Height - 10);
                    using (var path = Round(killBtn, 4)) using (var b = new SolidBrush(Hex("#8B1E1E"))) g.FillPath(b, path);
                    TextRenderer.DrawText(g, L.T("■ Beenden", "■ Quit"), fBold, killBtn, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    right = killBtn.X - 6;
                    TextRenderer.DrawText(g, a.Game, fBody, new Rectangle(tx, box.Y + 9, right - tx, 18), Ink, TextFormatFlags.EndEllipsis);
                }
                else if (st == AutoState.Idle)
                {
                    double frac = Math.Min(1, a.Idle.TotalMinutes / Math.Max(1, a.S.IdleMinutes));
                    string t = ((int)a.Idle.TotalMinutes) + ":" + a.Idle.Seconds.ToString("00") + "/" + a.S.IdleMinutes;
                    int tw = TextRenderer.MeasureText(t, fNumS).Width;
                    var bar = new Rectangle(tx, box.Y + 14, Math.Max(10, right - tx - tw - 6), 6);
                    using (var b = new SolidBrush(Line)) g.FillRectangle(b, bar);
                    using (var b = new SolidBrush(frac >= 1 ? pc : Muted)) g.FillRectangle(b, bar.X, bar.Y, (int)(bar.Width * frac), bar.Height);
                    TextRenderer.DrawText(g, t, fNumS, new Point(right - tw, box.Y + 10), Muted);
                }
                else if (active && a.ManualOverride)
                    TextRenderer.DrawText(g, L.T("manuell", "manual"), fBody, new Point(tx, box.Y + 9), Warm);
                y += chipH + gap;
            }
            return vertical ? y - gap : r.Y + chipH;
        }

        // ================================================================ PC-Modell

        void DrawPc(Graphics g, Rectangle r)
        {
            Rectangle c = PanelBox(g, r, L.T("Luftstrom", "Airflow"));
            var groups = app.Groups;
            // Gehäuse (Seitenansicht: hinten links, vorne rechts)
            int legendRows = (int)Math.Ceiling(groups.Count(x => x.Case) / 2.0);
            int legendH = legendRows * 18 + 22;
            var cas = new Rectangle(c.X + 26, c.Y + 22, c.Width - 52, c.Height - 22 - legendH - 26);
            using (var pen = new Pen(Muted, 2f)) using (var path = Round(cas, 10)) g.DrawPath(pen, path);
            float fr = Math.Max(11, Math.Min(cas.Width, cas.Height) / 11f);   // Lüfterradius

            // Wärmequellen
            var cpuR = new RectangleF(cas.X + cas.Width * 0.2f, cas.Y + cas.Height * 0.28f, cas.Width * 0.26f, cas.Height * 0.2f);
            var gpuR = new RectangleF(cas.X + cas.Width * 0.1f, cas.Y + cas.Height * 0.58f, cas.Width * 0.6f, cas.Height * 0.12f);
            var last = app.Samples.Count > 0 ? app.Samples[app.Samples.Count - 1] : null;
            HeatBlock(g, cpuR, "CPU", app.CpuTemp, last != null ? last.Cpu : null);
            HeatBlock(g, gpuR, "GPU", app.GpuHotspot ?? app.GpuTemp, last != null ? last.Gpu : null);

            // Positionen der Lüftergruppen
            foreach (var grp in groups)
            {
                var pts = new List<PointF>();
                PointF dir;   // Luftrichtung
                switch (grp.Pos)
                {
                    case FanPos.Unten: for (int i = 0; i < grp.Count; i++) pts.Add(new PointF(cas.X + cas.Width * (0.2f + 0.5f * (grp.Count == 1 ? 0.5f : i / (float)(grp.Count - 1))), cas.Bottom)); dir = new PointF(0, -1); break;
                    case FanPos.Oben: for (int i = 0; i < grp.Count; i++) pts.Add(new PointF(cas.X + cas.Width * (0.25f + 0.5f * (grp.Count == 1 ? 0.5f : i / (float)(grp.Count - 1))), cas.Y)); dir = new PointF(0, -1); break;
                    case FanPos.Hinten: for (int i = 0; i < grp.Count; i++) pts.Add(new PointF(cas.X, cas.Y + cas.Height * (0.28f + 0.15f * i))); dir = new PointF(-1, 0); break;
                    case FanPos.Vorne: for (int i = 0; i < grp.Count; i++) pts.Add(new PointF(cas.Right, cas.Y + cas.Height * (0.25f + 0.5f * (grp.Count == 1 ? 0.5f : i / (float)(grp.Count - 1))))); dir = new PointF(-1, 0); break;
                    case FanPos.Seite: for (int i = 0; i < grp.Count; i++) pts.Add(new PointF(cas.X + cas.Width * 0.84f, cas.Y + cas.Height * (0.2f + 0.55f * (grp.Count == 1 ? 0.5f : i / (float)(grp.Count - 1))))); dir = new PointF(-1, 0); break;
                    case FanPos.Cpu: pts.Add(new PointF(cpuR.Right + fr * 0.9f, cpuR.Y + cpuR.Height / 2)); dir = new PointF(-1, 0); break;
                    default: pts.Add(new PointF(gpuR.X + gpuR.Width * 0.5f, gpuR.Bottom + fr * 0.9f)); dir = new PointF(0, -1); break;
                }
                foreach (var pt in pts) Fan(g, pt, grp.Pos == FanPos.Cpu || grp.Pos == FanPos.Gpu ? fr * 0.8f : fr, grp, dir);
            }

            // Legende: Gruppe · Richtung · Drehzahl (im Kreis steht nur die %-Zahl)
            int li = 0, colW = c.Width / 2, ly0 = cas.Bottom + (int)fr + 24;
            foreach (var grp in groups.Where(x => x.Case))
            {
                int lx = c.X + (li % 2) * colW, ly = ly0 + (li / 2) * 18;
                string arrow = grp.Intake ? "→ " : "← ";
                TextRenderer.DrawText(g, arrow + grp.Label + (grp.Count > 1 ? " " + grp.Count + "×" : ""), fBody, new Point(lx, ly), grp.Intake ? Air : Muted);
                TextRenderer.DrawText(g, N(grp.Rpm) + " rpm", fNumS, new Rectangle(lx, ly + 1, colW - 8, 16), Ink, TextFormatFlags.Right);
                li++;
            }

            // Druckanzeige
            double inA = groups.Where(x => x.Case && x.Intake).Sum(x => (x.Rpm ?? 0) * x.Count);
            double outA = groups.Where(x => x.Case && !x.Intake).Sum(x => (x.Rpm ?? 0) * x.Count);
            if (inA + outA > 0)
            {
                double ratio = outA > 0 ? inA / outA : 9;
                string txt = ratio > 1.05 ? L.T("Überdruck", "Positive pressure") : ratio < 0.95 ? L.T("Unterdruck", "Negative pressure") : L.T("ausgeglichen", "balanced");
                Color col = ratio > 1.05 ? Good : ratio < 0.95 ? Warm : Muted;
                string s = txt + "  " + L.T("rein", "in") + " " + (inA / 1000).ToString("0.0") + "k : " + L.T("raus", "out") + " " + (outA / 1000).ToString("0.0") + "k rpm";
                TextRenderer.DrawText(g, s, fBold, new Rectangle(c.X, cas.Bottom + (int)fr + 4, c.Width, 18), col, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }
            if (!app.Live.Fresh)
                TextRenderer.DrawText(g, L.T("Keine Board-Sensoren (WattSensors-Plugin mit LiveDaten=1 in FanControl nötig)", "No board sensors (needs WattSensors plugin with LiveDaten=1)"),
                    fBody, new Rectangle(c.X, c.Y - 2, c.Width, 18), Warm, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        void HeatBlock(Graphics g, RectangleF r, string label, float? temp, float? watts)
        {
            Color col = TempColor(temp);
            using (var path = Round(Rectangle.Round(r), 6))
            {
                using (var b = new SolidBrush(Mix(Panel, col, 0.25f))) g.FillPath(b, path);
                using (var pen = new Pen(col, 1.5f)) g.DrawPath(pen, path);
            }
            string t = label + "  " + N(temp) + " °C";
            string w = N(watts) + " W";
            TextRenderer.DrawText(g, t, fBold, Rectangle.Round(new RectangleF(r.X, r.Y + r.Height / 2 - 17, r.Width, 18)), Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, w, fNumS, Rectangle.Round(new RectangleF(r.X, r.Y + r.Height / 2 + 1, r.Width, 16)), Muted, TextFormatFlags.HorizontalCenter);
        }

        void Fan(Graphics g, PointF p, float rad, FanGroup grp, PointF dir)
        {
            float pct = grp.Percent ?? 0;
            Color col = grp.Percent.HasValue ? Mix(Air, Warm, (pct - 40) / 60f) : Dim;
            var rect = new RectangleF(p.X - rad, p.Y - rad, rad * 2, rad * 2);
            using (var b = new SolidBrush(Bg)) g.FillEllipse(b, rect);
            using (var pen = new Pen(Line, 3f)) g.DrawEllipse(pen, rect);
            if (grp.Percent.HasValue) using (var pen = new Pen(col, 3f)) g.DrawArc(pen, rect, -90, 360f * pct / 100f);
            TextRenderer.DrawText(g, N(grp.Percent), fTitle, Rectangle.Round(rect), Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            // Luftpfeil (Länge ~ Drehzahl)
            if (grp.Pos != FanPos.Cpu && grp.Pos != FanPos.Gpu && grp.Percent.HasValue)
            {
                // Pfeil beginnt am Kreisrand, damit die Zahl lesbar bleibt
                if (!grp.Intake) dir = grp.Pos == FanPos.Hinten ? new PointF(-1, 0) : new PointF(0, -1);
                float len = rad * (0.4f + 0.9f * pct / 100f);
                float sx = p.X + dir.X * (rad + 3), sy = p.Y + dir.Y * (rad + 3);
                using (var pen = new Pen(Color.FromArgb(200, col), 2f) { EndCap = LineCap.ArrowAnchor })
                    g.DrawLine(pen, sx, sy, sx + dir.X * len, sy + dir.Y * len);
            }
        }

        // ================================================================ Zahlen

        void DrawNow(Graphics g, Rectangle r)
        {
            Rectangle c = PanelBox(g, r, L.T("Jetzt", "Now"));
            var last = app.Samples.Count > 0 ? app.Samples[app.Samples.Count - 1] : null;
            int cols = 2, rows = 3, gap = 8;
            int tw = (c.Width - gap) / cols, th = Math.Min(80, (c.Height - 2 * gap) / rows);
            Tile(g, new Rectangle(c.X, c.Y, tw, th), "CPU", app.CpuTemp, "°C", TempColor(app.CpuTemp));
            Tile(g, new Rectangle(c.X + tw + gap, c.Y, tw, th), "GPU Hotspot", app.GpuHotspot ?? app.GpuTemp, "°C", TempColor(app.GpuHotspot ?? app.GpuTemp));
            Tile(g, new Rectangle(c.X, c.Y + th + gap, tw, th), L.T("CPU Leistung", "CPU power"), last != null ? last.Cpu : null, "W", Cool);
            Tile(g, new Rectangle(c.X + tw + gap, c.Y + th + gap, tw, th), L.T("GPU Leistung", "GPU power"), last != null ? last.Gpu : null, "W", Warm);
            Tile(g, new Rectangle(c.X, c.Y + 2 * (th + gap), tw, th), L.T("CPU Last", "CPU load"), last != null ? last.Load : null, "%", Good);
            Tile(g, new Rectangle(c.X + tw + gap, c.Y + 2 * (th + gap), tw, th), L.T("GPU Last", "GPU load"), app.GpuLoad, "%", Good);
            int y = c.Y + 3 * (th + gap);
            if (y + 18 <= c.Bottom)
                TextRenderer.DrawText(g, L.T("Lüfterprofil ", "Fan profile ") + (string.IsNullOrEmpty(app.FanConfig) ? "–" : Path.GetFileNameWithoutExtension(app.FanConfig)) + "  ·  " + Power.Name(Power.Active()),
                    fBody, new Rectangle(c.X, y, c.Width, 18), Muted, TextFormatFlags.EndEllipsis);
        }

        void DrawNowCompact(Graphics g, Rectangle r)
        {
            Rectangle c = PanelBox(g, r, null);
            var last = app.Samples.Count > 0 ? app.Samples[app.Samples.Count - 1] : null;
            string[] lbl = { "CPU", "GPU" };
            float?[] t = { app.CpuTemp, app.GpuHotspot ?? app.GpuTemp }, w = { last != null ? last.Cpu : null, last != null ? last.Gpu : null }, ld = { last != null ? last.Load : null, app.GpuLoad };
            int colW = c.Width / 2;
            for (int i = 0; i < 2; i++)
            {
                int x = c.X + i * colW;
                TextRenderer.DrawText(g, lbl[i], fTitle, new Point(x, c.Y), Muted);
                TextRenderer.DrawText(g, N(t[i]), fNum, new Point(x - 4, c.Y + 16), TempColor(t[i]));
                int nw = TextRenderer.MeasureText(N(t[i]), fNum).Width;
                TextRenderer.DrawText(g, "°C", fBody, new Point(x + nw - 8, c.Y + 26), Muted);
                TextRenderer.DrawText(g, N(w[i]) + " W  ·  " + N(ld[i]) + " %", fNumS, new Point(x, c.Y + 50), Ink);
            }
            string hw = HardwareInfo.Cpu + "\n" + HardwareInfo.Gpu;
            TextRenderer.DrawText(g, hw, fBody, new Rectangle(c.X, c.Y + 72, c.Width, 40), Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        void Tile(Graphics g, Rectangle r, string label, float? v, string unit, Color accent)
        {
            using (var p = Round(r, 6)) using (var b = new SolidBrush(Mix(Panel, Bg, 0.5f))) g.FillPath(b, p);
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, r.X, r.Y + 8, 3, r.Height - 16);
            TextRenderer.DrawText(g, label.ToUpperInvariant(), fTitle, new Rectangle(r.X + 10, r.Y + 6, r.Width - 14, 16), Muted, TextFormatFlags.EndEllipsis);
            string val = N(v);
            int vy = r.Y + Math.Max(22, r.Height / 2 - 6);
            TextRenderer.DrawText(g, val, fNum, new Point(r.X + 6, vy - 6), Ink);
            var vs = TextRenderer.MeasureText(val, fNum);
            TextRenderer.DrawText(g, unit, fBody, new Point(r.X + vs.Width, vy + 4), Muted);
        }

        // ================================================================ Lüftergruppen: Kurven überlagert

        void DrawGroups(Graphics g, Rectangle r, bool compact)
        {
            var groups = app.Groups.Where(x => x.Demands.Count > 0 || x.Percent.HasValue).ToList();
            if (groups.Count == 0)
            {
                Rectangle c0 = PanelBox(g, r, L.T("Lüfterkurven", "Fan curves"));
                TextRenderer.DrawText(g, L.T("Keine FanControl-Konfiguration geladen.", "No FanControl configuration loaded."), fBody, c0.Location, Dim);
                return;
            }
            if (compact || r.Height < 170) { DrawGroupBars(g, r, groups); return; }
            int cols = Math.Min(groups.Count, Math.Max(1, r.Width / 260));
            int rows = (int)Math.Ceiling(groups.Count / (double)cols);
            int gap = 12, cw = (r.Width - (cols - 1) * gap) / cols, ch = (r.Height - (rows - 1) * gap) / rows;
            for (int i = 0; i < groups.Count; i++)
                DrawGroupChart(g, new Rectangle(r.X + (i % cols) * (cw + gap), r.Y + (i / cols) * (ch + gap), cw, ch), groups[i]);
        }

        // Verlauf 10 min: jede Einzelkurve dünn, tatsächliche Drehzahl dick. Die aktuell höchste Kurve ist markiert.
        void DrawGroupChart(Graphics g, Rectangle r, FanGroup grp)
        {
            string head = grp.Label + (grp.Count > 1 ? " · " + grp.Count + "×" : "") + (grp.Case ? (grp.Intake ? L.T(" · rein", " · in") : L.T(" · raus", " · out")) : "");
            Rectangle c = PanelBox(g, r, head);
            var legendH = Math.Min(grp.Demands.Count, 4) * 16 + 4;
            var plot = new Rectangle(c.X + 26, c.Y + 2, c.Width - 26, Math.Max(30, c.Height - legendH - 6));
            using (var pen = new Pen(Line) { DashStyle = DashStyle.Dot })
                foreach (int pct in new[] { 0, 50, 100 })
                {
                    int y = plot.Bottom - plot.Height * pct / 100;
                    g.DrawLine(pen, plot.X, y, plot.Right, y);
                    TextRenderer.DrawText(g, pct.ToString(), fNumS, new Rectangle(c.X - 4, y - 7, 26, 14), Dim, TextFormatFlags.Right);
                }
            DateTime t1 = DateTime.Now, t0 = t1.AddMinutes(-10);
            var s = app.Samples.Where(x => x.T >= t0).ToList();
            Func<DateTime, float> X = t => plot.X + (float)((t - t0).TotalSeconds / 600.0 * plot.Width);
            Func<float, float> Y = v => plot.Bottom - Math.Max(0, Math.Min(100, v)) / 100f * plot.Height;

            float? best = grp.Demands.Where(d => d.Value.HasValue).Select(d => d.Value).DefaultIfEmpty(null).Max();
            for (int i = 0; i < grp.Demands.Count; i++)
            {
                string key = grp.Pos + "/" + grp.Demands[i].Key.Name;
                Color col = Series[i % Series.Length];
                Line_(g, s, x => { float? v; return x.Demand.TryGetValue(key, out v) ? v : null; }, X, Y, Color.FromArgb(170, col), 1.4f);
            }
            Line_(g, s, x => { float? v; return x.Fan.TryGetValue(grp.Pos, out v) ? v : null; }, X, Y, Ink, 2.6f);

            // Legende mit aktuellem Wert; die bestimmende Kurve (Maximum) fett + ◀
            int ly = plot.Bottom + 6;
            for (int i = 0; i < Math.Min(grp.Demands.Count, 4); i++)
            {
                var d = grp.Demands[i];
                Color col = Series[i % Series.Length];
                bool win = d.Value.HasValue && best.HasValue && Math.Abs(d.Value.Value - best.Value) < 0.05f;
                using (var b = new SolidBrush(col)) g.FillRectangle(b, c.X, ly + 6, 10, 3);
                TextRenderer.DrawText(g, d.Key.Name, win ? fBold : fBody, new Rectangle(c.X + 14, ly, c.Width - 70, 16), win ? Ink : Muted, TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, N(d.Value) + " %" + (win ? " ◀" : ""), fNumS, new Rectangle(c.Right - 60, ly + 1, 60, 16), win ? Ink : Muted, TextFormatFlags.Right);
                ly += 16;
            }
            string actual = N(grp.Percent) + " %";
            var asz = TextRenderer.MeasureText(actual, fNumM);
            TextRenderer.DrawText(g, actual, fNumM, new Point(r.Right - 12 - asz.Width, r.Y + 6), Ink);
        }

        static void Line_(Graphics g, List<Sample> s, Func<Sample, float?> v, Func<DateTime, float> X, Func<float, float> Y, Color c, float w)
        {
            var pts = new List<PointF>();
            using (var pen = new Pen(c, w) { LineJoin = LineJoin.Round })
            {
                foreach (var x in s)
                {
                    float? val = v(x);
                    if (!val.HasValue) { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); continue; }
                    pts.Add(new PointF(X(x.T), Y(val.Value)));
                }
                if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            }
        }

        // Kompakt: pro Gruppe ein Balken (tatsächlich), darauf Markierungen für jede Einzelkurve.
        void DrawGroupBars(Graphics g, Rectangle r, List<FanGroup> groups)
        {
            Rectangle c = PanelBox(g, r, L.T("Lüfter · Balken = Ist, Striche = Kurven", "Fans · bar = actual, ticks = curves"));
            int rowH = Math.Max(28, Math.Min(40, c.Height / Math.Max(1, groups.Count)));
            int labelW = 86, y = c.Y;
            foreach (var grp in groups)
            {
                if (y + rowH > c.Bottom + 4) break;
                TextRenderer.DrawText(g, grp.Label, fBold, new Rectangle(c.X, y + 2, labelW, 18), Ink, TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, N(grp.Rpm) + " rpm", fNumS, new Point(c.X, y + 17), Muted);
                var bar = new Rectangle(c.X + labelW, y + 6, c.Width - labelW - 44, 12);
                using (var b = new SolidBrush(Line)) g.FillRectangle(b, bar);
                float pct = grp.Percent ?? 0;
                using (var b = new SolidBrush(Mix(Air, Warm, (pct - 40) / 60f))) g.FillRectangle(b, bar.X, bar.Y, bar.Width * pct / 100f, bar.Height);
                float? best = grp.Demands.Where(d => d.Value.HasValue).Select(d => d.Value).DefaultIfEmpty(null).Max();
                for (int i = 0; i < grp.Demands.Count; i++)
                {
                    var d = grp.Demands[i];
                    if (!d.Value.HasValue) continue;
                    float x = bar.X + bar.Width * Math.Max(0, Math.Min(100, d.Value.Value)) / 100f;
                    bool win = best.HasValue && Math.Abs(d.Value.Value - best.Value) < 0.05f;
                    using (var pen = new Pen(Series[i % Series.Length], win ? 3f : 2f)) g.DrawLine(pen, x, bar.Y - 4, x, bar.Bottom + 4);
                }
                TextRenderer.DrawText(g, N(grp.Percent) + "%", fNumS, new Rectangle(bar.Right + 4, y + 4, 40, 16), Ink, TextFormatFlags.Right);
                y += rowH;
            }
        }

        // ================================================================ Messung (aus der Optimier-Session)

        void LoadMeasure()
        {
            try
            {
                var fi = new FileInfo(app.MeasurePath);
                if (!fi.Exists) { measure = null; return; }
                if (fi.LastWriteTime == measureStamp) return;
                measureStamp = fi.LastWriteTime;
                var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(fi.FullName));
                var list = new List<MeasureStep>();
                object steps;
                if (root.TryGetValue("schritte", out steps) && steps is System.Collections.ArrayList)
                    foreach (Dictionary<string, object> d in (System.Collections.ArrayList)steps)
                        list.Add(new MeasureStep
                        {
                            Name = Convert.ToString(d["schritt"]),
                            CpuFan = Convert.ToInt32(d["cpuFan"]), CaseFan = Convert.ToInt32(d["caseFan"]),
                            C = d.ContainsKey("cpuPkgC") && d["cpuPkgC"] != null ? (double?)Convert.ToDouble(d["cpuPkgC"]) : null,
                            W = d.ContainsKey("cpuW") && d["cpuW"] != null ? (double?)Convert.ToDouble(d["cpuW"]) : null,
                            Hot = d.ContainsKey("zuHeiss") && Convert.ToBoolean(d["zuHeiss"])
                        });
                if (root.TryGetValue("profiltests", out steps) && steps is System.Collections.ArrayList)
                    foreach (Dictionary<string, object> d in (System.Collections.ArrayList)steps)
                    {
                        Func<string, double> D = k => d.ContainsKey(k) && d[k] != null ? Convert.ToDouble(d[k]) : 0;
                        double caseLo = new[] { D("seite"), D("hinten"), D("oben") }.Min(), caseHi = new[] { D("seite"), D("hinten"), D("oben") }.Max();
                        list.Add(new MeasureStep
                        {
                            Profile = true, Name = Path.GetFileNameWithoutExtension(Convert.ToString(d["profil"])),
                            Sub = "CPU " + D("cpuFan").ToString("0") + "% · " + L.T("Geh. ", "Case ") + caseLo.ToString("0") + "–" + caseHi.ToString("0") + "%",
                            C = D("cpuPkgC"), W = D("cpuW"), Hot = d.ContainsKey("zuHeiss") && Convert.ToBoolean(d["zuHeiss"])
                        });
                    }
                measure = list;
            }
            catch { measure = null; }
        }

        // Balken: CPU-Temperatur je Lüfterstufe. Achse fest 40-100 °C, damit Unterschiede ehrlich aussehen.
        void DrawMeasure(Graphics g, Rectangle r)
        {
            double w = measure.Where(x => x.W.HasValue).Select(x => x.W.Value).DefaultIfEmpty(0).Average();
            Rectangle c = PanelBox(g, r, L.T("Gemessen · CPU bei ", "Measured · CPU at ") + w.ToString("0") + " W");
            var rows = measure.OrderBy(x => x.Profile).ThenBy(x => x.CaseFan).ThenBy(x => x.CpuFan).ToList();
            int rowH = Math.Min(34, (c.Height - 20) / Math.Max(1, rows.Count));
            int labelW = 112, y = c.Y;
            float lo = 40, hi = 100;
            var axis = new Rectangle(c.X + labelW, c.Y, c.Width - labelW - 50, rows.Count * rowH);
            foreach (var x in rows)
            {
                if (x.Profile)
                {
                    TextRenderer.DrawText(g, "▶ " + x.Name, fBold, new Rectangle(c.X, y + 2, labelW, 16), Good, TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, x.Sub, fNumS, new Rectangle(c.X, y + 17, labelW + 60, 14), Muted, TextFormatFlags.EndEllipsis);
                }
                else
                {
                    TextRenderer.DrawText(g, "CPU " + x.CpuFan + "%", fBold, new Point(c.X, y + 2), Ink);
                    TextRenderer.DrawText(g, L.T("Gehäuse ", "Case ") + x.CaseFan + "%", fNumS, new Point(c.X, y + 17), Muted);
                }
                var bar = new Rectangle(axis.X, y + 7, axis.Width, 14);
                using (var b = new SolidBrush(Line)) g.FillRectangle(b, bar);
                float v = x.Hot ? hi : (float)(x.C ?? lo);
                float f = (v - lo) / (hi - lo);
                using (var b = new SolidBrush(x.Hot ? Hot : TempColor(v))) g.FillRectangle(b, bar.X, bar.Y, bar.Width * Math.Max(0, Math.Min(1, f)), bar.Height);
                TextRenderer.DrawText(g, x.Hot ? "≥ 90 °C" : v.ToString("0") + " °C", fNumS, new Rectangle(bar.Right + 4, y + 6, 50, 16), Ink);
                y += rowH;
            }
            // Achse
            for (int t = 40; t <= 100; t += 20)
            {
                int x = axis.X + (int)(axis.Width * (t - lo) / (hi - lo));
                TextRenderer.DrawText(g, t.ToString(), fNumS, new Point(x - 8, y + 2), Dim);
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
