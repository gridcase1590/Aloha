// NetConfig.cs — namespace Aloha.
//
// DESERIALIZATION-DEFAULTS FIX: DataContractJsonSerializer builds the object
// WITHOUT running field initializers or the constructor, so any field missing
// from an older AlohaNet.json came back as default(T) — i.e. ShowBookmarkButton
// became FALSE, which silently hid the bookmark star for anyone with an existing
// config. Defaults now live in SetDefaults(), called from the ctor AND from an
// [OnDeserializing] hook, so missing keys keep their intended values and only
// keys actually present in the JSON override them.
//
// Proxy defaults to Direct (none); Tor is the one-click Default() preset. Chromium
// has no "socks5h" scheme, so BuildBrowserArguments normalizes socks5h -> socks5.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Reflection;

namespace Aloha
{
    [DataContract]
    public sealed class NetConfig
    {
        // ---- proxy ----
        [DataMember] public string ProxyScheme;   // none | socks5h | socks5 | http
        [DataMember] public string ProxyHost;
        [DataMember] public int    ProxyPort;
        [DataMember] public string BypassList;
        [DataMember] public string ProfileName;

        // ---- dns ----
        [DataMember] public string DnsMode;        // remote | local
        [DataMember] public bool   DohEnabled;
        [DataMember] public string DohEndpoint;
        [DataMember] public string HostResolverRules;

        // ---- intercept ----
        [DataMember] public int InterceptPort;

        // ---- user agent / wallpaper ----
        [DataMember] public string UserAgent;
        [DataMember] public string Wallpaper;
        [DataMember] public bool   WallpaperEnabled;

        // ---- search ----
        // Query URL with {q} = the escaped search terms. Blank = auto: Startpage's .onion
        // service when the proxy points at Tor (so the query stays in-network), clearnet
        // otherwise. The two presets below are offered in Browser Configuration.
        [DataMember] public string SearchTemplate;
        public const string SearchStartpage      = "https://www.startpage.com/sp/search?q={q}";
        public const string SearchStartpageOnion = "http://startpagel6srwcjlue4zgq3zevrujfaow726kjytqbbjyrswwmjzcqd.onion/sp/search?q={q}";

        // ---- privacy / leak flags (-> BuildBrowserArguments) ----
        [DataMember] public bool CfgWebRtcLeakFix;
        [DataMember] public bool CfgWebRtcDisableFully;
        [DataMember] public bool CfgDisableQuic;
        [DataMember] public bool CfgDisableBackgroundNet;
        [DataMember] public bool CfgDisableComponentUpdate;
        [DataMember] public bool CfgDisableDnsPrefetch;
        [DataMember] public bool CfgDisableDomainReliability;
        [DataMember] public bool CfgDisableHyperlinkAudit;
        [DataMember] public bool CfgDisableMediaDevices;
        [DataMember] public bool CfgDisablePreconnect;
        [DataMember] public bool CfgDisableSafeBrowsing;
        [DataMember] public bool CfgDisableSync;
        [DataMember] public bool CfgReduceReferrers;
        [DataMember] public bool CfgUseInterceptProxy;
        [DataMember] public bool CfgWin9xBrowserScrollbars;
        [DataMember] public bool CfgAllowFridaSandboxOff;   // emits --no-sandbox for INSTR tracing
        [DataMember] public bool CfgEmbeddedDevTools;       // opens a loopback-only, origin-locked CDP port for the in-frame inspector

        // Fixed loopback CDP port for the embedded inspector. WebView2 binds this to
        // 127.0.0.1 only (it refuses external exposure even with --remote-allow-origins=*),
        // and we origin-lock it below, so it stays a local, foreign-proof surface. Fixed
        // (not ephemeral) so the inspector knows where to connect without hunting for the
        // DevToolsActivePort file, which WebView2 buries in its EBWebView subfolder.
        public const int EmbeddedDevToolsPort = 9333;

        [DataMember] public bool CfgSnapViewportToGrid;     // round WebView viewport to the SVG grid cell
        [DataMember] public bool CfgTorLetterbox;           // stricter preset: snap to Tor's 200x100 buckets (requires CfgSnapViewportToGrid)
        [DataMember] public int  WinWidth;                  // remembered window size (0 = use default)
        [DataMember] public int  WinHeight;
        [DataMember] public double WebZoom = 1.0;           // remembered WebView zoom factor

