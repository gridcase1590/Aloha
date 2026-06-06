using System;
using System.Drawing;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Borderless overlay that floats ABOVE the web engine (owned by Form1, so
    // it renders over the WebView2 — a child control can't, because Chromium's
    // airspace paints over siblings). Form1 pins its top-left to the left edge
    // of the "New File" button; the gray footer grip resizes width/height only,
    // so the left edge never crosses the button's line.
    //
    // Layout: the FileTreeView fills the body; a gray one-row footer (matching
    // the other Aloha windows) sits at the bottom with a status indent showing
    // the selected path, a bevel cube, and the resize grip.
    // ============================================================
    public class FileExplorerWindow : Form
    {
        private static readonly Color Modern = Color.FromArgb(0xF0, 0xF0, 0xF0);
        private const int FOOTER_H = 26;

        private FileTreeView view;
        private Panel footer;
        private BevelPanel fIndent, fCube, grip;
        private Label fStatus;

        // forwarded from the inner view so Form1 can drive the button label
        public event Action<string> SelectionChanged;

        // manual resize from the footer grip (top-left stays fixed)
        private bool gripDragging, gripDidDrag;
        private Point gripStart;
        private Size gripStartSize;

        public FileExplorerWindow(Action<string> openCallback)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            MinimumSize = new Size(280, 240);

            view = new FileTreeView(openCallback) { Dock = DockStyle.None };
            view.SelectionChanged += n => SelectionChanged?.Invoke(n);
            view.SelectionPath    += p => { if (fStatus != null) fStatus.Text = p ?? ""; };
            Controls.Add(view);

            // ── footer: gray one-row strip like the rest of the windows ──
            footer  = new Panel { BackColor = Modern };
            fIndent = new BevelPanel { Top = 3, Left = 4, Height = 20, BackColor = Modern };
            fStatus = new Label
            {
                Dock = DockStyle.Fill, BackColor = Modern, ForeColor = Color.FromArgb(0x30, 0x30, 0x30),
                Font = new Font("Consolas", 8.25f), TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, Padding = new Padding(4, 0, 4, 0)
            };
            fIndent.Controls.Add(fStatus);
            fCube = new BevelPanel { Top = 3, Width = 46, Height = 20, BackColor = Modern };
            grip  = new BevelPanel { Top = 3, Width = 20, Height = 20, BackColor = Modern, Cursor = Cursors.SizeNWSE };
            grip.MouseDown += Grip_Down;
            grip.MouseMove += Grip_Move;
            grip.MouseUp   += Grip_Up;
            footer.Controls.Add(fIndent);
            footer.Controls.Add(fCube);
            footer.Controls.Add(grip);
            footer.Resize += (s, e) => LayoutFooter();
            Controls.Add(footer);

            this.Resize += (s, e) => LayoutOverlay();
            LayoutOverlay();
        }

        public void FocusView() { view.FocusTree(); }

        private void LayoutOverlay()
        {
            int cw = ClientSize.Width, ch = ClientSize.Height;
            int bodyH = Math.Max(0, ch - FOOTER_H);
            view.SetBounds(0, 0, cw, bodyH);
            footer.SetBounds(0, bodyH, cw, FOOTER_H);
            LayoutFooter();
        }

        private void LayoutFooter()
        {
            int fw = footer.ClientSize.Width;
            grip.Left    = fw - grip.Width - 3;
            fCube.Left   = grip.Left - fCube.Width - 3;
            fIndent.Left = 4;
            fIndent.Width = Math.Max(20, fCube.Left - fIndent.Left - 4);
        }

        // ── resize from the footer grip; top-left fixed -> left edge stays on the button line ──
        private void Grip_Down(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            gripDragging = true; gripDidDrag = false;
            gripStart = Cursor.Position; gripStartSize = this.Size;
        }
        private void Grip_Move(object s, MouseEventArgs e)
        {
            if (!gripDragging) return;
            var now = Cursor.Position;
            int dx = now.X - gripStart.X, dy = now.Y - gripStart.Y;
            if (!gripDidDrag && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            gripDidDrag = true;
            int w = Math.Max(MinimumSize.Width,  gripStartSize.Width  + dx);
            int h = Math.Max(MinimumSize.Height, gripStartSize.Height + dy);
            this.Size = new Size(w, h);
        }
        private void Grip_Up(object s, MouseEventArgs e) { gripDragging = false; gripDidDrag = false; }
    }
}
