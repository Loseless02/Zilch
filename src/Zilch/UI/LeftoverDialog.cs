using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Shows found traces with checkboxes. High-confidence ones start checked.</summary>
    public sealed class LeftoverDialog : Form
    {
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, CheckBoxes = true };
        readonly Label _total = new Label { AutoSize = true, Font = Theme.Bold, Padding = new Padding(0, 6, 0, 0) };

        public List<Leftover> Chosen { get; } = new List<Leftover>();

        public LeftoverDialog(string title, string intro, List<Leftover> items)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 560);
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(10);

            var head = new Label { Text = intro, Dock = DockStyle.Top, Height = 44 };
            _list.Columns.Add("Type", 90);
            _list.Columns.Add("Location", 520);
            _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Match", 70);
            _list.Columns.Add("Why", 170);
            _list.NumericColumns.Add(2);

            foreach (var l in items)
            {
                var it = new ListViewItem(l.Kind.ToString()) { Tag = l, Checked = l.Confidence == Confidence.High };
                it.SubItems.Add(l.Path);
                it.SubItems.Add(l.Kind == LeftoverKind.Registry || l.Kind == LeftoverKind.Task || l.Kind == LeftoverKind.Service
                    ? new ListViewItem.ListViewSubItem(it, "") { Tag = 0L } : FastListView.SizeCell(it, l.Size));
                it.SubItems.Add(l.Confidence == Confidence.High ? "sure" : "maybe");
                it.SubItems.Add(l.Reason);
                if (l.Confidence == Confidence.Medium) it.ForeColor = Theme.Warn;
                _list.Items.Add(it);
            }
            _list.ItemChecked += (s, e) => UpdateTotal();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show in Explorer", null, (s, e) => { if (_list.SelectedItems.Count > 0) Dialogs.ShowInExplorer(((Leftover)_list.SelectedItems[0].Tag).Path); });
            menu.Items.Add("Check all", null, (s, e) => { foreach (ListViewItem i in _list.Items) i.Checked = true; });
            menu.Items.Add("Uncheck all", null, (s, e) => { foreach (ListViewItem i in _list.Items) i.Checked = false; });
            _list.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var remove = new Button { Text = AppState.UseRecycleBin ? "Recycle checked" : "Delete checked", Width = 130, DialogResult = DialogResult.OK };
            var skip = new Button { Text = "Keep everything", Width = 130, DialogResult = DialogResult.Cancel };
            bottom.Controls.AddRange(new Control[] { remove, skip, _total });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(head);
            AcceptButton = remove; CancelButton = skip;

            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK)
                    Chosen.AddRange(_list.CheckedItems.Cast<ListViewItem>().Select(i => (Leftover)i.Tag));
            };
            UpdateTotal();
        }

        void UpdateTotal()
        {
            var c = _list.CheckedItems.Cast<ListViewItem>().Select(i => (Leftover)i.Tag).ToList();
            _total.Text = $"{c.Count} of {_list.Items.Count} checked, {Fmt.Size(c.Sum(x => x.Size))}   ";
        }
    }
}
