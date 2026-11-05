using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Installed programs: uninstall, then hunt down everything they left behind.</summary>
    public sealed class AppsTab : UserControl
    {
        readonly TextBox _search = new TextBox { Width = 260 };
        readonly Button _refresh = new Button { Text = "Refresh", Width = 80 };
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, MultiSelect = false };
        readonly Button _uninstall = new Button { Text = "Uninstall + wipe traces", Width = 170, Font = Theme.Bold };
        readonly Button _force = new Button { Text = "Force remove (skip uninstaller)", Width = 210 };
        readonly Button _leftovers = new Button { Text = "Only find leftovers", Width = 140 };
        readonly Button _open = new Button { Text = "Open folder", Width = 100 };
        readonly CheckBox _quiet = new CheckBox { Text = "Silent uninstall", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 6, 0, 0) };

        List<InstalledApp> _apps = new List<InstalledApp>();

        public AppsTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.AddRange(new Control[] { new Label { Text = "Search:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _search, _refresh, _status });

            _list.Columns.Add("Name", 330);
            _list.Columns.Add("Publisher", 200);
            _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Version", 110);
            _list.Columns.Add("Installed", 80);
            _list.Columns.Add("For", 90);
            _list.NumericColumns.Add(2);
            _list.DoubleClick += (s, e) => Uninstall(false);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _uninstall, _force, _leftovers, _quiet, _open });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(top);

            _search.TextChanged += (s, e) => Fill();
            _refresh.Click += (s, e) => LoadApps();
            _uninstall.Click += (s, e) => Uninstall(false);
            _force.Click += (s, e) => Uninstall(true);
            _leftovers.Click += (s, e) => FindOnly();
            _open.Click += (s, e) =>
            {
                var a = Sel(); var d = a == null ? null : InstalledApps.GuessFolder(a);
                if (d != null) System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\"");
            };
            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)(() => { ApplyMode(); Fill(); })); };
            ApplyMode();
            HandleCreated += (s, e) => LoadApps();
        }

        void ApplyMode()
        {
            _force.Visible = _leftovers.Visible = _quiet.Visible = AppState.IsAdvancedOrMore;
        }

        async void LoadApps()
        {
            _status.Text = "Loading…";
            _apps = await Task.Run(() => InstalledApps.Load());
            Fill();
            _status.Text = $"{_apps.Count} programs. Measuring sizes…";
            // measure real sizes in the background, biggest folders show up as they finish
            var apps = _apps;
            await Task.Run(() => Parallel.ForEach(apps, new ParallelOptions { MaxDegreeOfParallelism = 4 }, a =>
            {
                var d = InstalledApps.GuessFolder(a);
                if (d != null && !Guard.IsSharedContainer(d)) a.MeasuredBytes = Scanner.SizeOf(d);
            }));
            if (apps != _apps) return;
            Fill();
            _status.Text = $"{_apps.Count} programs, {Fmt.Size(_apps.Sum(a => a.BestSize))} total.";
        }

        void Fill()
        {
            var q = _search.Text.Trim();
            var keepSel = Sel();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var a in _apps)
            {
                if (a.SystemComponent && !AppState.IsAdvancedOrMore) continue;
                if (q.Length > 0 && a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (a.Publisher ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var it = new ListViewItem(a.Name) { Tag = a };
                it.SubItems.Add(a.Publisher);
                it.SubItems.Add(a.BestSize > 0 ? FastListView.SizeCell(it, a.BestSize) : new ListViewItem.ListViewSubItem(it, "") { Tag = 0L });
                it.SubItems.Add(a.Version);
                it.SubItems.Add(a.InstallDate.Length == 8 ? $"{a.InstallDate.Substring(0, 4)}-{a.InstallDate.Substring(4, 2)}-{a.InstallDate.Substring(6, 2)}" : a.InstallDate);
                it.SubItems.Add(a.Scope);
                if (a.SystemComponent) it.ForeColor = Theme.Muted;
                _list.Items.Add(it);
                if (a == keepSel) it.Selected = true;
            }
            _list.EndUpdate();
        }

        InstalledApp Sel() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as InstalledApp : null;

        async void Uninstall(bool force)
        {
            var a = Sel();
            if (a == null) { Dialogs.Info(this, "Pick a program first."); return; }
            if (a.RegistryKey.StartsWith("HKLM") && !Elevation.IsAdmin &&
                MessageBox.Show(this, "This program is installed for all users. Its uninstaller will ask for admin rights, " +
                    "and some leftovers (Program Files, ProgramData, services) can only be removed as admin.\n\nContinue anyway?",
                    "Zilch", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

            var folder = InstalledApps.GuessFolder(a);
            var verb = force ? "Force remove" : "Uninstall";
            var msg = force
                ? $"Force remove {a.Name}?\n\nThe program's own uninstaller will NOT run. Zilch deletes its files, shortcuts and registry entries directly. " +
                  "Use this when the normal uninstaller is broken or missing."
                : $"Uninstall {a.Name}?\n\nIts own uninstaller runs first. Afterwards Zilch searches for every trace it left and shows you the list.";
            if (MessageBox.Show(this, msg, verb, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SetBusy(true, force ? "Removing…" : $"Waiting for {a.Name}'s uninstaller to finish…");
            int? code = null;
            if (!force)
            {
                var mode = _quiet.Checked && AppState.IsAdvancedOrMore ? InstalledApps.UninstallMode.Quiet : InstalledApps.UninstallMode.Normal;
                code = await Task.Run(() => InstalledApps.RunUninstaller(a, mode));
                History.Add("Uninstaller ran", $"{a.Name}: exit code {(code?.ToString() ?? "none (cancelled or missing)")}");
                // Still registered = the user cancelled it or it failed. Don't rip files out of a working program by surprise.
                if ((code == null || Deleter.RegistryKeyExists(a.RegistryKey)) &&
                    MessageBox.Show(this, $"{a.Name} still looks installed (the uninstaller was cancelled or failed).\n\n" +
                        "Search for its files anyway? Deleting them will break the program.", "Zilch",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    SetBusy(false, "");
                    return;
                }
            }
            else History.Add("Force remove", a.Name + " (uninstaller skipped)");

            await HuntAndRemove(a, folder, $"{a.Name}: leftovers");
            SetBusy(false, "");
            LoadApps();
        }

        async void FindOnly()
        {
            var a = Sel();
            if (a == null) { Dialogs.Info(this, "Pick a program first."); return; }
            SetBusy(true, "Searching…");
            await HuntAndRemove(a, InstalledApps.GuessFolder(a), $"{a.Name}: traces (program NOT uninstalled)");
            SetBusy(false, "");
            LoadApps();
        }

        async Task HuntAndRemove(InstalledApp a, string folder, string title)
        {
            SetBusy(true, $"Searching for traces of {a.Name}…");
            bool medium = AppState.IsAdvancedOrMore;
            var others = _apps.Where(x => x != a).Select(x => InstalledApps.GuessFolder(x)).Where(d => d != null).Select(Guard.Norm).ToList();
            var found = await Task.Run(() => new LeftoverFinder(a.Name, a.Publisher, folder, a.RegistryKey) { OtherInstallDirs = others }.Find(medium));
            SetBusy(false, "");
            if (found.Count == 0) { Dialogs.Info(this, $"No traces of {a.Name} found. Clean."); return; }

            using (var dlg = new LeftoverDialog(title,
                $"Found {found.Count} trace(s). Checked = sure it belongs to {a.Name}. " +
                (medium ? "Orange = similar name, check before deleting." : "Switch to Advanced to also see similar-name matches."), found))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Chosen.Count == 0) return;
                var chosen = dlg.Chosen;
                bool recycle = AppState.UseRecycleBin, force = AppState.IsSudo;
                SetBusy(true, "Removing traces…");
                var r = await Task.Run(() => LeftoverFinder.Remove(chosen, recycle, force));
                SetBusy(false, "");
                History.Add("Removed leftovers", r, chosen.Select(c => $"[{c.Kind}] {c.Path}"));
                MessageBox.Show(this, r.Summary(), a.Name, MessageBoxButtons.OK, r.Failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        void SetBusy(bool busy, string text)
        {
            UseWaitCursor = busy;
            _uninstall.Enabled = _force.Enabled = _leftovers.Enabled = _list.Enabled = !busy;
            _status.Text = text;
        }
    }
}
