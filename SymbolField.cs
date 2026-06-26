using System;
using System.Collections.Generic;
using System.Drawing;                 // PointF, Rectangle  — coordinates, not pixels
using System.Drawing.Drawing2D;       // Matrix, GraphicsPath — operators + frames
using System.Text;

namespace Aloha.Proof
{
    // ============================================================
    // SymbolField — the bicycle with a motor on it.
    //
    // System.Drawing is used here as a SPATIAL-LOGIC engine, not a
    // renderer. Every spot holds a SYMBOL (not a colour), and the
    // library's geometry does the work:
    //
    //   * GraphicsPath  = a frame you PLACE (declares a registry).
    //   * Region.Intersect / IsVisible = Falcighol intersection +
    //     "what fell into the registry" — set logic over placed frames.
    //   * Matrix.RotateAt + TransformPoints = an OPERATOR that
    //     permutes which symbol sits at which coordinate.
    //
    // Not a single paint call. The rendering half of the library
    // simply goes unused; the coordinate algebra is the motor.
    // Call SymbolField.Structure() to run it; it prints text grids.
    // ============================================================
    public static class SymbolField
    {
        const int W = 16, H = 12;
        static char[,] cell = new char[H, W];

        public static void Structure()
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    cell[y, x] = '.';                          // neutral ground symbol

            // scatter interactions as symbols at points
            Place('a', 3, 2); Place('b', 11, 3); Place('c', 7, 6);
            Place('d', 5, 8); Place('e', 12, 9);
            Dump("field (seeded):");

            // ── REGISTRY BY PLACEMENT ──
            // declare two frames; intersect them with GDI+ region algebra.
            // the overlap is the liminal registry — things fall into it.
            using (var pA = new GraphicsPath())
            using (var pB = new GraphicsPath())
            {
                pA.AddRectangle(new Rectangle(2, 1, 8, 7));    // one placed frame
                pB.AddEllipse(new Rectangle(5, 3, 9, 8));      // another placed frame
                using (var reg = new Region(pA))
                using (var rB  = new Region(pB))
                {
                    reg.Intersect(rB);                         // chained intersection

                    var caught = new List<string>();
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            bool inside = reg.IsVisible(new PointF(x + 0.5f, y + 0.5f));
                            if (inside && cell[y, x] != '.') caught.Add(cell[y, x] + "@(" + x + "," + y + ")");
                            else if (inside && cell[y, x] == '.') cell[y, x] = ':';   // mark registry footprint
                        }
                    Dump("registry = overlap of two placed frames (':')  caught: "
                         + (caught.Count > 0 ? string.Join(" ", caught) : "(none)"));
                }
            }

            // ── TRANSFORM AS OPERATOR ──
            // a Matrix rotation permutes symbol positions. System.Drawing
            // computes the coordinate algebra; we read it as a rewrite,
            // never as a rendered rotation.
            RotateSymbols(90f);
            Dump("after 90 deg matrix operator (symbols permuted, not drawn):");
        }

        static void RotateSymbols(float deg)
        {
            var pts  = new List<PointF>();
            var syms = new List<char>();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (cell[y, x] != '.' && cell[y, x] != ':')
                    { pts.Add(new PointF(x, y)); syms.Add(cell[y, x]); }

            var arr = pts.ToArray();
            using (var m = new Matrix())
            {
                m.RotateAt(deg, new PointF((W - 1) / 2f, (H - 1) / 2f));
                m.TransformPoints(arr);                        // the motor: pure coordinate logic
            }

            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (cell[y, x] != ':') cell[y, x] = '.';   // keep the registry, clear movers

            for (int i = 0; i < arr.Length; i++)
            {
                int nx = (int)Math.Round(arr[i].X), ny = (int)Math.Round(arr[i].Y);
                if (nx >= 0 && nx < W && ny >= 0 && ny < H) cell[ny, nx] = syms[i];
            }
        }

        static void Place(char s, int x, int y)
        {
            if (x >= 0 && x < W && y >= 0 && y < H) cell[y, x] = s;
        }

        static void Dump(string title)
        {
            Console.WriteLine(title);
            for (int y = 0; y < H; y++)
            {
                var sb = new StringBuilder();
                for (int x = 0; x < W; x++) sb.Append(cell[y, x]).Append(' ');
                Console.WriteLine(sb.ToString());
            }
            Console.WriteLine();
        }
    }
}
