using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TinySticky {
    internal sealed class StickyForm : Form {
        static readonly Color Good = Color.FromArgb(27, 127, 58);
        static readonly Color Bad = Color.FromArgb(198, 40, 40);
        static readonly Color Warn = Color.FromArgb(191, 98, 0);
        static readonly TextFormatFlags LeftText =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        static readonly TextFormatFlags RightText =
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        static readonly TextFormatFlags WrapText =
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        readonly WindowSettings _settings;
        readonly bool _persist;
        readonly List<DrawText> _texts = new List<DrawText>();
        readonly List<DrawDot> _dots = new List<DrawDot>();
        readonly List<RowHit> _hits = new List<RowHit>();
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly Timer _scanTimer = new Timer();

        bool _anchored;
        bool _suppress;
        bool _collapsed;
        bool _scanning;
        bool _inLayout;
        bool _ready;
        bool _balloonShown;
        Board _board;
        NotifyIcon _tray;
        Bitmap _iconBitmap;
        Icon _icon;
        Font _titleFont;
        Font _bodyFont;
        Font _boldFont;
        Font _smallFont;
        int _headerHeight;
        int _footerHeight;
        int _fontKey = -1;
        float _factor = 1f;
        Rectangle _titleBounds;
        Rectangle _noteBounds;
        string _note = "";
        bool _editingNote;
        TextBox _noteEditor;
        Point _pressAt;
        bool _pressTitle;
        bool _showAllUnknown;
        bool _growUnknown;
        string _editingKey;
        TextBox _editor;
        Color Paper;
        Color Band;
        Color Ink;
        Color Muted;
        Color Line;

        internal StickyForm(WindowSettings settings, bool persist) {
            _settings = settings ?? WindowSettings.CreateDefault();
            _persist = persist;
            _anchored = !_settings.hasPosition;
            _collapsed = _settings.collapsed;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = _settings.topMost;
            Opacity = _settings.opacity >= 0.55 ? _settings.opacity : 0.88;
            ApplyPalette();
            BackColor = Paper;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            Text = "Tiny";
            ClientSize = new Size(480, 360);
            if (_settings.hasPosition) Location = new Point(_settings.x, _settings.y);
            _note = SharedNote.Load();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
            _menu.Opening += delegate { RebuildMenu(); };
            if (_persist) {
                EnsureChrome();
                _tray = new NotifyIcon();
                _tray.Icon = _icon;
                _tray.Visible = true;
                _tray.Text = "Tiny 服务便利贴";
                _tray.ContextMenuStrip = _menu;
                _tray.DoubleClick += delegate { ToggleCollapsed(); };
                _scanTimer.Interval = 5000;
                _scanTimer.Tick += delegate { StartScan(); };
            }
        }

        internal void SetBoard(Board board) {
            _board = board;
            if (!_editingNote) _note = SharedNote.Load();
            if (!IsHandleCreated) {
                IntPtr handle = Handle;
                if (handle == IntPtr.Zero) return;
            }
            Relayout();
            UpdateTrayTip();
        }

        internal void Render(Graphics graphics) {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Paper);
            using (var band = new SolidBrush(Band)) {
                graphics.FillRectangle(band, 0, 0, width, _headerHeight);
                if (_footerHeight > 0)
                    graphics.FillRectangle(band, 0, height - _footerHeight, width, _footerHeight);
            }
            using (var pen = new Pen(Line)) {
                graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
                if (_headerHeight > 0)
                    graphics.DrawLine(pen, 0, _headerHeight - 1, width, _headerHeight - 1);
                if (_footerHeight > 0)
                    graphics.DrawLine(pen, 0, height - _footerHeight, width, height - _footerHeight);
            }
            if (_footerHeight > 0 && _smallFont != null) {
                var hint = new Rectangle(Ui(18), height - _footerHeight, Math.Max(1, width - Ui(64)), _footerHeight);
                TextRenderer.DrawText(graphics, "拖动标题移动 · 拖边缘改大小 · 双击收起", _smallFont, hint, Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            int grip = Math.Max(4, Ui(5));
            using (var pen = new Pen(Muted, Math.Max(1, Ui(1)))) {
                for (int i = 1; i <= 3; i++) {
                    int offset = grip * i;
                    graphics.DrawLine(pen, width - offset - Ui(4), height - Ui(6), width - Ui(6), height - offset - Ui(4));
                }
            }
            foreach (DrawDot dot in _dots) {
                using (var brush = new SolidBrush(dot.Color))
                    graphics.FillEllipse(brush, dot.Bounds);
            }
            foreach (DrawText item in _texts)
                TextRenderer.DrawText(graphics, item.Text, item.Font, item.Bounds, item.Color, item.Flags);
        }

        protected override CreateParams CreateParams {
            get {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void WndProc(ref Message m) {
            const int wmNcHitTest = 0x84;
            const int wmExitSizeMove = 0x232;
            if (m.Msg == wmExitSizeMove && _ready && _persist) Persist();
            if (m.Msg == wmNcHitTest && !_settings.clickThrough) {
                base.WndProc(ref m);
                if (m.Result == (IntPtr)1) m.Result = HitTestEdge(m.LParam);
                return;
            }
            base.WndProc(ref m);
        }

        IntPtr HitTestEdge(IntPtr lParam) {
            int packed = lParam.ToInt32();
            var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            Point p = PointToClient(screen);
            int grip = Math.Max(8, Scale(10));
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            bool left = p.X <= grip;
            bool right = p.X >= w - grip;
            bool top = p.Y <= grip;
            bool bottom = p.Y >= h - grip;
            if (top && left) return (IntPtr)13;
            if (top && right) return (IntPtr)14;
            if (bottom && left) return (IntPtr)16;
            if (bottom && right) return (IntPtr)17;
            if (left) return (IntPtr)10;
            if (right) return (IntPtr)11;
            if (top) return (IntPtr)12;
            if (bottom) return (IntPtr)15;
            return (IntPtr)1;
        }

        protected override void OnResize(EventArgs e) {
            base.OnResize(e);
            if (_inLayout || !_ready) return;
            if (_collapsed && ClientSize.Height > _headerHeight + Scale(16)) {
                _collapsed = false;
                _settings.collapsed = false;
            }
            _settings.hasSize = true;
            _settings.width = ClientSize.Width;
            if (!_collapsed) _settings.height = ClientSize.Height;
            Relayout();
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            try {
                int preference = 2;
                DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            } catch {
            }
            ApplyClickThrough();
        }

        protected override void OnLoad(EventArgs e) {
            base.OnLoad(e);
            Relayout();
            if (_anchored) PlaceBottomRight();
            else KeepVisible();
            _ready = true;
        }

        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            if (!_persist) return;
            _scanTimer.Start();
            StartScan();
        }

        protected override void OnPaintBackground(PaintEventArgs e) {
        }

        protected override void OnPaint(PaintEventArgs e) {
            Render(e.Graphics);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e) {
            base.OnDpiChanged(e);
            DisposeFonts();
            Relayout();
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                _pressAt = e.Location;
                _pressTitle = _titleBounds.Contains(e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            if (_noteBounds.Contains(e.Location)) Cursor = Cursors.IBeam;
            else if (HitAt(e.Location) != null) Cursor = Cursors.Hand;
            else Cursor = _titleBounds.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
            if (_pressTitle && e.Button == MouseButtons.Left) {
                int dx = Math.Abs(e.X - _pressAt.X);
                int dy = Math.Abs(e.Y - _pressAt.Y);
                if (dx + dy > 4) {
                    _pressTitle = false;
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) {
            bool dragged = Math.Abs(e.X - _pressAt.X) + Math.Abs(e.Y - _pressAt.Y) > 4;
            _pressTitle = false;
            if (e.Button == MouseButtons.Right) _menu.Show(this, e.Location);
            else if (e.Button == MouseButtons.Left && !dragged) ActivateHit(e.Location);
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) {
            if (e.Button == MouseButtons.Left && _noteBounds.Contains(e.Location)) BeginNoteEdit();
            else if (e.Button == MouseButtons.Left && _titleBounds.Contains(e.Location)) ToggleCollapsed();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnLocationChanged(EventArgs e) {
            base.OnLocationChanged(e);
            if (_suppress || !_ready || !_persist) return;
            _anchored = false;
            _settings.hasPosition = true;
            _settings.x = Left;
            _settings.y = Top;
            SettingsStore.Save(_settings);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) {
            _scanTimer.Stop();
            if (_tray != null) {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                _scanTimer.Dispose();
                _menu.Dispose();
                DisposeFonts();
                try { if (_icon != null) _icon.Dispose(); } catch { }
                if (_iconBitmap != null) _iconBitmap.Dispose();
            }
            base.Dispose(disposing);
        }

        void StartScan() {
            if (_scanning) return;
            _scanning = true;
            System.Threading.Tasks.Task.Run(delegate {
                Board board;
                try {
                    board = BoardBuilder.Build();
                } catch (Exception ex) {
                    Program.Log(ex);
                    board = new Board();
                    board.ScanFailed = true;
                    board.CatalogError = ex.Message;
                    board.ScannedAt = DateTime.Now;
                }
                try {
                    BeginInvoke(new Action(delegate {
                        _scanning = false;
                        if (IsDisposed) return;
                        SetBoard(board);
                    }));
                } catch (InvalidOperationException) {
                    _scanning = false;
                }
            });
        }

        void Relayout() {
            if (_inLayout) return;
            _inLayout = true;
            try {
                _hits.Clear();
                int width = TargetWidth();
                _factor = width / (float)Math.Max(1, Scale(480));
                if (_factor < 0.55f) _factor = 0.55f;
                if (_factor > 2.4f) _factor = 2.4f;
                EnsureFonts();
                _texts.Clear();
                _dots.Clear();
                int pad = Ui(18);
                int titleH = Math.Max(_titleFont.Height, Ui(28));
                int bodyH = Math.Max(_bodyFont.Height, Ui(22));
                int smallH = Math.Max(_smallFont.Height, Ui(18));
                int row = bodyH + Ui(6);
                int noteH = Math.Max(_bodyFont.Height, Ui(24));
                int header = Ui(8) + titleH + Ui(4) + noteH + Ui(8);
                _headerHeight = header;
                _footerHeight = _collapsed ? 0 : Math.Max(smallH + Ui(14), Ui(36));
                int edge = Ui(10);
                int titleRow = Math.Max(1, Ui(8) + titleH);
                _titleBounds = new Rectangle(edge, edge, Math.Max(1, width - edge * 2), Math.Max(1, titleRow - edge));
                _noteBounds = new Rectangle(pad, Ui(8) + titleH + Ui(2), Math.Max(1, width - pad * 2), noteH);

                AddText("Tiny", _titleFont, Ink, new Rectangle(pad, Ui(8), width - pad * 2 - Ui(120), titleH), LeftText);
                string clock = _board == null ? "检测中" : _board.ScannedAt.ToString("HH:mm:ss");
                AddText(clock, _smallFont, Muted, new Rectangle(width - pad - Ui(110), Ui(10), Ui(110), smallH), RightText);
                string noteText;
                Color noteColor;
                Font noteFont;
                if (!string.IsNullOrEmpty(_note)) {
                    noteText = _note;
                    noteColor = Ink;
                    noteFont = _boldFont;
                } else if (_collapsed) {
                    noteText = Summary(_board);
                    noteColor = Muted;
                    noteFont = _smallFont;
                } else {
                    noteText = "双击写提醒";
                    noteColor = Muted;
                    noteFont = _smallFont;
                }
                AddText(noteText, noteFont, noteColor, _noteBounds, LeftText);

                int y = header + Scale(8);
                if (!_collapsed) {
                    if (_board != null && !string.IsNullOrEmpty(_board.CatalogError)) {
                        Size size = TextRenderer.MeasureText(_board.CatalogError, _smallFont, new Size(width - pad * 2, 0), WrapText);
                        int errorH = Math.Max(size.Height, smallH);
                        AddText(_board.CatalogError, _smallFont, Bad, new Rectangle(pad, y, width - pad * 2, errorH), WrapText);
                        y += errorH + Ui(8);
                    }
                    if (_board == null) {
                        AddText("正在检测本机端口…", _bodyFont, Muted, new Rectangle(pad, y, width - pad * 2, row), LeftText);
                        y += row;
                    } else {
                        foreach (ServiceView service in _board.Services)
                            y = DrawService(service, pad, y, width, row);
                        y = DrawUnknown(pad, y, width, row, smallH);
                    }
                }

                int needed = _collapsed ? header : y + _footerHeight;
                int height = header;
                if (!_collapsed) {
                    height = _settings.hasSize ? TargetHeight() : Math.Max(header + _footerHeight, needed);
                    if (_growUnknown && height < needed) {
                        int max = WorkingSize().Height - Scale(24);
                        height = max >= Scale(220) && needed > max ? max : needed;
                        _settings.hasSize = true;
                        _settings.width = width;
                        _settings.height = height;
                    }
                    if (!_settings.hasSize) {
                        int max = WorkingSize().Height - Scale(24);
                        if (max >= Scale(220) && height > max) height = max;
                    }
                }
                _growUnknown = false;
                Size minimum = new Size(Scale(300), _collapsed ? Math.Max(1, header) : Scale(220));
                if (MinimumSize != minimum) MinimumSize = minimum;
                if (ClientSize.Width != width || ClientSize.Height != height)
                    ClientSize = new Size(width, height);
                if (_ready && _persist && _anchored) PlaceBottomRight();
                else if (_ready && _persist) KeepFullyVisible();
                PlaceEditor();
                PlaceNoteEditor();
                Invalidate();
            } finally {
                _inLayout = false;
            }
        }

        int DrawService(ServiceView service, int pad, int y, int width, int row) {
            Color stateColor = ColorFor(service.State);
            int dot = Ui(10);
            _dots.Add(new DrawDot { Color = stateColor, Bounds = new Rectangle(pad, y + Math.Max(0, (row - dot) / 2), dot, dot) });
            int nameX = pad + Ui(18);
            int stateW = Ui(210);
            AddText(service.Name ?? "", _boldFont, Ink, new Rectangle(nameX, y, width - nameX - pad - stateW, row), LeftText);
            AddText(StateLabel(service), _bodyFont, stateColor, new Rectangle(width - pad - stateW, y, stateW, row), RightText);
            y += row;

            int x = pad;
            int chipY = y;
            int lineH = row;
            int limit = width - pad;
            foreach (PortView port in service.Ports) {
                string text = ChipText(port);
                Size measured = TextRenderer.MeasureText(text, _bodyFont, new Size(int.MaxValue, lineH), LeftText);
                int chipW = measured.Width + Ui(8);
                if (x > pad && x + chipW > limit) {
                    x = pad;
                    chipY += lineH;
                }
                AddText(text, _bodyFont, port.Listening ? Ink : Bad, new Rectangle(x, chipY, chipW, lineH), LeftText);
                x += chipW + Ui(8);
            }
            return chipY + lineH + Ui(8);
        }

        int DrawUnknown(int pad, int y, int width, int row, int smallH) {
            List<UnknownGroup> groups = GroupUnknown(_board.Unknown);
            if (groups.Count == 0) return y;
            AddText("未登记", _boldFont, Muted, new Rectangle(pad, y, width - pad * 2 - Ui(180), row), LeftText);
            AddText("点击改名，回车保存", _smallFont, Muted, new Rectangle(width - pad - Ui(180), y, Ui(180), row), RightText);
            y += row;
            int shown = _showAllUnknown ? groups.Count : Math.Min(6, groups.Count);
            for (int i = 0; i < shown; i++) {
                UnknownGroup group = groups[i];
                string who = group.Name.StartsWith("pid ") ? "进程 " + group.Name.Substring(4) : group.Name;
                string ports = group.PortText();
                if (group.LoopbackOnly) ports += "  仅本机";
                int nameW = TextRenderer.MeasureText(who, _bodyFont, new Size(int.MaxValue, row), LeftText).Width + Ui(12);
                int maxName = Math.Max(Ui(80), (width - pad * 2) / 2);
                if (nameW > maxName) nameW = maxName;
                var rowBounds = new Rectangle(pad, y, width - pad * 2, row);
                var nameBounds = new Rectangle(pad, y, Math.Max(nameW, Ui(140)), row);
                AddText(who, _bodyFont, Ink, new Rectangle(pad, y, nameW, row), LeftText);
                AddText(ports, _bodyFont, Ink, new Rectangle(pad + nameW, y, width - pad * 2 - nameW, row), LeftText);
                _hits.Add(new RowHit { Kind = "edit", Bounds = rowBounds, EditorBounds = nameBounds, Group = group });
                y += row;
            }
            int rest = groups.Count - shown;
            if (rest > 0 || (_showAllUnknown && groups.Count > 6)) {
                string more = _showAllUnknown ? "收起" : "还有 " + rest + " 个进程";
                var moreBounds = new Rectangle(pad, y, width - pad * 2, smallH);
                AddText(more, _smallFont, Muted, moreBounds, LeftText);
                _hits.Add(new RowHit { Kind = "more", Bounds = moreBounds });
                y += smallH + Ui(4);
            }
            return y;
        }

        static List<UnknownGroup> GroupUnknown(List<UnknownView> items) {
            var groups = new List<UnknownGroup>();
            foreach (UnknownView item in items) {
                string name = string.IsNullOrEmpty(item.ProcessName) ? "pid " + item.Pid : item.ProcessName;
                UnknownGroup group = null;
                foreach (UnknownGroup existing in groups) {
                    if (existing.Name == name) {
                        group = existing;
                        break;
                    }
                }
                if (group == null) {
                    group = new UnknownGroup();
                    group.Name = name;
                    group.LoopbackOnly = true;
                    groups.Add(group);
                }
                group.Ports.Add(item.Port);
                if (!item.LoopbackOnly) group.LoopbackOnly = false;
            }
            return groups;
        }

        void RebuildMenu() {
            while (_menu.Items.Count > 0) {
                ToolStripItem item = _menu.Items[0];
                _menu.Items.RemoveAt(0);
                item.Dispose();
            }
            _menu.Items.Add(Item("刷新", delegate { StartScan(); }));
            _menu.Items.Add(CheckItem("置顶", TopMost, delegate {
                TopMost = !TopMost;
                _settings.topMost = TopMost;
                Persist();
            }));
            _menu.Items.Add(CheckItem("穿透点击", _settings.clickThrough, delegate { ToggleClickThrough(); }));
            _menu.Items.Add(CheckItem(_collapsed ? "展开" : "收起", _collapsed, delegate { ToggleCollapsed(); }));

            var colors = new ToolStripMenuItem("颜色");
            colors.DropDownItems.Add(ThemeItem("暖黄", "yellow"));
            colors.DropDownItems.Add(ThemeItem("薄荷", "mint"));
            colors.DropDownItems.Add(ThemeItem("天蓝", "sky"));
            colors.DropDownItems.Add(ThemeItem("淡紫", "lilac"));
            colors.DropDownItems.Add(ThemeItem("浅粉", "blush"));
            colors.DropDownItems.Add(ThemeItem("米白", "sand"));
            _menu.Items.Add(colors);

            var opacity = new ToolStripMenuItem("透明度");
            opacity.DropDownItems.Add(OpacityItem("100%", 1));
            opacity.DropDownItems.Add(OpacityItem("88%", 0.88));
            opacity.DropDownItems.Add(OpacityItem("75%", 0.75));
            _menu.Items.Add(opacity);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(Item("编辑服务清单", EditCatalog));
            _menu.Items.Add(Item("复位到右下角", delegate {
                _anchored = true;
                _settings.hasPosition = false;
                PlaceBottomRight();
                Persist();
            }));
            _menu.Items.Add(CheckItem("登录时启动", StartupShortcut.Exists(), delegate {
                try {
                    StartupShortcut.Enable(!StartupShortcut.Exists());
                } catch (Exception ex) {
                    MessageBox.Show(this, ex.Message, "Tiny", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }));

            string source = _board == null || string.IsNullOrEmpty(_board.CatalogPath) ? "内置清单" : _board.CatalogPath;
            if (source.Length > 52) source = "…" + source.Substring(source.Length - 48);
            var info = new ToolStripMenuItem("清单：" + source);
            info.Enabled = false;
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(info);
            _menu.Items.Add(Item("退出", delegate { Close(); }));
        }

        ToolStripMenuItem Item(string text, Action action) {
            var item = new ToolStripMenuItem(text);
            item.Click += delegate { action(); };
            return item;
        }

        ToolStripMenuItem CheckItem(string text, bool on, Action action) {
            ToolStripMenuItem item = Item(text, action);
            item.Checked = on;
            return item;
        }

        ToolStripMenuItem OpacityItem(string text, double value) {
            var item = new ToolStripMenuItem(text);
            item.Checked = Math.Abs(Opacity - value) < 0.02;
            item.Click += delegate { SetOpacity(value); };
            return item;
        }

        ToolStripMenuItem ThemeItem(string text, string id) {
            var item = new ToolStripMenuItem(text);
            item.Checked = string.Equals(PaletteId(), id, StringComparison.OrdinalIgnoreCase);
            item.Click += delegate { SetTheme(id); };
            return item;
        }

        void SetTheme(string id) {
            _settings.theme = id;
            ApplyPalette();
            BackColor = Paper;
            if (_editor != null) {
                _editor.BackColor = Paper;
                _editor.ForeColor = Ink;
            }
            if (_noteEditor != null) {
                _noteEditor.BackColor = Band;
                _noteEditor.ForeColor = Ink;
            }
            Persist();
            Invalidate();
        }

        void SetOpacity(double value) {
            Opacity = value;
            _settings.opacity = value;
            ApplyClickThrough();
            Persist();
        }

        void ToggleCollapsed() {
            _collapsed = !_collapsed;
            _settings.collapsed = _collapsed;
            Relayout();
            Persist();
        }

        void ToggleClickThrough() {
            _settings.clickThrough = !_settings.clickThrough;
            ApplyClickThrough();
            if (_settings.clickThrough && !_balloonShown && _tray != null) {
                _balloonShown = true;
                _tray.ShowBalloonTip(2500, "穿透已开启", "鼠标会穿过便利贴。右键托盘图标可以关掉。", ToolTipIcon.Info);
            }
            Persist();
        }

        void EditCatalog() {
            try {
                string path = _board != null ? _board.CatalogPath : null;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "services.json");
                    if (!File.Exists(path))
                        File.WriteAllText(path, Catalog.EmbeddedText(), new UTF8Encoding(true));
                }
                Process.Start(new ProcessStartInfo {
                    FileName = "notepad.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Tiny", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ApplyClickThrough() {
            if (!IsHandleCreated) return;
            const int styleIndex = -20;
            const int layered = 0x80000;
            const int transparent = 0x20;
            if (IntPtr.Size == 8) {
                long style = GetWindowLongPtr(Handle, styleIndex).ToInt64();
                if (_settings.clickThrough) style |= layered | transparent;
                else style &= ~transparent;
                SetWindowLongPtr(Handle, styleIndex, new IntPtr(style));
            } else {
                int style = GetWindowLong(Handle, styleIndex);
                if (_settings.clickThrough) style |= layered | transparent;
                else style &= ~transparent;
                SetWindowLong(Handle, styleIndex, style);
            }
        }

        void PlaceBottomRight() {
            Screen screen = IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen;
            Rectangle area = screen.WorkingArea;
            _suppress = true;
            Location = new Point(area.Right - Width - Scale(18), area.Bottom - Height - Scale(18));
            _suppress = false;
        }

        void KeepVisible() {
            if (!IsHandleCreated) return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (!area.IntersectsWith(Bounds)) PlaceBottomRight();
        }

        void KeepFullyVisible() {
            if (!IsHandleCreated) return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            if (x == Left && y == Top) return;
            _suppress = true;
            Location = new Point(x, y);
            _suppress = false;
            _settings.x = x;
            _settings.y = y;
            _settings.hasPosition = true;
            Persist();
        }

        void Persist() {
            if (!_persist) return;
            SettingsStore.Save(_settings);
        }

        void UpdateTrayTip() {
            if (_tray == null || _board == null) return;
            int up = 0;
            int bad = 0;
            foreach (ServiceView service in _board.Services) {
                if (service.State == RunState.Up) up++;
                else bad++;
            }
            string text = "正常 " + up + "，异常 " + bad;
            if (_board.Unknown.Count > 0) text += "，未登记 " + _board.Unknown.Count;
            if (text.Length > 63) text = text.Substring(0, 63);
            _tray.Text = text;
        }

        void EnsureFonts() {
            int key = (int)Math.Round(_factor * 1000) + Dpi;
            if (_titleFont != null && _fontKey == key) return;
            DisposeFonts();
            _fontKey = key;
            _titleFont = MakeFont(26f * _factor, FontStyle.Bold);
            _bodyFont = MakeFont(18f * _factor, FontStyle.Regular);
            _boldFont = MakeFont(18f * _factor, FontStyle.Bold);
            _smallFont = MakeFont(15f * _factor, FontStyle.Regular);
            if (_editor != null) _editor.Font = _bodyFont;
            if (_noteEditor != null) _noteEditor.Font = _boldFont;
        }

        Font MakeFont(float pixels, FontStyle style) {
            return new Font("Microsoft YaHei UI", pixels * Dpi / 96f, style, GraphicsUnit.Pixel);
        }

        void DisposeFonts() {
            if (_titleFont != null) _titleFont.Dispose();
            if (_bodyFont != null) _bodyFont.Dispose();
            if (_boldFont != null) _boldFont.Dispose();
            if (_smallFont != null) _smallFont.Dispose();
            _titleFont = null;
            _bodyFont = null;
            _boldFont = null;
            _smallFont = null;
        }

        void EnsureChrome() {
            if (_icon != null) return;
            _iconBitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(_iconBitmap)) {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (var brush = new SolidBrush(Color.FromArgb(255, 206, 92)))
                    graphics.FillEllipse(brush, 1, 1, 30, 30);
                using (var font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(graphics, "T", font, new Rectangle(0, 6, 32, 22), Color.FromArgb(92, 58, 16),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            _icon = Icon.FromHandle(_iconBitmap.GetHicon());
            Icon = _icon;
        }

        void AddText(string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags) {
            _texts.Add(new DrawText { Text = text ?? "", Font = font, Color = color, Bounds = bounds, Flags = flags });
        }

        int Dpi {
            get { return IsHandleCreated && DeviceDpi > 0 ? DeviceDpi : 96; }
        }

        int Scale(int dip) {
            return (int)Math.Round(dip * Dpi / 96.0);
        }

        int Ui(int dip) {
            return (int)Math.Round(dip * Dpi / 96.0 * _factor);
        }

        string PaletteId() {
            string id = _settings.theme;
            if (id == "mint" || id == "sky" || id == "lilac" || id == "blush" || id == "sand" || id == "yellow") return id;
            return "yellow";
        }

        void ApplyPalette() {
            switch (PaletteId()) {
                case "mint":
                    Paper = Color.FromArgb(232, 247, 236);
                    Band = Color.FromArgb(198, 230, 208);
                    Ink = Color.FromArgb(28, 72, 48);
                    Muted = Color.FromArgb(78, 118, 92);
                    Line = Color.FromArgb(168, 206, 180);
                    break;
                case "sky":
                    Paper = Color.FromArgb(232, 244, 252);
                    Band = Color.FromArgb(198, 224, 242);
                    Ink = Color.FromArgb(24, 58, 92);
                    Muted = Color.FromArgb(78, 110, 138);
                    Line = Color.FromArgb(168, 198, 220);
                    break;
                case "lilac":
                    Paper = Color.FromArgb(244, 236, 252);
                    Band = Color.FromArgb(226, 210, 242);
                    Ink = Color.FromArgb(62, 40, 92);
                    Muted = Color.FromArgb(112, 90, 138);
                    Line = Color.FromArgb(204, 184, 224);
                    break;
                case "blush":
                    Paper = Color.FromArgb(255, 238, 242);
                    Band = Color.FromArgb(246, 214, 222);
                    Ink = Color.FromArgb(92, 40, 56);
                    Muted = Color.FromArgb(150, 96, 110);
                    Line = Color.FromArgb(228, 186, 196);
                    break;
                case "sand":
                    Paper = Color.FromArgb(248, 246, 240);
                    Band = Color.FromArgb(232, 226, 212);
                    Ink = Color.FromArgb(58, 50, 40);
                    Muted = Color.FromArgb(118, 106, 90);
                    Line = Color.FromArgb(210, 200, 182);
                    break;
                default:
                    Paper = Color.FromArgb(255, 243, 196);
                    Band = Color.FromArgb(255, 228, 140);
                    Ink = Color.FromArgb(62, 44, 22);
                    Muted = Color.FromArgb(138, 112, 72);
                    Line = Color.FromArgb(224, 195, 106);
                    break;
            }
        }

        RowHit HitAt(Point point) {
            foreach (RowHit hit in _hits) {
                if (hit.Bounds.Contains(point)) return hit;
            }
            return null;
        }

        void ActivateHit(Point point) {
            RowHit hit = HitAt(point);
            if (hit == null) return;
            if (hit.Kind == "more") {
                _showAllUnknown = !_showAllUnknown;
                _growUnknown = _showAllUnknown;
                Relayout();
                return;
            }
            if (hit.Kind == "edit") BeginEdit(hit);
        }

        void BeginEdit(RowHit hit) {
            if (hit.Group == null) return;
            if (_editor == null) {
                _editor = new TextBox();
                _editor.BorderStyle = BorderStyle.FixedSingle;
                _editor.KeyDown += delegate(object sender, KeyEventArgs e) {
                    if (e.KeyCode == Keys.Enter) {
                        e.SuppressKeyPress = true;
                        CommitEdit();
                    } else if (e.KeyCode == Keys.Escape) {
                        e.SuppressKeyPress = true;
                        CancelEdit();
                    }
                };
                Controls.Add(_editor);
            }
            _editingKey = hit.Group.Name;
            _editor.Font = _bodyFont;
            _editor.BackColor = Paper;
            _editor.ForeColor = Ink;
            _editor.Bounds = hit.EditorBounds;
            _editor.Text = hit.Group.Name.StartsWith("pid ") ? "" : hit.Group.Name;
            _editor.Visible = true;
            _editor.BringToFront();
            _editor.Focus();
            _editor.SelectAll();
        }

        void PlaceEditor() {
            if (_editor == null || !_editor.Visible || string.IsNullOrEmpty(_editingKey)) return;
            foreach (RowHit hit in _hits) {
                if (hit.Kind == "edit" && hit.Group != null && hit.Group.Name == _editingKey) {
                    _editor.Bounds = hit.EditorBounds;
                    _editor.Font = _bodyFont;
                    return;
                }
            }
            CancelEdit();
        }

        void CommitEdit() {
            if (_editor == null || !_editor.Visible) return;
            string name = (_editor.Text ?? "").Trim();
            UnknownGroup group = null;
            foreach (RowHit hit in _hits) {
                if (hit.Kind == "edit" && hit.Group != null && hit.Group.Name == _editingKey) {
                    group = hit.Group;
                    break;
                }
            }
            CancelEdit();
            if (group == null || name.Length == 0) return;
            try {
                string path = _board != null ? _board.CatalogPath : null;
                if (string.IsNullOrEmpty(path))
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "services.json");
                Catalog.Upsert(path, name, group.Ports);
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Tiny", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            StartScan();
        }

        void CancelEdit() {
            _editingKey = null;
            if (_editor == null) return;
            _editor.Visible = false;
        }

        void BeginNoteEdit() {
            CancelEdit();
            if (_noteEditor == null) {
                _noteEditor = new TextBox();
                _noteEditor.BorderStyle = BorderStyle.FixedSingle;
                _noteEditor.KeyDown += delegate(object sender, KeyEventArgs e) {
                    if (e.KeyCode == Keys.Enter) {
                        e.SuppressKeyPress = true;
                        CommitNote();
                    } else if (e.KeyCode == Keys.Escape) {
                        e.SuppressKeyPress = true;
                        CancelNote();
                    }
                };
                Controls.Add(_noteEditor);
            }
            _editingNote = true;
            _noteEditor.Font = _boldFont;
            _noteEditor.BackColor = Band;
            _noteEditor.ForeColor = Ink;
            _noteEditor.Bounds = _noteBounds;
            _noteEditor.Text = _note ?? "";
            _noteEditor.Visible = true;
            _noteEditor.BringToFront();
            _noteEditor.Focus();
            _noteEditor.SelectAll();
        }

        void PlaceNoteEditor() {
            if (_noteEditor == null || !_noteEditor.Visible) return;
            _noteEditor.Bounds = _noteBounds;
            _noteEditor.Font = _boldFont;
            _noteEditor.BackColor = Band;
            _noteEditor.ForeColor = Ink;
        }

        void CommitNote() {
            if (_noteEditor == null || !_noteEditor.Visible) return;
            string text = _noteEditor.Text ?? "";
            try {
                SharedNote.Save(text);
                _note = SharedNote.Load();
            } catch (Exception ex) {
                CancelNote();
                MessageBox.Show(this, ex.Message, "Tiny", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            CancelNote();
            Relayout();
        }

        void CancelNote() {
            _editingNote = false;
            if (_noteEditor == null) return;
            _noteEditor.Visible = false;
        }

        int TargetWidth() {
            int min = Scale(300);
            int chosen = _settings.hasSize ? _settings.width : Scale(480);
            if (chosen < min) chosen = min;
            if (!_settings.hasSize) {
                int max = WorkingSize().Width - Scale(24);
                if (max >= min && chosen > max) chosen = max;
            }
            return chosen;
        }

        int TargetHeight() {
            int min = Scale(220);
            int chosen = _settings.hasSize ? _settings.height : Scale(720);
            if (chosen < min) chosen = min;
            if (!_settings.hasSize) {
                int max = WorkingSize().Height - Scale(24);
                if (max >= min && chosen > max) chosen = max;
            }
            return chosen;
        }

        Size WorkingSize() {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            try {
                if (IsHandleCreated) area = Screen.FromControl(this).WorkingArea;
            } catch {
            }
            return new Size(Math.Max(1, area.Width), Math.Max(1, area.Height));
        }

        static string Summary(Board board) {
            if (board == null) return "正在检测…";
            if (board.ScanFailed) return "端口扫描失败";
            int up = 0;
            int bad = 0;
            foreach (ServiceView service in board.Services) {
                if (service.State == RunState.Up) up++;
                else bad++;
            }
            string text = up + " 个正常";
            if (bad > 0) text += " · " + bad + " 个异常";
            if (board.Unknown.Count > 0) text += " · " + board.Unknown.Count + " 个未登记";
            return text;
        }

        static string StateLabel(ServiceView service) {
            string text;
            switch (service.State) {
                case RunState.Up: text = "正常"; break;
                case RunState.Down: text = "未启动"; break;
                case RunState.Partial: text = "部分异常"; break;
                case RunState.Unhealthy: text = "无响应"; break;
                default: text = "未能检测"; break;
            }
            if (service.LocalOnly && service.State != RunState.Down && service.State != RunState.Unknown)
                text += " · 仅本机";
            return text;
        }

        static string ChipText(PortView port) {
            string text = port.Port.ToString();
            if (!string.IsNullOrEmpty(port.Label)) text += " " + port.Label;
            if (!port.Listening) text += " 未监听";
            return text;
        }

        Color ColorFor(RunState state) {
            switch (state) {
                case RunState.Up: return Good;
                case RunState.Partial: return Warn;
                case RunState.Down:
                case RunState.Unhealthy: return Bad;
                default: return Muted;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        sealed class RowHit {
            public string Kind;
            public Rectangle Bounds;
            public Rectangle EditorBounds;
            public UnknownGroup Group;
        }

        sealed class DrawText {
            public string Text;
            public Font Font;
            public Color Color;
            public Rectangle Bounds;
            public TextFormatFlags Flags;
        }

        sealed class DrawDot {
            public Color Color;
            public Rectangle Bounds;
        }

        sealed class UnknownGroup {
            public string Name;
            public bool LoopbackOnly;
            public readonly List<int> Ports = new List<int>();

            public string PortText() {
                int count = Math.Min(3, Ports.Count);
                string text = Ports[0].ToString();
                for (int i = 1; i < count; i++) text += " · " + Ports[i];
                if (Ports.Count > count) text += " 共 " + Ports.Count + " 个";
                return text;
            }
        }
    }
}
