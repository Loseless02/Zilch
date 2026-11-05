using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Microsoft Edge, OneDrive and the apps Windows preinstalls on first launch.</summary>
    public sealed class BloatTab : UserControl, IRefreshable
    {
        readonly Label _edgeState = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        readonly Button _edge = new Button { Text = "Remove Microsoft Edge", Width = 180, Font = Theme.Bold };
        readonly CheckBox _webview = new CheckBox { Text = "Also remove WebView2 (breaks Widgets, Teams, new Outlook and many apps)", AutoSize = true, ForeColor = Theme.Danger, Padding = new Padding(0, 4, 0, 0) };
        readonly Label _odState = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        readonly Button _onedrive = new Button { Text = "Remove OneDrive", Width = 180 };
        readonly CheckBox _block = new CheckBox { Text = "Stop Windows reinstalling bloat, ads and Copilot", Checked = true, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        readonly Button _restore = new Button { Text = "Create restore point", Width = 150 };
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, CheckBoxes = true, ShowItemToolTips = true };
        readonly Button _load = new Button { Text = "Load apps", Width = 100 };
        readonly Button _track = new Button { Text = "Track (what is this?)", Width = 150 };
        readonly Button _remove = new Button { Text = "Remove checked apps", Width = 160, Font = Theme.Bold };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 6, 0, 0) };

        List<AppxApp> _apps = new List<AppxApp>();

        public BloatTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            FlowLayoutPanel Row(params Control[] c) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill }; f.Controls.AddRange(c); return f; }
            top.Controls.Add(Row(_edge, _edgeState));
            top.Controls.Add(Row(new Label { Width = 180 }, _webview));
            top.Controls.Add(Row(_onedrive, _odState));
            top.Controls.Add(Row(_restore, _block));
            top.Controls.Add(new Label
            {
                Text = "Preinstalled apps below. Checked = known bloat. Removed for every user, and Windows is told not to bring them back on updates.",
                AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 8, 0, 4),
            });

            _list.Columns.Add("App", 230);
            _list.Columns.Add("Should you remove it?", 180);
            _list.Columns.Add("What it is", 420);
            _list.Columns.Add("Package", 300);
            _list.Columns.Add("Kind", 80);
            _list.Columns.Add("Note", 240);
            _list.DoubleClick += (s, e) => Track();

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _remove, _load, _track, _status });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(top);

            _edge.Click += (s, e) => RemoveEdge();
            _onedrive.Click += (s, e) => RemoveOneDrive();
            _restore.Click += (s, e) =>
            {
                if (!Elevation.Require(this, "Restore points need admin rights.")) return;
                bool ok = Busy.Run(this, "Creating restore point…", f => Debloat.CreateRestorePoint());
                _status.Text = ok ? "Restore point created." : "Could not create a restore point (System Protection off?).";
                History.Add("Restore point", ok ? "created" : "failed");
            };
            _load.Click += (s, e) => LoadApps();
            _remove.Click += (s, e) => RemoveApps();
            _track.Click += (s, e) => Track();
            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Fill); };
            RefreshState();
        }

        public void RefreshNow() { RefreshState(); LoadApps(); }

        void Track()
        {
            var a = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as AppxApp : null;
            if (a == null) { Dialogs.Info(this, "Select an app first."); return; }
            TrackDialog.Open(this, a.Friendly, () => Explain.ForAppx(a),
                string.IsNullOrEmpty(a.InstallLocation) ? (Action)null : () => Nav.OpenPath(a.InstallLocation));
        }

        void RefreshState()
        {
            _edgeState.Text = Debloat.EdgeInstalled ? "Edge is installed." : "Edge is not installed.";
            _edgeState.ForeColor = Debloat.EdgeInstalled ? SystemColors.ControlText : Theme.Muted;
            var od = Debloat.OneDriveUninstaller();
            _odState.Text = od != null ? "OneDrive is installed. Your OneDrive files folder is kept." : "OneDrive is not installed.";
            _odState.ForeColor = od != null ? SystemColors.ControlText : Theme.Muted;
        }

        async void LoadApps()
        {
            _load.Enabled = false; _status.Text = Elevation.IsAdmin ? "Loading apps for all users…" : "Loading apps (your account only, not admin)…";
            _apps = await Task.Run(() => Debloat.LoadAppx());
            _load.Enabled = true; _load.Text = "Refresh";
            Fill();
        }

        void Fill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            int hidden = 0;
            foreach (var a in _apps)
            {
                bool show = a.Category == AppxCategory.Bloat ||
                            (a.Category == AppxCategory.Optional && AppState.IsAdvancedOrMore) ||
                            (a.Category == AppxCategory.System && AppState.IsSudo);
                if (!show) { hidden++; continue; }
                var it = new ListViewItem(a.Friendly) { Tag = a, Checked = a.Category == AppxCategory.Bloat, UseItemStyleForSubItems = false };
                var (what, verdict, why) = Explain.Summarize(a);
                var vcell = it.SubItems.Add(Explanation.VerdictLabel(verdict));
                vcell.ForeColor = TrackDialog.ColorOf(verdict);
                vcell.Font = Theme.Bold;
                it.SubItems.Add(what);
                it.ToolTipText = what + "\n\n" + Explanation.VerdictLabel(verdict) + ": " + why + "\n\nTrack for the full story.";
                it.SubItems.Add(a.Name);
                it.SubItems.Add(a.Category.ToString());
                var notes = new List<string>();
                if (a.Category == AppxCategory.System) notes.Add("Windows needs this. May break things.");
                if (a.NonRemovable) notes.Add("marked non-removable");
                if (a.IsFramework) notes.Add("framework other apps use");
                if (a.Provisioned) notes.Add("installs for new users");
                it.SubItems.Add(string.Join(", ", notes));
                var fc = a.Category == AppxCategory.System ? Theme.Danger : SystemColors.ControlText;
                foreach (ListViewItem.ListViewSubItem sub in it.SubItems) if (sub != vcell) sub.ForeColor = fc;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            if (_apps.Count > 0)
                _status.Text = $"{_list.Items.Count} shown" + (hidden > 0 ? $", {hidden} hidden ({(AppState.IsSudo ? "" : AppState.IsAdvancedOrMore ? "system apps: Sudo only" : "switch to Advanced to see optional apps")})" : "") + "   ";
            else _status.Text = "Press Load apps.   ";
        }

        void RemoveApps()
        {
            var sel = _list.CheckedItems.Cast<ListViewItem>().Select(i => (AppxApp)i.Tag).ToList();
            if (sel.Count == 0) return;
            if (!Elevation.IsAdmin &&
                MessageBox.Show(this, "Without admin rights Zilch can only remove these for your account, and Windows may reinstall them.\n\nRestart as admin? (No = continue for my account only)",
                    "Zilch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { Elevation.RestartAsAdmin(); return; }

            bool sys = sel.Any(a => a.Category == AppxCategory.System);
            var names = sel.Select(a => a.Friendly).ToList();
            if (sys)
            {
                if (!Dialogs.TypeToConfirm(this, "Remove Windows components",
                    "You checked apps Windows itself depends on (Store, Start menu, sign-in, frameworks...).\n" +
                    "Removing them can break Start, Settings, the Store or sign-in, and Windows may not repair itself.\n" +
                    "Create a restore point first.\n\n" + string.Join("\n", sel.Where(a => a.Category == AppxCategory.System).Select(a => a.Name)), "REMOVE")) return;
            }
            else if (MessageBox.Show(this, $"Remove {sel.Count} app(s)?\n\n" + string.Join(", ", names), "Zilch",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            bool force = AppState.IsSudo, block = _block.Checked;
            var res = Busy.Run(this, "Removing Windows apps…", bf =>
            {
                int okc = 0; var fl = new List<string>();
                for (int i = 0; i < sel.Count; i++)
                {
                    bf.Report($"{sel[i].Friendly} ({i + 1}/{sel.Count})", (double)i / sel.Count);
                    var (o, f) = Debloat.RemoveAppx(new[] { sel[i] }, force);
                    okc += o; fl.AddRange(f);
                }
                if (block) { bf.Report("Telling Windows not to bring them back…"); Debloat.BlockReinstall(); }
                return Tuple.Create(okc, fl);
            });
            if (res == null) { LoadApps(); return; }
            int ok = res.Item1; var failed = res.Item2;
            History.Add("Removed Windows apps", $"{ok} removed, {failed.Count} failed" + (block ? ", reinstall blocked" : ""),
                sel.Select(a => "- " + a.Name).Concat(failed.Select(f => "! failed: " + f)));
            MessageBox.Show(this, $"Removed {ok} app(s)." + (failed.Count > 0 ? "\n\nCould not remove:\n" + string.Join("\n", failed) +
                (force ? "" : "\n\nSudo mode can force system apps out.") : ""), "Zilch");
            LoadApps();
        }

        void RemoveEdge()
        {
            if (!Debloat.EdgeInstalled && !Debloat.WebViewInstalled) { Dialogs.Info(this, "Edge is already gone."); return; }
            if (!Elevation.Require(this, "Removing Edge needs admin rights.")) return;
            bool wv = _webview.Checked;
            var text = "Remove Microsoft Edge?\n\n" +
                       "• Runs Edge's own uninstaller (forced, all users)\n" +
                       "• Removes its leftover folders, shortcuts and services\n" +
                       "• Tells Windows Update not to reinstall it\n" +
                       (wv ? "• ALSO removes WebView2. Widgets, Teams, new Outlook and many other apps will stop working.\n" : "• Keeps WebView2 (other apps need it)\n") +
                       "\nInstall another browser first. Edge saved passwords and history are deleted.";
            if (MessageBox.Show(this, text, "Remove Edge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            bool force = AppState.IsSudo;
            var edgeRes = Busy.Run(this, "Removing Microsoft Edge…", f => { var t = Debloat.RemoveEdge(wv, force); return Tuple.Create(t.Item1, t.Item2); });
            if (edgeRes == null) return;
            bool removed = edgeRes.Item1; string log = edgeRes.Item2;
            History.Add("Remove Edge", removed ? "Edge removed" : "Edge uninstall incomplete", log.Split('\n').Select(l => l.TrimEnd('\r')));

            var left = Busy.Run(this, "Looking for Edge leftovers…", bf =>
            {
                var l = Debloat.EdgeLeftovers(wv);
                var finder = new LeftoverFinder("Microsoft Edge", "Microsoft Corporation", Debloat.EdgeDir, null).Find(false);
                foreach (var f in finder)
                {
                    // the generic finder matches "Edge" loosely; keep WebView2 and the shared updater unless asked
                    if (!wv && (f.Path.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) >= 0 || f.Path.IndexOf("EdgeUpdate", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (!l.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase))) l.Add(f);
                }
                return l;
            });
            ReviewAndRemove("Edge leftovers", left ?? new List<Leftover>());
            MessageBox.Show(this, log, "Edge", MessageBoxButtons.OK, removed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshState();
        }

        void RemoveOneDrive()
        {
            if (Debloat.OneDriveUninstaller() == null) { Dialogs.Info(this, "OneDrive is already gone."); return; }
            if (MessageBox.Show(this, "Remove OneDrive?\n\nFiles that are only in the cloud stay in the cloud. Your local OneDrive folder is NOT deleted.",
                "Remove OneDrive", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var msg = Busy.Run(this, "Removing OneDrive…", f => Debloat.RemoveOneDrive()) ?? "failed";
            var left = Busy.Run(this, "Looking for OneDrive leftovers…", f => new LeftoverFinder("Microsoft OneDrive", "Microsoft Corporation", null, null).Find(false));
            History.Add("Remove OneDrive", msg);
            ReviewAndRemove("OneDrive leftovers", left ?? new List<Leftover>());
            RefreshState();
        }

        void ReviewAndRemove(string title, List<Leftover> items)
        {
            if (items.Count == 0) return;
            using (var dlg = new LeftoverDialog(title, "Traces found. Uncheck anything you want to keep.", items))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Chosen.Count == 0) return;
                var chosen = dlg.Chosen;
                bool recycle = AppState.UseRecycleBin, force = AppState.IsSudo;
                var r = Busy.Run(this, "Removing " + title.ToLowerInvariant() + "…", f => LeftoverFinder.Remove(chosen, recycle, force));
                if (r == null) return;
                History.Add(title, r, chosen.Select(c => $"[{c.Kind}] {c.Path}"));
                MessageBox.Show(this, r.Summary(), title);
            }
        }
    }
}
