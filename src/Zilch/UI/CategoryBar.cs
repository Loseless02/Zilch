using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>One stacked bar for the whole disk, a colored legend under it. Click a color to inspect it.</summary>
    public sealed class CategoryBar : Control
    {
        public sealed class Seg
        {
            public string Name; public Color Color; public long Bytes; public object Key;
        }

        List<Seg> _segs = new List<Seg>();
        long _total;
        string _caption = "", _hint = "";
        readonly List<KeyValuePair<Rectangle, Seg>> _hits = new List<KeyValuePair<Rectangle, Seg>>();
        readonly ToolTip _tip = new ToolTip();
        Seg _hover;
        Font _ul;
        Font Underlined => _ul ?? (_ul = new Font(Font, FontStyle.Underline));

        public event Action<Seg> SegmentClicked;

        public CategoryBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            Height = 70;
        }

        public void SetData(string caption, string hint, List<Seg> segs, long total)
        {
            _caption = caption ?? ""; _hint = hint ?? "";
            _segs = segs ?? new List<Seg>();
            _total = Math.Max(1, total);
            Relayout();
            Invalidate();
        }

        int Line => Font.Height;

        void Relayout()
        {
            // legend wraps; grow the control to fit it
            using (var g = CreateGraphics())
            {
                int x = 0, rows = 1, w = Math.Max(100, ClientSize.Width - 8);
                foreach (var s in _segs)
                {
                    int iw = Line + TextRenderer.MeasureText(g, Label(s), Font, Size.Empty, TextFormatFlags.NoPrefix).Width + 14;
                    if (x + iw > w && x > 0) { rows++; x = 0; }
                    x += iw;
                }
                int h = Line + 6 + (Line + 4) + 6 + rows * (Line + 4) + 4;
                if (Height != h) Height = h;
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Relayout(); }

        static string Label(Seg s) => $"{s.Name}  {Fmt.Size(s.Bytes)}";

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            _hits.Clear();
            int pad = 4, line = Line;

            TextRenderer.DrawText(g, _caption, Theme.Bold, new Point(pad, 2), ForeColor, TextFormatFlags.NoPrefix);
            if (_hint.Length > 0)
            {
                int cw = TextRenderer.MeasureText(g, _caption, Theme.Bold).Width;
                TextRenderer.DrawText(g, _hint, Font, new Point(pad + cw + 8, 2), Theme.Muted, TextFormatFlags.NoPrefix);
            }

            var bar = new Rectangle(pad, line + 6, ClientSize.Width - 2 * pad, line + 4);
            using (var path = Rounded(bar, 5))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(Theme.Free)) g.FillPath(bg, path);
                var clip = g.Clip;
                g.SetClip(path);
                g.SmoothingMode = SmoothingMode.None;
                double x = bar.X;
                foreach (var s in _segs)
                {
                    if (s.Bytes <= 0) continue;
                    double w = Math.Max(2, bar.Width * (double)s.Bytes / _total);
                    var r = new Rectangle((int)x, bar.Y, (int)Math.Ceiling(w), bar.Height);
                    using (var b = new SolidBrush(s == _hover ? ControlPaint.Light(s.Color, 0.4f) : s.Color)) g.FillRectangle(b, r);
                    if (s.Key as string == "hidden")
                        using (var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(70, Color.White), Color.Transparent)) g.FillRectangle(hb, r);
                    _hits.Add(new KeyValuePair<Rectangle, Seg>(r, s));
                    x += w;
                }
                g.Clip = clip;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(200, 200, 200))) g.DrawPath(pen, path);
            }

            // legend
            int lx = pad, ly = bar.Bottom + 6, w2 = ClientSize.Width - 2 * pad;
            foreach (var s in _segs)
            {
                var text = Label(s);
                int tw = TextRenderer.MeasureText(g, text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
                int iw = line + tw + 14;
                if (lx + iw > pad + w2 && lx > pad) { lx = pad; ly += line + 4; }
                var dot = new Rectangle(lx, ly + 3, line - 6, line - 6);
                using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, dot);
                if (s.Key is string k && k == "free") using (var p = new Pen(Color.Silver)) g.DrawEllipse(p, dot);
                TextRenderer.DrawText(g, text, s == _hover ? Underlined : Font, new Point(lx + line - 2, ly), ForeColor, TextFormatFlags.NoPrefix);
                _hits.Add(new KeyValuePair<Rectangle, Seg>(new Rectangle(lx, ly, iw - 8, line + 2), s));
                lx += iw;
            }
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        Seg HitTest(Point p)
        {
            foreach (var h in _hits) if (h.Key.Contains(p)) return h.Value;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var s = HitTest(e.Location);
            if (s == _hover) return;
            _hover = s;
            Cursor = s != null && s.Key is Cat ? Cursors.Hand : Cursors.Default;
            _tip.SetToolTip(this, s == null ? "" :
                $"{s.Name}: {Fmt.Size(s.Bytes)} ({100.0 * s.Bytes / _total:0.0}%)" + (s.Key is Cat ? "\nClick to see what's in it." : ""));
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = null; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var s = HitTest(e.Location);
            if (s != null) SegmentClicked?.Invoke(s);
        }
    }
}
