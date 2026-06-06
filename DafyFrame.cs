using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // DafyFrame — frameless base Form, restyled to match Form1:
    //   * light "white" header — Form1's vertical gradient (#FCFCFD -> #D8D8DC)
    //     with the title centred in Tahoma 9 Bold black; draggable.
    //   * Form1-style FOOTER — modern #F0F0F0 strip with decorative INDENTED
    //     bevel cubes + two BLANK indented window buttons (maximize, close)
    //     that pop OUT when pressed (no colour on press; hover tint only:
    //     blue for maximize, red for close).
    //   * OPT icon is the window/taskbar Icon only.
    // Subclasses fill ClientArea (between header and footer) with their controls.
    // (Old dark-ember design preserved separately by the author.)
    // ============================================================
    public class DafyFrame : Form
    {
        [DllImport("user32.dll")] static extern int SendMessage(IntPtr h, int m, int w, int l);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        const int WM_NCLBUTTONDOWN = 0xA1, HT_CAPTION = 0x2;

        protected const int TITLE_H = 22;     // matches Form1's title bar
        private   const int FOOTER_H = 26;    // one-row footer (controls + indents + buttons)
        private static readonly Color cFooter = Color.FromArgb(0xF0, 0xF0, 0xF0);
        private static readonly Font  sBtnFont = new Font("MS Sans Serif", 8f);

        private string panelTitle;
        private Timer  titleTimer;
        private float  marqueeOffset;
        private bool   titleMarquee;

        protected Panel ClientArea;
        private Panel footer;
        private Panel btnMax, btnClose;
        private BevelPanel fIndentWide, fCube1, fCube2;

        // drag-to-resize grip state (manual; borderless forms don't resize via HT*)
        private bool gripDragging, gripDidDrag;
        private Point gripStart;
        private Size  gripStartSize;

        // Subclasses drop their control widgets onto this (left side); the frame
        // keeps the decorative indents + max/close on the right. Items ~20px, Top 3.
        protected Panel Footer { get { return footer; } }

        // Left edge of the frame's right cluster (indents + max/close). A subclass
        // with a stretchy control (e.g. the console input) sizes up to this.
        protected int FooterRightLimit
        {
            get
            {
                if (fCube1 != null && fCube1.Left > 0) return fCube1.Left;
                return (footer != null ? footer.ClientSize.Width : 600) - 130;
            }
        }

        public DafyFrame(string code, string title)
        {
            panelTitle = title;   // 'code' kept in the signature for subclasses; not drawn

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = cFooter;
            DoubleBuffered = true;
            Padding = new Padding(0, TITLE_H, 0, 0);   // reserve the header band

            // OPT icon -> window/taskbar icon only (not painted on the frame)
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                string ico = System.IO.Path.Combine(dir, "OptIcon.ico");
                if (System.IO.File.Exists(ico))
                    this.Icon = new Icon(ico);
            }
            catch { }

            ClientArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0xBB, 0xBB, 0xBB)
            };
            Controls.Add(ClientArea);   // added first -> Fill lays out last (fills above footer)

            SetupFooter();              // Dock.Bottom; ClientArea fills the space above it
            MinimumSize = new Size(360, 240);

            MouseDown += OnFrameMouseDown;
            Paint += PaintFrame;
            Resize += (s, e) => { Invalidate(); LayoutFooter(); };
            FormClosed += (s, e) => { titleTimer?.Stop(); titleTimer?.Dispose(); };
        }

        // ── drag by the white header ──
        private void OnFrameMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Y <= TITLE_H)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        // ── Form1's white header: vertical gradient + centred bold title ──
        private void PaintFrame(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;

            // gradient computed over the FULL window height (like Form1, whose
            // gradient spans the whole form) so the header band is the same
            // near-white top — then painted only across the title band.
            using (var brush = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                Color.FromArgb(252, 252, 253),   // light grey, top
                Color.FromArgb(216, 216, 220),   // darker grey, bottom
                LinearGradientMode.Vertical))
                g.FillRectangle(brush, 0, 0, Width, TITLE_H);

            // 1px hairline under the header
            using (var line = new Pen(Color.FromArgb(0xC8, 0xC8, 0xCC)))
                g.DrawLine(line, 0, TITLE_H - 1, Width, TITLE_H - 1);

            // title — Tahoma 9 Bold, black. Centred if it fits; if marquee is
            // enabled and the text is wider than the header, it scrolls left
            // (looping), clipped to the header band.
            using (var f = new Font("Tahoma", 9f, FontStyle.Bold))
            {
                string t = panelTitle ?? "";
                float pad = 8f, avail = Width - pad * 2;
                SizeF sz = g.MeasureString(t, f);
                if (!titleMarquee || sz.Width <= avail)
                {
                    using (var sf = new StringFormat
                           { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(t, f, Brushes.Black, new RectangleF(0, 0, Width, TITLE_H), sf);
                }
                else
                {
                    const float GAP = 60f;
                    float period = sz.Width + GAP;
                    float o = marqueeOffset % period;
                    float y = (TITLE_H - sz.Height) / 2f;
                    var st = g.Save();
                    g.SetClip(new RectangleF(pad, 0, avail, TITLE_H));
                    using (var sf = new StringFormat { FormatFlags = StringFormatFlags.NoWrap })
                    {
                        g.DrawString(t, f, Brushes.Black, pad - o, y, sf);
                        g.DrawString(t, f, Brushes.Black, pad - o + period, y, sf);
                    }
                    g.Restore(st);
                }
            }
        }

        // update the header title (and restart the scroll from the left)
        protected void SetTitle(string text)
        {
            panelTitle = text ?? "";
            marqueeOffset = 0f;
            InvalidateHeader();
        }

        // let the title scroll horizontally when it is wider than the header
        protected void EnableTitleMarquee()
        {
            titleMarquee = true;
            if (titleTimer == null)
            {
                titleTimer = new Timer { Interval = 40 };
                titleTimer.Tick += (s, e) => { marqueeOffset += 1.2f; InvalidateHeader(); };
                titleTimer.Start();
            }
        }

        private void InvalidateHeader()
        {
            if (IsHandleCreated) Invalidate(new Rectangle(0, 0, Math.Max(1, Width), TITLE_H));
        }

        // ── Form1-style footer: indented cubes + blank max/close buttons ──
        private void SetupFooter()
        {
            footer = new Panel { Dock = DockStyle.Bottom, Height = FOOTER_H, BackColor = cFooter };

            // small decorative indents sit just left of the buttons (right cluster)
            fIndentWide = new BevelPanel { Left = 4, Top = 3, Width = 200, Height = 20, BackColor = cFooter };
            fCube1 = new BevelPanel { Width = 20, Height = 20, Top = 3, BackColor = cFooter };
            fCube2 = new BevelPanel { Width = 46, Height = 20, Top = 3, BackColor = cFooter };

            btnMax = MakeFooterButton("max", () =>
                WindowState = (WindowState == FormWindowState.Maximized)
                    ? FormWindowState.Normal : FormWindowState.Maximized);
            btnClose = MakeFooterButton("close", () => Close());
            btnMax.Top = 3; btnClose.Top = 3;

            footer.Controls.Add(fIndentWide);
            footer.Controls.Add(fCube1);
            footer.Controls.Add(fCube2);
            MakeResizeGrip(fCube1);          // the gray footer cubes now resize the window
            MakeResizeGrip(fCube2);
            footer.Controls.Add(btnMax);
            footer.Controls.Add(btnClose);
            fIndentWide.SendToBack();   // sits under any subclass footer controls; shows through the gap
            footer.Resize += (s, e) => LayoutFooter();

            Controls.Add(footer);
            LayoutFooter();
        }

        private void LayoutFooter()
        {
            if (footer == null || btnMax == null) return;
            int w = footer.ClientSize.Width;
            btnClose.Left = w - btnClose.Width - 3;
            btnMax.Left   = btnClose.Left - btnMax.Width - 2;
            fCube2.Left   = btnMax.Left - fCube2.Width - 6;
            fCube1.Left   = fCube2.Left - fCube1.Width - 3;
            fIndentWide.Width = Math.Max(20, fCube1.Left - fIndentWide.Left - 4);
        }

        // a BLANK indented cube; hover tints (close red, max blue); press pops OUT.
        private Panel MakeFooterButton(string kind, Action onClick)
        {
            var b = new Panel { Width = 44, Height = 20, Cursor = Cursors.Hand, BackColor = cFooter };
            bool hover = false, pressed = false;
            b.Paint += (s, e) =>
            {
                var g = e.Graphics;
                var r = b.ClientRectangle;
                // no colour — indented (sunken) at rest, pops OUT (raised) while pressed
                Color fill = pressed ? Color.FromArgb(0xE4, 0xE4, 0xE4)
                                     : (hover ? Color.FromArgb(0xF7, 0xF7, 0xF7) : cFooter);
                using (var br = new SolidBrush(fill)) g.FillRectangle(br, r);
                int w1 = r.Width - 1, h1 = r.Height - 1;
                Color tl  = pressed ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);   // idle = sunken
                Color brc = pressed ? Color.FromArgb(0x80, 0x80, 0x80) : Color.White;
                using (var pTl = new Pen(tl))  { g.DrawLine(pTl, 0, 0, w1, 0);  g.DrawLine(pTl, 0, 0, 0, h1); }
                using (var pBr = new Pen(brc)) { g.DrawLine(pBr, 0, h1, w1, h1); g.DrawLine(pBr, w1, 0, w1, h1); }
            };
            b.MouseEnter += (s, e) => { hover = true; b.Invalidate(); };
            b.MouseLeave += (s, e) => { hover = false; pressed = false; b.Invalidate(); };
            b.MouseDown  += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; b.Invalidate(); } };
            b.MouseUp    += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                bool fire = pressed && b.ClientRectangle.Contains(e.Location);
                pressed = false; b.Invalidate();
                if (fire) onClick();
            };
            return b;
        }

        // a LABELLED indented button (sunken cube + centred text) for subclass
        // control bars; hover lightens, press pops it OUT. Keeps the text.
        protected Panel MakeLabeledButton(string text, Action onClick)
        {
            int wText = TextRenderer.MeasureText(text, sBtnFont).Width;
            var b = new Panel { Width = Math.Max(48, wText + 18), Height = 20, Cursor = Cursors.Hand, BackColor = cFooter, Text = text };
            bool hover = false, pressed = false;
            b.Paint += (s, e) =>
            {
                var g = e.Graphics;
                var r = b.ClientRectangle;
                Color fill = pressed ? Color.FromArgb(0xE4, 0xE4, 0xE4)
                                     : (hover ? Color.FromArgb(0xF7, 0xF7, 0xF7) : cFooter);
                using (var br = new SolidBrush(fill)) g.FillRectangle(br, r);
                int w1 = r.Width - 1, h1 = r.Height - 1;
                Color tl  = pressed ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);   // idle = sunken
                Color brc = pressed ? Color.FromArgb(0x80, 0x80, 0x80) : Color.White;
                using (var pTl = new Pen(tl))  { g.DrawLine(pTl, 0, 0, w1, 0);  g.DrawLine(pTl, 0, 0, 0, h1); }
                using (var pBr = new Pen(brc)) { g.DrawLine(pBr, 0, h1, w1, h1); g.DrawLine(pBr, w1, 0, w1, h1); }
                TextRenderer.DrawText(g, b.Text, sBtnFont, r, Color.Black,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            b.MouseEnter += (s, e) => { hover = true; b.Invalidate(); };
            b.MouseLeave += (s, e) => { hover = false; pressed = false; b.Invalidate(); };
            b.MouseDown  += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; b.Invalidate(); } };
            b.MouseUp    += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                bool fire = pressed && b.ClientRectangle.Contains(e.Location);
                pressed = false; b.Invalidate();
                if (fire) onClick();
            };
            return b;
        }

        // Wire a control (e.g. the scrollbar corner cube) as a bottom-right
        // drag-to-resize grip. Manual tracking — borderless forms don't resize
        // via the native HT* messages. MinimumSize clamps the floor.
        protected void MakeResizeGrip(Control grip)
        {
            grip.Cursor = Cursors.SizeNWSE;
            grip.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                gripDragging = true; gripDidDrag = false;
                gripStart = Cursor.Position; gripStartSize = this.Size;
            };
            grip.MouseMove += (s, e) =>
            {
                if (!gripDragging) return;
                var now = Cursor.Position;
                int dx = now.X - gripStart.X, dy = now.Y - gripStart.Y;
                if (!gripDidDrag && Math.Abs(dx) + Math.Abs(dy) < 4) return;
                gripDidDrag = true;
                if (WindowState == FormWindowState.Maximized) return;
                this.Size = new Size(gripStartSize.Width + dx, gripStartSize.Height + dy);
            };
            grip.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                gripDragging = false; gripDidDrag = false;
            };
        }
    }
}
