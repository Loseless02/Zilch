using System;
using System.Diagnostics;
using System.Text;

namespace Zilch.Core
{
    /// <summary>Runs a PowerShell script hidden and returns its output.</summary>
    public static class PS
    {
        public static (int code, string output) Run(string script, int timeoutMs = 15 * 60 * 1000)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';" + script));
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var sb = new StringBuilder();
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, sb.ToString()); }
                    p.WaitForExit();
                    return (p.ExitCode, sb.ToString());
                }
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        /// <summary>Quote a value as a single-quoted PowerShell string literal.</summary>
        public static string Q(string s) => "'" + (s ?? "").Replace("'", "''") + "'";
    }
}
