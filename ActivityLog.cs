using System;

namespace Aloha
{
    // ============================================================
    // ActivityLog — a tiny pub/sub hub. The browser pushes events
    // (navigation, requests, responses, resolution outcomes); the
    // Proxy and Network panels subscribe and print into their
    // green panes. Categories let each panel filter what it shows.
    // ============================================================
    public static class ActivityLog
    {
        public enum Cat { Proxy, Network, General }

        public static event Action<Cat, string> OnLog;

        public static void Log(Cat cat, string line)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line;
            OnLog?.Invoke(cat, stamped);
        }

        public static void Proxy(string s)   => Log(Cat.Proxy, s);
        public static void Network(string s) => Log(Cat.Network, s);
        public static void General(string s) => Log(Cat.General, s);
    }
}
