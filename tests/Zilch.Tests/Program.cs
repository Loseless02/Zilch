using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Zilch.Core;

// Self-tests for Zilch's core. Everything that gets deleted here is a dummy
// "ZilchTest*" file, folder or registry key this program created itself.
// Run:  dotnet run --project tests/Zilch.Tests          (add --scan-c for a timed full C: scan)
internal static class Program
{
    static int _pass, _fail;

    static void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  PASS " : "  FAIL ");
        Console.ResetColor();
        Console.WriteLine(what);
    }

    static void Section(string s) { Console.WriteLine(); Console.WriteLine("== " + s); }

    static int Main(string[] args)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "ZilchTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(sandbox);
        try
        {
            GuardTests(sandbox);
            ScannerTests(sandbox);
            DeleterTests(sandbox);
            LeftoverTests(sandbox);
            ReadOnlyTests(args.Contains("--scan-c"));
            HistoryTests();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("CRASH: " + ex);
        }
        finally
        {
            var cleanup = new DeleteResult();
            Deleter.DeletePath(sandbox, false, false, cleanup);
            Check(!Directory.Exists(sandbox), "test sandbox cleaned up");
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    static void GuardTests(string sandbox)
    {
        Section("Guard: what may be deleted");
        string Sf(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var win = Sf(Environment.SpecialFolder.Windows);
        Check(Guard.Classify(Path.GetPathRoot(win)) == Risk.Protected, "drive root is protected");
        Check(Guard.Classify(win) == Risk.Protected, @"C:\Windows is protected");
        Check(Guard.Classify(Path.Combine(win, @"System32\drivers\etc\hosts")) == Risk.Protected, "files under System32 are protected");
        Check(Guard.Classify(Sf(Environment.SpecialFolder.UserProfile)) == Risk.Protected, "your user folder is protected");
        Check(Guard.Classify(Environment.GetEnvironmentVariable("ProgramFiles")) == Risk.Protected, "Program Files itself is protected");
        Check(Guard.Classify(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles"), "SomeApp")) == Risk.Caution, "a Program Files app folder needs Advanced");
        Check(Guard.Classify(Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "Microsoft")) == Risk.Caution, @"AppData\Local\Microsoft is shared (Caution)");
        Check(Guard.IsSharedContainer(Path.Combine(Sf(Environment.SpecialFolder.ApplicationData), "Microsoft")), @"AppData\Roaming\Microsoft never counts as one app's leftover");
        Check(Guard.Classify(Path.Combine(sandbox, "x.txt")) == Risk.Normal, "a temp file is normal");

        AppState.Mode = Mode.Simple;
        Check(!Guard.Allowed(Risk.Caution) && !Guard.Allowed(Risk.Protected), "Simple: Caution and Protected locked");
        AppState.Mode = Mode.Advanced;
        Check(Guard.Allowed(Risk.Caution) && !Guard.Allowed(Risk.Protected), "Advanced: Caution allowed, Protected locked");
        AppState.Mode = Mode.Sudo;
        Check(Guard.Allowed(Risk.Protected), "Sudo: everything allowed");
        AppState.Mode = Mode.Simple;
    }

    // ------------------------------------------------------------------
    static void ScannerTests(string sandbox)
    {
        Section("Scanner: sizes are right");
        var root = Path.Combine(sandbox, "scan");
        Directory.CreateDirectory(Path.Combine(root, @"a\b"));
        Directory.CreateDirectory(Path.Combine(root, "c"));
        File.WriteAllBytes(Path.Combine(root, "top.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, @"a\one.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(root, @"a\b\big.bin"), new byte[50000]);
        File.WriteAllBytes(Path.Combine(root, @"c\small.bin"), new byte[10]);
        var hidden = Path.Combine(root, @"c\hidden.bin");
        File.WriteAllBytes(hidden, new byte[90]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        // a junction back to "a" must not be counted twice
        RunQuiet("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "loop")}\" \"{Path.Combine(root, "a")}\"");

        var s = new Scanner(default);
        var n = s.Scan(root);
        Check(n.Size == 53100, $"total = 53100 bytes (got {n.Size})");
        Check(n.FileCount == 5, $"5 files (got {n.FileCount})");
        Check(n.Children.All(c => c.Name != "loop"), "junction skipped");
        var a = n.Children.First(c => c.Name == "a");
        Check(a.Size == 52000, $"folder a = 52000 (got {a.Size})");
        var top = s.TopFiles();
        Check(top.Count == 5 && top[0].Path.EndsWith("big.bin"), "biggest file first");
        Check(top.Any(f => f.Path.EndsWith("hidden.bin") && f.Hidden), "hidden file seen and flagged");
        Check(Scanner.SizeOf(root) == 53100, "SizeOf matches");
    }

    // ------------------------------------------------------------------
    static void DeleterTests(string sandbox)
    {
        Section("Deleter: only what was asked");
        var root = Path.Combine(sandbox, "del");
        var keep = Path.Combine(sandbox, "keep");
        Directory.CreateDirectory(Path.Combine(root, @"deep\deeper"));
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "precious.txt"), "do not delete");
        var ro = Path.Combine(root, @"deep\deeper\readonly.txt");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        // junction inside the delete target that points at "keep": deleting must NOT follow it
        RunQuiet("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "link_to_keep")}\" \"{keep}\"");

        var r = new DeleteResult();
        Deleter.DeletePath(root, recycle: false, force: false, r);
        Check(!Directory.Exists(root), "folder tree with read-only/hidden/system file deleted");
        Check(File.Exists(Path.Combine(keep, "precious.txt")), "junction target untouched");
        Check(r.Failed.Count == 0, "no failures");

        var f = Path.Combine(sandbox, "single.bin");
        File.WriteAllBytes(f, new byte[4096]);
        r = new DeleteResult();
        Deleter.DeletePath(f, false, false, r);
        Check(!File.Exists(f) && r.BytesFreed == 4096, "single file deleted, 4096 bytes counted");

        var junk = Path.Combine(sandbox, "junkdir");
        Directory.CreateDirectory(Path.Combine(junk, "sub"));
        File.WriteAllText(Path.Combine(junk, "a.tmp"), "1");
        File.WriteAllText(Path.Combine(junk, @"sub\b.tmp"), "2");
        var locked = Path.Combine(junk, "locked.tmp");
        using (var fs = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            r = new DeleteResult();
            Deleter.EmptyFolder(junk, r);
        }
        Check(Directory.Exists(junk), "EmptyFolder keeps the folder itself");
        Check(!File.Exists(Path.Combine(junk, "a.tmp")) && !Directory.Exists(Path.Combine(junk, "sub")), "EmptyFolder removed contents");
        Check(File.Exists(locked), "file in use skipped, no crash");

        // registry
        const string key = @"HKCU\Software\ZilchTestRegistry\Sub";
        Deleter.SetDword(key, "v", 1);
        Check(Deleter.RegistryKeyExists(key), "registry test key created");
        Check(Deleter.DeleteRegistryKey(@"HKCU\Software\ZilchTestRegistry") && !Deleter.RegistryKeyExists(key), "registry key tree deleted");
    }

    // ------------------------------------------------------------------
    static void LeftoverTests(string sandbox)
    {
        Section("Leftover finder: fake app 'ZilchTestApp Pro 2.1' by 'ZilchTestCorp Inc.'");
        string Sf(Environment.SpecialFolder x) => Environment.GetFolderPath(x);
        var roaming = Sf(Environment.SpecialFolder.ApplicationData);
        var local = Sf(Environment.SpecialFolder.LocalApplicationData);
        var docs = Sf(Environment.SpecialFolder.MyDocuments);
        var startMenu = Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs");

        var install = Path.Combine(sandbox, @"Programs\ZilchTestApp");
        var made = new List<string>
        {
            install,
            Path.Combine(roaming, "ZilchTestApp"),
            Path.Combine(local, @"ZilchTestCorp\ZilchTestApp"),
            Path.Combine(docs, "ZilchTestApp"),
        };
        var lookalike = Path.Combine(local, "ZilchTestAppHelperCache");   // similar name: "maybe" only
        var sibling = Path.Combine(local, @"ZilchTestCorp\OtherProduct"); // same publisher, different app: never
        var shortcut = Path.Combine(startMenu, "ZilchTestApp.lnk");
        const string regKey = @"HKCU\Software\ZilchTestCorp\ZilchTestApp";
        const string uninstallKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ZilchTestApp_is1";

        try
        {
            foreach (var d in made.Concat(new[] { lookalike, sibling }))
            {
                Directory.CreateDirectory(d);
                File.WriteAllBytes(Path.Combine(d, "data.bin"), new byte[1234]);
            }
            File.WriteAllBytes(Path.Combine(install, "ZilchTestApp.exe"), new byte[100]);
            File.WriteAllText(shortcut, "");
            Deleter.SetDword(regKey, "Setting", 1);
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZilchTestApp_is1"))
            {
                k.SetValue("DisplayName", "ZilchTestApp Pro 2.1");
                k.SetValue("Publisher", "ZilchTestCorp Inc.");
                k.SetValue("DisplayVersion", "2.1");
                k.SetValue("InstallLocation", install);
                k.SetValue("UninstallString", "\"" + Path.Combine(install, "unins000.exe") + "\"");
            }

            var app = InstalledApps.Load().FirstOrDefault(a => a.Name == "ZilchTestApp Pro 2.1");
            Check(app != null && app.Scope == "Current user", "fake app shows up in installed list");
            Check(app != null && InstalledApps.GuessFolder(app) == install, "install folder detected");

            var sw = Stopwatch.StartNew();
            var simple = new LeftoverFinder(app.Name, app.Publisher, install, app.RegistryKey).Find(includeMedium: false);
            Console.WriteLine($"     (search took {sw.ElapsedMilliseconds} ms, {simple.Count} sure hits)");
            foreach (var l in simple) Console.WriteLine($"       [{l.Kind}] {l.Path}");

            bool Has(IEnumerable<Leftover> ls, string p) => ls.Any(l => string.Equals(l.Path, p, StringComparison.OrdinalIgnoreCase));
            foreach (var d in made) Check(Has(simple, d), "found " + d);
            Check(Has(simple, shortcut), "found Start Menu shortcut");
            Check(Has(simple, regKey), "found registry key");
            Check(Has(simple, uninstallKey), "found Installed Apps entry");
            Check(!Has(simple, sibling), "same publisher's OTHER product not touched");
            Check(!Has(simple, lookalike), "similar-name folder not in Simple results");
            Check(simple.All(l => l.Path.IndexOf("ZilchTest", StringComparison.OrdinalIgnoreCase) >= 0),
                  "every hit is test data (nothing real on this PC matched)");

            var adv = new LeftoverFinder(app.Name, app.Publisher, install, app.RegistryKey).Find(includeMedium: true);
            Check(adv.Any(l => l.Path.Equals(lookalike, StringComparison.OrdinalIgnoreCase) && l.Confidence == Confidence.Medium),
                  "Advanced shows similar-name folder as 'maybe'");
            var advForeign = adv.Where(l => l.Path.IndexOf("ZilchTest", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            Check(advForeign.Count == 0, "Advanced also matched nothing real" + (advForeign.Count > 0 ? ": " + string.Join(", ", advForeign.Select(x => x.Path)) : ""));

            // a folder holding another program must be downgraded
            var guarded = new LeftoverFinder(app.Name, app.Publisher, install, app.RegistryKey) { OtherInstallDirs = { Path.Combine(install, "plugins") } }.Find(false);
            Check(!Has(guarded, install), "install folder containing another program is not a sure hit");

            var r = LeftoverFinder.Remove(simple, recycle: false, force: false);
            Check(r.Failed.Count == 0, "removal had no failures" + (r.Failed.Count > 0 ? ": " + string.Join(", ", r.Failed) : ""));
            Check(made.All(d => !Directory.Exists(d)), "all app folders gone");
            Check(!File.Exists(shortcut), "shortcut gone");
            Check(!Deleter.RegistryKeyExists(regKey) && !Deleter.RegistryKeyExists(uninstallKey), "registry traces gone");
            Check(Directory.Exists(sibling), "other product of same publisher still there");
            Check(Directory.Exists(lookalike), "unchecked 'maybe' folder still there");
        }
        finally
        {
            // clean up every dummy we made, whatever happened
            foreach (var d in made.Concat(new[] { lookalike, sibling, Path.Combine(local, "ZilchTestCorp") }))
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            try { File.Delete(shortcut); } catch { }
            Deleter.DeleteRegistryKey(@"HKCU\Software\ZilchTestCorp");
            Deleter.DeleteRegistryKey(uninstallKey);
        }
    }

    // ------------------------------------------------------------------
    static void ReadOnlyTests(bool scanC)
    {
        Section("Read-only checks on this PC (nothing is changed)");
        var sw = Stopwatch.StartNew();
        var apps = InstalledApps.Load();
        Check(apps.Count > 0, $"{apps.Count} installed programs read in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var junk = Junk.All();
        long total = 0;
        foreach (var j in junk)
        {
            try { var s = j.Measure(); if (s > 0) total += s; Console.WriteLine($"       {j.Name,-32} {(s < 0 ? "-" : Fmt.Size(s))}"); }
            catch (Exception ex) { Check(false, j.Name + " measure threw " + ex.Message); }
        }
        Check(true, $"junk measured: {Fmt.Size(total)} in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var appx = Debloat.LoadAppx();
        // Windows Server (CI runners) may have no Store apps at all; that's fine.
        Check(appx.Count >= 0, $"{appx.Count} Windows apps read in {sw.ElapsedMilliseconds} ms " +
              $"({appx.Count(a => a.Category == AppxCategory.Bloat)} bloat, {appx.Count(a => a.Category == AppxCategory.Optional)} optional, {appx.Count(a => a.Category == AppxCategory.System)} system)");
        Console.WriteLine("       bloat: " + string.Join(", ", appx.Where(a => a.Category == AppxCategory.Bloat).Select(a => a.Friendly)));
        Check(appx.Where(a => a.Category == AppxCategory.Bloat).All(a => !a.Name.StartsWith("Microsoft.WindowsStore") && !a.Name.StartsWith("Microsoft.VCLibs")),
              "Store and frameworks are never marked as bloat");
        Console.WriteLine($"       Edge installed: {Debloat.EdgeInstalled}, WebView2: {Debloat.WebViewInstalled}, OneDrive: {Debloat.OneDriveUninstaller() != null}");

        if (scanC)
        {
            sw.Restart();
            var s = new Scanner(default);
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var n = s.Scan(root);
            Check(n.Size > 0, $"full scan of {root}: {Fmt.Size(n.Size)}, {n.FileCount:N0} files in {sw.Elapsed.TotalSeconds:0.0}s, {s.DeniedCount} unreadable folders");
            foreach (var c in n.Children.OrderByDescending(c => c.Size).Take(8))
                Console.WriteLine($"       {Fmt.Size(c.Size),10}  {c.Name}");
        }
    }

    // ------------------------------------------------------------------
    static void HistoryTests()
    {
        Section("History (Logs tab)");
        bool existed = File.Exists(History.FilePath);
        var backup = existed ? File.ReadAllText(History.FilePath) : null;
        try
        {
            History.Clear();
            History.Add("Test", "first", new[] { "- C:\\a", "- C:\\b" });
            History.Add("Test", "second");
            var h = History.Load();
            Check(h.Count == 2 && h[0].Summary == "second", "newest entry first");
            Check(h[1].Details == "- C:\\a\n- C:\\b", "details round-trip");
        }
        finally
        {
            if (existed) File.WriteAllText(History.FilePath, backup); else History.Clear();
        }
    }

    static void RunQuiet(string exe, string args)
    {
        using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
        { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
    }
}
