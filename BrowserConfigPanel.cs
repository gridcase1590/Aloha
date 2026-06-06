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
    public class BrowserConfigPanel : LiteFrame
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

        public BrowserConfigPanel(NetConfig config, Action applyCallback)
            : base("Browser Configuration")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(560, 620);
            Font = new Font("Tahoma", 8.25f);

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(245, 245, 246),
                Padding = new Padding(14, 10, 14, 10)
            };
            ClientArea.Controls.Add(scroll);

            int y = 6;

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

            y = Group(scroll, "Interface", y);
            cWin9xScroll = Check(scroll, "Modern Aloha browser scrollbars (laggy)",
                cfg.CfgWin9xBrowserScrollbars, ref y);

            // ── apply / maximize / close bar ──
            var btnApply = new RoundButton { Text = "Apply (reloads engine)", Width = 170, Height = 26 };
            var btnMax   = new RoundButton { Text = "Maximize", Width = 80, Height = 26 };
            var btnClose = new RoundButton { Text = "Close", Width = 80, Height = 26 };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.Transparent };
            btnApply.Click += (s, e) => Apply();
            btnMax.Click   += (s, e) => WindowState = (WindowState == FormWindowState.Maximized)
                                            ? FormWindowState.Normal : FormWindowState.Maximized;
            btnClose.Click += (s, e) => Close();
            bar.Controls.Add(btnApply);
            bar.Controls.Add(btnMax);
            bar.Controls.Add(btnClose);
            void LayoutBar()
            {
                btnClose.Left = bar.ClientSize.Width - btnClose.Width - 10;
                btnClose.Top = 6;
                btnMax.Left = btnClose.Left - btnMax.Width - 8;
                btnMax.Top = 6;
                btnApply.Left = btnMax.Left - btnApply.Width - 8;
                btnApply.Top = 6;
            }
            bar.Resize += (s, e) => LayoutBar();
            ClientArea.Controls.Add(bar);
            this.Shown += (s, e) => LayoutBar();
        }

        private int Group(Panel host, string title, int y)
        {
            if (y > 6) y += 8;
            var l = new Label
            {
                Text = title, Left = 0, Top = y, AutoSize = true,
                Font = new Font("Tahoma", 8.25f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x60, 0x00, 0x10)
            };
            host.Controls.Add(l);
            y += 20;
            var rule = new Panel { Left = 0, Top = y - 2, Width = 500, Height = 1, BackColor = Color.FromArgb(0x88, 0x88, 0x88) };
            host.Controls.Add(rule);
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
            cfg.Save();
            onApply?.Invoke();
        }
    }
}
