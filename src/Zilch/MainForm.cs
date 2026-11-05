using System;
using System.Drawing;
using System.Windows.Forms;
using Zilch.Core;
using Zilch.UI;

namespace Zilch
{
    public sealed class MainForm : Form
    {
        readonly RadioButton _simple = ModeButton("Simple");
        readonly RadioButton _advanced = ModeButton("Advanced");
        readonly RadioButton _sudo = ModeButton("Sudo");
        readonly CheckBox _recycle = new CheckBox { Text = "Use Recycle Bin (undo possible, frees no space until emptied)", AutoSize = true, Padding = new Padding(16, 6, 0, 0) };
        readonly Label _banner = new Label
        {
            Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter, Visible = false,
            BackColor = Theme.Danger, ForeColor = Color.White, Font = Theme.Bold,
            Text = "SUDO MODE: nothing is protected. Hidden and Windows system files are shown and can be force-deleted. You can break Windows.",
        };
        readonly Label _session = new Label { AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Good, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right };
        readonly Label _allTime = new Label { AutoSize = true, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right };
        bool _switching;

        static RadioButton ModeButton(string text) => new RadioButton
        {
            Text = text, Appearance = Appearance.Button, AutoSize = false, Width = 90, Height = 28,
            TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat,
        };

        public MainForm(bool wantSudo)
        {
            Text = "Zilch" + (Elevation.IsAdmin ? "  (admin)" : "");
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1180, 760);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Prefs.Load();
            try
            {
                using (var st = typeof(MainForm).Assembly.GetManifestResourceStream("zilch.ico"))
                    if (st != null) Icon = new Icon(st);
            }
            catch { }

            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Mode:", AutoSize = true, Padding = new Padding(0, 7, 4, 0), Font = Theme.Bold });
            bar.Controls.AddRange(new Control[] { _simple, _advanced, _sudo, _recycle });
            var refresh = new Button { Text = "Refresh (F5)", Width = 100, Height = 28, Margin = new Padding(12, 3, 3, 3) };
            bar.Controls.Add(refresh);
            if (!Elevation.IsAdmin)
            {
                var admin = new LinkLabel { Text = "Restart as admin", AutoSize = true, Padding = new Padding(16, 7, 0, 0) };
                admin.LinkClicked += (s, e) => Elevation.RestartAsAdmin();
                bar.Controls.Add(admin);
            }
            var tip = new ToolTip();
            tip.SetToolTip(_simple, "Safe defaults. Hides system files and anything Zilch isn't sure about.");
            tip.SetToolTip(_advanced, "Shows hidden files, similar-name leftovers, registry details, silent/force uninstall, optional Windows apps.");
            tip.SetToolTip(_sudo, "Admin. Shows and deletes EVERYTHING, including Windows system files and apps Windows marks non-removable.");

            // wiped counters, top right
            var stats = new TableLayoutPanel { Dock = DockStyle.Right, AutoSize = true, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 4, 10, 2) };
            stats.Controls.Add(_session, 0, 0);
            stats.Controls.Add(_allTime, 0, 1);
            var header = new Panel { Dock = DockStyle.Top, Height = 46 };
            header.Controls.Add(bar);
            header.Controls.Add(stats);
            ShowStats();
            Stats.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)ShowStats); };

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            void Tab(string name, Control c) { var p = new TabPage(name); p.Controls.Add(c); tabs.TabPages.Add(p); }
            var space = new SpaceTab();
            var inspector = new InspectorTab(space);
            space.Inspector = inspector;
            Tab("Space", space);
            Tab("Inspector", inspector);
            Tab("Apps", new AppsTab());
            Tab("Startup", new StartupTab());
            Tab("Processes", new ProcessesTab(space));
            Tab("Windows Bloat", new BloatTab());
            Tab("Junk", new JunkTab());
            Tab("Logs", new LogsTab());
            Tab("Settings", new SettingsTab());
            Nav.Show = c =>
            {
                foreach (TabPage p in tabs.TabPages) if (p.Contains(c)) { tabs.SelectedTab = p; return; }
            };
            Nav.OpenPath = p => space.OpenPath(p);

            // one Refresh for every tab: the button, or F5 anywhere
            void RefreshCurrent()
            {
                if (tabs.SelectedTab?.Controls.Count > 0 && tabs.SelectedTab.Controls[0] is IRefreshable r) r.RefreshNow();
            }
            refresh.Click += (s, e) => RefreshCurrent();
            tip.SetToolTip(refresh, "Reload whatever tab you are on (rescan, reload the list...).");
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) { e.Handled = e.SuppressKeyPress = true; RefreshCurrent(); } };

            Controls.Add(tabs);
            Controls.Add(_banner);
            Controls.Add(header);

            _simple.Checked = true;
            _simple.CheckedChanged += (s, e) => { if (_simple.Checked) SetMode(Mode.Simple); };
            _advanced.CheckedChanged += (s, e) => { if (_advanced.Checked) SetMode(Mode.Advanced); };
            _sudo.CheckedChanged += (s, e) => { if (_sudo.Checked) SetMode(Mode.Sudo); };
            _recycle.CheckedChanged += (s, e) => AppState.UseRecycleBin = _recycle.Checked;
            PaintModes();

            if (wantSudo && Elevation.IsAdmin) Shown += (s, e) => _sudo.Checked = true;
        }

        void ShowStats()
        {
            _session.Text = Stats.SessionLine();
            _allTime.Text = Stats.AllTimeLine();
            _allTime.Visible = _allTime.Text.Length > 0;
        }

        void SetMode(Mode m)
        {
            if (_switching) return;
            if (m == Mode.Sudo && !AppState.IsSudo)
            {
                bool ok = Elevation.IsAdmin
                    ? Dialogs.TypeToConfirm(this, "Enter Sudo mode",
                        "Sudo mode removes every safety lock in Zilch:\n\n" +
                        "• Hidden and Windows system files are listed (System32, WinSxS, pagefile...)\n" +
                        "• They can be deleted. Zilch takes ownership from TrustedInstaller when Windows refuses\n" +
                        "• Files in use get deleted on the next restart\n" +
                        "• Apps Windows marks as non-removable (Store, Start, frameworks) can be removed\n\n" +
                        "Deleting the wrong thing can leave Windows unbootable. Zilch still marks dangerous items in red " +
                        "and asks before each delete, but it will not stop you.\n\n" +
                        "Tip: Windows Bloat tab → Create restore point first.", "SUDO")
                    : Elevation.Require(this, "Sudo mode needs administrator rights.", "--sudo");
                if (!ok) { Revert(); return; }
                History.Add("Sudo mode", "entered");
            }
            AppState.Mode = m;
            _banner.Visible = m == Mode.Sudo;
            PaintModes();
        }

        void Revert()
        {
            _switching = true;
            (AppState.Mode == Mode.Advanced ? _advanced : _simple).Checked = true;
            _switching = false;
            PaintModes();
        }

        void PaintModes()
        {
            foreach (var b in new[] { _simple, _advanced, _sudo })
            {
                bool on = b.Checked;
                b.FlatAppearance.BorderColor = b == _sudo ? Theme.Danger : Theme.Accent;
                b.BackColor = on ? (b == _sudo ? Theme.Danger : Theme.Accent) : SystemColors.Control;
                b.ForeColor = on ? Color.White : (b == _sudo ? Theme.Danger : SystemColors.ControlText);
            }
        }
    }
}
