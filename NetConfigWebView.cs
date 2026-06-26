// NetConfigWebView.cs — applies the LIVE NetConfig settings to a running
// CoreWebView2 (called from InitWebAsync after init, and re-run on Apply via
// ReinitEngine). Flag-side options go through BuildBrowserArguments instead.

using Microsoft.Web.WebView2.Core;

namespace Aloha
{
    public static class NetConfigWebView
    {
        public static void ApplyLive(CoreWebView2 core, NetConfig cfg)
        {
            if (core == null || cfg == null) return;

            CoreWebView2Settings s = core.Settings;
            s.IsScriptEnabled              = !cfg.DisableJavaScript;   // next navigation — makes the toggle real
            s.IsGeneralAutofillEnabled     = !cfg.DisableAutofill;     // immediate
            s.IsPasswordAutosaveEnabled    = !cfg.DisablePasswordSave; // immediate
            s.IsReputationCheckingRequired = !cfg.DisableSmartScreen;  // next navigation (SmartScreen)
            s.AreDevToolsEnabled           = !cfg.DisableDevTools;     // next navigation
            if (!string.IsNullOrWhiteSpace(cfg.UserAgent))
                s.UserAgent = cfg.UserAgent;                           // next navigation

            CoreWebView2Profile profile = core.Profile;
            if (profile != null)
            {
                switch (cfg.TrackingPrevention)
                {
                    case 0:  profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;     break;
                    case 1:  profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Basic;    break;
                    case 3:  profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;   break;
                    default: profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced; break;
                }
            }
        }
    }
}
