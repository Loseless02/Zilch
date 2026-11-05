using System;
using System.IO;
using System.Threading;

namespace Zilch.Core
{
    /// <summary>How much Zilch has wiped: this session, and ever (kept in %LOCALAPPDATA%\Zilch\wiped.txt).</summary>
    public static class Stats
    {
        static readonly string FilePath = Path.Combine(History.Folder, "wiped.txt");
        static long _session, _allTime;

        public static event Action Changed;
        public static long Session => Interlocked.Read(ref _session);
        public static long AllTime => Interlocked.Read(ref _allTime);

        static Stats()
        {
            try { if (File.Exists(FilePath)) long.TryParse(File.ReadAllText(FilePath).Trim(), out _allTime); } catch { }
        }

        /// <summary>Count bytes that are really gone (not the ones that went to the Recycle Bin).</summary>
        public static void Add(long bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _session, bytes);
            var all = Interlocked.Add(ref _allTime, bytes);
            try { Directory.CreateDirectory(History.Folder); File.WriteAllText(FilePath, all.ToString()); } catch { }
            try { Changed?.Invoke(); } catch { }
        }

        const long MB = 1L << 20, GB = 1L << 30, TB = 1L << 40;

        public static string SessionLine()
        {
            long b = Session;
            var s = Fmt.Size(b);
            if (b <= 0) return "Nothing wiped yet this session.";
            if (b < 100 * MB) return $"You wiped out {s} this session. Crumbs, but crumbs count.";
            if (b < GB) return $"You wiped out {s} this session. Warming up.";
            if (b < 10 * GB) return $"You wiped out {s} this session! That's a few movies' worth.";
            if (b < 50 * GB) return $"You wiped out {s} this session! Your disk can breathe again.";
            if (b < 200 * GB) return $"You wiped out {s} this session! Absolute purge.";
            return $"You wiped out {s} this session! Did anything survive?";
        }

        public static string AllTimeLine()
        {
            long b = AllTime;
            if (b <= 0) return "";
            var s = $"You have wiped the existence of {Fmt.Size(b)} of data, for all time.";
            if (b >= TB) return s + " Data fears you.";
            if (b >= 100 * GB) return s + " Legend.";
            if (b >= 10 * GB) return s + " Respect.";
            return s;
        }
    }
}
