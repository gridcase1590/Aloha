using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // VpnGateWindow — Aloha-native picker for VPN Gate relays.
    // DafyFrame chrome; a GDI list scrolled with Up/Down (file-explorer
    // model: green inverse selection) and connected to with Enter.
    //
    //  * status/connection line lives in the DafyFrame indented footer
    //  * Win9x-styled scroll arrows down the right edge of the list
    //  * Proto column shows TCP/UDP (read from each server's real .ovpn)
    //  * double-click (or Space) opens a VpnDetailWindow with every field
    //    exposes (score, uptime, users, traffic, operator, message...)
    //
    // Data + engine live in VpnGateClient. This window is the UI only.
    // ============================================================
    public class VpnGateWindow : DafyFrame
    {
        private static readonly Color Green  = Color.FromArgb(0x33, 0xFF, 0x66);
        private static readonly Color Dim    = Color.FromArgb(0x2C, 0x8A, 0x4A);

        private readonly VpnGateClient client = new VpnGateClient();
        private List<VpnGateClient.Server> servers = new List<VpnGateClient.Server>();   // full set
        private List<VpnGateClient.Server> view = new List<VpnGateClient.Server>();       // filtered/shown
        private string filterText = "";
        private int selIdx = 0;
        private int scrollTop = 0;
        private int scrollX = 0;                 // horizontal pan offset (px)
        private const int CONTENT_W = 730;       // total width of all columns
        private const int HSB_H = 16;            // horizontal scrollbar height
        private string status = "Fetching relay list...";
        private bool loading = true;

        private static readonly Color Fab     = Color.FromArgb(0xFA, 0xFA, 0xFB);
        private static readonly Color LblDark = Color.FromArgb(0x55, 0x55, 0x55);

        private Panel canvas, topBar, rightPanel;
        private Panel list;                       // relay library (black, custom-painted)
        private TextBox console;                  // activity log (black, STDOUT-style)
        private TextBox filterBox;
        private ComboBox sectionBox;
        private readonly Dictionary<string, Panel> sections = new Dictionary<string, Panel>();
        private TextBox infoBox;                  // "Server info" section
        private ListBox custList;                 // "Custom configs" section (.ovpn files)
        private Label connLabel;                  // "Connection" section status
        private Label footStatus;
        private Font headFont, rowFont;
        private const int ROW_H = 18, HEAD_H = 22, SB_W = 16, ARROW_H = 16;
        private const int TB = 30, RW = 304, MinCW = 760, MinCH = 440;

        private List<string> custFiles = new List<string>();   // full paths backing custList
        private System.Windows.Forms.Timer logTimer;
        private long logPos = 0;

        protected override bool FooterActsAsResizeGrip { get { return false; } }

        public VpnGateWindow() : base("OPT-VPN", "VPN Gate")
        {
            Size = new Size(1080, 580);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            KeyPreview = true;
            ClientArea.BackColor = Fab;
            canvas = SetupAlohaScroll(Fab);

            headFont = new Font("Lucida Console", 8.5f, FontStyle.Bold);
            rowFont  = new Font("Consolas", 9f);

            footStatus = new Label
            {
                AutoSize = false, Left = 8, Top = 5, Width = FooterRightLimit - 14, Height = 16,
                Font = new Font("Lucida Console", 8.5f, FontStyle.Bold),
                ForeColor = Color.Black, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft, Text = status
            };
            footStatus.Click += (s, e) => { if (client.Connected) DoDisconnect(); };
            Footer.Controls.Add(footStatus);
            footStatus.BringToFront();
            HideSizeReadout();   // VPN Gate has its own footer status — avoid the px-readout overlap
            Footer.Resize += (s, e) => footStatus.Width = Math.Max(40, FooterRightLimit - 14);

            // top bar: connect / disconnect / refresh, plus a filter box
            topBar = new Panel { BackColor = Fab };
            var btnConnect    = new Button { Text = "Connect",    Left = 6,   Top = 4, Width = 78, Height = 22, FlatStyle = FlatStyle.System };
            var btnDisconnect = new Button { Text = "Disconnect", Left = 88,  Top = 4, Width = 86, Height = 22, FlatStyle = FlatStyle.System };
            var btnRefresh    = new Button { Text = "Refresh",    Left = 178, Top = 4, Width = 72, Height = 22, FlatStyle = FlatStyle.System };
            var lblFilter     = new Label  { Text = "filter", Left = 262, Top = 8, AutoSize = true, ForeColor = LblDark, BackColor = Color.Transparent };
            filterBox = new TextBox { Left = 300, Top = 5, Width = 210, BorderStyle = BorderStyle.Fixed3D };
            btnConnect.Click    += (s, e) => ConnectTo(Sel());
            btnDisconnect.Click += (s, e) => DoDisconnect();
            btnRefresh.Click    += (s, e) => { StartFetch(); RefreshCustomConfigs(); };
            filterBox.TextChanged += (s, e) => { filterText = filterBox.Text ?? ""; ApplyFilter(); list.Invalidate(); };
            topBar.Controls.AddRange(new Control[] { btnConnect, btnDisconnect, btnRefresh, lblFilter, filterBox });

            // right panel: a section dropdown over swappable info sub-panels
            rightPanel = new Panel { BackColor = Fab };
            BuildRightPanel();

            // relay library: black, custom-painted (kept from the original)
            list = new Panel { BackColor = Color.Black, BorderStyle = BorderStyle.Fixed3D };
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(list, true, null);
            list.Paint += List_Paint;
            list.MouseDown += List_MouseDown;
            list.MouseWheel += (s, e) => ScrollBy(e.Delta > 0 ? -3 : 3);

            // activity console: black STDOUT-style readout. OpenVPN runs elevated in its
            // own console window, so this shows Aloha's own connect/fetch activity rather
            // than the raw tunnel log.
            console = new TextBox
            {
                Multiline = true, ReadOnly = true, WordWrap = false,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.Fixed3D,
                BackColor = Color.Black, ForeColor = Green, Font = new Font("Consolas", 8.5f)
            };

            canvas.Controls.Add(topBar);
            canvas.Controls.Add(rightPanel);
            canvas.Controls.Add(list);
            canvas.Controls.Add(console);
            list.BringToFront();
            console.BringToFront();

            logTimer = new System.Windows.Forms.Timer { Interval = 700 };
            logTimer.Tick += PollLog;
            logTimer.Start();

            this.KeyDown += VpnGateWindow_KeyDown;
            canvas.Resize += (s, e) => LayoutVpn();
            this.Shown += (s, e) => { LayoutVpn(); StartFetch(); RefreshCustomConfigs(); Log("VPN Gate — fetching relay list…"); };
            this.Activated += (s, e) => RefreshCustomConfigs();   // pick up newly dropped .ovpn files
            this.FormClosed += (s, e) =>
            {
                try { logTimer?.Stop(); logTimer?.Dispose(); } catch { }
                headFont?.Dispose(); rowFont?.Dispose();
                // tunnel intentionally persists after close; Disconnect is explicit
            };
        }

        // ---- right-panel helpers (ADVOR style) ----
        private Label L(Panel p, string t, int y) { var l = new Label { Text = t, Left = 8, Top = y, AutoSize = true, ForeColor = LblDark, BackColor = Color.Transparent }; p.Controls.Add(l); return l; }
        private TextBox Box(Panel p, int y, int h) { var b = new TextBox { Left = 8, Top = y, Width = RW - 16, Height = h, Multiline = h > 24, ReadOnly = true, ScrollBars = h > 24 ? ScrollBars.Vertical : ScrollBars.None, BorderStyle = BorderStyle.Fixed3D, Font = new Font("Consolas", 8.5f) }; p.Controls.Add(b); return b; }
        private Button Btn(Panel p, string t, int x, int y, int w, Action a) { var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = 22, FlatStyle = FlatStyle.System }; b.Click += (s, e) => a(); p.Controls.Add(b); return b; }

        private void BuildRightPanel()
        {
            sectionBox = new ComboBox { Left = 8, Top = 6, Width = RW - 16, DropDownStyle = ComboBoxStyle.DropDownList };
            sectionBox.Items.AddRange(new object[] { "Server info", "Custom configs", "Connection" });
            sectionBox.SelectedIndexChanged += (s, e) => ShowSection((string)sectionBox.SelectedItem);
            rightPanel.Controls.Add(sectionBox);

            sections["Server info"]    = BuildServerInfo();
            sections["Custom configs"] = BuildCustom();
            sections["Connection"]     = BuildConnection();
            foreach (var kv in sections) { kv.Value.Visible = false; rightPanel.Controls.Add(kv.Value); }

            sectionBox.SelectedIndex = 0;   // Server info
        }

        private void ShowSection(string name)
        {
            foreach (var kv in sections) kv.Value.Visible = (kv.Key == name);
        }

        private Panel NewSection() { return new Panel { BackColor = Fab, Left = 0, Top = 34, Width = RW, Height = 500,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right }; }

        private Panel BuildServerInfo()
        {
            var p = NewSection();
            L(p, "selected relay", 4);
            infoBox = Box(p, 26, 372);
            infoBox.WordWrap = true;
            infoBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            int by = 406;
            var b1 = Btn(p, "Connect", 8, by, 96, () => ConnectTo(Sel())); b1.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var b2 = Btn(p, "Details", 110, by, 96, () => OpenDetail(Sel())); b2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            return p;
        }

        private Panel BuildCustom()
        {
            var p = NewSection();
            L(p, "custom .ovpn · openvpn\\config", 4);
            custList = new ListBox
            {
                Left = 8, Top = 26, Width = RW - 16, Height = 340,
                BorderStyle = BorderStyle.Fixed3D, Font = new Font("Consolas", 8.5f), IntegralHeight = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            custList.DoubleClick += (s, e) => ConnectSelectedCustom();
            p.Controls.Add(custList);
            int by = 374;
            var b1 = Btn(p, "Connect", 8, by, 96, ConnectSelectedCustom); b1.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var b2 = Btn(p, "Refresh", 110, by, 96, RefreshCustomConfigs); b2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var hint = new Label
            {
                Left = 8, Top = by + 28, Width = RW - 16, Height = 40, AutoSize = false,
                ForeColor = LblDark, BackColor = Color.Transparent, Font = new Font("Tahoma", 7.75f),
                Text = "Drop .ovpn files in openvpn\\config or config-auto, then Refresh. Double-click to connect.",
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            p.Controls.Add(hint);
            return p;
        }

        private Panel BuildConnection()
        {
            var p = NewSection();
            L(p, "tunnel", 4);
            connLabel = new Label
            {
                Left = 8, Top = 26, Width = RW - 16, Height = 56, AutoSize = false,
                ForeColor = LblDark, BackColor = Color.Transparent, Font = new Font("Consolas", 8.5f),
                Text = "not connected"
            };
            p.Controls.Add(connLabel);
            Btn(p, "Disconnect", 8, 88, 110, () => DoDisconnect());
            var note = new Label
            {
                Left = 8, Top = 124, Width = RW - 16, Height = 84, AutoSize = false,
                ForeColor = LblDark, BackColor = Color.Transparent, Font = new Font("Tahoma", 7.75f),
                Text = "VPN Gate relays are volunteer-run; treat the exit as untrusted. The live tunnel log appears in OpenVPN's own (elevated) console window, not here."
            };
            p.Controls.Add(note);
            return p;
        }

        private void ConnectSelectedCustom()
        {
            int i = custList != null ? custList.SelectedIndex : -1;
            if (i < 0 || i >= custFiles.Count) { Log("no custom config selected"); return; }
            ConnectCustom(custFiles[i]);
        }

        private void UpdateServerInfo()
        {
            if (infoBox == null) return;
            var s = Sel();
            if (s == null) { infoBox.Text = "(no relay selected)"; return; }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Host     : " + (s.Host ?? ""));
            sb.AppendLine("IP       : " + (s.Ip ?? ""));
            sb.AppendLine("Region   : " + (s.Country ?? "") + " (" + (s.CountryShort ?? "") + ")");
            sb.AppendLine("Proto    : " + (s.Proto ?? "?").ToUpperInvariant() + "  port " + s.Port);
            sb.AppendLine("Score    : " + s.Score);
            sb.AppendLine("Ping     : " + s.Ping + " ms");
            sb.AppendLine("Speed    : " + s.SpeedMbps);
            sb.AppendLine("Sessions : " + s.Sessions);
            sb.AppendLine("Uptime   : " + s.UptimeText);
            sb.AppendLine("Users    : " + s.TotalUsers);
            sb.AppendLine("Traffic  : " + s.TrafficText);
            sb.AppendLine("Log type : " + (s.LogType ?? ""));
            sb.AppendLine("Operator : " + (s.Operator ?? ""));
            if (!string.IsNullOrEmpty(s.Message)) { sb.AppendLine(); sb.AppendLine("Message:"); sb.AppendLine(s.Message); }
            infoBox.Text = sb.ToString();
            infoBox.SelectionStart = 0; infoBox.SelectionLength = 0;
        }

        private void UpdateConnLabel()
        {
            if (connLabel == null) return;
            connLabel.Text = client.Connected
                ? "connected\r\nhost: " + (client.ActiveHost ?? "?")
                : "not connected";
        }

        private void Log(string line)
        {
            Action a = () =>
            {
                if (console == null || console.IsDisposed) return;
                console.AppendText(line + "\r\n");
            };
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        // tail OpenVPN's --log file into the console. OpenVPN's own elevated window may
        // close early, but the file persists, so the real tunnel log still lands here.
        private void PollLog(object sender, EventArgs e)
        {
            string path = client.LogPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                    System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    if (fs.Length < logPos) logPos = 0;          // truncated on a fresh connect
                    if (fs.Length == logPos) return;
                    fs.Seek(logPos, System.IO.SeekOrigin.Begin);
                    using (var sr = new System.IO.StreamReader(fs))
                    {
                        string chunk = sr.ReadToEnd();
                        logPos = fs.Length;
                        if (!string.IsNullOrEmpty(chunk) && console != null && !console.IsDisposed)
                            console.AppendText(chunk.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    }
                }
            }
            catch { /* not created yet, or briefly locked — retry next tick */ }
        }

        private void LayoutVpn()
        {
            if (canvas == null || list == null || console == null) return;
            int cw = Math.Max(AlohaContent.Width,  MinCW);
            int ch = Math.Max(AlohaContent.Height, MinCH);
            topBar.SetBounds(0, 0, cw, TB);
            rightPanel.SetBounds(cw - RW, TB, RW, ch - TB);

            int leftW = Math.Max(160, cw - RW);
            int leftH = ch - TB;
            const int gap = 32;                                  // 32px Fab gap above the console
            int consoleH = Math.Min(200, Math.Max(90, leftH / 4));
            int relayH = Math.Max(120, leftH - consoleH - gap);
            consoleH = leftH - relayH - gap;                     // fill the remainder exactly
            list.SetBounds(0, TB, leftW, relayH);
            console.SetBounds(0, TB + relayH + gap, leftW, consoleH);
            list.Invalidate();
        }

        private void SetStatus(string s)
        {
            status = s;
            if (footStatus != null) { footStatus.Text = s; footStatus.Invalidate(); }
        }

        private void StartFetch()
        {
            loading = true; SetStatus("Fetching relay list..."); list.Invalidate();
            var t = new Thread(() =>
            {
                string err;
                var rows = client.FetchServers(out err);
                // marshal back to the UI thread only if the window handle is live;
                // the fetch can finish before/after the handle exists or is gone.
                Action apply = () =>
                {
                    if (err != null) { SetStatus("Fetch failed: " + err); Log("fetch failed: " + err); loading = false; list.Invalidate(); return; }
                    rows.Sort((a, b) => b.SpeedBps.CompareTo(a.SpeedBps));
                    servers = rows;
                    loading = false;
                    ApplyFilter();   // builds view + status, resets selection
                    Log(servers.Count + " relays loaded");
                    list.Invalidate();
                };
                try
                {
                    if (this.IsHandleCreated && !this.IsDisposed)
                        this.BeginInvoke(apply);
                }
                catch { /* window closed mid-fetch — nothing to update */ }
            });
            t.IsBackground = true;
            t.Start();
        }

        // rebuild the shown 'view' from 'servers' using the current filter text.
        // filter matches host, ip, country, or proto (case-insensitive substring).
        private void ApplyFilter()
        {
            if (string.IsNullOrEmpty(filterText))
                view = new List<VpnGateClient.Server>(servers);
            else
            {
                string f = filterText.ToLowerInvariant();
                view = servers.FindAll(s =>
                    (s.Host ?? "").ToLowerInvariant().Contains(f) ||
                    (s.Ip ?? "").ToLowerInvariant().Contains(f) ||
                    (s.Country ?? "").ToLowerInvariant().Contains(f) ||
                    (s.CountryShort ?? "").ToLowerInvariant().Contains(f) ||
                    (s.Proto ?? "").ToLowerInvariant().Contains(f));
            }
            selIdx = 0; scrollTop = 0;
            string flt = string.IsNullOrEmpty(filterText) ? "" : " (filter: " + filterText + ")";
            SetStatus(view.Count + " relays" + flt + " \u00B7 volunteer-run, treat as untrusted exit");
            UpdateServerInfo();
        }

        private int VisibleRows { get { return Math.Max(1, (list.ClientSize.Height - HEAD_H - HSB_H) / ROW_H); } }
        private int MaxScroll   { get { return Math.Max(0, view.Count - VisibleRows); } }

        private void ScrollBy(int d)
        {
            scrollTop = Math.Max(0, Math.Min(MaxScroll, scrollTop + d));
            list.Invalidate();
        }

        private void EnsureVisible()
        {
            if (selIdx < scrollTop) scrollTop = selIdx;
            else if (selIdx >= scrollTop + VisibleRows) scrollTop = selIdx - VisibleRows + 1;
        }

        private void VpnGateWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (loading || view.Count == 0) return;
            if (e.KeyCode == Keys.Up)        { selIdx = Math.Max(0, selIdx - 1); EnsureVisible(); UpdateServerInfo(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { selIdx = Math.Min(view.Count - 1, selIdx + 1); EnsureVisible(); UpdateServerInfo(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { selIdx = Math.Min(view.Count - 1, selIdx + VisibleRows); EnsureVisible(); UpdateServerInfo(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp)   { selIdx = Math.Max(0, selIdx - VisibleRows); EnsureVisible(); UpdateServerInfo(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter)  { ConnectTo(Sel()); e.Handled = true; }
            else if (e.KeyCode == Keys.Space)  { OpenDetail(Sel()); e.Handled = true; }
        }

        private void OpenDetail(VpnGateClient.Server s)
        {
            if (s == null) return;
            var d = new VpnDetailWindow(s);
            d.OnConnect += ConnectTo;   // detail's Connect routes through our shared client
            d.Show();
            d.BringToFront();
        }


        private VpnGateClient.Server Sel()
        {
            return (selIdx >= 0 && selIdx < view.Count) ? view[selIdx] : null;
        }

        private void ConnectTo(VpnGateClient.Server s)
        {
            if (s == null) return;
            SetStatus("Connecting to " + s.Host + " (" + s.Country + ")...");
            Log("connect \u2192 " + s.Host + " \u00b7 " + s.Country + " \u00b7 " + (s.Proto ?? "?").ToUpperInvariant() + " " + s.Port);
            string err = client.Connect(s);
            string msg = err == null
                ? "Connected: " + s.Host + " \u00B7 " + s.Country + " \u00B7 " + s.Proto + " " + s.Port + " \u00B7 click to disconnect"
                : "Connect failed: " + err;
            SetStatus(msg);
            Log(err == null ? "  ok \u2014 tunnel up (live log in OpenVPN's window)" : "  FAILED: " + err);
            UpdateConnLabel();
        }

        private void DoDisconnect()
        {
            string h = client.ActiveHost;
            client.Disconnect();
            SetStatus("Disconnected" + (h != null ? " from " + h : ""));
            Log("disconnect" + (h != null ? " \u2014 " + h : ""));
            UpdateConnLabel();
        }

        // ---- list painting ----
        private void List_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = list.ClientSize.Width, h = list.ClientSize.Height;
            int listW = w - SB_W;          // vertical scrollbar gutter on the right
            int listH = h - HSB_H;         // horizontal scrollbar strip at the bottom
            int ox = -scrollX;             // horizontal pan offset

            int cHost = 8, cIp = 230, cCty = 380, cSess = 480, cSpd = 545, cPing = 625, cProto = 675;

            // white header band, black text — matches the Instruction Map
            using (var wb = new SolidBrush(Color.White)) g.FillRectangle(wb, 0, 0, w, HEAD_H);
            g.DrawString("DDNS Hostname", headFont, Brushes.Black, ox + cHost, 4);
            g.DrawString("IP Address",    headFont, Brushes.Black, ox + cIp, 4);
            g.DrawString("Region",        headFont, Brushes.Black, ox + cCty, 4);
            g.DrawString("Sess",          headFont, Brushes.Black, ox + cSess, 4);
            g.DrawString("Speed",         headFont, Brushes.Black, ox + cSpd, 4);
            g.DrawString("Ping",          headFont, Brushes.Black, ox + cPing, 4);
            g.DrawString("Proto",         headFont, Brushes.Black, ox + cProto, 4);
            // re-cover the right gutter so panned header text doesn't bleed under the v-scrollbar
            using (var wb2 = new SolidBrush(Color.White)) g.FillRectangle(wb2, listW, 0, SB_W, HEAD_H);
            using (var sh = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0))) g.DrawLine(sh, 0, HEAD_H - 1, w, HEAD_H - 1);

            if (loading)
            {
                using (var gb = new SolidBrush(Green)) g.DrawString("fetching...", rowFont, gb, cHost, HEAD_H + 6);
                DrawScrollbar(g, w, h);
                DrawHScrollbar(g, w, h);
                return;
            }

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, HEAD_H, listW, listH - HEAD_H));   // keep rows out of the gutters
            int end = Math.Min(view.Count, scrollTop + VisibleRows);
            for (int i = scrollTop; i < end; i++)
            {
                var s = view[i];
                int y = HEAD_H + (i - scrollTop) * ROW_H;
                bool seld = (i == selIdx);
                if (seld) using (var sb = new SolidBrush(Green)) g.FillRectangle(sb, 0, y, listW, ROW_H);
                Brush fg = seld ? Brushes.Black : new SolidBrush(Green);
                Brush pf = seld ? Brushes.Black : new SolidBrush(s.Proto == "udp" ? Green : Dim);

                g.DrawString(Clip(s.Host, 28),     rowFont, fg, ox + cHost, y + 2);
                g.DrawString(Clip(s.Ip, 18),       rowFont, fg, ox + cIp, y + 2);
                g.DrawString(Clip(s.Country, 13),  rowFont, fg, ox + cCty, y + 2);
                g.DrawString(s.Sessions.ToString(),rowFont, fg, ox + cSess, y + 2);
                g.DrawString(s.SpeedMbps,          rowFont, fg, ox + cSpd, y + 2);
                g.DrawString(s.Ping.ToString(),    rowFont, fg, ox + cPing, y + 2);
                g.DrawString(s.Proto.ToUpperInvariant(), rowFont, pf, ox + cProto, y + 2);

                if (!seld) { fg.Dispose(); pf.Dispose(); }
            }
            g.Clip = clip;

            DrawScrollbar(g, w, h);

            DrawScrollbar(g, w, h);
            DrawHScrollbar(g, w, h);
        }

        // Win9x-styled vertical scrollbar (stops above the horizontal bar)
        private Rectangle upRect, downRect, thumbRect;
        private void DrawScrollbar(Graphics g, int w, int h)
        {
            int x = w - SB_W;
            int top = HEAD_H, bot = h - HSB_H;
            upRect   = new Rectangle(x, top, SB_W, ARROW_H);
            downRect = new Rectangle(x, bot - ARROW_H, SB_W, ARROW_H);

            using (var track = new SolidBrush(Color.FromArgb(0xF4, 0xF4, 0xF4)))
                g.FillRectangle(track, x, top, SB_W, bot - top);

            // proportional thumb (drawn first, between the arrow buttons)
            int trackH = (bot - ARROW_H) - (top + ARROW_H);
            if (view.Count > 0 && MaxScroll > 0 && trackH > 8)
            {
                double frac = (double)VisibleRows / view.Count;
                int thumbH = Math.Max(14, (int)(trackH * frac));
                int range = trackH - thumbH;
                int ty = top + ARROW_H + (int)((double)scrollTop / MaxScroll * range);
                thumbRect = new Rectangle(x + 1, ty, SB_W - 2, thumbH);
                DrawBevelButton(g, thumbRect);
            }
            else thumbRect = Rectangle.Empty;

            // arrow buttons LAST so they always sit on top of the track/thumb
            DrawBevelButton(g, upRect);
            DrawBevelButton(g, downRect);
            DrawArrow(g, upRect, true);
            DrawArrow(g, downRect, false);
        }

        // Win9x-styled horizontal scrollbar along the bottom of the list
        private Rectangle leftRect, rightRect, hThumbRect;
        private int MaxScrollX { get { return Math.Max(0, CONTENT_W - (list.ClientSize.Width - SB_W)); } }
        private void DrawHScrollbar(Graphics g, int w, int h)
        {
            int y = h - HSB_H;
            int left = 0, right = w - SB_W;     // span under the list, not the v-gutter
            leftRect  = new Rectangle(left, y, ARROW_H, HSB_H);
            rightRect = new Rectangle(right - ARROW_H, y, ARROW_H, HSB_H);

            using (var track = new SolidBrush(Color.FromArgb(0xF4, 0xF4, 0xF4)))
                g.FillRectangle(track, left, y, right - left, HSB_H);

            int trackW = (right - ARROW_H) - (left + ARROW_H);
            int maxX = MaxScrollX;
            if (maxX > 0 && trackW > 8)
            {
                double frac = (double)(list.ClientSize.Width - SB_W) / CONTENT_W;
                int thumbW = Math.Max(18, (int)(trackW * frac));
                int range = trackW - thumbW;
                int tx = left + ARROW_H + (int)((double)scrollX / maxX * range);
                hThumbRect = new Rectangle(tx, y + 1, thumbW, HSB_H - 2);
                DrawBevelButton(g, hThumbRect);
            }
            else hThumbRect = Rectangle.Empty;

            DrawBevelButton(g, leftRect);
            DrawBevelButton(g, rightRect);
            DrawHArrow(g, leftRect, true);
            DrawHArrow(g, rightRect, false);

            // small corner square where the two scrollbars meet (modern light)
            using (var c = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0)))
                g.FillRectangle(c, w - SB_W, y, SB_W, HSB_H);
        }

        private void DrawHArrow(Graphics g, Rectangle r, bool leftward)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var p = new Pen(Color.FromArgb(0x50, 0x50, 0x50), 1.4f))
            {
                if (leftward)
                    g.DrawLines(p, new[] { new Point(cx + 2, cy - 4), new Point(cx - 3, cy), new Point(cx + 2, cy + 4) });
                else
                    g.DrawLines(p, new[] { new Point(cx - 2, cy - 4), new Point(cx + 3, cy), new Point(cx - 2, cy + 4) });
            }
        }

        private void ScrollXBy(int d)
        {
            scrollX = Math.Max(0, Math.Min(MaxScrollX, scrollX + d));
            list.Invalidate();
        }

        private void DrawBevelButton(Graphics g, Rectangle r)
        {
            using (var f = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0))) g.FillRectangle(f, r);
            using (var hi = new Pen(Color.White))
            { g.DrawLine(hi, r.Left, r.Top, r.Right - 1, r.Top); g.DrawLine(hi, r.Left, r.Top, r.Left, r.Bottom - 1); }
            using (var sh = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0)))
            { g.DrawLine(sh, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1); g.DrawLine(sh, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1); }
            using (var sh2 = new Pen(Color.FromArgb(0xC8, 0xC8, 0xC8)))
            { g.DrawLine(sh2, r.Left + 1, r.Bottom - 2, r.Right - 2, r.Bottom - 2); }
        }

        private void DrawArrow(Graphics g, Rectangle r, bool up)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var p = new Pen(Color.FromArgb(0x50, 0x50, 0x50), 1.4f))
            {
                if (up)
                    g.DrawLines(p, new[] { new Point(cx - 4, cy + 2), new Point(cx, cy - 3), new Point(cx + 4, cy + 2) });
                else
                    g.DrawLines(p, new[] { new Point(cx - 4, cy - 3), new Point(cx, cy + 2), new Point(cx + 4, cy - 3) });
            }
        }


        // ---- mouse ----
        private void List_MouseDown(object sender, MouseEventArgs e)
        {
            // horizontal scrollbar hits (bottom strip)
            if (leftRect.Contains(e.Location))  { ScrollXBy(-40); return; }
            if (rightRect.Contains(e.Location)) { ScrollXBy(40); return; }
            if (e.Y >= list.ClientSize.Height - HSB_H)
            {
                if (!hThumbRect.IsEmpty)
                    ScrollXBy(e.X < hThumbRect.Left ? -120 : 120);
                return;
            }

            // vertical scrollbar hits
            if (upRect.Contains(e.Location))   { ScrollBy(-1); return; }
            if (downRect.Contains(e.Location)) { ScrollBy(1); return; }
            if (e.X >= list.ClientSize.Width - SB_W)
            {
                if (!thumbRect.IsEmpty)
                    ScrollBy(e.Y < thumbRect.Top ? -VisibleRows : VisibleRows);
                return;
            }

            if (loading || view.Count == 0 || e.Y < HEAD_H || e.Y >= list.ClientSize.Height - HSB_H) return;
            int idx = scrollTop + (e.Y - HEAD_H) / ROW_H;
            if (idx >= 0 && idx < view.Count)
            {
                selIdx = idx; UpdateServerInfo(); list.Invalidate();
                if (e.Clicks >= 2) OpenDetail(view[idx]);   // double-click => detail window
            }
        }

        // ---- custom .ovpn configs (openvpn\config) -> custList ----
        private void RefreshCustomConfigs()
        {
            custFiles = client.ListConfigFiles();
            if (custList == null) return;
            string prev = custList.SelectedItem as string;
            custList.BeginUpdate();
            custList.Items.Clear();
            foreach (var f in custFiles) custList.Items.Add(System.IO.Path.GetFileName(f));
            custList.EndUpdate();
            if (custList.Items.Count > 0)
            {
                int pi = prev != null ? custList.Items.IndexOf(prev) : -1;
                custList.SelectedIndex = pi >= 0 ? pi : 0;
            }
        }

        private void ConnectCustom(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string name = System.IO.Path.GetFileName(path);
            SetStatus("Connecting via " + name + "...");
            Log("connect (custom) \u2192 " + name);
            string err = client.ConnectConfigFile(path);
            string msg = err == null
                ? "Connected via " + name + " \u00b7 system-wide \u00b7 click to disconnect"
                : "Connect failed: " + err;
            SetStatus(msg);
            Log(err == null ? "  ok \u2014 tunnel up" : "  FAILED: " + err);
            UpdateConnLabel();
        }

        private static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "\u2026";
        }
    }
}
