// HydraClaude - manager window, tray presence, dialogs and entry point.
//
// The window is one card: a header that names the section and carries the two app-level
// actions, the profile list, and a footer holding system status on the left and exactly one
// primary action on the right. Everything per-row that isn't "open it" lives in the row's
// context menu, so the surface stays quiet (Rams 10).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HydraClaude
{
    public class MainForm : Form
    {
        readonly Store _store;
        readonly RunningTracker _tracker = new RunningTracker();
        PackageInfo _pkg;

        ProfileList _list;
        Panel _header, _footer;
        FlatButton _open, _edit, _folder, _new, _settings;
        Label _status;
        NotifyIcon _tray;
        ContextMenuStrip _rowMenu;
        System.Windows.Forms.Timer _poll;
        bool _scanning;

        public static readonly int ShowMessage =
            RegisterWindowMessage("HydraClaude.Show." + Environment.UserName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string message);

        public MainForm(Store store, bool startHidden)
        {
            _store = store;
            _pkg = Packaging.Resolve(_store.Config);

            Text = "HydraClaude";
            ClientSize = new Size(Metrics.S(740), Metrics.S(520));
            MinimumSize = new Size(Metrics.S(560), Metrics.S(380));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.P.WindowBg;
            Icon = Assets.Load(Assets.App, 32);
            ShowInTaskbar = true;

            BuildChrome();
            BuildTray();
            ApplyTheme();
            Reload(true);
            FitToContent();

            Theme.Changed += delegate { BeginInvoke((Action)ApplyTheme); };

            // Only poll while the window is on screen; in the tray the app costs nothing.
            _poll = new System.Windows.Forms.Timer { Interval = 3000 };
            _poll.Tick += delegate { BeginScan(false); };

            if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }
        }

        // ------------------------------------------------------------ chrome

        void BuildChrome()
        {
            _list = new ProfileList { Dock = DockStyle.Fill };
            _list.Activated += delegate { LaunchSelected(); };
            _list.SelectionChanged += delegate { SyncButtons(); };
            _list.RowContextMenu += (s, e) => _rowMenu.Show(_list, e.Location);

            _header = new Panel { Dock = DockStyle.Top, Height = Metrics.HeaderHeight };
            _header.Paint += (s, e) => PaintBar(e.Graphics, _header, true);

            _new = Ghost("New profile", delegate { NewProfile(); });
            _settings = Ghost("Settings", delegate { ShowSettings(); });
            _header.Controls.Add(_new);
            _header.Controls.Add(_settings);

            _footer = new Panel { Dock = DockStyle.Bottom, Height = Metrics.FooterHeight };
            _footer.Paint += (s, e) => PaintBar(e.Graphics, _footer, false);

            _status = new Label
            {
                AutoSize = false,
                Font = Theme.FontBody,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
            };
            _footer.Controls.Add(_status);

            _folder = Ghost("Open folder", delegate { OpenFolder(); });
            _edit = Ghost("Edit", delegate { EditSelected(); });
            _open = new FlatButton { Kind = ButtonKind.Primary, Text = "Open profile", Font = Theme.FontBodyBold };
            _open.Click += delegate { LaunchSelected(); };
            _footer.Controls.Add(_folder);
            _footer.Controls.Add(_edit);
            _footer.Controls.Add(_open);

            _rowMenu = new ContextMenuStrip();
            _rowMenu.Items.Add("Open profile", null, delegate { LaunchSelected(); });
            _rowMenu.Items.Add("Edit...", null, delegate { EditSelected(); });
            _rowMenu.Items.Add("Open folder", null, delegate { OpenFolder(); });
            _rowMenu.Items.Add("Create desktop shortcut", null, delegate { MakeShortcut(); });
            _rowMenu.Items.Add(new ToolStripSeparator());
            _rowMenu.Items.Add("Remove...", null, delegate { RemoveSelected(); });

            Controls.Add(_list);
            Controls.Add(_header);
            Controls.Add(_footer);

            Resize += delegate { LayoutBars(); };
            LayoutBars();
            KeyPreview = true;
        }

        FlatButton Ghost(string text, Action onClick)
        {
            var b = new FlatButton { Kind = ButtonKind.Ghost, Text = text };
            b.Click += delegate { onClick(); };
            return b;
        }

        void LayoutBars()
        {
            int g = Metrics.Gutter, h = Metrics.S(30);

            _settings.Size = new Size(Metrics.S(74), h);
            _settings.Location = new Point(_header.Width - g - _settings.Width, (Metrics.HeaderHeight - h) / 2);
            _new.Size = new Size(Metrics.S(98), h);
            _new.Location = new Point(_settings.Left - Metrics.S(8) - _new.Width, _settings.Top);

            int bh = Metrics.S(34);
            _open.Size = new Size(Metrics.S(126), bh);
            _open.Location = new Point(_footer.Width - g - _open.Width, (Metrics.FooterHeight - bh) / 2);
            _edit.Size = new Size(Metrics.S(62), bh);
            _edit.Location = new Point(_open.Left - Metrics.S(8) - _edit.Width, _open.Top);
            _folder.Size = new Size(Metrics.S(96), bh);
            _folder.Location = new Point(_edit.Left - Metrics.S(8) - _folder.Width, _open.Top);

            _status.Location = new Point(g, 0);
            _status.Size = new Size(Math.Max(0, _folder.Left - g - Metrics.S(12)), Metrics.FooterHeight);
        }

        // Hairline separators, no shadows: one elevation language for the whole window (R10).
        void PaintBar(Graphics g, Panel bar, bool header)
        {
            var p = Theme.P;
            g.Clear(header ? p.SurfaceBg : p.WindowBg);
            int y = header ? bar.Height - 1 : 0;
            g.DrawLine(p.PnHairline, 0, y, bar.Width, y);

            if (!header) return;
            Draw.Text(g, "Profiles", Theme.FontTitle, p.Text,
                new Rectangle(Metrics.Gutter, 0, bar.Width, bar.Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int w = TextRenderer.MeasureText("Profiles", Theme.FontTitle).Width;
            Draw.Text(g, _list.Rows.Count.ToString(CultureInfo.InvariantCulture),
                Theme.FontBody, p.TextMuted,
                new Rectangle(Metrics.Gutter + w + Metrics.S(8), 0, Metrics.S(40), bar.Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // Open at the size the content actually needs, then leave resizing to the user.
        void FitToContent()
        {
            int rows = Math.Max(3, _list.Rows.Count);
            int wanted = Metrics.HeaderHeight + Metrics.FooterHeight
                       + rows * Metrics.RowHeight + Metrics.S(8);
            ClientSize = new Size(ClientSize.Width,
                Math.Max(Metrics.S(300), Math.Min(wanted, Metrics.S(700))));
        }

        void ApplyTheme()
        {
            var p = Theme.P;
            BackColor = p.WindowBg;
            SyncStatus();
            _status.BackColor = p.WindowBg;
            _rowMenu.BackColor = p.SurfaceBg;
            _rowMenu.ForeColor = p.Text;
            Theme.ApplyCaption(Handle);
            if (_tray != null) SetTrayIcon();
            Invalidate(true);
            _header.Invalidate();
            _footer.Invalidate();
            _list.Invalidate();
        }

        // ------------------------------------------------------------- tray

        void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Text = "HydraClaude",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip(),
            };
            SetTrayIcon();
            _tray.MouseDoubleClick += delegate { ShowManager(); };
            _tray.ContextMenuStrip.Opening += delegate { BuildTrayMenu(); };
        }

        void SetTrayIcon()
        {
            var old = _tray.Icon;
            _tray.Icon = Assets.Load(Assets.Tray, 16);
            if (old != null) old.Dispose();
        }

        void BuildTrayMenu()
        {
            var m = _tray.ContextMenuStrip;
            m.Items.Clear();
            foreach (var row in _list.Rows)
            {
                var entry = row.Entry;
                var item = new ToolStripMenuItem(entry.Name, null, delegate { Launch(entry); });
                if (row.Running)
                {
                    item.Font = new Font(item.Font, FontStyle.Bold);
                    item.ToolTipText = "Already running - brings the window forward";
                }
                m.Items.Add(item);
            }
            if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Open manager", null, delegate { ShowManager(); });
            m.Items.Add("Exit HydraClaude", null, delegate { QuitApp(); });
        }

        public void ShowManager()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            BeginScan(true);
        }

        void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (!_store.Config.TrayHintShown)
            {
                _store.Config.TrayHintShown = true;
                SaveQuietly();
                _tray.ShowBalloonTip(5000, "HydraClaude is still running",
                    "Find it with the hidden icons arrow. Right-click it to open a profile.",
                    ToolTipIcon.None);
            }
        }

        void QuitApp()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window parks the app in the notification area; Exit on the tray menu
            // is the real quit. The one-time balloon above makes that contract visible (H1).
            //
            // Anything that isn't Windows shutting us down counts as "the user closed the
            // window": a raw WM_CLOSE arrives as CloseReason.None, not UserClosing, so testing
            // for UserClosing alone quietly exits instead of hiding.
            bool systemClose = e.CloseReason == CloseReason.ApplicationExitCall
                            || e.CloseReason == CloseReason.WindowsShutDown
                            || e.CloseReason == CloseReason.TaskManagerClosing;
            if (!systemClose && _tray != null)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShowMessage && ShowMessage != 0) { ShowManager(); return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyCaption(Handle);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_poll == null) return;
            if (Visible) { _poll.Start(); BeginScan(true); }
            else _poll.Stop();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: HideToTray(); return true;
                case Keys.F5: Reload(true); return true;
                case Keys.F2: EditSelected(); return true;
                case Keys.Delete: RemoveSelected(); return true;
                case Keys.Control | Keys.N: NewProfile(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------ state

        void Reload(bool rescan)
        {
            _pkg = Packaging.Resolve(_store.Config);
            RebuildRows();
            SyncStatus();
            if (rescan) BeginScan(true);
        }

        void RebuildRows()
        {
            var keep = _list.Selected != null ? _list.Selected.Entry.Name : null;
            var rows = _store.Config.Profiles.Select(p => new ProfileRow
            {
                Entry = p,
                Running = _tracker.IsRunning(p),
                Path = p.IsStock ? Store.StockDir : p.Dir,
                When = FormatWhen(p.LastLaunched),
            }).ToList();
            _list.SetRows(rows, keep);
            _header.Invalidate();
            SyncButtons();
        }

        void SyncButtons()
        {
            bool any = _list.Selected != null;
            _open.Enabled = any;
            _edit.Enabled = any;
            _folder.Enabled = any;
            foreach (ToolStripItem i in _rowMenu.Items) i.Enabled = any;
        }

        void SyncStatus()
        {
            if (_pkg.Source == "package")
                _status.Text = "Claude " + _pkg.Version + " detected";
            else if (_pkg.Source == "override")
                _status.Text = "Using the AUMID set in Settings";
            else
                _status.Text = "Claude not detected — falling back to a built-in AUMID. Check Settings.";
            _status.ForeColor = _pkg.Source == "fallback" ? Theme.P.Accent : Theme.P.TextMuted;
        }

        static string FormatWhen(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            DateTime dt;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out dt)) return "";
            var local = dt.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm")
                : local.ToString("d MMM, HH:mm");
        }

        void BeginScan(bool force)
        {
            if (_scanning) return;
            _scanning = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool changed = false;
                try { changed = _tracker.Refresh(force); }
                catch { }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        _scanning = false;
                        if (changed) RebuildRows();
                    });
                }
                catch { _scanning = false; }
            });
        }

        void SaveQuietly()
        {
            try { _store.Save(); }
            catch (Exception ex)
            {
                Alert("Could not save settings",
                    "HydraClaude could not write " + Store.ConfigPath + "\n\n" + ex.Message);
            }
        }

        void Alert(string title, string body)
        {
            MessageBox.Show(this, body, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------------------------------------------------------- actions

        void LaunchSelected()
        {
            if (_list.Selected == null) return;
            Launch(_list.Selected.Entry);
        }

        void Launch(ProfileEntry p)
        {
            Cursor = Cursors.WaitCursor;
            var err = Launcher.Launch(p, _pkg);
            Cursor = Cursors.Default;

            if (err != null)
            {
                MessageBox.Show(this, err, "HydraClaude", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SaveQuietly();
            RebuildRows();
            BeginScan(true);
        }

        void NewProfile()
        {
            var p = new ProfileEntry { Name = "", Dir = "", ExtraArgs = "", LastLaunched = "" };
            using (var dlg = new ProfileDialog(p, _store, true))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _store.Config.Profiles.Add(p);
                SaveQuietly();
                RebuildRows();
                for (int i = 0; i < _list.Rows.Count; i++)
                    if (_list.Rows[i].Entry == p) _list.SelectedIndex = i;
            }
        }

        void EditSelected()
        {
            if (_list.Selected == null) return;
            using (var dlg = new ProfileDialog(_list.Selected.Entry, _store, false))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SaveQuietly();
                RebuildRows();
            }
        }

        void RemoveSelected()
        {
            if (_list.Selected == null) return;
            var p = _list.Selected.Entry;

            using (var dlg = new RemoveDialog(p))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (dlg.DeleteFiles && !p.IsStock && Directory.Exists(p.Dir))
                {
                    try
                    {
                        // Recycle Bin, never a hard delete - a profile folder holds real history.
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            p.Dir,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    catch (Exception ex)
                    {
                        Alert("Folder kept",
                            "The profile was removed from the list, but its folder could not be "
                            + "moved to the Recycle Bin.\n\n" + ex.Message);
                    }
                }

                _store.Config.Profiles.Remove(p);
                SaveQuietly();
                RebuildRows();
            }
        }

        void OpenFolder()
        {
            if (_list.Selected == null) return;
            var p = _list.Selected.Entry;
            var dir = p.IsStock ? Store.StockDir : p.Dir;
            try
            {
                if (!Directory.Exists(dir))
                {
                    if (p.IsStock)
                    {
                        Alert("Nothing there yet",
                            "The stock profile keeps its data in\n\n" + dir
                            + "\n\nwhich Claude has not created yet.");
                        return;
                    }
                    Directory.CreateDirectory(dir);
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Alert("Could not open the folder", ex.Message); }
        }

        void MakeShortcut()
        {
            if (_list.Selected == null) return;
            try
            {
                var lnk = Shortcuts.Create(_list.Selected.Entry, Application.ExecutablePath, Assets.App);
                MessageBox.Show(this, "Created:\n\n" + lnk, "Desktop shortcut",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Alert("Could not create the shortcut", ex.Message); }
        }

        void ShowSettings()
        {
            using (var dlg = new SettingsDialog(_store.Config))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SaveQuietly();
                Reload(true);
            }
        }
    }

    // ------------------------------------------------------- themed dialogs

    public class Sheet : Form
    {
        protected Sheet(string title, int w, int h)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Metrics.S(w), Metrics.S(h));
            Font = Theme.FontBody;
            BackColor = Theme.P.WindowBg;
            ForeColor = Theme.P.Text;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyCaption(Handle);
        }

        protected Label Caption(string text, int x, int y, int w, bool muted)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(Metrics.S(x), Metrics.S(y)),
                Size = new Size(Metrics.S(w), Metrics.S(18)),
                ForeColor = muted ? Theme.P.TextMuted : Theme.P.Text,
                Font = muted ? Theme.FontBody : Theme.FontBodyBold,
                UseMnemonic = false,
            };
            Controls.Add(l);
            return l;
        }

        protected Label Hint(string text, int x, int y, int w, int h)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(Metrics.S(x), Metrics.S(y)),
                Size = new Size(Metrics.S(w), Metrics.S(h)),
                ForeColor = Theme.P.TextMuted,
                Font = Theme.FontBody,
                UseMnemonic = false,
            };
            Controls.Add(l);
            return l;
        }

        // A borderless TextBox inside a painted frame: WinForms draws FixedSingle with the
        // system frame colour, which is far too bright against the dark palette.
        protected TextBox Field(int x, int y, int w, string value)
        {
            var frame = new Panel
            {
                Location = new Point(Metrics.S(x), Metrics.S(y)),
                Size = new Size(Metrics.S(w), Metrics.S(26)),
                BackColor = Theme.P.SurfaceBg,
            };
            var t = new TextBox
            {
                Location = new Point(Metrics.S(7), Metrics.S(5)),
                Size = new Size(Metrics.S(w) - Metrics.S(14), Metrics.S(18)),
                Text = value ?? "",
                BorderStyle = BorderStyle.None,
                BackColor = Theme.P.SurfaceBg,
                ForeColor = Theme.P.Text,
                Font = Theme.FontBody,
            };
            frame.Controls.Add(t);
            frame.Paint += delegate(object s, PaintEventArgs e)
            {
                var r = new Rectangle(0, 0, frame.Width - 1, frame.Height - 1);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Draw.RoundRect(r, Metrics.S(4)))
                using (var pen = new Pen(t.Focused ? Theme.P.Accent : Theme.P.Hairline))
                    e.Graphics.DrawPath(pen, path);
            };
            t.Enter += delegate { frame.Invalidate(); };
            t.Leave += delegate { frame.Invalidate(); };
            t.EnabledChanged += delegate { frame.BackColor = t.BackColor = Theme.P.SurfaceBg; };
            Controls.Add(frame);
            return t;
        }

        protected FlatButton Action(string text, int x, int y, int w, ButtonKind kind)
        {
            var b = new FlatButton
            {
                Text = text,
                Kind = kind,
                Location = new Point(Metrics.S(x), Metrics.S(y)),
                Size = new Size(Metrics.S(w), Metrics.S(32)),
            };
            Controls.Add(b);
            return b;
        }
    }

    public class ProfileDialog : Sheet
    {
        readonly ProfileEntry _p;
        readonly Store _store;
        readonly string _originalName;

        TextBox _name, _dir, _args;
        CheckBox _stock;
        FlatButton _browse;

        // The stock entry is the one Claude installed; a new profile is always its own folder,
        // so that choice is simply absent when creating one (Rams 10 - don't offer an option
        // that has one right answer).
        public ProfileDialog(ProfileEntry p, Store store, bool isNew)
            : base(isNew ? "New profile" : "Edit profile", 540, isNew ? 230 : 262)
        {
            _p = p; _store = store; _originalName = p.Name;

            Caption("Name", 20, 22, 100, false);
            _name = Field(130, 18, 390, p.Name);
            _name.TextChanged += delegate { SyncDefaultDir(); };

            int y = 18;
            if (!isNew)
            {
                _stock = new CheckBox
                {
                    Text = "Use the stock data folder (the normal Claude install)",
                    Location = new Point(Metrics.S(130), Metrics.S(50)),
                    Size = new Size(Metrics.S(390), Metrics.S(22)),
                    Checked = p.IsStock,
                    ForeColor = Theme.P.Text,
                    Font = Theme.FontBody,
                    FlatStyle = FlatStyle.Flat,
                };
                _stock.CheckedChanged += delegate { ApplyStockState(); };
                Controls.Add(_stock);
                y = 50;
            }

            Caption("Data folder", 20, y + 36, 100, false);
            _dir = Field(130, y + 32, 300, p.IsStock ? Store.DefaultDirFor(p.Name) : p.Dir);
            _browse = Action("Browse", 440, y + 30, 80, ButtonKind.Ghost);
            _browse.Click += delegate { Browse(); };

            Caption("Extra args", 20, y + 72, 100, false);
            _args = Field(130, y + 68, 390, p.ExtraArgs);

            Hint("Each profile is its own Claude login, history and settings. Nothing here lives "
               + "inside the Claude package, so an app update never touches it.",
                 130, y + 100, 390, 36);

            var ok = Action(isNew ? "Create" : "Save", 350, y + 162, 84, ButtonKind.Primary);
            var cancel = Action("Cancel", 442, y + 162, 78, ButtonKind.Ghost);
            ok.Click += delegate { if (Commit()) DialogResult = DialogResult.OK; };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            CancelButton = null;
            AcceptButton = null;

            ApplyStockState();
            ActiveControl = _name;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter && !(ActiveControl is FlatButton))
            {
                if (Commit()) DialogResult = DialogResult.OK;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool UseStock { get { return _stock != null && _stock.Checked; } }

        void ApplyStockState()
        {
            _dir.Enabled = !UseStock;
            _browse.Enabled = !UseStock;
            _dir.ForeColor = UseStock ? Theme.P.TextMuted : Theme.P.Text;
        }

        // While the folder is still the default for the old name, follow the name as it's typed
        // - one less thing to keep in your head (cognitive load: offload it).
        void SyncDefaultDir()
        {
            if (UseStock) return;
            var currentDefault = Store.DefaultDirFor(_originalName ?? "");
            if (string.IsNullOrEmpty(_dir.Text)
                || string.Equals(_dir.Text, currentDefault, StringComparison.OrdinalIgnoreCase)
                || (_dir.Text.StartsWith(Store.Root, StringComparison.OrdinalIgnoreCase)
                    && !Directory.Exists(_dir.Text)))
            {
                _dir.Text = Store.DefaultDirFor(_name.Text.Trim());
            }
        }

        void Browse()
        {
            using (var fb = new FolderBrowserDialog
            {
                Description = "Choose the data folder for this profile",
                SelectedPath = Directory.Exists(_dir.Text) ? _dir.Text : Store.Root,
            })
            {
                if (fb.ShowDialog(this) == DialogResult.OK) _dir.Text = fb.SelectedPath;
            }
        }

        bool Commit()
        {
            var name = _name.Text.Trim();
            if (name.Length == 0) return Fail("Give the profile a name.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return Fail("A profile name cannot contain \\ / : * ? \" < > |");

            var clash = _store.Find(name);
            if (clash != null && clash != _p)
                return Fail("There is already a profile called \"" + name + "\".");

            var dir = UseStock ? "" : _dir.Text.Trim().TrimEnd('\\');
            if (!UseStock)
            {
                if (dir.Length == 0)
                    return Fail("Choose a data folder, or tick the stock data folder box.");
                try { dir = Path.GetFullPath(dir); }
                catch { return Fail("\"" + dir + "\" is not a valid folder path."); }

                var taken = _store.Config.Profiles.FirstOrDefault(
                    x => x != _p && !x.IsStock
                      && string.Equals(RunningScan.Normalize(x.Dir), RunningScan.Normalize(dir),
                                       StringComparison.OrdinalIgnoreCase));
                if (taken != null)
                    return Fail("That folder already belongs to the \"" + taken.Name
                              + "\" profile. Two profiles cannot share one folder.");

                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { return Fail("Could not create " + dir + "\n\n" + ex.Message); }
            }

            _p.Name = name;
            _p.Dir = dir;
            _p.ExtraArgs = _args.Text.Trim();
            return true;
        }

        bool Fail(string message)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    public class RemoveDialog : Sheet
    {
        CheckBox _delete;
        public bool DeleteFiles { get { return _delete.Checked && _delete.Enabled; } }

        public RemoveDialog(ProfileEntry p) : base("Remove profile", 460, 200)
        {
            var title = Caption("Remove \"" + p.Name + "\" from HydraClaude?", 20, 22, 420, false);
            title.Font = Theme.FontRow;
            title.Size = new Size(Metrics.S(420), Metrics.S(24));

            Hint(p.IsStock
                    ? "This removes the entry only. The Claude installation is untouched."
                    : "This removes the entry only. The folder and everything in it stays where it is.",
                 20, 54, 420, 36);

            _delete = new CheckBox
            {
                Text = p.IsStock ? "No folder to delete" : "Also send the folder to the Recycle Bin",
                Location = new Point(Metrics.S(20), Metrics.S(96)),
                Size = new Size(Metrics.S(420), Metrics.S(22)),
                Enabled = !p.IsStock && Directory.Exists(p.Dir),
                ForeColor = Theme.P.Text,
                Font = Theme.FontBody,
                FlatStyle = FlatStyle.Flat,
            };
            Controls.Add(_delete);

            if (!p.IsStock) Hint(p.Dir, 40, 118, 400, 18);

            var remove = Action("Remove", 274, 150, 84, ButtonKind.Primary);
            var cancel = Action("Cancel", 366, 150, 74, ButtonKind.Ghost);
            remove.Click += delegate { DialogResult = DialogResult.OK; };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            ActiveControl = cancel;     // destructive action never has default focus (H5)
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    public class SettingsDialog : Sheet
    {
        readonly HydraConfig _cfg;
        TextBox _family, _aumid;
        Label _detected;

        public SettingsDialog(HydraConfig cfg) : base("Settings", 560, 320)
        {
            _cfg = cfg;

            Hint("HydraClaude finds the installed Claude package every time it runs, so app "
               + "updates cannot break it. Change these only if detection fails.",
                 20, 18, 520, 34);

            Caption("Package family", 20, 66, 110, false);
            _family = Field(140, 62, 400, cfg.PackageFamilyName);

            Caption("AUMID override", 20, 102, 110, false);
            _aumid = Field(140, 98, 400, cfg.AumidOverride);

            var detect = Action("Detect now", 140, 130, 96, ButtonKind.Ghost);
            detect.Click += delegate { Detect(); };

            _detected = new Label
            {
                Location = new Point(Metrics.S(140), Metrics.S(172)),
                Size = new Size(Metrics.S(400), Metrics.S(74)),
                ForeColor = Theme.P.TextMuted,
                Font = Theme.FontBody,
                UseMnemonic = false,
            };
            Controls.Add(_detected);
            Caption("Detected", 20, 172, 110, false);

            Hint(Store.ConfigPath, 20, 254, 520, 18);

            var ok = Action("Save", 372, 274, 78, ButtonKind.Primary);
            var cancel = Action("Cancel", 458, 274, 78, ButtonKind.Ghost);
            ok.Click += delegate
            {
                _cfg.PackageFamilyName = _family.Text.Trim();
                _cfg.AumidOverride = _aumid.Text.Trim();
                DialogResult = DialogResult.OK;
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

            Detect();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Detect()
        {
            var info = Packaging.Resolve(new HydraConfig
            {
                PackageFamilyName = _family.Text.Trim(),
                AumidOverride = "",
            });

            var lines = new List<string>
            {
                "AUMID     " + info.Aumid + "   (" + info.Source + ")",
                "Package   " + (info.FullName ?? "-"),
                "Version   " + (info.Version ?? "-"),
            };
            if (info.Error != null) lines.Add("Note      " + info.Error);
            _detected.Text = string.Join(Environment.NewLine, lines.ToArray());
            _detected.ForeColor = info.Resolved ? Theme.P.TextMuted : Theme.P.Accent;
        }
    }

    // ---------------------------------------------------------- entry point

    public static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

        static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        [STAThread]
        public static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string profileArg = null;
            bool forceGui = false;
            foreach (var a in args)
            {
                if (string.IsNullOrEmpty(a)) continue;
                if (a.Equals("--gui", StringComparison.OrdinalIgnoreCase)
                    || a.Equals("/gui", StringComparison.OrdinalIgnoreCase)) { forceGui = true; continue; }
                if (a.StartsWith("-") || a.StartsWith("/")) continue;
                if (profileArg == null) profileArg = a.Trim();
            }

            Store store;
            try { store = Store.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("HydraClaude could not read its configuration.\n\n" + ex.Message,
                    "HydraClaude", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // The launch path stays deliberately thin: no window, no WMI, no theme work.
            if (!forceGui && profileArg != null) return LaunchDirect(store, profileArg);

            bool fresh;
            using (var single = new Mutex(true, "HydraClaude.Manager." + Environment.UserName, out fresh))
            {
                if (!fresh)
                {
                    // Already parked in the tray - wake that one instead of starting a second.
                    PostMessage(HWND_BROADCAST, MainForm.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Metrics.Scale = g.DpiX / 96f;
                Application.Run(new MainForm(store, false));
                return 0;
            }
        }

        // `HydraClaude.exe <name>` - what the desktop shortcuts call. An unknown name is
        // created on the spot so a hand-made shortcut still works.
        static int LaunchDirect(Store store, string name)
        {
            var p = store.Find(name);
            if (p == null)
            {
                p = new ProfileEntry
                {
                    Name = name,
                    Dir = Store.DefaultDirFor(name),
                    ExtraArgs = "",
                    LastLaunched = "",
                };
                store.Config.Profiles.Add(p);
            }

            var pkg = Packaging.Resolve(store.Config);
            var err = Launcher.Launch(p, pkg);
            try { store.Save(); }
            catch { }

            if (err != null)
            {
                MessageBox.Show(err, "Claude - " + name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
    }
}
