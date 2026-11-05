using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace Zilch.Core
{
    public static class Elevation
    {
        public static readonly bool IsAdmin =
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>Relaunch Zilch elevated. Returns false if the user declined the UAC prompt.</summary>
        public static bool RestartAsAdmin(string args = "")
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Application.Exit();
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // UAC cancelled
            }
        }

        /// <summary>Shows a prompt and relaunches elevated. Returns true if we are (or will be) admin.</summary>
        public static bool Require(IWin32Window owner, string why, string args = "")
        {
            if (IsAdmin) return true;
            var r = MessageBox.Show(owner,
                why + "\n\nRestart Zilch as administrator?",
                "Admin needed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) RestartAsAdmin(args);
            return false;
        }
    }
}
