using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace Zilch.Core
{
    public enum StartupSource { RunUser, RunMachine, RunMachine32, FolderUser, FolderCommon, StoreApp, Task, Service }

    public sealed class StartupItem
    {
        public string Name, Command, Exe, Publisher;
        public StartupSource Source;
        public string Location;      // registry key, file, task name or service name
        public string ValueName;     // Run value name / file name / store task key
        public bool Enabled;
        public bool Running;
        public bool NeedsAdmin => Source != StartupSource.RunUser && Source != StartupSource.FolderUser && Source != StartupSource.StoreApp;
        public bool CanRemove => Source != StartupSource.StoreApp && Source != StartupSource.Service;

        public string SourceText
        {
            get
            {
                switch (Source)
                {
                    case StartupSource.RunUser: return "Registry (you)";
                    case StartupSource.RunMachine: return "Registry (all users)";
                    case StartupSource.RunMachine32: return "Registry (all users, 32-bit)";
                    case StartupSource.FolderUser: return "Startup folder (you)";
                    case StartupSource.FolderCommon: return "Startup folder (all users)";
                    case StartupSource.StoreApp: return "Store app";
                    case StartupSource.Task: return "Scheduled task";
                    default: return "Service";
                }
            }
        }
    }

    /// <summary>
    /// Everything that starts with Windows. Enabling/disabling uses the same switches as Task Manager and
    /// Settings (StartupApproved), so the entry itself stays and can be turned back on.
    /// </summary>
    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        const string StoreTasks = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        public static List<StartupItem> Load(bool tasksAndServices, bool includeWindows)
        {
            var list = new List<StartupItem>();
            Try(() => ReadRun(list, RegistryHive.CurrentUser, RegistryView.Registry64, StartupSource.RunUser));
            Try(() => ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, StartupSource.RunMachine));
            Try(() => ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, StartupSource.RunMachine32));
            Try(() => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.FolderUser));
            Try(() => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.FolderCommon));
            Try(() => ReadStore(list));
            if (tasksAndServices)
            {
                Try(() => ReadTasks(list, includeWindows));
                Try(() => ReadServices(list, includeWindows));
            }

            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var p in Process.GetProcesses()) using (p) running.Add(p.ProcessName); } catch { }
            foreach (var it in list)
            {
                if (it.Exe != null)
                {
                    it.Running = running.Contains(Path.GetFileNameWithoutExtension(it.Exe));
                    if (it.Publisher == null)
                        try { if (File.Exists(it.Exe)) it.Publisher = FileVersionInfo.GetVersionInfo(it.Exe).CompanyName?.Trim(); } catch { }
                }
            }
            return list;
        }

        static void Try(Action a) { try { a(); } catch { } }

        static string ApprovedKey(StartupSource s)
        {
            switch (s)
            {
                case StartupSource.RunMachine32: return Approved + "Run32";
                case StartupSource.FolderUser: case StartupSource.FolderCommon: return Approved + "StartupFolder";
                default: return Approved + "Run";
            }
        }

        static RegistryKey Base(StartupSource s, RegistryView view = RegistryView.Registry64) =>
            RegistryKey.OpenBaseKey(s == StartupSource.RunUser || s == StartupSource.FolderUser || s == StartupSource.StoreApp
                ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);

        /// <summary>StartupApproved: first byte even = on, odd = off. No value = on.</summary>
        static bool IsApproved(StartupSource s, string name)
        {
            try
            {
                using (var b = Base(s))
                using (var k = b.OpenSubKey(ApprovedKey(s)))
                    return !(k?.GetValue(name) is byte[] v) || v.Length == 0 || (v[0] & 1) == 0;
            }
            catch { return true; }
        }

        static void ReadRun(List<StartupItem> list, RegistryHive hive, RegistryView view, StartupSource src)
        {
            using (var b = RegistryKey.OpenBaseKey(hive, view))
            using (var k = b.OpenSubKey(RunKey))
            {
                if (k == null) return;
                foreach (var n in k.GetValueNames())
                {
                    if (n.Length == 0) continue;
                    var cmd = k.GetValue(n)?.ToString() ?? "";
                    list.Add(new StartupItem
                    {
                        Name = n, Command = cmd, Exe = InstalledApps.ExePart(cmd), Source = src,
                        Location = (hive == RegistryHive.CurrentUser ? "HKCU" : view == RegistryView.Registry32 ? "HKLM32" : "HKLM64") + "\\" + RunKey,
                        ValueName = n, Enabled = IsApproved(src, n),
                    });
                }
            }
        }

        static void ReadFolder(List<StartupItem> list, string folder, StartupSource src)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Native.ResolveShortcut(f) : f;
                list.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(f), Command = target ?? f, Exe = target, Source = src,
                    Location = f, ValueName = name, Enabled = IsApproved(src, name),
                });
            }
        }

        static void ReadStore(List<StartupItem> list)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(StoreTasks))
            {
                if (k == null) return;
                foreach (var pfn in k.GetSubKeyNames())
                    using (var pk = k.OpenSubKey(pfn))
                    {
                        if (pk == null) continue;
                        foreach (var task in pk.GetSubKeyNames())
                            using (var tk = pk.OpenSubKey(task))
                            {
                                if (!(tk?.GetValue("State") is int state)) continue;
                                var friendly = pfn.Split('_')[0];
                                int dot = friendly.IndexOf('.');
                                if (dot > 0 && dot < friendly.Length - 1) friendly = friendly.Substring(dot + 1);
                                list.Add(new StartupItem
                                {
                                    Name = friendly, Command = pfn + " (" + task + ")", Source = StartupSource.StoreApp,
                                    Location = StoreTasks + "\\" + pfn + "\\" + task, ValueName = task,
                                    Enabled = state == 2 || state == 4,
                                });
                            }
                    }
            }
        }

        static void ReadTasks(List<StartupItem> list, bool includeWindows)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
            foreach (var f in SafeFiles(root))
            {
                var name = f.Substring(root.Length).Replace('/', '\\');
                if (!includeWindows && name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var doc = new XmlDocument();
                    doc.Load(f);
                    bool atStart = doc.SelectSingleNode("//*[local-name()='LogonTrigger' or local-name()='BootTrigger']") != null;
                    if (!atStart) continue;
                    var en = doc.SelectSingleNode("//*[local-name()='Settings']/*[local-name()='Enabled']")?.InnerText;
                    var cmd = doc.SelectSingleNode("//*[local-name()='Exec']/*[local-name()='Command']")?.InnerText ?? "";
                    var args = doc.SelectSingleNode("//*[local-name()='Exec']/*[local-name()='Arguments']")?.InnerText ?? "";
                    var author = doc.SelectSingleNode("//*[local-name()='RegistrationInfo']/*[local-name()='Author']")?.InnerText;
                    var exe = cmd.Length > 0 ? Environment.ExpandEnvironmentVariables(cmd.Trim('"')) : null;
                    list.Add(new StartupItem
                    {
                        Name = name.TrimStart('\\'), Command = (cmd + " " + args).Trim(), Exe = exe, Source = StartupSource.Task,
                        Location = name, ValueName = name, Enabled = !string.Equals(en, "false", StringComparison.OrdinalIgnoreCase),
                        Publisher = author != null && !author.StartsWith("$(") ? author : null,
                    });
                }
                catch { }
            }
        }

        static IEnumerable<string> SafeFiles(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files = null, dirs = null;
                try { files = Directory.GetFiles(d); dirs = Directory.GetDirectories(d); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (dirs != null) foreach (var s in dirs) stack.Push(s);
            }
        }

        static void ReadServices(List<StartupItem> list, bool includeWindows)
        {
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = b.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            {
                if (k == null) return;
                foreach (var n in k.GetSubKeyNames())
                    using (var s = k.OpenSubKey(n))
                    {
                        if (s == null) continue;
                        if (!(s.GetValue("Type") is int type) || (type & 0x30) == 0) continue;     // own/shared process services, no drivers
                        if (!(s.GetValue("Start") is int start) || (start != 2 && start != 3 && start != 4)) continue;
                        var img = s.GetValue("ImagePath")?.ToString() ?? "";
                        var exe = InstalledApps.ExePart(img);
                        bool windows = exe == null || exe.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) ||
                                       exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("system32", StringComparison.OrdinalIgnoreCase);
                        if (windows && !includeWindows) continue;
                        if (start == 3 && s.GetValue("ZilchDisabled") == null) continue; // manual services don't start with Windows
                        var display = s.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrEmpty(display) || display.StartsWith("@")) display = n;
                        list.Add(new StartupItem
                        {
                            Name = display, Command = img, Exe = exe, Source = StartupSource.Service,
                            Location = n, ValueName = n, Enabled = start == 2,
                        });
                    }
            }
        }

        // ---------------- changes ----------------

        public static string SetEnabled(StartupItem it, bool on)
        {
            try
            {
                switch (it.Source)
                {
                    case StartupSource.StoreApp:
                        using (var k = Registry.CurrentUser.OpenSubKey(it.Location, true))
                        {
                            if (k == null) return "entry is gone";
                            k.SetValue("State", on ? 2 : 1, RegistryValueKind.DWord);
                        }
                        break;
                    case StartupSource.Task:
                        if (Deleter.Run("schtasks.exe", $"/Change /TN \"{it.Location}\" {(on ? "/ENABLE" : "/DISABLE")}", 30000) != 0) return "schtasks refused (admin needed?)";
                        break;
                    case StartupSource.Service:
                        using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (var k = b.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + it.Location, true))
                        {
                            if (k == null) return "service is gone";
                            k.SetValue("Start", on ? 2 : 3, RegistryValueKind.DWord); // off = Manual: still starts when an app asks for it
                            if (on) k.DeleteValue("ZilchDisabled", false); else k.SetValue("ZilchDisabled", 1, RegistryValueKind.DWord);
                        }
                        break;
                    default:
                        using (var b = Base(it.Source))
                        using (var k = b.CreateSubKey(ApprovedKey(it.Source)))
                        {
                            var v = new byte[12];
                            v[0] = (byte)(on ? 2 : 3);
                            if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(v, 4);
                            k.SetValue(it.ValueName, v, RegistryValueKind.Binary);
                        }
                        break;
                }
                it.Enabled = on;
                return null;
            }
            catch (UnauthorizedAccessException) { return "needs admin"; }
            catch (System.Security.SecurityException) { return "needs admin"; }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Delete the entry itself (Run value, startup shortcut or scheduled task). The program stays installed.</summary>
        public static string Remove(StartupItem it)
        {
            try
            {
                switch (it.Source)
                {
                    case StartupSource.RunUser:
                    case StartupSource.RunMachine:
                    case StartupSource.RunMachine32:
                        if (!Deleter.SplitKey(it.Location, out var hive, out var view, out var sub)) return "bad key";
                        using (var b = RegistryKey.OpenBaseKey(hive, view))
                        using (var k = b.OpenSubKey(sub, true)) k?.DeleteValue(it.ValueName, false);
                        break;
                    case StartupSource.FolderUser:
                    case StartupSource.FolderCommon:
                        if (File.Exists(it.Location)) File.Delete(it.Location);
                        break;
                    case StartupSource.Task:
                        if (Deleter.Run("schtasks.exe", $"/Delete /TN \"{it.Location}\" /F", 30000) != 0) return "schtasks refused (admin needed?)";
                        break;
                    default: return "can't be removed here, only disabled";
                }
                // forget the on/off switch too
                try
                {
                    using (var b = Base(it.Source))
                    using (var k = b.OpenSubKey(ApprovedKey(it.Source), true)) k?.DeleteValue(it.ValueName, false);
                }
                catch { }
                return null;
            }
            catch (UnauthorizedAccessException) { return "needs admin"; }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
