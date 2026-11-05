using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    public static class Theme
    {
        public static readonly Color Danger = Color.FromArgb(196, 43, 28);
        public static readonly Color DangerBg = Color.FromArgb(255, 235, 232);
        public static readonly Color Muted = Color.FromArgb(120, 120, 120);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color Warn = Color.FromArgb(157, 93, 0);
        public static readonly Color Bar = Color.FromArgb(0, 120, 215);
        public static readonly Font Bold = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
    }

    /// <summary>ListView without flicker, with click-to-sort columns.</summary>
    public class FastListView : ListView
    {
        public FastListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            ColumnClick += (s, e) =>
            {
                var cur = ListViewItemSorter as Sorter;
                bool desc = cur != null && cur.Column == e.Column ? !cur.Desc : NumericColumns.Contains(e.Column);
                ListViewItemSorter = new Sorter { Column = e.Column, Desc = desc, Numeric = NumericColumns.Contains(e.Column) };
            };
        }

        /// <summary>Columns sorted by the long stored in SubItem.Tag (sizes).</summary>
        public HashSet<int> NumericColumns = new HashSet<int>();

        class Sorter : IComparer
        {
            public int Column; public bool Desc, Numeric;
            public int Compare(object x, object y)
            {
                var a = (ListViewItem)x; var b = (ListViewItem)y;
                int r;
                if (Numeric)
                {
                    long va = a.SubItems.Count > Column && a.SubItems[Column].Tag is long la ? la : -1;
                    long vb = b.SubItems.Count > Column && b.SubItems[Column].Tag is long lb ? lb : -1;
                    r = va.CompareTo(vb);
                }
                else r = string.Compare(a.SubItems.Count > Column ? a.SubItems[Column].Text : "", b.SubItems.Count > Column ? b.SubItems[Column].Text : "", StringComparison.OrdinalIgnoreCase);
                return Desc ? -r : r;
            }
        }

        public static ListViewItem.ListViewSubItem SizeCell(ListViewItem item, long size)
        {
            var s = new ListViewItem.ListViewSubItem(item, size < 0 ? "…" : Fmt.Size(size)) { Tag = size };
            return s;
        }
    }

    public static class Dialogs
    {
        /// <summary>
        /// Asks before deleting. If anything is Protected (Sudo only) the user must type DELETE.
        /// </summary>
        public static bool ConfirmDelete(IWin32Window owner, IList<string> paths, long totalBytes, bool anyProtected, string verb = null)
        {
            verb = verb ?? (AppState.UseRecycleBin ? "Move to Recycle Bin" : "Permanently delete");
            var preview = string.Join("\n", paths.Take(15)) + (paths.Count > 15 ? $"\n…and {paths.Count - 15} more" : "");
            var msg = $"{verb} {paths.Count} item(s)" + (totalBytes > 0 ? $" ({Fmt.Size(totalBytes)})" : "") + "?\n\n" + preview;
            if (!AppState.UseRecycleBin) msg += "\n\nThis cannot be undone.";
            if (!anyProtected)
                return MessageBox.Show(owner, msg, "Zilch", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

            return TypeToConfirm(owner, "Delete protected items",
                "Some of these are WINDOWS SYSTEM or SHARED files. Deleting them can stop Windows from booting, " +
                "break other apps, or lose data. There is no safety net.\n\n" + msg, "DELETE");
        }

        /// <summary>A dialog that only says yes once the user types the magic word.</summary>
        public static bool TypeToConfirm(IWin32Window owner, string title, string text, string word)
        {
            using (var f = new Form
            {
                Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(560, 360), Font = SystemFonts.MessageBoxFont,
            })
            {
                var lbl = new TextBox
                {
                    Text = text.Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    BorderStyle = BorderStyle.None, BackColor = Theme.DangerBg, ForeColor = Theme.Danger,
                    Location = new Point(12, 12), Size = new Size(536, 240), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                };
                var hint = new Label { Text = $"Type {word} to continue:", AutoSize = true, Location = new Point(12, 264) };
                var box = new TextBox { Location = new Point(12, 286), Width = 200 };
                var ok = new Button { Text = "Do it", Enabled = false, DialogResult = DialogResult.OK, Location = new Point(392, 320), Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(473, 320), Width = 75 };
                box.TextChanged += (s, e) => ok.Enabled = box.Text.Trim() == word;
                f.Controls.AddRange(new Control[] { lbl, hint, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => box.Focus();
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        public static void ShowInExplorer(string path)
        {
            try
            {
                if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch { }
        }

        public static void Info(IWin32Window owner, string text, string title = "Zilch") =>
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
