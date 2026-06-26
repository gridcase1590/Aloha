using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // A panel that paints the classic Win9x sunken bevel:
    // dark gray on top + left, white on bottom + right (1px each).
    // Put a borderless control inside (docked Fill with small padding)
    // to get the inset-field look from the reference.
    //
    // Set Raised = true to invert it (white top+left, dark bottom+right) — a
    // popped-OUT cube instead of a sunken one. Default stays sunken, so existing
    // wells are unchanged.
    public class BevelPanel : Panel
    {
        // false = classic sunken (indented); true = raised (popped out)
        public bool Raised { get; set; }

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
            Color tl = Raised ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);
            Color br = Raised ? Color.FromArgb(0x80, 0x80, 0x80) : Color.White;
            using (var penTL = new Pen(tl))
            using (var penBR = new Pen(br))
            {
                // top + left
                g.DrawLine(penTL, 0, 0, w, 0);
                g.DrawLine(penTL, 0, 0, 0, h);
                // bottom + right
                g.DrawLine(penBR, 0, h, w, h);
                g.DrawLine(penBR, w, 0, w, h);
            }
        }
    }
}
