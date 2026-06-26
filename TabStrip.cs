using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace Aloha
{
    // The Aloha tab strip — one button per open tab (title + x close). Drawn with the
    // SYSTEM button renderer so each tab looks exactly like the normal toolbar buttons:
    // no custom background, no colour. The only mark of the active tab is bold text.
    // The strip owns no WebView2s: Form1 holds those and hands each tab's view in as the
    // View object. The strip renders the tabs and raises intent (Activate / Close).
    public class TabStrip : Panel
    {
        public class TabItem
        {
            public string Title;
            public object View;            // the WebView2 for this tab (opaque to the strip)
            public TabItem(string t, object v) { Title = t; View = v; }
        }

        private readonly List<TabItem> tabs = new List<TabItem>();
        private readonly List<Rectangle> tabRects = new List<Rectangle>();
        private readonly List<Rectangle> closeRects = new List<Rectangle>();
        private int active = -1;
        private int hot = -1;
        private bool hotClose = false;
        private Font boldFont;

        // raised on a left click: a tab body (activate) or its x (close).
        public event Action<int> ActivateRequested;
        public event Action<int> CloseRequested;

        private const int TabW   = 134;   // fixed tab width
        private const int Gap    = 3;
        private const int Pad    = 8;
        private const int CloseW = 16;

        public TabStrip()
        {
            DoubleBuffered = true;
            BackColor = SystemColors.Control;          // same surface as the toolbar row
            SetStyle(ControlStyles.ResizeRedraw, true);
            boldFont = new Font(Font, FontStyle.Bold);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (boldFont != null) boldFont.Dispose();
            boldFont = new Font(Font, FontStyle.Bold);
            Invalidate();
        }

        public int Count { get { return tabs.Count; } }
        public int ActiveIndex { get { return active; } }
        public object ActiveView { get { return (active >= 0 && active < tabs.Count) ? tabs[active].View : null; } }
        public object ViewAt(int i) { return (i >= 0 && i < tabs.Count) ? tabs[i].View : null; }

        public int AddTab(string title, object view)
        {
            tabs.Add(new TabItem(string.IsNullOrEmpty(title) ? "tab" : title, view));
            if (active < 0) active = 0;
            ResizeToContent();
            Invalidate();
            return tabs.Count - 1;
        }

        public void RemoveTab(int i)
        {
            if (i < 0 || i >= tabs.Count) return;
            tabs.RemoveAt(i);
            if (tabs.Count == 0) active = -1;
            else if (active >= tabs.Count) active = tabs.Count - 1;
            ResizeToContent();
            Invalidate();
        }

        // the strip is only as wide as its tabs — no empty background band beyond them
        private void ResizeToContent()
        {
            int w = tabs.Count * (TabW + Gap);
            if (w < 1) w = 1;
            if (Width != w) Width = w;
        }

        public void SetActive(int i)
        {
            if (i < 0 || i >= tabs.Count) return;
            active = i;
            Invalidate();
        }

        public void SetTitle(int i, string title)
        {
            if (i < 0 || i >= tabs.Count) return;
            tabs[i].Title = string.IsNullOrEmpty(title) ? "tab" : title;
            Invalidate();
        }

        // repoint a tab at a different view (used when the engine is rebuilt under tab 0)
        public void SetView(int i, object view)
        {
            if (i < 0 || i >= tabs.Count) return;
            tabs[i].View = view;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            tabRects.Clear();
            closeRects.Clear();
            int x = 0;
            int h = Height;

            for (int i = 0; i < tabs.Count; i++)
            {
                Rectangle r = new Rectangle(x, 0, TabW, h);     // full strip height = button height
                tabRects.Add(r);
                bool act = (i == active);
                bool hov = (i == hot);

                // draw the tab as a normal system push button (hot when hovered)
                PushButtonState st = hov ? PushButtonState.Hot : PushButtonState.Normal;
                ButtonRenderer.DrawButton(g, new Rectangle(r.X, r.Y, r.Width, r.Height + 4), st);   // extend below the strip so the bottom border is clipped

                Rectangle cr = new Rectangle(r.Right - CloseW - 4, r.Top + (h - CloseW) / 2, CloseW, CloseW);
                closeRects.Add(cr);

                // title — bold only when this tab is active; otherwise the normal button text
                Rectangle tr = new Rectangle(r.Left + Pad, r.Top, r.Width - Pad - CloseW - 6, r.Height);
                TextRenderer.DrawText(g, tabs[i].Title, act ? boldFont : Font, tr,
                    SystemColors.ControlText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // close glyph — same text colour; bold on hover as the only affordance
                TextRenderer.DrawText(g, "\u00d7", (i == hot && hotClose) ? boldFont : Font, cr,
                    SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                x += TabW + Gap;
            }
        }

        private void HitTest(Point pt, out int tab, out bool close)
        {
            tab = -1;
            close = false;
            for (int i = 0; i < tabRects.Count; i++)
            {
                if (tabRects[i].Contains(pt))
                {
                    tab = i;
                    if (i < closeRects.Count && closeRects[i].Contains(pt)) close = true;
                    return;
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int tab; bool close;
            HitTest(e.Location, out tab, out close);
            if (tab < 0) return;
            if (close) { Action<int> h2 = CloseRequested; if (h2 != null) h2(tab); }
            else { Action<int> h2 = ActivateRequested; if (h2 != null) h2(tab); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int tab; bool close;
            HitTest(e.Location, out tab, out close);
            if (tab != hot || close != hotClose) { hot = tab; hotClose = close; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != -1 || hotClose) { hot = -1; hotClose = false; Invalidate(); }
        }
    }
}
