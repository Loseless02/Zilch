using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace Zilch.Core
{
    public enum ProcKind { Windows, Service, App, Background, Unknown }

    public sealed class ProcInfo
    {
        public int Pid, ParentPid, Threads, Handles;
        public uint Session;
        public long CreateTime;          // FILETIME, tells a process apart from a later one with the same PID
        public string Name, Path, Company, Description, Window;
        public long PrivateMem, WorkingSet;
        public double Cpu, IoRate, Gpu;  // %, bytes/s, %
        public long Vram;
        public ProcKind Kind;
        public bool Signed;
        public long Key => ((long)Pid << 32) ^ CreateTime;
    }

    public sealed class SystemUsage
    {
        public double Cpu, Gpu = -1;
        public long RamUsed, RamTotal, VramUsed = -1, VramTotal = -1;
        public int Processes, Threads;
    }

    /// <summary>
    /// Takes a snapshot of every process with one kernel call (NtQuerySystemInformation), plus GPU numbers
    /// from Windows' own "GPU Engine" counters. Call Sample() about once a second; rates are per second.
    /// Never throws: anything it can't read just stays empty.
    /// </summary>
    public sealed class ProcSampler
    {
        readonly Dictionary<long, long> _prevCpu = new Dictionary<long, long>();
        readonly Dictionary<long, long> _prevIo = new Dictionary<long, long>();
        readonly Dictionary<long, string> _paths = new Dictionary<long, string>();
        readonly Dictionary<string, (string company, string desc, bool signed)> _files = new Dictionary<string, (string, string, bool)>(StringComparer.OrdinalIgnoreCase);
        readonly Gpu _gpu = new Gpu();
        long _prevTick, _prevIdle, _prevKernel, _prevUser;
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        static readonly int Cores = Environment.ProcessorCount;

        static readonly HashSet<string> CoreNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe",
            "services.exe", "lsass.exe", "winlogon.exe", "LsaIso.exe", "fontdrvhost.exe", "dwm.exe", "svchost.exe",
        };

        /// <summary>Killing these crashes or logs out Windows.</summary>
        public static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe",
            "services.exe", "lsass.exe", "winlogon.exe", "LsaIso.exe",
        };

        public SystemUsage Usage = new SystemUsage();

        public List<ProcInfo> Sample()
        {
            var list = new List<ProcInfo>();
            long now = Stopwatch.GetTimestamp();
            double dt = _prevTick == 0 ? 0 : (now - _prevTick) / (double)Stopwatch.Frequency;
            _prevTick = now;

            var buf = Query();
            if (buf != IntPtr.Zero)
            {
                try { Parse(buf, list); }
                finally { Marshal.FreeHGlobal(buf); }
            }

            var windows = WindowTitles();
            var seen = new HashSet<long>();
            foreach (var p in list)
            {
                var key = p.Key;
                seen.Add(key);
                if (dt > 0 && _prevCpu.TryGetValue(key, out var pc)) p.Cpu = Math.Min(100, Math.Max(0, (CpuOf(p) - pc) / (dt * 1e7 * Cores) * 100));
                _prevCpu[key] = CpuOf(p);
                if (dt > 0 && _prevIo.TryGetValue(key, out var pio)) p.IoRate = Math.Max(0, (IoOf(p) - pio) / dt);
                _prevIo[key] = IoOf(p);

                if (!_paths.TryGetValue(key, out var path)) { path = ImagePath(p.Pid); _paths[key] = path; }
                p.Path = path;
                if (path != null)
                {
                    if (!_files.TryGetValue(path, out var fi)) { fi = FileFacts(path); _files[path] = fi; }
                    p.Company = fi.company; p.Description = fi.desc; p.Signed = fi.signed;
                }
                if (windows.TryGetValue(p.Pid, out var title)) p.Window = title;
                p.Kind = KindOf(p);
            }
            Forget(_prevCpu, seen); Forget(_prevIo, seen); Forget(_paths, seen); Forget(_cpuRaw, seen); Forget(_ioRaw, seen);

            try { _gpu.Apply(list, Usage); } catch { Usage.Gpu = -1; }

            // system totals
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long di = idle - _prevIdle, dk = kernel - _prevKernel, du = user - _prevUser;
                if (_prevKernel != 0 && dk + du > 0) Usage.Cpu = Math.Max(0, Math.Min(100, 100.0 * (dk + du - di) / (dk + du)));
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            }
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref ms)) { Usage.RamTotal = (long)ms.ullTotalPhys; Usage.RamUsed = (long)(ms.ullTotalPhys - ms.ullAvailPhys); }
            Usage.Processes = list.Count;
            Usage.Threads = list.Sum(p => p.Threads);
            return list;
        }

        static void Forget<T>(Dictionary<long, T> d, HashSet<long> keep)
        {
            if (d.Count <= keep.Count + 64) return;
            foreach (var k in d.Keys.Where(k => !keep.Contains(k)).ToList()) d.Remove(k);
        }

        long CpuOf(ProcInfo p) => _cpuRaw.TryGetValue(p.Key, out var v) ? v : 0;
        long IoOf(ProcInfo p) => _ioRaw.TryGetValue(p.Key, out var v) ? v : 0;
        readonly Dictionary<long, long> _cpuRaw = new Dictionary<long, long>();
        readonly Dictionary<long, long> _ioRaw = new Dictionary<long, long>();

        ProcKind KindOf(ProcInfo p)
        {
            if (p.Pid <= 4 || CoreNames.Contains(p.Name)) return ProcKind.Windows;
            if (p.Path != null && p.Path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) return ProcKind.Windows;
            if (p.Path != null && !p.Signed && string.IsNullOrWhiteSpace(p.Company)) return ProcKind.Unknown;
            if (p.Session == 0) return ProcKind.Service;
            if (p.Window != null) return ProcKind.App;
            return ProcKind.Background;
        }

        // ---------------- NtQuerySystemInformation ----------------

        static IntPtr Query()
        {
            int size = 1 << 20;
            for (int i = 0; i < 6; i++)
            {
                var buf = Marshal.AllocHGlobal(size);
                int st = NtQuerySystemInformation(5 /* SystemProcessInformation */, buf, size, out int need);
                if (st == 0) return buf;
                Marshal.FreeHGlobal(buf);
                if (st != unchecked((int)0xC0000004)) return IntPtr.Zero; // not "buffer too small"
                size = Math.Max(size * 2, need + (1 << 16));
            }
            return IntPtr.Zero;
        }

        // SYSTEM_PROCESS_INFORMATION offsets (x64)
        void Parse(IntPtr buf, List<ProcInfo> list)
        {
            long off = 0;
            while (true)
            {
                var b = new IntPtr(buf.ToInt64() + off);
                int next = Marshal.ReadInt32(b, 0);
                var p = new ProcInfo
                {
                    Threads = Marshal.ReadInt32(b, 4),
                    PrivateMem = Marshal.ReadInt64(b, 8),     // private working set ("Memory" in Task Manager)
                    CreateTime = Marshal.ReadInt64(b, 32),
                    Pid = (int)Marshal.ReadInt64(b, 80),
                    ParentPid = (int)Marshal.ReadInt64(b, 88),
                    Handles = Marshal.ReadInt32(b, 96),
                    Session = (uint)Marshal.ReadInt32(b, 100),
                    WorkingSet = Marshal.ReadInt64(b, 144),
                };
                long user = Marshal.ReadInt64(b, 40), kernel = Marshal.ReadInt64(b, 48);
                long io = Marshal.ReadInt64(b, 232) + Marshal.ReadInt64(b, 240); // read + write bytes
                int len = Marshal.ReadInt16(b, 56);
                var namePtr = Marshal.ReadIntPtr(b, 64);
                p.Name = p.Pid == 0 ? "Idle" : (namePtr != IntPtr.Zero && len > 0 ? Marshal.PtrToStringUni(namePtr, len / 2) : "System");
                if (p.Pid != 0)
                {
                    _cpuRaw[p.Key] = user + kernel;
                    _ioRaw[p.Key] = io;
                    list.Add(p);
                }
                if (next == 0) break;
                off += next;
            }
        }

        public static string ImagePath(int pid)
        {
            var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        static (string, string, bool) FileFacts(string path)
        {
            string company = null, desc = null;
            bool signed = false;
            try { var v = FileVersionInfo.GetVersionInfo(path); company = v.CompanyName?.Trim(); desc = v.FileDescription?.Trim(); } catch { }
            try { using (var c = X509Certificate.CreateFromSignedFile(path)) { signed = true; if (string.IsNullOrEmpty(company)) company = SubjectName(c.Subject); } } catch { }
            return (company, desc, signed);
        }

        static string SubjectName(string subject)
        {
            foreach (var part in subject.Split(','))
            {
                var t = part.Trim();
                if (t.StartsWith("O=")) return t.Substring(2).Trim('"');
            }
            return null;
        }

        static Dictionary<int, string> WindowTitles()
        {
            var d = new Dictionary<int, string>();
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                    int n = GetWindowTextLength(h);
                    if (n <= 0) return true;
                    var sb = new StringBuilder(n + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    GetWindowThreadProcessId(h, out int pid);
                    if (!d.ContainsKey(pid)) d[pid] = sb.ToString();
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return d;
        }

        // ---------------- killing ----------------

        /// <summary>The process and everything it started (children first).</summary>
        public static List<ProcInfo> Tree(ProcInfo root, List<ProcInfo> all)
        {
            var result = new List<ProcInfo>();
            void Walk(ProcInfo p, int depth)
            {
                if (depth > 64) return;
                foreach (var c in all)
                    if (c.ParentPid == p.Pid && c.CreateTime >= p.CreateTime && c.Pid != p.Pid) Walk(c, depth + 1);
                result.Add(p);
            }
            Walk(root, 0);
            return result;
        }

        public static string Kill(ProcInfo p)
        {
            try
            {
                using (var proc = Process.GetProcessById(p.Pid))
                {
                    if (proc.StartTime.ToFileTime() != p.CreateTime && p.CreateTime != 0) return "already gone (PID reused)";
                    proc.Kill();
                    proc.WaitForExit(3000);
                    return null;
                }
            }
            catch (ArgumentException) { return null; } // already exited
            catch (Exception ex) { return ex.Message; }
        }

        // ---------------- interop ----------------

        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

        // ---------------- GPU ----------------

        /// <summary>Per-process GPU load and video memory, from the same counters Task Manager uses.</summary>
        sealed class Gpu
        {
            Dictionary<string, CounterSample> _prev = new Dictionary<string, CounterSample>();
            bool _broken;
            long _vramTotal = -2;

            public void Apply(List<ProcInfo> list, SystemUsage u)
            {
                if (_vramTotal == -2) _vramTotal = DedicatedVideoMemory();
                u.VramTotal = _vramTotal;
                if (_broken) { u.Gpu = -1; return; }
                var byPid = list.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());

                // load: sum per (pid, engine type), the busiest engine type is the process' GPU %
                var cat = new PerformanceCounterCategory("GPU Engine");
                InstanceDataCollectionCollection data;
                try { data = cat.ReadCategory(); }
                catch { _broken = true; u.Gpu = -1; return; }
                var util = data["Utilization Percentage"];
                var cur = new Dictionary<string, CounterSample>();
                var perPidEng = new Dictionary<(int, string), double>();
                var perEng = new Dictionary<string, double>();
                if (util != null)
                    foreach (InstanceData inst in util.Values)
                    {
                        cur[inst.InstanceName] = inst.Sample;
                        if (!_prev.TryGetValue(inst.InstanceName, out var old)) continue;
                        double v;
                        try { v = CounterSample.Calculate(old, inst.Sample); } catch { continue; }
                        if (v <= 0 || double.IsNaN(v)) continue;
                        if (!ParseInstance(inst.InstanceName, out int pid, out string eng)) continue;
                        perPidEng[(pid, eng)] = (perPidEng.TryGetValue((pid, eng), out var a) ? a : 0) + v;
                        perEng[eng] = (perEng.TryGetValue(eng, out var b) ? b : 0) + v;
                    }
                _prev = cur;
                foreach (var kv in perPidEng)
                    if (byPid.TryGetValue(kv.Key.Item1, out var p)) p.Gpu = Math.Max(p.Gpu, Math.Min(100, kv.Value));
                u.Gpu = perEng.Count == 0 ? 0 : Math.Min(100, perEng.Values.Max());

                // dedicated video memory per process and in total
                try
                {
                    var mem = new PerformanceCounterCategory("GPU Process Memory").ReadCategory()["Dedicated Usage"];
                    if (mem != null)
                        foreach (InstanceData inst in mem.Values)
                            if (ParseInstance(inst.InstanceName, out int pid, out _) && byPid.TryGetValue(pid, out var p))
                                p.Vram += inst.RawValue;
                    var ad = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory()["Dedicated Usage"];
                    if (ad != null) { long t = 0; foreach (InstanceData inst in ad.Values) t = Math.Max(t, inst.RawValue); u.VramUsed = t; }
                }
                catch { }
            }

            // "pid_1234_luid_0x00000000_0x0000C1A5_phys_0_eng_0_engtype_3D"
            static bool ParseInstance(string s, out int pid, out string eng)
            {
                pid = 0; eng = "";
                if (!s.StartsWith("pid_")) return false;
                int e = s.IndexOf('_', 4);
                if (e < 0 || !int.TryParse(s.Substring(4, e - 4), out pid)) return false;
                int t = s.IndexOf("engtype_", StringComparison.Ordinal);
                if (t >= 0) eng = s.Substring(t + 8);
                return true;
            }

            static long DedicatedVideoMemory()
            {
                long best = -1;
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                        if (k != null)
                            foreach (var n in k.GetSubKeyNames())
                                try
                                {
                                    using (var a = k.OpenSubKey(n))
                                    {
                                        var v = a?.GetValue("HardwareInformation.qwMemorySize");
                                        long m = v is long l ? l : v is byte[] bytes && bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : v is int i ? (uint)i : -1;
                                        best = Math.Max(best, m);
                                    }
                                }
                                catch { }
                }
                catch { }
                return best;
            }
        }
    }
}
