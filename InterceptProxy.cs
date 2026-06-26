using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Aloha
{
    // ============================================================
    // InterceptProxy — a local HTTPS-intercepting proxy (Titanium Web Proxy).
    // WebView2 is pointed at 127.0.0.1:<port>; every request/response flows
    // through here, is logged to ActivityLog.Proxy (so Live headers sees it too)
    // and raised on OnTransaction for the Intercept panel's green feed.
    //
    // If an upstream proxy (Tor/SOCKS/HTTP) is set in NetConfig, we CHAIN to it
    // so the intercept doesn't punch a hole around the privacy route.
    //
    // REQUIRES the NuGet package 'Titanium.Web.Proxy'  (Install-Package
    // Titanium.Web.Proxy). HTTPS interception needs the proxy's root cert
    // trusted — the first Start() raises a UAC prompt to install it into the
    // Windows trust store. Only capture traffic you're authorised to, same as
    // Burp / mitmproxy / Wireshark.
    // ============================================================
    public class InterceptProxy
    {
        private ProxyServer server;
        private ExplicitProxyEndPoint endPoint;

        public bool Running { get; private set; }
        public int  Port    { get; private set; }

        // one line per request ("> GET url") and per response ("< 200 OK url")
        public event Action<string> OnTransaction;

        public void Start(int port, NetConfig cfg)
        {
            if (Running) return;
            Port = port;

            server = new ProxyServer();

            // ── upstream chain (Tor / SOCKS / HTTP) ───────────────────────────
            //  If this block doesn't compile against your Titanium version:
            //   • some versions use Task<ExternalProxy>  -> drop the <IExternalProxy>
            //   • the enum may be ProxyType instead of ExternalProxyType
            //   • or comment the whole block out to test plain intercept first.
            if (cfg != null
                && !string.Equals(cfg.ProxyScheme, "none", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(cfg.ProxyHost))
            {
                var up = new ExternalProxy { HostName = cfg.ProxyHost, Port = cfg.ProxyPort };
                if (cfg.ProxyScheme.ToLowerInvariant().StartsWith("socks"))
                    up.ProxyType = ExternalProxyType.Socks5;     // socks5 / socks5h
                server.GetCustomUpStreamProxyFunc = _ => Task.FromResult<IExternalProxy>(up);
            }
            // ──────────────────────────────────────────────────────────────────

            // create + trust the MITM root CA (UAC the first time only)
            server.CertificateManager.EnsureRootCertificate();

            server.BeforeRequest  += OnRequest;
            server.BeforeResponse += OnResponse;

            endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, port, true);
            server.AddEndPoint(endPoint);
            server.Start();

            Running = true;
            Emit("== intercept proxy listening on 127.0.0.1:" + port
                 + (server.GetCustomUpStreamProxyFunc != null ? "  (chained to upstream)" : "") + " ==");
        }

        public void Stop()
        {
            if (!Running) return;
            try
            {
                server.BeforeRequest  -= OnRequest;
                server.BeforeResponse -= OnResponse;
                server.Stop();
            }
            catch { }
            Running = false;
            Emit("== intercept proxy stopped ==");
        }

        private Task OnRequest(object sender, SessionEventArgs e)
        {
            try
            {
                var rq = e.HttpClient.Request;
                Emit("> " + rq.Method + "  " + rq.Url);
            }
            catch { }
            return Task.CompletedTask;
        }

        private Task OnResponse(object sender, SessionEventArgs e)
        {
            try
            {
                var rq = e.HttpClient.Request;
                var rs = e.HttpClient.Response;
                string ct = rs.ContentType != null ? "  [" + rs.ContentType + "]" : "";
                Emit("< " + (int)rs.StatusCode + " " + rs.StatusDescription + "  " + rq.Url + ct);
            }
            catch { }
            return Task.CompletedTask;
        }

        private void Emit(string line)
        {
            ActivityLog.Proxy(line);        // Live headers / existing green panes see it too
            OnTransaction?.Invoke(line);
        }
    }
}
