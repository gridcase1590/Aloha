// NetworkPrivacyFlags.cs — turns NetworkPrivacyConfig into real WebView2 inputs.
//
//   BuildBrowserArguments(config)  -> string for CreationProperties.AdditionalBrowserArguments
//                                     (set BEFORE the WebView2 handle is created)
//   ApplySettings(settings, config) -> live runtime toggles (call after CoreWebView2 ready)
//
// WebView2 merges --disable-features across sources (union), so passing one
// combined --disable-features is correct. Switches separated by spaces.
// Reliability of each flag is noted in NetworkPrivacyOptions / INTEGRATION_NOTES.

using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;   // WebView2 SDK reference

namespace Aloha.RingStoreCore
{
    public static class NetworkPrivacyFlags
    {
        public static string BuildBrowserArguments(NetworkPrivacyConfig c)
        {
            var args = new List<string>();
            var disableFeatures = new List<string>();

            // Networking
            if (c.WebRtcLeakProtection)
                // hides local IPs but keeps WebRTC usable. Strictest alternative
                // (breaks calls without a proxy): disable_non_proxied_udp
                args.Add("--force-webrtc-ip-handling-policy=default_public_interface_only");
            if (c.DisableQuic)                 args.Add("--disable-quic");
            if (c.DisableBackgroundNetworking) args.Add("--disable-background-networking");
            if (c.DisableHyperlinkAuditing)    args.Add("--no-pings");

            // Privacy / telemetry
            if (c.DisableBackgroundExtensions) args.Add("--disable-component-extensions-with-background-pages");
            if (c.DisableDomainReliability)    args.Add("--disable-domain-reliability");
            if (c.DisableSync)                 args.Add("--disable-sync");

            // Fingerprinting
            if (c.DisableCanvasReadback)       args.Add("--disable-reading-from-canvas");
            if (c.DisableWebGl)                args.Add("--disable-3d-apis");

            // feature toggles (merged into one switch)
            if (c.DisableGoogleServices)
            {
                disableFeatures.Add("Translate");
                disableFeatures.Add("OptimizationHints");
                disableFeatures.Add("MediaRouter");          // also stops Cast mDNS discovery
            }
            if (c.DisablePrivacySandbox)       // best-effort: names track Chromium version
            {
                disableFeatures.Add("PrivacySandboxAdsAPIs");
                disableFeatures.Add("BrowsingTopics");
            }
            if (c.DisableUaClientHints)        // best-effort
                disableFeatures.Add("UserAgentClientHint");

            if (disableFeatures.Count > 0)
                args.Add("--disable-features=" + string.Join(",", disableFeatures));

            return string.Join(" ", args);
        }

        // Live settings. Some require a recent WebView2 SDK; if your SDK lacks a
        // property it won't compile — comment that line out (versions in NOTES).
        public static void ApplySettings(CoreWebView2Settings s, NetworkPrivacyConfig c)
        {
            if (s == null) return;
            s.IsGeneralAutofillEnabled    = !c.DisableAutofill;          // SDK >= 1.0.902.49
            s.IsPasswordAutosaveEnabled   = !c.DisablePasswordSave;      // SDK >= 1.0.902.49
            s.AreDevToolsEnabled          = !c.DisableDevTools;
            s.AreDefaultContextMenusEnabled = !c.DisableDefaultContextMenu;
            if (!string.IsNullOrEmpty(c.UserAgentOverride))
                s.UserAgent = c.UserAgentOverride;                      // SDK >= 1.0.992.28
        }
    }
}
