using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Everything in one category of the disk bar (all Games, all Videos...), biggest first.</summary>
    public sealed class InspectorTab : UserControl
    {
        readonly Panel _swatch = new Panel { Width = 18, Height = 18, Margin = new Padding(0, 6, 6, 0) };
        readonly Label _title = new Label { AutoSize = true, UseMnemonic = false, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.Size * 1.3f, FontStyle.Bold), Padding = new Padding(0, 2, 0, 0) };
        readonly Label _info = new Label { AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(8, 7, 0, 0) };
        readonly TextBox _filter = new TextBox { Width = 200 };
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, OwnerDraw = true };
        readonly Button _delete = new Button { Text = "Delete selected", Width = 130 };
        readonly Button _empty = new Button { Text = "Free up this folder", Width = 140 };
        readonly Button _selectAll = new Button { Text = "Select all", Width = 90 };
        readonly Button _zilch = new Button { Text = "Open in Zilch", Width = 110 };
        readonly Button _explore = new Button { Text = "Show in Explorer", Width = 130 };

        readonly SpaceTab _space;
        List<SpaceItem> _items = new List<SpaceItem>();
        Cat _cat;
        long _total;

        public InspectorTab(SpaceTab space)
        {
            _space = space;
            Dock = DockStyle.Fill;
            Padding = new Padding(8);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.AddRange(new Control[] { _swatch, _title, _info });
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            tools.Controls.AddRange(new Control[] { new Label { Text = "Filter:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _filter });

            _list.Columns.Add("Name", 560);
            _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Share", 160);
            _list.Columns.Add("Files", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Kind", 130);
            _list.Columns.Add("Note", 240);
            _list.NumericColumns.UnionWith(new[] { 1, 2, 3 });
            _list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _list.DrawItem += (s, e) => { };
            _list.DrawSubItem += (s, e) => SpaceTab.DrawCell(_list, e);
            _list.DoubleClick += (s, e) => OpenInZilch();
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) Delete(false); else if (e.KeyCode == Keys.Enter) OpenInZilch(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open in Zilch", null, (s, e) => OpenInZilch());
            menu.Items.Add("Show in Explorer", null, (s, e) => Explore());
            menu.Items.Add("Copy path", null, (s, e) => { var r = Sel().FirstOrDefault(); if (r != null) try { Clipboard.SetText(r.Path); } catch { } });
            menu.Items.Add("Select all", null, (s, e) => _list.SelectAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Free up this folder", null, (s, e) => Delete(true));
            menu.Items.Add("Delete", null, (s, e) => Delete(false));
            _list.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _delete, _empty, _selectAll, _explore, _zilch });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(tools);
            Controls.Add(top);

            _filter.TextChanged += (s, e) => Fill();
            _delete.Click += (s, e) => Delete(false);
            _empty.Click += (s, e) => Delete(true);
            _selectAll.Click += (s, e) => { _list.Focus(); _list.SelectAll(); };
            _zilch.Click += (s, e) => OpenInZilch();
            _explore.Click += (s, e) => Explore();
            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Fill); };

            _title.Text = "Inspector";
            _info.Text = "Scan a drive in the Space tab, then click a color in the bar to list everything in that category.";
            _swatch.BackColor = SystemColors.Control;
        }

        public void Show(Cat c, List<SpaceItem> items, long total)
        {
            _cat = c; _items = items; _total = total;
            _swatch.BackColor = Theme.Of(c);
            _title.Text = Categories.Name(c);
            _filter.Text = "";
            Fill();
        }

        void Fill()
        {
            if (_items.Count == 0 && _title.Text == "Inspector") return;
            var q = _filter.Text.Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.ListViewItemSorter = null;
            int hidden = 0;
            foreach (var r in _items)
            {
                if (q.Length > 0 && r.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool h = (r.Attr & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0 || (r.Node != null && r.Node.Hidden);
                if (h && !AppState.IsAdvancedOrMore) { hidden++; continue; }
                r.Risk = Guard.Classify(r.Path);
                _list.Items.Add(SpaceTab.MakeRow(r, false, _total, h));
            }
            _list.EndUpdate();
            _info.Text = $"{Fmt.Size(_total)} in total.  {_items.Count(i => i.IsDir)} folder(s), {_items.Count(i => !i.IsDir)} file(s) listed, biggest first." +
                         (hidden > 0 ? $"  {hidden} hidden item(s): Advanced shows them." : "") +
                         "  Double-click opens it in the Space tab.";
        }

        IEnumerable<SpaceItem> Sel() => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as SpaceItem).Where(r => r != null);

        void OpenInZilch() { var r = Sel().FirstOrDefault(); if (r != null) _space.OpenPath(r.Path); }

        void Explore() { var r = Sel().FirstOrDefault(); if (r != null) Dialogs.ShowInExplorer(r.Path); }

        void Delete(bool emptyOnly)
        {
            var rows = Sel().Where(r => !emptyOnly || r.IsDir).ToList();
            if (rows.Count == 0) { Dialogs.Info(this, emptyOnly ? "Select one or more folders first." : "Select something first."); return; }
            if (!_space.DeleteItems(rows, emptyOnly)) return;
            // drop what's gone, refresh sizes of what's left
            foreach (var r in rows)
            {
                if (!File.Exists(r.Path) && !Directory.Exists(r.Path)) { _items.Remove(r); _total -= r.Size; }
                else if (r.IsDir && r.Node != null)
                {
                    long now = Categories.SizeIn(r.Node, _cat);
                    _total -= r.Size - now;
                    r.Size = now;
                }
            }
            _total = Math.Max(0, _total);
            _items.Sort((a, b) => b.Size.CompareTo(a.Size));
            Fill();
        }
    }
}
