using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // LiteFrame — the "modern" base frame for the config panels
    // (Proxy / Network / Browser Configuration). Mirrors the main
    // Aloha window: frameless, draggable, vertical grey gradient
    // (#FCFCFD -> #D8D8DC), a centred title, and min/close buttons.
    //
    // The cmd windows (Console / Live headers) keep DafyFrame's
    // dark dafy-node frame; this is the lighter counterpart.
    // Subclasses fill ClientArea.
    // ============================================================
    public class LiteFrame : Form
    {
        [DllImport("user32.dll")] static extern int SendMessage(IntPtr h, int m, int w, int l);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        const int WM_NCLBUTTONDOWN = 0xA1, HT_CAPTION = 0x2;

        protected const int TITLE_H = 22;

        private readonly string titleText;

        protected Panel ClientArea;

        public LiteFrame(string title)
        {
            titleText = title;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(245, 245, 246);
            DoubleBuffered = true;
            Padding = new Padding(1, TITLE_H, 1, 1);

            // taskbar/window icon (the OPT icon), not painted on the bar
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                string ico = System.IO.Path.Combine(dir, "OptIcon.ico");
                if (System.IO.File.Exists(ico)) this.Icon = new Icon(ico);
            }
            catch { }

            ClientArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(245, 245, 246)
            };
            Controls.Add(ClientArea);

            MouseDown += OnFrameMouseDown;
            Paint += PaintFrame;
            Resize += (s, e) => Invalidate();
        }

        private void OnFrameMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Y <= TITLE_H)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        private void PaintFrame(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;

            // whole-surface vertical grey gradient (matches the main window)
            using (var brush = new LinearGradientBrush(
                this.ClientRectangle,
                Color.FromArgb(252, 252, 253),
                Color.FromArgb(216, 216, 220),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, this.ClientRectangle);
            }

            // centred title (dark text), like the main window's bar
            using (var f = new Font("Tahoma", 9f, FontStyle.Bold))
            {
                var size = g.MeasureString(titleText, f);
                g.DrawString(titleText, f, Brushes.Black,
                    (Width - size.Width) / 2f, (TITLE_H - size.Height) / 2f + 1);
            }

            // no title-bar buttons — maximize/close live on the bottom button row
        }
    }
}
