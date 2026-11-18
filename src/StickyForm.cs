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
        static readonly Color Paper = Color.FromArgb(255, 243, 196);
        static readonly Color Band = Color.FromArgb(255, 232, 150);
        static readonly Color Ink = Color.FromArgb(62, 44, 22);
        static readonly Color Muted = Color.FromArgb(138, 112, 72);
        static readonly Color Line = Color.FromArgb(224, 195, 106);
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
        Rectangle _titleBounds;
        Point _pressAt;
        bool _pressTitle;

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
            BackColor = Paper;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            Text = "Tiny";
            ClientSize = new Size(320, 88);
            if (_settings.hasPosition) Location = new Point(_settings.x, _settings.y);
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
            using (var band = new SolidBrush(Band))
                graphics.FillRectangle(band, 0, 0, width, _headerHeight);
            using (var pen = new Pen(Line)) {
                graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
                if (_headerHeight > 0)
                    graphics.DrawLine(pen, 0, _headerHeight - 1, width, _headerHeight - 1);
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
            if (e.Button == MouseButtons.Left && _titleBounds.Contains(e.Location)) {
                _pressTitle = true;
                _pressAt = e.Location;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            Cursor = _titleBounds.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
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
            _pressTitle = false;
            if (e.Button == MouseButtons.Right) _menu.Show(this, e.Location);
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) {
            if (e.Button == MouseButtons.Left && _titleBounds.Contains(e.Location))
                ToggleCollapsed();
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
                EnsureFonts();
                _texts.Clear();
                _dots.Clear();
                int width = Scale(328);
                int pad = Scale(14);
                int header = Scale(54);
                _headerHeight = header;
                _titleBounds = new Rectangle(0, 0, width, header);
                int titleH = Math.Max(_titleFont.Height, Scale(20));
                int bodyH = Math.Max(_bodyFont.Height, Scale(18));
                int smallH = Math.Max(_smallFont.Height, Scale(16));
                int row = bodyH + Scale(4);

                AddText("Tiny", _titleFont, Ink, new Rectangle(pad, Scale(8), width - pad * 2 - Scale(78), titleH), LeftText);
                string clock = _board == null ? "检测中" : _board.ScannedAt.ToString("HH:mm:ss");
                AddText(clock, _smallFont, Muted, new Rectangle(width - pad - Scale(72), Scale(12), Scale(72), smallH), RightText);
                string subtitle = _collapsed ? Summary(_board) : Environment.MachineName;
                AddText(subtitle, _smallFont, Muted, new Rectangle(pad, Scale(30), width - pad * 2, smallH), LeftText);

                int y = header + Scale(8);
                if (!_collapsed) {
                    if (_board != null && !string.IsNullOrEmpty(_board.CatalogError)) {
                        Size size = TextRenderer.MeasureText(_board.CatalogError, _smallFont, new Size(width - pad * 2, 0), WrapText);
                        int errorH = Math.Max(size.Height, smallH);
                        AddText(_board.CatalogError, _smallFont, Bad, new Rectangle(pad, y, width - pad * 2, errorH), WrapText);
                        y += errorH + Scale(8);
                    }
                    if (_board == null) {
                        AddText("正在检测本机端口…", _bodyFont, Muted, new Rectangle(pad, y, width - pad * 2, row), LeftText);
                        y += row;
                    } else {
                        foreach (ServiceView service in _board.Services)
                            y = DrawService(service, pad, y, width, row);
                        y = DrawUnknown(pad, y, width, row, smallH);
                    }
                    string footer = (_board == null ? "" : _board.ScannedAt.ToString("HH:mm:ss") + " 刷新 · ")
                        + "拖标题移动 · 双击收起 · 右键菜单";
                    AddText(footer, _smallFont, Muted, new Rectangle(pad, y + Scale(2), width - pad * 2, smallH), LeftText);
                    y += smallH + Scale(8);
                }

                int height = Math.Max(header + Scale(2), y + Scale(6));
                ClientSize = new Size(width, height);
                if (_ready && _persist && _anchored) PlaceBottomRight();
                else if (_ready && _persist) KeepFullyVisible();
                Invalidate();
            } finally {
                _inLayout = false;
            }
        }

        int DrawService(ServiceView service, int pad, int y, int width, int row) {
            Color stateColor = ColorFor(service.State);
            _dots.Add(new DrawDot { Color = stateColor, Bounds = new Rectangle(pad, y + Scale(5), Scale(8), Scale(8)) });
            int nameX = pad + Scale(14);
            int stateW = Scale(138);
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
                int chipW = measured.Width + Scale(8);
                if (x > pad && x + chipW > limit) {
                    x = pad;
                    chipY += lineH;
                }
                AddText(text, _bodyFont, port.Listening ? Ink : Bad, new Rectangle(x, chipY, chipW, lineH), LeftText);
                x += chipW + Scale(8);
            }
            return chipY + lineH + Scale(8);
        }

        int DrawUnknown(int pad, int y, int width, int row, int smallH) {
            List<UnknownGroup> groups = GroupUnknown(_board.Unknown);
            if (groups.Count == 0) return y;
            AddText("未登记", _boldFont, Muted, new Rectangle(pad, y, width - pad * 2, row), LeftText);
            y += row;
            int shown = Math.Min(6, groups.Count);
            for (int i = 0; i < shown; i++) {
                UnknownGroup group = groups[i];
                string who = group.Name.StartsWith("pid ") ? "进程 " + group.Name.Substring(4) : group.Name;
                string line = who + "    " + group.PortText();
                if (group.LoopbackOnly) line += "  仅本机";
                AddText(line, _bodyFont, Ink, new Rectangle(pad, y, width - pad * 2, row), LeftText);
                y += row;
            }
            int rest = groups.Count - shown;
            if (rest > 0) {
                AddText("还有 " + rest + " 个进程", _smallFont, Muted, new Rectangle(pad, y, width - pad * 2, smallH), LeftText);
                y += smallH + Scale(4);
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
            if (_titleFont != null) return;
            _titleFont = MakeFont(18f, FontStyle.Bold);
            _bodyFont = MakeFont(14f, FontStyle.Regular);
            _boldFont = MakeFont(14f, FontStyle.Bold);
            _smallFont = MakeFont(12f, FontStyle.Regular);
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

        static Color ColorFor(RunState state) {
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
