using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>One-click cleanup of caches, temp files, dumps and update leftovers.</summary>
    public sealed class JunkTab : UserControl, IRefreshable
    {
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, CheckBoxes = true };
        readonly Button _analyze = new Button { Text = "Analyze", Width = 100 };
        readonly Button _clean = new Button { Text = "Clean checked", Width = 130, Font = Theme.Bold };
        readonly Label _total = new Label { AutoSize = true, Font = Theme.Bold, Padding = new Padding(0, 6, 0, 0) };
        readonly List<JunkItem> _items = Junk.All();

        public JunkTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            _list.Columns.Add("What", 260);
            _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Details", 560);
            _list.NumericColumns.Add(1);
            _list.ItemChecked += (s, e) => UpdateTotal();

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _clean, _analyze, _total });
            Controls.Add(_list);
            Controls.Add(bottom);

            _analyze.Click += (s, e) => Analyze();
            _clean.Click += (s, e) => Clean();
            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Fill); };
            Fill();
        }

        void Fill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var j in _items)
            {
                if (j.AdvancedOnly && !AppState.IsAdvancedOrMore) continue;
                bool usable = !j.NeedsAdmin || Elevation.IsAdmin;
                var it = new ListViewItem(j.Name + (usable ? "" : "  (admin)")) { Tag = j, Checked = usable && j.DefaultChecked };
                it.SubItems.Add(j.Size == -1 ? new ListViewItem.ListViewSubItem(it, "") { Tag = -1L } : FastListView.SizeCell(it, j.Size));
                it.SubItems.Add(j.Description);
                if (!usable) it.ForeColor = Theme.Muted;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            UpdateTotal();
        }

        void UpdateTotal()
        {
            var sel = Checked();
            long t = sel.Where(j => j.Size > 0).Sum(j => j.Size);
            _total.Text = sel.Count == 0 ? "" : $"{sel.Count} checked" + (t > 0 ? $", about {Fmt.Size(t)}   " : "   ");
        }

        List<JunkItem> Checked() => _list.CheckedItems.Cast<ListViewItem>().Select(i => (JunkItem)i.Tag).ToList();

        public void RefreshNow() => Analyze();

        async void Analyze()
        {
            _analyze.Enabled = _clean.Enabled = false; UseWaitCursor = true;
            _total.Text = "Measuring…";
            var visible = _list.Items.Cast<ListViewItem>().Select(i => (JunkItem)i.Tag).ToList();
            await Task.Run(() => Parallel.ForEach(visible, j => { try { j.Size = j.Measure(); } catch { j.Size = 0; } }));
            _analyze.Enabled = _clean.Enabled = true; UseWaitCursor = false;
            Fill();
            _total.Text = $"Found {Fmt.Size(visible.Where(j => j.Size > 0).Sum(j => j.Size))} of junk.   " + _total.Text;
        }

        void Clean()
        {
            var sel = Checked();
            if (sel.Count == 0) return;
            if (sel.Any(j => j.NeedsAdmin) && !Elevation.IsAdmin)
            {
                if (!Elevation.Require(this, "Some checked items are Windows folders that need admin rights."))
                    sel = sel.Where(j => !j.NeedsAdmin).ToList();
                if (sel.Count == 0) return;
            }
            if (MessageBox.Show(this, "Clean these?\n\n" + string.Join("\n", sel.Select(j => "• " + j.Name)) +
                "\n\nFiles in use are skipped. This cannot be undone.", "Zilch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            var r = Busy.Run(this, "Cleaning junk…", f =>
            {
                var res = new DeleteResult();
                for (int i = 0; i < sel.Count; i++)
                {
                    var j = sel[i];
                    f.Report(j.Name, (double)i / sel.Count);
                    try { j.Clean(res); } catch (Exception ex) { res.Failed.Add(j.Name + ": " + ex.Message); }
                }
                f.Report("Measuring what's left…");
                foreach (var j in sel) { try { j.Size = j.Measure(); } catch { } }
                return res;
            });
            if (r == null) { Fill(); return; }
            History.Add("Junk cleanup", r, sel.Select(j => j.Name));
            Fill();
            MessageBox.Show(this, r.Summary(), "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
