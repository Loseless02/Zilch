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
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = false };
            bar.Controls.Add(new Label { Text = "Mode:", AutoSize = true, Padding = new Padding(0, 7, 4, 0), Font = Theme.Bold });
            bar.Controls.AddRange(new Control[] { _simple, _advanced, _sudo, _recycle });
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

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            void Tab(string name, Control c) { var p = new TabPage(name); p.Controls.Add(c); tabs.TabPages.Add(p); }
            Tab("Space", new SpaceTab());
            Tab("Apps", new AppsTab());
            Tab("Windows Bloat", new BloatTab());
            Tab("Junk", new JunkTab());
            Tab("Logs", new LogsTab());

            Controls.Add(tabs);
            Controls.Add(_banner);
            Controls.Add(bar);

            _simple.Checked = true;
            _simple.CheckedChanged += (s, e) => { if (_simple.Checked) SetMode(Mode.Simple); };
            _advanced.CheckedChanged += (s, e) => { if (_advanced.Checked) SetMode(Mode.Advanced); };
            _sudo.CheckedChanged += (s, e) => { if (_sudo.Checked) SetMode(Mode.Sudo); };
            _recycle.CheckedChanged += (s, e) => AppState.UseRecycleBin = _recycle.Checked;
            PaintModes();

            if (wantSudo && Elevation.IsAdmin) Shown += (s, e) => _sudo.Checked = true;
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
