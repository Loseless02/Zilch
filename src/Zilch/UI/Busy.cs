using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zilch.Core;

namespace Zilch.UI
{
    /// <summary>
    /// Modal "working on it" window. While it is up the rest of Zilch can't be clicked or closed,
    /// so nothing can pull the rug out from under a delete in progress.
    /// </summary>
    public sealed class BusyForm : Form
    {
        readonly Label _what = new Label { AutoSize = false, AutoEllipsis = true, Location = new Point(16, 44), Size = new Size(468, 20), ForeColor = Theme.Muted };
        readonly Label _time = new Label { AutoSize = false, Location = new Point(16, 98), Size = new Size(468, 20), ForeColor = Theme.Muted };
        readonly ProgressBar _bar = new ProgressBar { Location = new Point(16, 70), Size = new Size(468, 20), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
        readonly Timer _tick = new Timer { Interval = 200 };
        readonly DateTime _start = DateTime.Now;
        volatile string _status = "";
        volatile int _permille = -1;
        bool _done;

        public BusyForm(string title)
        {
            Text = "Zilch";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, 128);
            var head = new Label { Text = title, Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold), AutoSize = false, Location = new Point(14, 12), Size = new Size(470, 28) };
            Controls.AddRange(new Control[] { head, _what, _bar, _time });
            _tick.Tick += (s, e) => Repaint();
            Shown += (s, e) => { _tick.Start(); Repaint(); };
        }

        /// <summary>Safe to call from any thread. fraction 0..1, or negative when unknown.</summary>
        public void Report(string status, double fraction = -1)
        {
            _status = status ?? "";
            _permille = fraction < 0 ? -1 : (int)(Math.Min(1, fraction) * 1000);
        }

        void Repaint()
        {
            _what.Text = _status;
            int p = _permille;
            if (p < 0) { if (_bar.Style != ProgressBarStyle.Marquee) _bar.Style = ProgressBarStyle.Marquee; }
            else
            {
                if (_bar.Style != ProgressBarStyle.Continuous) { _bar.Style = ProgressBarStyle.Continuous; _bar.Maximum = 1000; }
                _bar.Value = Math.Max(0, Math.Min(1000, p));
            }
            var el = DateTime.Now - _start;
            _time.Text = $"Started {_start:HH:mm:ss}   ·   {(int)el.TotalMinutes}:{el.Seconds:00} elapsed   ·   please wait, Zilch is locked until this is done";
        }

        internal void Finish() { _done = true; _tick.Stop(); Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_done) e.Cancel = true; // Alt+F4 and friends can't interrupt the work
            base.OnFormClosing(e);
        }
    }

    public static class Busy
    {
        /// <summary>
        /// Run work on a background thread behind a modal busy window. Returns its result,
        /// or default if it threw (the error is logged and shown; Zilch keeps running).
        /// </summary>
        public static T Run<T>(IWin32Window owner, string title, Func<BusyForm, T> work)
        {
            var top = (owner as Control)?.FindForm() ?? owner;
            T result = default(T);
            Exception error = null;
            using (var f = new BusyForm(title))
            {
                f.Shown += async (s, e) =>
                {
                    try { result = await Task.Run(() => work(f)); }
                    catch (Exception ex) { error = ex; }
                    f.Finish();
                };
                f.ShowDialog(top);
            }
            if (error != null)
            {
                Crash.Log(error, title);
                MessageBox.Show(top, $"\"{title}\" stopped with an error:\n\n{error.Message}\n\nZilch is still running. Details are in the Logs tab.",
                    "Zilch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return result;
        }

        public static void Run(IWin32Window owner, string title, Action<BusyForm> work) =>
            Run<object>(owner, title, f => { work(f); return null; });
    }
}
