using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zilch.Core
{
    public enum LeftoverKind { Folder, File, Shortcut, Registry, Task, Service }
    public enum Confidence { High, Medium }

    public sealed class Leftover
    {
        public LeftoverKind Kind;
        public string Path;       // file path, registry key, task name or service name
        public long Size;
        public Confidence Confidence;
        public string Reason;
        public Risk Risk = Risk.Normal;
    }

    /// <summary>
    /// Hunts for traces an app leaves behind: AppData, ProgramData, Documents, Program Files,
    /// shortcuts, registry keys, scheduled tasks and services.
    /// </summary>
    public sealed class LeftoverFinder
    {
        readonly string _name, _pub;
        readonly HashSet<string> _variants = new HashSet<string>();
        readonly string _token;       // main distinctive word, used for medium-confidence fuzzy hits
        readonly string _installDir;  // known before uninstall
        readonly string _uninstallKey;

        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","app","apps","pro","free","update","updater","driver","drivers","tools","tool","service","services","launcher",
            "setup","client","studio","player","manager","runtime","desktop","software","program","programs","helper","system",
            "common","data","user","users","for","and","visual","plus","edition","home","professional","x64","x86","windows",
            "microsoft","google","package","packages","installer","temp","cache","config","settings","local","roaming","files",
        };

        /// <summary>Install folders of the other installed programs. A folder holding one of them is never a sure leftover.</summary>
        public List<string> OtherInstallDirs = new List<string>();

        public LeftoverFinder(string appName, string publisher, string installDir, string uninstallRegistryKey)
        {
            _name = appName ?? "";
            _pub = NormPublisher(publisher);
            _installDir = string.IsNullOrEmpty(installDir) ? null : Guard.Norm(installDir);
            _uninstallKey = uninstallRegistryKey;

            var clean = CleanName(_name);
            Add(clean);
            Add(clean.Replace(" ", ""));
            // "Microsoft Edge" -> "Edge", "Valve Steam" -> "Steam"
            if (_pub.Length > 0 && Norm(clean).StartsWith(_pub) && Norm(clean).Length > _pub.Length)
                Add(Regex.Replace(clean, "^" + Regex.Escape(publisher.Split(' ')[0]) + @"\s*", "", RegexOptions.IgnoreCase));
            if (_installDir != null && !Guard.IsSharedContainer(_installDir)) Add(System.IO.Path.GetFileName(_installDir));

            // "VLC media player" -> "VLC" and "VLC media" count as "maybe" (Medium) matches only:
            // "Python Launcher" must never claim the real Python folder for sure.
            var words = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                AddWeak(words[0]);
                if (words.Length > 2) AddWeak(words[0] + words[1]);
            }

            _token = clean.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(Norm)
                          .FirstOrDefault(w => w.Length >= 5 && !Generic.Contains(w) && w != _pub);
        }

        readonly HashSet<string> _weak = new HashSet<string>();
        void Add(string s) { var n = Norm(s); if (n.Length >= 3 && !Generic.Contains(n) && n != _pub) _variants.Add(n); }
        void AddWeak(string s) { var n = Norm(s); if (n.Length >= 3 && !Generic.Contains(n) && n != _pub && !_variants.Contains(n)) _weak.Add(n); }

        public static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        static string CleanName(string n)
        {
            n = Regex.Replace(n, @"[®™©]", "");
            n = Regex.Replace(n, @"\(.*?\)|\[.*?\]", " ");
            n = Regex.Replace(n, @"\b(x64|x86|amd64|arm64|64-bit|32-bit|64bit|32bit|version)\b", " ", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\s+v?\d+(\.\d+)*[a-z]?\b", " ", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\s+-\s*$", "");
            return Regex.Replace(n, @"\s+", " ").Trim(' ', '-', '_');
        }

        static string NormPublisher(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            p = Regex.Replace(p, @"[,\.]", " ");
            p = Regex.Replace(p, @"\b(inc|llc|ltd|limited|gmbh|corp|corporation|co|company|ag|ab|sa|srl|s r o|bv|pty|plc|software|technologies|games|entertainment)\b", " ", RegexOptions.IgnoreCase);
            return Norm(p);
        }

        bool IsMatch(string folderName) => _variants.Contains(Norm(folderName));

        bool IsFuzzy(string folderName)
        {
            var n = Norm(folderName);
            if (_weak.Contains(n)) return true;
            if (n.Length < 5) return false;
            if (_token != null && n.Contains(_token)) return true;
            return _variants.Any(v => v.Length >= 5 && (n.StartsWith(v) || v.StartsWith(n)));
        }

        public List<Leftover> Find(bool includeMedium)
        {
            var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
            void Hit(LeftoverKind kind, string path, Confidence c, string why, Risk risk = Risk.Normal)
            {
                // A folder that holds another installed program (e.g. a "D:\Games" library) is never a sure hit.
                if (kind == LeftoverKind.Folder && c == Confidence.High && OtherInstallDirs.Any(o =>
                        !string.IsNullOrEmpty(o) && (o.Equals(path, StringComparison.OrdinalIgnoreCase) || o.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))))
                { c = Confidence.Medium; why += " (another program is inside!)"; }
                if (found.TryGetValue(path, out var ex)) { if (c < ex.Confidence) ex.Confidence = c; return; }
                if (c == Confidence.Medium && !includeMedium) return;
                // a parent already listed covers this one
                if (kind == LeftoverKind.Folder || kind == LeftoverKind.File || kind == LeftoverKind.Shortcut)
                    foreach (var k in found.Keys)
                        if (path.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)) return;
                found[path] = new Leftover { Kind = kind, Path = path, Confidence = c, Reason = why, Risk = risk };
            }

            string Env(string v) => Environment.GetEnvironmentVariable(v);
            string Sf(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
            var profile = Sf(Environment.SpecialFolder.UserProfile);
            var local = Sf(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Sf(Environment.SpecialFolder.ApplicationData);

            // 1. The install folder itself
            if (_installDir != null && Directory.Exists(_installDir) && !Guard.IsSharedContainer(_installDir))
                Hit(LeftoverKind.Folder, _installDir, Confidence.High, "Install folder");

            // 2. Data folders
            var roots = new List<(string path, string label)>
            {
                (roaming, "AppData\\Roaming"), (local, "AppData\\Local"), (System.IO.Path.Combine(profile, "AppData", "LocalLow"), "AppData\\LocalLow"),
                (System.IO.Path.Combine(local, "Programs"), "AppData\\Local\\Programs"), (Env("ProgramData"), "ProgramData"),
                (Env("ProgramFiles"), "Program Files"), (Env("ProgramFiles(x86)"), "Program Files (x86)"),
                (System.IO.Path.Combine(Env("ProgramFiles") ?? "", "Common Files"), "Common Files"),
                (System.IO.Path.Combine(Env("ProgramFiles(x86)") ?? "", "Common Files"), "Common Files (x86)"),
                (Sf(Environment.SpecialFolder.MyDocuments), "Documents"), (System.IO.Path.Combine(Env("PUBLIC") ?? "", "Documents"), "Public Documents"),
                (System.IO.Path.Combine(profile, "Saved Games"), "Saved Games"), (profile, "User folder"),
                (System.IO.Path.Combine(local, "Temp"), "Temp"), (System.IO.Path.Combine(local, "CrashDumps"), "Crash dumps"),
            };
            foreach (var (root, label) in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var dir in SafeDirs(root))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    var n = Norm(name);
                    if (_pub.Length > 0 && n == _pub)
                    {
                        // publisher folder: look one level inside, never offer the whole thing
                        foreach (var sub in SafeDirs(dir))
                        {
                            var sn = System.IO.Path.GetFileName(sub);
                            if (Guard.IsSharedContainer(sub)) continue;
                            if (IsMatch(sn)) Hit(LeftoverKind.Folder, sub, Confidence.High, label + " (publisher folder)");
                            else if (IsFuzzy(sn)) Hit(LeftoverKind.Folder, sub, Confidence.Medium, label + " (similar name)");
                        }
                        continue;
                    }
                    if (Guard.IsSharedContainer(dir)) continue;
                    // In the user folder itself only dot-folders (.vscode, .gradle...) are app data.
                    // Anything else there (OneDrive, a project folder...) is the user's own stuff.
                    if (root == profile && !name.StartsWith(".")) continue;
                    var nameNoDot = name.TrimStart('.');
                    if (IsMatch(nameNoDot)) Hit(LeftoverKind.Folder, dir, Confidence.High, label);
                    else if (root != profile && IsFuzzy(nameNoDot)) Hit(LeftoverKind.Folder, dir, Confidence.Medium, label + " (similar name)");
                }
            }

            // 3. Shortcuts: Start Menu, Desktop, taskbar pins
            // (root, recurse). The Desktop is only checked at top level: its subfolders are the user's own files.
            var lnkRoots = new[]
            {
                (System.IO.Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs"), true),
                (System.IO.Path.Combine(Env("ProgramData") ?? "", @"Microsoft\Windows\Start Menu\Programs"), true),
                (Sf(Environment.SpecialFolder.DesktopDirectory), false),
                (Sf(Environment.SpecialFolder.CommonDesktopDirectory), false),
                (System.IO.Path.Combine(roaming, @"Microsoft\Internet Explorer\Quick Launch"), true),
            };
            foreach (var (root, deep) in lnkRoots)
            {
                if (!Directory.Exists(root)) continue;
                if (deep)
                    foreach (var dir in SafeDirs(root, recursive: true))
                        if (IsMatch(System.IO.Path.GetFileName(dir)))
                            Hit(LeftoverKind.Shortcut, dir, Confidence.High, "Start Menu folder");
                foreach (var f in SafeFiles(root, recursive: deep))
                {
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url") continue;
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(f);
                    bool hit = IsMatch(baseName) || _variants.Any(v => v.Length >= 4 && Norm(baseName).StartsWith(v));
                    if (!hit && ext == ".lnk" && _installDir != null)
                    {
                        var t = Native.ResolveShortcut(f);
                        hit = t != null && t.StartsWith(_installDir + "\\", StringComparison.OrdinalIgnoreCase);
                    }
                    if (hit) Hit(LeftoverKind.Shortcut, f, Confidence.High, "Shortcut");
                    else if (IsFuzzy(baseName)) Hit(LeftoverKind.Shortcut, f, Confidence.Medium, "Shortcut (similar name)");
                }
            }

            // 4. Registry
            foreach (var (tag, path) in new[] { ("HKCU", "Software"), ("HKLM64", "SOFTWARE"), ("HKLM32", "SOFTWARE") })
                ScanRegistry(tag, path, Hit);
            if (!string.IsNullOrEmpty(_uninstallKey) && Deleter.RegistryKeyExists(_uninstallKey))
                Hit(LeftoverKind.Registry, _uninstallKey, Confidence.High, "Entry in Installed Apps list");

            // 5. Scheduled tasks
            var tasksDir = System.IO.Path.Combine(Sf(Environment.SpecialFolder.Windows), "System32", "Tasks");
            foreach (var f in SafeFiles(tasksDir, recursive: true))
            {
                var tn = System.IO.Path.GetFileName(f);
                bool hit = IsMatch(tn) || _variants.Any(v => v.Length >= 4 && Norm(tn).StartsWith(v));
                if (hit) Hit(LeftoverKind.Task, f.Substring(tasksDir.Length), Confidence.High, "Scheduled task");
                else if (IsFuzzy(tn)) Hit(LeftoverKind.Task, f.Substring(tasksDir.Length), Confidence.Medium, "Scheduled task (similar name)");
            }

            // 6. Services whose program lives in the install folder
            try
            {
                using (var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                    if (svc != null)
                        foreach (var s in svc.GetSubKeyNames())
                            using (var k = svc.OpenSubKey(s))
                            {
                                var img = Environment.ExpandEnvironmentVariables((k?.GetValue("ImagePath") as string ?? "").Trim('"'));
                                if (img.Length == 0) continue;
                                if (_installDir != null && img.TrimStart('"').StartsWith(_installDir + "\\", StringComparison.OrdinalIgnoreCase))
                                    Hit(LeftoverKind.Service, s, Confidence.High, "Windows service");
                                else if (IsMatch(s)) Hit(LeftoverKind.Service, s, Confidence.Medium, "Windows service (same name)");
                            }
            }
            catch { }

            var list = found.Values.ToList();
            foreach (var l in list)
                if (l.Kind == LeftoverKind.Folder || l.Kind == LeftoverKind.File || l.Kind == LeftoverKind.Shortcut)
                    l.Size = Scanner.SizeOf(l.Path);
            list.Sort((a, b) => a.Confidence != b.Confidence ? a.Confidence.CompareTo(b.Confidence) : b.Size.CompareTo(a.Size));
            return list;
        }

        static readonly HashSet<string> RegSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Microsoft", "Classes", "Policies", "Wow6432Node", "Windows", "Clients", "RegisteredApplications", "Intel", "WOW6432Node" };

        void ScanRegistry(string tag, string basePath, Action<LeftoverKind, string, Confidence, string, Risk> hit)
        {
            if (!Deleter.SplitKey(tag + "\\" + basePath, out var hive, out var view, out var sub)) return;
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (var name in k.GetSubKeyNames())
                    {
                        var full = tag + "\\" + basePath + "\\" + name;
                        var n = Norm(name);
                        if (_pub.Length > 0 && n == _pub)
                        {
                            using (var pk = k.OpenSubKey(name))
                                if (pk != null)
                                    foreach (var child in pk.GetSubKeyNames())
                                    {
                                        if (RegSkip.Contains(child)) continue;
                                        if (IsMatch(child)) hit(LeftoverKind.Registry, full + "\\" + child, Confidence.High, "Registry (publisher key)", Risk.Normal);
                                        else if (IsFuzzy(child)) hit(LeftoverKind.Registry, full + "\\" + child, Confidence.Medium, "Registry (similar name)", Risk.Normal);
                                    }
                            continue;
                        }
                        if (RegSkip.Contains(name)) continue;
                        if (IsMatch(name)) hit(LeftoverKind.Registry, full, Confidence.High, "Registry", Risk.Normal);
                        else if (IsFuzzy(name)) hit(LeftoverKind.Registry, full, Confidence.Medium, "Registry (similar name)", Risk.Normal);
                    }
                }
            }
            catch { }
        }

        static IEnumerable<string> SafeDirs(string root, bool recursive = false)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { yield break; }
            foreach (var d in dirs)
            {
                yield return d;
                if (recursive)
                {
                    bool reparse = false;
                    try { reparse = (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0; } catch { }
                    if (!reparse) foreach (var x in SafeDirs(d, true)) yield return x;
                }
            }
        }

        static IEnumerable<string> SafeFiles(string root, bool recursive)
        {
            if (!Directory.Exists(root)) yield break;
            string[] files;
            try { files = Directory.GetFiles(root); } catch { files = new string[0]; }
            foreach (var f in files) yield return f;
            if (recursive) foreach (var d in SafeDirs(root)) foreach (var f in SafeFiles(d, true)) yield return f;
        }

        // ---------------- Removal ----------------

        public static DeleteResult Remove(IEnumerable<Leftover> items, bool recycle, bool force)
        {
            var r = new DeleteResult();
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in items)
            {
                switch (l.Kind)
                {
                    case LeftoverKind.Folder:
                    case LeftoverKind.File:
                    case LeftoverKind.Shortcut:
                        Deleter.DeletePath(l.Path, recycle, force, r);
                        var p = System.IO.Path.GetDirectoryName(l.Path);
                        if (p != null) parents.Add(p);
                        break;
                    case LeftoverKind.Registry:
                        if (Deleter.DeleteRegistryKey(l.Path)) r.Deleted++; else r.Failed.Add(l.Path);
                        break;
                    case LeftoverKind.Task:
                        if (Deleter.Run("schtasks.exe", $"/Delete /TN \"{l.Path}\" /F") == 0) r.Deleted++; else r.Failed.Add("Task " + l.Path + " (needs admin?)");
                        break;
                    case LeftoverKind.Service:
                        Deleter.Run("sc.exe", $"stop \"{l.Path}\"", 30000);
                        if (Deleter.Run("sc.exe", $"delete \"{l.Path}\"") == 0) r.Deleted++; else r.Failed.Add("Service " + l.Path + " (needs admin?)");
                        break;
                }
            }
            // Drop publisher folders / Start Menu folders that are now empty.
            foreach (var p in parents)
            {
                try
                {
                    if (Directory.Exists(p) && !Guard.IsSharedContainer(p) && !Directory.EnumerateFileSystemEntries(p).Any())
                        Directory.Delete(p);
                }
                catch { }
            }
            return r;
        }
    }
}
