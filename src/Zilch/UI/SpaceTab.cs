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
    /// <summary>"What is eating my disk?" Scan a drive, drill into folders, delete.</summary>
    public sealed class SpaceTab : UserControl
    {
        readonly ComboBox _drives = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly Button _scan = new Button { Text = "Scan", Width = 80 };
        readonly Button _folder = new Button { Text = "Scan a folder…", Width = 110 };
        readonly Button _stop = new Button { Text = "Stop", Width = 60, Enabled = false };
        readonly Button _up = new Button { Text = "↑ Up", Width = 60, Enabled = false };
        readonly RadioButton _viewFolders = new RadioButton { Text = "Browse folders", Checked = true, AutoSize = true };
        readonly RadioButton _viewFiles = new RadioButton { Text = "Biggest files", AutoSize = true };
        readonly Label _path = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Bold };
        readonly Label _summary = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted };
        readonly FastListView _list = new FastListView { Dock = DockStyle.Fill, OwnerDraw = true };
        readonly Button _delete = new Button { Text = "Delete selected", Width = 130 };
        readonly Button _explore = new Button { Text = "Show in Explorer", Width = 130 };
        readonly System.Windows.Forms.Timer _progress = new System.Windows.Forms.Timer { Interval = 150 };

        DirNode _root, _current;
        Scanner _scanner;
        List<FileEntry> _topFiles = new List<FileEntry>();
        CancellationTokenSource _cts;
        DriveInfo _drive;

        sealed class Row { public string Path; public long Size; public bool IsDir; public DirNode Node; public uint Attr; public Risk Risk; }

        public SpaceTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.AddRange(new Control[] { _drives, _scan, _folder, _stop });

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
            bottom.Controls.AddRange(new Control[] { _delete, _explore });

            _list.Columns.Add("Name", 360);
            _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _list.Columns.Add("Share", 160);
            _list.Columns.Add("Files", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Note", 400);
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
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => OpenSelected());
            menu.Items.Add("Show in Explorer", null, (s, e) => Explore());
            menu.Items.Add("Copy path", null, (s, e) => { var r = Selected().FirstOrDefault(); if (r != null) Clipboard.SetText(r.Path); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Delete", null, (s, e) => DeleteSelected());
            _list.ContextMenuStrip = menu;

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(sum);
            Controls.Add(nav);
            Controls.Add(top);

            _scan.Click += (s, e) => { if (_drives.SelectedItem is DriveItem d) StartScan(d.Drive.RootDirectory.FullName, d.Drive); };
            _folder.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Pick a folder to scan" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) StartScan(dlg.SelectedPath, null);
            };
            _stop.Click += (s, e) => _cts?.Cancel();
            _up.Click += (s, e) => GoUp();
            _viewFolders.CheckedChanged += (s, e) => Fill();
            _delete.Click += (s, e) => DeleteSelected();
            _explore.Click += (s, e) => Explore();
            _progress.Tick += (s, e) =>
            {
                if (_scanner == null) return;
                _summary.Text = $"Scanning… {_scanner.FilesSeen:N0} files, {Fmt.Size(_scanner.BytesSeen)}   {_scanner.CurrentPath}";
            };

            AppState.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)Fill); };
            LoadDrives();
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
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                    _drives.Items.Add(new DriveItem { Drive = d });
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
            _summary.Text = "Pick a drive and press Scan. Nothing is deleted without asking you first.";
        }

        async void StartScan(string root, DriveInfo drive)
        {
            _cts = new CancellationTokenSource();
            _drive = drive;
            _scanner = new Scanner(_cts.Token);
            _scan.Enabled = _folder.Enabled = false; _stop.Enabled = true;
            _list.Items.Clear();
            _path.Text = root;
            _progress.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sc = _scanner;
            DirNode node = null;
            try { node = await Task.Run(() => sc.Scan(root)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Scan failed"); }
            _progress.Stop();
            _scan.Enabled = _folder.Enabled = true; _stop.Enabled = false;
            if (node == null) return;
            _root = _current = node;
            _topFiles = sc.TopFiles();
            var took = sw.Elapsed.TotalSeconds;
            string extra = "";
            if (drive != null)
            {
                try
                {
                    long used = drive.TotalSize - drive.TotalFreeSpace;
                    long unseen = used - node.Size;
                    if (unseen > 0) extra = $"   ·   {Fmt.Size(unseen)} Windows doesn't let normal apps see (System Volume Info, restore points, locked folders)" +
                                            (Elevation.IsAdmin ? "" : ", run as admin to see more");
                }
                catch { }
            }
            _summary.Text = $"{Fmt.Size(node.Size)} in {node.FileCount:N0} files, scanned in {took:0.0}s. {sc.DeniedCount:N0} folder(s) not readable.{extra}";
            if (_cts.IsCancellationRequested) _summary.Text = "Stopped. Sizes below are partial.   " + _summary.Text;
            Fill();
        }

        bool ShowHidden => AppState.IsAdvancedOrMore;

        void Fill()
        {
            if (_root == null) return;
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.ListViewItemSorter = null;
            var rows = new List<Row>();
            long hiddenBytes = 0; int hiddenCount = 0;

            if (_viewFolders.Checked)
            {
                _path.Text = _current.FullPath;
                _up.Enabled = _current.Parent != null;
                foreach (var c in _current.Children)
                    rows.Add(new Row { Path = c.FullPath, Size = c.Size, IsDir = true, Node = c, Attr = c.Attributes });
                foreach (var f in Scanner.ListFiles(_current.FullPath))
                    rows.Add(new Row { Path = f.Path, Size = f.Size, IsDir = false, Attr = f.Attributes });
            }
            else
            {
                _path.Text = $"Biggest files in {_root.FullPath}";
                _up.Enabled = false;
                foreach (var f in _topFiles)
                    if (File.Exists(f.Path)) rows.Add(new Row { Path = f.Path, Size = f.Size, Attr = f.Attributes });
            }

            long parentSize = _viewFolders.Checked ? Math.Max(1, _current.Size) : Math.Max(1, _root.Size);
            foreach (var r in rows.OrderByDescending(r => r.Size))
            {
                bool hidden = (r.Attr & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0;
                if (hidden && !ShowHidden) { hiddenBytes += r.Size; hiddenCount++; continue; }
                r.Risk = Guard.Classify(r.Path);

                var name = _viewFolders.Checked ? Path.GetFileName(r.Path) : r.Path;
                var it = new ListViewItem(r.IsDir && _viewFolders.Checked ? name + "\\" : name) { Tag = r };
                if (r.IsDir) it.Font = Theme.Bold;
                it.SubItems.Add(FastListView.SizeCell(it, r.Size));
                it.SubItems.Add(new ListViewItem.ListViewSubItem(it, "") { Tag = (long)(r.Size * 1000 / parentSize) });
                it.SubItems.Add(new ListViewItem.ListViewSubItem(it, r.IsDir ? r.Node.FileCount.ToString("N0") : "") { Tag = r.IsDir ? r.Node.FileCount : 0L });

                var notes = new List<string>();
                if (r.IsDir && r.Node.Denied) notes.Add("not readable");
                if (hidden) notes.Add("hidden by Windows");
                if (r.Risk == Risk.Protected) notes.Add(AppState.IsSudo ? "SYSTEM: deleting may break Windows" : "locked: system (Sudo only)");
                else if (r.Risk == Risk.Caution) notes.Add(AppState.IsAdvancedOrMore ? "shared / whole app" : "locked: Advanced only");
                it.SubItems.Add(string.Join(", ", notes));

                if (r.Risk == Risk.Protected) it.ForeColor = AppState.IsSudo ? Theme.Danger : Theme.Muted;
                else if (r.Risk == Risk.Caution) it.ForeColor = Theme.Warn;
                else if (hidden) it.ForeColor = Theme.Muted;
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

        void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 2 || !(e.SubItem.Tag is long permille)) { e.DrawDefault = true; return; }
            e.DrawBackground();
            if (e.Item.Selected) e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 4, e.Bounds.Width - 50, e.Bounds.Height - 8);
            e.Graphics.FillRectangle(Brushes.Gainsboro, r);
            var w = (int)(r.Width * Math.Min(1000, permille) / 1000);
            using (var b = new SolidBrush(e.Item.ForeColor == Theme.Danger ? Theme.Danger : Theme.Bar))
                e.Graphics.FillRectangle(b, r.X, r.Y, w, r.Height);
            TextRenderer.DrawText(e.Graphics, $"{permille / 10.0:0.0}%", _list.Font,
                new Point(r.Right + 4, e.Bounds.Y + 2), e.Item.Selected ? SystemColors.HighlightText : SystemColors.ControlText);
        }

        IEnumerable<Row> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as Row).Where(r => r != null);

        void OpenSelected()
        {
            var r = Selected().FirstOrDefault();
            if (r == null) return;
            if (r.IsDir && _viewFolders.Checked) { _current = r.Node; Fill(); }
            else Dialogs.ShowInExplorer(r.Path);
        }

        void GoUp() { if (_current?.Parent != null && _viewFolders.Checked) { _current = _current.Parent; Fill(); } }

        void Explore() { var r = Selected().FirstOrDefault(); if (r != null) Dialogs.ShowInExplorer(r.Path); else if (_current != null) Dialogs.ShowInExplorer(_current.FullPath); }

        async void DeleteSelected()
        {
            var rows = Selected().ToList();
            if (rows.Count == 0) return;

            var blocked = rows.Where(r => !Guard.Allowed(r.Risk)).ToList();
            if (blocked.Count > 0)
            {
                var need = blocked.Any(b => b.Risk == Risk.Protected) ? "Sudo" : "Advanced";
                MessageBox.Show(this, $"{blocked.Count} selected item(s) are Windows or shared folders.\nSwitch to {need} mode to delete them.\n\n" +
                    string.Join("\n", blocked.Take(10).Select(b => b.Path)), "Locked", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool anyProtected = rows.Any(r => r.Risk == Risk.Protected);
            if (!Dialogs.ConfirmDelete(this, rows.Select(r => r.Path).ToList(), rows.Sum(r => r.Size), anyProtected)) return;

            bool recycle = AppState.UseRecycleBin, force = AppState.IsSudo;
            UseWaitCursor = true; Enabled = false;
            var res = await Task.Run(() =>
            {
                var r = new DeleteResult();
                foreach (var row in rows) Deleter.DeletePath(row.Path, recycle, force, r);
                return r;
            });
            UseWaitCursor = false; Enabled = true;
            History.Add(recycle ? "Recycled" : (force ? "Force-deleted" : "Deleted"), res, rows.Select(r => $"{r.Path}  ({Fmt.Size(r.Size)})"));

            // update the tree without rescanning
            foreach (var row in rows)
            {
                if (File.Exists(row.Path) || Directory.Exists(row.Path)) continue;
                var owner = row.IsDir ? row.Node.Parent : FindNode(Path.GetDirectoryName(row.Path));
                if (row.IsDir) owner?.Children.Remove(row.Node);
                for (var n = owner; n != null; n = n.Parent) n.Size -= row.Size;
                _topFiles.RemoveAll(f => string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
            }
            Fill();
            MessageBox.Show(this, res.Summary(), "Done", MessageBoxButtons.OK, res.Failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        DirNode FindNode(string path)
        {
            if (_root == null || path == null) return null;
            var rootPath = _root.FullPath.TrimEnd('\\');
            path = path.TrimEnd('\\');
            if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;
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
