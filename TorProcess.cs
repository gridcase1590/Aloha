using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Aloha
{
    // ============================================================
    // TorProcess — launches and supervises a bundled tor.exe. Builds a torrc
    // that opens the SOCKS + control ports (cookie auth), points Tor at a
    // private data dir under the app, references bundled geoip / pluggable
    // transports when present, and streams stdout for bootstrap progress.
    //
    // BuildTorrc() is the single source of the torrc text: the UI shows it as a
    // live preview, and Start() writes exactly that (unless TorrcOverride is set,
    // in which case the caller's hand-edited torrc is written verbatim).
    //
    // tor.exe is located bundled-first (.\tor\tor.exe) — no PATH dependency.
    // ============================================================
    public class TorProcess
    {
        public int SocksPort   { get; set; } = 9050;
        public int ControlPort { get; set; } = 9051;
        public string DataDir   { get; private set; }
        public string CookiePath => Path.Combine(DataDir, "control_auth_cookie");
        public bool Running => proc != null && !proc.HasExited;
        public int BootstrapPercent { get; private set; }
        public string LastLine { get; private set; } = "";

        // when set, Start() writes this verbatim instead of BuildTorrc() — lets the
        // UI's hand-edited torrc box take effect.
        public string TorrcOverride { get; set; }

        // raised for each stdout line (bootstrap notices etc.), marshalled by caller
        public event Action<string> Line;
        // raised when bootstrap reaches 100%
        public event Action Ready;
        // raised with a human reason if launch fails
        public event Action<string> Failed;

        private Process proc;
        private string bridgesLine;   // optional "Bridge ..." lines (newline-separated)

        public TorProcess(string bridges = null, int socksPort = 9050, int controlPort = 9051)
        {
            bridgesLine = bridges;
            SocksPort   = socksPort;
            ControlPort = controlPort;
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aloha", "tor-data");
        }

        // find tor.exe without relying on PATH: bundled first, then a couple of
        // common install spots, then bare "tor" as a last resort.
        private static string FindTorExe()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates =
                {
                    Path.Combine(baseDir, "tor", "tor.exe"),
                    Path.Combine(baseDir, "tor", "Tor", "tor.exe"),
                    Path.Combine(baseDir, "tor.exe")
                };
                foreach (var c in candidates) if (File.Exists(c)) return c;
            }
            catch { }
            return "tor";   // PATH fallback
        }

        // Compose the full torrc text from the current settings + any bundled
        // geoip / transport files found beside tor.exe. Pure: builds a string,
        // creates nothing on disk, so the UI can call it on every keystroke.
        public string BuildTorrc()
        {
            string torExe = FindTorExe();
            var sb = new StringBuilder();
            sb.AppendLine("SocksPort " + SocksPort);
            sb.AppendLine("ControlPort " + ControlPort);
            sb.AppendLine("CookieAuthentication 1");
            sb.AppendLine("DataDirectory " + DataDir);
            sb.AppendLine("Log notice stdout");
            sb.AppendLine("AvoidDiskWrites 1");

            // bundled geoip + pluggable-transport files, searched across nested
            // (tor\data), sibling (tor\ + data\) and app-root layouts. Each line is
            // emitted only if the file is actually present, so an exe-only bundle works.
            try
            {
                string torDir  = Path.GetDirectoryName(torExe) ?? "";
                string parent  = string.IsNullOrEmpty(torDir) ? "" : (Path.GetDirectoryName(torDir) ?? "");
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                string[] roots = { torDir, Path.Combine(torDir, "data"),
                                   Path.Combine(parent, "data"), parent,
                                   Path.Combine(baseDir, "data"), baseDir };
                foreach (var d in roots)
                {
                    if (string.IsNullOrEmpty(d)) continue;
                    string g4 = Path.Combine(d, "geoip");
                    string g6 = Path.Combine(d, "geoip6");
                    bool any = false;
                    if (File.Exists(g4)) { sb.AppendLine("GeoIPFile " + g4); any = true; }
                    if (File.Exists(g6)) { sb.AppendLine("GeoIPv6File " + g6); any = true; }
                    if (any) break;
                }
                if (!string.IsNullOrWhiteSpace(bridgesLine))
                {
                    string[] ptDirs = { Path.Combine(torDir, "pluggable_transports"), torDir,
                                        Path.Combine(parent, "pluggable_transports"), parent,
                                        Path.Combine(baseDir, "pluggable_transports") };
                    string[] names = { "lyrebird.exe", "obfs4proxy.exe" };
                    bool done = false;
                    foreach (var d in ptDirs)
                    {
                        if (string.IsNullOrEmpty(d)) continue;
                        foreach (var nm in names)
                        {
                            string p = Path.Combine(d, nm);
                            if (File.Exists(p)) { sb.AppendLine("ClientTransportPlugin obfs4,meek_lite,webtunnel exec \"" + p + "\""); done = true; break; }
                        }
                        if (done) break;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(bridgesLine))
            {
                sb.AppendLine("UseBridges 1");
                foreach (var b in bridgesLine.Replace("\r", "").Split('\n'))
                {
                    string t = b.Trim();
                    if (t.Length == 0) continue;
                    sb.AppendLine(t.StartsWith("Bridge", StringComparison.OrdinalIgnoreCase) ? t : ("Bridge " + t));
                }
            }
            return sb.ToString();
        }

        private string WriteTorrc()
        {
            Directory.CreateDirectory(DataDir);
            string torrcPath = Path.Combine(DataDir, "torrc");
            string text = string.IsNullOrWhiteSpace(TorrcOverride) ? BuildTorrc() : TorrcOverride;
            File.WriteAllText(torrcPath, text);
            return torrcPath;
        }

        public bool Start()
        {
            if (Running) return true;
            try
            {
                string exe = FindTorExe();
                string torrc = WriteTorrc();
                BootstrapPercent = 0;

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-f \"" + torrc + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => OnStdout(e.Data);
                proc.ErrorDataReceived  += (s, e) => OnStdout(e.Data);
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                Failed?.Invoke("could not start tor.exe — is it bundled in .\\tor\\ ? (" + ex.Message + ")");
                proc = null;
                return false;
            }
        }

        private void OnStdout(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            LastLine = data;
            Line?.Invoke(data);

            // bootstrap lines look like: "... Bootstrapped 45% (requesting_descriptors): ..."
            int i = data.IndexOf("Bootstrapped ", StringComparison.Ordinal);
            if (i >= 0)
            {
                int p = i + "Bootstrapped ".Length, j = p;
                while (j < data.Length && char.IsDigit(data[j])) j++;
                if (j > p && int.TryParse(data.Substring(p, j - p), out int pct))
                {
                    BootstrapPercent = pct;
                    if (pct >= 100) Ready?.Invoke();
                }
            }
        }

        public void Stop()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            try { proc?.Dispose(); } catch { }
            proc = null;
            BootstrapPercent = 0;
        }
    }
}
