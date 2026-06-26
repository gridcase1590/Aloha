using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Aloha.RingStoreCore;   // BookmarkManager, BookmarkBarHost, BookmarkButtonGlue

namespace Aloha
{
    public partial class Form1 : Form
    {
        public const string VERSION = "0.5.1";
        private const string HOME = "";

        private static readonly string UserDataFolder =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                "AlohaProfile");

        private int engineGen = 0;   // bumped each re-init -> fresh sibling user-data folder

        private NetConfig cfg;

        // bookmarks (pointer store + the ★ button anchored to the URL bar)
        private BookmarkManager _bookmarks;
        private BookmarkBarHost  _bmHost;
        private DownloadHistory _downloads;
        private static readonly string DownloadsPath =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                "AlohaDownloads.json");
        private static readonly string BookmarksPath =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                "AlohaBookmarks.json");

        // Step D: modern WebView2-driving scrollbars (behind the config toggle)
        private WebScrollBar vScroll, hScroll;
        private Panel scrollCorner;
        private const string HideBarsScript =
            "(function(){var s=document.createElement('style');" +
            "s.textContent='::-webkit-scrollbar{width:0 !important;height:0 !important;display:none !important}" +
            "html{scrollbar-width:none !important;-ms-overflow-style:none !important}';" +
            "(document.head||document.documentElement).appendChild(s);})();";

        private TabStrip tabStrip;          // the tab row, revealed by the Home/Tabs toggle
        private TabManagerWindow tabBoardWin;
        private bool tabsMode = false;      // true while the strip is showing

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
            InitTabStrip();
            SetupLoadBar();             // lime navigation progress line
            DefocusToolbarButtons();    // kill the blue focus glow on toolbar buttons

            // the red status box (top-right) summons the inspector, adjacent to the window
            textBox2.Cursor = Cursors.Hand;
            textBox2.Click += (s, e) => OpenInspectorAdjacent();

            // bookmarks: load the store, tell the glue how to read the current page,
            // and place the ★ at the right end of the URL bar (textBox1).
            _bookmarks = BookmarkManager.Load(BookmarksPath);
            _downloads = DownloadHistory.Load(DownloadsPath);
            BookmarkButtonGlue.GetCurrentPage = () => new[]
            {
                web != null && web.Source != null ? web.Source.ToString() : "",
                web != null && web.CoreWebView2 != null ? (web.CoreWebView2.DocumentTitle ?? "") : ""
            };
            _bmHost = new BookmarkBarHost(textBox1, _bookmarks, BookmarksPath);
            _bmHost.Attach();   // ★ lives at the right inside-edge of the URL bar
            _bmHost.SetVisible(cfg.ShowBookmarkButton);
            // the URL bar is anchored and only gets its final width after layout/show,
            // so re-place the ★ then (and whenever the window resizes the bar).
            this.Shown  += (s, e) => _bmHost.Reposition();
            this.Resize += (s, e) => _bmHost.Reposition();

            // remember window size across launches
            if (cfg.WinWidth >= this.MinimumSize.Width && cfg.WinHeight >= this.MinimumSize.Height)
            {
                this.StartPosition = FormStartPosition.CenterScreen;
                this.Size = new System.Drawing.Size(cfg.WinWidth, cfg.WinHeight);
            }
            this.FormClosing += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Normal)
                {
                    cfg.WinWidth = this.Width;
                    cfg.WinHeight = this.Height;
                }
                try { cfg.Save(); } catch { }
            };
        }

        // ── lime navigation progress line ────────────────────────────────
        // A 3px lime strip at the URL-bar / page seam. Eases toward ~90% while
        // a page loads, snaps to 100% on completion, then vanishes. Placed over
        // the URL bar's bottom edge (NOT over the WebView2) so it never hits the
        // control's airspace.
        private Panel loadBar;
        private Timer loadTimer;
        private double loadProg;
        private bool loadActive;

        private void SetupLoadBar()
        {
            loadBar = new Panel
            {
                Height = 3,
                Width = 0,
                BackColor = System.Drawing.Color.FromArgb(0x2E, 0xE0, 0x5C),
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            loadBar.Location = new System.Drawing.Point(0, Math.Max(0, textBox1.Bottom - 3));
            this.Controls.Add(loadBar);
            loadBar.BringToFront();

            loadTimer = new Timer { Interval = 30 };
            loadTimer.Tick += LoadBar_Tick;
        }

        private void LoadStart()
        {
            if (loadBar == null) return;
            loadActive = true;
            loadProg = 0.06;
            loadBar.Width = 0;
            loadBar.Visible = true;
            loadBar.BringToFront();
            if (!loadTimer.Enabled) loadTimer.Start();
        }

        private void LoadDone()
        {
            loadActive = false;   // the tick ramps to 100% then hides the bar
            if (loadBar != null && loadTimer != null && loadBar.Visible && !loadTimer.Enabled)
                loadTimer.Start();
        }

        private void LoadBar_Tick(object sender, EventArgs e)
        {
            if (loadBar == null) { if (loadTimer != null) loadTimer.Stop(); return; }
            int full = Math.Max(1, this.ClientSize.Width);
            if (loadActive)
            {
                if (loadProg < 0.9) loadProg += (0.9 - loadProg) * 0.10 + 0.004;
            }
            else
            {
                loadProg += (1.0 - loadProg) * 0.34 + 0.03;
                if (loadProg >= 0.999)
                {
                    loadBar.Visible = false;
                    loadBar.Width = 0;
                    loadProg = 0;
                    loadTimer.Stop();
                    return;
                }
            }
            loadBar.Width = (int)(full * Math.Min(1.0, loadProg));
        }

        // Toolbar buttons keep the OS blue focus glow after a click. Drop the
        // active control on click so the glow clears, and keep them out of the
        // tab order so they never grab focus from the keyboard either.
        private void DefocusToolbarButtons()
        {
            var btns = new[] { button1, button2, button3, button4, button5, button6, button7, button8 };
            foreach (var b in btns)
            {
                if (b == null) continue;
                b.TabStop = false;
                b.Click += (s, e) => { try { this.ActiveControl = null; } catch { } };
            }
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

        // Ctrl + left-click "open in a new tab" — for links, but also images, media and CSS
        // background images that aren't normally navigable. Resolves the target's URL,
        // cancels the default (so it never reaches the popup blocker), and posts it to the
        // host. isTrusted-gated so only real user clicks fire it. Runs in capture phase so
        // it wins over the site's own handlers.
        private const string CtrlOpenScript = @"
(function(){
  document.addEventListener('click', function(e){
    if(!e.ctrlKey || e.button!==0 || !e.isTrusted || e.shiftKey || e.altKey) return;
    var t = e.target, url = null;
    var a = (t && t.closest) ? t.closest('a[href]') : null;
    if(a && a.href) url = a.href;
    if(!url){
      var el = t;
      for(var i=0; i<5 && el; i++){
        var tag = el.tagName ? el.tagName.toLowerCase() : '';
        if((tag==='img'||tag==='video'||tag==='audio'||tag==='source'||tag==='embed') && el.src){ url = el.src; break; }
        try{
          var bg = getComputedStyle(el).backgroundImage;
          var m = bg && bg.match(/url\((.*?)\)/);
          if(m && m[1]){ url = m[1].replace(/^['""]|['""]$/g, ''); break; }
        }catch(_){}
        el = el.parentElement;
      }
    }
    if(url){
      try{ url = new URL(url, location.href).href; }catch(_){}
      e.preventDefault(); e.stopPropagation();
      try{ window.chrome.webview.postMessage('ctrlopen|'+url); }catch(_){}
    }
  }, true);
})();";
        private BevelPanel fIndentWide;
        private Label sizeReadout;
        private Panel zoomBar;
        private Label zoomReadout;
        private double webZoom = 1.0;

        private void SetupChrome()
        {
            titleBar.TextAlign = ContentAlignment.MiddleCenter;
            this.MinimumSize = new System.Drawing.Size(520, 360);   // floor for corner-drag resize
            SetupFooter();

            // Intercept proxy / Raw TCP / Network map items — created here (with their
            // handlers), but PLACED by SetupMenus so the menu reorg stays in one place.
            interceptItem = new ToolStripMenuItem("Intercept proxy...");
            interceptItem.Click += interceptToolStripMenuItem_Click;
            tamperItem = new ToolStripMenuItem("Request tamper...");
            tamperItem.Click += tamperToolStripMenuItem_Click;
            rawTcpItem = new ToolStripMenuItem("Raw TCP listener...");
            rawTcpItem.Click += rawTcpToolStripMenuItem_Click;
            popupsItem = new ToolStripMenuItem("Blocked popups...");
            popupsItem.Click += (s, e) =>
            {
                if (popupsWin == null || popupsWin.IsDisposed)
                    popupsWin = new PopupsWindow(popupQueue, uri => { if (Ready) web.CoreWebView2.Navigate(uri); });
                popupsWin.RefreshList();
                popupsWin.Show();
                popupsWin.BringToFront();
            };

            // Option panels (Network Scanning / Network Map).
            nmapItem = new ToolStripMenuItem("Network scanning...");
            nmapItem.Click += (s, e) =>
            {
                var w = new NmapScanPanel(cfg, () => { });
                w.Show();
                w.BringToFront();
            };
            instructionMapItem = new ToolStripMenuItem("Network Map...");
            instructionMapItem.Click += (s, e) =>
            {
                var w = new InstructionMapWindow(
                    web != null && web.CoreWebView2 != null ? web.CoreWebView2.Environment : null);
                w.Show();
                w.BringToFront();
            };

            vpnGateItem = new ToolStripMenuItem("VPN Gate...");
            vpnGateItem.Click += (s, e) =>
            {
                var w = new VpnGateWindow();
                w.Show();
                w.BringToFront();
            };

            torItem = new ToolStripMenuItem("Tor...");
            torItem.Click += (s, e) =>
            {
                var w = new TorWindow();
                // "Route browser" flips the proxy to Tor's SOCKS port and reloads
                w.OnRouteRequested += () =>
                {
                    cfg.ProxyScheme = "socks5";
                    cfg.ProxyHost = "127.0.0.1";
                    cfg.ProxyPort = 9050;
                    cfg.Save();
                    ReinitEngine();
                };
                w.Show(this);
                w.BringToFront();
            };

            advorItem = new ToolStripMenuItem("ADVOR...");
            advorItem.Click += (s, e) =>
            {
                var w = new AdvorWindow();
                w.Show(this);
                w.BringToFront();
            };

            this.FormClosed += (s, e) => { try { interceptProxy.Stop(); } catch { } };

            RaiseChrome();
        }

        // keep the footer (and its buttons) above the (re-created) WebView2
        private void RaiseChrome()
        {
            if (footer != null) footer.BringToFront();
        }

        // Ctrl+T new tab · Ctrl+W close active tab
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.T)) { NewTab(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.M)) { OpenTabBoard(); return true; }
            if (keyData == (Keys.Control | Keys.W))
            {
                if (tabStrip != null && tabStrip.ActiveIndex >= 0) OnTabClose(tabStrip.ActiveIndex);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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

        // blueprint.svg grid: 160px tile. Snapping to 16px (160/10) — a clean divisor
        // of the tile and half the 32px minor cell — keeps the form, viewport, and
        // wallpaper aligned with no half-square left at the right/bottom edges.
        private const int SvgTile = 160;
        private const int SvgMinorDivisions = 10;
        private const int SnapGrid = SvgTile / SvgMinorDivisions;   // = 16

        // Tor Browser letterboxing rounds the content window to a multiple of 200x100
        // (its anti-fingerprint "bucket"). When the Tor preset is on we snap to those
        // instead of the 16px blueprint cell. The preset is gated on snap-to-grid.
        private int SnapStepW { get { return (cfg != null && cfg.CfgTorLetterbox) ? 200 : SnapGrid; } }
        private int SnapStepH { get { return (cfg != null && cfg.CfgTorLetterbox) ? 100 : SnapGrid; } }

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
            int newW = cornerStartSize.Width + dx;
            int newH = cornerStartSize.Height + dy;

            // when snap-to-grid is on, size the WINDOW so the WebView viewport lands on a
            // 32px grid multiple with no remainder — that removes the half-cell at the
            // edges and the gray gap between the scrollbars and the form.
            if (cfg != null && cfg.CfgSnapViewportToGrid)
            {
                int chromeW = this.Width - this.ClientSize.Width;            // borders (0 on borderless)
                int chromeH = this.Height - this.ClientSize.Height;
                int top     = textBox1 != null ? textBox1.Bottom : 93;       // toolbar above the web
                int vChrome = top + FT;                                      // toolbar + footer strip

                int viewW = Math.Max(SnapStepW, ((newW - chromeW) / SnapStepW) * SnapStepW);
                int viewH = Math.Max(SnapStepH, ((newH - chromeH - vChrome) / SnapStepH) * SnapStepH);

                newW = viewW + chromeW;
                newH = viewH + vChrome + chromeH;
            }

            // grow/shrink from the bottom-right; top-left stays put. MinimumSize clamps the floor.
            this.Size = new System.Drawing.Size(newW, newH);
        }

        private bool snappingWindow;
        // Snap the whole window so the WebView viewport is an exact 32px grid multiple.
        // Used on startup / after Apply (the drag grip snaps live while dragging).
        private void SnapWindowToGrid()
        {
            if (snappingWindow) return;
            if (cfg == null || !cfg.CfgSnapViewportToGrid) return;
            if (this.WindowState == FormWindowState.Maximized) return;
            snappingWindow = true;
            try
            {
                int chromeW = this.Width - this.ClientSize.Width;
                int chromeH = this.Height - this.ClientSize.Height;
                int top     = textBox1 != null ? textBox1.Bottom : 93;
                int vChrome = top + FT;
                int viewW = Math.Max(SnapStepW, ((this.ClientSize.Width) / SnapStepW) * SnapStepW);
                int viewH = Math.Max(SnapStepH, ((this.ClientSize.Height - vChrome) / SnapStepH) * SnapStepH);
                this.Size = new System.Drawing.Size(viewW + chromeW, viewH + vChrome + chromeH);
            }
            finally { snappingWindow = false; }
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
        {            footer = new Panel { Dock = DockStyle.Bottom, Height = FT, BackColor = cFooter };

            // decorative "indents that don't do anything" — modern light-gray sunken wells
            fIndentWide = new BevelPanel { Left = 2, Top = 0, Height = 17, Width = 300, BackColor = cFooter };

            // functional raised modern buttons, far right — 17px tall to fill the slim footer
            btnMin   = MakeFooterButton("min",   () => this.WindowState = FormWindowState.Minimized);
            btnMax   = MakeFooterButton("max",   ToggleMaximize);
            btnClose = MakeFooterButton("close", () => this.Close());
            btnMin.Top = 0; btnMax.Top = 0; btnClose.Top = 0;

            footer.Controls.Add(fIndentWide);
            footer.Controls.Add(btnMin);
            footer.Controls.Add(btnMax);
            footer.Controls.Add(btnClose);

            // live window-size readout, LEFT-aligned at the start of the footer well
            sizeReadout = new Label
            {
                AutoSize = false, Left = 4, Top = 2, Height = 13, Width = 120,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = cFooter,
                ForeColor = Color.FromArgb(0x50, 0x50, 0x50),
                Font = new Font("Lucida Console", 7.5f, FontStyle.Bold)
            };
            fIndentWide.Controls.Add(sizeReadout);
            sizeReadout.BringToFront();

            // zoom controls live at the RIGHT end of the same footer well
            BuildZoomBar(fIndentWide);

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
            // long indent (holds the readouts) spans up to the buttons, tight like the button gaps
            fIndentWide.Width = Math.Max(40, btnMin.Left - fIndentWide.Left - 2);
            if (sizeReadout != null)
            {
                sizeReadout.Left = 4;   // left edge — the ★ moved to the URL bar, freeing this space
                sizeReadout.Text = this.ClientSize.Width + "\u00D7" + this.ClientSize.Height + " px";
            }
            if (zoomBar != null)
            {
                // pushed to the RIGHT end of the indent, tight against its edge (2px, like the buttons)
                zoomBar.Left = Math.Max(sizeReadout.Right + 12, fIndentWide.ClientSize.Width - zoomBar.Width - 2);
                zoomBar.Top  = 2;
                zoomBar.BringToFront();
            }
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
            var full = this.ClientRectangle;
            int top = textBox1 != null ? Math.Min(textBox1.Bottom, full.Height) : Math.Min(93, full.Height);

            // upper toolbar band keeps the original modern gradient (defined over the
            // full client height so the band looks exactly as before)
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                full,
                System.Drawing.Color.FromArgb(252, 252, 253),   // light grey, top
                System.Drawing.Color.FromArgb(216, 216, 220),   // darker grey, bottom
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(brush, new System.Drawing.Rectangle(0, 0, full.Width, top));
            }

            // everything below the toolbar — the web sits here, and when snap-to-grid
            // letterboxes it (esp. maximized) this flat #FAFAFB shows in the margin
            if (top < full.Height)
                using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xFA, 0xFA, 0xFB)))
                    e.Graphics.FillRectangle(fill, new System.Drawing.Rectangle(0, top, full.Width, full.Height - top));
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
                ProfileName = SafeProfileName(cfg.ProfileName),   // full profile tied to the proxy choice
                AdditionalBrowserArguments = string.IsNullOrWhiteSpace(browserArgs) ? null : browserArgs
            };
        }

        // WebView2 profile names: ASCII letters/digits and . _ - (max 64, not ending
        // in . or space). Keep a safe subset; "" or "default" -> the built-in Default
        // profile (null). Makes the ProxyPanel "profile" field a real, isolated browser
        // profile — separate cookies/cache per proxy identity (tor vs direct vs custom).
        private static string SafeProfileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (string.Equals(name.Trim(), "default", StringComparison.OrdinalIgnoreCase)) return null;
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name.Trim())
                if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-') sb.Append(ch);
            string p = sb.ToString().Trim('.', ' ');
            if (p.Length == 0) return null;
            return p.Length > 64 ? p.Substring(0, 64) : p;
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

                // restore persisted zoom and reflect it in the readout
                webZoom = (cfg.WebZoom > 0.0 && cfg.WebZoom <= 50.0) ? cfg.WebZoom : 1.0;
                try { web.ZoomFactor = webZoom; } catch { }
                if (zoomReadout != null) zoomReadout.Text = (int)Math.Round(webZoom * 100) + "%";

                // Full WebRTC disable (the flag is dead in modern Chromium) — strip
                // the APIs before any page script runs, on this document and every
                // future one. Re-applied automatically since ReinitEngine rebuilds.
                if (cfg.CfgWebRtcDisableFully)
                {
                    await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcKillScript);
                    ActivityLog.General("WebRTC: APIs stripped (full disable active)");
                }

                // Ctrl+click -> open the target (link / image / media) in a new tab
                try { await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CtrlOpenScript); } catch { }

                var s = web.CoreWebView2.Settings;
                s.AreDevToolsEnabled = true;
                s.IsStatusBarEnabled = true;
                s.IsZoomControlEnabled = true;

                if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
                    s.UserAgent = cfg.UserAgent;

                // live privacy/content settings: JavaScript, autofill, password save,
                // SmartScreen, DevTools, tracking prevention, UA. Runs after the lines
                // above so it wins (e.g. DevTools follows cfg.DisableDevTools).
                NetConfigWebView.ApplyLive(web.CoreWebView2, cfg);

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
            // block downloads when the option is on (cancels every download —
            // drive-by and manual; reads cfg live so the toggle needs no re-init).
            web.CoreWebView2.DownloadStarting += (o, a) =>
            {
                // suppress WebView2's built-in download popup (we show our own window)
                if (cfg.HideDownloadPopup) a.Handled = true;

                string url  = a.DownloadOperation != null ? a.DownloadOperation.Uri : "";
                string path = a.ResultFilePath ?? "";
                string name = !string.IsNullOrEmpty(path) ? System.IO.Path.GetFileName(path) : "";

                // block everything (drive-by and manual)
                if (cfg.BlockDownloads)
                {
                    a.Cancel = true;
                    ActivityLog.General("download blocked: " + url);
                    _downloads.Add(DownloadRecord.Blocked(url, name));
                    return;
                }

                // no automatic downloads: confirm each one
                if (cfg.ConfirmDownloads)
                {
                    var ans = MessageBox.Show(this,
                        "Download this file?\r\n\r\n" + (string.IsNullOrEmpty(name) ? "(file)" : name) + "\r\n" + url,
                        "Aloha \u2014 confirm download", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ans != DialogResult.Yes)
                    {
                        a.Cancel = true;
                        ActivityLog.General("download declined: " + url);
                        _downloads.Add(DownloadRecord.Cancelled(url, name));
                        return;
                    }
                }

                // optional fixed download folder
                if (!string.IsNullOrWhiteSpace(cfg.DownloadDir))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(cfg.DownloadDir);
                        string fn = string.IsNullOrEmpty(name) ? "download" : name;
                        a.ResultFilePath = System.IO.Path.Combine(cfg.DownloadDir, fn);
                        path = a.ResultFilePath;
                        name = System.IO.Path.GetFileName(path);
                    }
                    catch { }
                }

                // track it + follow progress to completion
                var op = a.DownloadOperation;
                long total = (op != null && op.TotalBytesToReceive.HasValue) ? (long)op.TotalBytesToReceive.Value : 0;
                var rec = DownloadRecord.Start(url, name, path, total);
                _downloads.Add(rec);
                ActivityLog.General("download started: " + url);

                if (op != null)
                {
                    op.BytesReceivedChanged += (s2, e2) =>
                    {
                        rec.ReceivedBytes = op.BytesReceived;
                        if (op.TotalBytesToReceive.HasValue) rec.TotalBytes = (long)op.TotalBytesToReceive.Value;
                        _downloads.RaiseChanged();
                    };
                    op.StateChanged += (s2, e2) =>
                    {
                        if (op.State == CoreWebView2DownloadState.Completed)        rec.State = "completed";
                        else if (op.State == CoreWebView2DownloadState.Interrupted) rec.State = "interrupted";
                        else                                                        rec.State = "in progress";
                        rec.ReceivedBytes = op.BytesReceived;
                        if (op.State != CoreWebView2DownloadState.InProgress)
                            rec.FinishedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                        _downloads.Save();
                        _downloads.RaiseChanged();
                    };
                }
            };

            web.CoreWebView2.SourceChanged += (o, a) =>
            {
                string src = web.Source?.ToString() ?? "";
                // home/new-tab render via NavigateToString -> Source is "about:blank";
                // show an empty bar there instead of surfacing "about:blank".
                textBox1.Text = src.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ? "" : src;
                UpdateActiveTabTitle();
            };

            web.CoreWebView2.NavigationStarting += (o, a) =>
            {
                LoadStart();
                ClearPeers();
                SetStatus("LOADING\r\n" + a.Uri);
                ActivityLog.Network("resolve/navigate -> " + a.Uri);
            };

            web.CoreWebView2.NavigationCompleted += (o, a) =>
            {
                LoadDone();
                SetStatus((a.IsSuccess ? "OK " : "ERR ") + (int)a.WebErrorStatus +
                          "\r\n" + (web.Source?.ToString() ?? ""));
                if (a.IsSuccess) ActivityLog.Network("loaded OK");
                else ActivityLog.Network("FAILED status=" + a.WebErrorStatus);
            };

            // tab-engine capture hook: pages that try to open a NEW OS window
            // (target=_blank, window.open, ad/redirect popups) are caught back
            // INTO Aloha instead of escaping the shell with none of our flags or
            // proxy. for now the URL loads in the CURRENT view; once the tab strip
            // exists, this single line becomes "spawn a new tab" instead.
            web.CoreWebView2.NewWindowRequested += (o, a) =>
            {
                a.Handled = true;                       // never let it escape into an OS window
                if (string.IsNullOrEmpty(a.Uri)) { ActivityLog.Network("new-window: no uri"); return; }

                // A user gesture (target=_blank, middle-click, window.open from a click)
                // opens in a new Aloha tab. Script-spawned popups are still caught and
                // queued for review in the Popups window.
                if (a.IsUserInitiated)
                {
                    NewTab(a.Uri);
                    ActivityLog.Network("new tab (link) -> " + a.Uri);
                    return;
                }
                popupQueue.Add(new PopupEntry(DateTime.Now, a.Uri));
                SetStatus("BLOCKED POPUP\r\n" + a.Uri);
                popupsWin?.RefreshList();
                ActivityLog.Network("blocked popup -> " + a.Uri);
            };

            // request/response visibility — feeds both green panes
            web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.CoreWebView2.WebResourceRequested += (o, a) =>
            {
                try
                {
                    var core = o as CoreWebView2;
                    if (core != null) TamperEngine.Apply(core.Environment, a);
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

            // ctrl+click bridge: CtrlOpenScript posts "ctrlopen|<url>" -> open it in a tab
            web.CoreWebView2.WebMessageReceived += (o, a) =>
            {
                try
                {
                    string m = a.TryGetWebMessageAsString();
                    if (!string.IsNullOrEmpty(m) && m.StartsWith("ctrlopen|", StringComparison.Ordinal))
                    {
                        string url = m.Substring("ctrlopen|".Length);
                        if (!string.IsNullOrEmpty(url)) NewTab(url);
                    }
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
            // the home grid is our blank page — route the blank/home schemes to it
            string low = input.ToLowerInvariant();
            if (low == "about:blank" || low == "about:home" || low == "aloha:home") { GoHome(); return; }
            if (input.Contains("://") || HasDirectScheme(input))
                web.CoreWebView2.Navigate(input);
            else if (input.Contains(".") && !input.Contains(" "))
                web.CoreWebView2.Navigate("http://" + input);
            else
                web.CoreWebView2.Navigate(SearchUrl(input));
        }

        // Schemes that are real navigations, not search text. Without this "about:blank"
        // has no "://" and no "." so it fell through to the search engine (which is why a
        // closed first tab tried to *search* for "about:blank").
        private static readonly string[] DirectSchemes =
            { "about:", "view-source:", "data:", "file:", "blob:", "chrome:", "edge:" };
        private static bool HasDirectScheme(string s)
        {
            foreach (var p in DirectSchemes)
                if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Search engine is configurable in Browser Configuration: a query URL with {q}
        // (e.g. https://www.startpage.com/sp/search?q={q}). Blank = auto, which uses
        // Startpage's .onion service when the proxy points at Tor's SOCKS port so the
        // query never leaves the Tor network, and the clearnet endpoint otherwise.
        private string SearchUrl(string query)
        {
            string q = Uri.EscapeDataString(query);
            string tmpl = cfg != null ? cfg.SearchTemplate : null;
            if (!string.IsNullOrWhiteSpace(tmpl) && tmpl.Contains("{q}"))
                return tmpl.Replace("{q}", q);

            bool onTor = cfg != null && cfg.ProxyPort == 9050
                         && !string.IsNullOrEmpty(cfg.ProxyScheme)
                         && cfg.ProxyScheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
            return (onTor ? NetConfig.SearchStartpageOnion : NetConfig.SearchStartpage).Replace("{q}", q);
        }

        // ── buttons ──
        private void button1_Click(object sender, EventArgs e)
        { if (Ready && web.CoreWebView2.CanGoBack) web.CoreWebView2.GoBack(); }
        private void button2_Click(object sender, EventArgs e)
        { if (Ready && web.CoreWebView2.CanGoForward) web.CoreWebView2.GoForward(); }
        private void button3_Click(object sender, EventArgs e)
        { ToggleTabs(); }

        // tabs (stage 1: the strip UI + toggle; the per-tab WebView2 engine is stage 2).
        // home navigation now lives on the "blank" menu item, so the Home button is free
        // to double as the Tabs toggle.
        private void InitTabStrip()
        {
            tabStrip = new TabStrip();
            tabStrip.Visible = false;                       // hidden until toggled
            tabStrip.Font = button3.Font;                   // match the toolbar buttons' text exactly
            tabStrip.BackColor = this.BackColor;            // blend with the toolbar row (no band)
            tabStrip.SetBounds(button3.Right, button3.Top,  // sits in the toolbar row, sizes to its tabs
                               1, button3.Height);
            tabStrip.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            tabStrip.ActivateRequested += OnTabActivate;
            tabStrip.CloseRequested += OnTabClose;
            Controls.Add(tabStrip);
            tabStrip.BringToFront();
            tabStrip.AddTab("Home", web);                   // tab 0 = the existing webview
            tabStrip.SetActive(0);
        }

        private void ToggleTabs()
        {
            tabsMode = !tabsMode;
            button3.Text = tabsMode ? "Tabs" : "Home";
            if (tabStrip != null) tabStrip.Visible = tabsMode;
            button4.Visible = button5.Visible = button6.Visible =
                button7.Visible = button8.Visible = !tabsMode;   // tab buttons take the row
            if (tabsMode && tabStrip != null) tabStrip.BringToFront();
        }

        // ── Stage 2: per-tab WebView2 engine ──────────────────────────────────
        // Each tab is a real WebView2 (a "buffer"); they all share ONE user-data
        // folder, so they live in a single browser process group — which is exactly
        // what the Network Map snapshots, so every tab's renderers show up there
        // automatically. Switching tabs reassigns the `web` pointer (so all ~58
        // web.CoreWebView2 call sites hit the active buffer), shows that view, hides
        // and suspends the rest. New tabs reuse the proven InitWebAsync path.
        private bool tabBusy = false;

        private int IndexOfView(object v)
        {
            if (tabStrip == null || v == null) return -1;
            for (int k = 0; k < tabStrip.Count; k++)
                if (object.ReferenceEquals(tabStrip.ViewAt(k), v)) return k;
            return -1;
        }

        // synchronous: show `target`, hide every other tab view, point `web` at it.
        private void ShowTabView(Microsoft.Web.WebView2.WinForms.WebView2 target)
        {
            if (target == null || tabStrip == null) return;
            for (int k = 0; k < tabStrip.Count; k++)
            {
                var v = tabStrip.ViewAt(k) as Microsoft.Web.WebView2.WinForms.WebView2;
                if (v != null) v.Visible = object.ReferenceEquals(v, target);
            }
            web = target;                       // all call sites now hit the active buffer
            RaiseChrome();
            try
            {
                string src = web.Source != null ? web.Source.ToString() : "";
                textBox1.Text = src.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ? "" : src;
            }
            catch { }
            try { if (web.CoreWebView2 != null) web.CoreWebView2.Resume(); } catch { }
            ApplyBrowserScrollbars();           // custom scrollbars re-attach to the active view
        }

        private async void OnTabActivate(int i)
        {
            if (tabStrip == null || i < 0 || i >= tabStrip.Count) return;
            var target = tabStrip.ViewAt(i) as Microsoft.Web.WebView2.WinForms.WebView2;
            if (target == null) return;
            var outgoing = web;
            ShowTabView(target);
            tabStrip.SetActive(i);
            UpdateActiveTabTitle();
            // sleep the tab we just left (must be hidden first, which ShowTabView did)
            if (!object.ReferenceEquals(outgoing, target) && outgoing != null && outgoing.CoreWebView2 != null)
            {
                try { await outgoing.CoreWebView2.TrySuspendAsync(); } catch { }
            }
        }

        // Never leaves zero tabs: closing the last one resets it to about:blank instead
        // of removing it. Any other tab is disposed and a neighbour is activated.
        private void OnTabClose(int i)
        {
            if (tabStrip == null || i < 0 || i >= tabStrip.Count) return;
            if (tabStrip.Count == 1)
            {
                GoHome();                       // home grid, not a white about:blank
                tabStrip.SetTitle(0, "new tab");
                tabStrip.SetActive(0);
                return;
            }

            var view = tabStrip.ViewAt(i) as Microsoft.Web.WebView2.WinForms.WebView2;
            bool wasActive = object.ReferenceEquals(view, web);
            int next = (i >= tabStrip.Count - 1) ? i - 1 : i;   // neighbour index after removal
            tabStrip.RemoveTab(i);
            if (wasActive)
            {
                var nv = tabStrip.ViewAt(next) as Microsoft.Web.WebView2.WinForms.WebView2;
                ShowTabView(nv);
            }
            int ai = IndexOfView(web);          // re-sync the strip highlight to the live view
            if (ai >= 0) tabStrip.SetActive(ai);

            if (view != null && !object.ReferenceEquals(view, web))
            {
                try { this.Controls.Remove(view); } catch { }
                try { view.Dispose(); } catch { }
            }
        }

        // a bare WebView2 control sized/anchored exactly like the designer `web`
        private Microsoft.Web.WebView2.WinForms.WebView2 NewTabControl()
        {
            var v = new Microsoft.Web.WebView2.WinForms.WebView2();
            ((System.ComponentModel.ISupportInitialize)v).BeginInit();
            v.Location = new Point(0, textBox1.Bottom);
            v.Size = new Size(this.ClientSize.Width, this.ClientSize.Height - textBox1.Bottom);
            v.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            ((System.ComponentModel.ISupportInitialize)v).EndInit();
            return v;
        }

        private async void NewTab(string url = null)
        {
            if (tabStrip == null || tabBusy) return;
            tabBusy = true;
            try
            {
                var outgoing = tabStrip.ActiveView as Microsoft.Web.WebView2.WinForms.WebView2;

                var v = NewTabControl();
                int idx = tabStrip.AddTab("new tab", v);   // add to the strip BEFORE init so
                web = v;                                    // title updates target the new tab
                ApplyEngineCreationProperties();            // SAME folder -> shared environment
                this.Controls.Add(v);                       // handle created here, uses props
                ShowTabView(v);
                tabStrip.SetActive(idx);
                if (!tabsMode) ToggleTabs();                // reveal the strip so tabs are visible

                await InitWebAsync();                       // ensure + configure + hook + GoHome on v
                if (!string.IsNullOrEmpty(url))             // ctrl-clicked target overrides Home
                    try { web.CoreWebView2.Navigate(url); } catch { }

                if (outgoing != null && !object.ReferenceEquals(outgoing, v) && outgoing.CoreWebView2 != null)
                    try { await outgoing.CoreWebView2.TrySuspendAsync(); } catch { }
            }
            finally { tabBusy = false; }
        }

        // ---- Tab Board (tab manager window) ----
        private void OpenTabBoard()
        {
            if (tabBoardWin != null && !tabBoardWin.IsDisposed)
            {
                try { tabBoardWin.Activate(); tabBoardWin.BringToFront(); } catch { }
                return;
            }
            tabBoardWin = new TabManagerWindow(
                SnapshotTabs,
                EngineLabel,
                i => { OnTabActivate(i); try { this.Activate(); this.BringToFront(); } catch { } },
                i => OnTabClose(i));
            tabBoardWin.Show();
        }

        private List<TabManagerWindow.TabInfo> SnapshotTabs()
        {
            var outp = new List<TabManagerWindow.TabInfo>();
            if (tabStrip == null) return outp;
            int active = tabStrip.ActiveIndex;
            for (int k = 0; k < tabStrip.Count; k++)
            {
                var v = tabStrip.ViewAt(k) as Microsoft.Web.WebView2.WinForms.WebView2;
                string url = "";
                try { url = (v != null && v.Source != null) ? v.Source.ToString() : ""; } catch { }
                outp.Add(new TabManagerWindow.TabInfo { Index = k, Host = HostOf(url), Url = url, Active = (k == active) });
            }
            return outp;
        }

        private static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "new tab";
            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return "new tab";
            if (url.IndexOf("home.aloha", StringComparison.OrdinalIgnoreCase) >= 0) return "home";
            try { return new Uri(url).Host; } catch { return url; }
        }

        private string EngineLabel()
        {
            try
            {
                if (cfg != null && cfg.CfgUseInterceptProxy) return "INTERCEPT";
                string sch = (cfg != null ? cfg.ProxyScheme : null);
                sch = (sch ?? "none").Trim().ToLowerInvariant();
                if (cfg == null || sch == "none" || sch.Length == 0 || string.IsNullOrEmpty(cfg.ProxyHost)) return "DIRECT";
                if (cfg.ProxyPort == 9050 || cfg.ProxyPort == 9150) return "TOR " + cfg.ProxyHost + ":" + cfg.ProxyPort;
                return sch.ToUpperInvariant() + " " + cfg.ProxyHost + ":" + cfg.ProxyPort;
            }
            catch { return "DIRECT"; }
        }

        // Clear the whole browser's HTTP cache. The DevTools Network domain command
        // is available on every WebView2 build (no Profile-API version dependency),
        // and the cache is shared across tabs, so one call clears it for all of them.
        private async void ClearCache()
        {
            if (web?.CoreWebView2 == null) { ActivityLog.General("clear cache: engine not ready"); return; }
            try
            {
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
                ActivityLog.General("browser cache cleared");
                SetStatus("CACHE CLEARED");
            }
            catch (Exception ex) { ActivityLog.General("clear cache failed: " + ex.Message); }
        }

        // keep the active tab's strip title in step with the page (host, or about:…)
        private void UpdateActiveTabTitle()
        {
            if (tabStrip == null || tabStrip.ActiveIndex < 0) return;
            string t = "new tab";
            try
            {
                string src = web != null && web.Source != null ? web.Source.ToString() : null;
                if (!string.IsNullOrEmpty(src))
                {
                    if (src.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) t = "new tab";
                    else { try { t = new Uri(src).Host; } catch { t = src; } }
                    if (string.IsNullOrEmpty(t)) t = src;
                }
            }
            catch { }
            tabStrip.SetTitle(tabStrip.ActiveIndex, t);
        }

        // Before a full engine rebuild, dispose every non-active tab so they don't
        // outlive their (about-to-be-cleaned) user-data folder. Leaves only the active.
        private void DisposeExtraTabs()
        {
            if (tabStrip == null) return;
            for (int k = tabStrip.Count - 1; k >= 0; k--)
            {
                var v = tabStrip.ViewAt(k) as Microsoft.Web.WebView2.WinForms.WebView2;
                if (v != null && !object.ReferenceEquals(v, web))
                {
                    try { this.Controls.Remove(v); } catch { }
                    try { v.Dispose(); } catch { }
                    tabStrip.RemoveTab(k);
                }
            }
            int ai = IndexOfView(web);
            if (ai >= 0) tabStrip.SetActive(ai);
        }

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
                // Small wallpapers are inlined as a data URI so they always load even when
                // the home.aloha virtual host hasn't taken — which is what broke the SVG.
                css = "background-color:#060c14;" +
                      "background-image:url('" + WallpaperCssUrl(file) + "');" +
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
            // NavigateToString may not re-fire SourceChanged when Source was already
            // about:blank (e.g. a brand-new tab), so clear the chrome here too.
            try { textBox1.Text = ""; } catch { }
            UpdateActiveTabTitle();
        }

        // Resolve the wallpaper to a CSS url(): inline small files as a data URI (bullet-
        // proof — no dependency on the virtual host), and fall back to the home.aloha
        // mapping for anything large so we never inline enough to stall the renderer.
        private static string WallpaperCssUrl(string file)
        {
            try
            {
                string[] roots =
                {
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.CurrentDirectory
                };
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string p = System.IO.Path.Combine(root, file);
                    if (!System.IO.File.Exists(p)) continue;
                    var bytes = System.IO.File.ReadAllBytes(p);
                    if (bytes.Length <= 96 * 1024)
                        return "data:" + MimeFor(file) + ";base64," + Convert.ToBase64String(bytes);
                    break;   // found but too big to inline -> use the virtual host
                }
            }
            catch { }
            return "https://home.aloha/" + file;   // fallback (large image, or not found next to exe)
        }

        private static string MimeFor(string file)
        {
            switch (System.IO.Path.GetExtension(file ?? "").ToLowerInvariant())
            {
                case ".svg":  return "image/svg+xml";
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp":  return "image/bmp";
                default:      return "application/octet-stream";
            }
        }

        // ── menu layout ───────────────────────────────────────────────
        // Edit:     Find in this page · DevTools · Browser Style · Browser Configuration
        // Options:  Proxy · Network · Live headers
        // Navigate: Network map · Console · Site map
        private BrowserStylePanel styleWin;
        private ToolStripMenuItem browserStyleItem;
        private ToolStripMenuItem interceptItem, rawTcpItem, popupsItem, profilesItem;
        private ToolStripMenuItem tamperItem;
        private ToolStripMenuItem v2rayItem, xrayItem;
        private ToolStripMenuItem nmapItem, instructionMapItem, vpnGateItem, torItem, advorItem;
        private ToolStripMenuItem bookmarksItem;
        private ToolStripMenuItem downloadsItem;
        private void SetupMenus()
        {
            // Browser Style entry (opens the appearance panel)
            browserStyleItem = new ToolStripMenuItem("Browser Style...");
            browserStyleItem.Click += (s, e) =>
            {
                if (styleWin == null || styleWin.IsDisposed)
                    styleWin = new BrowserStylePanel(cfg, () =>
                    {
                        GoHome();
                        if (_bmHost != null) _bmHost.SetVisible(cfg.ShowBookmarkButton);
                    });
                styleWin.Show();
                styleWin.BringToFront();
            };

            // Aloha Inspector entry (our own in-process inspector; floats above Form1)
            inspectorItem = new ToolStripMenuItem("DevTools");
            inspectorItem.Click += (s, e) => OpenInspectorAdjacent();

            // Bookmarks browser (green-on-black overlay, same chrome as the file tree)
            bookmarksItem = new ToolStripMenuItem("Bookmarks...");
            bookmarksItem.Click += bookmarksToolStripMenuItem_Click;

            downloadsItem = new ToolStripMenuItem("Downloads...");
            downloadsItem.Click += downloadsToolStripMenuItem_Click;

            // Profiles submenu — save / load / delete full NetConfig snapshots.
            // Rebuilt each time it opens so the saved list stays current.
            profilesItem = new ToolStripMenuItem("Profiles");
            profilesItem.DropDownOpening += (s, e) => RebuildProfilesMenu();
            RebuildProfilesMenu();   // initial fill so the submenu arrow appears

            // v2ray / xray pickers — each opens its own window
            v2rayItem = new ToolStripMenuItem("V2Ray...");
            v2rayItem.Click += (s, e) => OpenCoreWindow(ref v2rayWin, "v2ray");
            xrayItem = new ToolStripMenuItem("Xray...");
            xrayItem.Click += (s, e) => OpenCoreWindow(ref xrayWin, "xray");

            // "Find in this page" had no handler — make it behave like Ctrl+F
            findInThisPageToolStripMenuItem.Click += findInThisPageToolStripMenuItem_Click;
            siteMapToolStripMenuItem.Click += siteMapToolStripMenuItem_Click;

            // re-parent everything into the layout above (handlers ride along on the items)
            editToolStripMenuItem.DropDownItems.Clear();
            optionsToolStripMenuItem.DropDownItems.Clear();
            navigateToolStripMenuItem.DropDownItems.Clear();

            editToolStripMenuItem.DropDownItems.Add(findInThisPageToolStripMenuItem);
            editToolStripMenuItem.DropDownItems.Add(inspectorItem);
            editToolStripMenuItem.DropDownItems.Add(devToolsToolStripMenuItem);
            editToolStripMenuItem.DropDownItems.Add(browserStyleItem);
            editToolStripMenuItem.DropDownItems.Add(browserConfigToolStripMenuItem);

            optionsToolStripMenuItem.DropDownItems.Add(proxyToolStripMenuItem);
            optionsToolStripMenuItem.DropDownItems.Add(networkToolStripMenuItem);
            optionsToolStripMenuItem.DropDownItems.Add(liveHeadersToolStripMenuItem);
            optionsToolStripMenuItem.DropDownItems.Add(tamperItem);
            optionsToolStripMenuItem.DropDownItems.Add(interceptItem);
            optionsToolStripMenuItem.DropDownItems.Add(rawTcpItem);
            optionsToolStripMenuItem.DropDownItems.Add(popupsItem);
            optionsToolStripMenuItem.DropDownItems.Add(profilesItem);
            optionsToolStripMenuItem.DropDownItems.Add(torItem);
            optionsToolStripMenuItem.DropDownItems.Add(advorItem);
            optionsToolStripMenuItem.DropDownItems.Add(vpnGateItem);
            optionsToolStripMenuItem.DropDownItems.Add(v2rayItem);
            optionsToolStripMenuItem.DropDownItems.Add(xrayItem);

            var helpItem = new ToolStripMenuItem("Help");
            helpItem.Click += helpWindowToolStripMenuItem_Click;
            helpToolStripMenuItem.DropDownItems.Insert(0, helpItem);

            // "blank" — the home/wallpaper page, relocated here off the Home button
            // (which is being freed to become the tab strip later)
            var blankItem = new ToolStripMenuItem("blank");
            blankItem.Click += (s, e) => GoHome();
            var newTabItem = new ToolStripMenuItem("New Tab\tCtrl+T");
            newTabItem.Click += (s, e) => NewTab();
            navigateToolStripMenuItem.DropDownItems.Add(newTabItem);
            var tabBoardItem = new ToolStripMenuItem("Tab Board\tCtrl+Shift+M");
            tabBoardItem.Click += (s, e) => OpenTabBoard();
            navigateToolStripMenuItem.DropDownItems.Add(tabBoardItem);
            navigateToolStripMenuItem.DropDownItems.Add(blankItem);
            navigateToolStripMenuItem.DropDownItems.Add(instructionMapItem);
            navigateToolStripMenuItem.DropDownItems.Add(nmapItem);
            navigateToolStripMenuItem.DropDownItems.Add(consoleToolStripMenuItem);
            navigateToolStripMenuItem.DropDownItems.Add(siteMapToolStripMenuItem);
            navigateToolStripMenuItem.DropDownItems.Add(bookmarksItem);
            navigateToolStripMenuItem.DropDownItems.Add(downloadsItem);
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
        // "Change Network IP": restart the live OpenVPN tunnel, requesting a fresh tunnel
        // address each press (cycles 10.10.11.x). The request rides peer-info — your own
        // server honours it, public VPN Gate exits ignore it but the restart still rotates
        // the session. No active tunnel -> tell the user to connect one first.
        private int nextVpnIpOctet = 1;
        private void button7_Click(object sender, EventArgs e)
        {
            var vpn = VpnGateClient.Active;
            if (vpn == null || !vpn.Connected)
            {
                SetStatus("CHANGE IP\r\nno active VPN \u2014 connect one in VPN Gate first");
                return;
            }
            string ip = "10.10.11." + nextVpnIpOctet;
            nextVpnIpOctet = nextVpnIpOctet >= 254 ? 1 : nextVpnIpOctet + 1;
            string note;
            vpn.ChangeNetworkIp(ip, out note);
            SetStatus("CHANGE IP\r\n" + note);
        }
        private void textBox1_KeyDown(object sender, KeyEventArgs e)
        {
            // swallow Enter so WinForms doesn't play the system "ding"
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; }
        }
        private void textBox1_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && textBox1.Text.Trim().Length > 0) Go(textBox1.Text);
        }

        // ── menu ──
        private void exitToolStripMenuItem_Click(object sender, EventArgs e) => Application.Exit();
        private AlohaInspectorWindow inspectorWin;
        private ToolStripMenuItem inspectorItem;

        // Open the Aloha inspector as an ADJACENT window — docked to the right edge of
        // the main window, just under the chrome — so its (indented, freely selectable)
        // output sits beside the page instead of clumping over it.
        private void OpenInspectorAdjacent()
        {
            if (!Ready) return;
            try
            {
                if (inspectorWin == null || inspectorWin.IsDisposed)
                    inspectorWin = new AlohaInspectorWindow(web.CoreWebView2);
                inspectorWin.Owner = this;                       // owned -> rides above the main window
                inspectorWin.StartPosition = FormStartPosition.Manual;

                int w = inspectorWin.Width, h = inspectorWin.Height;
                int x = this.Bounds.Right - w - 8;
                int y = this.Bounds.Top + (textBox1 != null ? textBox1.Bottom : 96) + 8;
                var wa = Screen.FromControl(this).WorkingArea;    // keep it on-screen
                x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
                y = Math.Max(wa.Top,  Math.Min(y, wa.Bottom - h));
                inspectorWin.Location = new System.Drawing.Point(x, y);

                if (!inspectorWin.Visible) inspectorWin.Show(this);
                else { inspectorWin.BringToFront(); inspectorWin.Activate(); }
                ActivityLog.General("Inspector: opened adjacent");
            }
            catch (Exception ex) { ActivityLog.General("Inspector: " + ex.Message); }
        }
        private void devToolsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Ready) return;
            try { web.CoreWebView2.OpenDevToolsWindow(); } catch { }
        }
        private void infoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var w = new AboutWindow();
            w.Show();
            w.BringToFront();
        }

        private void helpWindowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var w = new HelpWindow();
            w.Show();
            w.BringToFront();
        }

        private ProxyPanel proxyWin;
        private NetworkPanel networkWin;
        private BrowserConfigPanel browserCfgWin;
        private CoreProxyWindow v2rayWin, xrayWin;
        // Blocked popups: NewWindowRequested URIs are captured here (suppressed, never
        // auto-opened) and reviewed in the singleton PopupsWindow (Options -> Blocked popups).
        private readonly List<PopupEntry> popupQueue = new List<PopupEntry>();
        private PopupsWindow popupsWin;

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
                proxyWin = new ProxyPanel(cfg, ReinitEngine, () => ClearCache());
            proxyWin.Show();
            proxyWin.BringToFront();
        }

        // ── Profiles (Options -> Profiles) ──
        private void RebuildProfilesMenu()
        {
            if (profilesItem == null) return;
            profilesItem.DropDownItems.Clear();

            var saveAs = new ToolStripMenuItem("Save current settings as\u2026");
            saveAs.Click += (s, e) => SaveCurrentProfile();
            profilesItem.DropDownItems.Add(saveAs);

            var names = ProfileStore.List();
            if (names.Length == 0) return;

            // load section: clicking a profile loads it and reloads the engine
            profilesItem.DropDownItems.Add(new ToolStripSeparator());
            foreach (var n in names)
            {
                string name = n;   // capture per-iteration
                var it = new ToolStripMenuItem(name);
                it.Click += (s, e) => LoadProfile(name);
                profilesItem.DropDownItems.Add(it);
            }

            // delete section: a sub-menu so it can't be hit by accident
            profilesItem.DropDownItems.Add(new ToolStripSeparator());
            var del = new ToolStripMenuItem("Delete");
            foreach (var n in names)
            {
                string name = n;
                var it = new ToolStripMenuItem(name);
                it.Click += (s, e) => DeleteProfile(name);
                del.DropDownItems.Add(it);
            }
            profilesItem.DropDownItems.Add(del);
        }

        private void SaveCurrentProfile()
        {
            using (var dlg = new NamePromptDialog("Save profile", "Profile name:", ""))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.Value;
                if (string.IsNullOrWhiteSpace(name)) return;
                if (ProfileStore.Exists(name) &&
                    MessageBox.Show(this, "Overwrite profile \"" + name + "\"?", "Profiles",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                ProfileStore.Save(name, cfg);
            }
        }

        private void LoadProfile(string name)
        {
            cfg.CopyFrom(ProfileStore.Load(name));   // mutate in place (panels share cfg)
            cfg.Save();                              // persist as the active config
            ReinitEngine();                          // rebuild engine with new settings
        }

        private void DeleteProfile(string name)
        {
            if (MessageBox.Show(this, "Delete profile \"" + name + "\"?", "Profiles",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ProfileStore.Delete(name);
        }

        private void OpenCoreWindow(ref CoreProxyWindow win, string core)
        {
            if (win == null || win.IsDisposed)
                win = new CoreProxyWindow(cfg, ReinitEngine, core);
            win.Show();
            win.BringToFront();
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
            DisposeExtraTabs();   // collapse to the active tab so others don't outlive their folder
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
                if (tabStrip != null)              // repoint the surviving tab at the rebuilt engine
                {
                    int ai = tabStrip.ActiveIndex >= 0 ? tabStrip.ActiveIndex : 0;
                    if (tabStrip.Count == 0) tabStrip.AddTab("Home", web);
                    else tabStrip.SetView(ai, web);
                    tabStrip.SetActive(ai < tabStrip.Count ? ai : 0);
                }
                TidyEngineFolders(engineGen);   // drop older ephemeral folders
                SnapWindowToGrid();             // if snap toggle is on, fit the window now
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
        private TamperPanel tamperWin;
        private InterceptPanel interceptWin;
        private RawTcpPanel rawTcpWin;
        private readonly InterceptProxy interceptProxy = new InterceptProxy();

        private void consoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (consoleWin == null || consoleWin.IsDisposed)
            {
                consoleWin = new ConsoleWindow(cfg, OpenUrl);
                consoleWin.CurrentUrl = () => web != null && web.Source != null ? web.Source.ToString() : "";
                consoleWin.Bookmarks = _bookmarks;
            }
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

        private void tamperToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (tamperWin == null || tamperWin.IsDisposed)
                tamperWin = new TamperPanel();
            tamperWin.Show();
            tamperWin.BringToFront();
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
        private BookmarkExplorerWindow bmOverlay;   // bookmarks browser, same overlay pattern
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
            string navPath = LocalPathFromUrlBar();   // a real local dir/file in the URL bar, else null

            // With a path to jump to, always open+navigate (never toggle-closed).
            // Without one, behave as a normal open/close toggle.
            if (overlay.Visible && navPath == null)
            {
                overlay.Hide();
                SetButton8Text("New File...");
                button8.Invalidate();
                return;
            }

            if (!overlay.Visible)
            {
                PositionOverlayFull();
                overlay.Show();
                overlay.FocusView();
            }
            if (navPath != null)
            {
                string p = navPath;   // defer so the (possibly just-shown) tree is laid out
                overlay.BeginInvoke(new Action(delegate { try { overlay.NavigateTo(p); } catch { } }));
            }
            button8.Invalidate();
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

        // The URL bar text IF it's a real local directory/file (C:\..., \\server\...,
        // or a file:/// URL) — else null. Web URLs / search text return null, so the
        // explorer just opens normally. Paste a directory, hit New File, land in it.
        private string LocalPathFromUrlBar()
        {
            if (textBox1 == null) return null;
            string t = (textBox1.Text ?? "").Trim().Trim('"');
            if (t.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                try { t = new Uri(t).LocalPath; } catch { }
            }
            if (t.Length < 2) return null;
            bool looksLocal = (char.IsLetter(t[0]) && t[1] == ':') || t.StartsWith("\\\\");
            if (!looksLocal) return null;
            try { if (System.IO.Directory.Exists(t) || System.IO.File.Exists(t)) return t; }
            catch { }
            return null;
        }

        // ── bookmarks browser: its own tool window (DafyFrame), opened like the
        //    Console / Network windows — no overlay positioning needed ──
        private void bookmarksToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (bmOverlay == null || bmOverlay.IsDisposed)
                bmOverlay = new BookmarkExplorerWindow(_bookmarks, BookmarksPath, OpenUrl);

            bmOverlay.Reload();      // pick up ★ adds/removes made while browsing
            bmOverlay.Show();
            bmOverlay.BringToFront();
            bmOverlay.FocusView();

            // expand+select the cluster for the page you're on, if it's saved
            string here = (web != null && web.Source != null) ? web.Source.ToString() : "";
            if (!string.IsNullOrEmpty(here))
            {
                string u = here;
                bmOverlay.BeginInvoke(new Action(delegate { try { bmOverlay.NavigateTo(u); } catch { } }));
            }
        }

        // ── downloads history window (its own tool window, opened like Console) ──
        private DownloadsWindow downloadsWin;
        private void downloadsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (downloadsWin == null || downloadsWin.IsDisposed)
                downloadsWin = new DownloadsWindow(_downloads);
            downloadsWin.Show();
            downloadsWin.BringToFront();
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
            // deprecated NetGraphPanel removed — the real Network Map is
            // InstructionMapWindow, opened from Options. This stub remains only
            // if any designer wiring still references the old handler name.
            var w = new InstructionMapWindow(
                web != null && web.CoreWebView2 != null ? web.CoreWebView2.Environment : null);
            w.Show();
            w.BringToFront();
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
        // ── WebView zoom control: a small Win9x cluster ( [-] 100% [+] ) docked at
        //    the RIGHT end of the footer well (next to the size readout). Buttons
        //    styled like Aloha's scrollbars. Clicking % resets to 100%. Persists
        //    in cfg.WebZoom.
        private const int ZB_BTN = 16, ZB_READ = 40, ZB_H = 13;
        private void BuildZoomBar(Control parent)
        {
            zoomBar = new Panel { Width = ZB_BTN * 2 + ZB_READ, Height = ZB_H, Top = 1, BackColor = cFooter };

            var minus = new Panel { Left = 0, Top = 0, Width = ZB_BTN, Height = ZB_H, Cursor = Cursors.Hand };
            zoomReadout = new Label
            {
                Left = ZB_BTN, Top = 0, Width = ZB_READ, Height = ZB_H,
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                BackColor = Color.FromArgb(0xF4, 0xF4, 0xF4), ForeColor = Color.FromArgb(0x50, 0x50, 0x50),
                Font = new Font("Lucida Console", 7.5f, FontStyle.Bold), Text = "100%"
            };
            var plus = new Panel { Left = ZB_BTN + ZB_READ, Top = 0, Width = ZB_BTN, Height = ZB_H, Cursor = Cursors.Hand };

            minus.Paint += (s, e) => DrawZoomButton(e.Graphics, ((Panel)s).ClientRectangle, "\u2212");
            plus.Paint  += (s, e) => DrawZoomButton(e.Graphics, ((Panel)s).ClientRectangle, "+");
            minus.Click += (s, e) => SetZoom(webZoom - 0.10);
            plus.Click  += (s, e) => SetZoom(webZoom + 0.10);
            zoomReadout.Click += (s, e) => SetZoom(1.0);   // click % -> reset to 100%

            zoomBar.Controls.Add(minus);
            zoomBar.Controls.Add(zoomReadout);
            zoomBar.Controls.Add(plus);
            parent.Controls.Add(zoomBar);
            zoomBar.BringToFront();
        }

        private void DrawZoomButton(Graphics g, Rectangle r, string glyph)
        {
            using (var f = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0))) g.FillRectangle(f, r);
            using (var hi = new Pen(Color.White))
            { g.DrawLine(hi, r.Left, r.Top, r.Right - 1, r.Top); g.DrawLine(hi, r.Left, r.Top, r.Left, r.Bottom - 1); }
            using (var sh = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0)))
            { g.DrawLine(sh, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1); g.DrawLine(sh, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1); }
            TextRenderer.DrawText(g, glyph, new Font("Lucida Console", 8f, FontStyle.Bold), r,
                Color.FromArgb(0x50, 0x50, 0x50), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void SetZoom(double z)
        {
            double want = Math.Max(0.01, Math.Round(z, 2));   // floor just above 0 (WebView2 rejects <= 0); no upper cap, for the lols
            try
            {
                if (web != null && web.CoreWebView2 != null) web.ZoomFactor = want;
                webZoom = want;                               // commit only if the engine didn't throw
            }
            catch { return; }                                 // engine refused this extreme -> keep last good zoom
            if (zoomReadout != null) zoomReadout.Text = (int)Math.Round(webZoom * 100) + "%";
            cfg.WebZoom = webZoom;
        }

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

            int webX = 0, webW = w;
            if (cfg.CfgSnapViewportToGrid)
            {
                // Snap the measurable viewport to the blueprint.svg grid (160px tile,
                // 16px cells = 160/10). The window itself is snapped to the grid in the
                // resize grip, so the viewport lands flush top-left with no centering
                // margin and the wallpaper grid starts exactly on a cell line (no
                // half-square at the edges).
                int snappedW = Math.Max(SnapStepW, (w / SnapStepW) * SnapStepW);
                int snappedH = Math.Max(SnapStepH, (webH / SnapStepH) * SnapStepH);
                webX = 0;                  // flush left — no gray margin
                webW = snappedW;
                webH = snappedH;
            }
            web.SetBounds(webX, top, webW, webH);
            if (bars)
            {
                vScroll.SetBounds(webX + webW - SB, top, SB, Math.Max(0, webH - SB));
                hScroll.SetBounds(webX, top + webH - SB, Math.Max(0, webW - SB), SB);
                scrollCorner.SetBounds(webX + webW - SB, top + webH - SB, SB, SB);
                vScroll.BringToFront();
                hScroll.BringToFront();
                scrollCorner.BringToFront();
            }
            if (footer != null) footer.BringToFront();
        }
    }
}
