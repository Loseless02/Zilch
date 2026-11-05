using System;
using System.Collections.Generic;
using System.IO;

namespace Zilch.Core
{
    public enum Risk
    {
        /// <summary>Ordinary user data or app data.</summary>
        Normal,
        /// <summary>Removing it can break an app or lose settings, e.g. a whole Program Files subfolder.</summary>
        Caution,
        /// <summary>Windows itself or a folder everything shares. Only Sudo mode may delete it.</summary>
        Protected,
    }

    /// <summary>Decides how dangerous it is to delete a path.</summary>
    public static class Guard
    {
        static readonly string Win = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        static readonly string SysDrive = Norm(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        static readonly string Profile = Norm(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        static readonly string Self = Norm(System.Windows.Forms.Application.ExecutablePath);

        // Exact folders that must never go in one piece (their *contents* may still be fine).
        static readonly HashSet<string> ProtectedExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Everything below these is protected.
        static readonly List<string> ProtectedTrees = new List<string>();
        // Exact folders that are shared containers: deleting them whole is reckless but not fatal.
        static readonly HashSet<string> CautionExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static Guard()
        {
            string Env(string v) => Environment.GetEnvironmentVariable(v) ?? "";
            string Sf(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

            foreach (var p in new[]
            {
                Win,
                Path.Combine(SysDrive, "Boot"),
                Path.Combine(SysDrive, "Recovery"),
                Path.Combine(SysDrive, "System Volume Information"),
                Path.Combine(SysDrive, "$Recycle.Bin"),
                Path.Combine(SysDrive, "$WinREAgent"),
                Path.Combine(Env("ProgramFiles"), "WindowsApps"),
                Path.Combine(Env("ProgramFiles"), "Windows Defender"),
                Path.Combine(Env("ProgramFiles"), "Common Files", "microsoft shared"),
                Path.Combine(Env("ProgramData"), "Microsoft", "Windows"),
                Path.Combine(Env("ProgramData"), "Microsoft", "Windows Defender"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows"),
                Path.Combine(Sf(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows"),
            })
                if (!string.IsNullOrEmpty(p)) ProtectedTrees.Add(Norm(p));

            foreach (var p in new[]
            {
                SysDrive,
                Path.Combine(SysDrive, "Users"),
                Path.Combine(SysDrive, "pagefile.sys"),
                Path.Combine(SysDrive, "hiberfil.sys"),
                Path.Combine(SysDrive, "swapfile.sys"),
                Path.Combine(SysDrive, "bootmgr"),
                Path.Combine(SysDrive, "BOOTNXT"),
                Env("ProgramFiles"), Env("ProgramFiles(x86)"), Env("ProgramData"), Env("ProgramW6432"),
                Profile,
                Env("PUBLIC"),
                Sf(Environment.SpecialFolder.ApplicationData),
                Sf(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Profile, "AppData"),
                Path.Combine(Profile, "AppData", "LocalLow"),
                Self,
            })
                if (!string.IsNullOrEmpty(p)) ProtectedExact.Add(Norm(p));

            foreach (var p in new[]
            {
                Sf(Environment.SpecialFolder.MyDocuments), Sf(Environment.SpecialFolder.Desktop),
                Path.Combine(Profile, "Downloads"), Sf(Environment.SpecialFolder.MyPictures),
                Sf(Environment.SpecialFolder.MyMusic), Sf(Environment.SpecialFolder.MyVideos),
                Path.Combine(Env("ProgramFiles"), "Common Files"), Path.Combine(Env("ProgramFiles(x86)"), "Common Files"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
                Path.Combine(Sf(Environment.SpecialFolder.ApplicationData), "Microsoft"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "Packages"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                Path.Combine(Env("ProgramData"), "Microsoft"),
                Path.Combine(Env("ProgramFiles"), "Microsoft"),
                Path.Combine(Env("ProgramFiles(x86)"), "Microsoft"),
                Path.Combine(Env("ProgramFiles"), "dotnet"),
                // the user's synced files, never "leftovers" of the OneDrive app
                Env("OneDrive"), Env("OneDriveConsumer"), Env("OneDriveCommercial"),
                // Zilch's own history folder and the folder it runs from
                History.Folder, Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath),
            })
                if (!string.IsNullOrEmpty(p)) CautionExact.Add(Norm(p));
        }

        public static string Norm(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { }
            if (p.Length > 3) p = p.TrimEnd('\\');
            return p;
        }

        static bool Under(string path, string root) =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
             (root.EndsWith("\\") || path.Length > root.Length && path[root.Length] == '\\'));

        public static Risk Classify(string path)
        {
            var p = Norm(path);
            if (p.Length <= 3) return Risk.Protected; // any drive root
            if (ProtectedExact.Contains(p)) return Risk.Protected;
            foreach (var t in ProtectedTrees) if (Under(p, t)) return Risk.Protected;
            // Any other user's profile folder itself
            var users = Norm(Path.Combine(SysDrive, "Users"));
            var parent = Norm(Path.GetDirectoryName(p));
            if (parent.Equals(users, StringComparison.OrdinalIgnoreCase)) return Risk.Protected;
            if (CautionExact.Contains(p)) return Risk.Caution;
            // A top-level Program Files folder = a whole installed program
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                if (!string.IsNullOrEmpty(pf) && parent.Equals(Norm(pf), StringComparison.OrdinalIgnoreCase)) return Risk.Caution;
            return Risk.Normal;
        }

        /// <summary>True if this mode may delete an item with this risk.</summary>
        public static bool Allowed(Risk r) =>
            r == Risk.Normal || (r == Risk.Caution && AppState.IsAdvancedOrMore) || AppState.IsSudo;

        /// <summary>Folders that must never be treated as one app's leftovers, even if the name matches.</summary>
        public static bool IsSharedContainer(string path)
        {
            var p = Norm(path);
            return Classify(p) == Risk.Protected || CautionExact.Contains(p);
        }
    }
}
