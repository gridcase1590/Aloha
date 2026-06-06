using System;
using System.Windows.Forms;

namespace Aloha
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // The process had been dying silently with exit code 1 and no message.
            // These handlers catch every failure path (UI thread, background
            // thread, and startup), write the full detail to aloha_crash.txt next
            // to the exe, and show it once — so the REAL cause is visible.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Report("Unhandled", e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) =>
                Report("UI thread", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                Report("Run", ex);
            }
        }

        private static bool reported;
        private static void Report(string where, Exception ex)
        {
            string text = where + " error:\r\n\r\n" + (ex != null ? ex.ToString() : "(no detail)");
            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                    "aloha_crash.txt");
                System.IO.File.WriteAllText(path, text);
            }
            catch { }
            if (!reported)
            {
                reported = true;
                MessageBox.Show(text, "Aloha — startup error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
