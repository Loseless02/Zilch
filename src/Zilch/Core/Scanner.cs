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
        public Cat Cat;              // what kind of data this folder is (see Categories)
        public byte[] OwnCats;       // folders that sort files by type: categories of the files directly inside...
        public long[] OwnCatBytes;   // ...and their bytes. null when the folder's own Cat takes all its files.
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
        bool _serial;               // no parallel fan-out (small one-folder measurements)
        const int MaxDepth = 400;   // deeper trees are only built by mistake or on purpose; don't blow the stack
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

        /// <summary>
        /// Scan one folder of an existing tree again (after deleting or emptying it, or on Refresh)
        /// and fix the sizes of every folder above it.
        /// </summary>
        public static void Rescan(DirNode node)
        {
            long oldSize = node.Size, oldFiles = node.FileCount;
            node.Children = new List<DirNode>();
            node.Denied = false;
            node.OwnCats = null; node.OwnCatBytes = null;
            node.Size = node.OwnFilesSize = node.FileCount = 0;
            new Scanner(CancellationToken.None, 1).ScanDir(node, node.FullPath.TrimEnd('\\'), 0);
            long dSize = node.Size - oldSize, dFiles = node.FileCount - oldFiles;
            for (var p = node.Parent; p != null; p = p.Parent) { p.Size += dSize; p.FileCount += dFiles; }
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

        /// <summary>Re-read only the files directly inside a folder (not its subfolders) and fix the sizes above it.</summary>
        public static void RescanFiles(DirNode node)
        {
            var files = ListFiles(node.FullPath);
            long own = 0;
            bool split = !Categories.Absorbs(node.Cat);
            var catBytes = split ? new long[Categories.N] : null;
            foreach (var f in files)
            {
                own += f.Size;
                if (split) catBytes[(int)Categories.ForFile(System.IO.Path.GetFileName(f.Path), node.Cat)] += f.Size;
            }
            long childFiles = 0;
            foreach (var c in node.Children) childFiles += c.FileCount;
            long dSize = own - node.OwnFilesSize, dFiles = files.Count - (node.FileCount - childFiles);
            node.OwnFilesSize = own;
            SetOwnCats(node, catBytes);
            for (var p = node; p != null; p = p.Parent) { p.Size += dSize; p.FileCount += dFiles; }
        }

        static void SetOwnCats(DirNode node, long[] catBytes)
        {
            node.OwnCats = null; node.OwnCatBytes = null;
            if (catBytes == null) return;
            int k = 0;
            foreach (var b in catBytes) if (b > 0) k++;
            if (k == 0) return;
            node.OwnCats = new byte[k]; node.OwnCatBytes = new long[k];
            k = 0;
            for (int i = 0; i < catBytes.Length; i++)
                if (catBytes[i] > 0) { node.OwnCats[k] = (byte)i; node.OwnCatBytes[k++] = catBytes[i]; }
        }

        void ScanDir(DirNode node, string path, int depth)
        {
            if (_ct.IsCancellationRequested) return;
            CurrentPath = path;
            node.Cat = node.Parent == null ? Categories.ForPath(path) : Categories.ForDir(node.Parent.Cat, node.Name, path);
            if (depth > MaxDepth) { node.Denied = true; return; }

            var h = Native.FindFirstFileExW(Long(path) + "\\*", Native.FindExInfoBasic, out var fd,
                Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                node.Denied = true;
                Interlocked.Increment(ref DeniedCount);
                return;
            }

            long ownSize = 0, ownFiles = 0;
            bool split = !Categories.Absorbs(node.Cat);
            long[] catBytes = null;
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
                        if (split) (catBytes ?? (catBytes = new long[Categories.N]))[(int)Categories.ForFile(name, node.Cat)] += size;
                        if (size > Volatile.Read(ref _topMin)) OfferTop(path + "\\" + name, size, fd.dwFileAttributes);
                    }
                } while (Native.FindNextFileW(h, out fd));
            }
            finally { Native.FindClose(h); }

            Interlocked.Add(ref FilesSeen, ownFiles);
            Interlocked.Add(ref BytesSeen, ownSize);
            if (catBytes != null)
            {
                int k = 0;
                foreach (var b in catBytes) if (b > 0) k++;
                node.OwnCats = new byte[k]; node.OwnCatBytes = new long[k];
                k = 0;
                for (int i = 0; i < catBytes.Length; i++)
                    if (catBytes[i] > 0) { node.OwnCats[k] = (byte)i; node.OwnCatBytes[k++] = catBytes[i]; }
            }

            if (!_serial && depth < 3 && node.Children.Count > 1)
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
                var s = new Scanner(ct, 1) { _serial = true };
                var n = new DirNode { Name = path };
                s.ScanDir(n, path.TrimEnd('\\'), 0);
                return n.Size;
            }
            catch { return 0; }
        }
    }
}
