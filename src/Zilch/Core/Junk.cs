using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Zilch.Core
{
    public sealed class JunkItem
    {
        public string Name, Description;
        public bool NeedsAdmin, AdvancedOnly, DefaultChecked = true;
        public Func<long> Measure;
        public Action<DeleteResult> Clean;
        public long Size = -1;
    }

    /// <summary>Known-safe places Windows and apps pile up throwaway data.</summary>
    public static class Junk
    {
        static string Env(string v) => Environment.GetEnvironmentVariable(v) ?? "";
        static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        static long SizeAll(IEnumerable<string> paths) => paths.Sum(p => Scanner.SizeOf(p));
        static void EmptyAll(IEnumerable<string> paths, DeleteResult r) { foreach (var p in paths) Deleter.EmptyFolder(p, r); }

        static JunkItem Folders(string name, string desc, Func<IEnumerable<string>> dirs, bool admin = false, bool adv = false, bool on = true) =>
            new JunkItem
            {
                Name = name, Description = desc, NeedsAdmin = admin, AdvancedOnly = adv, DefaultChecked = on,
                Measure = () => SizeAll(dirs().Where(Directory.Exists)),
                Clean = r => EmptyAll(dirs().Where(Directory.Exists), r),
            };

        static IEnumerable<string> Glob(string root, params string[] subPatterns)
        {
            // root\*\sub for browser profile folders
            if (!Directory.Exists(root)) yield break;
            string[] profiles;
            try { profiles = Directory.GetDirectories(root); } catch { yield break; }
            foreach (var p in profiles)
                foreach (var s in subPatterns)
                {
                    var d = Path.Combine(p, s);
                    if (Directory.Exists(d)) yield return d;
                }
        }

        public static List<JunkItem> All()
        {
            var list = new List<JunkItem>
            {
                Folders("Temporary files (you)", "Your %TEMP% folder. Files in use are skipped.",
                    () => new[] { Path.GetTempPath() }),

                Folders("Temporary files (Windows)", @"C:\Windows\Temp.",
                    () => new[] { Path.Combine(Win, "Temp") }, admin: true),

                new JunkItem
                {
                    Name = "Recycle Bin", Description = "Everything in the Recycle Bin on all drives.",
                    Measure = () => { var i = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                                      return Native.SHQueryRecycleBinW(null, ref i) == 0 ? i.i64Size : 0; },
                    Clean = r => { var before = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                                   Native.SHQueryRecycleBinW(null, ref before);
                                   if (Native.SHEmptyRecycleBinW(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND) == 0)
                                   { r.Deleted += (int)before.i64NumItems; r.BytesFreed += before.i64Size; } },
                },

                new JunkItem
                {
                    Name = "Thumbnail cache", Description = "Explorer's picture previews. Rebuilt automatically.",
                    Measure = () => ThumbFiles().Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }),
                    Clean = r => { foreach (var f in ThumbFiles()) Deleter.DeletePath(f, false, false, r); },
                },

                Folders("Crash dumps & error reports", "Memory dumps and Windows Error Reporting files.",
                    () => new[] { Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER"),
                                  Path.Combine(Env("ProgramData"), @"Microsoft\Windows\WER"), Path.Combine(Win, "Minidump"),
                                  Path.Combine(Win, "LiveKernelReports") }),

                new JunkItem
                {
                    Name = "Full memory dump", Description = @"C:\Windows\MEMORY.DMP from a past blue screen. Often several GB.", NeedsAdmin = true,
                    Measure = () => File.Exists(Path.Combine(Win, "MEMORY.DMP")) ? new FileInfo(Path.Combine(Win, "MEMORY.DMP")).Length : 0,
                    Clean = r => Deleter.DeletePath(Path.Combine(Win, "MEMORY.DMP"), false, true, r),
                },

                Folders("Browser caches", "Cached web files for Chrome, Edge, Brave, Opera, Vivaldi, Firefox. Logins and history stay.",
                    () => BrowserCaches()),

                Folders("GPU shader caches", "DirectX, NVIDIA, AMD and Intel shader caches. Rebuilt by games when needed.",
                    () => new[] { Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"),
                                  Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\VkCache"),
                                  Path.Combine(Local, @"Intel\ShaderCache"), Path.Combine(Path.Combine(Local, "..", "LocalLow"), @"NVIDIA\PerDriverVersion\DXCache") }),

                Folders("Windows Update downloads", "Already-installed update packages.",
                    () => new[] { Path.Combine(Win, @"SoftwareDistribution\Download") }, admin: true),

                Folders("Delivery Optimization cache", "Update pieces Windows shares with other PCs.",
                    () => new[] { Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                                  Path.Combine(Win, @"SoftwareDistribution\DeliveryOptimization") }, admin: true),

                Folders("Windows logs", @"C:\Windows\Logs and CBS logs.",
                    () => new[] { Path.Combine(Win, "Logs") }, admin: true, adv: true),

                Folders("Prefetch", "Launch-speed hints. Windows rebuilds them; first app starts may be a bit slower.",
                    () => new[] { Path.Combine(Win, "Prefetch") }, admin: true, adv: true, on: false),

                new JunkItem
                {
                    Name = "Old Windows installation", Description = @"C:\Windows.old after a big Windows upgrade. Removes the option to roll back.",
                    NeedsAdmin = true, AdvancedOnly = true, DefaultChecked = false,
                    Measure = () => Scanner.SizeOf(Path.Combine(Path.GetPathRoot(Win), "Windows.old")),
                    Clean = r => Deleter.DeletePath(Path.Combine(Path.GetPathRoot(Win), "Windows.old"), false, true, r),
                },

                new JunkItem
                {
                    Name = "Hibernation file", Description = "hiberfil.sys. Turns hibernation and Fast Startup off (powercfg /h off).",
                    NeedsAdmin = true, AdvancedOnly = true, DefaultChecked = false,
                    Measure = () => HiddenFileSize(Path.Combine(Path.GetPathRoot(Win), "hiberfil.sys")),
                    Clean = r => { long s = HiddenFileSize(Path.Combine(Path.GetPathRoot(Win), "hiberfil.sys"));
                                   if (Deleter.Run("powercfg.exe", "/h off") == 0) { r.Deleted++; r.BytesFreed += s; } else r.Failed.Add("powercfg /h off"); },
                },

                new JunkItem
                {
                    Name = "Component store cleanup", Description = "DISM /StartComponentCleanup. Removes superseded Windows components. Slow (minutes).",
                    NeedsAdmin = true, AdvancedOnly = true, DefaultChecked = false,
                    Measure = () => -1,
                    Clean = r => { if (Deleter.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /Quiet", 60 * 60 * 1000) == 0) r.Deleted++; else r.Failed.Add("DISM component cleanup"); },
                },
            };
            return list;
        }

        static long HiddenFileSize(string path)
        {
            var list = Scanner.ListFiles(Path.GetDirectoryName(path));
            foreach (var f in list) if (string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) return f.Size;
            return 0;
        }

        static IEnumerable<string> ThumbFiles()
        {
            var d = Path.Combine(Local, @"Microsoft\Windows\Explorer");
            if (!Directory.Exists(d)) return new string[0];
            try { return Directory.GetFiles(d, "thumbcache_*.db").Concat(Directory.GetFiles(d, "iconcache_*.db")).ToArray(); }
            catch { return new string[0]; }
        }

        static IEnumerable<string> BrowserCaches()
        {
            var chromium = new[]
            {
                @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data",
                @"Vivaldi\User Data", @"Chromium\User Data",
            };
            var subs = new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };
            foreach (var c in chromium)
                foreach (var d in Glob(Path.Combine(Local, c), subs)) yield return d;

            // Opera keeps cache directly in the profile root
            foreach (var o in new[] { @"Opera Software\Opera Stable", @"Opera Software\Opera GX Stable" })
                foreach (var s in subs)
                {
                    var d = Path.Combine(Local, o, s);
                    if (Directory.Exists(d)) yield return d;
                }

            foreach (var d in Glob(Path.Combine(Local, @"Mozilla\Firefox\Profiles"), "cache2", "startupCache")) yield return d;
        }
    }
}
