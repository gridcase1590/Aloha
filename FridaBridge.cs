using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace Aloha
{
    // ============================================================
    // FridaBridge — spawns the frida CLI against a WebView2 RENDERER
    // process with instr_agent.js, reads the agent's JSON lines off
    // stdout, and raises one event per line. INSTR subscribes and
    // swaps its synthetic sampler for these real values.
    //
    // Honest boundaries:
    //  - Requires frida on PATH (pip install frida-tools).
    //  - WebView2 renderers are sandboxed; attaching needs the browser
    //    launched with --no-sandbox (Browser Configuration > Diagnostics).
    //    Without it, frida fails to attach and the Line event never fires —
    //    INSTR keeps showing the synthetic sampler. No crash, no fake data.
    //  - We resolve the real renderer child of the browser process so the
    //    trace reflects the page's own thread/register state.
    //  - The agent reads real threads/regs/modules; nothing is invented.
    // ============================================================
    public class FridaBridge : IDisposable
    {
        public event Action<string> Line;      // raw JSON line from the agent
        public event Action<string> Error;     // stderr / lifecycle notes

        private Process proc;
        private readonly int browserPid;
        private int targetPid;
        private readonly string agentPath;
        private bool started;

        public int TargetPid { get { return targetPid; } }
        public bool Running { get { return started && proc != null && !proc.HasExited; } }

        public FridaBridge(int webViewBrowserPid, string agentScriptPath)
        {
            browserPid = webViewBrowserPid;
            agentPath = agentScriptPath;
        }

        // Find a renderer child: a msedgewebview2.exe process descended from the
        // browser pid whose command line carries --type=renderer. Falls back to
        // the browser pid itself if none is found (still attachable, less useful).
        // Find python.exe without relying on PATH. Searches the per-user install
        // roots Frida's pythoncore/pyenv layouts use, plus a couple of common spots.
        // Returns most-specific first. Each is verified to exist on disk.
        private static System.Collections.Generic.List<string> FindPythonExes()
        {
            var found = new System.Collections.Generic.List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var roots = new System.Collections.Generic.List<string>();
            // per-user "pythoncore-X.Y-64" layout (e.g. pip --user / nuget python)
            string pyBase = System.IO.Path.Combine(local, "Python");
            if (System.IO.Directory.Exists(pyBase))
            {
                try
                {
                    foreach (var d in System.IO.Directory.GetDirectories(pyBase))
                        roots.Add(d);                       // e.g. ...\Python\pythoncore-3.14-64
                    roots.Add(System.IO.Path.Combine(pyBase, "bin"));
                }
                catch { }
            }
            // official python.org per-user installs: ...\Programs\Python\PythonXY\
            string progPy = System.IO.Path.Combine(local, "Programs", "Python");
            if (System.IO.Directory.Exists(progPy))
            {
                try { foreach (var d in System.IO.Directory.GetDirectories(progPy)) roots.Add(d); }
                catch { }
            }

            foreach (var r in roots)
            {
                try
                {
                    string exe = System.IO.Path.Combine(r, "python.exe");
                    if (System.IO.File.Exists(exe) && !found.Contains(exe)) found.Add(exe);
                }
                catch { }
            }
            return found;
        }

        private int ResolveRendererPid()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process " +
                    "WHERE Name = 'msedgewebview2.exe'"))
                using (var results = searcher.Get())
                {
                    int firstChild = 0;
                    foreach (ManagementObject mo in results)
                    {
                        int ppid = Convert.ToInt32(mo["ParentProcessId"] ?? 0);
                        int pid  = Convert.ToInt32(mo["ProcessId"] ?? 0);
                        string cmd = (mo["CommandLine"] as string) ?? "";
                        if (ppid == browserPid || pid == browserPid)
                        {
                            if (firstChild == 0 && pid != browserPid) firstChild = pid;
                            if (cmd.IndexOf("--type=renderer", StringComparison.OrdinalIgnoreCase) >= 0)
                                return pid;   // exact renderer
                        }
                    }
                    if (firstChild != 0) return firstChild;
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke("renderer lookup failed: " + ex.Message);
            }
            return browserPid;   // last resort
        }

        public bool Start()
        {
            if (started) return Running;
            started = true;

            if (!File.Exists(agentPath))
            {
                Error?.Invoke("agent script not found: " + agentPath);
                return false;
            }

            targetPid = ResolveRendererPid();

            // Build the launcher list. We do NOT rely on PATH (per-user / pyenv Python
            // installs often aren't on PATH, and the frida.exe shim frequently isn't
            // either). Instead we hunt for a real python.exe in the common per-user
            // locations and run `python -m frida_tools.repl`, which is proven to work.
            // "-q -t inf" = quiet JSON output, no prompt, running forever so the
            // agent's setInterval timers keep firing.
            string agentArg = "-p " + targetPid + " -l \"" + agentPath + "\" -q -t inf";
            string moduleArg = "-m frida_tools.repl " + agentArg;

            var cands = new System.Collections.Generic.List<string[]>();

            // 0) bundled frida shipped next to Aloha — zero dependencies for end users.
            //    Drop frida.exe (and its frida DLLs) in .\frida\ beside the exe and it
            //    wins over everything else; nobody needs Python or pip installed.
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] bundled =
                {
                    System.IO.Path.Combine(baseDir, "frida", "frida.exe"),
                    System.IO.Path.Combine(baseDir, "frida.exe")
                };
                foreach (var b in bundled)
                    if (System.IO.File.Exists(b)) cands.Add(new[] { b, agentArg });
            }
            catch { }

            // 1) any python.exe we can find on disk (no PATH needed)
            foreach (var py in FindPythonExes())
                cands.Add(new[] { py, moduleArg });

            // 2) PATH-based fallbacks, in case the above found nothing
            cands.Add(new[] { "frida",   agentArg });
            cands.Add(new[] { "py",      "-3 " + moduleArg });
            cands.Add(new[] { "python",  moduleArg });
            cands.Add(new[] { "python3", moduleArg });

            var candidates = cands.ToArray();

            string lastErr = "";
            foreach (var cand in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = cand[0],
                        Arguments = cand[1],
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        string d = e.Data.Trim();
                        if (d.Length > 0 && d[0] == '{') Line?.Invoke(d);
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) Error?.Invoke(e.Data);
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    try { proc.StandardInput.AutoFlush = true; } catch { }
                    started = true;
                    return true;   // this launcher exists and started
                }
                catch (Exception ex)
                {
                    // Win32Exception "file not found" => this launcher isn't present; try the next
                    lastErr = ex.Message;
                    try { proc?.Dispose(); } catch { }
                    proc = null;
                }
            }

            // none of the launchers were found
            Error?.Invoke("frida CLI not found — pip install frida-tools (and add its Scripts to PATH)");
            if (!string.IsNullOrEmpty(lastErr)) { /* keep last for debugging */ }
            started = false;
            return false;
        }

        public void Dispose()
        {
            try
            {
                if (proc != null && !proc.HasExited) proc.Kill();
            }
            catch { }
            try { proc?.Dispose(); } catch { }
            proc = null;
        }
    }
}
