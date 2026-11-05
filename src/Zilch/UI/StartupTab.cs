using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>What starts with Windows, and a switch for each.</summary>
    public sealed class StartupTab : UserControl
    {
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill };
        readonly Button _enable = new Button { Text = "Enable", Width = 80 };
        readonly Button _disable = new Button { Text = "Disable", Width = 80, Font = Theme.Bold };
        readonly Button _remove = new Button { Text = "Remove entry", Width = 110 };
        readonly Button _explore = new Button { Text = "Show in Explorer", Width = 130 };
        readonly Button _refresh = new Button { Text = "Refresh", Width = 80 };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 6, 0, 0) };
        List<StartupItem> _items = new List<StartupItem>();
        bool _loading;

        public StartupTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            var head = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 0, 0, 6),
                Text = "Programs that start when you sign in. Disable = it stays installed but won't auto-start (same switch Task Manager uses). " +
                       "Advanced also shows scheduled tasks and services that start with Windows.",
            };
            _list.Columns.Add("Name", 230);
            _list.Columns.Add("Status", 90);
            _list.Columns.Add("Running", 70);
            _list.Columns.Add("Publisher", 180);
            _list.Columns.Add("Where", 170);
            _list.Columns.Add("Command", 480);
            _list.DoubleClick += (s, e) => Toggle(null);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _disable, _enable, _remove, _explore, _refresh, _status });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(head);

            _enable.Click += (s, e) => Toggle(true);
            _disable.Click += (s, e) => Toggle(false);
            _remove.Click += (s, e) => Remove();
            _explore.Click += (s, e) => { var it = Sel().FirstOrDefault(); if (it?.Exe != null) Dialogs.ShowInExplorer(it.Exe); };
            _refresh.Click += (s, e) => Reload();
            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Reload); };
            VisibleChanged += (s, e) => { if (Visible && _items.Count == 0) Reload(); };
        }

        async void Reload()
        {
            if (_loading) return;
            _loading = true;
            _status.Text = "Loading…";
            bool adv = AppState.IsAdvancedOrMore, sudo = AppState.IsSudo;
            try { _items = await Task.Run(() => Startup.Load(adv, sudo)); }
            catch (Exception ex) { Crash.Log(ex, "Startup"); }
            finally { _loading = false; }
            Fill();
        }

        void Fill()
        {
            var keep = new HashSet<string>(Sel().Select(i => i.Location + "|" + i.ValueName));
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var i in _items.OrderByDescending(i => i.Enabled).ThenBy(i => i.Source).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var it = new ListViewItem(i.Name) { Tag = i };
                it.SubItems.Add(i.Enabled ? "Enabled" : "Disabled");
                it.SubItems.Add(i.Running ? "yes" : "");
                it.SubItems.Add(i.Publisher ?? (i.Source == StartupSource.StoreApp ? "Store" : "unknown"));
                it.SubItems.Add(i.SourceText);
                it.SubItems.Add(i.Command);
                if (!i.Enabled) it.ForeColor = Theme.Muted;
                else if (string.IsNullOrEmpty(i.Publisher) && i.Source != StartupSource.StoreApp) it.ForeColor = Color.FromArgb(200, 90, 0);
                else it.ForeColor = Theme.Good;
                if (keep.Contains(i.Location + "|" + i.ValueName)) it.Selected = true;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            int on = _items.Count(i => i.Enabled);
            _status.Text = $"{on} of {_items.Count} start with Windows. Green = enabled, orange = no known publisher, gray = disabled.   ";
        }

        IEnumerable<StartupItem> Sel() => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as StartupItem).Where(i => i != null);

        bool AdminOk(List<StartupItem> sel) =>
            !sel.Any(i => i.NeedsAdmin) || Elevation.IsAdmin || Elevation.Require(this, "Changing startup entries for all users, tasks and services needs admin rights.");

        void Toggle(bool? on)
        {
            var sel = Sel().ToList();
            if (sel.Count == 0) return;
            if (!AdminOk(sel)) return;
            var errors = new List<string>();
            foreach (var i in sel)
            {
                bool want = on ?? !i.Enabled;
                if (i.Enabled == want) continue;
                var err = Startup.SetEnabled(i, want);
                if (err != null) errors.Add($"{i.Name}: {err}");
                else History.Add(want ? "Startup enabled" : "Startup disabled", i.Name, new[] { i.SourceText + ": " + i.Command });
            }
            if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Zilch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Fill();
        }

        void Remove()
        {
            var sel = Sel().ToList();
            if (sel.Count == 0) return;
            var cant = sel.Where(i => !i.CanRemove).ToList();
            if (cant.Count == sel.Count) { Dialogs.Info(this, "Store apps and services can only be disabled here. Uninstall them in the Apps or Windows Bloat tab."); return; }
            sel = sel.Where(i => i.CanRemove).ToList();
            if (!AdminOk(sel)) return;
            if (MessageBox.Show(this, "Remove these startup entries for good? The programs stay installed, they just stop auto-starting " +
                    "and can't be re-enabled here.\n\n" + string.Join("\n", sel.Select(i => "• " + i.Name)),
                    "Zilch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var errors = new List<string>();
            foreach (var i in sel)
            {
                var err = Startup.Remove(i);
                if (err != null) errors.Add($"{i.Name}: {err}");
                else History.Add("Startup entry removed", i.Name, new[] { i.SourceText + ": " + i.Command });
            }
            if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Zilch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Reload();
        }
    }
}
