using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Aloha
{
    public partial class Form1 : Form
    {
        public const string VERSION = "0.4.1";
        private const string HOME = "";

        private static readonly string UserDataFolder =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                "AlohaProfile");

        private int engineGen = 0;   // bumped each re-init -> fresh sibling user-data folder

        private NetConfig cfg;

        // Step D: modern WebView2-driving scrollbars (behind the config toggle)
        private WebScrollBar vScroll, hScroll;
        private Panel scrollCorner;
        private const string HideBarsScript =
            "(function(){var s=document.createElement('style');" +
            "s.textContent='::-webkit-scrollbar{width:0 !important;height:0 !important;display:none !important}" +
            "html{scrollbar-width:none !important;-ms-overflow-style:none !important}';" +
            "(document.head||document.documentElement).appendChild(s);})();";

        public Form1()
        {
            InitializeComponent();
            cfg = NetConfig.Load();
            ApplyEngineCreationProperties();   // folder + args BEFORE the control's handle -> single engine
            titleBar.Text = "Aloha Browser " + VERSION;
            LoadBranding();
            this.DoubleBuffered = true;
            this.Paint += Form1_Paint;
            this.Resize += (s, e) => { this.Invalidate(); LayoutWeb(); LayoutFooter(); };
            SetupChrome();
            SetupMenus();
        }

        // ───────────────────────────────────────────────────────────────
        // Window chrome lives in a bottom FOOTER (console-style gray strip):
        // decorative sunken cubes + raised maximize/close buttons. Corner cube
        // still does click-maximize / drag-resize. Edge-resize to be revisited.
        // ───────────────────────────────────────────────────────────────
        private Panel btnMax, btnClose, btnMin, footer;
        private readonly Color cFooter = Color.FromArgb(0xF0, 0xF0, 0xF0);   // modern light gray (matches the scrollbar band)

        // Injected at document-creation when "Disable WebRTC entirely" is on.
        // Chromium dropped --disable-webrtc, so we strip the constructors instead:
        // non-writable undefined means a page can't even restore them, and ICE
        // never runs, so there is no IP to leak. getUserMedia rejects cleanly.
        private const string WebRtcKillScript = @"
(function(){
  var kill = ['RTCPeerConnection','webkitRTCPeerConnection','mozRTCPeerConnection','RTCDataChannel','RTCSessionDescription','RTCIceCandidate','RTCRtpSender'];
  kill.forEach(function(k){
    try { Object.defineProperty(window, k, { value: undefined, writable:false, configurable:false }); }
    catch(e){ try { window[k] = undefined; } catch(_){} }
  });
  function reject(){ return Promise.reject(new DOMException('WebRTC disabled','NotFoundError')); }
  if (navigator.mediaDevices){
    try { navigator.mediaDevices.getUserMedia = reject; } catch(e){}
    try { navigator.mediaDevices.enumerateDevices = function(){ return Promise.resolve([]); }; } catch(e){}
  }
  try { navigator.getUserMedia = navigator.webkitGetUserMedia = navigator.mozGetUserMedia =
        function(c,s,e){ if(e) e(new DOMException('WebRTC disabled','NotFoundError')); }; } catch(e){}
})();";
        private BevelPanel fIndentWide, fCube1, fCube2;

        private void SetupChrome()
        {
            titleBar.TextAlign = ContentAlignment.MiddleCenter;
            this.MinimumSize = new System.Drawing.Size(520, 360);   // floor for corner-drag resize
            SetupFooter();

            // Intercept proxy / Raw TCP / Network map items — created here (with their
            // handlers), but PLACED by SetupMenus so the menu reorg stays in one place.
            interceptItem = new ToolStripMenuItem("Intercept proxy...");
            interceptItem.Click += interceptToolStripMenuItem_Click;
            rawTcpItem = new ToolStripMenuItem("Raw TCP listener...");
            rawTcpItem.Click += rawTcpToolStripMenuItem_Click;
            netMapItem = new ToolStripMenuItem("Network map");
            netMapItem.Click += networkMapToolStripMenuItem_Click;
            this.FormClosed += (s, e) => { try { interceptProxy.Stop(); } catch { } };

            RaiseChrome();
        }

        // keep the footer (and its buttons) above the (re-created) WebView2
        private void RaiseChrome()
        {
            if (footer != null) footer.BringToFront();
        }

        // shared by the ▲ button and the corner cube
        private void ToggleMaximize()
        {
            if (this.WindowState == FormWindowState.Maximized)
                this.WindowState = FormWindowState.Normal;
            else
            {
                try { this.MaximizedBounds = Screen.FromControl(this).WorkingArea; } catch { }
                this.WindowState = FormWindowState.Maximized;
            }
        }

        // ── bottom-right corner cube: click = maximize/restore, drag = resize ──
        // Lives on the scrollCorner control (in the chrome, above the page), so it
        // gets clean mouse events without fighting the WebView2's HWND. We detect
        // click-vs-drag manually rather than handing off to a native resize loop,
        // because that loop would hijack the mouse on down and we'd lose the click.
        private System.Drawing.Point cornerDragStart;
        private System.Drawing.Size  cornerStartSize;
        private bool cornerDragging, cornerDidDrag;

        private void Corner_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            cornerDragging  = true;
            cornerDidDrag   = false;
            cornerDragStart = Cursor.Position;        // screen coords (control captures mouse)
            cornerStartSize = this.Size;
        }

        private void Corner_MouseMove(object sender, MouseEventArgs e)
        {
            if (!cornerDragging) return;
            var now = Cursor.Position;
            int dx = now.X - cornerDragStart.X;
            int dy = now.Y - cornerDragStart.Y;
            if (!cornerDidDrag && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // below threshold = still a click
            cornerDidDrag = true;
            if (this.WindowState == FormWindowState.Maximized) return;       // don't drag-resize while maximized
            // grow/shrink from the bottom-right; top-left stays put. MinimumSize clamps the floor.
            this.Size = new System.Drawing.Size(cornerStartSize.Width + dx, cornerStartSize.Height + dy);
        }

        private void Corner_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            cornerDragging = false;
            cornerDidDrag  = false;
            // click-maximize removed — maximize is the footer button now; the cube only resizes
        }

        // ── footer: a console-style gray status strip across the bottom, filled
        // with decorative SUNKEN bevel cubes (cosmetic, do nothing). The window's
        // maximize/close live here as RAISED bevel cubes with modern □/✕ glyphs
        // ("outdented" — opposite bevel from the sunken indents). The footer is
        // chrome with nothing under it, so the buttons get clean mouse events.
        private void SetupFooter()
        {
            footer = new Panel { Dock = DockStyle.Bottom, Height = FT, BackColor = cFooter };

            // decorative "indents that don't do anything" — modern light-gray sunken wells
            fIndentWide = new BevelPanel { Left = 2, Top = 1, Height = 15, Width = 300, BackColor = cFooter };
            fCube1 = new BevelPanel { Width = 20, Height = 15, Top = 1, BackColor = cFooter };
            fCube2 = new BevelPanel { Width = 46, Height = 15, Top = 1, BackColor = cFooter };

            // functional raised modern buttons, far right — 17px tall to fill the slim footer
            btnMin   = MakeFooterButton("min",   () => this.WindowState = FormWindowState.Minimized);
            btnMax   = MakeFooterButton("max",   ToggleMaximize);
            btnClose = MakeFooterButton("close", () => this.Close());
            btnMin.Top = 0; btnMax.Top = 0; btnClose.Top = 0;

            footer.Controls.Add(fIndentWide);
            footer.Controls.Add(fCube1);
            footer.Controls.Add(fCube2);
            footer.Controls.Add(btnMin);
            footer.Controls.Add(btnMax);
            footer.Controls.Add(btnClose);
            footer.Resize += (s, e) => LayoutFooter();

            this.Controls.Add(footer);
            LayoutFooter();
        }

        // position footer contents against the real footer width (post-layout safe)
        private void LayoutFooter()
        {
            if (footer == null || btnMin == null) return;
            int w = footer.ClientSize.Width;
            btnClose.Left = w - btnClose.Width - 3;
            btnMax.Left   = btnClose.Left - btnMax.Width - 2;
            btnMin.Left   = btnMax.Left - btnMin.Width - 2;
            fCube2.Left   = btnMin.Left - fCube2.Width - 6;
            fCube1.Left   = fCube2.Left - fCube1.Width - 3;
            fIndentWide.Width = Math.Max(40, fCube1.Left - fIndentWide.Left - 4);
        }

        // (minimize now goes to the taskbar via WindowState.Minimized; no tray)

        // an INDENTED (sunken) modern cube — no glyph, matching the decorative
        // indent cubes. It reveals itself only on hover (close → red); a press
        // darkens it. Wider than the old glyph buttons.
        private Panel MakeFooterButton(string kind, Action onClick)
        {
            var b = new Panel { Width = 44, Height = 17, Cursor = Cursors.Hand, BackColor = cFooter };
            bool hover = false, pressed = false;
            b.Paint += (s, e) =>
            {
                var g = e.Graphics;
                var r = b.ClientRectangle;
                Color fill = pressed ? Color.FromArgb(0xE4, 0xE4, 0xE4)
                                     : (hover ? Color.FromArgb(0xF7, 0xF7, 0xF7) : cFooter);
                using (var br = new SolidBrush(fill)) g.FillRectangle(br, r);
                // 1px bevel: indented (sunken) normally, pops OUT (raised) while pressed
                int w1 = r.Width - 1, h1 = r.Height - 1;
                Color tl  = pressed ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);
                Color brc = pressed ? Color.FromArgb(0x80, 0x80, 0x80) : Color.White;
                using (var pTl = new Pen(tl))  { g.DrawLine(pTl, 0, 0, w1, 0);  g.DrawLine(pTl, 0, 0, 0, h1); }
                using (var pBr = new Pen(brc)) { g.DrawLine(pBr, 0, h1, w1, h1); g.DrawLine(pBr, w1, 0, w1, h1); }
            };
            b.MouseEnter += (s, e) => { hover = true; b.Invalidate(); };
            b.MouseLeave += (s, e) => { hover = false; pressed = false; b.Invalidate(); };
            b.MouseDown  += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; b.Invalidate(); } };
            b.MouseUp    += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                bool fire = pressed && b.ClientRectangle.Contains(e.Location);
                pressed = false; b.Invalidate();
                if (fire) onClick();
            };
            return b;
        }

        // window icon (taskbar / alt-tab) + the title-bar mark
        private void LoadBranding()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                string ico = System.IO.Path.Combine(dir, "AlohaIcon.ico");
                if (System.IO.File.Exists(ico))
                    this.Icon = new System.Drawing.Icon(ico);
            }
            catch { }
        }

        // Paint the form background with the same light->darker grey vertical
        // gradient the menu strip uses, so the whole window is one surface.
        private void Form1_Paint(object sender, PaintEventArgs e)
        {
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                this.ClientRectangle,
                System.Drawing.Color.FromArgb(252, 252, 253),   // light grey, top
                System.Drawing.Color.FromArgb(216, 216, 220),   // darker grey, bottom
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(brush, this.ClientRectangle);
            }
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            LayoutFooter();   // form now has its real size
            TidyEngineFolders(0);   // clear ephemeral "_rN" folders from prior runs
            await InitWebAsync();
        }

        // Best-effort: remove the ephemeral "AlohaProfile_rN" folders created by
        // engine re-inits. The base AlohaProfile (persistent profile) is never
        // touched. keepGen>0 preserves the currently-active folder; folders still
        // locked by a live process are skipped.
        private static void TidyEngineFolders(int keepGen)
        {
            try
            {
                string parent = System.IO.Path.GetDirectoryName(UserDataFolder);
                string prefix = System.IO.Path.GetFileName(UserDataFolder) + "_r";
                string keep   = keepGen > 0 ? prefix + keepGen : null;
                foreach (var d in System.IO.Directory.GetDirectories(parent, prefix + "*"))
                    if (keep == null || System.IO.Path.GetFileName(d) != keep)
                        try { System.IO.Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        // ── engine init, applying the current NetConfig ──
        // Apply the user-data folder + browser args via the control's CreationProperties.
        // This MUST run before the control's window handle is created — in the ctor for the
        // first engine, and before Controls.Add for a rebuilt one — so the control's OWN
        // initialization uses these settings. Result: exactly one engine, not two trees.
        private void ApplyEngineCreationProperties()
        {
            string dataFolder = engineGen == 0 ? UserDataFolder : UserDataFolder + "_r" + engineGen;
            string browserArgs = cfg.BuildBrowserArguments();
            web.CreationProperties = new Microsoft.Web.WebView2.WinForms.CoreWebView2CreationProperties
            {
                UserDataFolder = dataFolder,
                AdditionalBrowserArguments = string.IsNullOrWhiteSpace(browserArgs) ? null : browserArgs
            };
        }

        private async Task InitWebAsync()
        {
            try
            {
                string browserArgs = cfg.BuildBrowserArguments();
                ActivityLog.General("engine init — profile [" + cfg.ProfileName + "]");
                if (!string.IsNullOrWhiteSpace(browserArgs))
                    ActivityLog.General("browser args: " + browserArgs);

                // Folder + args are applied via web.CreationProperties (set before the
                // control's handle existed), so the control runs a SINGLE initialization
                // with our settings — no separate environment, no second engine/process
                // tree racing the control's own auto-init.
                var ensure = web.EnsureCoreWebView2Async();
                if (await Task.WhenAny(ensure, Task.Delay(12000)) != ensure)
                    throw new TimeoutException(
                        "WebView2 took too long to start — the user-data folder may still be " +
                        "locked by the previous engine. Try Apply again.");
                await ensure;   // observe completion / surface any inner error

                // Full WebRTC disable (the flag is dead in modern Chromium) — strip
                // the APIs before any page script runs, on this document and every
                // future one. Re-applied automatically since ReinitEngine rebuilds.
                if (cfg.CfgWebRtcDisableFully)
                {
                    await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcKillScript);
                    ActivityLog.General("WebRTC: APIs stripped (full disable active)");
                }

                var s = web.CoreWebView2.Settings;
                s.AreDevToolsEnabled = true;
                s.IsStatusBarEnabled = true;
                s.IsZoomControlEnabled = true;

                if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
                    s.UserAgent = cfg.UserAgent;

                // Serve the app folder at a virtual host so the home page loads the
                // wallpaper SVG as a normal resource — not inlined into the page (a big
                // inline data URI froze the renderer). The SVG sits next to the exe.
                try
                {
                    web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "home.aloha",
                        System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                        CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { }

                HookEvents();
                GoHome();
                ApplyBrowserScrollbars();
            }
            catch (Exception ex)
            {
                // Write the FULL detail (stack + inner exceptions) to a file — it's
                // readable even when the box ends up hidden behind the borderless
                // full-screen window — then force the box itself to the top.
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                            "aloha_crash.txt"),
                        "WebView2 init failed:\r\n\r\n" + ex.ToString());
                }
                catch { }
                using (var top = new Form
                {
                    TopMost = true,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-3000, -3000),
                    Size = new System.Drawing.Size(1, 1)
                })
                {
                    top.Show();
                    MessageBox.Show(top,
                        "WebView2 failed to start.\r\n\r\nDetail:\r\n" + ex.Message,
                        "Aloha", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ── site peers: every distinct remote host the current page connects to,
        //    harvested from WebResourceRequested, cleared on each top-level navigation.
        //    Skips our own host (loopback + the home.aloha virtual host). The Site map
        //    panel resolves these to IPs.
        private readonly System.Collections.Generic.HashSet<string> sitePeers =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object peerLock = new object();

        private void ClearPeers() { lock (peerLock) sitePeers.Clear(); }

        private void CollectPeer(string uri)
        {
            try
            {
                string h = new Uri(uri).Host;
                if (string.IsNullOrEmpty(h)) return;
                if (h.Equals("home.aloha", StringComparison.OrdinalIgnoreCase)) return;
                if (h == "localhost" || h == "127.0.0.1" || h == "::1") return;
                lock (peerLock) sitePeers.Add(h);
            }
            catch { }
        }

        // thread-safe snapshot of the hosts seen on the current page
        public string[] SitePeersSnapshot()
        {
            lock (peerLock)
            {
                var arr = new string[sitePeers.Count];
                sitePeers.CopyTo(arr);
                return arr;
            }
        }

        private void HookEvents()
        {
            web.CoreWebView2.SourceChanged += (o, a) =>
                textBox1.Text = web.Source?.ToString() ?? "";

            web.CoreWebView2.NavigationStarting += (o, a) =>
            {
                ClearPeers();
                SetStatus("LOADING\r\n" + a.Uri);
                ActivityLog.Network("resolve/navigate -> " + a.Uri);
            };

            web.CoreWebView2.NavigationCompleted += (o, a) =>
            {
                SetStatus((a.IsSuccess ? "OK " : "ERR ") + (int)a.WebErrorStatus +
                          "\r\n" + (web.Source?.ToString() ?? ""));
                if (a.IsSuccess) ActivityLog.Network("loaded OK");
                else ActivityLog.Network("FAILED status=" + a.WebErrorStatus);
            };

            // request/response visibility — feeds both green panes
            web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.CoreWebView2.WebResourceRequested += (o, a) =>
            {
                try
                {
                    CollectPeer(a.Request.Uri);
                    ActivityLog.Proxy("REQ  " + a.Request.Method + "  " + a.Request.Uri);
                    foreach (var h in a.Request.Headers)
                        ActivityLog.Network("  > " + h.Key + ": " + h.Value);
                }
                catch { }
            };
            web.CoreWebView2.WebResourceResponseReceived += (o, a) =>
            {
                try
                {
                    ActivityLog.Proxy("RESP " + a.Response.StatusCode + "  " + a.Request.Uri);
                    foreach (var h in a.Response.Headers)
                        ActivityLog.Network("  < " + h.Key + ": " + h.Value);
                }
                catch { }
            };
        }

        private void SetStatus(string text)
        {
            if (textBox2.IsHandleCreated) textBox2.Text = text;
        }

        private bool Ready => web?.CoreWebView2 != null;

        private void Go(string input)
        {
            if (!Ready || string.IsNullOrWhiteSpace(input)) return;
            input = input.Trim();
            if (input.Contains("://")) web.CoreWebView2.Navigate(input);
            else if (input.Contains(".")) web.CoreWebView2.Navigate("http://" + input);
            else web.CoreWebView2.Navigate(
                "https://metager.de/meta/meta.ger3?eingabe=" + Uri.EscapeDataString(input));
        }

        // ── buttons ──
        private void button1_Click(object sender, EventArgs e)
        { if (Ready && web.CoreWebView2.CanGoBack) web.CoreWebView2.GoBack(); }
        private void button2_Click(object sender, EventArgs e)
        { if (Ready && web.CoreWebView2.CanGoForward) web.CoreWebView2.GoForward(); }
        private void button3_Click(object sender, EventArgs e)
        { GoHome(); }

        // Home = a full-bleed wallpaper page. The image is embedded in the exe and
        // inlined as a data URI, so the single-file build needs no loose image file.
        // Home = the palm-camo wallpaper. The SVG is a loose file copied next to the exe
        // and served over a virtual host (https://home.aloha/, mapped in InitWebAsync), so
        // the page itself stays a few hundred bytes. The image is NOT inlined into the page
        // — a ~376 KB inline data URI is what froze the renderer before.
        private void GoHome()
        {
            if (!Ready) return;
            string css;
            if (cfg.WallpaperEnabled)
            {
                string file = string.IsNullOrWhiteSpace(cfg.Wallpaper) ? "blueprint.svg" : cfg.Wallpaper;
                // tiled at a FIXED pixel size — squares stay constant as the window
                // resizes (graph-paper behaviour), the grid just reveals more cells.
                css = "background-color:#060c14;" +
                      "background-image:url('https://home.aloha/" + file + "');" +
                      "background-repeat:repeat;background-size:160px 160px";
            }
            else
            {
                css = "background-color:#0d0d12";   // wallpaper off -> plain dark home page
            }
            web.CoreWebView2.NavigateToString(
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<style>html,body{margin:0;height:100%;background:#0d0d12;overflow:hidden}" +
                ".bg{position:fixed;top:0;left:0;right:0;bottom:0;" + css + "}" +
                "</style></head><body><div class=\"bg\"></div></body></html>");
        }

        // ── menu layout ───────────────────────────────────────────────
        // Edit:     Find in this page · DevTools · Browser Style · Browser Configuration
        // Options:  Proxy · Network · Live headers
        // Navigate: Network map · Console · Site map
        private BrowserStylePanel styleWin;
        private ToolStripMenuItem browserStyleItem;
        private ToolStripMenuItem interceptItem, rawTcpItem, netMapItem;
        private void SetupMenus()
        {
            // Browser Style entry (opens the appearance panel)
            browserStyleItem = new ToolStripMenuItem("Browser Style...");
            browserStyleItem.Click += (s, e) =>
            {
                if (styleWin == null || styleWin.IsDisposed)
                    styleWin = new BrowserStylePanel(cfg, GoHome);
                styleWin.Show();
                styleWin.BringToFront();
            };

            // "Find in this page" had no handler — make it behave like Ctrl+F
            findInThisPageToolStripMenuItem.Click += findInThisPageToolStripMenuItem_Click;
            siteMapToolStripMenuItem.Click += siteMapToolStripMenuItem_Click;

            // re-parent everything into the layout above (handlers ride along on the items)
            editToolStripMenuItem.DropDownItems.Clear();
            optionsToolStripMenuItem.DropDownItems.Clear();
            navigateToolStripMenuItem.DropDownItems.Clear();

            editToolStripMenuItem.DropDownItems.Add(findInThisPageToolStripMenuItem);
            editToolStripMenuItem.DropDownItems.Add(devToolsToolStripMenuItem);
            editToolStripMenuItem.DropDownItems.Add(browserStyleItem);
            editToolStripMenuItem.DropDownItems.Add(browserConfigToolStripMenuItem);

            optionsToolStripMenuItem.DropDownItems.Add(proxyToolStripMenuItem);
            optionsToolStripMenuItem.DropDownItems.Add(networkToolStripMenuItem);
            optionsToolStripMenuItem.DropDownItems.Add(liveHeadersToolStripMenuItem);
            optionsToolStripMenuItem.DropDownItems.Add(interceptItem);
            optionsToolStripMenuItem.DropDownItems.Add(rawTcpItem);

            navigateToolStripMenuItem.DropDownItems.Add(netMapItem);
            navigateToolStripMenuItem.DropDownItems.Add(consoleToolStripMenuItem);
            navigateToolStripMenuItem.DropDownItems.Add(siteMapToolStripMenuItem);
        }

        // Find in this page == Ctrl+F : focus the page and let the engine open its find bar.
        private void findInThisPageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Ready) return;
            try { web.Focus(); SendKeys.Send("^f"); } catch { }
        }
        private void button4_Click(object sender, EventArgs e)
        { if (Ready) web.CoreWebView2.Navigate("http://localhost"); }
        private void button5_Click(object sender, EventArgs e)
        { if (Ready) web.CoreWebView2.Reload(); }
        private void button6_Click(object sender, EventArgs e)
        {
            if (!Ready) return;
            string url = web.Source?.ToString() ?? "";
            textBox1.Text = url;
            if (!string.IsNullOrEmpty(url))
            {
                try { Clipboard.SetText(url); SetStatus("COPIED\r\n" + url); } catch { }
            }
        }
        private void button8_Click(object sender, EventArgs e)
        {
            try
            {
                using (var op = new OpenFileDialog())
                    if (op.ShowDialog() == DialogResult.OK && Ready)
                        web.CoreWebView2.Navigate(new Uri(op.FileName).AbsoluteUri);
            }
            catch { }
        }
        private void textBox1_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && textBox1.Text.Trim().Length > 0) Go(textBox1.Text);
        }

        // ── menu ──
        private void exitToolStripMenuItem_Click(object sender, EventArgs e) => Application.Exit();
        private void devToolsToolStripMenuItem_Click(object sender, EventArgs e)
        { if (Ready) web.CoreWebView2.OpenDevToolsWindow(); }
        private void infoToolStripMenuItem_Click(object sender, EventArgs e)
            => MessageBox.Show("Aloha Browser " + VERSION + "\r\nWebView2 (Chromium) shell\r\nLiberté", "Aloha");

        private ProxyPanel proxyWin;
        private NetworkPanel networkWin;
        private BrowserConfigPanel browserCfgWin;

        private void browserConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (browserCfgWin == null || browserCfgWin.IsDisposed)
                browserCfgWin = new BrowserConfigPanel(cfg, ReinitEngine);
            browserCfgWin.Show();
            browserCfgWin.BringToFront();
        }

        private void proxyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (proxyWin == null || proxyWin.IsDisposed)
                proxyWin = new ProxyPanel(cfg, ReinitEngine);
            proxyWin.Show();
            proxyWin.BringToFront();
        }
        private void networkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (networkWin == null || networkWin.IsDisposed)
                networkWin = new NetworkPanel(cfg, ReinitEngine);
            networkWin.Show();
            networkWin.BringToFront();
        }

        // Apply from a panel: re-create the engine with the new config.
        // WebView2 settings are set at init, so applying = reload engine.
        private async void ReinitEngine()
        {
            titleBar.Text = "Aloha Browser " + VERSION;
            ActivityLog.General("re-initialising engine with new config...");
            RemoveBrowserScrollbars();
            try
            {
                try { web.Dispose(); } catch { }
                this.Controls.Remove(web);
                await Task.Delay(300);        // let the old engine release the folder lock
                engineGen++;                  // new args -> fresh sibling user-data folder

                web = new Microsoft.Web.WebView2.WinForms.WebView2();
                ((System.ComponentModel.ISupportInitialize)(web)).BeginInit();
                web.Location = new Point(0, textBox1.Bottom);
                web.Size = new Size(this.ClientSize.Width, this.ClientSize.Height - textBox1.Bottom);
                web.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                ((System.ComponentModel.ISupportInitialize)(web)).EndInit();
                ApplyEngineCreationProperties();   // folder + args before the handle -> single engine
                this.Controls.Add(web);
                RaiseChrome();
                await InitWebAsync();
                TidyEngineFolders(engineGen);   // drop older ephemeral folders
            }
            catch (Exception ex)
            {
                ActivityLog.General("re-init failed: " + ex.Message);
                MessageBox.Show("Engine re-init failed:\r\n\r\n" + ex.Message,
                    "Aloha", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private ConsoleWindow consoleWin;
        private LiveHeadersPanel liveWin;
        private InterceptPanel interceptWin;
        private RawTcpPanel rawTcpWin;
        private NetGraphPanel netMapWin;
        private readonly InterceptProxy interceptProxy = new InterceptProxy();

        private void consoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (consoleWin == null || consoleWin.IsDisposed)
                consoleWin = new ConsoleWindow(cfg, OpenUrl);
            consoleWin.Show();
            consoleWin.BringToFront();
        }

        private void liveHeadersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (liveWin == null || liveWin.IsDisposed)
                liveWin = new LiveHeadersPanel();
            liveWin.Show();
            liveWin.BringToFront();
        }

        private void interceptToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (interceptWin == null || interceptWin.IsDisposed)
                interceptWin = new InterceptPanel(cfg, ReinitEngine, interceptProxy);
            interceptWin.Show();
            interceptWin.BringToFront();
        }

        private void rawTcpToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (rawTcpWin == null || rawTcpWin.IsDisposed)
                rawTcpWin = new RawTcpPanel();
            rawTcpWin.Show();
            rawTcpWin.BringToFront();
        }

        // ── file explorer: a borderless overlay floating above the web engine,
        //    toggled by the New File toolbar button; that button shows the
        //    selected name ("New File : <name>", left-aligned, scrolling) ──
        private FileExplorerWindow overlay;
        private Timer  b8Timer;
        private string b8Text = "New File...";
        private float  b8Off;
        private bool   b8Overflow;

        private void fileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (overlay == null || overlay.IsDisposed)
            {
                overlay = new FileExplorerWindow(OpenFileFromExplorer) { Owner = this };
                overlay.SelectionChanged += OnFileViewSelection;
                if (b8Timer == null) SetupButton8Marquee();
                // keep the overlay glued to the button line as the window moves/resizes
                this.Resize         += (s, ev) => { if (overlay != null && overlay.Visible) PositionOverlayFull(); };
                this.LocationChanged += (s, ev) => { if (overlay != null && overlay.Visible) PositionOverlayKeepSize(); };
            }
            if (overlay.Visible)
            {
                overlay.Hide();
                SetButton8Text("New File...");
                button8.Invalidate();
            }
            else
            {
                PositionOverlayFull();
                overlay.Show();
                overlay.FocusView();
                button8.Invalidate();
            }
        }

        // overlay top-left pinned to (button8.Left, top of the URL bar) — it sits
        // OVER the URL bar; right edge follows the URL bar's end; fills down to the footer
        private void PositionOverlayFull()
        {
            if (overlay == null) return;
            int top   = (textBox1 != null ? textBox1.Top : 73) - 2;   // overlap the button's bottom edge (covers it)
            int left  = button8.Left;
            int right = textBox1 != null ? textBox1.Right : ClientSize.Width;
            int w = Math.Max(overlay.MinimumSize.Width,  right - left);
            int h = Math.Max(overlay.MinimumSize.Height, ClientSize.Height / 2);   // open at half the form's height
            Point tl = this.PointToScreen(new Point(left, top));
            overlay.Bounds = new Rectangle(tl.X, tl.Y, w, h);
        }

        // keep the user's size, just re-pin the top-left to the button line / URL-bar top
        private void PositionOverlayKeepSize()
        {
            if (overlay == null) return;
            int top = (textBox1 != null ? textBox1.Top : 73) - 2;
            overlay.Location = this.PointToScreen(new Point(button8.Left, top));
        }

        private void OnFileViewSelection(string name)
        {
            SetButton8Text(string.IsNullOrEmpty(name) ? "New File..." : "New File : " + name);
        }

        // open the chosen file in the browser; tuck the overlay away so the page shows
        private void OpenFileFromExplorer(string uri)
        {
            OpenUrl(uri);
            if (overlay != null && overlay.Visible) overlay.Hide();
            SetButton8Text("New File...");
        }

        // ── button8 ("New File...") drawn by us so the label can scroll ──
        private void SetupButton8Marquee()
        {
            button8.Text = "";                 // we paint the text ourselves
            button8.Paint += Button8_Paint;
            b8Timer = new Timer { Interval = 40 };
            b8Timer.Tick += (s, e) => { b8Off += 1.2f; button8.Invalidate(); };
        }

        private void SetButton8Text(string t)
        {
            b8Text = t ?? "";
            b8Off = 0f;
            int avail = button8.Width - 12;
            int tw = TextRenderer.MeasureText(b8Text, button8.Font).Width;
            b8Overflow = tw > avail;
            if (b8Timer != null)
            {
                if (b8Overflow) { if (!b8Timer.Enabled) b8Timer.Start(); }
                else b8Timer.Stop();
            }
            button8.Invalidate();
        }

        private void Button8_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = button8.ClientRectangle;
            Color fg = Color.LimeGreen;
            int pad = 6, avail = r.Width - pad * 2;
            Size tsz = TextRenderer.MeasureText(g, b8Text, button8.Font);
            if (!b8Overflow)
            {
                var tr = new Rectangle(r.X + pad, r.Y, r.Width - pad, r.Height);
                TextRenderer.DrawText(g, b8Text, button8.Font, tr, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else
            {
                int gap = 50, period = tsz.Width + gap;
                int o = ((int)b8Off) % period;
                int y = r.Y + (r.Height - tsz.Height) / 2;
                g.SetClip(new Rectangle(r.X + pad, r.Y, avail, r.Height));
                TextRenderer.DrawText(g, b8Text, button8.Font, new Point(r.X + pad - o, y), fg, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, b8Text, button8.Font, new Point(r.X + pad - o + period, y), fg, TextFormatFlags.NoPrefix);
                g.ResetClip();
            }
        }

        private void networkMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (netMapWin == null || netMapWin.IsDisposed)
                netMapWin = new NetGraphPanel();
            netMapWin.Show();
            netMapWin.BringToFront();
        }

        private SiteMapPanel siteMapWin;
        private void siteMapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (siteMapWin == null || siteMapWin.IsDisposed)
                siteMapWin = new SiteMapPanel(SitePeersSnapshot, OpenUrl);
            siteMapWin.Show();
            siteMapWin.BringToFront();
        }

        // navigate the browser to a url (called by the console's 'open')
        public void OpenUrl(string url)
        {
            if (!Ready || string.IsNullOrWhiteSpace(url)) return;
            if (this.InvokeRequired) { this.BeginInvoke((Action)(() => OpenUrl(url))); return; }
            web.CoreWebView2.Navigate(url);
            this.BringToFront();
        }

        // ── Step D: modern browser scrollbars (behind the config toggle) ──
        private void ApplyBrowserScrollbars()
        {
            RemoveBrowserScrollbars();
            if (cfg.CfgWin9xBrowserScrollbars && Ready)
            {
                vScroll = new WebScrollBar(false);
                hScroll = new WebScrollBar(true);
                scrollCorner = new Panel { BackColor = Color.FromArgb(0xEC, 0xEC, 0xEE), Cursor = Cursors.SizeNWSE };
                scrollCorner.MouseDown += Corner_MouseDown;
                scrollCorner.MouseMove += Corner_MouseMove;
                scrollCorner.MouseUp += Corner_MouseUp;
                this.Controls.Add(vScroll);
                this.Controls.Add(hScroll);
                this.Controls.Add(scrollCorner);
                vScroll.BringToFront();
                hScroll.BringToFront();
                scrollCorner.BringToFront();
                vScroll.Attach(web.CoreWebView2);
                hScroll.Attach(web.CoreWebView2);
                // No CSS hiding: LayoutWeb oversizes the WebView2 so Chromium's
                // own scrollbars fall under our custom bars at the window edge.
                ActivityLog.General("modern browser scrollbars: on");
            }
            LayoutWeb();
            RaiseChrome();
        }

        private void RemoveBrowserScrollbars()
        {
            if (vScroll != null) { vScroll.Detach(); this.Controls.Remove(vScroll); vScroll.Dispose(); vScroll = null; }
            if (hScroll != null) { hScroll.Detach(); this.Controls.Remove(hScroll); hScroll.Dispose(); hScroll = null; }
            if (scrollCorner != null) { this.Controls.Remove(scrollCorner); scrollCorner.Dispose(); scrollCorner = null; }
        }

        // Lay out the WebView2 (and the modern bars, when on). Trick: the page
        // fills the FULL width/height, so Chromium's own scrollbars render at the
        // very edge — then we lay our custom bars OVER that same 17px strip. The
        // native bars are still there and still scroll; they're just hidden under
        // ours. No CSS injection, no document-created script, no engine reload.
        private const int SB = 17;   // native Chromium scrollbar thickness
        private const int FT = 17;   // footer height — matches the 17px scrollbar band
        private void LayoutWeb()
        {
            if (web == null) return;
            web.Anchor = AnchorStyles.None;          // LayoutWeb fully controls the web bounds
            int top = textBox1 != null ? textBox1.Bottom : 93;
            int w = this.ClientSize.Width;
            int h = this.ClientSize.Height;
            int bottom = h - FT;                     // leave room for the footer strip
            int webH = Math.Max(0, bottom - top);
            bool bars = cfg.CfgWin9xBrowserScrollbars && vScroll != null;
            web.SetBounds(0, top, w, webH);          // full size up to the footer; native bars at the edge
            if (bars)
            {
                vScroll.SetBounds(w - SB, top, SB, Math.Max(0, webH - SB));   // over Chromium's vertical
                hScroll.SetBounds(0, bottom - SB, Math.Max(0, w - SB), SB);   // over Chromium's horizontal
                scrollCorner.SetBounds(w - SB, bottom - SB, SB, SB);
                vScroll.BringToFront();
                hScroll.BringToFront();
                scrollCorner.BringToFront();
            }
            if (footer != null) footer.BringToFront();
        }
    }
}
