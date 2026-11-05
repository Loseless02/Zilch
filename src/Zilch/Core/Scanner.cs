using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Zilch.Core
{
    public sealed class DirNode
    {
        public string Name;          // full path for the root node, folder name otherwise
        public DirNode Parent;
        public uint Attributes;
        public long Size;            // total bytes, recursive
        public long OwnFilesSize;    // bytes of files directly inside
        public long FileCount;       // recursive
        public bool Denied;          // could not be read
        public List<DirNode> Children = new List<DirNode>();

        public string FullPath
        {
            get
            {
                if (Parent == null) return Name;
                var p = Parent.FullPath;
                return (p.EndsWith("\\") ? p : p + "\\") + Name;
            }
        }

        public bool Hidden => (Attributes & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0;
    }

    public struct FileEntry
    {
        public string Path;
        public long Size;
        public uint Attributes;
        public bool Hidden => (Attributes & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0;
    }

    /// <summary>
    /// Fast multi-threaded folder size scanner. Talks to FindFirstFileEx directly
    /// (basic info level + large fetch) and only keeps folders in memory, so a full
    /// C: drive fits in a few dozen MB.
    /// </summary>
    public sealed class Scanner
    {
        public long FilesSeen;
        public long BytesSeen;
        public long DeniedCount;
        public string CurrentPath = "";

        readonly CancellationToken _ct;
        readonly int _topN;
        readonly List<FileEntry> _top = new List<FileEntry>();
        long _topMin; // smallest size currently in _top once full

        public Scanner(CancellationToken ct, int topN = 300) { _ct = ct; _topN = topN; }

        public DirNode Scan(string root)
        {
            ThreadPool.GetMinThreads(out var w, out var io);
            if (w < Environment.ProcessorCount * 4) ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, io);

            var node = new DirNode { Name = root, Attributes = Native.FILE_ATTRIBUTE_DIRECTORY };
            ScanDir(node, root.TrimEnd('\\'), 0);
            return node;
        }

        /// <summary>Biggest files seen during the scan, largest first.</summary>
        public List<FileEntry> TopFiles()
        {
            lock (_top)
            {
                var l = new List<FileEntry>(_top);
                l.Sort((a, b) => b.Size.CompareTo(a.Size));
                return l;
            }
        }

        static string Long(string p) => p.StartsWith(@"\\") ? p : @"\\?\" + p;

        void ScanDir(DirNode node, string path, int depth)
        {
            if (_ct.IsCancellationRequested) return;
            CurrentPath = path;

            var h = Native.FindFirstFileExW(Long(path) + "\\*", Native.FindExInfoBasic, out var fd,
                Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                node.Denied = true;
                Interlocked.Increment(ref DeniedCount);
                return;
            }

            long ownSize = 0, ownFiles = 0;
            try
            {
                do
                {
                    var name = fd.cFileName;
                    if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        if (name == "." || name == "..") continue;
                        // Junctions/symlinks point at data counted elsewhere (or loop forever): skip.
                        if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                        node.Children.Add(new DirNode { Name = name, Parent = node, Attributes = fd.dwFileAttributes });
                    }
                    else
                    {
                        long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        ownSize += size;
                        ownFiles++;
                        if (size > Volatile.Read(ref _topMin)) OfferTop(path + "\\" + name, size, fd.dwFileAttributes);
                    }
                } while (Native.FindNextFileW(h, out fd));
            }
            finally { Native.FindClose(h); }

            Interlocked.Add(ref FilesSeen, ownFiles);
            Interlocked.Add(ref BytesSeen, ownSize);

            if (depth < 3 && node.Children.Count > 1)
            {
                Parallel.ForEach(node.Children,
                    new ParallelOptions { CancellationToken = CancellationToken.None, MaxDegreeOfParallelism = Environment.ProcessorCount * 2 },
                    c => ScanDir(c, path + "\\" + c.Name, depth + 1));
            }
            else
            {
                foreach (var c in node.Children) ScanDir(c, path + "\\" + c.Name, depth + 1);
            }

            long total = ownSize, files = ownFiles;
            foreach (var c in node.Children) { total += c.Size; files += c.FileCount; }
            node.OwnFilesSize = ownSize;
            node.Size = total;
            node.FileCount = files;
        }

        void OfferTop(string path, long size, uint attr)
        {
            lock (_top)
            {
                if (_top.Count < _topN)
                {
                    _top.Add(new FileEntry { Path = path, Size = size, Attributes = attr });
                    if (_top.Count == _topN) RecomputeMin();
                    return;
                }
                if (size <= _topMin) return;
                // replace the current smallest
                int mi = 0;
                for (int i = 1; i < _top.Count; i++) if (_top[i].Size < _top[mi].Size) mi = i;
                _top[mi] = new FileEntry { Path = path, Size = size, Attributes = attr };
                RecomputeMin();
            }
        }

        void RecomputeMin()
        {
            long m = long.MaxValue;
            foreach (var f in _top) if (f.Size < m) m = f.Size;
            Volatile.Write(ref _topMin, m);
        }

        /// <summary>Files directly inside one folder (non-recursive). Used when browsing.</summary>
        public static List<FileEntry> ListFiles(string folder)
        {
            var list = new List<FileEntry>();
            var h = Native.FindFirstFileExW(Long(folder.TrimEnd('\\')) + "\\*", Native.FindExInfoBasic, out var fd,
                Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE) return list;
            try
            {
                do
                {
                    if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
                    list.Add(new FileEntry
                    {
                        Path = folder.TrimEnd('\\') + "\\" + fd.cFileName,
                        Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow,
                        Attributes = fd.dwFileAttributes,
                    });
                } while (Native.FindNextFileW(h, out fd));
            }
            finally { Native.FindClose(h); }
            return list;
        }

        /// <summary>Quick recursive size of one folder (single-threaded, used for app folders and junk).</summary>
        public static long SizeOf(string path, CancellationToken ct = default)
        {
            try
            {
                if (System.IO.File.Exists(path)) return new System.IO.FileInfo(path).Length;
                var s = new Scanner(ct, 1);
                var n = new DirNode { Name = path };
                s.ScanDir(n, path.TrimEnd('\\'), 99); // depth 99 = no parallel fan-out
                return n.Size;
            }
            catch { return 0; }
        }
    }
}