        // ---- live CoreWebView2 settings (-> NetConfigWebView.ApplyLive) ----
        [DataMember] public int  TrackingPrevention;   // 0 Off 1 Basic 2 Balanced 3 Strict
        [DataMember] public bool DisableAutofill;
        [DataMember] public bool DisablePasswordSave;
        [DataMember] public bool DisableSmartScreen;
        [DataMember] public bool DisableDevTools;
        [DataMember] public bool DisableJavaScript;    // -> Settings.IsScriptEnabled = !this

        // ---- content ----
        [DataMember] public bool   BlockDownloads;     // Form1 DownloadStarting cancels when true
        [DataMember] public bool   ConfirmDownloads;   // ask before every download (no automatic downloads)
        [DataMember] public bool   HideDownloadPopup;  // suppress WebView2's built-in download dialog
        [DataMember] public string DownloadDir;        // fixed download folder ("" = WebView2 default)

        // ---- interface ----
        [DataMember] public bool ShowBookmarkButton;

        // ---- instruction cockpit / nmap / primarch ----
        [DataMember] public bool EnableFridaTracing;
        [DataMember] public int  FridaSampleRate;
        [DataMember] public bool EnableNmapScanning;
        [DataMember] public string NmapDefaultTarget;
        [DataMember] public bool EnablePrimarch;

        public NetConfig() { SetDefaults(); }

        // Runs before the serializer fills present fields, so missing keys keep
        // these values instead of default(T). THIS is what un-hides the bookmark
        // button for existing configs.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext ctx) { SetDefaults(); }

        private void SetDefaults()
        {
            ProxyScheme = "none"; ProxyHost = "127.0.0.1"; ProxyPort = 9050;
            BypassList  = "localhost,127.0.0.1"; ProfileName = "default";

            DnsMode = "remote"; DohEnabled = false;
            DohEndpoint = "https://cloudflare-dns.com/dns-query"; HostResolverRules = "";

            InterceptPort = 8888;

            UserAgent = ""; Wallpaper = ""; WallpaperEnabled = true;
            SearchTemplate = "";   // blank = auto (Startpage onion on Tor, clearnet otherwise)

            CfgWebRtcLeakFix = true;  CfgWebRtcDisableFully = false; CfgDisableQuic = false;
            CfgDisableBackgroundNet = true; CfgDisableComponentUpdate = false; CfgDisableDnsPrefetch = true;
            CfgDisableDomainReliability = true; CfgDisableHyperlinkAudit = true; CfgDisableMediaDevices = false;
            CfgDisablePreconnect = true; CfgDisableSafeBrowsing = false; CfgDisableSync = true;
            CfgReduceReferrers = true; CfgUseInterceptProxy = false; CfgWin9xBrowserScrollbars = false;

            TrackingPrevention = 2; DisableAutofill = false; DisablePasswordSave = true;
            DisableSmartScreen = false; DisableDevTools = false; DisableJavaScript = false;

            BlockDownloads = false;
            ConfirmDownloads = false;
            HideDownloadPopup = false;
            DownloadDir = "";

            ShowBookmarkButton = true;

            EnableFridaTracing = false;
            FridaSampleRate = 10;
            EnableNmapScanning = false;
            NmapDefaultTarget = "192.168.1.0/24";
            EnablePrimarch = false;
        }

        static bool Has(string s) { return !string.IsNullOrWhiteSpace(s); }

