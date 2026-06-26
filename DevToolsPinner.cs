using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // DevToolsPinner — keeps the native WebView2 DevTools window
    // sitting just above the main window, so it never sinks behind
    // Form1 when Form1 is clicked.
    //
    // The DevTools window is a cross-process top-level Chromium
    // window (msedgewebview2.exe), so we can't cleanly OWN it.
    // Instead we locate its HWND after OpenDevToolsWindow() and
    // re-assert its z-order (just above the owner) whenever the
    // owner is activated — an owned-window relationship in effect,
    // without the unsupported cross-process GWLP_HWNDPARENT hack.
    //
    // One long-lived instance per owner: it hooks Activated once;
    // Track() is called each time DevTools is (re)opened to (re)find
    // the current window.
    // ============================================================
    public sealed class DevToolsPinner
    {
        private readonly Form owner;
        private readonly Timer finder;
        private IntPtr devHwnd = IntPtr.Zero;
        private int browserPid;
        private int tries;

        public DevToolsPinner(Form owner)
        {
            this.owner = owner;
            owner.Activated += (s, e) => Repin();
            finder = new Timer { Interval = 150 };
            finder.Tick += (s, e) => Find();
        }

        // Call right after web.CoreWebView2.OpenDevToolsWindow().
        public void Track(int browserProcessId)
        {
            browserPid = browserProcessId;
            devHwnd = IntPtr.Zero;
            tries = 0;
            finder.Start();   // the window appears a beat after the call; poll for it
        }

        private void Find()
        {
            if (++tries > 24) { finder.Stop(); return; }   // ~3.6s then give up quietly
            IntPtr h = FindDevToolsWindow(browserPid);
            if (h != IntPtr.Zero)
            {
                devHwnd = h;
                finder.Stop();
                Repin();
            }
        }

        private void Repin()
        {
            if (devHwnd == IntPtr.Zero || !IsWindow(devHwnd) || owner == null || !owner.IsHandleCreated) return;
            // place the DevTools window immediately above the owner, without stealing focus
            SetWindowPos(devHwnd, owner.Handle, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // Find a visible top-level Chromium window titled "DevTools …".
        // Prefer one belonging to our browser process; fall back to any match.
        private static IntPtr FindDevToolsWindow(int browserPid)
        {
            IntPtr match = IntPtr.Zero, pidMatch = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                if (GetClassName2(h) != "Chrome_WidgetWin_1") return true;
                string t = GetWindowText2(h);
                if (string.IsNullOrEmpty(t) || !t.StartsWith("DevTools", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (match == IntPtr.Zero) match = h;
                if (browserPid != 0)
                {
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    if ((int)pid == browserPid) { pidMatch = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return pidMatch != IntPtr.Zero ? pidMatch : match;
        }

        private static string GetWindowText2(IntPtr h)
        {
            int len = GetWindowTextLength(h);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetClassName2(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
