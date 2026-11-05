using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch
{
    internal static class Program
    {
        static bool _showingError;

        [STAThread]
        static void Main(string[] args)
        {
            // Every error is caught, logged to the Logs tab and shown once. Zilch keeps running.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception);
            TaskScheduler.UnobservedTaskException += (s, e) => { Crash.Log(e.Exception, "background task"); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash.Log(e.ExceptionObject as Exception, "fatal");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool sudo = args.Any(a => a.Equals("--sudo", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(sudo));
        }

        static void Report(Exception ex)
        {
            Crash.Log(ex);
            if (_showingError) return; // one dialog at a time, no error storms
            _showingError = true;
            try
            {
                MessageBox.Show("Something went wrong, but Zilch caught it and is still running:\n\n" + ex.GetBaseException().Message +
                                "\n\nDetails are in the Logs tab.", "Zilch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            finally { _showingError = false; }
        }
    }
}
