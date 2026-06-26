using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Aloha
{
    // ============================================================
    // TorControl — speaks Tor's control protocol on 127.0.0.1:9051.
    // Cookie auth: read control_auth_cookie, send AUTHENTICATE <hex>.
    // Then SIGNAL NEWNYM (new identity) and GETINFO circuit-status
    // (live circuit/relay path). Responses are line-based: continuation
    // lines start "250-" / "250+"; the final line is "250 OK".
    //   Ref: spec.torproject.org/control-spec
    // ============================================================
    public class TorControl : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private readonly string cookiePath;
        private TcpClient client;
        private NetworkStream stream;
        private StreamReader reader;
        public bool Authenticated { get; private set; }

        public TorControl(string cookiePath, int controlPort = 9051, string host = "127.0.0.1")
        {
            this.host = host; this.port = controlPort; this.cookiePath = cookiePath;
        }

        public bool Connect(out string error)
        {
            error = null;
            try
            {
                client = new TcpClient();
                client.Connect(host, port);
                stream = client.GetStream();
                stream.ReadTimeout = 5000;
                // Latin-1 (ISO-8859-1): a strict 1:1 byte->char map that can never
                // overflow the StreamReader char buffer the way ASCII does on a
                // non-ASCII byte straddling a read-chunk boundary. ns/all is ASCII,
                // so nothing is lost; this just makes that decode error impossible.
                reader = new StreamReader(stream, System.Text.Encoding.GetEncoding(28591), false);

                // cookie -> hex
                byte[] cookie = File.ReadAllBytes(cookiePath);
                var hex = new StringBuilder(cookie.Length * 2);
                foreach (byte b in cookie) hex.Append(b.ToString("x2"));

                var reply = Send("AUTHENTICATE " + hex);
                if (!reply.EndsWith("250 OK") && !reply.Contains("250 OK"))
                {
                    error = "control auth failed: " + reply;
                    return false;
                }
                Authenticated = true;
                return true;
            }
            catch (Exception ex)
            {
                error = "control connect failed: " + ex.Message;
                return false;
            }
        }

        // send one command, read the full multi-line reply (until a "NNN " line)
        private string Send(string cmd)
        {
            var bytes = Encoding.ASCII.GetBytes(cmd + "\r\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            var sb = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                sb.AppendLine(line);
                // a final line has a space as the 4th char: "250 OK"; continuations use - or +
                if (line.Length >= 4 && line[3] == ' ') break;
            }
            return sb.ToString().TrimEnd();
        }

        // new identity. Honest: Tor MAY rate-limit and a new exit isn't guaranteed.
        public bool NewIdentity(out string error)
        {
            error = null;
            try
            {
                if (!Authenticated) { error = "not authenticated"; return false; }
                var r = Send("SIGNAL NEWNYM");
                if (r.Contains("250 OK")) return true;
                error = r; return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public void ClearDnsCache()
        {
            try { if (Authenticated) Send("SIGNAL CLEARDNSCACHE"); } catch { }
        }

        // parsed view of one circuit
        public class Circuit
        {
            public string Id;
            public string Status;          // LAUNCHED / BUILT / EXTENDED / FAILED / CLOSED
            public string Purpose;
            public List<string> Relays = new List<string>();   // nicknames along the path
            // 40-hex fingerprint per hop (no leading $), parallel to Relays; "" when the
            // path line gave only a nickname. Lets callers match hops to the consensus by
            // fingerprint (nicknames are not unique) without disturbing the nickname list.
            public List<string> Fingerprints = new List<string>();
        }

        // GETINFO circuit-status -> list of circuits with their relay paths.
        // Each data line: "<id> <status> <$fp~nick>,<$fp~nick>,... PURPOSE=..."
        public List<Circuit> GetCircuits(out string error)
        {
            error = null;
            var outp = new List<Circuit>();
            try
            {
                if (!Authenticated) { error = "not authenticated"; return outp; }
                string r = Send("GETINFO circuit-status");
                foreach (var raw in r.Replace("\r", "").Split('\n'))
                {
                    string ln = raw.Trim();
                    // strip the protocol framing: 250+circuit-status=, 250-..., 250 OK, '.'
                    if (ln.StartsWith("250")) ln = ln.Length > 4 ? ln.Substring(4) : "";
                    if (ln.StartsWith("circuit-status=")) ln = ln.Substring("circuit-status=".Length);
                    if (ln.Length == 0 || ln == "OK" || ln == ".") continue;

                    var tok = ln.Split(' ');
                    if (tok.Length < 2) continue;
                    var c = new Circuit { Id = tok[0], Status = tok[1] };
                    if (tok.Length >= 3 && (tok[2].Contains("~") || tok[2].Contains("$")))
                    {
                        foreach (var hop in tok[2].Split(','))
                        {
                            // hop is "$FINGERPRINT~nickname" (or "=nickname"): keep the
                            // nickname for display and the fingerprint to match the consensus.
                            int sep = hop.IndexOfAny(new[] { '~', '=' });
                            c.Relays.Add(sep >= 0 ? hop.Substring(sep + 1) : hop);
                            string fp = "";
                            if (hop.StartsWith("$"))
                            {
                                int end = sep >= 0 ? sep : hop.Length;
                                if (end > 1) fp = hop.Substring(1, end - 1).ToUpperInvariant();
                            }
                            c.Fingerprints.Add(fp);
                        }
                    }
                    foreach (var t in tok)
                        if (t.StartsWith("PURPOSE=")) c.Purpose = t.Substring("PURPOSE=".Length);
                    outp.Add(c);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return outp;
        }

        // current bootstrap phase string, if Tor reports one
        public string GetBootstrapPhase()
        {
            try
            {
                if (!Authenticated) return "";
                string r = Send("GETINFO status/bootstrap-phase");
                int i = r.IndexOf("SUMMARY=", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int q1 = r.IndexOf('"', i), q2 = q1 >= 0 ? r.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1) return r.Substring(q1 + 1, q2 - q1 - 1);
                }
            }
            catch { }
            return "";
        }

        // GETINFO traffic/read + traffic/written -> total bytes since Tor started.
        // Process-wide; when this Tor is a relay the bulk of it is relayed traffic.
        public bool GetTraffic(out long read, out long written, out string error)
        {
            read = 0; written = 0; error = null;
            try
            {
                if (!Authenticated) { error = "not authenticated"; return false; }
                read    = ParseInfoLong(Send("GETINFO traffic/read"),    "traffic/read");
                written = ParseInfoLong(Send("GETINFO traffic/written"), "traffic/written");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static long ParseInfoLong(string reply, string key)
        {
            // reply: 250-traffic/read=12345 \r\n 250 OK
            int i = reply.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length + 1;
            int j = i;
            while (j < reply.Length && char.IsDigit(reply[j])) j++;
            long v; return long.TryParse(reply.Substring(i, j - i), out v) ? v : 0;
        }

        // ---- ADVOR data layer: relay enumeration + custom routing ----

        public class Relay
        {
            public string Nick;
            public string Fingerprint;     // 40-hex, no leading $
            public string Ip;
            public int    ORPort;
            public List<string> Flags = new List<string>();
            public long   BandwidthKb;
            public string Country = "??";  // filled by GeoIp at the call site
            public bool IsExit   { get { return Flags.Contains("Exit"); } }
            public bool IsGuard  { get { return Flags.Contains("Guard"); } }
            public bool IsFast   { get { return Flags.Contains("Fast"); } }
            public bool IsStable { get { return Flags.Contains("Stable"); } }
        }

        // GETINFO ns/all -> every relay in the current consensus (r/s/w lines).
        public List<Relay> GetAllRelays(out string error)
        {
            error = null;
            var outp = new List<Relay>();
            try
            {
                if (!Authenticated) { error = "not authenticated"; return outp; }
                string r = SendUntilStatus("GETINFO ns/all");
                Relay cur = null;
                foreach (var raw in r.Replace("\r", "").Split('\n'))
                {
                    if (raw.Length == 0) continue;
                    if (raw.StartsWith("250") || raw == ".") continue;   // protocol framing
                    char t = raw[0];
                    if (t == 'r' && raw.Length > 1 && raw[1] == ' ')
                    {
                        if (cur != null) outp.Add(cur);
                        var tk = raw.Split(' ');
                        cur = new Relay();
                        if (tk.Length >= 8)
                        {
                            cur.Nick        = tk[1];
                            cur.Fingerprint = B64ToHex(tk[2]);
                            cur.Ip          = tk[6];
                            int op; if (int.TryParse(tk[7], out op)) cur.ORPort = op;
                        }
                    }
                    else if (t == 's' && cur != null && raw.Length > 1 && raw[1] == ' ')
                    {
                        foreach (var fl in raw.Substring(2).Split(' '))
                            if (fl.Length > 0) cur.Flags.Add(fl);
                    }
                    else if (t == 'w' && cur != null && raw.StartsWith("w "))
                    {
                        foreach (var tok in raw.Substring(2).Split(' '))
                            if (tok.StartsWith("Bandwidth="))
                            { long bw; if (long.TryParse(tok.Substring(10), out bw)) cur.BandwidthKb = bw; }
                    }
                }
                if (cur != null) outp.Add(cur);
            }
            catch (Exception ex) { error = ex.Message; }
            return outp;
        }

        // SETCONF — set node-selection options live, e.g.
        //   SetConf("ExitNodes \"{de}\" StrictNodes 1", out err)
        public bool SetConf(string options, out string error)
        {
            error = null;
            try
            {
                if (!Authenticated) { error = "not authenticated"; return false; }
                var r = Send("SETCONF " + options);
                if (r.Contains("250 OK")) return true;
                error = r; return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // RESETCONF — clear options back to defaults, e.g. ResetConf("ExitNodes EntryNodes StrictNodes")
        public bool ResetConf(string keys, out string error)
        {
            error = null;
            try
            {
                if (!Authenticated) { error = "not authenticated"; return false; }
                var r = Send("RESETCONF " + keys);
                if (r.Contains("250 OK")) return true;
                error = r; return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // SAVECONF — write Tor's current running config to the torrc it was started
        // with, so live SETCONF changes survive a restart.
        public bool SaveConf(out string error)
        {
            error = null;
            try
            {
                if (!Authenticated) { error = "not authenticated"; return false; }
                var r = Send("SAVECONF");
                if (r.Contains("250")) return true;
                error = r; return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // MAPADDRESS — remap an address through Tor (e.g. a .onion alias or a
        // forced exit IP for one host). Reply is "250-from=to" or "250 OK".
        public bool MapAddress(string from, string to, out string error)
        {
            error = null;
            try
            {
                if (!Authenticated) { error = "not authenticated"; return false; }
                var r = Send("MAPADDRESS " + from + "=" + to);
                if (r.Contains("250")) return true;
                error = r; return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string B64ToHex(string b64)
        {
            try
            {
                string s = b64; int pad = s.Length % 4; if (pad > 0) s += new string('=', 4 - pad);
                byte[] by = Convert.FromBase64String(s);
                var sb = new StringBuilder(by.Length * 2);
                foreach (var b in by) sb.Append(b.ToString("x2"));
                return sb.ToString().ToUpperInvariant();
            }
            catch { return ""; }
        }

        // like Send(), but terminates only on a real status line (3 digits + space),
        // so huge dotted replies (ns/all) whose data lines may have a space at index 3
        // are read in full.
        private string SendUntilStatus(string cmd)
        {
            var bytes = Encoding.ASCII.GetBytes(cmd + "\r\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            var sb = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                sb.AppendLine(line);
                if (line.Length >= 4 && line[3] == ' '
                    && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2])) break;
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            try { reader?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { client?.Close(); } catch { }
            client = null; Authenticated = false;
        }
    }
}
