using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Zilch.Core
{
    public sealed class DeleteResult
    {
        public int Deleted;
        public int ScheduledForReboot;
        public long BytesFreed;
        public long BytesRecycled;   // part of BytesFreed that only moved to the Recycle Bin
        public List<string> Failed = new List<string>();

        public string Summary()
        {
            var s = $"Removed {Deleted} item(s), freed about {Fmt.Size(BytesFreed)}.";
            if (ScheduledForReboot > 0) s += $"\n{ScheduledForReboot} locked item(s) will be deleted on next restart.";
            if (Failed.Count > 0)
            {
                s += $"\n\n{Failed.Count} item(s) could not be removed:";
                for (int i = 0; i < Math.Min(Failed.Count, 12); i++) s += "\n  " + Failed[i];
                if (Failed.Count > 12) s += $"\n  ...and {Failed.Count - 12} more";
            }
            return s;
        }
    }

    /// <summary>Deletes files, folders and registry keys.</summary>
    public static class Deleter
    {
        /// <summary>
        /// Delete a file or folder.
        /// recycle: send to Recycle Bin instead of wiping.
        /// force: (Sudo) take ownership + grant admins full control when access is denied,
        ///        and schedule locked files for deletion on reboot.
        /// </summary>
        public static void DeletePath(string path, bool recycle, bool force, DeleteResult r)
        {
            path = Guard.Norm(path);
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return;

            long size = isDir ? Scanner.SizeOf(path) : SafeLen(path);

            if (recycle)
            {
                var op = new Native.SHFILEOPSTRUCT
                {
                    wFunc = Native.FO_DELETE,
                    pFrom = path + "\0\0",
                    fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI),
                };
                int rc = Native.SHFileOperationW(ref op);
                if (rc == 0 && !op.fAnyOperationsAborted && !Exists(path)) { r.Deleted++; r.BytesFreed += size; r.BytesRecycled += size; return; }
                if (!force) { r.Failed.Add(path + " (Recycle Bin refused it)"); return; }
                // fall through to a hard delete in Sudo mode
            }

            int failedBefore = r.Failed.Count, rebootBefore = r.ScheduledForReboot;
            var localFails = new List<string>();
            Wipe(path, isDir, force, localFails, r);

            if (localFails.Count > 0 && force)
            {
                TakeOwnership(path, isDir);
                var retry = new List<string>();
                foreach (var f in localFails) Wipe(f, Directory.Exists(f), true, retry, r);
                // whatever is still stuck (in use) goes on the reboot list
                foreach (var f in retry)
                {
                    if (Native.MoveFileExW(@"\\?\" + f, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) r.ScheduledForReboot++;
                    else r.Failed.Add(f);
                }
                if (isDir && Directory.Exists(path))
                {
                    try { Directory.Delete(path, true); }
                    catch { if (Native.MoveFileExW(@"\\?\" + path, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) r.ScheduledForReboot++; }
                }
            }
            else
            {
                foreach (var f in localFails) r.Failed.Add(f);
            }

            if (!Exists(path)) { r.Deleted++; r.BytesFreed += size; }
            else if (r.ScheduledForReboot > rebootBefore) { r.BytesFreed += size; }
            else if (r.Failed.Count == failedBefore) r.Failed.Add(path);
        }

        /// <summary>Delete everything inside a folder but keep the folder. Locked files are skipped silently.</summary>
        public static void EmptyFolder(string folder, DeleteResult r)
        {
            if (!Directory.Exists(folder)) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder); } catch { return; }
            foreach (var e in entries)
            {
                bool dir = Directory.Exists(e);
                long size = dir ? Scanner.SizeOf(e) : SafeLen(e);
                var fails = new List<string>();
                Wipe(e, dir, false, fails, r);
                if (!Exists(e)) { r.Deleted++; r.BytesFreed += size; }
                else if (dir) r.BytesFreed += Math.Max(0, size - Scanner.SizeOf(e)); // partial
            }
        }

        static void Wipe(string path, bool isDir, bool force, List<string> fails, DeleteResult r)
        {
            if (!isDir)
            {
                try
                {
                    var a = File.GetAttributes(path);
                    if ((a & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                        File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                catch { fails.Add(path); }
                return;
            }

            // Reparse points (junctions/symlinks): delete the link, never what it points to.
            try
            {
                var di = new DirectoryInfo(path);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { di.Delete(false); return; }
            }
            catch { fails.Add(path); return; }

            try
            {
                foreach (var f in Directory.EnumerateFiles(path)) Wipe(f, false, force, fails, r);
                foreach (var d in Directory.EnumerateDirectories(path)) Wipe(d, true, force, fails, r);
            }
            catch { fails.Add(path); return; }

            try
            {
                File.SetAttributes(path, FileAttributes.Directory);
                Directory.Delete(path, false);
            }
            catch
            {
                // only report the folder itself if nothing inside it already failed
                bool childFailed = fails.Exists(x => x.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));
                if (!childFailed) fails.Add(path);
            }
        }

        static void TakeOwnership(string path, bool isDir)
        {
            // Administrators group SID works on every language of Windows.
            Run("takeown.exe", isDir ? $"/F \"{path}\" /A /R /D Y" : $"/F \"{path}\" /A");
            Run("icacls.exe", isDir ? $"\"{path}\" /grant *S-1-5-32-544:F /T /C /Q" : $"\"{path}\" /grant *S-1-5-32-544:F /C /Q");
        }

        public static int Run(string exe, string args, int timeoutMs = 10 * 60 * 1000)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                }))
                {
                    p.OutputDataReceived += (s, e) => { };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
        static long SafeLen(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

        // ---------------- Registry ----------------

        /// <summary>Registry path like "HKCU\Software\Foo" or "HKLM64\SOFTWARE\Foo" / "HKLM32\...".</summary>
        public static bool DeleteRegistryKey(string fullKey)
        {
            if (!SplitKey(fullKey, out var hive, out var view, out var sub)) return false;
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                {
                    using (var k = root.OpenSubKey(sub)) if (k == null) return true;
                    root.DeleteSubKeyTree(sub, false);
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool RegistryKeyExists(string fullKey)
        {
            if (!SplitKey(fullKey, out var hive, out var view, out var sub)) return false;
            try { using (var root = RegistryKey.OpenBaseKey(hive, view)) using (var k = root.OpenSubKey(sub)) return k != null; }
            catch { return false; }
        }

        public static bool SplitKey(string fullKey, out RegistryHive hive, out RegistryView view, out string sub)
        {
            hive = RegistryHive.CurrentUser; view = RegistryView.Default; sub = null;
            int i = fullKey.IndexOf('\\');
            if (i < 0) return false;
            var h = fullKey.Substring(0, i).ToUpperInvariant();
            sub = fullKey.Substring(i + 1);
            switch (h)
            {
                case "HKCU": hive = RegistryHive.CurrentUser; view = RegistryView.Registry64; return true;
                case "HKLM": case "HKLM64": hive = RegistryHive.LocalMachine; view = RegistryView.Registry64; return true;
                case "HKLM32": hive = RegistryHive.LocalMachine; view = RegistryView.Registry32; return true;
                case "HKCR": hive = RegistryHive.ClassesRoot; view = RegistryView.Registry64; return true;
                default: return false;
            }
        }

        public static void SetDword(string fullKey, string name, int value)
        {
            if (!SplitKey(fullKey, out var hive, out var view, out var sub)) return;
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var k = root.CreateSubKey(sub)) k?.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
