using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Zilch.Core
{
    /// <summary>What kind of data something is. Drives the colored bar on top of the Space tab.</summary>
    public enum Cat : byte
    {
        Unknown, Windows, Programs, Games, AppData, Cache, Dev,
        Video, Picture, Audio, Document, Archive, DiskImage, RecycleBin,
    }

    public sealed class CatItem
    {
        public string Path;
        public long Size;
        public bool IsDir;
        public uint Attr;
        public DirNode Node;
    }

    /// <summary>
    /// Sorts scanned data into categories. Folders decide first (C:\Windows, Program Files, steamapps,
    /// caches...). Inside folders that don't decide (your user folders, AppData) each file is sorted by
    /// its extension. Anything left over is Unknown.
    /// </summary>
    public static class Categories
    {
        public const int N = 14;

        public static readonly string[] Names =
        {
            "Unknown", "Windows & system", "Programs", "Games", "App data", "Caches & temp", "Developer stuff",
            "Videos", "Pictures", "Music & audio", "Documents", "Archives & installers", "Disk images & VMs", "Recycle Bin",
        };

        public static string Name(Cat c) => Names[(int)c];

        /// <summary>Folder categories own every file inside them. The others sort files by extension.</summary>
        public static bool Absorbs(Cat c) => c == Cat.Windows || c == Cat.Programs || c == Cat.Games ||
                                             c == Cat.Cache || c == Cat.Dev || c == Cat.RecycleBin;

        static readonly StringComparer IC = StringComparer.OrdinalIgnoreCase;
        static readonly string WinDir = Guard.Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        static readonly HashSet<string> RootSystem = new HashSet<string>(IC)
        {
            "System Volume Information", "Recovery", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset",
            "Windows.old", "PerfLogs", "Config.Msi", "Boot", "EFI", "OneDriveTemp",
        };
        static readonly HashSet<string> GameDirs = new HashSet<string>(IC)
        {
            "steamapps", "SteamLibrary", "Epic Games", "Riot Games", "XboxGames", "GOG Games", "GOG Galaxy",
            "Origin Games", "EA Games", "Rockstar Games", "Ubisoft Game Launcher", "Games", ".minecraft",
            "curseforge", "Roblox",
        };
        static readonly HashSet<string> DevDirs = new HashSet<string>(IC)
        {
            "node_modules", ".nuget", ".gradle", ".m2", ".cargo", ".rustup", ".npm", ".vscode", ".vscode-insiders",
            ".cursor", ".platformio", ".espressif", "Arduino15", ".android", "venv", ".venv", ".git", ".conda",
            "anaconda3", "miniconda3", ".docker", ".dotnet", ".pyenv",
        };
        static readonly HashSet<string> CacheDirs = new HashSet<string>(IC)
        {
            "Temp", "tmp", "CrashDumps", "Crashpad", "Code Cache", "Service Worker",
        };
        static readonly HashSet<string> SystemFiles = new HashSet<string>(IC)
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "DumpStack.log",
        };

        static readonly Dictionary<string, Cat> Ext = Build();
        static Dictionary<string, Cat> Build()
        {
            var d = new Dictionary<string, Cat>(IC);
            void Add(Cat c, string list) { foreach (var e in list.Split(' ')) d[e] = c; }
            Add(Cat.Video, "mp4 mkv mov avi wmv webm m4v flv mpg mpeg ts m2ts mts 3gp vob");
            Add(Cat.Picture, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg xcf kra");
            Add(Cat.Audio, "mp3 wav flac aac ogg m4a wma opus aiff aif alac mid midi");
            Add(Cat.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub");
            Add(Cat.Archive, "zip rar 7z tar gz bz2 xz tgz zst cab msi msix msixbundle appx appxbundle");
            Add(Cat.DiskImage, "iso img vhd vhdx vmdk vdi qcow2 ova avhdx");
            return d;
        }

        /// <summary>Category of a folder, given its parent's category.</summary>
        public static Cat ForDir(Cat parent, string name, string fullPath)
        {
            if (parent == Cat.Windows || parent == Cat.RecycleBin) return parent;
            if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return Cat.RecycleBin;

            bool atRoot = fullPath.Length > 3 && fullPath.IndexOf('\\', 3) < 0 && fullPath[1] == ':';
            if (atRoot)
            {
                if (RootSystem.Contains(name)) return Cat.Windows;
                if (name.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)) return Cat.Programs;
                if (name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)) return Cat.AppData;
            }
            if (fullPath.Length == WinDir.Length && fullPath.Equals(WinDir, StringComparison.OrdinalIgnoreCase)) return Cat.Windows;

            if (name.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0 || CacheDirs.Contains(name)) return Cat.Cache;
            if (GameDirs.Contains(name)) return Cat.Games;
            if (DevDirs.Contains(name)) return Cat.Dev;
            if (name.Equals("Sdk", StringComparison.OrdinalIgnoreCase) && fullPath.EndsWith(@"\Android\Sdk", StringComparison.OrdinalIgnoreCase)) return Cat.Dev;
            if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) return Cat.AppData;
            if (name.Equals("Programs", StringComparison.OrdinalIgnoreCase) && parent == Cat.AppData &&
                fullPath.EndsWith(@"\AppData\Local\Programs", StringComparison.OrdinalIgnoreCase)) return Cat.Programs;
            return parent;
        }

        /// <summary>Category of any folder path, by walking down from the drive root.</summary>
        public static Cat ForPath(string path)
        {
            if (path.Length <= 3) return Cat.Unknown; // "C:" or "C:\" (GetFullPath("C:") would mean the current folder)
            path = Guard.Norm(path);
            var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Cat.Unknown;
            var cur = parts[0];
            var cat = Cat.Unknown;
            for (int i = 1; i < parts.Length; i++)
            {
                cur += "\\" + parts[i];
                cat = ForDir(cat, parts[i], cur);
            }
            return cat;
        }

        /// <summary>Category of a file inside a folder that doesn't own its files.</summary>
        public static Cat ForFile(string name, Cat dirCat)
        {
            if (Absorbs(dirCat)) return dirCat;
            if (SystemFiles.Contains(name)) return Cat.Windows;
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1)
            {
                var ext = name.Substring(dot + 1);
                if (Ext.TryGetValue(ext, out var c)) return c;
                if (dirCat != Cat.AppData && ext.Equals("exe", StringComparison.OrdinalIgnoreCase)) return Cat.Archive;
            }
            return dirCat == Cat.AppData ? Cat.AppData : Cat.Unknown;
        }

        /// <summary>Bytes per category for a scanned tree.</summary>
        public static long[] Totals(DirNode root)
        {
            var t = new long[N];
            if (root == null) return t;
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                AddOwn(n, t);
                foreach (var c in n.Children) stack.Push(c);
            }
            return t;
        }

        static void AddOwn(DirNode n, long[] t)
        {
            if (n.OwnCats == null) t[(int)n.Cat] += n.OwnFilesSize;
            else for (int i = 0; i < n.OwnCats.Length; i++) t[n.OwnCats[i]] += n.OwnCatBytes[i];
        }

        static long OwnOf(DirNode n, Cat c)
        {
            if (n.OwnCats == null) return n.Cat == c ? n.OwnFilesSize : 0;
            for (int i = 0; i < n.OwnCats.Length; i++) if (n.OwnCats[i] == (byte)c) return n.OwnCatBytes[i];
            return 0;
        }

        /// <summary>The category holding most of a folder's bytes, and how big its share is (0..1).</summary>
        public static (Cat cat, double share) Dominant(DirNode n)
        {
            var t = Totals(n);
            long sum = 0, best = -1; int bi = (int)n.Cat;
            for (int i = 0; i < N; i++) { sum += t[i]; if (t[i] > best) { best = t[i]; bi = i; } }
            return sum <= 0 ? (n.Cat, 1.0) : ((Cat)bi, (double)best / sum);
        }

        /// <summary>Bytes of one category inside a folder (recursive).</summary>
        public static long SizeIn(DirNode root, Cat c)
        {
            long s = 0;
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                s += OwnOf(n, c);
                foreach (var ch in n.Children) stack.Push(ch);
            }
            return s;
        }

        /// <summary>Keep a folder's per-category numbers right after one of its files was deleted.</summary>
        public static void RemoveFile(DirNode owner, string fileName, long size)
        {
            if (owner.OwnCats == null) return;
            var c = (byte)ForFile(fileName, owner.Cat);
            for (int i = 0; i < owner.OwnCats.Length; i++)
                if (owner.OwnCats[i] == c) { owner.OwnCatBytes[i] = Math.Max(0, owner.OwnCatBytes[i] - size); return; }
        }

        static readonly HashSet<string> Containers = new HashSet<string>(IC)
        {
            "Program Files", "Program Files (x86)", "ProgramData", "AppData", "Local", "Roaming", "LocalLow", "Programs",
            "steamapps", "common", "SteamLibrary", "Epic Games", "XboxGames", "Games", "GOG Games", "Packages",
            "WindowsApps", "Windows", "Users",
        };

        /// <summary>
        /// The folders and files that make up one category, biggest first. Big container folders
        /// (Program Files, steamapps\common, AppData\Local...) are opened up so you see the actual apps and games.
        /// </summary>
        public static List<CatItem> Find(DirNode root, Cat c, int max = 1500)
        {
            var result = new List<CatItem>();
            if (root == null) return result;

            // 1. folders whose category is c and whose parent's isn't
            var roots = new List<DirNode>();
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.Cat == c && (n.Parent == null || n.Parent.Cat != c) && n != root) { roots.Add(n); continue; }
                foreach (var ch in n.Children) stack.Push(ch);
            }
            if (root.Cat == c && root.Children.Count > 0) roots.AddRange(root.Children.Where(ch => ch.Cat == c));

            var folders = new List<CatItem>();
            var queue = new Queue<DirNode>(roots);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                bool open = Containers.Contains(n.Name) && n.Children.Count > 0;
                if (open && (c != Cat.Windows || n.Parent == null || n.Parent.Cat != Cat.Windows))
                {
                    foreach (var ch in n.Children) if (ch.Cat == c) queue.Enqueue(ch);
                    continue;
                }
                long s = SizeIn(n, c);
                if (s > 0) folders.Add(new CatItem { Path = n.FullPath, Size = s, IsDir = true, Node = n, Attr = n.Attributes });
            }

            // 2. single files of this category sitting in folders that sort by extension
            var holders = new List<KeyValuePair<DirNode, long>>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.OwnCats != null)
                {
                    long b = OwnOf(n, c);
                    if (b > 0) holders.Add(new KeyValuePair<DirNode, long>(n, b));
                }
                else if (n == root && n.Cat == c && n.OwnFilesSize > 0) holders.Add(new KeyValuePair<DirNode, long>(n, n.OwnFilesSize));
                foreach (var ch in n.Children) stack.Push(ch);
            }
            var files = new List<CatItem>();
            foreach (var h in holders.OrderByDescending(h => h.Value).Take(150))
                foreach (var f in Scanner.ListFiles(h.Key.FullPath))
                    if (ForFile(Path.GetFileName(f.Path), h.Key.Cat) == c)
                        files.Add(new CatItem { Path = f.Path, Size = f.Size, Attr = f.Attributes });

            result.AddRange(folders);
            result.AddRange(files);
            result.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }
    }
}
