using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zilch.Core
{
    public sealed class InstalledApp
    {
        public string Name, Publisher, Version, InstallLocation, UninstallString, QuietUninstallString, DisplayIcon, InstallDate;
        public string RegistryKey;   // e.g. HKLM64\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}
        public string KeyName;       // last segment (GUID or name)
        public bool SystemComponent;
        public bool IsMsi;
        public long EstimatedBytes;  // from registry (often missing or wrong)
        public long MeasuredBytes = -1; // measured from InstallLocation

        public long BestSize => MeasuredBytes > 0 ? MeasuredBytes : EstimatedBytes;
        public string Scope => RegistryKey.StartsWith("HKCU") ? "Current user" : "All users";
        public override string ToString() => Name;
    }

    public static class InstalledApps
    {
        const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<InstalledApp> Load()
        {
            var list = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Read(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64", list, seen);
            Read(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", list, seen);
            Read(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", list, seen);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        static void Read(RegistryHive hive, RegistryView view, string tag, List<InstalledApp> list, HashSet<string> seen)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var un = root.OpenSubKey(UninstallPath))
                {
                    if (un == null) return;
                    foreach (var keyName in un.GetSubKeyNames())
                    {
                        using (var k = un.OpenSubKey(keyName))
                        {
                            if (k == null) continue;
                            var name = k.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (k.GetValue("ParentKeyName") != null) continue;           // updates / patches
                            if ((k.GetValue("ReleaseType") as string)?.Contains("Update") == true) continue;
                            var unStr = k.GetValue("UninstallString") as string;
                            var ver = k.GetValue("DisplayVersion") as string ?? "";

                            // 32/64-bit views of the same key show up twice on some systems
                            if (!seen.Add(name + "|" + ver + "|" + keyName)) continue;

                            var app = new InstalledApp
                            {
                                Name = name.Trim(),
                                Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                                Version = ver,
                                InstallLocation = Clean(k.GetValue("InstallLocation") as string),
                                UninstallString = unStr,
                                QuietUninstallString = k.GetValue("QuietUninstallString") as string,
                                DisplayIcon = k.GetValue("DisplayIcon") as string,
                                InstallDate = k.GetValue("InstallDate") as string ?? "",
                                RegistryKey = tag + "\\" + UninstallPath + "\\" + keyName,
                                KeyName = keyName,
                                SystemComponent = Convert.ToInt32(k.GetValue("SystemComponent") ?? 0) == 1,
                                IsMsi = Convert.ToInt32(k.GetValue("WindowsInstaller") ?? 0) == 1 ||
                                        (unStr ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0,
                            };
                            try { app.EstimatedBytes = Convert.ToInt64(k.GetValue("EstimatedSize") ?? 0) * 1024; } catch { }
                            if (string.IsNullOrEmpty(app.UninstallString) && !app.SystemComponent) app.SystemComponent = true;
                            list.Add(app);
                        }
                    }
                }
            }
            catch { }
        }

        static string Clean(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')).TrimEnd('\\');
            return p.Length < 4 ? null : p;
        }

        /// <summary>Best guess of the folder the program lives in.</summary>
        public static string GuessFolder(InstalledApp a)
        {
            if (!string.IsNullOrEmpty(a.InstallLocation) && Directory.Exists(a.InstallLocation)) return a.InstallLocation;
            foreach (var s in new[] { a.DisplayIcon, a.UninstallString })
            {
                var exe = ExePart(s);
                if (exe == null || exe.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Guard.IsSharedContainer(dir)) return dir;
            }
            return null;
        }

        /// <summary>Extract the executable path from a command line like "C:\x\unins000.exe" /SILENT.</summary>
        public static string ExePart(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int e = cmd.IndexOf('"', 1);
                return e > 1 ? cmd.Substring(1, e - 1) : null;
            }
            var m = Regex.Match(cmd, @"^(.+?\.(exe|dll|ico|bat|cmd))(,\d+)?(\s|$)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : cmd.Split(' ')[0];
        }

        public enum UninstallMode { Normal, Quiet }

        /// <summary>Runs the program's own uninstaller and waits. Returns exit code, or null if there was nothing to run.</summary>
        public static int? RunUninstaller(InstalledApp a, UninstallMode mode)
        {
            string cmd = mode == UninstallMode.Quiet && !string.IsNullOrEmpty(a.QuietUninstallString) ? a.QuietUninstallString : a.UninstallString;
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());

            string exe, args;
            if (a.IsMsi && Regex.IsMatch(a.KeyName, @"^\{[0-9A-Fa-f\-]+\}$"))
            {
                exe = "msiexec.exe";
                args = "/x " + a.KeyName + (mode == UninstallMode.Quiet ? " /qn /norestart" : "");
            }
            else
            {
                exe = ExePart(cmd);
                if (exe == null) return null;
                args = cmd.StartsWith("\"") ? cmd.Substring(exe.Length + 2).Trim() : cmd.Substring(Math.Min(cmd.Length, exe.Length)).Trim();
                if (mode == UninstallMode.Quiet && string.IsNullOrEmpty(a.QuietUninstallString))
                {
                    // Common silent switches by installer family
                    var f = Path.GetFileName(exe).ToLowerInvariant();
                    if (f.StartsWith("unins")) args += " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; // Inno Setup
                    else args += " /S";                                                            // NSIS and many others
                }
            }

            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }))
                {
                    if (p == null) return null;
                    p.WaitForExit();
                    int code = p.ExitCode;
                    // Inno/NSIS uninstallers copy themselves to %TEMP% and return instantly; give them time.
                    WaitForChildUninstallers(TimeSpan.FromMinutes(30));
                    return code;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return null; } // UAC cancelled / missing exe
        }

        static void WaitForChildUninstallers(TimeSpan max)
        {
            var sw = Stopwatch.StartNew();
            System.Threading.Thread.Sleep(1500);
            while (sw.Elapsed < max)
            {
                bool any = false;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var n = p.ProcessName.ToLowerInvariant();
                        if (n.StartsWith("au_") || n.StartsWith("un_a") || n.StartsWith("unins") || n.StartsWith("_iu") || n.EndsWith(".tmp")) { any = true; break; }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (!any) return;
                System.Threading.Thread.Sleep(1000);
            }
        }
    }
}
