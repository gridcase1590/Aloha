using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // A panel that paints the classic Win9x sunken bevel:
    // dark gray on top + left, white on bottom + right (1px each).
    // Put a borderless control inside (docked Fill with small padding)
    // to get the inset-field look from the reference.
    public class BevelPanel : Panel
    {
        public BevelPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.FromArgb(0xBB, 0xBB, 0xBB);
            Padding = new Padding(2, 2, 2, 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int w = Width - 1, h = Height - 1;
            using (var dark = new Pen(Color.FromArgb(0x80, 0x80, 0x80)))
            using (var light = new Pen(Color.White))
            {
                // dark: top + left
                g.DrawLine(dark, 0, 0, w, 0);
                g.DrawLine(dark, 0, 0, 0, h);
                // light: bottom + right
                g.DrawLine(light, 0, h, w, h);
                g.DrawLine(light, w, 0, w, h);
            }
        }
    }
}
