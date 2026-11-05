using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Zilch.Core
{
    public enum AppxCategory { Bloat, Optional, System }

    public sealed class AppxApp
    {
        public string Name, Friendly, Version, InstallLocation;
        public List<string> FullNames = new List<string>();
        public string FamilyName;
        public bool NonRemovable, IsFramework, Provisioned;
        public string Signature;
        public AppxCategory Category;
        public long Size = -1;

        // read from the package's AppxManifest.xml (see Explain.ReadManifests)
        public string DisplayName, PublisherName, Description;
        public List<string> Dependencies = new List<string>();
        public List<string> UsedBy = new List<string>();
        public List<string> Does = new List<string>();      // plain-English list of what it hooks into
        public List<string> Access = new List<string>();    // sensitive capabilities
        public bool StartsWithWindows;
    }

    /// <summary>Preinstalled Windows apps (Appx/MSIX), Microsoft Edge and OneDrive.</summary>
    public static class Debloat
    {
        // name prefix -> friendly name. Everything here is pre-checked in the Windows Bloat tab.
        static readonly (string prefix, string friendly)[] BloatList =
        {
            ("Microsoft.BingNews", "News"), ("Microsoft.BingWeather", "Weather"), ("Microsoft.BingSearch", "Bing Search"),
            ("Microsoft.BingFinance", "Money"), ("Microsoft.BingSports", "Sports"), ("Microsoft.BingTranslator", "Translator"),
            ("Microsoft.GetHelp", "Get Help"), ("Microsoft.Getstarted", "Tips"), ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection"),
            ("Microsoft.People", "People"), ("Microsoft.PowerAutomateDesktop", "Power Automate"), ("Microsoft.Todos", "Microsoft To Do"),
            ("Microsoft.WindowsFeedbackHub", "Feedback Hub"), ("Microsoft.WindowsMaps", "Maps"), ("Microsoft.ZuneVideo", "Movies & TV"),
            ("Clipchamp.Clipchamp", "Clipchamp"), ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub"),
            ("Microsoft.OutlookForWindows", "Outlook (new)"), ("MicrosoftTeams", "Teams (personal)"),
            ("Microsoft.549981C3F5F10", "Cortana"), ("Microsoft.XboxApp", "Xbox Console Companion"), ("Microsoft.GamingApp", "Xbox app"),
            ("Microsoft.XboxGameOverlay", "Xbox Game Overlay"), ("Microsoft.XboxSpeechToTextOverlay", "Xbox Speech To Text"),
            ("Microsoft.YourPhone", "Phone Link"), ("microsoft.windowscommunicationsapps", "Mail and Calendar (old)"),
            ("Microsoft.Copilot", "Copilot"), ("Microsoft.Windows.Ai.Copilot.Provider", "Copilot provider"), ("Microsoft.MicrosoftCopilot", "Copilot"),
            ("Microsoft.SkypeApp", "Skype"), ("Microsoft.Wallet", "Wallet"), ("Microsoft.MixedReality.Portal", "Mixed Reality Portal"),
            ("Microsoft.Microsoft3DViewer", "3D Viewer"), ("Microsoft.MSPaint", "Paint 3D"), ("Microsoft.Windows.DevHome", "Dev Home"),
            ("MicrosoftCorporationII.MicrosoftFamily", "Family Safety"), ("Microsoft.OneDriveSync", "OneDrive (app part)"),
            ("MicrosoftWindows.CrossDevice", "Cross Device Experience"), ("MicrosoftWindows.Client.WebExperience", "Widgets"),
            ("Microsoft.StartExperiencesApp", "Start menu suggestions"), ("Microsoft.Office.OneNote", "OneNote for Windows 10"),
            ("Microsoft.Messaging", "Messaging"), ("Microsoft.OneConnect", "Mobile Plans"), ("Microsoft.Print3D", "Print 3D"),
            ("Microsoft.NetworkSpeedTest", "Network Speed Test"), ("Microsoft.News", "News"), ("Microsoft.Office.Sway", "Sway"),
            ("Microsoft.MicrosoftJournal", "Journal"), ("Microsoft.Whiteboard", "Whiteboard"), ("MicrosoftCorporationII.QuickAssist", "Quick Assist"),
            ("Microsoft.Edge.GameAssist", "Edge Game Assist"), ("Microsoft.WindowsMeetNow", "Meet Now"),
            // third-party stuff Windows drops on new PCs
            ("king.com.", "Candy Crush & friends"), ("SpotifyAB.SpotifyMusic", "Spotify"), ("Disney", "Disney+"), ("Facebook", "Facebook"),
            ("Instagram", "Instagram"), ("BytedancePte.Ltd.TikTok", "TikTok"), ("AmazonVideo.PrimeVideo", "Prime Video"),
            ("Amazon.com.Amazon", "Amazon"), ("4DF9E0F8.Netflix", "Netflix"), ("7EE7776C.LinkedInforWindows", "LinkedIn"),
            ("9E2F88E3.Twitter", "Twitter / X"), ("Duolingo", "Duolingo"), ("AdobeSystemsIncorporated.AdobeExpress", "Adobe Express"),
            ("AdobeSystemsIncorporated.AdobePhotoshopExpress", "Photoshop Express"), ("PandoraMediaInc", "Pandora"),
            ("ShazamEntertainmentLtd.Shazam", "Shazam"), ("Flipboard", "Flipboard"), ("Picsart", "Picsart"),
            ("Microsoft.BingWallpaper", "Bing Wallpaper"), ("A278AB0D.MarchofEmpires", "March of Empires"), ("A278AB0D.DisneyMagicKingdoms", "Disney Magic Kingdoms"),
            ("DolbyLaboratories.DolbyAccess", "Dolby Access"), ("Microsoft.MinecraftEducationEdition", "Minecraft Education"),
        };

        // Removing these breaks Windows (Store, Start menu, sign-in, Settings, frameworks...). Sudo mode only.
        static readonly string[] SystemPrefixes =
        {
            "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.VCLibs", "Microsoft.UI.Xaml",
            "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Microsoft.Services.Store", "Microsoft.SecHealthUI", "Microsoft.Windows.ShellExperienceHost",
            "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
            "Microsoft.Windows.CloudExperienceHost", "windows.immersivecontrolpanel", "Microsoft.Windows.Search", "Microsoft.LockApp",
            "Microsoft.Windows.OOBE", "Microsoft.Win32WebViewHost", "Microsoft.Windows.ContentDeliveryManager", "Microsoft.Windows.Apprep",
            "Microsoft.Windows.AssignedAccessLockApp", "Microsoft.Windows.CapturePicker", "Microsoft.Windows.PeopleExperienceHost",
            "Microsoft.Windows.PinningConfirmationDialog", "Microsoft.Windows.SecureAssessmentBrowser", "Microsoft.Windows.XGpuEjectDialog",
            "Microsoft.Windows.ParentalControls", "Microsoft.Windows.NarratorQuickStart", "Microsoft.Windows.PrintQueueActionCenter",
            "Microsoft.CredDialogHost", "Microsoft.ECApp", "Microsoft.AsyncTextService", "Microsoft.BioEnrollment", "Microsoft.MicrosoftEdgeDevToolsClient",
            "Microsoft.Windows.FileExplorer", "MicrosoftWindows.UndockedDevKit", "Microsoft.WindowsAppSDK", "Microsoft.WidgetsPlatformRuntime",
            "windows.", "Windows.", "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "MicrosoftWindows.LKG", "1527c705-839a-4832-9118-54d4Bd6a0c89",
            "c5e2524a-ea46-4f67-841f-6a9465d9d515", "E2A4F912-2574-4A75-9BB0-0D023378592B", "F46D4000-FD22-4DB4-AC8E-4E1DDDE828FE",
            "Microsoft.Windows.SecHealthUI", "MicrosoftWindows.Ai.",
        };

        // Edge has its own section.
        static readonly string[] HiddenHere = { "Microsoft.MicrosoftEdge", "Microsoft.MicrosoftEdge.Stable", "Microsoft.MicrosoftEdgeDevToolsClient" };

        public static List<AppxApp> LoadAppx()
        {
            var all = Elevation.IsAdmin ? "-AllUsers" : "";
            var script = $@"
$prov = @{{}}
try {{ Get-AppxProvisionedPackage -Online -ErrorAction Stop | ForEach-Object {{ $prov[$_.DisplayName] = 1 }} }} catch {{}}
Get-AppxPackage {all} -PackageTypeFilter Main,Framework -ErrorAction SilentlyContinue | ForEach-Object {{
  $p = $prov.ContainsKey($_.Name)
  ""{{0}}`t{{1}}`t{{2}}`t{{3}}`t{{4}}`t{{5}}`t{{6}}`t{{7}}`t{{8}}"" -f $_.Name, $_.PackageFullName, $_.PackageFamilyName, [int][bool]$_.NonRemovable, [int][bool]$_.IsFramework, $_.SignatureKind, $_.Version, $_.InstallLocation, [int]$p
}}";
            var (_, output) = PS.Run(script, 120000);
            var byName = new Dictionary<string, AppxApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                if (f.Length < 9) continue;
                var name = f[0];
                if (HiddenHere.Any(h => name.Equals(h, StringComparison.OrdinalIgnoreCase))) continue;
                if (!byName.TryGetValue(name, out var a))
                {
                    a = new AppxApp
                    {
                        Name = name, FamilyName = f[2], NonRemovable = f[3] == "1", IsFramework = f[4] == "1",
                        Signature = f[5], Version = f[6], InstallLocation = f[7], Provisioned = f[8] == "1",
                    };
                    var b = BloatList.FirstOrDefault(x => Matches(name, x.prefix));
                    if (b.prefix != null) { a.Category = AppxCategory.Bloat; a.Friendly = b.friendly; }
                    else if (a.NonRemovable || a.IsFramework || a.Signature == "System" ||
                             SystemPrefixes.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                        a.Category = AppxCategory.System;
                    else a.Category = AppxCategory.Optional;
                    a.Friendly = a.Friendly ?? Pretty(name);
                    byName[name] = a;
                }
                if (!a.FullNames.Contains(f[1])) a.FullNames.Add(f[1]);
                if (f[3] == "1") a.NonRemovable = true;
            }
            var list = byName.Values.ToList();
            try { Explain.ReadManifests(list); } catch { }
            list.Sort((x, y) => x.Category != y.Category ? x.Category.CompareTo(y.Category) : string.Compare(x.Friendly, y.Friendly, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>
        /// "Microsoft.Office.OneNote" must match exactly (not OneNoteVirtualPrinter).
        /// "king.com." matches everything from that publisher; "Duolingo" also matches "D5EA27B7.Duolingo-...".
        /// </summary>
        static bool Matches(string name, string pattern)
        {
            if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            if (pattern.EndsWith(".")) return name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
            if (name.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase)) return true;
            return !pattern.Contains(".") && name.IndexOf("." + pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string Pretty(string name)
        {
            // "Microsoft.WindowsCalculator" -> "WindowsCalculator"
            var i = name.LastIndexOf('.');
            var s = i >= 0 && i < name.Length - 1 ? name.Substring(i + 1) : name;
            return s.Length < 3 ? name : s;
        }

        /// <summary>
        /// Removes Appx packages for every user and deprovisions them so new accounts and
        /// feature updates don't bring them back. force = Sudo: also marks NonRemovable/system
        /// packages as end-of-life first so Windows lets them go.
        /// </summary>
        public static (int ok, List<string> failed) RemoveAppx(IEnumerable<AppxApp> apps, bool force)
        {
            var failed = new List<string>();
            int ok = 0;
            bool admin = Elevation.IsAdmin;
            // Sudo: mark the package end-of-life so Windows stops refusing to remove it.
            const string forceBlock = @"
  try { Set-NonRemovableAppsPolicy -Online -PackageFamilyName $p.PackageFamilyName -NonRemovable 0 | Out-Null } catch {}
  New-Item -Path ""$store\EndOfLife\$sid\$($p.PackageFullName)"" -Force | Out-Null
  New-Item -Path ""$store\EndOfLife\S-1-5-18\$($p.PackageFullName)"" -Force | Out-Null";
            foreach (var a in apps)
            {
                var n = PS.Q(a.Name);
                var fb = force ? forceBlock : "";
                var script = admin ? $@"
$ErrorActionPreference = 'SilentlyContinue'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$store = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore'
$pk = @(Get-AppxPackage -AllUsers -Name {n})
foreach ($p in $pk) {{
  New-Item -Path ""$store\Deprovisioned\$($p.PackageFamilyName)"" -Force | Out-Null{fb}
  Remove-AppxPackage -Package $p.PackageFullName -AllUsers
  if (Get-AppxPackage -AllUsers -Name {n} | Where-Object PackageFullName -eq $p.PackageFullName) {{ Remove-AppxPackage -Package $p.PackageFullName }}
}}
Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq {n} | ForEach-Object {{
  try {{ Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -AllUsers -ErrorAction Stop | Out-Null }}
  catch {{ Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName | Out-Null }}
}}
if (Get-AppxPackage -AllUsers -Name {n}) {{ 'STILL' }} else {{ 'GONE' }}
" : $@"
$ErrorActionPreference = 'SilentlyContinue'
Get-AppxPackage -Name {n} | Remove-AppxPackage
if (Get-AppxPackage -Name {n}) {{ 'STILL' }} else {{ 'GONE' }}
";
                var (_, output) = PS.Run(script);
                if (output.Contains("GONE"))
                {
                    ok++;
                    // per-user data folder
                    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", a.FamilyName ?? "");
                    if (!string.IsNullOrEmpty(a.FamilyName) && Directory.Exists(data))
                        Deleter.DeletePath(data, false, force, new DeleteResult());
                }
                else failed.Add(a.Friendly + " (" + a.Name + ")");
            }
            return (ok, failed);
        }

        // ---------------- Stop Windows reinstalling junk ----------------

        public static void BlockReinstall()
        {
            const string cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            foreach (var v in new[] { "ContentDeliveryAllowed", "OemPreInstalledAppsEnabled", "PreInstalledAppsEnabled", "PreInstalledAppsEverEnabled",
                                      "SilentInstalledAppsEnabled", "SubscribedContentEnabled", "SystemPaneSuggestionsEnabled", "SoftLandingEnabled",
                                      "FeatureManagementEnabled", "RotatingLockScreenOverlayEnabled" })
                Deleter.SetDword(cdm, v, 0);
            Deleter.SetDword(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
            if (Elevation.IsAdmin)
            {
                const string cc = @"HKLM64\SOFTWARE\Policies\Microsoft\Windows\CloudContent";
                Deleter.SetDword(cc, "DisableWindowsConsumerFeatures", 1);
                Deleter.SetDword(cc, "DisableConsumerAccountStateContent", 1);
                Deleter.SetDword(cc, "DisableCloudOptimizedContent", 1);
                Deleter.SetDword(@"HKLM64\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                Deleter.SetDword(@"HKLM64\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0); // widgets
            }
        }

        // ---------------- Restore point ----------------

        public static bool CreateRestorePoint()
        {
            Deleter.SetDword(@"HKLM64\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 0);
            var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var (_, o) = PS.Run($@"
try {{ Enable-ComputerRestore -Drive {PS.Q(drive)} -ErrorAction Stop }} catch {{}}
try {{ Checkpoint-Computer -Description 'Zilch cleanup' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'OK' }} catch {{ 'FAIL' }}", 300000);
            return o.Contains("OK");
        }

        // ---------------- Microsoft Edge ----------------

        static string PF86 => Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? Environment.GetEnvironmentVariable("ProgramFiles");
        public static string EdgeDir => Path.Combine(PF86, @"Microsoft\Edge");
        public static string WebViewDir => Path.Combine(PF86, @"Microsoft\EdgeWebView");
        public static bool EdgeInstalled => File.Exists(Path.Combine(EdgeDir, @"Application\msedge.exe"));
        public static bool WebViewInstalled => Directory.Exists(Path.Combine(WebViewDir, "Application"));

        static string FindSetup(string productDir)
        {
            var app = Path.Combine(productDir, "Application");
            if (!Directory.Exists(app)) return null;
            return Directory.GetDirectories(app)
                .Select(d => Path.Combine(d, "Installer", "setup.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p)
                .FirstOrDefault();
        }

        static void Kill(params string[] names)
        {
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); } }
        }

        /// <summary>
        /// Uninstalls Chromium Edge with its own setup.exe (forced, system level), unlocking the
        /// uninstall first the way Windows does for EEA users. Blocks it from coming back.
        /// Leftover folders/shortcuts are returned so the UI can show them for review.
        /// </summary>
        public static (bool removed, string log) RemoveEdge(bool alsoWebView, bool force)
        {
            var log = new System.Text.StringBuilder();
            Kill("msedge", "MicrosoftEdgeUpdate", "msedge_proxy", "identity_helper", "elevation_service");

            // 1. Tell Edge's updater that uninstalling is allowed.
            Deleter.SetDword(@"HKLM32\SOFTWARE\Microsoft\EdgeUpdateDev", "AllowUninstall", 1);
            Deleter.SetDword(@"HKLM64\SOFTWARE\Microsoft\EdgeUpdateDev", "AllowUninstall", 1);

            // 2. The stub file Edge's uninstaller looks for before agreeing to run.
            var stubDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe");
            var stub = Path.Combine(stubDir, "MicrosoftEdge.exe");
            bool madeStub = false;
            try { if (!File.Exists(stub)) { Directory.CreateDirectory(stubDir); File.WriteAllBytes(stub, new byte[0]); madeStub = true; } } catch { }

            var setup = FindSetup(EdgeDir);
            if (setup != null)
            {
                int c = Deleter.Run(setup, "--uninstall --msedge --channel=stable --system-level --verbose-logging --force-uninstall", 5 * 60 * 1000);
                log.AppendLine($"Edge setup.exe --uninstall exit code {c}");
            }
            else log.AppendLine("Edge setup.exe not found (already uninstalled?)");

            if (madeStub) { try { File.Delete(stub); } catch { } }

            // 3. Remove the Edge Appx stubs that keep it pinned.
            var (_, failed) = RemoveAppx(new[]
            {
                new AppxApp { Name = "Microsoft.MicrosoftEdge.Stable", Friendly = "Edge (Appx)" },
                new AppxApp { Name = "Microsoft.MicrosoftEdge", Friendly = "Edge Legacy (Appx)" },
            }, force: true);
            foreach (var f in failed) log.AppendLine("Could not remove " + f);

            if (alsoWebView)
            {
                Kill("msedgewebview2");
                var wv = FindSetup(WebViewDir);
                if (wv != null)
                    log.AppendLine($"WebView2 uninstall exit code {Deleter.Run(wv, "--uninstall --msedgewebview --system-level --verbose-logging --force-uninstall", 5 * 60 * 1000)}");
            }

            // 4. Stop Windows Update / EdgeUpdate from reinstalling it.
            Deleter.SetDword(@"HKLM64\SOFTWARE\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", 1);
            Deleter.SetDword(@"HKLM32\SOFTWARE\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", 1);
            Deleter.SetDword(@"HKLM64\SOFTWARE\Policies\Microsoft\EdgeUpdate", "Install{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}", 0);
            Deleter.SetDword(@"HKLM64\SOFTWARE\Policies\Microsoft\EdgeUpdate", "UpdateDefault", alsoWebView ? 0 : 1);

            bool removed = !EdgeInstalled;
            if (!removed && force)
            {
                // Sudo: the official uninstaller refused. Take the folder down by hand.
                var r = new DeleteResult();
                Deleter.DeletePath(Path.Combine(EdgeDir, "Application"), false, true, r);
                log.AppendLine("Forced delete of Edge\\Application: " + r.Summary());
                removed = !EdgeInstalled;
            }
            log.AppendLine(removed ? "Edge program files are gone." : "Edge is still installed. Try Sudo mode.");
            return (removed, log.ToString());
        }

        /// <summary>Extra Edge traces the generic leftover finder might miss.</summary>
        public static List<Leftover> EdgeLeftovers(bool alsoWebView)
        {
            var l = new List<Leftover>();
            string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            void Dir(string p, string why) { if (Directory.Exists(p)) l.Add(new Leftover { Kind = LeftoverKind.Folder, Path = p, Reason = why, Size = Scanner.SizeOf(p) }); }
            void Svc(string s) { if (Deleter.RegistryKeyExists(@"HKLM64\SYSTEM\CurrentControlSet\Services\" + s)) l.Add(new Leftover { Kind = LeftoverKind.Service, Path = s, Reason = "Edge service" }); }

            Dir(EdgeDir, "Edge program folder");
            Dir(Path.Combine(PF86, @"Microsoft\EdgeCore"), "Edge core files");
            Dir(Path.Combine(Local, @"Microsoft\Edge"), "Edge profile, cache and history");
            Dir(Path.Combine(Local, @"Microsoft\EdgeUpdate"), "Edge updater data");
            Svc("MicrosoftEdgeElevationService");
            if (alsoWebView)
            {
                // the updater is shared with WebView2, so it only goes when WebView2 goes too
                Dir(WebViewDir, "WebView2 runtime");
                Dir(Path.Combine(PF86, @"Microsoft\EdgeUpdate"), "Edge/WebView2 updater");
                Svc("edgeupdate"); Svc("edgeupdatem");
                var tasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\Tasks");
                try
                {
                    foreach (var t in Directory.GetFiles(tasks, "MicrosoftEdgeUpdate*"))
                        l.Add(new Leftover { Kind = LeftoverKind.Task, Path = "\\" + Path.GetFileName(t), Reason = "Edge updater task" });
                }
                catch { }
            }
            foreach (var x in l) { x.Confidence = Confidence.High; x.Risk = Risk.Normal; }
            return l;
        }

        // ---------------- OneDrive ----------------

        public static string OneDriveUninstaller()
        {
            var app = InstalledApps.Load().FirstOrDefault(a => a.Name.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                              (a.Publisher ?? "").IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0);
            if (app?.UninstallString != null) return app.UninstallString;
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var p in new[] { Path.Combine(win, @"System32\OneDriveSetup.exe"), Path.Combine(win, @"SysWOW64\OneDriveSetup.exe") })
                if (File.Exists(p)) return "\"" + p + "\" /uninstall";
            return null;
        }

        public static string RemoveOneDrive()
        {
            Kill("OneDrive", "OneDriveStandaloneUpdater", "FileCoAuth");
            var cmd = OneDriveUninstaller();
            if (cmd == null) return "OneDrive uninstaller not found (already removed?).";
            var exe = InstalledApps.ExePart(cmd);
            var args = cmd.Substring(cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) + exe.Length).Trim().Trim('"').Trim();
            if (args.IndexOf("/uninstall", StringComparison.OrdinalIgnoreCase) < 0) args += " /uninstall";
            int code = Deleter.Run(exe, args, 5 * 60 * 1000);
            // remove the "set up OneDrive" hook that runs for every new user account
            if (Elevation.IsAdmin)
            {
                var hive = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), @"Users\Default\NTUSER.DAT");
                if (Deleter.Run("reg.exe", $"load HKU\\ZilchDefault \"{hive}\"", 30000) == 0)
                {
                    Deleter.Run("reg.exe", @"delete HKU\ZilchDefault\Software\Microsoft\Windows\CurrentVersion\Run /v OneDriveSetup /f", 30000);
                    Deleter.Run("reg.exe", @"unload HKU\ZilchDefault", 30000);
                }
            }
            return $"OneDrive uninstaller exit code {code}.";
        }
    }
}
