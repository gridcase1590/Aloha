using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Options -> Browser Style   (sits just below Browser Configuration)
    // Appearance settings for the shell. For now: the home-page wallpaper
    // (Blueprint) and a switch to turn it off entirely. Apply saves NetConfig
    // and re-renders the home page — no engine reload needed.
    //
    // Uses LiteFrame (grey-gradient header) to match the other config panels.
    // ============================================================
    public class BrowserStylePanel : LiteFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;

        private CheckBox cWallpaper;

        public BrowserStylePanel(NetConfig config, Action applyCallback)
            : base("Browser Style")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(460, 250);
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
            y = Group(scroll, "Home page  ·  wallpaper", y);

            cWallpaper = new CheckBox
            {
                Text = "Show the Blueprint wallpaper on the home page",
                Left = 6, Top = y, Width = 420, Checked = cfg.WallpaperEnabled,
                ForeColor = Color.Black, BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            scroll.Controls.Add(cWallpaper);
            y += 26;

            var note = new Label
            {
                Text = "When off, the home page is a plain dark background.",
                Left = 24, Top = y, Width = 410, Height = 18, AutoSize = false,
                ForeColor = Color.Gray
            };
            scroll.Controls.Add(note);

            // ── apply / close bar ──
            var btnApply = new RoundButton { Text = "Apply", Width = 90, Height = 26 };
            var btnClose = new RoundButton { Text = "Close", Width = 80, Height = 26 };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.Transparent };
            btnApply.Click += (s, e) => Apply();
            btnClose.Click += (s, e) => Close();
            bar.Controls.Add(btnApply);
            bar.Controls.Add(btnClose);
            void LayoutBar()
            {
                btnClose.Left = bar.ClientSize.Width - btnClose.Width - 10;
                btnClose.Top = 6;
                btnApply.Left = btnClose.Left - btnApply.Width - 8;
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
            var rule = new Panel { Left = 0, Top = y - 2, Width = 420, Height = 1, BackColor = Color.FromArgb(0x88, 0x88, 0x88) };
            host.Controls.Add(rule);
            return y + 4;
        }

        private void Apply()
        {
            cfg.WallpaperEnabled = cWallpaper.Checked;
            cfg.Wallpaper = "blueprint.svg";   // Blueprint is the only shipped wallpaper for now
            cfg.Save();
            onApply?.Invoke();
        }
    }
}
