using System;
using System.Diagnostics;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Everything Zilch did, newest first.</summary>
    public sealed class LogsTab : UserControl
    {
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, MultiSelect = false };
        readonly TextBox _details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new System.Drawing.Font("Consolas", 9f) };

        public LogsTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            _list.Columns.Add("When", 140);
            _list.Columns.Add("Action", 170);
            _list.Columns.Add("Result", 520);
            _list.SelectedIndexChanged += (s, e) =>
            {
                var h = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as HistoryEntry : null;
                _details.Text = h == null ? "" : $"{h.Time:yyyy-MM-dd HH:mm:ss}  {h.Action}\r\n{h.Summary}\r\n\r\n{h.Details.Replace("\n", "\r\n")}";
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(_details);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var clear = new Button { Text = "Clear history", Width = 110 };
            var open = new Button { Text = "Open log file", Width = 110 };
            var refresh = new Button { Text = "Refresh", Width = 80 };
            bottom.Controls.AddRange(new Control[] { clear, open, refresh,
                new Label { Text = "Saved in " + History.FilePath, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 6, 0, 0) } });

            Controls.Add(split);
            Controls.Add(bottom);

            refresh.Click += (s, e) => Reload();
            open.Click += (s, e) => { if (System.IO.File.Exists(History.FilePath)) Process.Start("notepad.exe", "\"" + History.FilePath + "\""); };
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Delete the whole history? Zilch keeps no other records.", "Zilch", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                History.Clear();
                Reload();
            };
            History.Added += e => { if (IsHandleCreated) BeginInvoke((Action)Reload); };
            HandleCreated += (s, e) => Reload();
        }

        void Reload()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var h in History.Load())
            {
                var it = new ListViewItem(h.Time.ToString("yyyy-MM-dd HH:mm")) { Tag = h };
                it.SubItems.Add(h.Action);
                it.SubItems.Add(h.Summary);
                if (h.Summary.Contains("failed") || h.Action.StartsWith("Force") || h.Action.StartsWith("Sudo")) it.ForeColor = Theme.Danger;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            _details.Text = _list.Items.Count == 0 ? "Nothing yet. Every delete, uninstall and cleanup shows up here." : "";
        }
    }
}
