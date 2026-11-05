using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zilch.Core
{
    public sealed class HistoryEntry
    {
        public DateTime Time;
        public string Action;
        public string Summary;
        public string Details;
    }

    /// <summary>
    /// What Zilch did, so you can trace it back. Stored as plain text in
    /// %LOCALAPPDATA%\Zilch\history.log. "Clear history" in the Logs tab wipes it.
    /// </summary>
    public static class History
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zilch");
        public static readonly string FilePath = Path.Combine(Folder, "history.log");
        static readonly object Lock = new object();

        public static event Action<HistoryEntry> Added;

        public static void Add(string action, string summary, IEnumerable<string> details = null)
        {
            var e = new HistoryEntry
            {
                Time = DateTime.Now, Action = action, Summary = summary,
                Details = details == null ? "" : string.Join("\n", details),
            };
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var sb = new StringBuilder();
                    sb.Append("@@ ").Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append('\t').Append(Esc(action)).Append('\t').Append(Esc(summary)).Append('\n');
                    if (e.Details.Length > 0)
                        foreach (var line in e.Details.Split('\n')) sb.Append("   ").Append(line).Append('\n');
                    File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
                }
                catch { }
            }
            Added?.Invoke(e);
        }

        public static void Add(string action, DeleteResult r, IEnumerable<string> targets)
        {
            var d = new List<string>();
            foreach (var t in targets) d.Add("- " + t);
            foreach (var f in r.Failed) d.Add("! failed: " + f);
            Add(action, $"{r.Deleted} removed, {Fmt.Size(r.BytesFreed)} freed" +
                        (r.ScheduledForReboot > 0 ? $", {r.ScheduledForReboot} on reboot" : "") +
                        (r.Failed.Count > 0 ? $", {r.Failed.Count} failed" : ""), d);
        }

        static string Esc(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        public static List<HistoryEntry> Load()
        {
            var list = new List<HistoryEntry>();
            lock (Lock)
            {
                if (!File.Exists(FilePath)) return list;
                HistoryEntry cur = null;
                var details = new StringBuilder();
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (line.StartsWith("@@ "))
                    {
                        if (cur != null) { cur.Details = details.ToString().TrimEnd('\n'); list.Add(cur); }
                        details.Clear();
                        var f = line.Substring(3).Split('\t');
                        cur = new HistoryEntry { Action = f.Length > 1 ? f[1] : "", Summary = f.Length > 2 ? f[2] : "" };
                        DateTime.TryParse(f[0], out cur.Time);
                    }
                    else if (cur != null) details.Append(line.StartsWith("   ") ? line.Substring(3) : line).Append('\n');
                }
                if (cur != null) { cur.Details = details.ToString().TrimEnd('\n'); list.Add(cur); }
            }
            list.Reverse(); // newest first
            return list;
        }

        public static void Clear()
        {
            lock (Lock) { try { File.Delete(FilePath); } catch { } }
        }
    }
}
