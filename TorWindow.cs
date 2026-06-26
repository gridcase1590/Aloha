using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // TorWindow — browserconfig-styled control panel for the bundled Tor daemon.
    // Light #FAFAFB (FAB) ground like the config panels, with the torrc / CIRCUIT
    // / STDOUT windows as sunken black consoles (lime-on-black + DarkScrollBars).
    //
    //   TOR
    //   SOCKS    [ 9050 ]   control [ 9051 ]    (editable; defaults 9050 / 9051)
    //   torrc    [ large black console — live torrc, hand-editable, dark scrolls ]
    //   BRIDGES  [ optional, one per line ]
    //   status   <live one-line state>
    //   CIRCUIT  [ black console, smaller ]
    //   [Start] [Stop] [New Identity] [Route browser]
    //   STDOUT   [ black console, large — all tor stdout streams here ]
    // ============================================================
    public class TorWindow : DafyFrame
    {
        private static readonly Color Lime    = Color.FromArgb(0x33, 0xFF, 0x66);
        private static readonly Color Dim     = Color.FromArgb(0x2C, 0x8A, 0x4A);
        private static readonly Color Fab     = Color.FromArgb(0xFA, 0xFA, 0xFB);   // #FAFAFB ground
        private static readonly Color LblDark = Color.FromArgb(0x55, 0x55, 0x55);   // section labels
        private static readonly Color HeadGrn = Color.FromArgb(0x17, 0x6B, 0x33);   // ready accent
        private static readonly Color Frame   = Color.FromArgb(0xF0, 0xF0, 0xF0);   // light-gray console frame

        private TorProcess tor;
        private TorControl ctrl;
        private System.Windows.Forms.Timer poll;

        private Panel view;                       // FAB background: header + section labels
        private TextBox socksInput, controlInput;
        private RichTextBox torrcBox, consoleBox, circuitsBox;
        private Panel torrcBevel, consoleBevel, circuitsBevel;
        private readonly List<Panel> actionButtons = new List<Panel>();

        private bool suppressDirty = false;       // guard programmatic torrc updates
        private bool torrcDirty = false;          // user has hand-edited the torrc box

        private string status = "stopped";
        private List<TorControl.Circuit> circuits = new List<TorControl.Circuit>();
        private Font hFont, bFont;

        // layout constants (client coords) shared by View_Paint + LayoutTor
        private const int M = 10;
        private const int YSocks = 14, YControl = 42;
        private const int YTorrcLbl   = 70,  YTorrc   = 88,  HTorrc   = 130;
        private const int YCircuitsLbl = 228, YCircuits = 246, HCircuits = 150;
        private const int YButtons = 404, HButtons = 22;
        private const int YConsoleLbl = 432, YConsole = 452;
        private const int PortX = 78, PortW = 64;
        private const int MinW = 420, MinH = 520;   // below this the client area scrolls

        // parent may listen to route the browser proxy at Tor's SOCKS port
        public event Action OnRouteRequested;

        protected override bool FooterActsAsResizeGrip { get { return false; } }
        private Panel canvas;

        public TorWindow() : base("OPT-TOR", "Tor")
        {
            hFont = new Font("Lucida Console", 12f, FontStyle.Bold);
            bFont = new Font("Consolas", 9.5f);

            this.Width = 560; this.Height = 700;
            this.ClientArea.BackColor = Fab;
            canvas = SetupAlohaScroll(Fab);

            // FAB background paint surface: header + section labels
            view = new DoubleBufferedPanel { BackColor = Fab };   // sized in LayoutTor; scrolls with the controls
            view.Paint += View_Paint;
            view.MouseDown += (s, e) => view.Focus();
            canvas.Controls.Add(view);

            // SOCKS / control port inputs (default 9050 / 9051; editable)
            socksInput   = LightInput(false); socksInput.Text   = "9050";
            controlInput = LightInput(false); controlInput.Text = "9051";
            canvas.Controls.Add(socksInput);
            canvas.Controls.Add(controlInput);

            // torrc — large black console, hand-editable, with dark v+h scrollbars
            torrcBevel = MakeConsole(out torrcBox, readOnly: false, horizontal: true);
            canvas.Controls.Add(torrcBevel);

            // CIRCUIT inset — smaller black console
            circuitsBevel = MakeConsole(out circuitsBox, readOnly: true, horizontal: true);
            canvas.Controls.Add(circuitsBevel);

            // action buttons row — under the circuits window
            var bStart = MakeLabeledButton("Start",         StartTor);
            var bStop  = MakeLabeledButton("Stop",          StopTor);
            var bId    = MakeLabeledButton("New Identity",  NewIdentity);
            var bRoute = MakeLabeledButton("Route browser", () => { var h = OnRouteRequested; h?.Invoke(); });
            foreach (var b in new[] { bStart, bStop, bId, bRoute })
            {
                actionButtons.Add(b);
                canvas.Controls.Add(b);
            }

            // STDOUT inset — large black console; all tor output streams here
            consoleBevel = MakeConsole(out consoleBox);
            canvas.Controls.Add(consoleBevel);

            // bring the floating controls above the painted background
            socksInput.BringToFront(); controlInput.BringToFront();
            torrcBevel.BringToFront();
            circuitsBevel.BringToFront();
            foreach (var b in actionButtons) b.BringToFront();
            consoleBevel.BringToFront();

            // live torrc regeneration: field edits rebuild the box; box edits mark dirty
            socksInput.TextChanged   += (s, e) => RegenTorrc(true);
            controlInput.TextChanged += (s, e) => RegenTorrc(true);
            torrcBox.TextChanged     += (s, e) => { if (!suppressDirty) torrcDirty = true; };

            poll = new System.Windows.Forms.Timer { Interval = 1200 };
            poll.Tick += (s, e) => Poll();

            canvas.Resize += (s, e) => LayoutTor();
            this.Shown += (s, e) => { LayoutTor(); RegenTorrc(true); RenderCircuits(); };
            this.FormClosed += (s, e) => Cleanup();

            Log("tor control panel ready. set ports / bridges if needed, then press Start.");
        }

        // a light (white) lime-free form field, matching the FAB browserconfig look
        private TextBox LightInput(bool multiline)
        {
            return new TextBox
            {
                Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                BackColor = Color.White, ForeColor = Color.Black,
                BorderStyle = BorderStyle.Fixed3D, Font = new Font("Consolas", 9f)
            };
        }

        // Build an indented (sunken-bevel) black console: lime-on-black RichTextBox with
        // dark scrollbars laid over its edges. readOnly=false makes it editable (torrc);
        // horizontal=true adds a bottom dark scrollbar + disables word wrap (long paths).
        private Panel MakeConsole(out RichTextBox box, bool readOnly = true, bool horizontal = false)
        {
            var bevel = new Panel { BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0), BorderStyle = BorderStyle.None };   // flat gray frame (FixedSingle rendered a 1px black edge)
            var b = new RichTextBox
            {
                ReadOnly = readOnly, WordWrap = !horizontal, DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.None,
                BackColor = Color.Black, ForeColor = Lime,
                Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None
            };
            var v = new DarkScrollBar(false, light: true) { Dock = DockStyle.None };
            DarkScrollBar h = horizontal ? new DarkScrollBar(true, light: true) { Dock = DockStyle.None } : null;
            bevel.Controls.Add(b);
            bevel.Controls.Add(v);
            if (h != null) bevel.Controls.Add(h);
            const int SB = 17, PAD = 1;
            void Layout()
            {
                int cw = bevel.ClientSize.Width, ch = bevel.ClientSize.Height;
                int hh = (h != null) ? SB : 0;
                if (cw < SB + PAD * 2 + 1 || ch < PAD * 2 + hh + 1) return;
                b.SetBounds(PAD, PAD, cw - SB - PAD * 2, ch - PAD * 2 - hh);
                v.SetBounds(cw - SB - PAD, PAD, SB, ch - PAD * 2 - hh);
                if (h != null) h.SetBounds(PAD, ch - SB - PAD, cw - SB - PAD * 2, SB);
            }
            bevel.Resize += (s, e) => Layout();
            v.Attach(b);
            if (h != null) h.Attach(b);
            box = b;
            return bevel;
        }

        // single source of layout truth; runs on every client resize and first show
        private void LayoutTor()
        {
            if (torrcBox == null) return;
            int cw = Math.Max(AlohaContent.Width,  MinW);
            int ch = Math.Max(AlohaContent.Height, MinH);
            view.SetBounds(0, 0, cw, ch);
            int w = Math.Max(120, cw - M * 2);
            socksInput.SetBounds(M + PortX, YSocks, PortW, 20);
            controlInput.SetBounds(M + PortX, YControl, PortW, 20);
            torrcBevel.SetBounds(M, YTorrc, w, HTorrc);
            circuitsBevel.SetBounds(M, YCircuits, w, HCircuits);
            int bx = M;
            foreach (var b in actionButtons) { b.Top = YButtons; b.Left = bx; bx += b.Width + 6; }
            int consoleH = Math.Max(48, ch - YConsole - M);
            consoleBevel.SetBounds(M, YConsole, w, consoleH);
        }

        private int ParsePort(TextBox t, int dflt)
        {
            int v;
            return (int.TryParse((t.Text ?? "").Trim(), out v) && v > 0 && v < 65536) ? v : dflt;
        }

        // Rebuild the torrc box from the current port/bridge fields. force overrides a
        // user-dirtied box (a deliberate field edit reasserts control over the torrc).
        private void RegenTorrc(bool force)
        {
            if (torrcBox == null) return;
            if (torrcDirty && !force) return;
            int s = ParsePort(socksInput, 9050), c = ParsePort(controlInput, 9051);
            string preview;
            try { preview = new TorProcess("", s, c).BuildTorrc(); }
            catch (Exception ex) { preview = "# could not build torrc: " + ex.Message; }
            suppressDirty = true;
            torrcBox.Text = preview;
            suppressDirty = false;
            torrcDirty = false;
        }

        private void StartTor()
        {
            if (tor != null && tor.Running) { SetStatus("already running"); return; }
            int s = ParsePort(socksInput, 9050), c = ParsePort(controlInput, 9051);
            tor = new TorProcess("", s, c);
            if (torrcDirty)
            {
                tor.TorrcOverride = torrcBox.Text;
                // keep the control connection aligned with the hand-edited torrc
                int op;
                if (TryReadPort(torrcBox.Text, "SocksPort",   out op)) tor.SocksPort   = op;
                if (TryReadPort(torrcBox.Text, "ControlPort", out op)) tor.ControlPort = op;
                Log("using hand-edited torrc (override).");
            }
            tor.Line   += (ln) => Log(ln);
            tor.Ready  += () => Marshal(() => { SetStatus("bootstrapped 100% \u2014 connecting control"); ConnectControl(); });
            tor.Failed += (why) => Marshal(() => { SetStatus("failed"); Log("FAILED: " + why); });
            Log("\u2014\u2014 starting tor.exe (SOCKS " + tor.SocksPort + " / control " + tor.ControlPort + ") \u2014\u2014");
            SetStatus("starting tor.exe...");
            if (tor.Start()) poll.Start();
        }

        private static bool TryReadPort(string torrc, string key, out int port)
        {
            port = 0;
            foreach (var raw in (torrc ?? "").Replace("\r", "").Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = ln.Substring(key.Length).Trim();
                    int sp = rest.IndexOf(' ');
                    if (sp > 0) rest = rest.Substring(0, sp);
                    return int.TryParse(rest, out port);
                }
            }
            return false;
        }

        private void StopTor()
        {
            poll.Stop();
            try { ctrl?.Dispose(); } catch { }
            ctrl = null;
            try { tor?.Stop(); } catch { }
            circuits.Clear();
            RenderCircuits();
            SetStatus("stopped");
            Log("\u2014\u2014 stopped \u2014\u2014");
        }

        private void ConnectControl()
        {
            if (tor == null) return;
            try
            {
                ctrl = new TorControl(tor.CookiePath, tor.ControlPort);
                string err;
                if (!ctrl.Connect(out err)) { SetStatus("control: " + err); Log("control auth failed: " + err); ctrl = null; }
                else { SetStatus("connected \u2014 Tor ready"); Log("control port authenticated; Tor ready."); }
            }
            catch (Exception ex) { SetStatus("control error"); Log("control error: " + ex.Message); }
        }

        private void NewIdentity()
        {
            if (ctrl == null || !ctrl.Authenticated) { SetStatus("not connected"); return; }
            string err;
            if (ctrl.NewIdentity(out err))
            {
                ctrl.ClearDnsCache();
                // honest: a new circuit / exit IP is likely but not guaranteed, and
                // Tor may rate-limit repeated NEWNYM signals.
                SetStatus("new identity requested (new circuits building)");
                Log("NEWNYM sent + DNS cache cleared.");
            }
            else { SetStatus("new identity failed"); Log("new identity failed: " + err); }
        }

        private void Poll()
        {
            if (tor == null) return;
            if (!tor.Running) { SetStatus("tor exited"); poll.Stop(); return; }

            if (tor.BootstrapPercent < 100)
            {
                SetStatus("bootstrapping " + tor.BootstrapPercent + "%");
            }
            else if (ctrl != null && ctrl.Authenticated)
            {
                string err;
                circuits = ctrl.GetCircuits(out err);
                RenderCircuits();
            }
        }

        private void RenderCircuits()
        {
            if (circuitsBox == null || circuitsBox.IsDisposed) return;
            circuitsBox.Clear();
            bool ready = tor != null && tor.BootstrapPercent >= 100;
            if (circuits == null || circuits.Count == 0)
            {
                AppendCircuit(ready ? "(no circuits yet)" : "(start Tor to see circuits)", Dim);
            }
            else
            {
                int shown = 0;
                foreach (var c in circuits)
                {
                    if (shown++ >= 8) break;
                    bool built = c.Status == "BUILT";
                    string path = c.Relays.Count > 0 ? string.Join(" \u2192 ", c.Relays) : "(building)";
                    string head = ("#" + c.Id).PadRight(5) + c.Status.PadRight(7);
                    AppendCircuit(head + path, built ? Lime : Dim);
                }
            }
            circuitsBox.SelectionStart = 0;
            circuitsBox.ScrollToCaret();
        }

        private void AppendCircuit(string text, Color color)
        {
            circuitsBox.SelectionStart = circuitsBox.TextLength;
            circuitsBox.SelectionColor = color;
            circuitsBox.AppendText(text + "\n");
        }

        // Append a line to STDOUT (marshalled — tor.Line fires off-thread).
        private void Log(string line)
        {
            Marshal(() =>
            {
                if (consoleBox == null || consoleBox.IsDisposed) return;
                consoleBox.AppendText((line ?? "") + "\n");
                consoleBox.SelectionStart = consoleBox.TextLength;
                consoleBox.ScrollToCaret();
            });
        }

        private void SetStatus(string s) { status = s; view?.Invalidate(); }
        private void Marshal(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }

        private void View_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var lbl = new SolidBrush(LblDark))
            {
                g.DrawString("SOCKS",   bFont, lbl, M, YSocks + 2);
                g.DrawString("control", bFont, lbl, M, YControl + 2);
                g.DrawString("torrc",   bFont, lbl, M, YTorrcLbl);
                g.DrawString("CIRCUIT", bFont, lbl, M, YCircuitsLbl);
                g.DrawString("STDOUT",  bFont, lbl, M, YConsoleLbl);
            }
        }

        private void Cleanup()
        {
            try { poll?.Stop(); } catch { }
            try { ctrl?.Dispose(); } catch { }
            try { tor?.Stop(); } catch { }
        }
    }
}
