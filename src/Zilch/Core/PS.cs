using System;
using System.Diagnostics;
using System.Text;

namespace Zilch.Core
{
    /// <summary>Runs a PowerShell script hidden and returns its output.</summary>
    public static class PS
    {
        // Reads the whole script from stdin as UTF-8 and runs it. The script itself is plain text on
        // stdin (no -EncodedCommand blob), so it works with any characters in paths and app names.
        const string Loader =
            "$in=[Console]::OpenStandardInput();$ms=New-Object IO.MemoryStream;$in.CopyTo($ms);" +
            "& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($ms.ToArray())))";

        public static (int code, string output) Run(string script, int timeoutMs = 15 * 60 * 1000)
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -NoLogo -Command \"" + Loader + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
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
                    var bytes = new UTF8Encoding(false).GetBytes(
                        "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + script);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
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
