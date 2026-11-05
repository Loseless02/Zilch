using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>
    /// Two views: one category of the disk bar (all Games, all Caches...), or the whole disk.
    /// The big search box searches whatever view you are in; on the whole disk it searches every file and folder name.
    /// </summary>
    public sealed class InspectorTab : UserControl, IRefreshable
    {
        readonly Button _back = new Button { Text = "← Whole disk", Width = 110, Height = 28, Margin = new Padding(0, 2, 10, 0) };
        readonly Panel _swatch = new Panel { Width = 18, Height = 18, Margin = new Padding(0, 7, 6, 0) };
        readonly Label _title = new Label { AutoSize = true, UseMnemonic = false, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.Size * 1.3f, FontStyle.Bold), Padding = new Padding(0, 3, 0, 0) };
        readonly Label _info = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Muted, Padding = new Padding(8, 8, 0, 0) };
        readonly TextBox _search = new TextBox { Dock = DockStyle.Fill, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.Size * 1.5f), Margin = new Padding(0, 2, 8, 2) };
        readonly CheckBox _relatives = new CheckBox { Text = "Show relatives (similar names, typos)", AutoSize = true, Margin = new Padding(0, 9, 8, 0) };
        readonly Button _refresh = new Button { Text = "Refresh (F5)", Width = 100, Height = 30, Margin = new Padding(0, 4, 0, 0) };
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, OwnerDraw = true };
        readonly Button _delete = new Button { Text = "Delete selected", Width = 130 };
        readonly Button _empty = new Button { Text = "Free up this folder", Width = 140 };
        readonly Button _selectAll = new Button { Text = "Select all", Width = 90 };
        readonly Button _zilch = new Button { Text = "Open in Zilch", Width = 110 };
        readonly Button _explore = new Button { Text = "Show in Explorer", Width = 130 };
        readonly Button _track = new Button { Text = "Track (what is this?)", Width = 150 };
        readonly System.Windows.Forms.Timer _debounce = new System.Windows.Forms.Timer { Interval = 150 };

        readonly SpaceTab _space;
        List<SpaceItem> _items = new List<SpaceItem>();   // category view: everything in the category
        List<SpaceItem> _hits;                             // whole-disk view: last search results
        bool _wholeDisk;
        Cat _cat;
        long _total;
        CancellationTokenSource _cts;
        string _status = "";

        public InspectorTab(SpaceTab space)
        {
            _space = space;
            Dock = DockStyle.Fill;
            Padding = new Padding(8);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.AddRange(new Control[] { _back, _swatch, _title, _info });

            var tools = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 4, 0, 6) };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tools.Controls.Add(_search, 0, 0);
            tools.Controls.Add(_relatives, 1, 0);
            tools.Controls.Add(_refresh, 2, 0);

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
            menu.Items.Add("Track (what is this? can I remove it?)", null, (s, e) => Track());
            menu.Items.Add("Copy path", null, (s, e) => { var r = Sel().FirstOrDefault(); if (r != null) try { Clipboard.SetText(r.Path); } catch { } });
            menu.Items.Add("Select all", null, (s, e) => _list.SelectAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Free up this folder", null, (s, e) => Delete(true));
            menu.Items.Add("Delete", null, (s, e) => Delete(false));
            _list.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _delete, _empty, _selectAll, _explore, _zilch, _track });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(tools);
            Controls.Add(top);

            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.Focus(); _list.Items[0].Selected = true; e.Handled = true; } };
            _relatives.CheckedChanged += (s, e) => Run();
            _debounce.Tick += (s, e) => { _debounce.Stop(); Run(); };
            _back.Click += (s, e) => ShowWholeDisk();
            _delete.Click += (s, e) => Delete(false);
            _empty.Click += (s, e) => Delete(true);
            _selectAll.Click += (s, e) => { _list.Focus(); _list.SelectAll(); };
            _zilch.Click += (s, e) => OpenInZilch();
            _explore.Click += (s, e) => Explore();
            _track.Click += (s, e) => Track();
            _refresh.Click += (s, e) => RefreshNow();
            Prefs.Changed += () => { SpaceTab.ApplyColumns(_list); Fill(); };
            SpaceTab.ApplyColumns(_list);
            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Fill); };
            VisibleChanged += (s, e) => { if (Visible && _title.Text == "Inspector" && _space.Root != null) ShowWholeDisk(); };
            HandleCreated += (s, e) => Cue();

            _title.Text = "Inspector";
            _info.Text = "Scan a drive in the Space tab first. Then search it here, or click a color in the bar.";
            _swatch.Visible = false;
            _back.Visible = false;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        void Cue() { try { SendMessage(_search.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "Search file and folder names…"); } catch { } }

        /// <summary>Category view (from the disk bar).</summary>
        public void Show(Cat c, List<SpaceItem> items, long total)
        {
            _wholeDisk = false;
            _cat = c; _items = items; _total = total;
            _swatch.BackColor = Theme.Of(c); _swatch.Visible = true;
            _back.Visible = true;
            _title.Text = Categories.Name(c);
            _search.Text = "";
            Fill();
        }

        /// <summary>Whole-disk view: the top folders, and search covers every name on the disk.</summary>
        void ShowWholeDisk()
        {
            if (_space.Root == null) { Dialogs.Info(this, "Scan a drive in the Space tab first."); return; }
            _wholeDisk = true;
            _swatch.Visible = false;
            _back.Visible = false;
            _title.Text = _space.Root.FullPath.TrimEnd('\\') + "  (whole disk)";
            _total = _space.Root.Size;
            _hits = null;
            Run();
        }

        /// <summary>Apply the search box to the current view.</summary>
        void Run()
        {
            _cts?.Cancel();
            var q = _search.Text.Trim();
            if (!_wholeDisk || q.Length == 0) { _hits = null; Fill(); return; }
            var nodes = _space.Flat();
            bool rel = _relatives.Checked;
            var cts = _cts = new CancellationTokenSource();
            _status = "Searching…";
            ShowInfo(0, 0);
            var sw = Stopwatch.StartNew();
            Task.Run(() => Search.Run(nodes, q, rel, cts.Token)).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || IsDisposed) return;
                if (t.IsFaulted) { Crash.Log(t.Exception, "Search"); _status = "Search failed: " + t.Exception.GetBaseException().Message; Fill(); return; }
                _hits = t.Result.Select(h => new SpaceItem
                {
                    Path = h.Path, Size = h.Size, IsDir = h.IsDir, Node = h.IsDir ? h.Node : null,
                    Attr = h.Hidden ? Native.FILE_ATTRIBUTE_HIDDEN : 0u,
                    Cat = h.IsDir ? h.Node.Cat : Categories.ForFile(h.Name, h.Node.Cat),
                }).ToList();
                _status = $"{_hits.Count:N0} match(es) in {sw.Elapsed.TotalSeconds:0.00}s" + (_hits.Count >= 1000 ? " (best 1,000 shown)" : "") +
                          (rel ? $", up to {Search.Tolerance(q.Length)} typo(s) allowed" : "");
                Fill();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Fill()
        {
            if (_title.Text == "Inspector") return;
            var q = _search.Text.Trim();
            bool rel = _relatives.Checked;
            IEnumerable<SpaceItem> source;
            if (_wholeDisk)
            {
                if (q.Length == 0)
                {
                    var root = _space.Root;
                    if (root == null) return;
                    source = root.Children.Select(c => new SpaceItem { Path = c.FullPath, Size = c.Size, IsDir = true, Node = c, Attr = c.Attributes, Cat = Categories.Dominant(c).cat })
                        .Concat(Scanner.ListFiles(root.FullPath).Select(f => new SpaceItem { Path = f.Path, Size = f.Size, Attr = f.Attributes, Cat = Categories.ForFile(Path.GetFileName(f.Path), root.Cat) }))
                        .OrderByDescending(i => i.Size).ToList();
                    _status = "Type to search every file and folder name on the disk.";
                }
                else source = _hits ?? new List<SpaceItem>();
            }
            else
            {
                // category view: match the file/folder name, exact or within a few typos
                int k = rel ? Search.Tolerance(q.Length) : 0;
                source = q.Length == 0 ? _items : _items.Where(i => NameMatches(Name(i), q, k)).ToList();
                _status = q.Length == 0 ? "" : $"{source.Count():N0} match(es) in {Categories.Name(_cat)}";
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            _list.ListViewItemSorter = null;
            int hidden = 0, filtered = 0;
            foreach (var r in source)
            {
                bool h = (r.Attr & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0 || (r.Node != null && r.Node.Hidden);
                if (h && !AppState.IsAdvancedOrMore) { hidden++; continue; }
                if (!Prefs.Pass(r.IsDir, r.Size)) { filtered++; continue; }
                r.Risk = Guard.Classify(r.Path);
                r.Marks = q.Length > 0 ? Search.Marks(Name(r), q, rel) : null;
                _list.Items.Add(SpaceTab.MakeRow(r, r.Marks != null, _total, h));
            }
            _list.EndUpdate();
            ShowInfo(hidden, filtered);
        }

        void ShowInfo(int hidden, int filtered)
        {
            var head = _wholeDisk ? $"{Fmt.Size(_total)} scanned." :
                $"{Fmt.Size(_total)} in total.  {_items.Count(i => i.IsDir)} folder(s), {_items.Count(i => !i.IsDir)} file(s), biggest first.";
            _info.Text = head + (_status.Length > 0 ? "  " + _status + "." : "") +
                         (hidden > 0 ? $"  {hidden} hidden item(s): Advanced shows them." : "") +
                         (filtered > 0 ? $"  {filtered} hidden by your filter ({Prefs.FilterText()})." : "");
        }

        static string Name(SpaceItem i)
        {
            var n = Path.GetFileName(i.Path.TrimEnd('\\'));
            return string.IsNullOrEmpty(n) ? i.Path : n;
        }

        static bool NameMatches(string name, string q, int k)
        {
            if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (k == 0) return false;
            var m = Search.Marks(name, q, true);
            int hits = m.Count(b => b);
            return hits >= q.Length - k;
        }

        IEnumerable<SpaceItem> Sel() => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as SpaceItem).Where(r => r != null);

        void OpenInZilch() { var r = Sel().FirstOrDefault(); if (r != null) _space.OpenPath(r.Path); }

        void Explore() { var r = Sel().FirstOrDefault(); if (r != null) Dialogs.ShowInExplorer(r.Path); }

        void Track() { var r = Sel().FirstOrDefault(); if (r != null) SpaceTab.TrackItem(this, r); else Dialogs.Info(this, "Select something first."); }

        /// <summary>Category view: rescan everything listed. Whole disk: rescan the drive (or folder) and search again.</summary>
        public void RefreshNow()
        {
            if (_title.Text == "Inspector") return;
            if (_wholeDisk)
            {
                _space.RefreshNow();
                if (_space.Root != null) { _total = _space.Root.Size; Run(); }
                return;
            }
            var fresh = _space.RecheckCategory(_cat, _items);
            if (fresh == null) return;
            _items = fresh;
            _total = _space.CategoryTotal(_cat);
            Fill();
        }

        void Delete(bool emptyOnly)
        {
            var rows = Sel().Where(r => !emptyOnly || r.IsDir).ToList();
            if (rows.Count == 0) { Dialogs.Info(this, emptyOnly ? "Select one or more folders first." : "Select something first."); return; }
            if (!_space.DeleteItems(rows, emptyOnly)) return;
            if (_wholeDisk) { _total = _space.Root?.Size ?? 0; Run(); return; }
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
