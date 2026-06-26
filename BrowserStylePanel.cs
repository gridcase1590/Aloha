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
    public class BrowserStylePanel : DafyFrame
    {
        private readonly NetConfig cfg;
        private readonly Action onApply;

        private CheckBox cWallpaper;
        private CheckBox cShowBookmark;

        public BrowserStylePanel(NetConfig config, Action applyCallback)
            : base("OPT-STYL", "Browser Style")
        {
            cfg = config;
            onApply = applyCallback;

            Size = new Size(460, 360);
            Font = new Font("Tahoma", 8.25f);

            // decorative black strip, exactly 2 grid squares (64px) tall, at the bottom
            var body = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.Black };
            ClientArea.Controls.Add(body);

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFB),
                Padding = new Padding(14, 10, 14, 10)
            };
            ClientArea.Controls.Add(scroll);
            scroll.BringToFront();

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

            y += 22;
            y = Group(scroll, "Toolbar", y);
            cShowBookmark = new CheckBox
            {
                Text = "Show the bookmark \u2605 button in the address bar",
                Left = 6, Top = y, Width = 420, Checked = cfg.ShowBookmarkButton,
                ForeColor = Color.Black, BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat
            };
            scroll.Controls.Add(cShowBookmark);
            y += 26;

            // ── apply / close bar ──
            // Apply in the DafyFrame footer; close is provided by the frame
            MakeLabeledButton("Apply", () => Apply());
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

        private void Apply()
        {
            cfg.WallpaperEnabled = cWallpaper.Checked;
            cfg.Wallpaper = "blueprint.svg";   // Blueprint is the only shipped wallpaper for now
            cfg.ShowBookmarkButton = cShowBookmark.Checked;
            cfg.Save();
            onApply?.Invoke();
        }
    }
}
