using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace Zilch.Core
{
    public enum Verdict { Remove, YourCall, Keep }

    /// <summary>"What is this thing, who does it belong to, and can I delete it?"</summary>
    public sealed class Explanation
    {
        public string Title, Path;
        public readonly List<KeyValuePair<string, string>> Facts = new List<KeyValuePair<string, string>>();
        public Verdict Verdict = Verdict.YourCall;
        public string Conclusion = "";

        public void Add(string head, string text) { if (!string.IsNullOrWhiteSpace(text)) Facts.Add(new KeyValuePair<string, string>(head, text)); }

        public static string VerdictLabel(Verdict v) =>
            v == Verdict.Remove ? "You can remove it" : v == Verdict.Keep ? "You should not remove it" : "Your call";
    }

    public static class Explain
    {
        // ======================= files and folders =======================

        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        static readonly Dictionary<Cat, string> CatWhat = new Dictionary<Cat, string>
        {
            [Cat.Cache] = "Cache or temporary data. Programs rebuild it on their own when they need it again.",
            [Cat.Windows] = "Part of Windows itself.",
            [Cat.Programs] = "Installed program files.",
            [Cat.Games] = "Game files.",
            [Cat.AppData] = "Settings and data a program keeps for you (AppData / ProgramData).",
            [Cat.Dev] = "Developer tool data: downloaded packages, SDKs, build output. Tools download it again when needed.",
            [Cat.Video] = "Video file(s).", [Cat.Picture] = "Picture(s).", [Cat.Audio] = "Music or audio.", [Cat.Document] = "Document(s).",
            [Cat.Archive] = "Archive or installer. Very often a leftover download.",
            [Cat.DiskImage] = "Disk image or virtual machine disk (WSL, Docker, Hyper-V, VirtualBox, an ISO...).",
            [Cat.RecycleBin] = "Deleted files waiting in the Recycle Bin.",
            [Cat.Unknown] = "Zilch can't tell what kind of data this is from its name or type.",
        };

        static readonly HashSet<string> SharedAppData = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Packages", "Temp", "Programs", "Publishers", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps",
            "PeerDistRepub", "VirtualStore", "Package Cache", "Application Data", "History", "Microsoft_Corporation", "IsolatedStorage",
        };

        /// <summary>Explain a file or folder. Reads the installed-programs list and the running processes; takes well under a second.</summary>
        public static Explanation ForPath(string path, bool isDir, Cat cat, long size)
        {
            path = Guard.Norm(path);
            var e = new Explanation { Title = System.IO.Path.GetFileName(path.TrimEnd('\\')), Path = path };
            if (string.IsNullOrEmpty(e.Title)) e.Title = path;
            e.Add("What it is", (isDir ? "Folder. " : "File. ") + CatWhat[cat]);

            // the file itself says who made it
            if (!isDir)
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(path);
                    var who = string.Join(", ", new[] { v.FileDescription, v.ProductName, v.CompanyName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct());
                    if (who.Length > 0) e.Add("File says", who);
                }
                catch { }

            // owner
            var risk = Guard.Classify(path);
            string ownerName = null; bool ownerInstalled = false, isInstallDir = false, orphan = false;
            if (path.StartsWith(Win, StringComparison.OrdinalIgnoreCase) || path.Equals(Win.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                ownerName = "Windows";
            else
            {
                List<InstalledApp> apps;
                try { apps = InstalledApps.Load(); } catch { apps = new List<InstalledApp>(); }
                foreach (var a in apps)
                {
                    var d = InstalledApps.GuessFolder(a);
                    if (d == null) continue;
                    d = Guard.Norm(d);
                    if (path.Equals(d, StringComparison.OrdinalIgnoreCase) || path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        ownerName = a.Name + (string.IsNullOrEmpty(a.Publisher) ? "" : $" ({a.Publisher})");
                        ownerInstalled = true; isInstallDir = true;
                        break;
                    }
                }
                if (ownerName == null)
                {
                    var seg = DataSegment(path, out bool packages);
                    if (packages && seg != null)
                    {
                        ownerName = "Windows Store app " + seg.Split('_')[0];
                        ownerInstalled = true;
                    }
                    else if (seg != null && !SharedAppData.Contains(seg))
                    {
                        var hits = apps.Where(a => NameMatch(a.Name, seg) || NameMatch(a.Publisher, seg)).ToList();
                        var pub = hits.Where(a => !string.IsNullOrEmpty(a.Publisher)).GroupBy(a => a.Publisher, StringComparer.OrdinalIgnoreCase)
                                      .OrderByDescending(g => g.Count()).FirstOrDefault();
                        if (hits.Count > 1 && pub != null && pub.Count() > 1)
                        { ownerName = $"{pub.Key} ({pub.Count()} of its programs are installed, e.g. {pub.First().Name})"; ownerInstalled = true; }
                        else if (hits.Count > 0)
                        { var hit = hits[0]; ownerName = hit.Name + (string.IsNullOrEmpty(hit.Publisher) ? "" : $" ({hit.Publisher})"); ownerInstalled = true; }
                        else { ownerName = $"\"{seg}\", which doesn't match any installed program"; orphan = true; }
                    }
                    else if (seg != null) ownerName = "Shared by Windows and many programs";
                }
            }
            if (ownerName != null) e.Add("Belongs to", ownerName + (ownerInstalled && !isInstallDir && cat != Cat.Cache ? " (installed)" : ""));

            // what it's doing right now
            var running = RunningFrom(isDir ? path : null, isDir ? null : path);
            e.Add("Doing right now", running.Count > 0 ? "In use by " + string.Join(", ", running.Distinct().Take(6)) : "Nothing running from here.");
            var changed = LastChange(path, isDir);
            if (changed != DateTime.MinValue) e.Add("Last changed", Ago(changed));
            e.Add("Size", Fmt.Size(size));

            // conclusion
            if (risk == Risk.Protected)
            { e.Verdict = Verdict.Keep; e.Conclusion = "Windows or your user profile needs it. Deleting it can break Windows or lose your data."; }
            else if (risk == Risk.Caution && (cat == Cat.AppData || cat == Cat.Unknown || cat == Cat.Programs))
            { e.Verdict = Verdict.Keep; e.Conclusion = "Shared by many programs, or a whole personal folder. Delete what's inside selectively, not the folder."; }
            else if (cat == Cat.Cache || cat == Cat.RecycleBin)
            {
                e.Verdict = Verdict.Remove;
                e.Conclusion = "Safe to empty. It rebuilds by itself; the program may be a little slower the first time." +
                               (running.Count > 0 ? " Files in use are skipped." : "");
            }
            else if (isInstallDir)
            { e.Verdict = Verdict.Keep; e.Conclusion = $"Don't delete the files of an installed program: it breaks it and leaves traces. Uninstall {ownerName} in the Apps tab instead."; }
            else if (orphan && cat == Cat.AppData)
            { e.Verdict = Verdict.Remove; e.Conclusion = "Probably left behind by a program you uninstalled (or a portable app). If nothing you use is called that, remove it."; }
            else if (cat == Cat.AppData && ownerInstalled)
            { e.Verdict = Verdict.YourCall; e.Conclusion = "Deleting it resets that program: settings, logins, saved data. To get rid of the program for good, uninstall it in the Apps tab."; }
            else if (cat == Cat.Games)
            { e.Verdict = Verdict.YourCall; e.Conclusion = "If you don't play it anymore, uninstall it from its launcher (Steam, Epic, Xbox) so the launcher knows."; }
            else if (cat == Cat.Dev)
            { e.Verdict = Verdict.YourCall; e.Conclusion = "Safe to delete if you're not working on those projects right now. The tools download it again when needed."; }
            else if (cat == Cat.Archive)
            { e.Verdict = Verdict.YourCall; e.Conclusion = "Usually safe to delete if you already installed or unpacked it."; }
            else if (cat == Cat.DiskImage)
            { e.Verdict = Verdict.YourCall; e.Conclusion = "If it belongs to WSL, Docker or a VM, remove the distro/VM with its own tool instead of deleting the file."; }
            else if (cat == Cat.Video || cat == Cat.Picture || cat == Cat.Audio || cat == Cat.Document)
            { e.Verdict = Verdict.YourCall; e.Conclusion = "Your own files. Only you know if you still need them."; }
            else { e.Verdict = Verdict.YourCall; e.Conclusion = "Zilch doesn't know enough about it to say. Check the facts above."; }

            if (running.Count > 0 && e.Verdict != Verdict.Keep) e.Conclusion += " Close " + running[0] + " first.";
            return e;
        }

        /// <summary>The first folder name after AppData\Local|Roaming|LocalLow or ProgramData.</summary>
        static string DataSegment(string path, out bool packages)
        {
            packages = false;
            var parts = path.Split('\\');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                bool appData = parts[i].Equals("AppData", StringComparison.OrdinalIgnoreCase) && i + 2 < parts.Length;
                if (appData)
                {
                    var seg = parts[i + 2];
                    if (seg.Equals("Packages", StringComparison.OrdinalIgnoreCase) && i + 3 < parts.Length) { packages = true; return parts[i + 3]; }
                    if (seg.Equals("Programs", StringComparison.OrdinalIgnoreCase) && i + 3 < parts.Length) return parts[i + 3];
                    return seg;
                }
                if (i == 0 && parts.Length > 2 && parts[1].Equals("ProgramData", StringComparison.OrdinalIgnoreCase)) return parts[2];
            }
            return null;
        }

        static bool NameMatch(string name, string seg)
        {
            if (string.IsNullOrWhiteSpace(name) || seg.Length < 3) return false;
            var n = name.Replace(" ", ""); var s = seg.Replace(" ", "").Replace("_", "").Replace("-", "");
            if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var first = name.Split(' ', ',', '.')[0];
            return first.Length >= 4 && s.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Names of running processes whose exe is inside the folder (or is the file).</summary>
        public static List<string> RunningFrom(string folder, string file)
        {
            var list = new List<string>();
            folder = folder == null ? null : folder.TrimEnd('\\') + "\\";
            try
            {
                foreach (var p in Process.GetProcesses())
                    using (p)
                    {
                        if (p.Id <= 4) continue;
                        var img = ProcSampler.ImagePath(p.Id);
                        if (img == null) continue;
                        if ((folder != null && img.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) ||
                            (file != null && img.Equals(file, StringComparison.OrdinalIgnoreCase)))
                            list.Add(System.IO.Path.GetFileName(img));
                    }
            }
            catch { }
            return list;
        }

        static DateTime LastChange(string path, bool isDir)
        {
            try
            {
                if (!isDir) return File.GetLastWriteTime(path);
                var t = Directory.GetLastWriteTime(path);
                int n = 0;
                foreach (var e in Directory.EnumerateFileSystemEntries(path))
                {
                    var w = File.GetLastWriteTime(e);
                    if (w > t) t = w;
                    if (++n > 500) break;
                }
                return t;
            }
            catch { return DateTime.MinValue; }
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            string rel = d.TotalMinutes < 2 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} minutes ago" :
                         d.TotalDays < 1 ? $"{(int)d.TotalHours} hours ago" : d.TotalDays < 60 ? $"{(int)d.TotalDays} days ago" :
                         $"{(int)(d.TotalDays / 30)} months ago";
            return $"{t:yyyy-MM-dd HH:mm} ({rel})";
        }

        // ======================= Windows (Store) apps =======================

        sealed class Known { public string Pattern, What; public Verdict V; public string Why; }

        // Only things we are sure about. Everything else is explained from its own manifest.
        static readonly Known[] KnownApps =
        {
            new Known { Pattern = "Microsoft.HEVCVideoExtension", What = "Video codec: lets Windows' own apps (Photos, Media Player, Explorer thumbnails) play HEVC/H.265 video.", V = Verdict.YourCall, Why = "Remove if you watch video in VLC or a browser instead. Getting it back from the Store may cost money." },
            new Known { Pattern = "Microsoft.AV1VideoExtension", What = "Video codec: AV1 video in Windows' own apps.", V = Verdict.YourCall, Why = "Remove if you don't play AV1 files in Windows' own apps." },
            new Known { Pattern = "Microsoft.VP9VideoExtensions", What = "Video codec: VP9 video in Windows' own apps.", V = Verdict.YourCall, Why = "Small. Remove if you don't need it." },
            new Known { Pattern = "Microsoft.MPEG2VideoExtension", What = "Video codec: MPEG-2 (DVD-style) video in Windows' own apps.", V = Verdict.YourCall, Why = "Small. Remove if you don't need it." },
            new Known { Pattern = "Microsoft.AVCEncoderVideoExtension", What = "Video encoder: lets Windows apps (Clipchamp, Photos video editor, camera) save H.264 video.", V = Verdict.YourCall, Why = "Remove if you never record or export video with Windows' own apps." },
            new Known { Pattern = "Microsoft.HEIFImageExtension", What = "Image codec: opens iPhone photos (.heic) in Photos and Explorer.", V = Verdict.YourCall, Why = "Keep if you copy photos from an iPhone." },
            new Known { Pattern = "Microsoft.WebpImageExtension", What = "Image codec: .webp pictures in Photos and Explorer thumbnails.", V = Verdict.YourCall, Why = "Small. Keep if you save pictures from the web." },
            new Known { Pattern = "Microsoft.RawImageExtension", What = "Image codec: camera RAW photos in Photos and Explorer.", V = Verdict.YourCall, Why = "Keep if you shoot RAW photos." },
            new Known { Pattern = "Microsoft.WebMediaExtensions", What = "Codecs for web formats (Ogg, Vorbis, Theora) in Windows' own apps.", V = Verdict.YourCall, Why = "Small. Remove if you don't need it." },
            new Known { Pattern = "Microsoft.GamingServices", What = "Installs and runs Xbox PC Game Pass and many Microsoft Store games.", V = Verdict.Keep, Why = "Keep if you play Game Pass or Store games. Otherwise safe to remove." },
            new Known { Pattern = "NVIDIACorp.NVIDIAControlPanel", What = "NVIDIA graphics settings (resolution, G-Sync, 3D settings).", V = Verdict.Keep, Why = "Keep it on a PC with an NVIDIA graphics card." },
            new Known { Pattern = "AppUp.IntelGraphicsExperience", What = "Intel Graphics Command Center: settings for Intel graphics.", V = Verdict.YourCall, Why = "Optional. The Intel driver works without it." },
            new Known { Pattern = "B9ECED6F.ArmouryCrate", What = "ASUS Armoury Crate: fan, performance and RGB control for ASUS hardware.", V = Verdict.YourCall, Why = "Keep if you use it to control fans or lighting on an ASUS PC." },
            new Known { Pattern = "DTSInc.DTSXUltra", What = "DTS:X Ultra audio enhancement for your speakers/headphones.", V = Verdict.YourCall, Why = "Optional. Sound works without it, just without the effects." },
            new Known { Pattern = "Microsoft.Paint", What = "Paint.", V = Verdict.YourCall, Why = "Built-in app. Remove if you don't use it; the Store gives it back anytime." },
            new Known { Pattern = "Microsoft.Windows.Photos", What = "Photos: the default picture viewer.", V = Verdict.YourCall, Why = "Keep unless you have another picture viewer set as default." },
            new Known { Pattern = "Microsoft.MicrosoftStickyNotes", What = "Sticky Notes.", V = Verdict.YourCall, Why = "Built-in app. Remove if you don't use it." },
            new Known { Pattern = "Microsoft.WindowsCalculator", What = "Calculator.", V = Verdict.YourCall, Why = "Built-in app, tiny." },
            new Known { Pattern = "Microsoft.WindowsNotepad", What = "Notepad.", V = Verdict.Keep, Why = "Tiny and plenty of things open text files with it." },
            new Known { Pattern = "Microsoft.WindowsTerminal", What = "Windows Terminal.", V = Verdict.YourCall, Why = "Keep if you use the command line." },
            new Known { Pattern = "Microsoft.ScreenSketch", What = "Snipping Tool (Win+Shift+S screenshots).", V = Verdict.Keep, Why = "Win+Shift+S stops working without it." },
            new Known { Pattern = "Microsoft.WindowsCamera", What = "Camera app.", V = Verdict.YourCall, Why = "Other apps use the webcam without it." },
            new Known { Pattern = "Microsoft.WindowsSoundRecorder", What = "Sound Recorder.", V = Verdict.YourCall, Why = "Built-in app. Remove if you don't use it." },
            new Known { Pattern = "Microsoft.WindowsAlarms", What = "Clock: alarms, timers, focus sessions.", V = Verdict.YourCall, Why = "Built-in app. Remove if you don't use it." },
            new Known { Pattern = "Microsoft.Office.ActionsServer", What = "Part of Microsoft Office (installed with it).", V = Verdict.Keep, Why = "Keep while you use Office." },
            new Known { Pattern = "Microsoft.OfficePushNotificationUtility", What = "Part of Microsoft Office: Office notifications.", V = Verdict.Keep, Why = "Keep while you use Office." },
            new Known { Pattern = "Microsoft.Office.OneNoteVirtualPrinter", What = "\"Send to OneNote\" printer, installed with Office.", V = Verdict.YourCall, Why = "Remove if you never print to OneNote." },
            new Known { Pattern = "MicrosoftCorporationII.WinAppRuntime", What = "Windows App Runtime: a framework other apps are built on.", V = Verdict.Keep, Why = "Apps that need it stop starting without it." },
            new Known { Pattern = "Microsoft.WindowsAppRuntime", What = "Windows App Runtime: a framework other apps are built on.", V = Verdict.Keep, Why = "Apps that need it stop starting without it." },
        };

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cch, IntPtr reserved);

        /// <summary>Real display names, publishers, dependencies and hooks, from each package's AppxManifest.xml.</summary>
        public static void ReadManifests(List<AppxApp> apps)
        {
            foreach (var a in apps)
            {
                try { ReadManifest(a); } catch { }
                if (a.Category != AppxCategory.Bloat && !string.IsNullOrWhiteSpace(a.DisplayName)) a.Friendly = a.DisplayName;
            }
            var byName = apps.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps)
                foreach (var d in a.Dependencies)
                    if (byName.TryGetValue(d, out var dep) && dep != a && !dep.UsedBy.Contains(a.Friendly)) dep.UsedBy.Add(a.Friendly);
        }

        static void ReadManifest(AppxApp a)
        {
            if (string.IsNullOrEmpty(a.InstallLocation)) return;
            var file = System.IO.Path.Combine(a.InstallLocation, "AppxManifest.xml");
            if (!File.Exists(file)) return;
            var doc = new XmlDocument();
            doc.Load(file);
            string Node(string xpath) => doc.SelectSingleNode(xpath)?.InnerText?.Trim();
            string Attr(string xpath) => doc.SelectSingleNode(xpath)?.Value?.Trim();
            var full = a.FullNames.FirstOrDefault();

            var dn = Resolve(Node("//*[local-name()='Properties']/*[local-name()='DisplayName']"), a.Name, full);
            var ve = Resolve(Attr("//*[local-name()='VisualElements']/@DisplayName"), a.Name, full);
            a.DisplayName = Useful(dn) ? dn : Useful(ve) ? ve : null;
            var pub = Resolve(Node("//*[local-name()='Properties']/*[local-name()='PublisherDisplayName']"), a.Name, full);
            if (Useful(pub)) a.PublisherName = pub;
            var desc = Resolve(Node("//*[local-name()='Properties']/*[local-name()='Description']"), a.Name, full);
            var vdesc = Resolve(Attr("//*[local-name()='VisualElements']/@Description"), a.Name, full);
            a.Description = Useful(vdesc) && vdesc != a.DisplayName ? vdesc : Useful(desc) && desc != a.DisplayName ? desc : null;

            foreach (XmlNode d in doc.SelectNodes("//*[local-name()='PackageDependency']/@Name")) a.Dependencies.Add(d.Value);

            var does = new List<string>();
            foreach (XmlNode ext in doc.SelectNodes("//*[@Category]"))
            {
                var c = ext.Attributes["Category"].Value;
                switch (c)
                {
                    case "windows.startupTask": a.StartsWithWindows = true; does.Add("starts when you sign in"); break;
                    case "windows.backgroundTasks": does.Add("runs tasks in the background"); break;
                    case "windows.fileTypeAssociation":
                        var types = ext.SelectNodes(".//*[local-name()='FileType']").Cast<XmlNode>().Select(x => x.InnerText.Trim()).Take(6).ToList();
                        does.Add("opens files" + (types.Count > 0 ? " (" + string.Join(" ", types) + ")" : "")); break;
                    case "windows.protocol":
                        var pn = ext.SelectSingleNode(".//*[local-name()='Protocol']/@Name")?.Value;
                        does.Add("handles links" + (pn != null ? " (" + pn + ":)" : "")); break;
                    case "windows.appExecutionAlias": does.Add("adds a command you can type in a terminal"); break;
                    case "windows.service": does.Add("installs a Windows service"); break;
                    case "windows.comServer": case "windows.comInterface": does.Add("plugs into other programs (COM)"); break;
                    case "windows.shareTarget": does.Add("appears in the Share menu"); break;
                    case "windows.toastNotificationActivation": does.Add("shows notifications"); break;
                    case "windows.updateTask": does.Add("runs a task after updates"); break;
                    case "windows.appService": does.Add("offers a service to other apps"); break;
                    case "windows.fileExplorerContextMenus": does.Add("adds right-click menu entries in Explorer"); break;
                }
            }
            a.Does = does.Distinct().ToList();

            var caps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["broadFileSystemAccess"] = "all your files", ["webcam"] = "camera", ["microphone"] = "microphone", ["location"] = "location",
                ["contacts"] = "contacts", ["appointments"] = "calendar", ["userAccountInformation"] = "your account info",
                ["runFullTrust"] = "full desktop-program rights", ["picturesLibrary"] = "Pictures", ["videosLibrary"] = "Videos",
                ["musicLibrary"] = "Music", ["documentsLibrary"] = "Documents", ["bluetooth"] = "Bluetooth", ["phoneCall"] = "phone calls",
            };
            foreach (XmlNode cap in doc.SelectNodes("//*[local-name()='Capabilities']/*/@Name"))
                if (caps.TryGetValue(cap.Value, out var plain) && !a.Access.Contains(plain)) a.Access.Add(plain);
        }

        static bool Useful(string s) => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("@");

        static string Resolve(string s, string pkgName, string fullName)
        {
            if (string.IsNullOrWhiteSpace(s) || !s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || fullName == null) return s;
            var r = s.Substring("ms-resource:".Length);
            var tries = new List<string>();
            if (r.StartsWith("//")) tries.Add("ms-resource:" + r);
            else if (r.StartsWith("/")) tries.Add("ms-resource://" + pkgName + r);
            else
            {
                tries.Add("ms-resource://" + pkgName + "/Resources/" + r);
                tries.Add("ms-resource://" + pkgName + "/" + r);
            }
            foreach (var t in tries)
            {
                var sb = new StringBuilder(1024);
                if (SHLoadIndirectString("@{" + fullName + "?" + t + "}", sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
            }
            return s;
        }

        /// <summary>One line for the list: what it is. Plus a verdict.</summary>
        public static (string what, Verdict v, string why) Summarize(AppxApp a)
        {
            var known = KnownApps.FirstOrDefault(k => a.Name.Equals(k.Pattern, StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith(k.Pattern + ".", StringComparison.OrdinalIgnoreCase));
            var lang = Lang(a.Name);
            if (a.Category == AppxCategory.System)
                return (a.Description ?? (a.IsFramework ? "Framework other apps are built on." : "Part of Windows."), Verdict.Keep,
                        a.IsFramework ? "Other apps are built on it." : "Windows needs it. Removing it can break Start, Settings, the Store or sign-in.");
            if (a.UsedBy.Count > 0)
                return (a.Description ?? known?.What ?? "Shared component.", Verdict.Keep, $"{a.UsedBy.Count} app(s) need it: {string.Join(", ", a.UsedBy.Take(4))}.");
            if (a.Category == AppxCategory.Bloat)
                return (a.Description ?? known?.What ?? "Preinstalled extra.", Verdict.Remove, "Preinstalled extra. Nothing depends on it; you can usually get it back from the Store.");
            if (known != null) return (known.What, known.V, known.Why);
            if (lang != null)
            {
                bool current = CultureInfo.CurrentUICulture.Name.Equals(lang.Name, StringComparison.OrdinalIgnoreCase);
                bool listed = UserLanguages().Any(l => l.Equals(lang.Name, StringComparison.OrdinalIgnoreCase) ||
                                                       l.Equals(lang.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
                var kind = a.Name.IndexOf("LanguageExperiencePack", StringComparison.OrdinalIgnoreCase) >= 0 ? "Windows display language" :
                           a.Name.IndexOf("Voice", StringComparison.OrdinalIgnoreCase) >= 0 ? "Text-to-speech voice" : "Speech recognition";
                if (current) return ($"{kind}: {lang.DisplayName}.", Verdict.Keep, "This is the language Windows is using right now.");
                if (listed) return ($"{kind}: {lang.DisplayName}.", Verdict.YourCall,
                    $"{lang.DisplayName} is in your language list (Settings > Language), so apps may use it. Remove it only if you never use Windows in that language.");
                return ($"{kind}: {lang.DisplayName}.", Verdict.Remove, $"{lang.DisplayName} isn't in your language list. Nothing uses it.");
            }
            var what = a.Description ?? (a.DisplayName != null ? a.DisplayName + (a.PublisherName != null ? " by " + a.PublisherName : "") : "No description in its manifest.");
            return (what, Verdict.YourCall,
                    "Not part of Windows and nothing else needs it. Remove it if you don't use " + (a.DisplayName ?? a.Friendly) + "; you can usually get it back from the Store.");
        }

        /// <summary>The languages in Settings > Time & language > Language (tr, de-DE, en-GB...).</summary>
        static string[] UserLanguages()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"))
                    if (k?.GetValue("Languages") is string[] l) return l;
            }
            catch { }
            return new string[0];
        }

        static CultureInfo Lang(string name)
        {
            foreach (var part in name.Split('.'))
            {
                var p = part.StartsWith("LanguageExperiencePack", StringComparison.OrdinalIgnoreCase) ? part.Substring("LanguageExperiencePack".Length) : part;
                if (p.Length == 5 && p[2] == '-')
                    try { return new CultureInfo(p); } catch { }
            }
            return null;
        }

        public static Explanation ForAppx(AppxApp a)
        {
            var (what, v, why) = Summarize(a);
            var e = new Explanation { Title = a.Friendly, Path = a.InstallLocation, Verdict = v, Conclusion = why };
            e.Add("What it is", what);
            e.Add("Package", a.Name + (a.Version != null ? "  " + a.Version : ""));
            e.Add("Made by", a.PublisherName ?? (a.Signature == "System" ? "Microsoft (part of Windows)" : a.Signature == "Store" ? "a Microsoft Store publisher" : null));
            e.Add("Hooks into Windows", a.Does.Count > 0 ? string.Join("; ", a.Does) : "Nothing special: it only runs when you open it.");
            if (a.Access.Count > 0) e.Add("Allowed to use", string.Join(", ", a.Access));
            e.Add("Needs", a.Dependencies.Count > 0 ? string.Join(", ", a.Dependencies.Take(8)) : null);
            e.Add("Needed by", a.UsedBy.Count > 0 ? string.Join(", ", a.UsedBy) : "No other app.");
            var running = string.IsNullOrEmpty(a.InstallLocation) ? new List<string>() : RunningFrom(a.InstallLocation, null);
            e.Add("Doing right now", running.Count > 0 ? "Running: " + string.Join(", ", running.Distinct()) : "Not running.");
            var flags = new List<string>();
            if (a.Provisioned) flags.Add("installs itself for every new user");
            if (a.NonRemovable) flags.Add("Windows marks it non-removable");
            if (a.IsFramework) flags.Add("framework");
            e.Add("Notes", string.Join("; ", flags));
            long size = string.IsNullOrEmpty(a.InstallLocation) ? 0 : Scanner.SizeOf(a.InstallLocation);
            var data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", a.FamilyName ?? "-");
            long dataSize = Directory.Exists(data) ? Scanner.SizeOf(data) : 0;
            e.Add("Size", $"{Fmt.Size(size)} program" + (dataSize > 0 ? $" + {Fmt.Size(dataSize)} of your data in {data}" : ""));
            e.Add("Lives in", a.InstallLocation);
            return e;
        }
    }
}
