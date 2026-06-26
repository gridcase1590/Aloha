using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Options -> Network Scanning (nmap)
    // Runs nmap and shows results. Matches the LiteFrame
    // (grey-gradient) style of the other config panels.
    // ============================================================
    public class NmapScanPanel : DafyFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;

        private CheckBox cEnable, cAggressive, cServiceDetect;
        private TextBox tTarget;
        private RichTextBox rtbResults;
        private Label lblStatus;
        private RoundButton btnScan;
        private bool isScanning;

        public NmapScanPanel(NetConfig config, Action applyCallback, string prefillTarget = null)
            : base("OPT-NMAP", "Network Scanning")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(560, 600);
            Font = new Font("Tahoma", 8.25f);

            // black console body holds the scan output below the settings header
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            ClientArea.Controls.Add(body);

            var scroll = new Panel
            {
                Dock = DockStyle.Top,
                Height = 330,
                AutoScroll = true,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB),
                Padding = new Padding(14, 10, 14, 10)
            };
            ClientArea.Controls.Add(scroll);
            scroll.BringToFront();

            int y = 6;

            y = Group(scroll, "nmap integration", y);
            cEnable = Check(scroll, "Enable nmap network scanning",
                cfg.EnableNmapScanning, ref y);

            y = Group(scroll, "Target", y);

            var targetRow = new Panel { Left = 6, Top = y, Width = 500, Height = 30, BackColor = Color.Transparent };
            var targetLabel = new Label
            {
                Text = "IP / CIDR / host:", Left = 0, Top = 6, Width = 100,
                ForeColor = Color.Black, Font = new Font("Tahoma", 8.25f)
            };
            targetRow.Controls.Add(targetLabel);

            tTarget = new TextBox
            {
                Left = 105, Top = 3, Width = 280,
                Text = !string.IsNullOrEmpty(prefillTarget) ? prefillTarget : (cfg.NmapDefaultTarget ?? "192.168.1.0/24"),
                Font = new Font("Consolas", 9f)
            };
            targetRow.Controls.Add(tTarget);
            scroll.Controls.Add(targetRow);
            y += 36;

            y = Group(scroll, "Scan options", y);
            cAggressive = Check(scroll, "Aggressive scan (-A): OS detection, version, scripts, traceroute",
                false, ref y);
            cServiceDetect = Check(scroll, "Service / version detection (-sV)",
                true, ref y);

            y = Group(scroll, "Run", y);

            var runRow = new Panel { Left = 6, Top = y, Width = 500, Height = 36, BackColor = Color.Transparent };
            btnScan = new RoundButton { Text = "Start scan", Width = 100, Height = 26, Left = 0, Top = 4 };
            btnScan.Click += (s, e) => RunScan();
            runRow.Controls.Add(btnScan);

            lblStatus = new Label
            {
                Text = "idle", Left = 110, Top = 10, Width = 280, AutoSize = false, Height = 20,
                ForeColor = Color.FromArgb(0x30, 0x70, 0x30),
                Font = new Font("Tahoma", 8.25f)
            };
            runRow.Controls.Add(lblStatus);
            scroll.Controls.Add(runRow);
            y += 42;

            // scan output fills the black console body
            rtbResults = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x0A, 0x0A, 0x0A),
                ForeColor = Color.FromArgb(0x33, 0xFF, 0x66),
                Font = new Font("Consolas", 8.5f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None
            };
            body.Controls.Add(rtbResults);

            BuildButtonBar();
        }

        private void BuildButtonBar()
        {
            // Apply in the DafyFrame footer; max/close are provided by the frame
            MakeLabeledButton("Apply", () => Apply());
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

        private void RunScan()
        {
            if (isScanning) return;
            string target = (tTarget.Text ?? "").Trim();
            if (string.IsNullOrEmpty(target))
            {
                MessageBox.Show("Enter a target.", "nmap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            isScanning = true;
            btnScan.Enabled = false;
            rtbResults.Clear();
            lblStatus.Text = "scanning...";
            lblStatus.ForeColor = Color.FromArgb(0x10, 0x80, 0x10);

            string args = "";
            if (cAggressive.Checked) args += "-A ";
            if (cServiceDetect.Checked) args += "-sV ";
            args += target;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "nmap",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        string error = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();

                        this.BeginInvoke(new Action(() =>
                        {
                            if (!string.IsNullOrEmpty(output))
                                rtbResults.AppendText(output);
                            if (!string.IsNullOrEmpty(error))
                                rtbResults.AppendText("\n[stderr]\n" + error);
                            lblStatus.Text = "scan complete";
                            lblStatus.ForeColor = Color.FromArgb(0x30, 0x70, 0x30);
                            isScanning = false;
                            btnScan.Enabled = true;
                        }));
                    }
                }
                catch (Exception ex)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        rtbResults.AppendText("Failed to run nmap: " + ex.Message + "\n");
                        rtbResults.AppendText("Make sure nmap is installed and on PATH.\n");
                        lblStatus.Text = "error";
                        lblStatus.ForeColor = Color.FromArgb(0x90, 0x20, 0x20);
                        isScanning = false;
                        btnScan.Enabled = true;
                    }));
                }
            });
        }

        private void Apply()
        {
            cfg.EnableNmapScanning = cEnable.Checked;
            cfg.NmapDefaultTarget = (tTarget.Text ?? "").Trim();
            cfg.Save();
            onApply?.Invoke();
        }
    }
}
