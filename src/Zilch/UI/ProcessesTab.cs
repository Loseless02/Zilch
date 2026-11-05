using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Live CPU / RAM / GPU / VRAM meters with a 60 second history.</summary>
    public sealed class Meters : Control
    {
        sealed class M { public string Name; public Color Color; public string Value = "…"; public double Pct = -1; public readonly Queue<double> Hist = new Queue<double>(); }
        readonly M[] _m =
        {
            new M { Name = "CPU", Color = Color.FromArgb(47, 128, 237) },
            new M { Name = "RAM", Color = Color.FromArgb(155, 81, 224) },
            new M { Name = "GPU", Color = Color.FromArgb(39, 174, 96) },
            new M { Name = "VRAM", Color = Color.FromArgb(242, 153, 74) },
        };

        public Meters()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            Height = 78;
        }

        public void Set(int i, double pct, string value)
        {
            var m = _m[i];
            m.Pct = pct; m.Value = value;
            m.Hist.Enqueue(pct < 0 ? 0 : pct);
            while (m.Hist.Count > 60) m.Hist.Dequeue();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int gap = 10, w = (ClientSize.Width - gap * 3) / 4, h = ClientSize.Height - 4;
            for (int i = 0; i < 4; i++)
            {
                var m = _m[i];
                var r = new Rectangle(i * (w + gap), 2, w, h);
                using (var bg = new SolidBrush(Color.FromArgb(248, 248, 248))) g.FillRectangle(bg, r);
                using (var pen = new Pen(Color.FromArgb(220, 220, 220))) g.DrawRectangle(pen, r);

                // history graph
                var gr = new Rectangle(r.X + 1, r.Y + Font.Height * 2 + 6, r.Width - 2, r.Height - Font.Height * 2 - 7);
                if (m.Hist.Count > 1 && gr.Height > 4)
                {
                    var pts = new List<PointF> { new PointF(gr.Right, gr.Bottom) };
                    var arr = m.Hist.ToArray();
                    for (int k = arr.Length - 1; k >= 0; k--)
                    {
                        float x = gr.Right - (arr.Length - 1 - k) * (gr.Width / 59f);
                        pts.Add(new PointF(x, gr.Bottom - (float)(Math.Min(100, arr[k]) / 100 * gr.Height)));
                    }
                    pts.Add(new PointF(pts[pts.Count - 1].X, gr.Bottom));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(60, m.Color))) g.FillPolygon(b, pts.ToArray());
                    using (var p = new Pen(m.Color, 1.5f)) g.DrawLines(p, pts.Skip(1).Take(pts.Count - 2).ToArray());
                    g.SmoothingMode = SmoothingMode.None;
                }
                TextRenderer.DrawText(g, m.Name, Theme.Bold, new Point(r.X + 6, r.Y + 4), m.Color);
                var pct = m.Pct < 0 ? "n/a" : $"{m.Pct:0}%";
                var big = new Font(Font.FontFamily, Font.Size * 1.2f, FontStyle.Bold);
                var sz = TextRenderer.MeasureText(g, pct, big);
                TextRenderer.DrawText(g, pct, big, new Point(r.Right - sz.Width - 4, r.Y + 2), ForeColor);
                big.Dispose();
                TextRenderer.DrawText(g, m.Value, Font, new Point(r.X + 6, r.Y + Font.Height + 6), Theme.Muted);
            }
        }
    }

    /// <summary>Everything running, what it's doing, and a way to stop it. Updates every second.</summary>
    public sealed class ProcessesTab : UserControl
    {
        readonly Meters _meters = new Meters();
        readonly TextBox _search = new TextBox { Width = 220 };
        readonly CheckBox _freeze = new CheckBox { Text = "Freeze", AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
        readonly Label _count = new Label { AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(8, 6, 0, 0) };
        readonly FastListView _list = new FastListView();
        readonly Button _kill = new Button { Text = "End task", Width = 90, Font = Theme.Bold };
        readonly Button _killTree = new Button { Text = "End task + children", Width = 140 };
        readonly Button _explore = new Button { Text = "Show in Explorer", Width = 130 };
        readonly Button _zilch = new Button { Text = "Show in Zilch", Width = 110 };
        readonly Timer _timer = new Timer { Interval = 1000 };
        readonly ProcSampler _sampler = new ProcSampler();
        readonly Dictionary<long, ListViewItem> _rows = new Dictionary<long, ListViewItem>();
        List<ProcInfo> _last = new List<ProcInfo>();
        bool _sampling;
        SpaceTab _space;

        static readonly string[] KindText = { "Windows", "Service", "App", "Background", "Unknown" };
        static readonly Color[] KindColor =
        {
            Theme.Slate,                    // Windows
            Color.FromArgb(0, 92, 175),     // Service
            Color.FromArgb(20, 20, 20),     // App
            Theme.Purple,                   // Background
            Color.FromArgb(200, 90, 0),     // Unknown: no signature, no publisher
        };

        public ProcessesTab(SpaceTab space)
        {
            _space = space;
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            _list.Dock = DockStyle.Fill;

            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            tools.Controls.AddRange(new Control[] { new Label { Text = "Search:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _search, _freeze, _count });
            var legend = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            string[] tips = { "part of Windows", "runs without you logging in", "has a window", "runs in your session without a window", "no digital signature and no publisher: worth a look" };
            for (int i = 0; i < KindText.Length; i++)
                legend.Controls.Add(new Label { Text = $"■ {KindText[i]}: {tips[i]}", AutoSize = true, ForeColor = KindColor[i], Padding = new Padding(0, 2, 14, 2) });

            _list.Columns.Add("Name", 200);
            _list.Columns.Add("PID", 60, HorizontalAlignment.Right);
            _list.Columns.Add("CPU", 60, HorizontalAlignment.Right);
            _list.Columns.Add("RAM", 85, HorizontalAlignment.Right);
            _list.Columns.Add("GPU", 55, HorizontalAlignment.Right);
            _list.Columns.Add("VRAM", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Disk/IO", 85, HorizontalAlignment.Right);
            _list.Columns.Add("Threads", 60, HorizontalAlignment.Right);
            _list.Columns.Add("Kind", 85);
            _list.Columns.Add("Publisher", 150);
            _list.Columns.Add("Doing / description", 240);
            _list.Columns.Add("Path", 400);
            _list.NumericColumns.UnionWith(new[] { 1, 2, 3, 4, 5, 6, 7 });
            _list.SortBy(2, true);
            _list.DoubleClick += (s, e) => Zilch();
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) Kill(false); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("End task", null, (s, e) => Kill(false));
            menu.Items.Add("End task + children", null, (s, e) => Kill(true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Show in Explorer", null, (s, e) => { var p = Sel().FirstOrDefault(); if (p?.Path != null) Dialogs.ShowInExplorer(p.Path); });
            menu.Items.Add("Show in Zilch", null, (s, e) => Zilch());
            menu.Items.Add("Copy path", null, (s, e) => { var p = Sel().FirstOrDefault(); if (p?.Path != null) try { Clipboard.SetText(p.Path); } catch { } });
            _list.ContextMenuStrip = menu;

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.AddRange(new Control[] { _kill, _killTree, _explore, _zilch });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(legend);
            Controls.Add(tools);
            Controls.Add(_meters);

            _kill.Click += (s, e) => Kill(false);
            _killTree.Click += (s, e) => Kill(true);
            _explore.Click += (s, e) => { var p = Sel().FirstOrDefault(); if (p?.Path != null) Dialogs.ShowInExplorer(p.Path); };
            _zilch.Click += (s, e) => Zilch();
            _search.TextChanged += (s, e) => Apply(_last);
            _timer.Tick += (s, e) => Tick();
            VisibleChanged += (s, e) => { if (Visible) { _timer.Start(); Tick(); } else _timer.Stop(); };
        }

        async void Tick()
        {
            if (_sampling || !Visible) return;
            var form = FindForm();
            if (form != null && form.WindowState == FormWindowState.Minimized) return;
            _sampling = true;
            try
            {
                var list = await Task.Run(() => _sampler.Sample());
                if (IsDisposed) return;
                _last = list;
                var u = _sampler.Usage;
                _meters.Set(0, u.Cpu, $"{u.Processes} processes, {u.Threads:N0} threads");
                _meters.Set(1, u.RamTotal > 0 ? 100.0 * u.RamUsed / u.RamTotal : -1, $"{Fmt.Size(u.RamUsed)} of {Fmt.Size(u.RamTotal)}");
                _meters.Set(2, u.Gpu, u.Gpu < 0 ? "no GPU counters on this PC" : "busiest engine");
                _meters.Set(3, u.VramUsed >= 0 && u.VramTotal > 0 ? 100.0 * u.VramUsed / u.VramTotal : -1,
                    u.VramUsed >= 0 ? $"{Fmt.Size(u.VramUsed)}" + (u.VramTotal > 0 ? $" of {Fmt.Size(u.VramTotal)}" : "") : "not available");
                if (!_freeze.Checked) Apply(list);
            }
            catch (Exception ex) { Crash.Log(ex, "Processes"); }
            finally { _sampling = false; }
        }

        void Apply(List<ProcInfo> list)
        {
            var q = _search.Text.Trim();
            var keep = new HashSet<long>();
            _list.BeginUpdate();
            foreach (var p in list)
            {
                if (q.Length > 0 && !Match(p, q)) continue;
                keep.Add(p.Key);
                if (!_rows.TryGetValue(p.Key, out var it))
                {
                    it = new ListViewItem(p.Name) { UseItemStyleForSubItems = true };
                    for (int i = 1; i < _list.Columns.Count; i++) it.SubItems.Add("");
                    _rows[p.Key] = it;
                    _list.Items.Add(it);
                }
                it.Tag = p;
                Set(it, 1, p.Pid.ToString(), p.Pid);
                Set(it, 2, p.Cpu >= 0.05 ? $"{p.Cpu:0.0}%" : "", (long)(p.Cpu * 100));
                Set(it, 3, Fmt.Size(p.PrivateMem), p.PrivateMem);
                Set(it, 4, p.Gpu >= 0.5 ? $"{p.Gpu:0}%" : "", (long)(p.Gpu * 100));
                Set(it, 5, p.Vram > 0 ? Fmt.Size(p.Vram) : "", p.Vram);
                Set(it, 6, p.IoRate >= 1024 ? Fmt.Size((long)p.IoRate) + "/s" : "", (long)p.IoRate);
                Set(it, 7, p.Threads.ToString(), p.Threads);
                Set(it, 8, KindText[(int)p.Kind] + (p.Session == 0 && p.Kind != ProcKind.Service ? " (svc)" : ""), null);
                Set(it, 9, p.Company ?? "", null);
                Set(it, 10, p.Window ?? p.Description ?? "", null);
                Set(it, 11, p.Path ?? (Elevation.IsAdmin ? "(protected)" : "(run as admin to see)"), null);
                var fc = KindColor[(int)p.Kind];
                if (it.ForeColor != fc) it.ForeColor = fc;
                var bc = p.Cpu >= 50 ? Color.FromArgb(255, 220, 200) : p.Cpu >= 15 ? Color.FromArgb(255, 240, 220) : SystemColors.Window;
                if (it.BackColor != bc) it.BackColor = bc;
                var font = p.Kind == ProcKind.Unknown ? Theme.Bold : _list.Font;
                if (it.Font != font) it.Font = font;
            }
            foreach (var k in _rows.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _list.Items.Remove(_rows[k]);
                _rows.Remove(k);
            }
            if (_list.ListViewItemSorter != null) _list.Sort();
            _list.EndUpdate();
            _count.Text = $"{_rows.Count} shown · updates every second" + (_freeze.Checked ? " (frozen)" : "") + (Elevation.IsAdmin ? "" : " · run as admin to see and end every process");
        }

        static void Set(ListViewItem it, int col, string text, long? sortKey)
        {
            var s = it.SubItems[col];
            if (s.Text != text) s.Text = text;
            if (sortKey.HasValue) s.Tag = sortKey.Value;
        }

        static bool Match(ProcInfo p, string q) =>
            p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Pid.ToString() == q ||
            (p.Path ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (p.Company ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (p.Window ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        IEnumerable<ProcInfo> Sel() => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as ProcInfo).Where(p => p != null);

        void Zilch()
        {
            var p = Sel().FirstOrDefault();
            if (p?.Path == null) { Dialogs.Info(this, "Zilch can't see where this process lives" + (Elevation.IsAdmin ? "." : ". Run as admin to see more.")); return; }
            _space.OpenPath(p.Path);
        }

        void Kill(bool tree)
        {
            var sel = Sel().ToList();
            if (sel.Count == 0) return;
            var targets = tree ? sel.SelectMany(p => ProcSampler.Tree(p, _last)).GroupBy(p => p.Key).Select(g => g.First()).ToList() : sel;
            var critical = targets.Where(p => ProcSampler.Critical.Contains(p.Name)).ToList();
            var windows = targets.Where(p => p.Kind == ProcKind.Windows).ToList();

            if (critical.Count > 0 && !AppState.IsSudo)
            {
                MessageBox.Show(this, "These keep Windows alive. Ending them crashes or logs out Windows, so only Sudo mode allows it:\n\n" +
                    string.Join("\n", critical.Select(p => $"{p.Name} ({p.Pid})")), "Zilch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var names = string.Join("\n", targets.Take(20).Select(p => $"{p.Name}  (PID {p.Pid})")) + (targets.Count > 20 ? $"\n…and {targets.Count - 20} more" : "");
            if (critical.Count > 0)
            {
                if (!Dialogs.TypeToConfirm(this, "End critical Windows processes",
                    "Ending these will crash Windows (blue screen) or sign you out right away. Unsaved work everywhere is lost.\n\n" + names, "KILL")) return;
            }
            else if (MessageBox.Show(this, $"End {targets.Count} process(es)?\n\n{names}\n\nUnsaved work in them is lost." +
                (windows.Count > 0 ? "\n\nSome are part of Windows and may restart themselves or make something stop working." : ""),
                "Zilch", MessageBoxButtons.OKCancel, windows.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;

            var errors = Busy.Run(this, "Ending processes…", f =>
            {
                var errs = new List<string>();
                for (int i = 0; i < targets.Count; i++)
                {
                    f.Report($"{targets[i].Name} ({targets[i].Pid})", (double)i / targets.Count);
                    var err = ProcSampler.Kill(targets[i]);
                    if (err != null) errs.Add($"{targets[i].Name} ({targets[i].Pid}): {err}");
                }
                return errs;
            }) ?? new List<string>();
            History.Add("Ended process", $"{targets.Count - errors.Count} ended, {errors.Count} failed",
                targets.Select(p => $"- {p.Name} ({p.Pid}) {p.Path}").Concat(errors.Select(e => "! failed: " + e)));
            if (errors.Count > 0)
                MessageBox.Show(this, "Could not end:\n\n" + string.Join("\n", errors) + (Elevation.IsAdmin ? "" : "\n\nRun Zilch as admin to end more."), "Zilch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Tick();
        }
    }
}
