using System;
using System.Drawing;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>Small settings page. Every change applies right away and is saved.</summary>
    public sealed class SettingsTab : UserControl
    {
        readonly FlowLayoutPanel _page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        public SettingsTab()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(8);
            Controls.Add(_page);
            Build();
        }

        void Build()
        {
            _page.SuspendLayout();
            _page.Controls.Clear();

            var bar = Group("Storage bar (Space tab)");
            Check(bar, "Show the storage bar", () => Prefs.BarVisible, v => Prefs.BarVisible = v);
            Combo(bar, "Size", new[] { "Small", "Medium", "Large" }, () => (int)Prefs.BarHeight, i => Prefs.BarHeight = (BarSize)i);
            Combo(bar, "Position", new[] { "Top", "Bottom" }, () => (int)Prefs.BarPosition, i => Prefs.BarPosition = (BarPlace)i);
            Check(bar, "Legend under the bar", () => Prefs.BarLegend, v => Prefs.BarLegend = v);
            Check(bar, "Sizes in the legend", () => Prefs.BarLegendSizes, v => Prefs.BarLegendSizes = v);
            Check(bar, "Percentages in the legend", () => Prefs.BarPercent, v => Prefs.BarPercent = v);
            Check(bar, "Show free space", () => Prefs.BarFree, v => Prefs.BarFree = v);
            Check(bar, "Show space hidden from apps", () => Prefs.BarHidden, v => Prefs.BarHidden = v);

            var colors = Group("Category colors (click a color to change it)");
            var grid = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(760, 0), WrapContents = true, Margin = new Padding(0) };
            for (int i = 0; i < Categories.N; i++)
            {
                int idx = i;
                var swatch = new Panel { Width = 18, Height = 18, BackColor = Theme.CatColors[i], Cursor = Cursors.Hand, Margin = new Padding(0, 4, 6, 4), BorderStyle = BorderStyle.FixedSingle };
                var name = new Label { Text = Categories.Names[i], AutoSize = true, UseMnemonic = false, Cursor = Cursors.Hand, Margin = new Padding(0, 5, 18, 4) };
                EventHandler pick = (s, e) =>
                {
                    using (var dlg = new ColorDialog { Color = Theme.CatColors[idx], FullOpen = true })
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            Theme.CatColors[idx] = Color.FromArgb(255, dlg.Color);
                            swatch.BackColor = Theme.CatColors[idx];
                            Prefs.Apply();
                        }
                };
                swatch.Click += pick; name.Click += pick;
                var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Width = 230 };
                cell.Controls.Add(swatch); cell.Controls.Add(name);
                grid.Controls.Add(cell);
            }
            colors.Controls.Add(grid);

            var rows = Group("Colors per file / folder");
            Check(rows, "Color square before each name (its category)", () => Prefs.ColorChips, v => Prefs.ColorChips = v);
            Check(rows, "Share bar in the category color", () => Prefs.ColorShareBars, v => Prefs.ColorShareBars = v);
            Check(rows, "Orange / red / gray text for shared, system and hidden items", () => Prefs.ColorRisk, v => Prefs.ColorRisk = v);
            Check(rows, "Folders in bold", () => Prefs.BoldFolders, v => Prefs.BoldFolders = v);

            var details = Group("Details (columns in Space and Inspector)");
            Check(details, "Share (bar and %)", () => Prefs.ColShare, v => Prefs.ColShare = v);
            Check(details, "Files (how many files inside)", () => Prefs.ColFiles, v => Prefs.ColFiles = v);
            Check(details, "Kind (category)", () => Prefs.ColKind, v => Prefs.ColKind = v);
            Check(details, "Note (locked / hidden / shared)", () => Prefs.ColNote, v => Prefs.ColNote = v);

            var filters = Group("Filters (Space and Inspector lists)");
            Combo(filters, "Show", new[] { "Files and folders", "Only folders", "Only files" }, () => (int)Prefs.Show, i => Prefs.Show = (ShowKind)i);
            var minRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            minRow.Controls.Add(new Label { Text = "Only things bigger than", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            bool gb = Prefs.MinBytes >= (1L << 30) && Prefs.MinBytes % (1L << 30) == 0;
            var num = new NumericUpDown { Width = 80, Maximum = 100000, Minimum = 0, Value = Prefs.MinBytes / (gb ? (1L << 30) : (1L << 20)) };
            var unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
            unit.Items.AddRange(new object[] { "MB", "GB" });
            unit.SelectedIndex = gb ? 1 : 0;
            void SetMin() { Prefs.MinBytes = (long)num.Value * (unit.SelectedIndex == 1 ? (1L << 30) : (1L << 20)); Prefs.Apply(); }
            num.ValueChanged += (s, e) => SetMin();
            unit.SelectedIndexChanged += (s, e) => SetMin();
            minRow.Controls.AddRange(new Control[] { num, unit, new Label { Text = "(0 = everything)", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(8, 6, 0, 0) } });
            filters.Controls.Add(minRow);

            var reset = new Button { Text = "Reset everything to defaults", Width = 200, Margin = new Padding(3, 10, 3, 3) };
            reset.Click += (s, e) => { Prefs.Reset(); Build(); };
            _page.Controls.Add(reset);
            _page.Controls.Add(new Label { Text = "Saved in " + System.IO.Path.Combine(History.Folder, "settings.ini"), AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 6, 3, 3) });
            _page.ResumeLayout();
        }

        FlowLayoutPanel Group(string title)
        {
            var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(3, 3, 3, 8), MinimumSize = new Size(780, 0) };
            var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            box.Controls.Add(inner);
            _page.Controls.Add(box);
            return inner;
        }

        static void Check(FlowLayoutPanel g, string text, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = get(), Margin = new Padding(0, 2, 0, 2) };
            c.CheckedChanged += (s, e) => { set(c.Checked); Prefs.Apply(); };
            g.Controls.Add(c);
        }

        static void Combo(FlowLayoutPanel g, string label, string[] items, Func<int> get, Action<int> set)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.Add(new Label { Text = label, AutoSize = false, Width = 80, Height = 20, Margin = new Padding(0, 6, 8, 0) });
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            cb.Items.AddRange(items);
            cb.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, get()));
            cb.SelectedIndexChanged += (s, e) => { set(cb.SelectedIndex); Prefs.Apply(); };
            row.Controls.Add(cb);
            g.Controls.Add(row);
        }
    }
}
