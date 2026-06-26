using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Aloha
{
    // ============================================================
    // DevToolsWindow — an in-frame inspector. A DafyFrame (so it
    // wears the Win9x chrome) hosting its own WebView2, owned by
    // Form1 (so it stays above the main window and never sinks
    // behind it). The inner WebView2 loads a SELF-HOSTED copy of
    // the DevTools frontend and points it at the main engine's
    // loopback CDP endpoint.
    //
    // Security: this window opens NOTHING. The debug port is opened
    // by the main engine only when the user enables "Embedded
    // DevTools" in Browser Configuration, and it is loopback-only,
    // ephemeral, and origin-locked to https://devtools.aloha — so
    // no foreign page can connect. This window merely reads the
    // already-chosen port and connects to it through the local
    // frontend.
    //
    // The DevTools frontend FILES must be present in  .\devtools\
    // (an inspector.html plus its assets), sourced separately and
    // version-matched to the WebView2 runtime — just like tor.exe
    // lives in .\tor\. If absent, this shows an instruction page
    // instead of a blank window, and the footer always offers the
    // native DevTools window as a fallback.
    // ============================================================
    public class DevToolsWindow : DafyFrame
    {
        private const string VHOST = "devtools.aloha";   // must equal --remote-allow-origins origin host

        private readonly string dataFolder;     // engine user-data dir holding DevToolsActivePort
        private readonly string appDir;          // exe dir; .\devtools\ frontend lives under it
        private readonly string inspectedUrl;    // current page URL, to pick the right CDP target
        private readonly Action openNativeFallback;

        private WebView2 inspector;

        public DevToolsWindow(string dataFolder, string appDir, string inspectedUrl, Action openNativeFallback)
            : base("DEVTOOLS", "DevTools")
        {
            this.dataFolder        = dataFolder;
            this.appDir            = appDir;
            this.inspectedUrl      = inspectedUrl ?? "";
            this.openNativeFallback = openNativeFallback;

            Size = new Size(960, 600);

            inspector = new WebView2 { Dock = DockStyle.Fill };
            // Its own user-data folder — never share the engine's profile, and never
            // open a debug port of its own (default creation properties: no args).
            inspector.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = dataFolder + "_devtools"
            };
            ClientArea.Controls.Add(inspector);
            inspector.BringToFront();

            // footer escape hatch — always available, even if the embed fails
            MakeLabeledButton("Native DevTools window", () =>
            {
                try { openNativeFallback?.Invoke(); } catch { }
            });

            Load += async (s, e) => await StartAsync();
        }

        private async Task StartAsync()
        {
            try
            {
                var ensure = inspector.EnsureCoreWebView2Async();
                if (await Task.WhenAny(ensure, Task.Delay(12000)) != ensure)
                {
                    ShowMessage("INSPECTOR ENGINE TIMEOUT",
                        "The inspector's own WebView2 took too long to start. Try again, "
                        + "or use the native DevTools window from the footer.");
                    return;
                }
                await ensure;

                // self-host the frontend; this is the ONLY origin the engine will let connect
                string frontendDir = ResolveFrontendDir(appDir);
                string indexFile   = frontendDir == null ? null : Path.Combine(frontendDir, "inspector.html");
                if (frontendDir == null || !File.Exists(indexFile))
                {
                    ShowMessage("FRONTEND NOT BUNDLED",
                        "No inspector.html found. Put a DevTools frontend build (inspector.html "
                        + "+ its assets) in a 'devtools' folder beside the .exe:\r\n\r\n    "
                        + Path.Combine(appDir, "devtools") + "\\\r\n\r\n"
                        + "A 'devtools' folder in a parent directory is also accepted, so it can "
                        + "live in the source tree during development. Sourced separately, like "
                        + "tor.exe in .\\tor\\. Until then, use the native DevTools window below.");
                    return;
                }

                try
                {
                    inspector.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        VHOST, frontendDir, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { /* already mapped on a reused core */ }

                int port = NetConfig.EmbeddedDevToolsPort;

                string ws = await Task.Run(() => DiscoverPageWs(port, inspectedUrl));
                if (string.IsNullOrEmpty(ws))
                {
                    ShowMessage("DEBUG PORT NOT ANSWERING",
                        "No CDP endpoint answered on 127.0.0.1:" + port + ". Enable \"Embedded "
                        + "DevTools\" in Browser Configuration (Diagnostics) and Apply to reload "
                        + "the engine — the browser args line in the console should then include "
                        + "--remote-debugging-port=" + port + ". Load a page, then reopen this window.");
                    return;
                }

                // ws value carries no scheme — the frontend prepends ws://
                if (ws.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) ws = ws.Substring(5);
                string url = "https://" + VHOST + "/inspector.html?ws=" + ws;
                SetTitle("DevTools — 127.0.0.1:" + port);
                inspector.CoreWebView2.Navigate(url);
                ActivityLog.General("DevTools: embedded inspector attached on 127.0.0.1:" + port);
            }
            catch (Exception ex)
            {
                ShowMessage("INSPECTOR ERROR", ex.Message
                    + "\r\n\r\nUse the native DevTools window from the footer.");
            }
        }

        // Find the devtools frontend folder: beside the exe first, then walk up a
        // few parents so it can sit in the source tree during development (the exe
        // runs from bin\x64\Debug\net48, the folder may be at the project root).
        private static string ResolveFrontendDir(string appDir)
        {
            try
            {
                var dir = new DirectoryInfo(appDir);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    string cand = Path.Combine(dir.FullName, "devtools");
                    if (File.Exists(Path.Combine(cand, "inspector.html"))) return cand;
                }
            }
            catch { }
            return null;
        }

        // GET http://127.0.0.1:<port>/json/list over loopback and pick the page
        // target's WebSocket debugger URL (preferring the one matching the page
        // we're inspecting). Loopback only; never leaves the machine.
        private static string DiscoverPageWs(int port, string inspectedUrl)
        {
            string json = null;
            // the endpoint can lag a moment after an engine reinit — retry briefly
            for (int attempt = 0; attempt < 6; attempt++)
            {
                json = HttpGetLoopback("http://127.0.0.1:" + port + "/json/list", 1500);
                if (!string.IsNullOrEmpty(json)) break;
                System.Threading.Thread.Sleep(400);
            }
            if (string.IsNullOrEmpty(json)) return null;

            string firstPageWs = null;
            // /json/list is a flat (non-nested) array — split into object chunks
            string[] chunks = json.Split(new[] { "},{", "}, {", "},\n{" }, StringSplitOptions.None);
            foreach (var ch in chunks)
            {
                var tm = Regex.Match(ch, "\"type\"\\s*:\\s*\"([^\"]+)\"");
                if (!tm.Success || !tm.Groups[1].Value.Equals("page", StringComparison.OrdinalIgnoreCase))
                    continue;
                var wm = Regex.Match(ch, "\"webSocketDebuggerUrl\"\\s*:\\s*\"(ws://[^\"]+)\"");
                if (!wm.Success) continue;
                string ws = wm.Groups[1].Value;
                if (firstPageWs == null) firstPageWs = ws;

                var um = Regex.Match(ch, "\"url\"\\s*:\\s*\"([^\"]*)\"");
                if (um.Success && !string.IsNullOrEmpty(inspectedUrl))
                {
                    string u = um.Groups[1].Value;
                    if (u == inspectedUrl || u.StartsWith(inspectedUrl, StringComparison.OrdinalIgnoreCase)
                        || inspectedUrl.StartsWith(u, StringComparison.OrdinalIgnoreCase))
                        return ws;   // best match for the active page
                }
            }
            return firstPageWs;
        }

        private static string HttpGetLoopback(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;            // never route the debug query through any proxy
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Aloha-DevTools";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // Win9x-styled status page shown in place of the inspector when something
        // isn't ready — black ground, OCR-ish mono, the #a00021 accent.
        private void ShowMessage(string heading, string body)
        {
            if (inspector == null || inspector.CoreWebView2 == null) return;
            string h = WebUtility.HtmlEncode(heading);
            string b = WebUtility.HtmlEncode(body).Replace("\r\n", "<br>");
            string html =
                "<!doctype html><html><head><meta charset='utf-8'><style>"
                + "html,body{margin:0;height:100%;background:#000;color:#cfcfcf;"
                + "font:13px 'Consolas','OCR A',monospace}"
                + ".w{padding:26px 30px;max-width:640px}"
                + "h1{color:#ff3b3b;font-size:14px;letter-spacing:1px;margin:0 0 14px}"
                + "p{line-height:1.55;white-space:normal}"
                + "</style></head><body><div class='w'>"
                + "<h1>" + h + "</h1><p>" + b + "</p></div></body></html>";
            inspector.CoreWebView2.NavigateToString(html);
        }
    }
}
