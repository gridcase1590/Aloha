using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // PrimarchView — opened by clicking a node on the Instruction Map.
    // A smaller modern Aloha window (DafyFrame chrome). The client area
    // carries a BLACK header: "PrimarchView" in white, with the warning
    //   Danger: "--no-sandbox required here."
    // Below it, the semantic zoom ladder (PROCESS > THREAD > TASK >
    // FUNCTION > REGISTERS) drills into the node level by level.
    //
    // Honest frame: PROCESS/THREAD/REGISTERS, the TASK call stack and the
    // FUNCTION symbols are all read live from the renderer via Frida once
    // attached. Before attach (no Frida, or sandbox left on) the levels fall
    // back to a synthetic sampler and label themselves as such.
    // ============================================================
    public class PrimarchView : DafyFrame
    {
        private static readonly Color Modern = Color.FromArgb(0xF0, 0xF0, 0xF0);
        private static readonly Color Green  = Color.FromArgb(0x33, 0xFF, 0x66);
        private static readonly string[] LEVELS = { "PROCESS", "THREAD", "TASK", "FUNCTION", "REGISTERS" };

        private readonly string nodeName, nodeIp, nodeState, nodeLoad, nodeRole;
        private readonly int nodePort;
        private readonly int rendererPid;
        private int level = 0;

        private Panel ladder, view;
        private int selIdx = 0;
        private readonly List<Panel> levelButtons = new List<Panel>();
        private Font headFont, dangerFont, bodyFont, btnFont;
        private Timer tick;
        private ulong[] regs = new ulong[8];
        private int[] threadLoads = new int[6];
        private List<string> liveStack = new List<string>();   // TASK: real backtrace frames
        private List<string> liveSyms  = new List<string>();   // FUNCTION: real resolved symbols
        private int liveStackTid = 0;
        private FridaBridge frida;
        private bool live = false;              // true once a real agent sample arrives
        private int liveThreadCount = 0;
        private string fridaError = "";         // why we're not live (shown in the status line)

        // frida stderr / lifecycle notes — keep the most recent, marshalled to UI
        private void OnFridaError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            // common, readable hints
            string m = msg;
            if (msg.IndexOf("not recognized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("cannot find", StringComparison.OrdinalIgnoreCase) >= 0)
                m = "frida CLI not found — pip install frida-tools";
            else if (msg.IndexOf("unable to access process", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     msg.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
                m = "can't attach — run Aloha as admin, or enable --no-sandbox in Browser Config";
            fridaError = m;
            try { if (view != null) view.BeginInvoke((Action)(() => view.Invalidate())); } catch { }
        }
        private string[] regNames = { "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rsp", "rip" };
        private readonly object sampleLock = new object();

        public PrimarchView(string name, string ip, string state, string load, string role, int rendererPid = 0)
            : base("OPT-INSTR", "INSTR")
        {
            nodeName = name; nodeIp = ip; nodeState = state; nodeLoad = load; nodeRole = role;
            this.rendererPid = rendererPid;
            // port: stable per node — synthetic until the Frida bridge reports the real endpoint
            int[] common = { 443, 80, 8080, 5090, 4443, 9001 };
            nodePort = common[Math.Abs((name + ip).GetHashCode()) % common.Length];

            Size = new Size(560, 440);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            KeyPreview = true;
            ClientArea.BackColor = Modern;

            headFont   = new Font("Lucida Console", 10f, FontStyle.Bold);
            dangerFont = new Font("Lucida Console", 8f,  FontStyle.Bold);
            bodyFont   = new Font("Consolas", 9f);
            btnFont    = new Font("Tahoma", 8f, FontStyle.Bold);

            // danger moves to its own popup window — but only if the user hasn't
            // already opted into --no-sandbox in Browser Config (if they have, the
            // risk is already acknowledged and the popup is just noise).
            this.Shown += (s, e) =>
            {
                bool alreadyOptedIn = false;
                try { alreadyOptedIn = NetConfig.Load().CfgAllowFridaSandboxOff; } catch { }
                if (!alreadyOptedIn)
                {
                    var warn = new DangerPopup();
                    warn.Show(this);
                }
            };

            // arrow-key selection, file-explorer style
            this.KeyDown += (s, e) =>
            {
                if (level != 1) return;
                if (e.KeyCode == Keys.Up)   { selIdx = Math.Max(0, selIdx - 1); view.Invalidate(); e.Handled = true; }
                if (e.KeyCode == Keys.Down) { selIdx = Math.Min(threadLoads.Length - 1, selIdx + 1); view.Invalidate(); e.Handled = true; }
            };

            // ladder buttons
            ladder = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Modern };
            int bx = 6;
            for (int i = 0; i < LEVELS.Length; i++)
            {
                int idx = i;
                var b = new Panel { Left = bx, Top = 4, Width = 86, Height = 22, BackColor = Modern };
                b.Paint += (s, e) =>
                {
                    var g = e.Graphics; var r = b.ClientRectangle;
                    bool on = (level == idx);
                    using (var bg = new SolidBrush(on ? Color.Black : Modern))
                        g.FillRectangle(bg, r);
                    // win9x bevel: raised when off, sunken when on
                    Color tl = on ? Color.FromArgb(0x80, 0x80, 0x80) : Color.White;
                    Color br = on ? Color.White : Color.FromArgb(0x80, 0x80, 0x80);
                    using (var p1 = new Pen(tl)) { g.DrawLine(p1, 0, 0, r.Width - 1, 0); g.DrawLine(p1, 0, 0, 0, r.Height - 1); }
                    using (var p2 = new Pen(br)) { g.DrawLine(p2, 0, r.Height - 1, r.Width - 1, r.Height - 1); g.DrawLine(p2, r.Width - 1, 0, r.Width - 1, r.Height - 1); }
                    TextRenderer.DrawText(g, LEVELS[idx], btnFont, r,
                        on ? Green : Color.Black,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                };
                b.Click += (s, e) => { level = idx; RepaintAll(); };
                b.Cursor = Cursors.Hand;
                ladder.Controls.Add(b);
                levelButtons.Add(b);
                bx += 90;
            }

            // detail view
            view = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(view, true, null);
            view.Paint += View_Paint;

            ClientArea.Controls.Add(view);
            ClientArea.Controls.Add(ladder);

            tick = new Timer { Interval = 420 };   // discrete sample-and-hold
            tick.Tick += (s, e) => { view.Invalidate(); };   // repaint; data only updates via Frida
            tick.Start();

            // try to go live: attach Frida to the renderer if we were handed a PID
            if (rendererPid > 0)
            {
                string agent = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                    "instr_agent.js");
                frida = new FridaBridge(rendererPid, agent);
                frida.Line += OnFridaLine;
                frida.Error += OnFridaError;
                bool ok = frida.Start();
                if (!ok && string.IsNullOrEmpty(fridaError))
                    fridaError = "frida did not start (is frida-tools installed and on PATH?)";
            }

            this.FormClosed += (s, e) =>
            {
                tick?.Stop(); tick?.Dispose();
                frida?.Dispose();
                headFont?.Dispose(); dangerFont?.Dispose(); bodyFont?.Dispose(); btnFont?.Dispose();
            };
        }

        // one JSON line from the agent (raised on a background thread) → marshal to UI
        private void OnFridaLine(string json)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)(() => ApplySample(json)));
            }
            catch { }
        }

        private void ApplySample(string json)
        {
            lock (sampleLock)
            {
                if (json.Contains("\"t\":\"ready\"") || json.Contains("\"t\":\"proc\""))
                {
                    live = true;
                    fridaError = "";   // agent loaded — clear any earlier attach error
                }
                else if (json.Contains("\"t\":\"threads\""))
                {
                    int n = CountOccurrences(json, "\"id\":");
                    if (n > 0)
                    {
                        live = true;
                        liveThreadCount = n;
                        // pull the real per-thread cpu values in order; -1 means
                        // baseline not yet established (first sample) -> show 0
                        int[] cpus = ParseCpuList(json);
                        for (int i = 0; i < threadLoads.Length; i++)
                            threadLoads[i] = (i < cpus.Length && cpus[i] >= 0) ? cpus[i] : 0;
                    }
                }
                else if (json.Contains("\"t\":\"regs\""))
                {
                    live = true;
                    for (int i = 0; i < regNames.Length && i < regs.Length; i++)
                        regs[i] = ParseHexField(json, regNames[i]);
                }
                else if (json.Contains("\"t\":\"stack\""))
                {
                    live = true;
                    liveStackTid = (int)ParseDecField(json, "tid");
                    liveStack = ParseFrames(json);
                }
                else if (json.Contains("\"t\":\"syms\""))
                {
                    live = true;
                    liveSyms = ParseSyms(json);
                }
            }
            view.Invalidate();
        }

        private static int CountOccurrences(string s, string sub)
        {
            int c = 0, idx = 0;
            while ((idx = s.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0) { c++; idx += sub.Length; }
            return c;
        }

        // extracts each "cpu":NN value, in the order the agent emitted the threads
        private static int[] ParseCpuList(string json)
        {
            var list = new List<int>();
            string key = "\"cpu\":";
            int i = 0;
            while ((i = json.IndexOf(key, i, StringComparison.Ordinal)) >= 0)
            {
                i += key.Length;
                int j = i;
                if (j < json.Length && json[j] == '-') j++;
                while (j < json.Length && char.IsDigit(json[j])) j++;
                int v;
                if (int.TryParse(json.Substring(i, j - i), out v)) list.Add(v);
                i = j;
            }
            return list.ToArray();
        }

        // pulls "name":"0x...." out of the JSON line without a full parser
        private static ulong ParseHexField(string json, string name)
        {
            string key = "\"" + name + "\":\"0x";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int j = i;
            while (j < json.Length && Uri.IsHexDigit(json[j])) j++;
            string hex = json.Substring(i, j - i);
            ulong v; return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v) ? v : 0;
        }

        // truncate a line with an ellipsis so it fits the panel width
        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "\u2026";
        }

        // pulls "name":NN (decimal) out of the JSON line
        private static long ParseDecField(string json, string name)
        {
            string key = "\"" + name + "\":";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int j = i;
            if (j < json.Length && json[j] == '-') j++;
            while (j < json.Length && char.IsDigit(json[j])) j++;
            long v; return long.TryParse(json.Substring(i, j - i), out v) ? v : 0;
        }

        // pulls a string field value: "name":"...."
        private static string ParseStrField(string seg, string name)
        {
            string key = "\"" + name + "\":\"";
            int i = seg.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            i += key.Length;
            int j = seg.IndexOf('"', i);
            return j > i ? seg.Substring(i, j - i) : "";
        }

        // TASK: each backtrace frame -> "module!symbol +offset". Frames arrive as
        // {"addr":..,"mod":..,"sym":..,"off":..} objects inside "frames":[ ... ].
        private static List<string> ParseFrames(string json)
        {
            var outp = new List<string>();
            int arr = json.IndexOf("\"frames\":[", StringComparison.Ordinal);
            if (arr < 0) return outp;
            int i = arr, depth = 0, n = json.Length;
            // walk each {...} object inside the frames array
            int objStart = -1;
            for (; i < n; i++)
            {
                char c = json[i];
                if (c == '{') { if (depth == 0) objStart = i; depth++; }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        string seg = json.Substring(objStart, i - objStart + 1);
                        string mod = ParseStrField(seg, "mod");
                        string sym = ParseStrField(seg, "sym");
                        string off = ParseStrField(seg, "off");
                        string addr = ParseStrField(seg, "addr");
                        string line = (string.IsNullOrEmpty(mod) ? "?" : mod) + "!" +
                                      (string.IsNullOrEmpty(sym) ? addr : sym) +
                                      (off != "0x0" && off.Length > 0 ? " +" + off : "");
                        outp.Add(line);
                        objStart = -1;
                    }
                }
                else if (c == ']' && depth == 0) break;
            }
            return outp;
        }

        // FUNCTION: each resolved symbol -> "module!symbol  tid". Items arrive as
        // {"tid":..,"mod":..,"sym":..,"addr":..} inside "list":[ ... ].
        private static List<string> ParseSyms(string json)
        {
            var outp = new List<string>();
            int arr = json.IndexOf("\"list\":[", StringComparison.Ordinal);
            if (arr < 0) return outp;
            int i = arr, depth = 0, n = json.Length, objStart = -1;
            for (; i < n; i++)
            {
                char c = json[i];
                if (c == '{') { if (depth == 0) objStart = i; depth++; }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        string seg = json.Substring(objStart, i - objStart + 1);
                        string mod = ParseStrField(seg, "mod");
                        string sym = ParseStrField(seg, "sym");
                        long tid = ParseDecField(seg, "tid");
                        string line = (string.IsNullOrEmpty(mod) ? "?" : mod) + "!" +
                                      (string.IsNullOrEmpty(sym) ? "?" : sym) +
                                      "   tid " + tid;
                        outp.Add(line);
                        objStart = -1;
                    }
                }
                else if (c == ']' && depth == 0) break;
            }
            return outp;
        }

        private void RepaintAll()
        {
            foreach (var b in levelButtons) b.Invalidate();
            view.Invalidate();
        }

        private void View_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = view.ClientSize.Width;
            int y = 0;

            using (var hdr = new SolidBrush(Color.White))
            using (var grn = new SolidBrush(Green))
            using (var dim = new SolidBrush(Color.FromArgb(0x2C, 0x8A, 0x4A)))
            using (var bandBrush = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0)))
            using (var bandText = new SolidBrush(Color.FromArgb(0x50, 0x50, 0x50)))
            using (var blackBg = new SolidBrush(Color.Black))
            {
                bool isLocal = rendererPid > 0 && nodeIp != null && nodeIp.StartsWith("pid ");
                string tail = isLocal ? "[" + nodeRole + "]" : "[" + nodeRole + ":" + nodePort + "]";

                // gray header band continuing the title bar, covering the status row
                int bandH = 22;
                g.FillRectangle(bandBrush, 0, 0, w, bandH);
                string status = nodeName + "   " + nodeIp + "   " + nodeState + "   load " + nodeLoad + "%   " + tail;
                g.DrawString(status, bodyFont, bandText, 10, 4);
                y = bandH + 8;

                // heading on its own black plate (black only behind the text)
                string headTxt = isLocal ? nodeIp : (nodeIp + ":" + nodePort);
                using (var big = new Font("Lucida Console", 13f, FontStyle.Bold))
                {
                    var hsz = g.MeasureString(headTxt, big);
                    g.FillRectangle(blackBg, 6, y - 1, hsz.Width + 6, hsz.Height + 2);
                    g.DrawString(headTxt, big, grn, 8, y);
                }
                y += 30;

                switch (level)
                {
                    case 0: // PROCESS — real facts pulled from the live pid
                    {
                        int pid = rendererPid;
                        string image = "?"; int thr = 0; int handles = 0; long wsMB = 0; bool got = false;
                        if (pid > 0)
                        {
                            try
                            {
                                using (var pr = System.Diagnostics.Process.GetProcessById(pid))
                                {
                                    image = pr.ProcessName;
                                    thr = pr.Threads.Count;
                                    handles = pr.HandleCount;
                                    wsMB = pr.WorkingSet64 / (1024 * 1024);
                                    got = true;
                                }
                            }
                            catch { got = false; }
                        }
                        g.DrawString("pid     " + (pid > 0 ? pid.ToString() : "(none)"), bodyFont, grn, 10, y); y += 16;
                        g.DrawString("image   " + (got ? image + ".exe (" + nodeName + ")" : nodeName), bodyFont, grn, 10, y); y += 16;
                        g.DrawString("threads " + (got ? thr.ToString() : "-") + "    handles " + (got ? handles.ToString() : "-"), bodyFont, grn, 10, y); y += 16;
                        g.DrawString("ws      " + (got ? wsMB + " MB" : "- MB") + "    cpu " + nodeLoad + "%", bodyFont, grn, 10, y); y += 16;
                        break;
                    }
                    case 1: // THREAD — real per-thread CPU when live; honest note otherwise
                    {
                        if (!live)
                        {
                            g.DrawString("per-thread CPU — attach Frida to sample the live threads",
                                bodyFont, dim, 10, y); y += 17;
                            break;
                        }
                        int labelX = 10, barX = 200;
                        for (int i = 0; i < threadLoads.Length; i++)
                        {
                            int rowY = y + i * 18;
                            bool hot = threadLoads[i] >= 70;
                            string lbl = "thr-" + i.ToString("00") + "  load " + threadLoads[i].ToString().PadLeft(3) + "%";
                            if (i == selIdx)
                            {
                                // file-explorer style inverse highlight: green bar, black text
                                var sz = TextRenderer.MeasureText(g, lbl, bodyFont);
                                using (var sb = new SolidBrush(Green))
                                    g.FillRectangle(sb, labelX - 3, rowY - 1, sz.Width + 6, 16);
                                g.DrawString(lbl, bodyFont, Brushes.Black, labelX, rowY);
                            }
                            else
                                g.DrawString(lbl, bodyFont, grn, labelX, rowY);
                            int barW = (int)((w - barX - 30) * (threadLoads[i] / 100f));
                            using (var bar = new SolidBrush(hot ? Color.White : Green))
                                g.FillRectangle(bar, barX, rowY + 3, Math.Max(2, barW), 8);
                        }
                        y += threadLoads.Length * 18;
                        break;
                    }
                    case 2: // TASK — real backtrace of the busiest live thread
                        if (live && liveStack.Count > 0)
                        {
                            g.DrawString("call stack · tid " + liveStackTid, bodyFont, hdr, 10, y); y += 18;
                            for (int i = 0; i < liveStack.Count && i < 14; i++)
                            {
                                string fr = "#" + i.ToString("00") + "  " + liveStack[i];
                                g.DrawString(Clip(fr, 64), bodyFont, i == 0 ? hdr : grn, 16, y);
                                y += 17;
                            }
                        }
                        else
                        {
                            g.DrawString("call stack — attach Frida to read the live backtrace",
                                bodyFont, dim, 10, y); y += 17;
                        }
                        break;
                    case 3: // FUNCTION — real resolved symbols at the live thread PCs
                        if (live && liveSyms.Count > 0)
                        {
                            g.DrawString("resolved symbols · live thread program counters", bodyFont, hdr, 10, y); y += 18;
                            for (int i = 0; i < liveSyms.Count && i < 14; i++)
                            {
                                g.DrawString(Clip(liveSyms[i], 64), bodyFont, grn, 16, y);
                                y += 17;
                            }
                        }
                        else
                        {
                            g.DrawString("symbols — attach Frida to resolve live functions",
                                bodyFont, dim, 10, y); y += 17;
                        }
                        break;
                    case 4: // REGISTERS — real CPU context when live; honest note otherwise
                        if (live)
                        {
                            string[] names = { "RAX", "RBX", "RCX", "RDX", "RSI", "RDI", "RSP", "RIP" };
                            for (int i = 0; i < names.Length; i++)
                            {
                                g.DrawString(names[i] + "  0x" + regs[i].ToString("x16"), bodyFont,
                                    i == 7 ? hdr : grn, 10, y);
                                y += 17;
                            }
                        }
                        else
                        {
                            g.DrawString("registers — attach Frida to read the live CPU context",
                                bodyFont, dim, 10, y); y += 17;
                        }
                        break;
                }

                g.DrawString(live ? ("frida live · renderer pid " + rendererPid + " · " + liveThreadCount + " threads")
                                  : (string.IsNullOrEmpty(fridaError)
                                        ? "synthetic sampler · attaching to renderer pid " + rendererPid + "\u2026"
                                        : "synthetic · " + Clip(fridaError, 70)),
                    bodyFont, dim, 10, view.ClientSize.Height - 22);
            }
        }
    }

    // ============================================================
    // DangerPopup — small Aloha "Warning" window shown when INSTR
    // opens. Carries the --no-sandbox danger line on black.
    // ============================================================
    public class DangerPopup : DafyFrame
    {
        private Font headFont, dangerFont, hintFont;

        public DangerPopup() : base("OPT-WARN", "Warning")
        {
            Size = new Size(540, 150);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;

            headFont   = new Font("Lucida Console", 10f, FontStyle.Bold);
            dangerFont = new Font("Lucida Console", 8f,  FontStyle.Bold);
            hintFont   = new Font("Lucida Console", 8f,  FontStyle.Regular);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            body.Paint += (s, e) =>
            {
                TextRenderer.DrawText(e.Graphics, "INSTR", headFont,
                    new Point(10, 8), Color.White, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, "Danger: \"--no-sandbox required here.\"", dangerFont,
                    new Point(10, 28), Color.White, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics,
                    "Enable: Options \u2192 Browser Configuration \u2192 Diagnostics", hintFont,
                    new Point(10, 50), Color.FromArgb(0x33, 0xFF, 0x66), TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics,
                    "\"Allow INSTR instruction tracing\", then Apply.", hintFont,
                    new Point(10, 66), Color.FromArgb(0x33, 0xFF, 0x66), TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, "(click to dismiss)", hintFont,
                    new Point(10, 90), Color.FromArgb(0x80, 0x80, 0x80), TextFormatFlags.NoPrefix);
            };
            body.Click += (s, e) => Close();
            ClientArea.Controls.Add(body);

            this.FormClosed += (s, e) => { headFont?.Dispose(); dangerFont?.Dispose(); hintFont?.Dispose(); };
        }
    }
}