        public string BuildBrowserArguments()
        {
            var a = new List<string>();

            if (CfgWebRtcDisableFully)
                a.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
            else if (CfgWebRtcLeakFix)
                a.Add("--force-webrtc-ip-handling-policy=default_public_interface_only");

            if (CfgDisableQuic)              a.Add("--disable-quic");
            if (CfgDisableBackgroundNet)     a.Add("--disable-background-networking");
            if (CfgDisableComponentUpdate)   a.Add("--disable-component-update");
            if (CfgDisableDnsPrefetch)       a.Add("--dns-prefetch-disable");
            if (CfgDisableDomainReliability) a.Add("--disable-domain-reliability");
            if (CfgDisableHyperlinkAudit)    a.Add("--no-pings");
            if (CfgDisableSync)              a.Add("--disable-sync");
            if (CfgReduceReferrers)          a.Add("--no-referrers"); // NOTE: kills ALL referrers

            if (CfgUseInterceptProxy)
            {
                a.Add("--proxy-server=http://127.0.0.1:" + InterceptPort.ToString(CultureInfo.InvariantCulture));
            }
            else if (!string.Equals(ProxyScheme, "none", StringComparison.OrdinalIgnoreCase) && Has(ProxyHost))
            {
                string scheme = string.Equals(ProxyScheme, "socks5h", StringComparison.OrdinalIgnoreCase)
                              ? "socks5" : ProxyScheme;
                a.Add("--proxy-server=" + scheme + "://" + ProxyHost + ":" + ProxyPort.ToString(CultureInfo.InvariantCulture));
            }

            if (Has(HostResolverRules)) a.Add("--host-resolver-rules=" + HostResolverRules);

            // DNS-over-HTTPS: opt-in secure resolver. NOTE: when a SOCKS proxy is set
            // (Tor/socks5h), Chromium resolves DNS remotely through the proxy, so DoH
            // mainly takes effect in DIRECT or http-proxy modes.
            if (DohEnabled && Has(DohEndpoint))
            {
                a.Add("--dns-over-https-mode=secure");
                a.Add("--dns-over-https-templates=" + DohEndpoint);
            }

            // INSTR/Frida instruction tracing needs the renderer un-sandboxed so
            // Frida can attach. Off by default; this WEAKENS the sandbox and is only
            // emitted when the user explicitly enables tracing in Browser Configuration.
            if (CfgAllowFridaSandboxOff)     a.Add("--no-sandbox");

            // Embedded DevTools needs a CDP endpoint. Open one ONLY on explicit
            // opt-in, bound to loopback with an OS-assigned ephemeral port (written
            // to DevToolsActivePort), and locked to the self-hosted inspector origin
            // so no foreign page — local or via DNS-rebinding — can connect. Chromium
            // itself 403s any DevTools WebSocket whose Origin isn't allow-listed.
            // Off by default; this is the only thing that opens a local port.
            if (CfgEmbeddedDevTools)
            {
                a.Add("--remote-debugging-port=" + EmbeddedDevToolsPort);
                a.Add("--remote-debugging-address=127.0.0.1");
                a.Add("--remote-allow-origins=https://devtools.aloha");
            }

            return string.Join(" ", a);
        }

        public static NetConfig Default()       // Tor (one click; needs Tor running on 9050)
        {
            return new NetConfig
            {
                ProxyScheme = "socks5", ProxyHost = "127.0.0.1", ProxyPort = 9050,
                BypassList = "localhost,127.0.0.1", ProfileName = "tor",
            };
        }

        public static NetConfig DirectProfile() // no proxy
        {
            return new NetConfig
            {
                ProxyScheme = "none", ProxyHost = "", ProxyPort = 9050,
                BypassList = "", ProfileName = "default",
            };
        }

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AlohaNet.json"); }
        }

        public void Save() { Save(DefaultPath); }
        public void Save(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(NetConfig));
            using (var fs = File.Create(path)) ser.WriteObject(fs, this);
        }

        public static NetConfig Load() { return Load(DefaultPath); }
        public static NetConfig Load(string path)
        {
            if (!File.Exists(path)) return new NetConfig();
            try
            {
                var ser = new DataContractJsonSerializer(typeof(NetConfig));
                using (var fs = File.OpenRead(path))
                    return (ser.ReadObject(fs) as NetConfig) ?? new NetConfig();
            }
            catch { return new NetConfig(); }
        }

        // Copy every instance field from another config into this one. Used when
        // loading a named profile: existing references to this object (the panels
        // all hold the same cfg) then see the new values instead of being left
        // pointing at a stale config.
        public void CopyFrom(NetConfig other)
        {
            if (other == null) return;
            foreach (var f in typeof(NetConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.IsLiteral) continue;
                f.SetValue(this, f.GetValue(other));
            }
        }
    }
}
