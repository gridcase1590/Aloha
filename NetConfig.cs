using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Aloha
{
    // ============================================================
    // NetConfig — the browser's networking configuration.
    // Persisted to AlohaNet.json next to the exe. Builds the
    // Chromium argument string WebView2 is created with.
    //
    // Only the browser's own networking is touched here — never
    // OS-level routing. This is the right boundary.
    // ============================================================
    public class NetConfig
    {
        // ── proxy ──
        public string ProxyScheme = "none";        // none | socks5h | socks5 | http
        public string ProxyHost   = "127.0.0.1";
        public int    ProxyPort   = 9050;
        public string BypassList  = "localhost,127.0.0.1,127.0.255.*";

        // ── intercept proxy (Titanium) ──
        // Runtime-only (deliberately NOT persisted): when on, the browser is
        // routed through the local intercept proxy, which itself chains to the
        // upstream above. Starts false every run so we never point at a dead port.
        public bool CfgUseInterceptProxy = false;
        public int  InterceptPort        = 8888;

        // ── network / resolution ──
        // DnsMode is informational for the user; the actual remote-vs-local
        // resolution is driven by the proxy scheme (socks5h = remote).
        public string DnsMode          = "remote"; // remote | local
        public string DohEndpoint      = "";        // empty = default/off
        public bool   DohEnabled       = false;
        public string HostResolverRules = "";       // e.g. MAP fbi.gov.clos 127.0.255.218
        public string UserAgent        = "";        // empty = engine default

        public string ProfileName = "Clossys";

        // ── appearance ──
        public string Wallpaper = "blueprint.svg";   // home-page wallpaper file (served via virtual host)
        public bool   WallpaperEnabled = true;        // false = plain dark home page, no wallpaper

        // ── browser configuration (privacy / leak toggles) ──
        // Defaults are the privacy-forward stance; the panel can untick any.
        // Each maps to one or more Chromium flags in BuildBrowserArguments().
        //   WebRTC
        public bool CfgWebRtcLeakFix      = true;   // disable_non_proxied_udp + permission check
        public bool CfgWebRtcDisableFully = false;  // hard-disable WebRTC entirely
        //   DNS / prefetch
        public bool CfgDisableDnsPrefetch    = true;
        public bool CfgDisablePreconnect     = true;
        //   phone-home / telemetry
        public bool CfgDisableSafeBrowsing   = true;   // stops URL hash lookups to Google
        public bool CfgDisableHyperlinkAudit = true;   // no <a ping> beacons
        public bool CfgDisableDomainReliability = true;// no reliability uploads to Google
        public bool CfgDisableComponentUpdate   = true;
        //   background networking
        public bool CfgDisableBackgroundNet  = true;
        public bool CfgDisableSync           = true;
        //   protocols / fingerprint surface
        public bool CfgDisableQuic           = true;   // QUIC/UDP off (predictable TCP via proxy)
        public bool CfgReduceReferrers       = true;   // strict-origin referrer policy
        public bool CfgDisableMediaDevices   = false;  // off by default (breaks cam/mic if on)
        //   UI
        public bool CfgWin9xBrowserScrollbars = false; // wired in Step D (laggy)

        // ---- persistence (tiny hand-rolled JSON; no dependency) ----
        private static string Path =>
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                "AlohaNet.json");

        public static NetConfig Load()
        {
            try
            {
                if (File.Exists(Path))
                    return Parse(File.ReadAllText(Path));
            }
            catch { }
            return Default();
        }

        public void Save()
        {
            try { File.WriteAllText(Path, ToJson()); } catch { }
        }

        public static NetConfig Default()
        {
            // ships ready for the .clos network out of the box
            return new NetConfig
            {
                ProfileName = "Clossys",
                ProxyScheme = "socks5h",
                ProxyHost = "127.0.0.1",
                ProxyPort = 9050,
                BypassList = "localhost,127.0.0.1,127.0.255.*",
                DnsMode = "remote",
                DohEnabled = false,
                DohEndpoint = "",
                HostResolverRules = "",
                UserAgent = ""
            };
        }

        public static NetConfig DirectProfile()
        {
            return new NetConfig
            {
                ProfileName = "Direct",
                ProxyScheme = "none",
                BypassList = "",
                DnsMode = "local",
                DohEnabled = false
            };
        }

        // ---- builds the Chromium/WebView2 additional-arguments string ----
        public string BuildBrowserArguments()
        {
            var args = new List<string>();

            if (CfgUseInterceptProxy)
            {
                // Everything goes to the local intercept proxy (Titanium). It
                // chains to the upstream (Tor/SOCKS/HTTP) itself, so the privacy
                // route is preserved — we just insert the MITM in front of it.
                args.Add($"--proxy-server=\"http://127.0.0.1:{InterceptPort}\"");
                if (!string.IsNullOrWhiteSpace(BypassList))
                    args.Add($"--proxy-bypass-list=\"{BypassList}\"");
            }
            else if (!string.Equals(ProxyScheme, "none", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ProxyHost))
            {
                // socks5h:// in Chromium semantics resolves DNS proxy-side.
                // Chromium's --proxy-server takes socks5:// for SOCKS5; remote
                // DNS for SOCKS5 is the default behaviour in Chromium, so this
                // gives Tor-side resolution of .clos / .onion.
                string scheme = ProxyScheme.ToLowerInvariant();
                string chromiumScheme = scheme == "socks5h" ? "socks5" : scheme;
                args.Add($"--proxy-server=\"{chromiumScheme}://{ProxyHost}:{ProxyPort}\"");

                if (!string.IsNullOrWhiteSpace(BypassList))
                    args.Add($"--proxy-bypass-list=\"{BypassList}\"");
            }

            if (!string.IsNullOrWhiteSpace(HostResolverRules))
                args.Add($"--host-resolver-rules=\"{HostResolverRules}\"");

            if (DohEnabled && !string.IsNullOrWhiteSpace(DohEndpoint))
                args.Add($"--dns-over-https-templates=\"{DohEndpoint}\"");

            // ── browser-configuration privacy/leak toggles ──
            // WebRTC. NOTE: Chromium removed --disable-webrtc, so a *full* disable
            // can't be done with a flag any more — it's enforced in the app via a
            // document-created script (Form1) that strips RTCPeerConnection. The
            // IP-handling policy below still helps in BOTH modes as defense-in-depth.
            if (CfgWebRtcLeakFix || CfgWebRtcDisableFully)
            {
                // ICE/STUN can otherwise enumerate interfaces and make peer
                // connections that bypass the proxy, leaking the real IP.
                args.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
                args.Add("--enforce-webrtc-ip-permission-check");
            }

            if (CfgDisableDnsPrefetch)
            {
                args.Add("--disable-features=NetworkPrediction");
                args.Add("--dns-prefetch-disable");
            }
            if (CfgDisablePreconnect)
                args.Add("--disable-features=PreconnectToSearch");

            if (CfgDisableSafeBrowsing)
                args.Add("--safebrowsing-disable-auto-update");
            if (CfgDisableHyperlinkAudit)
                args.Add("--no-pings");
            if (CfgDisableDomainReliability)
                args.Add("--disable-domain-reliability");
            if (CfgDisableComponentUpdate)
                args.Add("--disable-component-update");

            if (CfgDisableBackgroundNet)
                args.Add("--disable-background-networking");
            if (CfgDisableSync)
                args.Add("--disable-sync");

            if (CfgDisableQuic)
                args.Add("--disable-quic");
            if (CfgReduceReferrers)
                args.Add("--no-referrers");
            if (CfgDisableMediaDevices)
                args.Add("--use-fake-device-for-media-stream");

            return string.Join(" ", args);
        }

        // ---- minimal JSON (flat string/int/bool only) ----
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"ProfileName\": \"{Esc(ProfileName)}\",");
            sb.AppendLine($"  \"ProxyScheme\": \"{Esc(ProxyScheme)}\",");
            sb.AppendLine($"  \"ProxyHost\": \"{Esc(ProxyHost)}\",");
            sb.AppendLine($"  \"ProxyPort\": {ProxyPort},");
            sb.AppendLine($"  \"BypassList\": \"{Esc(BypassList)}\",");
            sb.AppendLine($"  \"DnsMode\": \"{Esc(DnsMode)}\",");
            sb.AppendLine($"  \"DohEnabled\": {(DohEnabled ? "true" : "false")},");
            sb.AppendLine($"  \"DohEndpoint\": \"{Esc(DohEndpoint)}\",");
            sb.AppendLine($"  \"HostResolverRules\": \"{Esc(HostResolverRules)}\",");
            sb.AppendLine($"  \"UserAgent\": \"{Esc(UserAgent)}\",");
            sb.AppendLine($"  \"Wallpaper\": \"{Esc(Wallpaper)}\",");
            sb.AppendLine($"  \"WallpaperEnabled\": {B(WallpaperEnabled)},");
            sb.AppendLine($"  \"CfgWebRtcLeakFix\": {B(CfgWebRtcLeakFix)},");
            sb.AppendLine($"  \"CfgWebRtcDisableFully\": {B(CfgWebRtcDisableFully)},");
            sb.AppendLine($"  \"CfgDisableDnsPrefetch\": {B(CfgDisableDnsPrefetch)},");
            sb.AppendLine($"  \"CfgDisablePreconnect\": {B(CfgDisablePreconnect)},");
            sb.AppendLine($"  \"CfgDisableSafeBrowsing\": {B(CfgDisableSafeBrowsing)},");
            sb.AppendLine($"  \"CfgDisableHyperlinkAudit\": {B(CfgDisableHyperlinkAudit)},");
            sb.AppendLine($"  \"CfgDisableDomainReliability\": {B(CfgDisableDomainReliability)},");
            sb.AppendLine($"  \"CfgDisableComponentUpdate\": {B(CfgDisableComponentUpdate)},");
            sb.AppendLine($"  \"CfgDisableBackgroundNet\": {B(CfgDisableBackgroundNet)},");
            sb.AppendLine($"  \"CfgDisableSync\": {B(CfgDisableSync)},");
            sb.AppendLine($"  \"CfgDisableQuic\": {B(CfgDisableQuic)},");
            sb.AppendLine($"  \"CfgReduceReferrers\": {B(CfgReduceReferrers)},");
            sb.AppendLine($"  \"CfgDisableMediaDevices\": {B(CfgDisableMediaDevices)},");
            sb.AppendLine($"  \"CfgWin9xBrowserScrollbars\": {B(CfgWin9xBrowserScrollbars)}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string B(bool v) => v ? "true" : "false";

        private static string Esc(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static NetConfig Parse(string json)
        {
            var c = Default();
            c.ProfileName       = Str(json, "ProfileName", c.ProfileName);
            c.ProxyScheme       = Str(json, "ProxyScheme", c.ProxyScheme);
            c.ProxyHost         = Str(json, "ProxyHost", c.ProxyHost);
            c.ProxyPort         = IntV(json, "ProxyPort", c.ProxyPort);
            c.BypassList        = Str(json, "BypassList", c.BypassList);
            c.DnsMode           = Str(json, "DnsMode", c.DnsMode);
            c.DohEnabled        = BoolV(json, "DohEnabled", c.DohEnabled);
            c.DohEndpoint       = Str(json, "DohEndpoint", c.DohEndpoint);
            c.HostResolverRules = Str(json, "HostResolverRules", c.HostResolverRules);
            c.UserAgent         = Str(json, "UserAgent", c.UserAgent);
            c.Wallpaper         = Str(json, "Wallpaper", c.Wallpaper);
            c.WallpaperEnabled  = BoolV(json, "WallpaperEnabled", c.WallpaperEnabled);
            c.CfgWebRtcLeakFix          = BoolV(json, "CfgWebRtcLeakFix", c.CfgWebRtcLeakFix);
            c.CfgWebRtcDisableFully     = BoolV(json, "CfgWebRtcDisableFully", c.CfgWebRtcDisableFully);
            c.CfgDisableDnsPrefetch     = BoolV(json, "CfgDisableDnsPrefetch", c.CfgDisableDnsPrefetch);
            c.CfgDisablePreconnect      = BoolV(json, "CfgDisablePreconnect", c.CfgDisablePreconnect);
            c.CfgDisableSafeBrowsing    = BoolV(json, "CfgDisableSafeBrowsing", c.CfgDisableSafeBrowsing);
            c.CfgDisableHyperlinkAudit  = BoolV(json, "CfgDisableHyperlinkAudit", c.CfgDisableHyperlinkAudit);
            c.CfgDisableDomainReliability = BoolV(json, "CfgDisableDomainReliability", c.CfgDisableDomainReliability);
            c.CfgDisableComponentUpdate = BoolV(json, "CfgDisableComponentUpdate", c.CfgDisableComponentUpdate);
            c.CfgDisableBackgroundNet   = BoolV(json, "CfgDisableBackgroundNet", c.CfgDisableBackgroundNet);
            c.CfgDisableSync            = BoolV(json, "CfgDisableSync", c.CfgDisableSync);
            c.CfgDisableQuic            = BoolV(json, "CfgDisableQuic", c.CfgDisableQuic);
            c.CfgReduceReferrers        = BoolV(json, "CfgReduceReferrers", c.CfgReduceReferrers);
            c.CfgDisableMediaDevices    = BoolV(json, "CfgDisableMediaDevices", c.CfgDisableMediaDevices);
            c.CfgWin9xBrowserScrollbars = BoolV(json, "CfgWin9xBrowserScrollbars", c.CfgWin9xBrowserScrollbars);
            return c;
        }

        private static string Str(string json, string key, string def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : def;
        }
        private static int IntV(string json, string key, int def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                json, "\"" + key + "\"\\s*:\\s*(\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : def;
        }
        private static bool BoolV(string json, string key, bool def)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                json, "\"" + key + "\"\\s*:\\s*(true|false)");
            return m.Success ? m.Groups[1].Value == "true" : def;
        }
    }
}
