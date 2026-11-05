using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>"Track": where something lives, who it belongs to, what it's doing, and whether to remove it.</summary>
    public sealed class TrackDialog : Form
    {
        public static readonly Color RemoveColor = Color.FromArgb(16, 124, 16);
        public static readonly Color CallColor = Color.FromArgb(176, 106, 0);
        public static readonly Color KeepColor = Theme.Danger;

        public static Color ColorOf(Verdict v) => v == Verdict.Remove ? RemoveColor : v == Verdict.Keep ? KeepColor : CallColor;

        /// <summary>Gather facts behind the busy window, then show them.</summary>
        public static void Open(IWin32Window owner, string title, Func<Explanation> gather, Action showInZilch)
        {
            var e = Busy.Run(owner, "Tracking " + title + "…", f => gather());
            if (e == null) return;
            using (var d = new TrackDialog(e, showInZilch)) d.ShowDialog(owner);
        }

        TrackDialog(Explanation e, Action showInZilch)
        {
            Text = "Track: " + e.Title;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(760, 0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 600));
            var head = new Label { Text = e.Title, AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, Font.Size * 1.35f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
            table.Controls.Add(head, 0, 0); table.SetColumnSpan(head, 2);
            if (!string.IsNullOrEmpty(e.Path))
            {
                var p = new Label { Text = e.Path, AutoSize = true, UseMnemonic = false, ForeColor = Theme.Muted, MaximumSize = new Size(740, 0), Margin = new Padding(0, 0, 0, 10) };
                table.Controls.Add(p); table.SetColumnSpan(p, 2);
            }
            foreach (var kv in e.Facts)
            {
                table.Controls.Add(new Label { Text = kv.Key, AutoSize = true, Font = Theme.Bold, UseMnemonic = false, Margin = new Padding(0, 3, 12, 3) });
                table.Controls.Add(new Label { Text = kv.Value, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(600, 0), Margin = new Padding(0, 3, 0, 3) });
            }
            var color = ColorOf(e.Verdict);
            var verdict = new Label
            {
                Text = "In conclusion: " + Explanation.VerdictLabel(e.Verdict).ToLowerInvariant() + ".\n" + e.Conclusion,
                AutoSize = true, MaximumSize = new Size(740, 0), UseMnemonic = false, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 12, 0, 8),
                BackColor = Color.FromArgb(28, color), ForeColor = color, Font = Theme.Bold,
            };
            table.Controls.Add(verdict); table.SetColumnSpan(verdict, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
            var close = new Button { Text = "Close", Width = 80, DialogResult = DialogResult.OK };
            buttons.Controls.Add(close);
            bool exists = !string.IsNullOrEmpty(e.Path) && (File.Exists(e.Path) || Directory.Exists(e.Path));
            if (exists)
            {
                var exp = new Button { Text = "Show in Explorer", Width = 130 };
                exp.Click += (s, a) => Dialogs.ShowInExplorer(e.Path);
                buttons.Controls.Add(exp);
                if (showInZilch != null)
                {
                    var z = new Button { Text = "Show in Zilch", Width = 110 };
                    z.Click += (s, a) => { Close(); showInZilch(); };
                    buttons.Controls.Add(z);
                }
            }
            table.Controls.Add(buttons); table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = close; CancelButton = close;
        }
    }
}
