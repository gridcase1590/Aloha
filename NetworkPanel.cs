using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Options -> Network...
    // Resolution + identity: DNS mode, DoH endpoint, host-resolver
    // rules (a per-browser MapAddress), user-agent. Green pane shows
    // resolution outcomes + header/request traffic (what WebView2
    // exposes — not raw DNS packets; that comes later if we tap deeper).
    // ============================================================
    public class NetworkPanel : LiteFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;

        private ComboBox cmbDns;
        private CheckBox chkDoh;
        private TextBox txtDoh, txtRules, txtUA;
        private TextBox log;
        private Button btnApply, btnMax, btnClose;

        public NetworkPanel(NetConfig config, Action applyCallback)
            : base("Network")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(640, 580 + 30);
            Font = new Font("Tahoma", 8.25f);

            int y = 14;
            AddLabel("DNS mode", 14, y);
            cmbDns = new ComboBox
            {
                Left = 150, Top = y - 3, Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbDns.Items.AddRange(new object[] { "remote", "local" });
            cmbDns.SelectedItem = (cfg.DnsMode == "local") ? "local" : "remote";
            ClientArea.Controls.Add(cmbDns);
            AddHint("remote = resolve through the proxy (Tor-side, for .clos/.onion)", 150, y + 22);

            y += 50;
            chkDoh = new CheckBox { Text = "Enable DNS-over-HTTPS", Left = 14, Top = y, Width = 220, Checked = cfg.DohEnabled };
            ClientArea.Controls.Add(chkDoh);
            y += 26;
            AddLabel("DoH endpoint", 14, y);
            txtDoh = AddText(150, y, 440, cfg.DohEndpoint);
            AddHint("e.g. https://mozilla.cloudflare-dns.com/dns-query  (blank = engine default)", 150, y + 22);

            y += 52;
            AddLabel("Host-resolver rules", 14, y);
            txtRules = AddText(150, y, 440, cfg.HostResolverRules);
            AddHint("per-browser MapAddress, e.g.  MAP fbi.gov.clos 127.0.255.218", 150, y + 22);

            y += 52;
            AddLabel("User-agent", 14, y);
            txtUA = AddText(150, y, 440, cfg.UserAgent);
            AddHint("blank = engine default", 150, y + 22);

            // ── live activity pane ──
            y += 50;
            AddLabel("Resolution & header activity", 14, y);
            log = new TextBox
            {
                Left = 14, Top = y + 18, Width = 583, Height = 280,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None,
                WordWrap = false, BorderStyle = BorderStyle.None,
                BackColor = Color.Black, ForeColor = Color.FromArgb(0x33, 0xFF, 0x66),
                Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            ClientArea.Controls.Add(log);

            // dark Win9x scrollbar in place of the native (grey) one
            var logBar = new DarkScrollBar(false, true) { Dock = DockStyle.None };
            logBar.Left = log.Left + log.Width;
            logBar.Top = log.Top;
            logBar.Width = 17;
            logBar.Height = log.Height;
            logBar.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
            ClientArea.Controls.Add(logBar);
            logBar.Attach(log);

            btnApply = new RoundButton { Text = "Apply (reloads engine)", Width = 150, Height = 26 };
            btnMax   = new RoundButton { Text = "Maximize", Width = 80, Height = 26 };
            btnClose = new RoundButton { Text = "Close", Width = 80, Height = 26 };
            btnApply.Anchor = btnMax.Anchor = btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ClientArea.Controls.Add(btnApply); ClientArea.Controls.Add(btnMax); ClientArea.Controls.Add(btnClose);
            Layout += (s, e) => PositionButtons();
            PositionButtons();

            btnApply.Click += (s, e) => Apply();
            btnMax.Click   += (s, e) => WindowState = (WindowState == FormWindowState.Maximized)
                                            ? FormWindowState.Normal : FormWindowState.Maximized;
            btnClose.Click += (s, e) => Close();

            ActivityLog.OnLog += HandleLog;
            FormClosed += (s, e) => ActivityLog.OnLog -= HandleLog;

            ActivityLog.Network("panel opened — dns=" + cmbDns.SelectedItem);
        }

        private void PositionButtons()
        {
            btnClose.Left = ClientArea.ClientSize.Width - btnClose.Width - 14;
            btnClose.Top  = ClientArea.ClientSize.Height - btnClose.Height - 12;
            btnMax.Left   = btnClose.Left - btnMax.Width - 8;
            btnMax.Top    = btnClose.Top;
            btnApply.Left = btnMax.Left - btnApply.Width - 8;
            btnApply.Top  = btnClose.Top;
        }

        private void HandleLog(ActivityLog.Cat cat, string line)
        {
            if (cat != ActivityLog.Cat.Network && cat != ActivityLog.Cat.General) return;
            if (log.IsDisposed) return;
            if (log.InvokeRequired) { log.BeginInvoke((Action)(() => Append(line))); }
            else Append(line);
        }
        private void Append(string line) => log.AppendText(line + "\r\n");

        private void Apply()
        {
            cfg.DnsMode = cmbDns.SelectedItem?.ToString() ?? "remote";
            cfg.DohEnabled = chkDoh.Checked;
            cfg.DohEndpoint = txtDoh.Text.Trim();
            cfg.HostResolverRules = txtRules.Text.Trim();
            cfg.UserAgent = txtUA.Text.Trim();
            cfg.Save();
            ActivityLog.Network("applied — dns=" + cfg.DnsMode +
                                " doh=" + (cfg.DohEnabled ? cfg.DohEndpoint : "off"));
            if (!string.IsNullOrWhiteSpace(cfg.HostResolverRules))
                ActivityLog.Network("resolver-rules: " + cfg.HostResolverRules);
            onApply?.Invoke();
        }

        private void AddLabel(string t, int x, int y)
            => ClientArea.Controls.Add(new Label { Text = t, Left = x, Top = y, Width = 130, Height = 16, AutoSize = false });
        private void AddHint(string t, int x, int y)
            => ClientArea.Controls.Add(new Label { Text = t, Left = x, Top = y, Width = 460, ForeColor = Color.Gray, AutoSize = false });
        private TextBox AddText(int x, int y, int w, string val)
        {
            var tb = new TextBox { Left = x, Top = y - 3, Width = w, Text = val ?? "" };
            ClientArea.Controls.Add(tb); return tb;
        }
    }
}
