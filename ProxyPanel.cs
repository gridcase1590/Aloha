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
    public class ProxyPanel : DafyFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;
        private readonly Action onClearCache;

        private ComboBox cmbScheme, cmbProfiles;
        private TextBox txtHost, txtPort, txtBypass;
        private TextBox log;
        private Panel head;   // #FAFAFB input band at the top
        private Button btnTor, btnDirect;

        public ProxyPanel(NetConfig config, Action applyCallback, Action clearCacheCallback)
            : base("OPT-PRXY", "Proxy")
        {
            cfg = config;
            onApply = applyCallback;
            onClearCache = clearCacheCallback;

            Size = new Size(900, 350);
            Font = new Font("Tahoma", 8.25f);

            // black console body fills the remaining space below the header
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            ClientArea.Controls.Add(body);
            // #FAFAFB header band holds the inputs; auto-sized to fit them (set below)
            head = new Panel { Dock = DockStyle.Top, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB) };
            ClientArea.Controls.Add(head);
            head.BringToFront();

            int y = 14;
            AddLabel("Proxy scheme", 14, y);
            cmbScheme = new ComboBox
            {
                Left = 130, Top = y - 3, Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbScheme.Items.AddRange(new object[] { "none", "socks5h", "socks5", "http" });
            cmbScheme.SelectedItem = Has(cfg.ProxyScheme) ? cfg.ProxyScheme : "none";
            head.Controls.Add(cmbScheme);

            y += 34;
            AddLabel("Host", 14, y);
            txtHost = AddText(130, y, 160, cfg.ProxyHost);
            head.Controls.Add(new Label { Text = "Port", Left = 320, Top = y, Width = 36, Height = 16, AutoSize = false });
            txtPort = AddText(360, y, 70, cfg.ProxyPort.ToString());

            y += 34;
            AddLabel("Bypass list", 14, y);
            txtBypass = AddText(130, y, 440, cfg.BypassList);
            AddHint("hosts that skip the proxy (comma-separated, * wildcards ok)", 130, y + 22);

            y += 50;
            AddLabel("Profile", 14, y);
            cmbProfiles = new ComboBox { Left = 130, Top = y - 3, Width = 180, DropDownStyle = ComboBoxStyle.DropDown };
            cmbProfiles.SelectedIndexChanged += (s, e) => { if (cmbProfiles.SelectedItem != null) LoadNamed(cmbProfiles.SelectedItem.ToString()); };
            head.Controls.Add(cmbProfiles);

            var btnSave = new RoundButton { Text = "Save", Left = 318, Top = y - 4, Width = 56, Height = 24 };
            btnSave.Click += (s, e) => SaveProfile();
            head.Controls.Add(btnSave);

            var btnDel = new RoundButton { Text = "Delete", Left = 378, Top = y - 4, Width = 60, Height = 24 };
            btnDel.Click += (s, e) => DeleteProfile();
            head.Controls.Add(btnDel);

            var btnFile = new RoundButton { Text = "From File", Left = 442, Top = y - 4, Width = 80, Height = 24 };
            btnFile.Click += (s, e) => LoadFromFile();
            head.Controls.Add(btnFile);

            btnTor = new RoundButton { Text = "Horus config", Left = 526, Top = y - 4, Width = 100, Height = 24 };
            btnTor.Click += (s, e) => FillFrom(NetConfig.Default());
            head.Controls.Add(btnTor);

            btnDirect = new RoundButton { Text = "Direct", Left = 630, Top = y - 4, Width = 68, Height = 24 };
            btnDirect.Click += (s, e) => FillFrom(NetConfig.DirectProfile());
            head.Controls.Add(btnDirect);

            // Apply + a whole-browser cache clear on their own row.
            y += 38;
            var btnApply = new RoundButton { Text = "Apply Settings", Left = 130, Top = y - 4, Width = 110, Height = 24 };
            btnApply.Click += (s, e) => Apply();
            head.Controls.Add(btnApply);

            var btnClear = new RoundButton { Text = "Clear Cache", Left = 246, Top = y - 4, Width = 100, Height = 24 };
            btnClear.Click += (s, e) => { onClearCache?.Invoke(); ActivityLog.Proxy("clear cache requested (whole browser)"); };
            head.Controls.Add(btnClear);

            RefreshProfiles();   // populate the dropdown from .\profiles\*.json

            // header is exactly tall enough for its inputs
            head.Height = y + 26;

            // ── live activity pane (fills the black body) ──
            // ScrollBars=Both so the OS tracks real horizontal + vertical extent;
            // the native bars sit under our DarkScrollBars (covered) and are driven
            // through GetScrollInfo/WM_*SCROLL via AttachScrollable.
            const int SB = 17;
            log = new TextBox
            {
                Left = 0, Top = 0,
                Width = body.ClientSize.Width, Height = body.ClientSize.Height,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                WordWrap = false, BorderStyle = BorderStyle.None,
                BackColor = Color.Black, ForeColor = Color.FromArgb(0x33, 0xFF, 0x66),
                Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            body.Controls.Add(log);

            // vertical dark scrollbar (covers the native one; stops above the corner)
            var vbar = new DarkScrollBar(false, true) { Dock = DockStyle.None };
            vbar.Left = body.ClientSize.Width - SB; vbar.Top = 0;
            vbar.Width = SB; vbar.Height = body.ClientSize.Height - SB;
            vbar.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
            body.Controls.Add(vbar);
            vbar.AttachScrollable(log);
            vbar.BringToFront();

            // horizontal dark scrollbar (covers the native one; stops before the corner)
            var hbar = new DarkScrollBar(true, true) { Dock = DockStyle.None };
            hbar.Left = 0; hbar.Top = body.ClientSize.Height - SB;
            hbar.Width = body.ClientSize.Width - SB; hbar.Height = SB;
            hbar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            body.Controls.Add(hbar);
            hbar.AttachScrollable(log);
            hbar.BringToFront();

            // the square where the two scrollbars meet is the resize grip (the
            // footer cube is hidden for this window — see HideFooterCubes below)
            var corner = new Panel { BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0) };
            corner.SetBounds(body.ClientSize.Width - SB, body.ClientSize.Height - SB, SB, SB);
            corner.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            corner.Cursor = Cursors.SizeNWSE;
            body.Controls.Add(corner);
            corner.BringToFront();
            MakeResizeGrip(corner);
            HideFooterCubes();

            ActivityLog.OnLog += HandleLog;
            FormClosed += (s, e) => ActivityLog.OnLog -= HandleLog;

            ActivityLog.Proxy("panel opened — scheme=" + cmbScheme.SelectedItem);
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
            cmbProfiles.Text = c.ProfileName;
            ActivityLog.Proxy("loaded profile: " + c.ProfileName);
        }

        private void Apply()
        {
            cfg.ProxyScheme = cmbScheme.SelectedItem?.ToString() ?? "none";
            cfg.ProxyHost = txtHost.Text.Trim();
            int.TryParse(txtPort.Text.Trim(), out int p); cfg.ProxyPort = p == 0 ? 9050 : p;
            cfg.BypassList = txtBypass.Text.Trim();
            cfg.ProfileName = cmbProfiles.Text.Trim();
            cfg.Save();
            ActivityLog.Proxy("applied — " + cfg.ProxyScheme + "://" + cfg.ProxyHost + ":" + cfg.ProxyPort);
            ActivityLog.Proxy("args: " + cfg.BuildBrowserArguments());
            onApply?.Invoke();
        }

        // ── profile store: .\profiles\<name>.json ──
        private static string ProfilesDir()
        {
            string d = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
            try { System.IO.Directory.CreateDirectory(d); } catch { }
            return d;
        }
        private static string ProfilePath(string name)
        {
            foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return System.IO.Path.Combine(ProfilesDir(), name + ".json");
        }

        private void RefreshProfiles()
        {
            string current = cmbProfiles.Text;
            cmbProfiles.BeginUpdate();
            cmbProfiles.Items.Clear();
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(ProfilesDir(), "*.json"))
                    cmbProfiles.Items.Add(System.IO.Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            cmbProfiles.EndUpdate();
            cmbProfiles.Text = string.IsNullOrEmpty(current) ? (cfg.ProfileName ?? "default") : current;
        }

        // a full config snapshot carrying the proxy fields currently in the form
        private NetConfig FormSnapshot()
        {
            var snap = new NetConfig();
            snap.CopyFrom(cfg);
            snap.ProxyScheme = cmbScheme.SelectedItem?.ToString() ?? "none";
            snap.ProxyHost   = txtHost.Text.Trim();
            int.TryParse(txtPort.Text.Trim(), out int p); snap.ProxyPort = p == 0 ? 9050 : p;
            snap.BypassList  = txtBypass.Text.Trim();
            snap.ProfileName = cmbProfiles.Text.Trim();
            return snap;
        }

        private void SaveProfile()
        {
            string name = cmbProfiles.Text.Trim();
            if (string.IsNullOrEmpty(name)) { ActivityLog.Proxy("save: name a profile first"); return; }
            try { FormSnapshot().Save(ProfilePath(name)); ActivityLog.Proxy("profile saved: " + name); RefreshProfiles(); cmbProfiles.Text = name; }
            catch (Exception ex) { ActivityLog.Proxy("save failed: " + ex.Message); }
        }

        private void DeleteProfile()
        {
            string name = cmbProfiles.Text.Trim();
            if (string.IsNullOrEmpty(name)) { ActivityLog.Proxy("delete: name a profile first"); return; }
            try
            {
                string path = ProfilePath(name);
                if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); ActivityLog.Proxy("profile deleted: " + name); }
                else ActivityLog.Proxy("delete: no such profile: " + name);
                RefreshProfiles();
            }
            catch (Exception ex) { ActivityLog.Proxy("delete failed: " + ex.Message); }
        }

        private void LoadNamed(string name)
        {
            try { string path = ProfilePath(name); if (System.IO.File.Exists(path)) FillFrom(NetConfig.Load(path)); }
            catch (Exception ex) { ActivityLog.Proxy("load failed: " + ex.Message); }
        }

        private void LoadFromFile()
        {
            using (var ofd = new OpenFileDialog { Filter = "Aloha profile (*.json)|*.json|All files (*.*)|*.*", Title = "Load proxy profile" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try { FillFrom(NetConfig.Load(ofd.FileName)); ActivityLog.Proxy("profile loaded from file: " + System.IO.Path.GetFileName(ofd.FileName)); }
                catch (Exception ex) { ActivityLog.Proxy("load file failed: " + ex.Message); }
            }
        }

        // ── tiny UI helpers ──
        private bool Has(string s) => !string.IsNullOrWhiteSpace(s);
        private void AddLabel(string t, int x, int y)
            => head.Controls.Add(new Label { Text = t, Left = x, Top = y, Width = 110, Height = 16, AutoSize = false });
        private void AddHint(string t, int x, int y)
            => head.Controls.Add(new Label { Text = t, Left = x, Top = y, Width = 440, ForeColor = Color.Gray, AutoSize = false });
        private TextBox AddText(int x, int y, int w, string val)
        {
            var tb = new TextBox { Left = x, Top = y - 3, Width = w, Text = val ?? "" };
            head.Controls.Add(tb); return tb;
        }
    }
}
