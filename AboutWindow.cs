using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // AboutWindow — Help -> info. The ship face: title, build, the
    // Liberté line, a live Telegram link, and the co-authorship
    // credit. Black text on the light Fab panel.
    // ============================================================
    public class AboutWindow : DafyFrame
    {
        private static readonly Color Fab  = Color.FromArgb(0xFA, 0xFA, 0xFB);
        private static readonly Color Ink  = Color.Black;
        private static readonly Color Dim  = Color.FromArgb(0x55, 0x55, 0x55);
        private static readonly Color Link = Color.FromArgb(0x1E, 0x6B, 0x3A);
        private const string Telegram = "https://t.me/v3rdad";

        public AboutWindow() : base("OPT-ABOUT", "About Aloha")
        {
            Size = new Size(460, 280);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientArea.BackColor = Fab;

            var p = new Panel { Dock = DockStyle.Fill, BackColor = Fab };
            ClientArea.Controls.Add(p);

            p.Controls.Add(new Label
            {
                Text = "ALOHA BROWSER", AutoSize = true, Left = 26, Top = 20,
                ForeColor = Ink, BackColor = Fab, Font = new Font("Consolas", 16f, FontStyle.Bold)
            });
            p.Controls.Add(new Label
            {
                Text = "v" + Form1.VERSION, AutoSize = true, Left = 28, Top = 56,
                ForeColor = Dim, BackColor = Fab, Font = new Font("Consolas", 9f)
            });
            var tg = new LinkLabel
            {
                Text = Telegram, AutoSize = true, Left = 28, Top = 124,
                BackColor = Fab, Font = new Font("Consolas", 12f, FontStyle.Bold),
                LinkColor = Link, ActiveLinkColor = Color.Black, VisitedLinkColor = Link,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            tg.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(Telegram); } catch { }
            };
            p.Controls.Add(tg);

            p.Controls.Add(new Label
            {
                Text = "built by Truth \u0026 Dandy", AutoSize = true, Left = 28, Top = 174,
                ForeColor = Ink, BackColor = Fab, Font = new Font("Lucida Console", 9.5f, FontStyle.Bold)
            });
        }
    }
}
