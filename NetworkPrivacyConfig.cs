// NetworkPrivacyConfig.cs — persisted network/privacy flags for Aloha (.NET 4.8).
//
// These map the SPIRIT of the LibreWolf panel to REAL Chromium/WebView2
// controls. Two kinds:
//   - switches  -> built into AdditionalBrowserArguments (need env recreation)
//   - settings  -> applied live on CoreWebView2.Settings after init
//
// Honest notes live in INTEGRATION_NOTES.md. In short: a few Firefox options
// (ResistFingerprinting, letterboxing, an IPv6 toggle) have NO clean Chromium
// equivalent and are deliberately absent rather than faked.
//
// Persistence is JSON via the BCL serializer (reference System.Runtime.Serialization).

using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Aloha.RingStoreCore
{
    [DataContract]
    public sealed class NetworkPrivacyConfig
    {
        // ---- Networking (switches) ----
        [DataMember] public bool WebRtcLeakProtection       = true;   // hide local IPs, keep WebRTC working
        [DataMember] public bool DisableQuic                = false;  // force TCP/TLS, no QUIC/HTTP3
        [DataMember] public bool DisableBackgroundNetworking = true;  // stop background phone-home
        [DataMember] public bool DisableHyperlinkAuditing   = true;   // --no-pings (<a ping> beacons)

        // ---- Privacy / telemetry (switches) ----
        [DataMember] public bool DisableBackgroundExtensions = true;  // Hangouts-style bg pages
        [DataMember] public bool DisableDomainReliability    = true;  // reliability beacons to Google
        [DataMember] public bool DisableSync                 = true;  // profile sync
        [DataMember] public bool DisableGoogleServices       = true;  // Translate, OptimizationHints, MediaRouter
        [DataMember] public bool DisablePrivacySandbox       = true;  // Topics / ad-measurement (best-effort)

        // ---- Fingerprinting (switches + UA) ----
        [DataMember] public bool   DisableCanvasReadback = false;     // strong; can break legit canvas use
        [DataMember] public bool   DisableWebGl          = false;     // --disable-3d-apis (WebGL + accel 2D)
        [DataMember] public bool   DisableUaClientHints  = true;      // reduce UA-CH surface (best-effort)
        [DataMember] public string UserAgentOverride     = "";        // "" = leave WebView2 default

        // ---- Runtime settings (live, no restart) ----
        [DataMember] public bool DisableAutofill          = true;
        [DataMember] public bool DisablePasswordSave      = true;
        [DataMember] public bool DisableDevTools          = false;
        [DataMember] public bool DisableDefaultContextMenu = false;

        public void Save(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(NetworkPrivacyConfig));
            using (var fs = File.Create(path)) ser.WriteObject(fs, this);
        }

        public static NetworkPrivacyConfig Load(string path)
        {
            if (!File.Exists(path)) return new NetworkPrivacyConfig();
            var ser = new DataContractJsonSerializer(typeof(NetworkPrivacyConfig));
            using (var fs = File.OpenRead(path))
                return (ser.ReadObject(fs) as NetworkPrivacyConfig) ?? new NetworkPrivacyConfig();
        }
    }
}
