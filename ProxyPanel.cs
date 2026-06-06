using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Options -> Proxy...
    // Config fields (scheme/host/port/bypass/profiles) on top,
    // a black pane with green text showing routing + request
    // activity below. Apply saves config and asks the main form
    // to re-init the engine.
    // ============================================================
    public class ProxyPanel : LiteFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;

        private ComboBox cmbScheme;
        private TextBox txtHost, txtPort, txtBypass, txtProfile;
        private TextBox log;
        private Button btnTor, btnDirect, btnApply, btnMax, btnClose;

        public ProxyPanel(NetConfig config, Action applyCallback)
            : base("Proxy")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(620, 560 + 30);
            Font = new Font("Tahoma", 8.25f);

            int y = 14;
            AddLabel("Proxy scheme", 14, y);
            cmbScheme = new ComboBox
            {
                Left = 130, Top = y - 3, Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbScheme.Items.AddRange(new object[] { "none", "socks5h", "socks5", "http" });
            cmbScheme.SelectedItem = Has(cfg.ProxyScheme) ? cfg.ProxyScheme : "none";
            ClientArea.Controls.Add(cmbScheme);

            y += 34;
            AddLabel("Host", 14, y);
            txtHost = AddText(130, y, 160, cfg.ProxyHost);
            ClientArea.Controls.Add(new Label { Text = "Port", Left = 320, Top = y, Width = 36, Height = 16, AutoSize = false });
            txtPort = AddText(360, y, 70, cfg.ProxyPort.ToString());

            y += 34;
            AddLabel("Bypass list", 14, y);
            txtBypass = AddText(130, y, 440, cfg.BypassList);
            AddHint("hosts that skip the proxy (comma-separated, * wildcards ok)", 130, y + 22);

            y += 50;
            AddLabel("Profile", 14, y);
            txtProfile = AddText(130, y, 200, cfg.ProfileName);

            btnTor = new RoundButton { Text = "Load: Clossys", Left = 340, Top = y - 4, Width = 110, Height = 24 };
            btnTor.Click += (s, e) => FillFrom(NetConfig.Default());
            ClientArea.Controls.Add(btnTor);

            btnDirect = new RoundButton { Text = "Load: Direct", Left = 456, Top = y - 4, Width = 110, Height = 24 };
            btnDirect.Click += (s, e) => FillFrom(NetConfig.DirectProfile());
            ClientArea.Controls.Add(btnDirect);

            // ── live activity pane ──
            y += 40;
            AddLabel("Proxy activity", 14, y);
            log = new TextBox
            {
                Left = 14, Top = y + 18, Width = 563, Height = 300,
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

            ActivityLog.Proxy("panel opened — scheme=" + cmbScheme.SelectedItem);
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
            if (cat != ActivityLog.Cat.Proxy && cat != ActivityLog.Cat.General) return;
            if (log.IsDisposed) return;
            if (log.InvokeRequired) { log.BeginInvoke((Action)(() => Append(line))); }
            else Append(line);
        }
        private void Append(string line)
        {
            log.AppendText(line + "\r\n");
        }

        private void FillFrom(NetConfig c)
        {
            cmbScheme.SelectedItem = c.ProxyScheme;
            txtHost.Text = c.ProxyHost;
            txtPort.Text = c.ProxyPort.ToString();
            txtBypass.Text = c.BypassList;
            txtProfile.Text = c.ProfileName;
            ActivityLog.Proxy("loaded profile: " + c.ProfileName);
        }

        private void Apply()
        {
            cfg.ProxyScheme = cmbScheme.SelectedItem?.ToString() ?? "none";
            cfg.ProxyHost = txtHost.Text.Trim();
            int.TryParse(txtPort.Text.Trim(), out int p); cfg.ProxyPort = p == 0 ? 9050 : p;
            cfg.BypassList = txtBypass.Text.Trim();
            cfg.ProfileName = txtProfile.Text.Trim();
            cfg.Save();
            ActivityLog.Proxy("applied — " + cfg.ProxyScheme + "://" + cfg.ProxyHost + ":" + cfg.ProxyPort);
            ActivityLog.Proxy("args: " + cfg.BuildBrowserArguments());
            onApply?.Invoke();
        }

        // ── tiny UI helpers ──
        private bool Has(string s) => !string.IsNullOrWhiteSpace(s);
        private void AddLabel(string t, int x, int y)
            => ClientArea.Controls.Add(new Label { Text = t, Left = x, Top = y, Width = 110, Height = 16, AutoSize = false });
        private void AddHint(string t, int x, int y)
            => ClientArea.Controls.Add(new Label { Text = t, Left = x, Top = y, Width = 440, ForeColor = Color.Gray, AutoSize = false });
        private TextBox AddText(int x, int y, int w, string val)
        {
            var tb = new TextBox { Left = x, Top = y - 3, Width = w, Text = val ?? "" };
            ClientArea.Controls.Add(tb); return tb;
        }
    }
}
