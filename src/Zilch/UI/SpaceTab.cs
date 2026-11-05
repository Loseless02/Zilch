using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>A file or folder shown in the Space or Inspector list.</summary>
    public sealed class SpaceItem
    {
        public string Path; public long Size; public bool IsDir; public DirNode Node; public uint Attr; public Risk Risk; public Cat Cat; public string KindText;
    }

    /// <summary>"What is eating my disk?" Scan a drive, drill into folders, delete.</summary>
    public sealed class SpaceTab : UserControl
    {
        readonly ComboBox _drives = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, DrawMode = DrawMode.OwnerDrawFixed };
        readonly Button _scan = new Button { Text = "Scan", Width = 80 };
        readonly Button _folder = new Button { Text = "Scan a folder…", Width = 110 };
        readonly Button _stop = new Button { Text = "Stop", Width = 60, Enabled = false };
        readonly Button _refresh = new Button { Text = "Refresh", Width = 80 };
        readonly Button _up = new Button { Text = "↑ Up", Width = 60, Enabled = false };
        readonly RadioButton _viewFolders = new RadioButton { Text = "Browse folders", Checked = true, AutoSize = true };
        readonly RadioButton _viewFiles = new RadioButton { Text = "Biggest files", AutoSize = true };
        readonly Label _path = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Bold };
        readonly Label _summary = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, AutoEllipsis = true };
        readonly CategoryBar _bar = new CategoryBar();
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, OwnerDraw = true };
        readonly Button _delete = new Button { Text = "Delete selected", Width = 130 };
        readonly Button _empty = new Button { Text = "Free up this folder", Width = 140 };
        readonly Button _selectAll = new Button { Text = "Select all", Width = 90 };
        readonly Button _explore = new Button { Text = "Show in Explorer", Width = 130 };
        readonly System.Windows.Forms.Timer _progress = new System.Windows.Forms.Timer { Interval = 150 };
        readonly System.Windows.Forms.Timer _live = new System.Windows.Forms.Timer { Interval = 1000 };

        DirNode _root, _current;
        Scanner _scanner;
        List<FileEntry> _topFiles = new List<FileEntry>();
        CancellationTokenSource _cts;
        DriveInfo _drive;
        long[] _catTotals;
        DateTime _scannedAt;
        string _afterScanSelect;

        /// <summary>The Inspector tab, set by MainForm.</summary>
        public InspectorTab Inspector;

        public SpaceTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.AddRange(new Control[] { _drives, _scan, _folder, _stop, _refresh });

            var nav = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 4 };
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            nav.Controls.Add(_up, 0, 0); nav.Controls.Add(_path, 1, 0);
            nav.Controls.Add(_viewFolders, 2, 0); nav.Controls.Add(_viewFiles, 3, 0);

            var sum = new Panel { Dock = DockStyle.Top, Height = 24 };
            sum.Controls.Add(_summary);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _delete, _empty, _selectAll, _explore });

            _list.Columns.Add("Name", 360);
            _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Share", 160);
            _list.Columns.Add("Files", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Kind", 130);
            _list.Columns.Add("Note", 300);
            _list.NumericColumns.UnionWith(new[] { 1, 2, 3 });
            _list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _list.DrawItem += (s, e) => { };
            _list.DrawSubItem += DrawSubItem;
            _list.DoubleClick += (s, e) => OpenSelected();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) OpenSelected();
                else if (e.KeyCode == Keys.Back) GoUp();
                else if (e.KeyCode == Keys.Delete) DeleteSelected();
                else if (e.KeyCode == Keys.F5) Refresh_();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => OpenSelected());
            menu.Items.Add("Show in Explorer", null, (s, e) => Explore());
            menu.Items.Add("Copy path", null, (s, e) => { var r = Selected().FirstOrDefault(); if (r != null) try { Clipboard.SetText(r.Path); } catch { } });
            menu.Items.Add("Select all", null, (s, e) => _list.SelectAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Free up this folder (keep folder, delete what's inside)", null, (s, e) => FreeUpSelected());
            menu.Items.Add("Delete", null, (s, e) => DeleteSelected());
            _list.ContextMenuStrip = menu;

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(sum);
            Controls.Add(nav);
            Controls.Add(_bar);
            Controls.Add(top);

            _drives.DrawItem += (s, e) =>
            {
                e.DrawBackground();
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, _drives.Items[e.Index].ToString(), e.Font, e.Bounds, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            _drives.DropDown += (s, e) => _drives.Invalidate();
            _drives.SelectedIndexChanged += (s, e) => { if (_root == null) UpdateBar(); };
            _scan.Click += (s, e) => { if (_drives.SelectedItem is DriveItem d) StartScan(d.Drive.RootDirectory.FullName, d.Drive); };
            _folder.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Pick a folder to scan" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) StartScan(dlg.SelectedPath, null);
            };
            _stop.Click += (s, e) => _cts?.Cancel();
            _refresh.Click += (s, e) => Refresh_();
            _up.Click += (s, e) => GoUp();
            _viewFolders.CheckedChanged += (s, e) => Fill();
            _delete.Click += (s, e) => DeleteSelected();
            _empty.Click += (s, e) => FreeUpSelected();
            _selectAll.Click += (s, e) => { _list.Focus(); _list.SelectAll(); };
            _explore.Click += (s, e) => Explore();
            _bar.SegmentClicked += OnSegment;
            _progress.Tick += (s, e) =>
            {
                if (_scanner == null) return;
                _summary.Text = $"Scanning… {_scanner.FilesSeen:N0} files, {Fmt.Size(_scanner.BytesSeen)}   {_scanner.CurrentPath}";
            };
            // every second: free space, the bar, the drive list. Cheap: no disk scanning here.
            _live.Tick += (s, e) => { if (Visible) { _drives.Invalidate(); UpdateBar(); } };
            VisibleChanged += (s, e) => { if (Visible) { _drives.Invalidate(); UpdateBar(); } };

            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Fill); };
            LoadDrives();
            HandleCreated += (s, e) => { UpdateBar(); _live.Start(); };
        }

        sealed class DriveItem
        {
            public DriveInfo Drive;
            public override string ToString()
            {
                try { return $"{Drive.Name}  {Drive.VolumeLabel}   {Fmt.Size(Drive.AvailableFreeSpace)} free of {Fmt.Size(Drive.TotalSize)}"; }
                catch { return Drive.Name; }
            }
        }

        void LoadDrives()
        {
            _drives.Items.Clear();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                        _drives.Items.Add(new DriveItem { Drive = d });
            }
            catch { }
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
            _summary.Text = "Pick a drive and press Scan. Nothing is deleted without asking you first.";
        }

        DriveInfo BarDrive => _root != null ? _drive : (_drives.SelectedItem as DriveItem)?.Drive;

        /// <summary>Redraw the colored disk bar from cached numbers plus live free space.</summary>
        void UpdateBar()
        {
            var segs = new List<CategoryBar.Seg>();
            long total;
            string caption, hint;
            var d = BarDrive;
            long free = 0, size = 0;
            if (d != null) { try { free = d.AvailableFreeSpace; size = d.TotalSize; } catch { d = null; } }

            if (_root != null && _catTotals != null)
            {
                for (int i = 0; i < Categories.N; i++)
                    if (_catTotals[i] > 0) segs.Add(new CategoryBar.Seg { Name = Categories.Names[i], Color = Theme.CatColors[i], Bytes = _catTotals[i], Key = (Cat)i });
                segs.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                // Unknown always last, like Steam's "other"
                var unk = segs.FirstOrDefault(s => (Cat)s.Key == Cat.Unknown);
                if (unk != null) { segs.Remove(unk); segs.Add(unk); }
                if (d != null)
                {
                    long unseen = (size - free) - _root.Size;
                    if (unseen > 0) segs.Add(new CategoryBar.Seg { Name = "Hidden from apps", Color = Theme.Hidden, Bytes = unseen, Key = "hidden" });
                    segs.Add(new CategoryBar.Seg { Name = "Free", Color = Theme.Free, Bytes = free, Key = "free" });
                    // hard links (WinSxS) and cloud placeholders can make the scan bigger than the used space
                    total = Math.Max(size, segs.Sum(x => x.Bytes));
                    caption = $"{d.Name.TrimEnd('\\')}   {Fmt.Size(free)} free of {Fmt.Size(size)}";
                }
                else
                {
                    total = _root.Size;
                    caption = $"{_root.FullPath}   {Fmt.Size(_root.Size)}";
                }
                hint = $"scanned {Ago(_scannedAt)}. Click a color to see what's in it.";
            }
            else if (d != null)
            {
                segs.Add(new CategoryBar.Seg { Name = "Used", Color = Theme.Bar, Bytes = size - free, Key = "used" });
                segs.Add(new CategoryBar.Seg { Name = "Free", Color = Theme.Free, Bytes = free, Key = "free" });
                total = size;
                caption = $"{d.Name.TrimEnd('\\')}   {Fmt.Size(free)} free of {Fmt.Size(size)}";
                hint = "Scan to break it down by type.";
            }
            else { _bar.SetData("", "", segs, 1); return; }
            _bar.SetData(caption, hint, segs, total);
        }

        static string Ago(DateTime t)
        {
            var s = (DateTime.Now - t).TotalSeconds;
            if (s < 60) return "just now";
            if (s < 3600) return $"{(int)(s / 60)} min ago";
            return t.ToString("HH:mm");
        }

        void OnSegment(CategoryBar.Seg s)
        {
            if (s.Key is Cat c)
            {
                if (_root == null || Inspector == null) return;
                var items = Categories.Find(_root, c).Select(ci => Item(ci.Path, ci.Size, ci.IsDir, ci.Node, ci.Attr)).ToList();
                foreach (var i in items) i.Cat = c;
                Inspector.Show(c, items, _catTotals[(int)c]);
                Nav.Show(Inspector);
            }
            else if (s.Key as string == "hidden")
                Dialogs.Info(this, $"{Fmt.Size(s.Bytes)} is used on this drive but no app is allowed to read it: restore points, " +
                                   "System Volume Information, the NTFS file table, other users' folders and locked Windows folders." +
                                   (Elevation.IsAdmin ? "" : "\n\nRestart Zilch as admin and scan again to see more of it."));
        }

        static SpaceItem Item(string path, long size, bool isDir, DirNode node, uint attr) =>
            new SpaceItem { Path = path, Size = size, IsDir = isDir, Node = node, Attr = attr };

        void StartScan(string root, DriveInfo drive) => StartScanAsync(root, drive);

        async void StartScanAsync(string root, DriveInfo drive)
        {
            if (_stop.Enabled) return; // already scanning
            _cts = new CancellationTokenSource();
            _scanner = new Scanner(_cts.Token);
            _scan.Enabled = _folder.Enabled = _refresh.Enabled = false; _stop.Enabled = true;
            _list.Items.Clear();
            _path.Text = root;
            _progress.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sc = _scanner;
            DirNode node = null;
            long[] totals = null;
            try
            {
                node = await Task.Run(() => sc.Scan(root));
                totals = await Task.Run(() => Categories.Totals(node));
            }
            catch (Exception ex) { Crash.Log(ex, "Scan"); MessageBox.Show(this, ex.Message, "Scan failed"); node = null; }
            _progress.Stop();
            _scan.Enabled = _folder.Enabled = _refresh.Enabled = true; _stop.Enabled = false;
            if (node == null) return;
            _drive = drive;
            _root = _current = node;
            _catTotals = totals;
            _scannedAt = DateTime.Now;
            _topFiles = sc.TopFiles();
            var took = sw.Elapsed.TotalSeconds;
            _summary.Text = $"{Fmt.Size(node.Size)} in {node.FileCount:N0} files, scanned in {took:0.0}s. {sc.DeniedCount:N0} folder(s) not readable." +
                            (drive != null && !Elevation.IsAdmin ? "   Run as admin to see more." : "");
            if (_cts.IsCancellationRequested) _summary.Text = "Stopped. Sizes below are partial.   " + _summary.Text;
            _viewFolders.Checked = true;
            if (_afterScanSelect != null) { var p = _afterScanSelect; _afterScanSelect = null; OpenPath(p); }
            else Fill();
            UpdateBar();
        }

        /// <summary>Rescan the folder you're looking at (or everything) without starting over.</summary>
        void Refresh_()
        {
            LoadDrivesKeepSelection();
            if (_root == null) { UpdateBar(); return; }
            var node = _viewFolders.Checked ? _current : _root;
            if (node == _root && _drive != null &&
                MessageBox.Show(this, "Refresh rescans the whole drive. Continue?\n\n(Tip: open a folder first to refresh just that folder.)",
                    "Zilch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Busy.Run(this, "Refreshing " + node.FullPath, f =>
            {
                f.Report(node.FullPath);
                Scanner.Rescan(node);
                _catTotals = Categories.Totals(_root);
                lock (_topFiles) _topFiles.RemoveAll(t => !File.Exists(t.Path));
            });
            _scannedAt = DateTime.Now;
            Fill();
            UpdateBar();
            ShowTotals("refreshed");
        }

        void ShowTotals(string why) =>
            _summary.Text = $"{Fmt.Size(_root.Size)} in {_root.FileCount:N0} files ({why}, {DateTime.Now:HH:mm:ss}).";

        void LoadDrivesKeepSelection()
        {
            var sel = (_drives.SelectedItem as DriveItem)?.Drive.Name;
            LoadDrives();
            for (int i = 0; i < _drives.Items.Count; i++)
                if (((DriveItem)_drives.Items[i]).Drive.Name == sel) _drives.SelectedIndex = i;
        }

        /// <summary>Show a file or folder in the Space tab ("Show in Zilch"). Scans the folder if it isn't scanned yet.</summary>
        public void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            path = Guard.Norm(path);
            bool isFile = File.Exists(path);
            var folder = isFile ? System.IO.Path.GetDirectoryName(path) : path;
            Nav.Show(this);
            var node = FindNode(folder);
            if (node == null)
            {
                if (!Directory.Exists(folder)) return;
                if (!_scan.Enabled) return; // a scan is running
                _afterScanSelect = path;
                StartScan(folder, null);
                return;
            }
            _viewFolders.Checked = true;
            _current = node;
            Fill();
            if (isFile)
                foreach (ListViewItem it in _list.Items)
                    if (it.Tag is SpaceItem r && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))
                    { it.Selected = true; it.EnsureVisible(); _list.Focus(); break; }
        }

        bool ShowHidden => AppState.IsAdvancedOrMore;

        void Fill()
        {
            if (_root == null) return;
            var keep = new HashSet<string>(Selected().Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.ListViewItemSorter = null;
            var rows = new List<SpaceItem>();
            long hiddenBytes = 0; int hiddenCount = 0;

            if (_viewFolders.Checked)
            {
                _path.Text = _current.FullPath;
                _up.Enabled = _current.Parent != null;
                foreach (var c in _current.Children)
                {
                    // color a folder by what is actually inside it (AppData\NVIDIA is mostly shader cache)
                    var (dom, share) = Categories.Dominant(c);
                    rows.Add(new SpaceItem
                    {
                        Path = c.FullPath, Size = c.Size, IsDir = true, Node = c, Attr = c.Attributes, Cat = dom,
                        KindText = share >= 0.6 ? Categories.Name(dom) : $"Mixed, mostly {Categories.Name(dom)}",
                    });
                }
                foreach (var f in Scanner.ListFiles(_current.FullPath))
                    rows.Add(new SpaceItem { Path = f.Path, Size = f.Size, IsDir = false, Attr = f.Attributes, Cat = Categories.ForFile(System.IO.Path.GetFileName(f.Path), _current.Cat) });
            }
            else
            {
                _path.Text = $"Biggest files in {_root.FullPath}";
                _up.Enabled = false;
                List<FileEntry> top;
                lock (_topFiles) top = _topFiles.ToList();
                foreach (var f in top)
                    if (File.Exists(f.Path))
                    {
                        var dir = System.IO.Path.GetDirectoryName(f.Path);
                        var owner = FindNode(dir);
                        rows.Add(new SpaceItem { Path = f.Path, Size = f.Size, Attr = f.Attributes, Cat = Categories.ForFile(System.IO.Path.GetFileName(f.Path), owner?.Cat ?? Categories.ForPath(dir)) });
                    }
            }

            long parentSize = _viewFolders.Checked ? Math.Max(1, _current.Size) : Math.Max(1, _root.Size);
            foreach (var r in rows.OrderByDescending(r => r.Size))
            {
                bool hidden = (r.Attr & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0;
                if (hidden && !ShowHidden) { hiddenBytes += r.Size; hiddenCount++; continue; }
                r.Risk = Guard.Classify(r.Path);
                var it = MakeRow(r, _viewFolders.Checked, parentSize, hidden);
                if (keep.Contains(r.Path)) it.Selected = true;
                _list.Items.Add(it);
            }
            if (hiddenCount > 0)
            {
                var it = new ListViewItem($"    + {hiddenCount} hidden/system item(s). Switch to Advanced to see them.") { ForeColor = Theme.Muted };
                it.SubItems.Add(FastListView.SizeCell(it, hiddenBytes));
                _list.Items.Add(it);
            }
            _list.EndUpdate();
        }

        /// <summary>One list row with the colors Zilch uses everywhere (also used by the Inspector).</summary>
        internal static ListViewItem MakeRow(SpaceItem r, bool shortName, long parentSize, bool hidden)
        {
            var name = shortName ? System.IO.Path.GetFileName(r.Path) : r.Path;
            var it = new ListViewItem(r.IsDir && shortName ? name + "\\" : name) { Tag = r };
            if (r.IsDir) it.Font = Theme.Bold;
            it.SubItems.Add(FastListView.SizeCell(it, r.Size));
            it.SubItems.Add(new ListViewItem.ListViewSubItem(it, "") { Tag = (long)(r.Size * 1000 / Math.Max(1, parentSize)) });
            it.SubItems.Add(new ListViewItem.ListViewSubItem(it, r.IsDir && r.Node != null ? r.Node.FileCount.ToString("N0") : "") { Tag = r.IsDir && r.Node != null ? r.Node.FileCount : 0L });
            it.SubItems.Add(r.KindText ?? Categories.Name(r.Cat));

            var notes = new List<string>();
            if (r.IsDir && r.Node != null && r.Node.Denied) notes.Add("not readable");
            if (hidden) notes.Add("hidden by Windows");
            if (r.Risk == Risk.Protected) notes.Add(AppState.IsSudo ? "SYSTEM: deleting may break Windows" : "locked: system (Sudo only)");
            else if (r.Risk == Risk.Caution) notes.Add(AppState.IsAdvancedOrMore ? "shared / whole app" : "locked: Advanced only");
            it.SubItems.Add(string.Join(", ", notes));

            if (r.Risk == Risk.Protected) it.ForeColor = AppState.IsSudo ? Theme.Danger : Theme.Muted;
            else if (r.Risk == Risk.Caution) it.ForeColor = Theme.Warn;
            else if (hidden) it.ForeColor = Theme.Muted;
            return it;
        }

        void DrawSubItem(object sender, DrawListViewSubItemEventArgs e) => DrawCell((ListView)sender, e);

        /// <summary>Name gets a category color chip, Share gets a bar in the category color.</summary>
        internal static void DrawCell(ListView list, DrawListViewSubItemEventArgs e)
        {
            var r = e.Item.Tag as SpaceItem;
            if (e.ColumnIndex == 0 && r != null)
            {
                e.DrawBackground();
                bool sel = e.Item.Selected;
                if (sel) e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
                int h = Math.Max(8, e.Bounds.Height - 10);
                var chip = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + (e.Bounds.Height - h) / 2, h, h);
                using (var b = new SolidBrush(Theme.Of(r.Cat))) e.Graphics.FillRectangle(b, chip);
                var tr = new Rectangle(chip.Right + 5, e.Bounds.Y, e.Bounds.Right - chip.Right - 5, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Item.Text, e.Item.Font ?? list.Font, tr, sel ? SystemColors.HighlightText : e.Item.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }
            if (e.ColumnIndex != 2 || !(e.SubItem.Tag is long permille)) { e.DrawDefault = true; return; }
            e.DrawBackground();
            if (e.Item.Selected) e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
            var rr = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 4, e.Bounds.Width - 50, e.Bounds.Height - 8);
            e.Graphics.FillRectangle(Brushes.Gainsboro, rr);
            var w = (int)(rr.Width * Math.Min(1000, permille) / 1000);
            var color = e.Item.ForeColor == Theme.Danger ? Theme.Danger : r != null ? Theme.Of(r.Cat) : Theme.Bar;
            using (var b = new SolidBrush(color)) e.Graphics.FillRectangle(b, rr.X, rr.Y, w, rr.Height);
            TextRenderer.DrawText(e.Graphics, $"{permille / 10.0:0.0}%", list.Font,
                new Point(rr.Right + 4, e.Bounds.Y + 2), e.Item.Selected ? SystemColors.HighlightText : SystemColors.ControlText);
        }

        IEnumerable<SpaceItem> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as SpaceItem).Where(r => r != null);

        void OpenSelected()
        {
            var r = Selected().FirstOrDefault();
            if (r == null) return;
            if (r.IsDir && r.Node != null && _viewFolders.Checked) { _current = r.Node; Fill(); }
            else if (r.IsDir) OpenPath(r.Path);
            else Dialogs.ShowInExplorer(r.Path);
        }

        void GoUp() { if (_current?.Parent != null && _viewFolders.Checked) { _current = _current.Parent; Fill(); } }

        void Explore() { var r = Selected().FirstOrDefault(); if (r != null) Dialogs.ShowInExplorer(r.Path); else if (_current != null) Dialogs.ShowInExplorer(_current.FullPath); }

        void DeleteSelected() => DeleteItems(Selected().ToList(), false);

        void FreeUpSelected()
        {
            var rows = Selected().Where(r => r.IsDir).ToList();
            if (rows.Count == 0 && _current != null && _viewFolders.Checked && !Selected().Any())
                rows.Add(new SpaceItem { Path = _current.FullPath, Size = _current.Size, IsDir = true, Node = _current, Attr = _current.Attributes, Cat = _current.Cat, Risk = Guard.Classify(_current.FullPath) });
            if (rows.Count == 0) { Dialogs.Info(this, "Select one or more folders first. Their contents get deleted, the folders themselves stay."); return; }
            DeleteItems(rows, true);
        }

        /// <summary>
        /// Delete files/folders (or, with emptyOnly, everything inside folders) behind the busy window,
        /// then fix the scanned tree, the bar and the list. Used by Space and Inspector.
        /// </summary>
        public bool DeleteItems(List<SpaceItem> rows, bool emptyOnly)
        {
            if (rows.Count == 0) return false;
            foreach (var r in rows) r.Risk = Guard.Classify(r.Path);

            var blocked = rows.Where(r => !Guard.Allowed(r.Risk)).ToList();
            if (blocked.Count > 0)
            {
                var need = blocked.Any(b => b.Risk == Risk.Protected) ? "Sudo" : "Advanced";
                MessageBox.Show(this, $"{blocked.Count} selected item(s) are Windows or shared folders.\nSwitch to {need} mode to {(emptyOnly ? "empty" : "delete")} them.\n\n" +
                    string.Join("\n", blocked.Take(10).Select(b => b.Path)), "Locked", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            bool anyProtected = rows.Any(r => r.Risk == Risk.Protected);
            var verb = emptyOnly ? (AppState.UseRecycleBin ? "Empty into the Recycle Bin" : "Empty (folders stay, everything inside is deleted)") : null;
            if (!Dialogs.ConfirmDelete(this, rows.Select(r => r.Path).ToList(), rows.Sum(r => r.Size), anyProtected, verb)) return false;

            bool recycle = AppState.UseRecycleBin, force = AppState.IsSudo;
            var res = Busy.Run(this, emptyOnly ? "Freeing up folders…" : "Removing…", f =>
            {
                var r = new DeleteResult();
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    f.Report(row.Path, (double)i / rows.Count);
                    if (!emptyOnly) { Deleter.DeletePath(row.Path, recycle, force, r); continue; }
                    List<string> inside;
                    try { inside = Directory.EnumerateFileSystemEntries(row.Path).ToList(); } catch { r.Failed.Add(row.Path); continue; }
                    for (int j = 0; j < inside.Count; j++)
                    {
                        f.Report(inside[j], (i + (double)j / Math.Max(1, inside.Count)) / rows.Count);
                        Deleter.DeletePath(inside[j], recycle, force, r);
                    }
                }
                f.Report("Updating sizes…");
                UpdateTree(rows, emptyOnly);
                return r;
            });
            if (res == null) { Fill(); UpdateBar(); return false; }

            var action = (emptyOnly ? "Emptied" : recycle ? "Recycled" : force ? "Force-deleted" : "Deleted");
            History.Add(action, res, rows.Select(r => $"{r.Path}  ({Fmt.Size(r.Size)})"));
            Fill();
            UpdateBar();
            ShowTotals("after your last change");
            MessageBox.Show(this, res.Summary(), "Done", MessageBoxButtons.OK, res.Failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            return true;
        }

        /// <summary>Runs on the busy thread (the UI is locked meanwhile).</summary>
        void UpdateTree(List<SpaceItem> rows, bool emptyOnly)
        {
            if (_root == null) return;
            foreach (var row in rows)
            {
                bool gone = !File.Exists(row.Path) && !Directory.Exists(row.Path);
                var node = row.IsDir ? (row.Node ?? FindNode(row.Path)) : null;
                if (row.IsDir)
                {
                    if (node == null) continue;
                    if (gone && !emptyOnly)
                    {
                        var parent = node.Parent;
                        if (parent == null) continue;
                        parent.Children.Remove(node);
                        for (var n = parent; n != null; n = n.Parent) { n.Size -= node.Size; n.FileCount -= node.FileCount; }
                        if (IsUnder(_current, node)) _current = parent;
                    }
                    else Scanner.Rescan(node);
                }
                else if (gone)
                {
                    var owner = FindNode(System.IO.Path.GetDirectoryName(row.Path));
                    if (owner == null) continue;
                    Categories.RemoveFile(owner, System.IO.Path.GetFileName(row.Path), row.Size);
                    owner.OwnFilesSize -= row.Size;
                    for (var n = owner; n != null; n = n.Parent) { n.Size -= row.Size; n.FileCount--; }
                }
            }
            lock (_topFiles)
                _topFiles.RemoveAll(t => rows.Any(r => t.Path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase)) && !File.Exists(t.Path));
            _catTotals = Categories.Totals(_root);
        }

        static bool IsUnder(DirNode n, DirNode ancestor)
        {
            for (; n != null; n = n.Parent) if (n == ancestor) return true;
            return false;
        }

        DirNode FindNode(string path)
        {
            if (_root == null || path == null) return null;
            var rootPath = _root.FullPath.TrimEnd('\\');
            path = path.TrimEnd('\\');
            if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;
            if (path.Length > rootPath.Length && path[rootPath.Length] != '\\') return null;
            var n = _root;
            foreach (var part in path.Substring(rootPath.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                n = n.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (n == null) return null;
            }
            return n;
        }
    }
}
