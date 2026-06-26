using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // AdvorWindow — ADVOR (Advanced Onion Router) options for Aloha's bundled
    // Tor. Left: a relay library (every relay in the consensus, by region /
    // flags / bandwidth). Right: a section dropdown swapping between option
    // groups, each applied live over the control port:
    //   Circuit     — ExitNodes / EntryNodes / ExcludeNodes / StrictNodes (the
    //                 relay picker's "+ selected" feeds these)
    //   Bridges     — UseBridges + Bridge lines
    //   Proxy       — outbound Socks5/Socks4/HTTPS proxy
    //   Firewall    — FascistFirewall + ReachableAddresses
    //   Tuning      — circuit timeouts / guards / distinct subnets
    //   Address map — MAPADDRESS remaps
    //
    // Data layer: TorControl.GetAllRelays + GeoIp (bundled geoip, no network).
    // ============================================================
    public class AdvorWindow : DafyFrame
    {
        private static readonly Color Green   = Color.FromArgb(0x33, 0xFF, 0x66);
        private static readonly Color Dim     = Color.FromArgb(0x2C, 0x8A, 0x4A);
        private static readonly Color Fab     = Color.FromArgb(0xFA, 0xFA, 0xFB);
        private static readonly Color LblDark = Color.FromArgb(0x55, 0x55, 0x55);

        private TorControl ctrl;
        private readonly GeoIp geo = new GeoIp();
        private List<TorControl.Relay> relays = new List<TorControl.Relay>();
        private List<TorControl.Relay> view  = new List<TorControl.Relay>();
        private string filterText = "";
        private int selIdx = 0, scrollTop = 0;
        private bool loading = false;
        private string status = "not connected";

        private Panel topBar, rightPanel, list;
        private TextBox portInput, filterBox;
        private Label footStatus;
        private Font headFont, rowFont;

        // section sub-panels (only one visible)
        private ComboBox sectionBox;
        private readonly Dictionary<string, Panel> sections = new Dictionary<string, Panel>();

        // Circuit
        private TextBox exitBox, entryBox, excludeBox;
        private CheckBox strictCheck;
        // Bridges
        private TextBox bridgesBox; private CheckBox useBridgesCheck;
        // Proxy
        private ComboBox proxyType; private TextBox proxyHostBox;
        // Firewall
        private CheckBox fascistCheck; private TextBox reachBox;
        // Tuning
        private TextBox cbtBox, ncpBox, mcdBox; private CheckBox distinctCheck, guardsCheck;
        // Address map
        private TextBox mapFromBox, mapToBox;
        // Server (relay)
        private TextBox orPortBox, nickBox, contactBox, relayRateBox, relayBurstBox;
        private CheckBox allowExitCheck;
        private System.Windows.Forms.Timer trafficTimer;
        private Label trafficReadout; private CheckBox logTrafficCheck;
        private long tRead, tWritten; private DateTime tStamp = DateTime.MinValue;

        // circuits inset (below the relay library): the live circuits this Tor has built,
        // each hop matched to the consensus and drawn in the same node-list style. Click a
        // hop -> the relay detail window. Refreshed off the same timer as relay traffic.
        private Panel circuitPanel;
        private List<TorControl.Circuit> circuits = new List<TorControl.Circuit>();
        private Dictionary<string, TorControl.Relay> fpMap;   // fingerprint -> consensus relay
        private int circuitScrollTop = 0;
        private Rectangle cUpRect, cDownRect, cThumbRect;
        private class CircRow
        {
            public bool Header;                 // true = circuit heading bar; false = a hop
            public string Text;                 // heading text (header rows)
            public Color HeadColor;             // heading colour (header rows)
            public TorControl.Relay Relay;      // resolved consensus relay (hop rows), or null
            public string Nick;                 // fallback nickname when Relay is null
        }
        private readonly List<CircRow> circRows = new List<CircRow>();

        private const int TB = 30, RW = 304;
        private const int ROW_H = 18, HEAD_H = 22, SB_W = 16, ARROW_H = 16;

        protected override bool FooterActsAsResizeGrip { get { return false; } }
        private Panel canvas;

        public AdvorWindow() : base("OPT-ADVOR", "ADVOR \u2014 Advanced Onion Router")
        {
            Size = new Size(960, 610);
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
            Footer.Controls.Add(footStatus);
            footStatus.BringToFront();
            HideSizeReadout();   // ADVOR has its own footer status — avoid the px-readout overlap
            Footer.Resize += (s, e) => footStatus.Width = Math.Max(40, FooterRightLimit - 14);

            topBar = new Panel { BackColor = Fab };
            var lblCtl = new Label { Text = "control", Left = 6, Top = 8, AutoSize = true, ForeColor = LblDark, BackColor = Color.Transparent };
            portInput = new TextBox { Left = 58, Top = 5, Width = 56, Text = "9051", BorderStyle = BorderStyle.Fixed3D };
            var btnConnect = new Button { Text = "Connect", Left = 120, Top = 4, Width = 72, Height = 22, FlatStyle = FlatStyle.System };
            var btnRefresh = new Button { Text = "Refresh", Left = 196, Top = 4, Width = 72, Height = 22, FlatStyle = FlatStyle.System };
            var lblFilter = new Label { Text = "filter", Left = 284, Top = 8, AutoSize = true, ForeColor = LblDark, BackColor = Color.Transparent };
            filterBox = new TextBox { Left = 322, Top = 5, Width = 220, BorderStyle = BorderStyle.Fixed3D };
            btnConnect.Click += (s, e) => Connect();
            btnRefresh.Click += (s, e) => { LoadRelays(); RefreshCircuits(); };
            filterBox.TextChanged += (s, e) => { filterText = filterBox.Text ?? ""; ApplyFilter(); list.Invalidate(); };
            topBar.Controls.AddRange(new Control[] { lblCtl, portInput, btnConnect, btnRefresh, lblFilter, filterBox });

            rightPanel = new Panel { BackColor = Fab };
            BuildRightPanel();

            list = new Panel { BackColor = Color.Black, BorderStyle = BorderStyle.Fixed3D };
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(list, true, null);
            list.Paint += List_Paint;
            list.MouseDown += List_MouseDown;
            list.MouseWheel += (s, e) => ScrollBy(e.Delta > 0 ? -3 : 3);

            circuitPanel = new Panel { BackColor = Color.Black, BorderStyle = BorderStyle.Fixed3D };
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(circuitPanel, true, null);
            circuitPanel.Paint += Circuits_Paint;
            circuitPanel.MouseDown += Circuits_MouseDown;
            circuitPanel.MouseWheel += (s, e) => CircuitScrollBy(e.Delta > 0 ? -2 : 2);

            canvas.Controls.Add(topBar);
            canvas.Controls.Add(rightPanel);
            canvas.Controls.Add(list);
            canvas.Controls.Add(circuitPanel);
            list.BringToFront();
            circuitPanel.BringToFront();

            this.KeyDown += Advor_KeyDown;
            canvas.Resize += (s, e) => LayoutAdvor();
            this.Shown += (s, e) => { LayoutAdvor(); Connect(); };
            trafficTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            trafficTimer.Tick += TrafficTick;
            this.FormClosed += (s, e) => { try { trafficTimer?.Stop(); } catch { } try { ctrl?.Dispose(); } catch { } headFont?.Dispose(); rowFont?.Dispose(); };
        }

        // ---- right panel: a section dropdown over swappable option sub-panels ----
        private Label L(Panel p, string t, int y) { var l = new Label { Text = t, Left = 8, Top = y, AutoSize = true, ForeColor = LblDark, BackColor = Color.Transparent }; p.Controls.Add(l); return l; }
        private TextBox Box(Panel p, int y, int h) { var b = new TextBox { Left = 8, Top = y, Width = RW - 16, Height = h, Multiline = h > 24, ScrollBars = h > 24 ? ScrollBars.Vertical : ScrollBars.None, BorderStyle = BorderStyle.Fixed3D, Font = new Font("Consolas", 8.5f) }; p.Controls.Add(b); return b; }
        private Button Btn(Panel p, string t, int x, int y, int w, Action a) { var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = 22, FlatStyle = FlatStyle.System }; b.Click += (s, e) => a(); p.Controls.Add(b); return b; }
        private CheckBox Chk(Panel p, string t, int y, bool on = false) { var c = new CheckBox { Text = t, Left = 8, Top = y, AutoSize = true, Checked = on, ForeColor = LblDark, BackColor = Color.Transparent }; p.Controls.Add(c); return c; }
        private Label Hint(Panel p, int y, string t) { var l = new Label { Left = 8, Top = y, Width = RW - 16, Height = 96, AutoSize = false, ForeColor = LblDark, BackColor = Color.Transparent, Font = new Font("Tahoma", 7.75f), Text = t }; p.Controls.Add(l); return l; }

        private void BuildRightPanel()
        {
            sectionBox = new ComboBox { Left = 8, Top = 6, Width = RW - 16, DropDownStyle = ComboBoxStyle.DropDownList };
            sectionBox.Items.AddRange(new object[] { "Circuit", "Bridges", "Proxy", "Firewall", "Tuning", "Address map", "Server (relay)" });
            sectionBox.SelectedIndexChanged += (s, e) => ShowSection((string)sectionBox.SelectedItem);
            rightPanel.Controls.Add(sectionBox);

            sections["Circuit"]     = BuildCircuit();
            sections["Bridges"]     = BuildBridges();
            sections["Proxy"]       = BuildProxy();
            sections["Firewall"]    = BuildFirewall();
            sections["Tuning"]      = BuildTuning();
            sections["Address map"] = BuildAddrMap();
            sections["Server (relay)"] = BuildServer();
            foreach (var kv in sections) { kv.Value.Visible = false; rightPanel.Controls.Add(kv.Value); }

            sectionBox.SelectedIndex = 0;   // Circuit
        }

        private void ShowSection(string name)
        {
            foreach (var kv in sections) kv.Value.Visible = (kv.Key == name);
        }

        private Panel NewSection() { return new Panel { BackColor = Fab, Left = 0, Top = 34, Width = RW, Height = 480,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right }; }

        private Panel BuildCircuit()
        {
            var p = NewSection();
            L(p, "Exit nodes", 4);     Btn(p, "+ sel", RW - 70, 2, 60, () => AddSelectedTo(exitBox));    exitBox    = Box(p, 22, 38);
            L(p, "Entry nodes", 66);   Btn(p, "+ sel", RW - 70, 64, 60, () => AddSelectedTo(entryBox));  entryBox   = Box(p, 84, 38);
            L(p, "Exclude nodes", 128);Btn(p, "+ sel", RW - 70, 126, 60, () => AddSelectedTo(excludeBox)); excludeBox = Box(p, 146, 38);
            strictCheck = Chk(p, "StrictNodes (obey strictly)", 192);
            Btn(p, "Apply route", 8, 216, 116, ApplyRoute);
            Btn(p, "Clear", 130, 216, 76, ClearRoute);
            Hint(p, 248, "Pick a relay on the left, then \"+ sel\" to add its $fingerprint. You can also type "
                       + "{de}, {us} country codes, one per line. StrictNodes forces Tor to obey \u2014 can break "
                       + "connectivity if the set is too small. Exits are volunteer-run; treat as untrusted.");
            return p;
        }

        private Panel BuildBridges()
        {
            var p = NewSection();
            useBridgesCheck = Chk(p, "UseBridges", 4);
            L(p, "Bridge lines (one per line)", 28);
            bridgesBox = Box(p, 48, 90);
            Btn(p, "Apply bridges", 8, 144, 130, ApplyBridges);
            Hint(p, 176, "Each line is a Bridge address (obfs4/webtunnel/plain). Applied live via SETCONF. "
                       + "Pluggable-transport bridges also need the transport binary bundled and a "
                       + "ClientTransportPlugin line in the torrc \u2014 set that in the Tor window's torrc box.");
            return p;
        }

        private Panel BuildProxy()
        {
            var p = NewSection();
            L(p, "Outbound proxy type", 4);
            proxyType = new ComboBox { Left = 8, Top = 24, Width = RW - 16, DropDownStyle = ComboBoxStyle.DropDownList };
            proxyType.Items.AddRange(new object[] { "Socks5Proxy", "Socks4Proxy", "HTTPSProxy" });
            proxyType.SelectedIndex = 0;
            p.Controls.Add(proxyType);
            L(p, "host:port", 54);
            proxyHostBox = Box(p, 74, 22);
            Btn(p, "Apply proxy", 8, 104, 116, ApplyProxy);
            Btn(p, "Clear", 130, 104, 76, ClearProxy);
            Hint(p, 136, "Routes Tor's own connections out through another proxy first (proxy \u2192 Tor). "
                       + "Leave host:port blank and Clear to remove. Useful behind a corporate proxy.");
            return p;
        }

        private Panel BuildFirewall()
        {
            var p = NewSection();
            fascistCheck = Chk(p, "FascistFirewall (only reach common ports)", 4);
            L(p, "ReachableAddresses (e.g. *:80,*:443)", 28);
            reachBox = Box(p, 48, 22);
            Btn(p, "Apply firewall", 8, 78, 130, ApplyFirewall);
            Hint(p, 110, "For restrictive networks: FascistFirewall limits Tor to ports 80/443. "
                       + "ReachableAddresses is an explicit allowlist of address:port patterns Tor may use. "
                       + "If your network blocks Tor outright, use Bridges instead.");
            return p;
        }

        private Panel BuildTuning()
        {
            var p = NewSection();
            L(p, "CircuitBuildTimeout (s)", 4);   cbtBox = Box(p, 22, 22);
            L(p, "NewCircuitPeriod (s)", 50);     ncpBox = Box(p, 68, 22);
            L(p, "MaxCircuitDirtiness (s)", 96);  mcdBox = Box(p, 114, 22);
            distinctCheck = Chk(p, "EnforceDistinctSubnets", 146, true);
            guardsCheck   = Chk(p, "UseEntryGuards", 170, true);
            Btn(p, "Apply tuning", 8, 196, 130, ApplyTuning);
            Hint(p, 228, "Circuit lifetimes and guard behaviour. Blank numeric fields are left untouched. "
                       + "Disabling UseEntryGuards rotates entry relays more (worse for anonymity). "
                       + "EnforceDistinctSubnets keeps hops in different /16s.");
            return p;
        }

        private Panel BuildAddrMap()
        {
            var p = NewSection();
            L(p, "From (address)", 4);  mapFromBox = Box(p, 22, 22);
            L(p, "To (address)", 50);   mapToBox   = Box(p, 68, 22);
            Btn(p, "Add map", 8, 98, 116, AddAddrMap);
            Hint(p, 130, "MAPADDRESS remaps one address to another inside Tor \u2014 e.g. pin a host to a "
                       + "specific .onion, or alias a name. Takes effect immediately for new streams.");
            return p;
        }

        private Panel BuildServer()
        {
            var p = NewSection();
            L(p, "ORPort (inbound, must be reachable)", 4);  orPortBox    = Box(p, 22, 22);
            L(p, "Nickname", 50);                            nickBox      = Box(p, 68, 22);
            L(p, "ContactInfo", 96);                         contactBox   = Box(p, 114, 22);
            L(p, "RelayBandwidthRate KB/s", 142);            relayRateBox = Box(p, 160, 22);
            L(p, "RelayBandwidthBurst KB/s", 188);           relayBurstBox= Box(p, 206, 22);
            allowExitCheck = Chk(p, "Allow exit traffic (LEGAL EXPOSURE)", 234);
            Btn(p, "Start relay", 8, 260, 116, ApplyServer);
            Btn(p, "Stop relay", 130, 260, 90, StopServer);
            Hint(p, 290, "Donates bandwidth as a Tor relay. ORPort must be reachable from the internet "
                       + "(forward the port — Tor self-tests). Default is a NON-exit relay. Enabling exit "
                       + "means other people's traffic leaves through your IP: serious abuse / legal exposure, "
                       + "leave OFF unless you know what that means. Relays should stay online; a transient "
                       + "browser relay is poor for the network. Not persisted — add to torrc to keep it.");
            trafficReadout = new Label { Left = 8, Top = 392, Width = RW - 16, Height = 18, AutoSize = false, ForeColor = LblDark, BackColor = Color.Transparent, Font = new Font("Consolas", 8.5f), Text = "traffic: idle" };
            p.Controls.Add(trafficReadout);
            logTrafficCheck = Chk(p, "log traffic to tor/tmp", 414, true);
            return p;
        }

        private void ApplyServer()
        {
            if (!Ready()) return;
            string port = (orPortBox.Text ?? "").Trim();
            int pn;
            if (port.Length == 0 || !int.TryParse(port, out pn) || pn <= 0 || pn >= 65536) { SetStatus("enter a valid ORPort"); return; }
            var sets = new List<string> { "ORPort=" + pn };
            string nick = (nickBox.Text ?? "").Trim();
            if (nick.Length > 0) sets.Add("Nickname=\"" + nick + "\"");
            string contact = (contactBox.Text ?? "").Trim();
            if (contact.Length > 0) sets.Add("ContactInfo=\"" + contact + "\"");
            int n;
            if (int.TryParse((relayRateBox.Text ?? "").Trim(), out n) && n > 0)  sets.Add("RelayBandwidthRate=\"" + n + " KBytes\"");
            if (int.TryParse((relayBurstBox.Text ?? "").Trim(), out n) && n > 0) sets.Add("RelayBandwidthBurst=\"" + n + " KBytes\"");
            if (allowExitCheck.Checked) sets.Add("ExitRelay=1");
            else { sets.Add("ExitRelay=0"); sets.Add("ExitPolicy=\"reject *:*\""); }
            string err;
            if (ctrl.SetConf(string.Join(" ", sets), out err))
                { Persist(); SetStatus("relay starting on ORPort " + pn + " + saved — Tor self-tests reachability (watch STDOUT)"); }
            else SetStatus("relay start failed: " + err);
        }

        private void StopServer()
        {
            if (!Ready()) return;
            string err;
            if (ctrl.ResetConf("ORPort RelayBandwidthRate RelayBandwidthBurst ExitRelay", out err)) { Persist(); SetStatus("relay stopped + saved"); }
            else SetStatus("stop failed: " + err);
        }

        private const int MinCW = 740, MinCH = 544;
        private void LayoutAdvor()
        {
            int cw = Math.Max(AlohaContent.Width,  MinCW);
            int ch = Math.Max(AlohaContent.Height, MinCH);
            topBar.SetBounds(0, 0, cw, TB);
            rightPanel.SetBounds(cw - RW, TB, RW, ch - TB);

            // left column = relay library (top) + circuits inset (bottom). Circuits take
            // about a third, clamped, with the library keeping the rest.
            int leftW = Math.Max(120, cw - RW);
            int leftH = ch - TB;
            const int gap = 6;
            int circuitH = Math.Min(300, Math.Max(140, leftH / 3));
            int relayH = Math.Max(100, leftH - circuitH - gap);
            circuitH = leftH - relayH - gap;                 // fill the remainder exactly
            list.SetBounds(0, TB, leftW, relayH);
            circuitPanel.SetBounds(0, TB + relayH + gap, leftW, circuitH);
            list.Invalidate();
            circuitPanel.Invalidate();
        }

        private void SetStatus(string s) { status = s; if (footStatus != null) { footStatus.Text = s; footStatus.Invalidate(); } }
        private bool Ready() { if (ctrl == null || !ctrl.Authenticated) { SetStatus("connect first"); return false; } return true; }
        private void Persist() { if (ctrl == null || !ctrl.Authenticated) return; string e; ctrl.SaveConf(out e); ctrl.NewIdentity(out e); }
        private int ParsePort() { int v; return (int.TryParse((portInput.Text ?? "").Trim(), out v) && v > 0 && v < 65536) ? v : 9051; }

        private static string CookiePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aloha", "tor-data", "control_auth_cookie");
        }

        private void Connect()
        {
            try { ctrl?.Dispose(); } catch { }
            ctrl = null;
            string cookie = CookiePath();
            if (!File.Exists(cookie)) { SetStatus("Tor not running? control cookie not found \u2014 start Tor first"); return; }
            ctrl = new TorControl(cookie, ParsePort());
            string err;
            if (!ctrl.Connect(out err)) { SetStatus("control connect failed: " + err); ctrl = null; return; }
            SetStatus("connected \u2014 loading relays...");
            LoadRelays();
            RefreshCircuits();
            tStamp = DateTime.MinValue;
            trafficTimer?.Start();
        }

        private void LoadRelays()
        {
            if (!Ready()) return;
            loading = true; list.Invalidate();
            var t = new Thread(() =>
            {
                string err;
                var rows = ctrl.GetAllRelays(out err);
                if (err == null) foreach (var r in rows) r.Country = geo.Lookup(r.Ip);
                Action apply = () =>
                {
                    loading = false;
                    if (err != null) { SetStatus("relay fetch failed: " + err); list.Invalidate(); return; }
                    rows.Sort((a, b) => b.BandwidthKb.CompareTo(a.BandwidthKb));
                    relays = rows;
                    fpMap = new Dictionary<string, TorControl.Relay>();
                    foreach (var rr in relays)
                        if (!string.IsNullOrEmpty(rr.Fingerprint)) fpMap[rr.Fingerprint.ToUpperInvariant()] = rr;
                    ApplyFilter();
                    RebuildCircuitRows();          // resolve hop fingerprints now the consensus is here
                    list.Invalidate();
                    if (circuitPanel != null) circuitPanel.Invalidate();
                };
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(apply); } catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrEmpty(filterText)) view = new List<TorControl.Relay>(relays);
            else
            {
                string f = filterText.ToLowerInvariant();
                view = relays.FindAll(r =>
                    (r.Nick ?? "").ToLowerInvariant().Contains(f) ||
                    (r.Country ?? "").ToLowerInvariant().Contains(f) ||
                    (r.Fingerprint ?? "").ToLowerInvariant().Contains(f) ||
                    FlagStr(r).ToLowerInvariant().Contains(f));
            }
            selIdx = 0; scrollTop = 0;
            string flt = string.IsNullOrEmpty(filterText) ? "" : " (filter: " + filterText + ")";
            SetStatus(view.Count + " relays" + flt + (geo.Loaded ? "" : "  [geoip not loaded: regions show ??]"));
        }

        private static string FlagStr(TorControl.Relay r)
        {
            var sb = new StringBuilder();
            sb.Append(r.IsExit ? 'E' : '-'); sb.Append(r.IsGuard ? 'G' : '-');
            sb.Append(r.IsFast ? 'F' : '-'); sb.Append(r.IsStable ? 'S' : '-');
            return sb.ToString();
        }

        private TorControl.Relay Sel() { return (selIdx >= 0 && selIdx < view.Count) ? view[selIdx] : null; }

        private void AddSelectedTo(TextBox box)
        {
            var r = Sel();
            if (r == null || string.IsNullOrEmpty(r.Fingerprint)) { SetStatus("select a relay first"); return; }
            string tok = "$" + r.Fingerprint, cur = box.Text ?? "";
            if (cur.Contains(tok)) return;
            box.Text = (cur.Trim().Length == 0) ? tok : (cur.TrimEnd() + "\r\n" + tok);
        }

        private void AddRelayTo(TextBox box, TorControl.Relay r)
        {
            if (box == null || r == null || string.IsNullOrEmpty(r.Fingerprint)) return;
            string tok = "$" + r.Fingerprint, cur = box.Text ?? "";
            if (!cur.Contains(tok)) box.Text = (cur.Trim().Length == 0) ? tok : (cur.TrimEnd() + "\r\n" + tok);
        }

        private void OpenDetail(TorControl.Relay r)
        {
            if (r == null) return;
            var d = new AdvorDetailWindow(r);
            d.OnUseExit  += n => { sectionBox.SelectedItem = "Circuit"; AddRelayTo(exitBox,  n); SetStatus("added " + n.Nick + " to exit \u2014 press Apply route"); };
            d.OnUseEntry += n => { sectionBox.SelectedItem = "Circuit"; AddRelayTo(entryBox, n); SetStatus("added " + n.Nick + " to entry \u2014 press Apply route"); };
            d.Show(this);
        }

        // ---- relay traffic monitor (polls GETINFO traffic/*, logs to tor/tmp) ----
        private void TrafficTick(object sender, EventArgs e)
        {
            if (ctrl == null || !ctrl.Authenticated) return;
            RefreshCircuits();
            long rd, wr; string err;
            if (!ctrl.GetTraffic(out rd, out wr, out err)) return;
            var now = DateTime.UtcNow;
            string rate = "";
            if (tStamp != DateTime.MinValue)
            {
                double secs = (now - tStamp).TotalSeconds;
                if (secs > 0.1)
                {
                    double dr = (rd - tRead) / secs, dw = (wr - tWritten) / secs;
                    rate = "  (\u2193 " + Rate(dr) + " \u2191 " + Rate(dw) + ")";
                }
            }
            tRead = rd; tWritten = wr; tStamp = now;
            if (trafficReadout != null) trafficReadout.Text = "\u2193 " + Bytes(rd) + " \u00B7 \u2191 " + Bytes(wr) + rate;
            if (logTrafficCheck != null && logTrafficCheck.Checked) LogTraffic(rd, wr);
        }

        private static string Bytes(long b)
        {
            if (b >= 1L << 30) return (b / 1073741824.0).ToString("0.00") + " GB";
            if (b >= 1L << 20) return (b / 1048576.0).ToString("0.0") + " MB";
            if (b >= 1L << 10) return (b / 1024.0).ToString("0.0") + " KB";
            return b + " B";
        }
        private static string Rate(double bps)
        {
            if (bps >= 1 << 20) return (bps / 1048576.0).ToString("0.0") + " MB/s";
            if (bps >= 1 << 10) return (bps / 1024.0).ToString("0.0") + " KB/s";
            return ((long)bps) + " B/s";
        }

        private void LogTraffic(long rd, long wr)
        {
            try
            {
                string dir;
                try { dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tor", "tmp"); System.IO.Directory.CreateDirectory(dir); }
                catch { dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aloha", "tor-data", "tmp"); System.IO.Directory.CreateDirectory(dir); }
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "relay_traffic.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\tread=" + rd + "\twritten=" + wr + "\r\n");
            }
            catch { }
        }

        private static string Joined(TextBox box)
        {
            var outp = new List<string>();
            foreach (var raw in (box.Text ?? "").Replace("\r", "").Split('\n'))
                foreach (var tok in raw.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    outp.Add(tok.Trim());
            return string.Join(",", outp);
        }

        // ---- apply handlers (all live SETCONF / MAPADDRESS) ----
        private void RunSet(string setconf)
        {
            if (!Ready()) return;
            string e;
            if (ctrl.SetConf(setconf, out e)) { Persist(); SetStatus("applied + saved \u00B7 " + setconf); }
            else SetStatus("failed: " + e);
        }

        private void ApplyRoute()
        {
            if (!Ready()) return;
            var sets = new List<string>(); var resets = new List<string>();
            void One(string key, TextBox box) { string v = Joined(box); if (v.Length > 0) sets.Add(key + "=\"" + v + "\""); else resets.Add(key); }
            One("ExitNodes", exitBox); One("EntryNodes", entryBox); One("ExcludeNodes", excludeBox);
            sets.Add("StrictNodes=" + (strictCheck.Checked ? "1" : "0"));
            string err;
            if (resets.Count > 0 && !ctrl.ResetConf(string.Join(" ", resets), out err)) { SetStatus("reset failed: " + err); return; }
            if (sets.Count > 0 && !ctrl.SetConf(string.Join(" ", sets), out err)) { SetStatus("apply failed: " + err); return; }
            Persist();
            SetStatus("route applied + saved to torrc \u2014 circuits rebuilt");
        }

        private void ClearRoute()
        {
            exitBox.Text = entryBox.Text = excludeBox.Text = ""; strictCheck.Checked = false;
            if (ctrl != null && ctrl.Authenticated)
            {
                string err;
                if (ctrl.ResetConf("ExitNodes EntryNodes ExcludeNodes StrictNodes", out err)) { Persist(); SetStatus("route cleared + saved"); }
                else SetStatus("clear failed: " + err);
            }
        }

        private void ApplyBridges()
        {
            if (!Ready()) return;
            var lines = new List<string>();
            foreach (var raw in (bridgesBox.Text ?? "").Replace("\r", "").Split('\n'))
            { string t = raw.Trim(); if (t.Length > 0) lines.Add(t); }
            string err;
            if (lines.Count == 0 && !useBridgesCheck.Checked)
            { if (ctrl.ResetConf("UseBridges Bridge", out err)) { Persist(); SetStatus("bridges cleared + saved"); } else SetStatus("failed: " + err); return; }
            var sb = new StringBuilder("UseBridges=" + (useBridgesCheck.Checked ? "1" : "0"));
            foreach (var b in lines) sb.Append(" Bridge=\"" + b + "\"");
            if (ctrl.SetConf(sb.ToString(), out err)) { Persist(); SetStatus("bridges applied + saved (" + lines.Count + ")"); } else SetStatus("failed: " + err);
        }

        private void ApplyProxy()
        {
            if (!Ready()) return;
            string type = (string)proxyType.SelectedItem, host = (proxyHostBox.Text ?? "").Trim();
            string err;
            if (host.Length == 0) { if (ctrl.ResetConf(type, out err)) { Persist(); SetStatus(type + " cleared"); } else SetStatus("failed: " + err); return; }
            RunSet(type + "=" + host);
        }

        private void ClearProxy()
        {
            proxyHostBox.Text = "";
            if (!Ready()) return;
            string err;
            if (ctrl.ResetConf("Socks5Proxy Socks4Proxy HTTPSProxy", out err)) { Persist(); SetStatus("proxies cleared"); } else SetStatus("failed: " + err);
        }

        private void ApplyFirewall()
        {
            if (!Ready()) return;
            var sb = new StringBuilder("FascistFirewall=" + (fascistCheck.Checked ? "1" : "0"));
            string reach = Joined(reachBox);
            string err;
            if (reach.Length > 0) sb.Append(" ReachableAddresses=\"" + reach + "\"");
            else { ctrl.ResetConf("ReachableAddresses", out err); }
            if (ctrl.SetConf(sb.ToString(), out err)) { Persist(); SetStatus("firewall applied + saved"); } else SetStatus("failed: " + err);
        }

        private void ApplyTuning()
        {
            if (!Ready()) return;
            var sets = new List<string>();
            void Num(string key, TextBox box) { string v = (box.Text ?? "").Trim(); int n; if (v.Length > 0 && int.TryParse(v, out n) && n >= 0) sets.Add(key + "=" + n); }
            Num("CircuitBuildTimeout", cbtBox); Num("NewCircuitPeriod", ncpBox); Num("MaxCircuitDirtiness", mcdBox);
            sets.Add("EnforceDistinctSubnets=" + (distinctCheck.Checked ? "1" : "0"));
            sets.Add("UseEntryGuards=" + (guardsCheck.Checked ? "1" : "0"));
            RunSet(string.Join(" ", sets));
        }

        private void AddAddrMap()
        {
            if (!Ready()) return;
            string from = (mapFromBox.Text ?? "").Trim(), to = (mapToBox.Text ?? "").Trim();
            if (from.Length == 0 || to.Length == 0) { SetStatus("enter both From and To"); return; }
            string err;
            if (ctrl.MapAddress(from, to, out err)) SetStatus("mapped " + from + " \u2192 " + to); else SetStatus("map failed: " + err);
        }

        // ---- list paint / scroll / mouse ----
        private int VisibleRows { get { return Math.Max(1, (list.ClientSize.Height - HEAD_H) / ROW_H); } }
        private int MaxScroll   { get { return Math.Max(0, view.Count - VisibleRows); } }
        private void ScrollBy(int d) { scrollTop = Math.Max(0, Math.Min(MaxScroll, scrollTop + d)); list.Invalidate(); }
        private void EnsureVisible() { if (selIdx < scrollTop) scrollTop = selIdx; else if (selIdx >= scrollTop + VisibleRows) scrollTop = selIdx - VisibleRows + 1; }

        private void Advor_KeyDown(object sender, KeyEventArgs e)
        {
            if (loading || view.Count == 0) return;
            if (e.KeyCode == Keys.Up)        { selIdx = Math.Max(0, selIdx - 1); EnsureVisible(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { selIdx = Math.Min(view.Count - 1, selIdx + 1); EnsureVisible(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { selIdx = Math.Min(view.Count - 1, selIdx + VisibleRows); EnsureVisible(); list.Invalidate(); e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp)   { selIdx = Math.Max(0, selIdx - VisibleRows); EnsureVisible(); list.Invalidate(); e.Handled = true; }
        }

        private void List_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = list.ClientSize.Width, h = list.ClientSize.Height;
            int listW = w - SB_W;
            int cNick = 8, cCty = 218, cFlags = 268, cBw = 330, cFp = 412;

            using (var wb = new SolidBrush(Color.White)) g.FillRectangle(wb, 0, 0, w, HEAD_H);
            g.DrawString("Nickname", headFont, Brushes.Black, cNick, 4);
            g.DrawString("Region",   headFont, Brushes.Black, cCty, 4);
            g.DrawString("Flags",    headFont, Brushes.Black, cFlags, 4);
            g.DrawString("KB/s",     headFont, Brushes.Black, cBw, 4);
            g.DrawString("Fingerprint", headFont, Brushes.Black, cFp, 4);
            using (var sh = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0))) g.DrawLine(sh, 0, HEAD_H - 1, w, HEAD_H - 1);

            if (loading)
            {
                using (var gb = new SolidBrush(Green)) g.DrawString("loading relays...", rowFont, gb, cNick, HEAD_H + 6);
                DrawScrollbar(g, w, h); return;
            }

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, HEAD_H, listW, h - HEAD_H));
            int end = Math.Min(view.Count, scrollTop + VisibleRows);
            for (int i = scrollTop; i < end; i++)
            {
                var r = view[i];
                int y = HEAD_H + (i - scrollTop) * ROW_H;
                bool seld = (i == selIdx);
                if (seld) using (var sb = new SolidBrush(Green)) g.FillRectangle(sb, 0, y, listW, ROW_H);
                Brush fg = seld ? Brushes.Black : new SolidBrush(r.IsExit ? Green : Dim);
                g.DrawString(Clip(r.Nick, 26), rowFont, fg, cNick, y + 2);
                g.DrawString(r.Country ?? "??", rowFont, fg, cCty, y + 2);
                g.DrawString(FlagStr(r), rowFont, fg, cFlags, y + 2);
                g.DrawString(r.BandwidthKb.ToString(), rowFont, fg, cBw, y + 2);
                g.DrawString(Clip(r.Fingerprint, 16), rowFont, fg, cFp, y + 2);
                if (!seld) fg.Dispose();
            }
            g.Clip = clip;
            DrawScrollbar(g, w, h);
        }

        private Rectangle upRect, downRect, thumbRect;
        private void DrawScrollbar(Graphics g, int w, int h)
        {
            int x = w - SB_W, top = HEAD_H, bot = h;
            upRect = new Rectangle(x, top, SB_W, ARROW_H);
            downRect = new Rectangle(x, bot - ARROW_H, SB_W, ARROW_H);
            using (var track = new SolidBrush(Color.FromArgb(0xF4, 0xF4, 0xF4))) g.FillRectangle(track, x, top, SB_W, bot - top);
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
            DrawBevelButton(g, upRect); DrawBevelButton(g, downRect);
            DrawArrow(g, upRect, true); DrawArrow(g, downRect, false);
        }

        private void DrawBevelButton(Graphics g, Rectangle r)
        {
            using (var f = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0))) g.FillRectangle(f, r);
            using (var hi = new Pen(Color.White)) { g.DrawLine(hi, r.Left, r.Top, r.Right - 1, r.Top); g.DrawLine(hi, r.Left, r.Top, r.Left, r.Bottom - 1); }
            using (var sh = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0))) { g.DrawLine(sh, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1); g.DrawLine(sh, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1); }
        }

        private void DrawArrow(Graphics g, Rectangle r, bool up)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var p = new Pen(Color.FromArgb(0x50, 0x50, 0x50), 1.4f))
            {
                if (up) g.DrawLines(p, new[] { new Point(cx - 4, cy + 2), new Point(cx, cy - 3), new Point(cx + 4, cy + 2) });
                else    g.DrawLines(p, new[] { new Point(cx - 4, cy - 3), new Point(cx, cy + 2), new Point(cx + 4, cy - 3) });
            }
        }

        private void List_MouseDown(object sender, MouseEventArgs e)
        {
            if (upRect.Contains(e.Location))   { ScrollBy(-1); return; }
            if (downRect.Contains(e.Location)) { ScrollBy(1); return; }
            if (e.X >= list.ClientSize.Width - SB_W) { if (!thumbRect.IsEmpty) ScrollBy(e.Y < thumbRect.Top ? -VisibleRows : VisibleRows); return; }
            if (loading || view.Count == 0 || e.Y < HEAD_H) return;
            int idx = scrollTop + (e.Y - HEAD_H) / ROW_H;
            if (idx >= 0 && idx < view.Count)
            {
                selIdx = idx; list.Invalidate();
                if (e.Clicks >= 2) OpenDetail(view[idx]);
            }
        }

        // ---- circuits inset: fetch / build rows / paint / scroll / click ----
        private void RefreshCircuits()
        {
            if (ctrl == null || !ctrl.Authenticated) return;
            string err;
            var cs = ctrl.GetCircuits(out err);
            if (err != null) return;
            circuits = cs;
            RebuildCircuitRows();
            if (circuitPanel != null) circuitPanel.Invalidate();
        }

        // Flatten circuits into display rows: a heading bar per circuit, then one node-list
        // row per hop (resolved against the consensus by fingerprint when possible).
        private void RebuildCircuitRows()
        {
            circRows.Clear();
            if (circuits != null)
            {
                foreach (var c in circuits)
                {
                    string purpose = string.IsNullOrEmpty(c.Purpose) ? "" : "  \u00b7  " + c.Purpose;
                    bool built = string.Equals(c.Status, "BUILT", StringComparison.OrdinalIgnoreCase);
                    circRows.Add(new CircRow { Header = true, HeadColor = built ? Green : Dim,
                        Text = "#" + c.Id + "  " + (c.Status ?? "") + purpose });
                    int hops = c.Relays != null ? c.Relays.Count : 0;
                    for (int i = 0; i < hops; i++)
                    {
                        string fp = (c.Fingerprints != null && i < c.Fingerprints.Count) ? c.Fingerprints[i] : "";
                        TorControl.Relay rel = null;
                        if (fpMap != null && !string.IsNullOrEmpty(fp)) fpMap.TryGetValue(fp.ToUpperInvariant(), out rel);
                        circRows.Add(new CircRow { Header = false, Relay = rel, Nick = c.Relays[i] });
                    }
                }
            }
            int max = Math.Max(0, circRows.Count - CircuitVisibleRows);
            if (circuitScrollTop > max) circuitScrollTop = max;
        }

        private int CircuitVisibleRows { get { return circuitPanel == null ? 1 : Math.Max(1, (circuitPanel.ClientSize.Height - HEAD_H) / ROW_H); } }
        private int CircuitMaxScroll   { get { return Math.Max(0, circRows.Count - CircuitVisibleRows); } }
        private void CircuitScrollBy(int d) { circuitScrollTop = Math.Max(0, Math.Min(CircuitMaxScroll, circuitScrollTop + d)); if (circuitPanel != null) circuitPanel.Invalidate(); }

        private void Circuits_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = circuitPanel.ClientSize.Width, h = circuitPanel.ClientSize.Height;
            int listW = w - SB_W;
            int cNick = 8, cCty = 218, cFlags = 268, cBw = 330, cFp = 412;

            // header row mirrors the relay library so the columns line up between the two
            using (var wb = new SolidBrush(Color.White)) g.FillRectangle(wb, 0, 0, w, HEAD_H);
            g.DrawString("Circuits",    headFont, Brushes.Black, cNick, 4);
            g.DrawString("Region",      headFont, Brushes.Black, cCty, 4);
            g.DrawString("Flags",       headFont, Brushes.Black, cFlags, 4);
            g.DrawString("KB/s",        headFont, Brushes.Black, cBw, 4);
            g.DrawString("Fingerprint", headFont, Brushes.Black, cFp, 4);
            using (var sh = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0))) g.DrawLine(sh, 0, HEAD_H - 1, w, HEAD_H - 1);

            if (ctrl == null || !ctrl.Authenticated)
            {
                using (var gb = new SolidBrush(Dim)) g.DrawString("(connect to see circuits)", rowFont, gb, cNick, HEAD_H + 6);
                DrawCircuitScrollbar(g, w, h); return;
            }
            if (circRows.Count == 0)
            {
                using (var gb = new SolidBrush(Dim)) g.DrawString("(no circuits yet \u2014 browse to build some)", rowFont, gb, cNick, HEAD_H + 6);
                DrawCircuitScrollbar(g, w, h); return;
            }

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, HEAD_H, listW, h - HEAD_H));
            int end = Math.Min(circRows.Count, circuitScrollTop + CircuitVisibleRows);
            for (int i = circuitScrollTop; i < end; i++)
            {
                var row = circRows[i];
                int y = HEAD_H + (i - circuitScrollTop) * ROW_H;
                if (row.Header)
                {
                    using (var hb = new SolidBrush(Color.FromArgb(0x12, 0x12, 0x12))) g.FillRectangle(hb, 0, y, listW, ROW_H);
                    using (var tb = new SolidBrush(row.HeadColor)) g.DrawString(Clip(row.Text, 70), rowFont, tb, cNick, y + 2);
                }
                else
                {
                    var r = row.Relay;
                    Color col = (r != null && r.IsExit) ? Green : Dim;
                    using (var fg = new SolidBrush(col))
                    {
                        g.DrawString("\u2192 " + Clip(r != null ? r.Nick : (row.Nick ?? "?"), 24), rowFont, fg, cNick, y + 2);
                        g.DrawString(r != null ? (r.Country ?? "??") : "??",      rowFont, fg, cCty, y + 2);
                        g.DrawString(r != null ? FlagStr(r) : "----",            rowFont, fg, cFlags, y + 2);
                        g.DrawString(r != null ? r.BandwidthKb.ToString() : "?", rowFont, fg, cBw, y + 2);
                        g.DrawString(r != null ? Clip(r.Fingerprint, 16) : "(left consensus)", rowFont, fg, cFp, y + 2);
                    }
                }
            }
            g.Clip = clip;
            DrawCircuitScrollbar(g, w, h);
        }

        private void DrawCircuitScrollbar(Graphics g, int w, int h)
        {
            int x = w - SB_W, top = HEAD_H, bot = h;
            cUpRect   = new Rectangle(x, top, SB_W, ARROW_H);
            cDownRect = new Rectangle(x, bot - ARROW_H, SB_W, ARROW_H);
            using (var track = new SolidBrush(Color.FromArgb(0xF4, 0xF4, 0xF4))) g.FillRectangle(track, x, top, SB_W, bot - top);
            int trackH = (bot - ARROW_H) - (top + ARROW_H);
            if (circRows.Count > 0 && CircuitMaxScroll > 0 && trackH > 8)
            {
                double frac = (double)CircuitVisibleRows / circRows.Count;
                int thumbH = Math.Max(14, (int)(trackH * frac));
                int range = trackH - thumbH;
                int ty = top + ARROW_H + (int)((double)circuitScrollTop / CircuitMaxScroll * range);
                cThumbRect = new Rectangle(x + 1, ty, SB_W - 2, thumbH);
                DrawBevelButton(g, cThumbRect);
            }
            else cThumbRect = Rectangle.Empty;
            DrawBevelButton(g, cUpRect); DrawBevelButton(g, cDownRect);
            DrawArrow(g, cUpRect, true); DrawArrow(g, cDownRect, false);
        }

        private void Circuits_MouseDown(object sender, MouseEventArgs e)
        {
            if (cUpRect.Contains(e.Location))   { CircuitScrollBy(-1); return; }
            if (cDownRect.Contains(e.Location)) { CircuitScrollBy(1); return; }
            if (e.X >= circuitPanel.ClientSize.Width - SB_W)
            { if (!cThumbRect.IsEmpty) CircuitScrollBy(e.Y < cThumbRect.Top ? -CircuitVisibleRows : CircuitVisibleRows); return; }
            if (e.Y < HEAD_H || circRows.Count == 0) return;
            int idx = circuitScrollTop + (e.Y - HEAD_H) / ROW_H;
            if (idx < 0 || idx >= circRows.Count) return;
            var row = circRows[idx];
            if (!row.Header && row.Relay != null) OpenDetail(row.Relay);   // click a hop -> detail
        }

        private static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "\u2026";
        }
    }
}
