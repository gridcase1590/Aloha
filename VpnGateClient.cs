using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Aloha
{
    // ============================================================
    // VpnGateClient — real data + real engine for the VPN Gate picker.
    //
    //  Data : VPN Gate's public API (vpngate.net/api/iphone/) returns a
    //         CSV of volunteer relays, one row per server, including a
    //         base64-encoded OpenVPN (.ovpn) config in the last column.
    //         We parse it into Server rows for the picker.
    //
    //  Engine: OpenVPN. On connect we decode the chosen server's .ovpn,
    //         write it to a temp file, and launch a bundled openvpn.exe
    //         against it. Disconnect kills that process.
    //
    // Honest boundaries:
    //  - A VPN here is SYSTEM-WIDE (whole machine routes through it), not
    //    per-browser like the Tor proxy mode. The UI says so.
    //  - openvpn.exe needs elevation to bring up the TAP/Wintun adapter;
    //    if it isn't bundled/!found, Connect reports that instead of faking.
    //  - VPN Gate relays are anonymous volunteers: treat as an untrusted
    //    exit. We surface that in the window, not bury it.
    // ============================================================
    public class VpnGateClient
    {
        public class Server
        {
            public string Host;       // DDNS hostname
            public string Ip;
            public string Country;
            public string CountryShort;
            public long   Sessions;
            public long   SpeedBps;    // line speed, bits/sec
            public int    Ping;
            public int    Score;       // VPN Gate quality score
            public long   Uptime;      // ms
            public long   TotalUsers;
            public long   TotalTraffic; // bytes, cumulative
            public string LogType;
            public string Operator;
            public string Message;
            public string OvpnBase64;  // decoded -> .ovpn config

            // derived from the decoded .ovpn (real, not guessed)
            public string Proto = "?";   // tcp / udp
            public int    Port  = 0;

            public string SpeedMbps
            {
                get { return (SpeedBps / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"; }
            }

            public string UptimeText
            {
                get
                {
                    if (Uptime <= 0) return "?";
                    var ts = TimeSpan.FromMilliseconds(Uptime);
                    if (ts.TotalDays >= 1) return ((int)ts.TotalDays) + "d " + ts.Hours + "h";
                    if (ts.TotalHours >= 1) return ts.Hours + "h " + ts.Minutes + "m";
                    return ts.Minutes + "m";
                }
            }

            public string TrafficText
            {
                get
                {
                    double b = TotalTraffic;
                    string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
                    int k = 0;
                    while (b >= 1024 && k < u.Length - 1) { b /= 1024; k++; }
                    return b.ToString("0.0", CultureInfo.InvariantCulture) + " " + u[k];
                }
            }
        }

        private const string ApiUrl = "https://www.vpngate.net/api/iphone/";
        private Process vpn;
        private string logPath;
        private string activeHost;
        private string lastConfigPath;   // the .ovpn currently driving the tunnel
        private string lastLabel;

        // The system-wide tunnel is singular; the most recently connected client is the
        // live one. Form1's "Change Network IP" reaches the active tunnel through this.
        public static VpnGateClient Active { get; private set; }

        public bool Connected { get { return vpn != null && !vpn.HasExited; } }
        public string ActiveHost { get { return activeHost; } }
        public string LogPath { get { return logPath; } }

        // ---- DATA: live fetch, with a bundled/cached snapshot fallback ----
        public List<Server> FetchServers(out string error)
        {
            error = null;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string csv;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Aloha/1.0");
                    csv = wc.DownloadString(ApiUrl);
                }
                var live = ParseCsv(csv);
                if (live.Count > 0) { TrySaveSnapshot(csv); return live; }
                error = "API returned no usable servers";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // offline / failed: fall back to the writable cache, then the bundled snapshot
            string snap = TryLoadSnapshot();
            if (snap != null)
            {
                var cached = ParseCsv(snap);
                if (cached.Count > 0)
                {
                    error = "live fetch failed" + (error != null ? " (" + error + ")" : "") + " \u2014 using saved snapshot";
                    return cached;
                }
            }
            return new List<Server>();
        }

        // parse the VPN Gate CSV (identical format live or snapshot) into Server rows
        private List<Server> ParseCsv(string csv)
        {
            var list = new List<Server>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (var raw in csv.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '*' || line[0] == '#') continue;
                if (line.StartsWith("HostName", StringComparison.OrdinalIgnoreCase)) continue;

                var c = line.Split(',');
                if (c.Length < 15) continue;
                try
                {
                    var sv = new Server
                    {
                        Host         = c[0],
                        Ip           = c[1],
                        Score        = (int)ParseLong(c[2]),
                        Ping         = (int)ParseLong(c[3]),
                        SpeedBps     = ParseLong(c[4]),
                        Country      = c[5],
                        CountryShort = c[6],
                        Sessions     = ParseLong(c[7]),
                        Uptime       = ParseLong(c[8]),
                        TotalUsers   = ParseLong(c[9]),
                        TotalTraffic = ParseLong(c[10]),
                        LogType      = c[11],
                        Operator     = c[12],
                        Message      = c[13],
                        OvpnBase64   = c[c.Length - 1]
                    };
                    DeriveProto(sv);   // read proto/port from the real config
                    list.Add(sv);
                }
                catch { /* skip a malformed row, keep the rest */ }
            }
            return list;
        }

        // snapshot: a writable cache refreshed on every successful live fetch, plus a
        // read-only snapshot shipped in .\vpngate\ so the picker works fully offline.
        private static string CacheSnapshotPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aloha", "vpngate_servers.csv");
        }

        private static void TrySaveSnapshot(string csv)
        {
            try
            {
                string p = CacheSnapshotPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, csv);
            }
            catch { }
        }

        private static string TryLoadSnapshot()
        {
            try
            {
                string cache = CacheSnapshotPath();
                if (File.Exists(cache)) return File.ReadAllText(cache);
            }
            catch { }
            try
            {
                string baseDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                string[] bundled =
                {
                    Path.Combine(baseDir, "vpngate", "vpngate_servers.csv"),
                    Path.Combine(baseDir, "vpngate_servers.csv")
                };
                foreach (var p in bundled) if (File.Exists(p)) return File.ReadAllText(p);
            }
            catch { }
            return null;
        }

        private static long ParseLong(string s)
        {
            long v; return long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // read the real proto (tcp/udp) and remote port out of the decoded .ovpn
        private static void DeriveProto(Server s)
        {
            try
            {
                string cfg = Encoding.UTF8.GetString(Convert.FromBase64String(s.OvpnBase64));
                foreach (var raw in cfg.Split('\n'))
                {
                    string ln = raw.Trim();
                    if (ln.StartsWith("proto ", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = ln.Substring(6).Trim().ToLowerInvariant();
                        if (p.StartsWith("tcp")) s.Proto = "tcp";
                        else if (p.StartsWith("udp")) s.Proto = "udp";
                    }
                    else if (ln.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = ln.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3) { int pt; if (int.TryParse(parts[2], out pt)) s.Port = pt; }
                    }
                }
            }
            catch { /* leave proto '?' / port 0 if config can't be read */ }
        }

        // ---- ENGINE: bring a server up via bundled openvpn.exe ----
        // Returns null on success, or a human-readable reason on failure.
        public string Connect(Server s)
        {
            if (s == null) return "no server selected";
            if (string.IsNullOrEmpty(s.OvpnBase64)) return "server has no OpenVPN config";

            string ovpn;
            try { ovpn = Encoding.UTF8.GetString(Convert.FromBase64String(s.OvpnBase64)); }
            catch { return "could not decode the server config"; }

            string cfgPath = Path.Combine(Path.GetTempPath(), "aloha_vpngate.ovpn");
            try { File.WriteAllText(cfgPath, ovpn); }
            catch (Exception ex) { return "could not write config: " + ex.Message; }

            return LaunchOpenVpn(cfgPath, s.Host);
        }

        // Connect using a custom .ovpn already on disk (openvpn/config or config-auto).
        public string ConnectConfigFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "config file not found";
            return LaunchOpenVpn(path, Path.GetFileNameWithoutExtension(path));
        }

        // The custom .ovpn configs the user has dropped in openvpn\config (+ config-auto).
        public List<string> ListConfigFiles()
        {
            var outp = new List<string>();
            try
            {
                string baseDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                string[] dirs =
                {
                    Path.Combine(baseDir, "openvpn", "config"),
                    Path.Combine(baseDir, "openvpn", "config-auto")
                };
                foreach (var d in dirs)
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.ovpn", SearchOption.AllDirectories))
                        outp.Add(f);
                }
            }
            catch { }
            outp.Sort(StringComparer.OrdinalIgnoreCase);
            return outp;
        }

        // shared launcher — writes nothing, just runs openvpn.exe against an .ovpn path
        private string LaunchOpenVpn(string cfgPath, string label)
        {
            string exe = FindOpenVpn();
            if (exe == null)
                return "openvpn.exe not found (expected next to Aloha in .\\openvpn\\)";

            Disconnect();   // tear down any prior tunnel first

            // OpenVPN runs elevated in its own console (its stdout can't be piped), but
            // --log mirrors that output to a file the non-elevated window can tail. The
            // file outlives the console window, which may close as soon as openvpn exits.
            logPath = Path.Combine(Path.GetTempPath(), "aloha_openvpn.log");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--log \"" + logPath + "\" --config \"" + cfgPath + "\"",
                    UseShellExecute = true,     // triggers UAC; adapter needs elevation
                    Verb = "runas",
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                vpn = Process.Start(psi);
                activeHost = label;
                lastConfigPath = cfgPath;
                lastLabel = label;
                Active = this;
                return null;
            }
            catch (Exception ex)
            {
                // user declined UAC, or launch failed — report, don't pretend
                vpn = null; activeHost = null;
                return "launch failed: " + ex.Message;
            }
        }

        // Restart the active tunnel to rotate the session / tunnel address. Optionally
        // request a specific tunnel IP via peer-info — a cooperating server honours it
        // (client-connect -> ifconfig-push); public VPN Gate exits ignore it. Returns
        // null on success (note carries the human-readable result), else an error.
        public string ChangeNetworkIp(string requestedIp, out string note)
        {
            note = null;
            if (!Connected) { note = "no active VPN tunnel \u2014 connect one first"; return "not connected"; }
            if (string.IsNullOrEmpty(lastConfigPath) || !File.Exists(lastConfigPath))
            { note = "the active config is no longer on disk"; return "no config to restart"; }

            string baseCfg;
            try { baseCfg = File.ReadAllText(lastConfigPath); }
            catch (Exception ex) { note = ex.Message; return "could not read the active config"; }

            // strip any request block we appended before, then add a fresh one
            var sb = new StringBuilder();
            foreach (var raw in baseCfg.Replace("\r", "").Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.StartsWith("setenv UV_REQ_IP", StringComparison.OrdinalIgnoreCase)) continue;
                if (ln.Equals("push-peer-info", StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine(raw);
            }
            if (!string.IsNullOrEmpty(requestedIp))
            {
                sb.AppendLine("push-peer-info");
                sb.AppendLine("setenv UV_REQ_IP " + requestedIp);
            }

            string work = Path.Combine(Path.GetTempPath(), "aloha_vpn_reip.ovpn");
            try { File.WriteAllText(work, sb.ToString()); }
            catch (Exception ex) { note = ex.Message; return "could not write the working config"; }

            string err = LaunchOpenVpn(work, lastLabel ?? "vpn");
            if (err != null) { note = err; return err; }
            note = string.IsNullOrEmpty(requestedIp)
                ? "tunnel restarting \u2014 the server reassigns your address"
                : "tunnel restarting; requested " + requestedIp + " via peer-info (your own server honours it, public exits ignore it)";
            return null;
        }

        public void Disconnect()
        {
            try { if (vpn != null && !vpn.HasExited) vpn.Kill(); }
            catch { }
            try { vpn?.Dispose(); } catch { }
            vpn = null; activeHost = null;
            if (Active == this) Active = null;   // keep lastConfigPath for a later restart
        }

        private static string FindOpenVpn()
        {
            string baseDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            string[] candidates =
            {
                Path.Combine(baseDir, "openvpn", "bin", "openvpn.exe"),   // standard OpenVPN tree
                Path.Combine(baseDir, "openvpn", "openvpn.exe"),
                Path.Combine(baseDir, "openvpn.exe"),
                @"C:\Program Files\OpenVPN\bin\openvpn.exe",
                @"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe"
            };
            foreach (var p in candidates) if (File.Exists(p)) return p;
            return null;
        }
    }
}
