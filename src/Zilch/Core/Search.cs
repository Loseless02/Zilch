using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zilch.Core
{
    public sealed class SearchHit
    {
        public DirNode Node;     // the folder itself (IsDir) or the folder holding the file
        public string Name;
        public long Size;
        public bool IsDir, Hidden;
        public int Distance;     // 0 = the text is in the name as typed; >0 = typos allowed by "show relatives"
        public string Path => IsDir ? Node.FullPath : Node.FullPath.TrimEnd('\\') + "\\" + Name;
    }

    /// <summary>
    /// Searches every file and folder name of a scan. Exact = the typed text appears in the name (any case).
    /// Relatives = also names that are a few typos away ("mivcrosoft" finds "Microsoft").
    /// </summary>
    public static class Search
    {
        /// <summary>How many typos "show relatives" allows for a query of this length.</summary>
        public static int Tolerance(int len) => len < 4 ? 0 : len < 7 ? 1 : len < 12 ? 2 : 3;

        public static List<SearchHit> Run(DirNode[] nodes, string query, bool relatives, CancellationToken ct, int max = 1000)
        {
            var q = (query ?? "").Trim();
            var result = new List<SearchHit>();
            if (q.Length == 0 || nodes == null) return result;
            int k = relatives ? Tolerance(q.Length) : 0;
            var matcher = k > 0 && q.Length <= 63 ? new Fuzzy(q) : null;
            var bag = new System.Collections.Concurrent.ConcurrentBag<List<SearchHit>>();
            const int cap = 300000; // only one-letter searches get near this
            int found = 0;

            Parallel.For(0, nodes.Length, new ParallelOptions { CancellationToken = ct }, () => new List<SearchHit>(), (i, state, local) =>
            {
                if (Volatile.Read(ref found) > cap) { state.Stop(); return local; }
                var n = nodes[i];
                // the folder's own name (the scan root is matched by its full path)
                int d = Match(n.Name, 0, n.Name.Length, q, k, matcher);
                if (d >= 0) { local.Add(new SearchHit { Node = n, Name = n.Name, Size = n.Size, IsDir = true, Hidden = n.Hidden, Distance = d }); Interlocked.Increment(ref found); }
                // its files
                var names = n.FileNames;
                var sizes = n.FileSizes;
                if (names != null && sizes != null)
                {
                    int start = 0, idx = 0;
                    for (int p = 0; p <= names.Length; p++)
                    {
                        if (p < names.Length && names[p] != '\0') continue;
                        int len = p - start;
                        int fd = Match(names, start, len, q, k, matcher);
                        if (fd >= 0 && idx < sizes.Length)
                        {
                            long sz = sizes[idx];
                            local.Add(new SearchHit
                            {
                                Node = n, Name = names.Substring(start, len), Size = sz & ~DirNode.HiddenBit,
                                Hidden = (sz & DirNode.HiddenBit) != 0, Distance = fd,
                            });
                            Interlocked.Increment(ref found);
                        }
                        start = p + 1; idx++;
                    }
                }
                return local;
            }, local => bag.Add(local));

            ct.ThrowIfCancellationRequested();
            foreach (var l in bag) result.AddRange(l);
            // fewest typos first, then names closest to what you typed ("Microsoft" before "amd64_microsoft-windows-..."), then biggest
            result.Sort((a, b) =>
            {
                if (a.Distance != b.Distance) return a.Distance.CompareTo(b.Distance);
                int la = Math.Abs(a.Name.Length - q.Length) / 8, lb = Math.Abs(b.Name.Length - q.Length) / 8;
                return la != lb ? la.CompareTo(lb) : b.Size.CompareTo(a.Size);
            });
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        /// <summary>-1 = no match, otherwise the number of typos.</summary>
        static int Match(string s, int start, int len, string q, int k, Fuzzy f)
        {
            if (len <= 0) return -1;
            if (s.IndexOf(q, start, len, StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (f == null || len + k < q.Length) return -1;
            int d = f.Best(s, start, len);
            return d <= k ? d : -1;
        }

        /// <summary>
        /// Myers' bit-parallel approximate substring matching: the fewest edits needed to find the
        /// pattern anywhere inside a name, in one pass over the name.
        /// </summary>
        sealed class Fuzzy
        {
            readonly Dictionary<char, ulong> _peq = new Dictionary<char, ulong>();
            readonly int _m;
            readonly ulong _high;

            public Fuzzy(string pattern)
            {
                _m = pattern.Length;
                _high = 1UL << (_m - 1);
                for (int i = 0; i < _m; i++)
                {
                    var c = char.ToLowerInvariant(pattern[i]);
                    _peq.TryGetValue(c, out var v);
                    _peq[c] = v | (1UL << i);
                }
            }

            public int Best(string s, int start, int len)
            {
                ulong pv = ulong.MaxValue, mv = 0;
                int score = _m, best = _m;
                for (int i = start; i < start + len; i++)
                {
                    _peq.TryGetValue(char.ToLowerInvariant(s[i]), out var eq);
                    ulong xv = eq | mv;
                    ulong xh = (((eq & pv) + pv) ^ pv) | eq;
                    ulong ph = mv | ~(xh | pv);
                    ulong mh = pv & xh;
                    if ((ph & _high) != 0) score++;
                    else if ((mh & _high) != 0) score--;
                    ph <<= 1; mh <<= 1;
                    pv = mh | ~(xv | ph);
                    mv = ph & xv;
                    if (score < best) best = score;
                }
                return best;
            }
        }

        /// <summary>
        /// Which characters of text to highlight for a query: every exact occurrence, or for a near match
        /// the letters that line up with the query.
        /// </summary>
        public static bool[] Marks(string text, string query, bool relatives)
        {
            var q = (query ?? "").Trim();
            var marks = new bool[text.Length];
            if (q.Length == 0 || text.Length == 0) return marks;
            bool any = false;
            for (int i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(q, i + q.Length, StringComparison.OrdinalIgnoreCase))
            {
                for (int j = 0; j < q.Length; j++) marks[i + j] = true;
                any = true;
                if (i + q.Length >= text.Length) break;
            }
            if (any || !relatives) return marks;
            AlignMarks(text, q, marks);
            return marks;
        }

        /// <summary>Edit-distance alignment (free start/end in text); marks text letters equal to the query letter they align with.</summary>
        static void AlignMarks(string t, string q, bool[] marks)
        {
            int n = t.Length, m = q.Length;
            if (n > 400) return;
            var d = new int[m + 1, n + 1];
            for (int i = 1; i <= m; i++) d[i, 0] = i;
            for (int i = 1; i <= m; i++)
                for (int j = 1; j <= n; j++)
                {
                    int cost = char.ToLowerInvariant(q[i - 1]) == char.ToLowerInvariant(t[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            int bj = 0;
            for (int j = 1; j <= n; j++) if (d[m, j] < d[m, bj]) bj = j;
            int ii = m, jj = bj;
            while (ii > 0 && jj > 0)
            {
                int cost = char.ToLowerInvariant(q[ii - 1]) == char.ToLowerInvariant(t[jj - 1]) ? 0 : 1;
                if (d[ii, jj] == d[ii - 1, jj - 1] + cost) { if (cost == 0) marks[jj - 1] = true; ii--; jj--; }
                else if (d[ii, jj] == d[ii - 1, jj] + 1) ii--;
                else jj--;
            }
        }
    }
}
