using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // Options -> Network map...
    // Observation-only view of this machine's live TCP connection table,
    // drawn as flat geometry: this host at the centre, every remote host a
    // node, every connection an edge. It only READS the edge set the OS
    // already keeps (IPGlobalProperties.GetActiveTcpConnections) — no
    // interception, no payload, nothing is captured or modified.
    //
    // The Sphere over Flatland: the graph apprehended from above, the figures
    // left flat, never descending into a line. A tap, if ever wanted, is a
    // separate deliberate move (Raw TCP / a sniffer) made once an edge is
    // worth it. This panel is the ambient layer only.
    // ============================================================
    public class NetGraphPanel : DafyFrame
    {
        private static readonly Color Modern = Color.FromArgb(0xF0, 0xF0, 0xF0);
        private static readonly Color Green  = Color.FromArgb(0x33, 0xFF, 0x66);
        private const int SB = 17;

        // static radial layout (no animation): each new node gets a stable
        // golden-angle slot so existing nodes never move when the set changes.
        private const double GOLDEN = 2.39996323;   // ~137.5 degrees

        private Canvas canvas;
        private Panel corner, status;
        private Timer poll;
        private int slotSeq = 0;
        private string statusText = "reading…";

        private Font nodeFont, localFont, hudFont, detailFont;

        private readonly Dictionary<string, Node> nodes =
            new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        private Node local;
        private string hoverKey, dragKey;
        private Point mouse;

        private class Node
        {
            public string Key;
            public float X, Y;
            public double Angle;
            public bool Pinned;
            public int Count;
            public bool IsLocal;
            public Dictionary<TcpState, int> States = new Dictionary<TcpState, int>();
            public Color Tone = Color.FromArgb(0x2C, 0x8A, 0x4A);
        }

        private class Canvas : Panel
        {
            public Canvas() { this.DoubleBuffered = true; this.BackColor = Color.Black; }
        }

        public NetGraphPanel() : base("OPT-0700", "Network map")
        {
            Size = new Size(760, 600);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(120, 80);
            ShowInTaskbar = true;
            ClientArea.BackColor = Color.Black;

            nodeFont   = new Font("Consolas", 7.5f);
            localFont  = new Font("Consolas", 8f, FontStyle.Bold);
            hudFont    = new Font("Consolas", 7.5f);
            detailFont = new Font("Consolas", 8f);

            canvas = new Canvas { Dock = DockStyle.None };
            canvas.Paint      += Canvas_Paint;
            canvas.MouseMove  += Canvas_MouseMove;
            canvas.MouseDown  += Canvas_MouseDown;
            canvas.MouseUp    += (s, e) => { dragKey = null; };
            canvas.MouseLeave += (s, e) => { hoverKey = null; canvas.Invalidate(); };

            corner = new Panel { BackColor = Modern };
            MakeResizeGrip(corner);

            // central node = this host, pinned at centre
            local = new Node { Key = "*local*", IsLocal = true, Tone = Green };
            nodes[local.Key] = local;

            // footer: Refresh + Tidy on the left, status text to their right
            var btnRefresh = MakeLabeledButton("Refresh", () => { Poll(); canvas.Invalidate(); });
            var btnTidy = MakeLabeledButton("Tidy", Tidy);
            status = new Panel { Top = 3, Height = 20, Width = 360, BackColor = Modern };
            status.Paint += (s, e) =>
                TextRenderer.DrawText(e.Graphics, statusText, detailFont, status.ClientRectangle,
                    Color.FromArgb(0x44, 0x44, 0x44),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Footer.Controls.Add(btnRefresh);
            Footer.Controls.Add(btnTidy);
            Footer.Controls.Add(status);
            Footer.Resize += (s, e) => LayoutFooterBits(btnRefresh, btnTidy);
            LayoutFooterBits(btnRefresh, btnTidy);

            ClientArea.Controls.Add(canvas);
            ClientArea.Controls.Add(corner);
            ClientArea.Resize += (s, e) => LayoutMain();
            this.Shown += (s, e) => { LayoutMain(); Poll(); };
            this.FormClosed += (s, e) =>
            {
                poll?.Stop(); poll?.Dispose();
                nodeFont?.Dispose(); localFont?.Dispose(); hudFont?.Dispose(); detailFont?.Dispose();
            };

            poll = new Timer { Interval = 2000 };
            poll.Tick += (s, e) => Poll();
            poll.Start();
        }

        private void LayoutMain()
        {
            int cw = ClientArea.ClientSize.Width, ch = ClientArea.ClientSize.Height;
            if (cw < 20 || ch < 20) return;
            canvas.SetBounds(0, 0, cw, ch);
            corner.SetBounds(cw - SB, ch - SB, SB, SB);
            corner.BringToFront();
            Relayout();
            canvas.Invalidate();
        }

        private void LayoutFooterBits(Panel btnRefresh, Panel btnTidy)
        {
            btnRefresh.Left = 6; btnRefresh.Top = 3;
            btnTidy.Left = btnRefresh.Right + 6; btnTidy.Top = 3;
            status.Left = btnTidy.Right + 12; status.Top = 3;
            status.Width = Math.Max(40, FooterRightLimit - status.Left - 8);
            status.Invalidate();
        }

        private void Tidy()
        {
            foreach (var n in nodes.Values) n.Pinned = false;
            Relayout();
            canvas.Invalidate();
        }

        // place each non-pinned remote node at its stable golden-angle slot on a
        // single radius that fits the canvas; the local node stays centre.
        private void Relayout()
        {
            int w = canvas.ClientSize.Width, h = canvas.ClientSize.Height;
            if (w < 10 || h < 10) return;
            float cx = w / 2f, cy = h / 2f;
            local.X = cx; local.Y = cy;
            float radius = Math.Max(70f, Math.Min(w, h) / 2f - 120f);
            foreach (var n in nodes.Values)
            {
                if (n.IsLocal || n.Pinned) continue;
                n.X = cx + (float)Math.Cos(n.Angle) * radius;
                n.Y = cy + (float)Math.Sin(n.Angle) * radius;
            }
        }

        // ── read the edge set (no capture) ──
        private void Poll()
        {
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                var conns = props.GetActiveTcpConnections();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var agg = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                int listeners = 0, total = 0;

                foreach (var c in conns)
                {
                    var r = c.RemoteEndPoint;
                    // listeners / half-open with no real peer aren't edges to anywhere
                    if (r == null || r.Port == 0 ||
                        r.Address.Equals(IPAddress.Any) || r.Address.Equals(IPAddress.IPv6Any))
                    { listeners++; continue; }

                    total++;
                    string key = r.Address.ToString();
                    Node n;
                    if (!agg.TryGetValue(key, out n)) { n = new Node { Key = key }; agg[key] = n; }
                    n.Count++;
                    int cur; n.States.TryGetValue(c.State, out cur); n.States[c.State] = cur + 1;
                    seen.Add(key);
                }

                foreach (var kv in agg)
                {
                    Node existing;
                    if (nodes.TryGetValue(kv.Key, out existing) && !existing.IsLocal)
                    {
                        existing.Count  = kv.Value.Count;
                        existing.States = kv.Value.States;
                        existing.Tone   = ToneFor(kv.Value.States);
                    }
                    else
                    {
                        var n = kv.Value;
                        n.Angle = (slotSeq++) * GOLDEN;   // stable slot; others never shift
                        n.Tone = ToneFor(n.States);
                        nodes[kv.Key] = n;
                    }
                }

                var gone = nodes.Keys.Where(k => k != local.Key && !seen.Contains(k)).ToList();
                foreach (var k in gone) nodes.Remove(k);

                Relayout();
                canvas.Invalidate();

                statusText = (nodes.Count - 1) + " hosts · " + total + " conns · "
                           + listeners + " listening   (TCP, read-only)";
                status.Invalidate();
            }
            catch (Exception ex)
            {
                statusText = "read failed: " + ex.Message;
                status.Invalidate();
            }
        }

        private static Color ToneFor(Dictionary<TcpState, int> st)
        {
            if (st.ContainsKey(TcpState.Established)) return Color.FromArgb(0x33, 0xFF, 0x66);
            if (st.ContainsKey(TcpState.SynSent) || st.ContainsKey(TcpState.SynReceived))
                return Color.FromArgb(0xFF, 0xC0, 0x40);
            return Color.FromArgb(0x2C, 0x8A, 0x4A);   // waiting / closing — dim green
        }

        // ── draw ──
        private void Canvas_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = canvas.ClientSize.Width, h = canvas.ClientSize.Height;
            float cx = local.X, cy = local.Y;

            var list = nodes.Values.ToList();

            // edges
            foreach (var n in list)
            {
                if (n.IsLocal) continue;
                bool hot = (n.Key == hoverKey);
                Color col = hot ? Color.FromArgb(0xCF, 0xFF, 0xDF) : Color.FromArgb(200, n.Tone);
                float wgt = 1f + Math.Min(3, Math.Max(0, n.Count - 1));
                if (hot) wgt = Math.Max(wgt, 2f);
                using (var pen = new Pen(col, wgt))
                    g.DrawLine(pen, cx, cy, n.X, n.Y);
            }

            // remote nodes
            foreach (var n in list)
            {
                if (n.IsLocal) continue;
                bool hot = (n.Key == hoverKey);
                float r = 4f + Math.Min(8, n.Count);
                using (var fill = new SolidBrush(Color.FromArgb(0x06, 0x10, 0x0A)))
                    g.FillEllipse(fill, n.X - r, n.Y - r, r * 2, r * 2);
                using (var pen = new Pen(hot ? Color.White : n.Tone, hot ? 2f : 1.4f))
                    g.DrawEllipse(pen, n.X - r, n.Y - r, r * 2, r * 2);
                using (var tb = new SolidBrush(hot ? Color.White : Color.FromArgb(190, Green)))
                    g.DrawString(n.Key, nodeFont, tb, n.X + r + 3, n.Y - 7);
            }

            // local node
            using (var lfill = new SolidBrush(Color.FromArgb(0x0A, 0x2A, 0x16)))
                g.FillEllipse(lfill, cx - 10, cy - 10, 20, 20);
            using (var lpen = new Pen(Green, 2.2f))
                g.DrawEllipse(lpen, cx - 10, cy - 10, 20, 20);
            using (var ldot = new SolidBrush(Green))
                g.FillEllipse(ldot, cx - 3, cy - 3, 6, 6);
            using (var lb = new SolidBrush(Color.FromArgb(0xCF, 0xFF, 0xDF)))
                g.DrawString("this host", localFont, lb, cx + 13, cy - 8);

            // honest frame-mark
            using (var hb = new SolidBrush(Color.FromArgb(0x2C, 0x6A, 0x40)))
                g.DrawString("observation · TCP edges · no capture", hudFont, hb, 8, h - 16);

            if (hoverKey != null && nodes.ContainsKey(hoverKey) && hoverKey != local.Key)
                DrawDetail(g, nodes[hoverKey], w, h);
        }

        private void DrawDetail(Graphics g, Node n, int w, int h)
        {
            var lines = new List<string> { n.Key, n.Count + (n.Count == 1 ? " connection" : " connections") };
            foreach (var kv in n.States.OrderByDescending(k => k.Value))
                lines.Add("  " + kv.Key + " x" + kv.Value);

            int bw = 0, bh = 8;
            foreach (var ln in lines)
            {
                var sz = g.MeasureString(ln, detailFont);
                bw = Math.Max(bw, (int)sz.Width);
                bh += (int)sz.Height;
            }
            bw += 14;
            int bx = mouse.X + 14, by = mouse.Y + 12;
            if (bx + bw > w) bx = w - bw - 4;
            if (by + bh > h) by = h - bh - 4;
            if (bx < 2) bx = 2; if (by < 2) by = 2;

            using (var bg = new SolidBrush(Color.FromArgb(0xEA, 0x02, 0x12, 0x09)))
                g.FillRectangle(bg, bx, by, bw, bh);
            using (var bp = new Pen(Green))
                g.DrawRectangle(bp, bx, by, bw, bh);

            float yy = by + 4; bool first = true;
            foreach (var ln in lines)
            {
                using (var tb = new SolidBrush(first ? Color.FromArgb(0xCF, 0xFF, 0xDF) : Green))
                    g.DrawString(ln, detailFont, tb, bx + 6, yy);
                yy += g.MeasureString(ln, detailFont).Height; first = false;
            }
        }

        // ── interaction ──
        private string HitTest(Point p)
        {
            string best = null; float bestD = float.MaxValue;
            foreach (var n in nodes.Values)
            {
                if (n.IsLocal) continue;
                float r = 4f + Math.Min(8, n.Count) + 5f;
                float dx = p.X - n.X, dy = p.Y - n.Y;
                float d = dx * dx + dy * dy;
                if (d <= r * r && d < bestD) { bestD = d; best = n.Key; }
            }
            return best;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            mouse = e.Location;
            if (dragKey != null && nodes.ContainsKey(dragKey))
            {
                var n = nodes[dragKey];
                n.X = e.X; n.Y = e.Y; n.Pinned = true;
                canvas.Invalidate();
                return;
            }
            string near = HitTest(e.Location);
            if (near != hoverKey) { hoverKey = near; canvas.Invalidate(); }
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            string near = HitTest(e.Location);
            if (near != null) dragKey = near;
        }
    }
}
