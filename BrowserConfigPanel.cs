using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Options -> Browser Configuration
    // A board of Chromium privacy / leak toggles, grouped by area.
    // Each maps to NetConfig.Cfg* and assembles into the browser
    // arguments. Apply saves + re-inits the engine.
    //
    // Uses LiteFrame (grey-gradient header), matching the main Aloha
    // window — the config panels' "modern" style. The cmd windows keep
    // the dark dafy-node frame.
    // ============================================================
    public class BrowserConfigPanel : DafyFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;

        // keep references so Apply can read them back
        private CheckBox cWebRtcFix, cWebRtcOff,
                         cDnsPrefetch, cPreconnect,
                         cSafeBrowsing, cHyperlinkAudit, cDomainReliability, cComponentUpdate,
                         cBackgroundNet, cSync,
                         cQuic, cReferrers, cMediaDevices,
                         cWin9xScroll;
        private CheckBox cBlockDl, cConfirmDl, cHidePopup;
        private CheckBox cFridaSandboxOff;
        private CheckBox cEmbeddedDevTools;
        private CheckBox cSnapGrid;
        private CheckBox cTorLetterbox;
        private TextBox  tDownloadDir;
        private TextBox  tSearch;
        private CheckBox cDoh;
        private TextBox  tDohEndpoint;

        public BrowserConfigPanel(NetConfig config, Action applyCallback)
            : base("OPT-CFG", "Browser Configuration")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(560, 620);
            Font = new Font("Tahoma", 8.25f);

            // #FAFAFB settings area fills the space above the strips. AutoScroll stays
            // on so the OS tracks the real vertical extent (read via GetScrollInfo); a
            // light DarkScrollBar is laid over the native bar and driven through
            // AttachScrollable. Added first so it sits at the front and takes Fill.
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB),
                Padding = new Padding(14, 10, 14, 10)
            };
            ClientArea.Controls.Add(scroll);

            // light strip holding the Apply button, pinned just ABOVE the black bar.
            // Same flat style/size as the other buttons in this panel.
            var applyStrip = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB) };
            var applyBtn = new RoundButton
            {
                Text = "Apply Settings",
                Width = 150, Height = 24,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB)
            };
            applyBtn.Click += (s, e) => Apply();
            applyStrip.Controls.Add(applyBtn);
            void CenterApply() { applyBtn.Left = Math.Max(8, (applyStrip.ClientSize.Width - applyBtn.Width) / 2); applyBtn.Top = 7; }
            applyStrip.Resize += (s, e) => CenterApply();
            ClientArea.Controls.Add(applyStrip);

            // decorative black bar, 2 grid squares (64px) tall, at the very bottom
            var body = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.Black };
            ClientArea.Controls.Add(body);

            CenterApply();

            int y = 6;

            y = Group(scroll, "Search engine", y);
            var lblSearch = new Label
            {
                Text = "Query URL  ({q} = search terms).  Blank = auto: Startpage .onion on Tor, clearnet otherwise.",
                Left = 6, Top = y, Width = 500, Height = 16, AutoSize = false,
                ForeColor = Color.FromArgb(0x40, 0x40, 0x40), BackColor = Color.Transparent,
                Font = new Font("Tahoma", 7.5f)
            };
            scroll.Controls.Add(lblSearch); y += 18;
            tSearch = new TextBox { Left = 6, Top = y, Width = 500, Text = cfg.SearchTemplate ?? "" };
            scroll.Controls.Add(tSearch); y += 28;
            int spy = y;
            var bSearchAuto = new RoundButton { Text = "Auto", Left = 6, Top = spy, Width = 150, Height = 24,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB) };
            bSearchAuto.Click += (s, e) => tSearch.Text = "";
            var bSearchSp = new RoundButton { Text = "Startpage", Left = 162, Top = spy, Width = 150, Height = 24,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB) };
            bSearchSp.Click += (s, e) => tSearch.Text = NetConfig.SearchStartpage;
            var bSearchSpO = new RoundButton { Text = "Startpage (Onion)", Left = 318, Top = spy, Width = 176, Height = 24,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB) };
            bSearchSpO.Click += (s, e) => tSearch.Text = NetConfig.SearchStartpageOnion;
            scroll.Controls.Add(bSearchAuto); scroll.Controls.Add(bSearchSp); scroll.Controls.Add(bSearchSpO);
            y += 32;

            y = Group(scroll, "WebRTC  ·  IP leak surface", y);
            cWebRtcFix = Check(scroll, "Route WebRTC through proxy only (disable non-proxied UDP)",
                cfg.CfgWebRtcLeakFix, ref y);
            cWebRtcOff = Check(scroll, "Disable WebRTC entirely (breaks video/voice calls)",
                cfg.CfgWebRtcDisableFully, ref y);

            y = Group(scroll, "DNS  ·  prefetch", y);
            cDnsPrefetch = Check(scroll, "Disable DNS prefetching / network prediction",
                cfg.CfgDisableDnsPrefetch, ref y);
            cPreconnect = Check(scroll, "Disable speculative preconnect",
                cfg.CfgDisablePreconnect, ref y);

            y = Group(scroll, "DNS-over-HTTPS", y);
            cDoh = Check(scroll, "Resolve DNS over HTTPS (secure mode)", cfg.DohEnabled, ref y);
            var lblDoh = new Label
            {
                Text = "Resolver template.  NOTE: a SOCKS/Tor proxy resolves DNS itself, so DoH mainly applies in Direct mode.",
                Left = 6, Top = y, Width = 500, Height = 16, AutoSize = false,
                ForeColor = Color.FromArgb(0x40, 0x40, 0x40), BackColor = Color.Transparent,
                Font = new Font("Tahoma", 7.5f)
            };
            scroll.Controls.Add(lblDoh); y += 18;
            tDohEndpoint = new TextBox { Left = 6, Top = y, Width = 372,
                Text = string.IsNullOrEmpty(cfg.DohEndpoint) ? "https://cloudflare-dns.com/dns-query" : cfg.DohEndpoint };
            scroll.Controls.Add(tDohEndpoint);
            var bDohCf = new RoundButton { Text = "Cloudflare", Left = tDohEndpoint.Right + 6, Top = y - 1, Width = 116, Height = 24,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB) };
            bDohCf.Click += (s, e) => tDohEndpoint.Text = "https://cloudflare-dns.com/dns-query";
            scroll.Controls.Add(bDohCf);
            y += 32;

            y = Group(scroll, "Phone-home  ·  telemetry", y);
            cSafeBrowsing = Check(scroll, "Disable Safe Browsing URL lookups (Google)",
                cfg.CfgDisableSafeBrowsing, ref y);
            cHyperlinkAudit = Check(scroll, "Disable hyperlink auditing  <a ping>  beacons",
                cfg.CfgDisableHyperlinkAudit, ref y);
            cDomainReliability = Check(scroll, "Disable Domain Reliability uploads (Google)",
                cfg.CfgDisableDomainReliability, ref y);
            cComponentUpdate = Check(scroll, "Disable component auto-update",
                cfg.CfgDisableComponentUpdate, ref y);

            y = Group(scroll, "Background networking", y);
            cBackgroundNet = Check(scroll, "Disable background networking",
                cfg.CfgDisableBackgroundNet, ref y);
            cSync = Check(scroll, "Disable profile sync",
                cfg.CfgDisableSync, ref y);

            y = Group(scroll, "Protocols  ·  fingerprint surface", y);
            cQuic = Check(scroll, "Disable QUIC (force predictable TCP through proxy)",
                cfg.CfgDisableQuic, ref y);
            cReferrers = Check(scroll, "Send no referrers",
                cfg.CfgReduceReferrers, ref y);
            cMediaDevices = Check(scroll, "Spoof media devices (fake cam/mic — breaks real capture)",
                cfg.CfgDisableMediaDevices, ref y);

            y = Group(scroll, "Downloads", y);
            cBlockDl = Check(scroll, "Block all downloads",
                cfg.BlockDownloads, ref y);
            cConfirmDl = Check(scroll, "Ask before every download (no automatic downloads)",
                cfg.ConfirmDownloads, ref y);
            cHidePopup = Check(scroll, "Hide the browser's built-in download popup",
                cfg.HideDownloadPopup, ref y);
            // download folder (blank = WebView2 default)
            var lblDir = new Label
            {
                Text = "Download folder (blank = browser default):", Left = 6, Top = y, AutoSize = true,
                ForeColor = Color.Black, BackColor = Color.Transparent
            };
            scroll.Controls.Add(lblDir); y += 20;
            tDownloadDir = new TextBox { Left = 6, Top = y, Width = 372, Text = cfg.DownloadDir ?? "" };
            var btnBrowse = new RoundButton
            {
                Text = "Browse...", Left = tDownloadDir.Right + 6, Top = y - 1, Width = 84, Height = 24,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB)
            };
            btnBrowse.Click += (s, e) =>
            {
                using (var fb = new FolderBrowserDialog())
                    if (fb.ShowDialog() == DialogResult.OK) tDownloadDir.Text = fb.SelectedPath;
            };
            scroll.Controls.Add(tDownloadDir);
            scroll.Controls.Add(btnBrowse);
            y += 32;

            y = Group(scroll, "Interface", y);
            cWin9xScroll = Check(scroll, "Modern Aloha browser scrollbars (laggy)",
                cfg.CfgWin9xBrowserScrollbars, ref y);

            y = Group(scroll, "Diagnostics", y);
            cFridaSandboxOff = Check(scroll,
                "Allow INSTR instruction tracing (--no-sandbox \u2014 weakens renderer sandbox)",
                cfg.CfgAllowFridaSandboxOff, ref y);
            var lblWarn = new Label
            {
                Text = "Required for INSTR \u2192 THREAD/REGISTERS to read the live renderer via Frida.\n"
                     + "Leaves the page renderer un-sandboxed. Enable only while tracing.",
                Left = 24, Top = y, Width = 470, Height = 32, AutoSize = false,
                ForeColor = Color.FromArgb(0x80, 0x40, 0x00), BackColor = Color.Transparent,
                Font = new Font("Tahoma", 7.5f)
            };
            scroll.Controls.Add(lblWarn);
            y += 36;

            cEmbeddedDevTools = Check(scroll,
                "Embedded DevTools (in-frame inspector \u2014 opens a loopback debug port)",
                cfg.CfgEmbeddedDevTools, ref y);
            var lblDev = new Label
            {
                Text = "Opens a CDP port bound to 127.0.0.1 with an OS-assigned port, locked to the\n"
                     + "self-hosted inspector origin so no foreign page can connect. Needs a DevTools\n"
                     + "frontend in .\\devtools\\. Off = the native DevTools window. Applying reloads the engine.",
                Left = 24, Top = y, Width = 470, Height = 44, AutoSize = false,
                ForeColor = Color.FromArgb(0x80, 0x40, 0x00), BackColor = Color.Transparent,
                Font = new Font("Tahoma", 7.5f)
            };
            scroll.Controls.Add(lblDev);
            y += 48;

            y = Group(scroll, "Anti-fingerprint", y);
            cSnapGrid = Check(scroll,
                "Snap viewport to the blueprint grid (report grid-aligned window size)",
                cfg.CfgSnapViewportToGrid, ref y);
            var lblSnap = new Label
            {
                Text = "Rounds the measurable page viewport down to a 16px grid cell so sites\n"
                     + "see coarse, shared dimensions instead of your exact window size.",
                Left = 24, Top = y, Width = 470, Height = 32, AutoSize = false,
                ForeColor = Color.FromArgb(0x40, 0x40, 0x40), BackColor = Color.Transparent,
                Font = new Font("Tahoma", 7.5f)
            };
            scroll.Controls.Add(lblSnap);
            y += 36;

            cTorLetterbox = Check(scroll,
                "Tor letterbox preset (snap to Tor Browser's 200\u00D7100 buckets)",
                cfg.CfgTorLetterbox, ref y);
            var lblTor = new Label
            {
                Text = "Stricter: rounds to the exact 200\u00D7100 sizes Tor Browser uses, so you\n"
                     + "share a window-size bucket with all Tor users. Requires grid snap above.",
                Left = 24, Top = y, Width = 470, Height = 32, AutoSize = false,
                ForeColor = Color.FromArgb(0x40, 0x40, 0x40), BackColor = Color.Transparent,
                Font = new Font("Tahoma", 7.5f)
            };
            scroll.Controls.Add(lblTor);
            y += 36;

            // gate: Tor letterbox is only selectable when grid snap is on
            cTorLetterbox.Enabled = cSnapGrid.Checked;
            cSnapGrid.CheckedChanged += (s, e) =>
            {
                cTorLetterbox.Enabled = cSnapGrid.Checked;
                if (!cSnapGrid.Checked) cTorLetterbox.Checked = false;   // can't have the preset without the base
            };

            // ── light Win9x scrollbar laid over the native AutoScroll bar ──
            // Same trick as NetworkPanel/ProxyPanel: the native vertical bar stays
            // live (covered, but it's what GetScrollInfo reads); this opaque bar
            // sits on top at the right edge and scrolls the panel via WM_VSCROLL.
            // Content is a single column, so no horizontal bar is needed.
            const int SB = 17;
            var vbar = new DarkScrollBar(false, light: true) { Dock = DockStyle.None };
            vbar.Left   = ClientArea.ClientSize.Width - SB;
            vbar.Top    = 0;
            vbar.Width  = SB;
            vbar.Height = ClientArea.ClientSize.Height - body.Height - applyStrip.Height;
            vbar.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
            ClientArea.Controls.Add(vbar);
            vbar.AttachScrollable(scroll);
            vbar.BringToFront();
        }

        private int Group(Panel host, string title, int y)
        {
            if (y > 6) y += 8;
            var l = new Label
            {
                Text = title, Left = 0, Top = y, AutoSize = true,
                Font = new Font("Tahoma", 8.25f, FontStyle.Bold),
                ForeColor = Color.Black
            };
            host.Controls.Add(l);
            y += 20;
            // (no divider rule — removed)
            return y + 4;
        }

        private CheckBox Check(Panel host, string text, bool val, ref int y)
        {
            var c = new CheckBox
            {
                Text = text, Left = 6, Top = y, Width = 500, Checked = val,
                ForeColor = Color.Black, BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            host.Controls.Add(c);
            y += 24;
            return c;
        }

        private void Apply()
        {
            cfg.CfgWebRtcLeakFix          = cWebRtcFix.Checked;
            cfg.CfgWebRtcDisableFully     = cWebRtcOff.Checked;
            cfg.CfgDisableDnsPrefetch     = cDnsPrefetch.Checked;
            cfg.CfgDisablePreconnect      = cPreconnect.Checked;
            cfg.CfgDisableSafeBrowsing    = cSafeBrowsing.Checked;
            cfg.CfgDisableHyperlinkAudit  = cHyperlinkAudit.Checked;
            cfg.CfgDisableDomainReliability = cDomainReliability.Checked;
            cfg.CfgDisableComponentUpdate = cComponentUpdate.Checked;
            cfg.CfgDisableBackgroundNet   = cBackgroundNet.Checked;
            cfg.CfgDisableSync            = cSync.Checked;
            cfg.CfgDisableQuic            = cQuic.Checked;
            cfg.CfgReduceReferrers        = cReferrers.Checked;
            cfg.CfgDisableMediaDevices    = cMediaDevices.Checked;
            cfg.CfgWin9xBrowserScrollbars = cWin9xScroll.Checked;
            cfg.CfgAllowFridaSandboxOff   = cFridaSandboxOff.Checked;
            cfg.CfgEmbeddedDevTools       = cEmbeddedDevTools.Checked;
            cfg.CfgSnapViewportToGrid     = cSnapGrid.Checked;
            cfg.CfgTorLetterbox           = cSnapGrid.Checked && cTorLetterbox.Checked;
            cfg.BlockDownloads            = cBlockDl.Checked;
            cfg.ConfirmDownloads          = cConfirmDl.Checked;
            cfg.HideDownloadPopup         = cHidePopup.Checked;
            cfg.DownloadDir               = (tDownloadDir.Text ?? "").Trim();
            cfg.SearchTemplate            = (tSearch.Text ?? "").Trim();
            cfg.DohEnabled                = cDoh.Checked;
            cfg.DohEndpoint               = (tDohEndpoint.Text ?? "").Trim();
            cfg.Save();
            onApply?.Invoke();
        }
    }
}
