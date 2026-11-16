using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AdbMonitor;

public sealed class MainForm : Form
{
    const int SnapDist = 20, Pad = 6, EdgeGap = 12, PollMs = 5000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "AdbMonitor";

    readonly AppState _state;
    readonly bool _demo;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _rename, _remove, _autostart;
    readonly DetailCard _card = new();
    readonly System.Windows.Forms.Timer _clickWatch = new() { Interval = 50 };
    Device? _target, _cardDevice;
    string? _error;
    Point _downPos;
    bool _pressed, _buttonWasDown;

    public MainForm(bool demo)
    {
        _demo = demo;
        _state = demo ? new AppState() : AppState.Load();
        if (demo) // 先按显示顺序占位（其中一台带别名），在线数据由 Demo() 填入；最后一台是已断开的
            _state.Devices.AddRange([
                new Device { Key = "R58M1A2B" }, new Device { Key = "PA9410MGK5", Alias = "测试机" },
                new Device { Key = "1C9X7F3E" }, new Device { Key = "ZY22ABCD" },
                new Device { Key = "HT7A0001", Model = "Nexus 5X", Android = "8.1", Level = 42, LastSeen = DateTime.Now.AddMinutes(-12) }]);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        MaximizeBox = false;
        BackColor = Theme.Surface;
        Text = "ADB Monitor";

        _rename = new ToolStripMenuItem("重命名", null, (_, _) => Rename());
        _remove = new ToolStripMenuItem("移除", null, (_, _) => Remove());
        _autostart = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutostart());
        _menu.Items.AddRange([_rename, _remove, new ToolStripSeparator(), _autostart,
            new ToolStripMenuItem("退出", null, (_, _) => Close())]);
        _menu.Renderer = new MenuRenderer();
        _menu.ForeColor = Theme.Text;
        _menu.Opening += (_, _) =>
        {
            _target = (_menu.SourceControl as DeviceIcon)?.Device;
            _rename.Enabled = _target != null;
            _remove.Enabled = _target is { State: null };
            _autostart.Checked = AutostartOn;
        };
        ContextMenuStrip = _menu;
        HookMouse(this);

        _card.Owner = this;
        _card.Renamed += name =>
        {
            if (_cardDevice is not { } d) return;
            d.Alias = name.Length == 0 ? null : name;
            SaveState();
            RefreshIcons();
        };
        _card.RemoveClicked += () => { _target = _cardDevice; Remove(); };
        _card.VisibleChanged += (_, _) => { _clickWatch.Enabled = _card.Visible; MarkSelected(); };
        _clickWatch.Tick += (_, _) => CloseCardOnOutsideClick();

        RefreshIcons();
        PlaceOnStart();
    }

    // 不出现在 Alt+Tab，显示时不抢焦点；ShowWithoutActivation 会让 TopMost 失效，所以直接写 WS_EX_TOPMOST
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*WS_EX_TOOLWINDOW*/ | 0x8 /*WS_EX_TOPMOST*/; return cp; }
    }
    protected override bool ShowWithoutActivation => true;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleWindow(Handle); }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_demo && Adb.Exe == null)
        {
            _error = "找不到 adb.exe。把 Android SDK 的 platform-tools 加入 PATH，或安装到 %LOCALAPPDATA%\\Android\\Sdk。";
            RefreshIcons();
            return;
        }
        if (!_demo) await Task.Run(Adb.StartServer);
        while (!IsDisposed)
        {
            try
            {
                var lives = await Task.Run(_demo ? Demo : (Func<List<Live>>)Adb.Poll);
                if (IsDisposed) return;
                _error = null;
                Apply(lives);
            }
            catch (Exception ex) when (!IsDisposed)
            {
                _error = "轮询失败：" + ex.Message;
                RefreshIcons();
            }
            await Task.Delay(PollMs);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { SaveState(); base.OnFormClosing(e); }

    internal static List<Live> Demo() =>
    [
        new("R58M1A2B", "R58M1A2B", "device", true, true, "192.168.1.23", "SM-A515F", "13", 85, 2, "USB", 31.5),
        new("PA9410MGK5", "PA9410MGK5", "device", false, true, "192.168.1.57", "PICO 4", "10", 68, 3, null, 33.2),
        new("1C9X7F3E", "1C9X7F3E", "device", false, true, "192.168.1.40", "Pixel 8", "15", 15, 3, null, 29.0),
        new("ZY22ABCD", "ZY22ABCD", "unauthorized", true, false, null),
    ];

    /// <summary>把一轮轮询结果合并进已知设备列表：按 Key 合并有线和无线，消失的设备标记为断开。</summary>
    internal void Apply(List<Live> lives)
    {
        var now = DateTime.Now;
        bool changed = false;
        foreach (var g in lives.GroupBy(l => l.Key))
        {
            var d = _state.Devices.Find(x => x.Key == g.Key);
            if (d == null) { _state.Devices.Add(d = new Device { Key = g.Key }); changed = true; }
            var best = g.FirstOrDefault(l => l.State == "device") ?? g.First();
            changed |= d.State != best.State;
            d.State = best.State;
            d.Usb = g.Any(l => l.Usb);
            d.Wifi = g.Any(l => l.Wifi);
            d.Ip = g.Select(l => l.Ip).FirstOrDefault(ip => ip != null);
            d.LastSeen = now;
            if (best.State == "device")
            {
                d.Model = best.Model ?? d.Model;
                d.Android = best.Android ?? d.Android;
                d.Level = best.Level ?? d.Level;
                d.Status = best.Status;
                d.Source = best.Source;
                d.TempC = best.TempC;
            }
        }
        foreach (var d in _state.Devices)
            if (d.State != null && !lives.Any(l => l.Key == d.Key)) { d.State = null; changed = true; }
        // 从未授权成功过的设备（不知道型号）断开后不保留
        changed |= _state.Devices.RemoveAll(d => d.State == null && d.Model == null) > 0;
        if (changed) SaveState();
        RefreshIcons();
    }

    void RefreshIcons()
    {
        var items = _state.Devices.Cast<Device?>().ToList();
        if (items.Count == 0 || _error != null) items.Add(null); // 占位图标：无设备或错误
        SuspendLayout();
        while (Controls.Count < items.Count)
        {
            var ic = new DeviceIcon { ContextMenuStrip = _menu };
            HookMouse(ic);
            Controls.Add(ic);
        }
        while (Controls.Count > items.Count) Controls[Controls.Count - 1].Dispose();
        for (int i = 0; i < items.Count; i++)
        {
            var ic = (DeviceIcon)Controls[i];
            ic.Device = items[i];
            ic.Error = items[i] == null && _error != null;
            ic.Invalidate();
        }
        ResumeLayout();
        Relayout();
        if (_card.Visible) UpdateCard();
        MarkSelected();
    }

    // ---- 详情卡片：单击图标打开；点另一台设备立即切换；再点同一台或点别处关闭；内容随轮询刷新 ----

    void ToggleCard(DeviceIcon ic)
    {
        if (_card.Visible && _cardDevice == ic.Device) _card.Hide();
        else ShowCard(ic);
    }

    void ShowCard(DeviceIcon ic)
    {
        _cardDevice = ic.Device;
        UpdateCard();
        if (!_card.Visible) _card.Show();
        MarkSelected();
    }

    internal void ShowCardFor(int index) => ToggleCard((DeviceIcon)Controls[index]);
    internal DetailCard Card => _card;

    void MarkSelected()
    {
        foreach (DeviceIcon ic in Controls)
        {
            bool sel = _card.Visible && ic.Device == _cardDevice;
            if (ic.Selected != sel) { ic.Selected = sel; ic.Invalidate(); }
        }
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    /// <summary>卡片不抢焦点，所以用全局按键状态判断「点了别处」：按下瞬间光标不在卡片和小窗上就关闭。</summary>
    void CloseCardOnOutsideClick()
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (down && !_buttonWasDown && !_card.Bounds.Contains(Cursor.Position) && !Bounds.Contains(Cursor.Position))
            _card.Hide();
        _buttonWasDown = down;
    }

    void UpdateCard()
    {
        var ic = Controls.OfType<DeviceIcon>().FirstOrDefault(i => i.Device == _cardDevice);
        if (ic == null) { _card.Hide(); return; }
        var d = ic.Device;
        if (d != null)
        {
            var (big, sub) = d.Headline(DateTime.Now);
            _card.SetContent(new(d.FullName, big, sub, d.Tone, d.Tone == Tone.Warn ? null : d.Level, d.Rows(),
                d.Key, d.Alias ?? "", d.DefaultName, d.State == null));
        }
        else if (_error != null) _card.SetContent(new("出错了", "", _error, Tone.Low, null, []));
        else _card.SetContent(new("没有设备", "", "用 USB 调试连接设备，或在设备上打开无线调试。", Tone.Off, null, []));

        // 朝屏幕内侧展开：竖排时放左/右侧，横排时放上/下方，并限制在工作区内
        var r = ic.RectangleToScreen(ic.ClientRectangle);
        var wa = Screen.FromControl(this).WorkingArea;
        var sz = _card.Size;
        int gap = LogicalToDeviceUnits(8);
        var p = Horizontal
            ? new Point(r.Left, Top + Height / 2 < wa.Top + wa.Height / 2 ? Bottom + gap : Top - sz.Height - gap)
            : new Point(Left + Width / 2 > wa.Left + wa.Width / 2 ? Left - sz.Width - gap : Right + gap, r.Top);
        p.X = Math.Clamp(p.X, wa.Left, Math.Max(wa.Left, wa.Right - sz.Width));
        p.Y = Math.Clamp(p.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - sz.Height));
        _card.Location = p;
    }

    // ---- 布局与贴边 ----

    bool Horizontal => (_state.Edge & (Edge.Top | Edge.Bottom)) != 0 && (_state.Edge & (Edge.Left | Edge.Right)) == 0;

    /// <summary>可贴靠的区域：工作区四周各留 EdgeGap，小窗贴边后悬浮在屏幕边缘而不是紧贴。</summary>
    Rectangle Area(Screen s)
    {
        var wa = s.WorkingArea;
        wa.Inflate(-LogicalToDeviceUnits(EdgeGap), -LogicalToDeviceUnits(EdgeGap));
        return wa;
    }

    /// <summary>贴左右边（或没贴边）竖排，只贴上下边横排；贴右/下边时向内生长。</summary>
    void Relayout()
    {
        var cell = new Size(LogicalToDeviceUnits(DeviceIcon.CellW), LogicalToDeviceUnits(DeviceIcon.CellH));
        int pad = LogicalToDeviceUnits(Pad), n = Controls.Count;
        var e = _state.Edge;
        bool horiz = Horizontal;
        for (int i = 0; i < n; i++)
            Controls[i].Bounds = new Rectangle(pad + (horiz ? i * cell.Width : 0), pad + (horiz ? 0 : i * cell.Height), cell.Width, cell.Height);
        var b = new Rectangle(Location, new Size(pad * 2 + cell.Width * (horiz ? n : 1), pad * 2 + cell.Height * (horiz ? 1 : n)));
        var wa = Area(Screen.FromRectangle(b));
        if (e.HasFlag(Edge.Left)) b.X = wa.Left;
        if (e.HasFlag(Edge.Right)) b.X = wa.Right - b.Width;
        if (e.HasFlag(Edge.Top)) b.Y = wa.Top;
        if (e.HasFlag(Edge.Bottom)) b.Y = wa.Bottom - b.Height;
        if (Bounds != b) Bounds = b;
    }

    void PlaceOnStart()
    {
        if (_state.X is int x && _state.Y is int y && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(x, y)))
            Location = new Point(x, y);
        else
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            _state.Edge = Edge.Right;
            Location = new Point(wa.Right - Width, wa.Top + wa.Height / 3);
        }
        Relayout();
    }

    void SaveState()
    {
        _state.X = Left;
        _state.Y = Top;
        if (!_demo) _state.Save();
    }

    // ---- 拖动与单击 ----

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// <summary>左键按下后移动超过系统拖动阈值才进入拖动；没移动就松开算单击。</summary>
    void HookMouse(Control c)
    {
        c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressed = true; _downPos = Cursor.Position; } };
        c.MouseMove += (_, e) =>
        {
            if (!_pressed || e.Button != MouseButtons.Left) return;
            var d = Cursor.Position - (Size)_downPos;
            if (Math.Abs(d.X) <= SystemInformation.DragSize.Width && Math.Abs(d.Y) <= SystemInformation.DragSize.Height) return;
            _pressed = false;
            _card.Hide();
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, IntPtr.Zero);
        };
        c.MouseUp += (_, e) =>
        {
            if (!_pressed || e.Button != MouseButtons.Left) return;
            _pressed = false;
            if (c is DeviceIcon ic) ToggleCard(ic);
        };
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232;
        if (m.Msg == WM_MOVING)
        {
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            var wa = Area(Screen.FromPoint(Cursor.Position));
            var snapped = Snap(Rectangle.FromLTRB(r.L, r.T, r.R, r.B), wa, LogicalToDeviceUnits(SnapDist));
            Marshal.StructureToPtr(new RECT { L = snapped.Left, T = snapped.Top, R = snapped.Right, B = snapped.Bottom }, m.LParam, false);
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_EXITSIZEMOVE)
        {
            _state.Edge = EdgesOf(Bounds, Area(Screen.FromControl(this)));
            Relayout();
            SaveState();
        }
    }

    public static Rectangle Snap(Rectangle r, Rectangle wa, int dist)
    {
        if (Math.Abs(r.Left - wa.Left) < dist) r.X = wa.Left;
        else if (Math.Abs(r.Right - wa.Right) < dist) r.X = wa.Right - r.Width;
        if (Math.Abs(r.Top - wa.Top) < dist) r.Y = wa.Top;
        else if (Math.Abs(r.Bottom - wa.Bottom) < dist) r.Y = wa.Bottom - r.Height;
        return r;
    }

    public static Edge EdgesOf(Rectangle r, Rectangle wa) =>
        (r.Left == wa.Left ? Edge.Left : 0) | (r.Right == wa.Right ? Edge.Right : 0) |
        (r.Top == wa.Top ? Edge.Top : 0) | (r.Bottom == wa.Bottom ? Edge.Bottom : 0);

    // ---- 菜单 ----

    /// <summary>打开该设备的卡片并直接进入改名状态。</summary>
    void Rename()
    {
        if (Controls.OfType<DeviceIcon>().FirstOrDefault(i => i.Device != null && i.Device == _target) is not { } ic) return;
        ShowCard(ic);
        _card.BeginRename();
    }

    void Remove()
    {
        if (_target is not { State: null }) return; // 只能移除已断开的设备
        _state.Devices.Remove(_target);
        SaveState();
        RefreshIcons();
    }

    static string ExeCmd => $"\"{Environment.ProcessPath}\"";

    static bool AutostartOn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) as string == ExeCmd; }
    }

    static void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (AutostartOn) k.DeleteValue(RunName, false);
        else k.SetValue(RunName, ExeCmd);
    }

    /// <summary>深色菜单：禁用项变暗，勾选项用浅色对勾（默认渲染器的黑色对勾在深色底上看不见）。</summary>
    sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Off;
            base.OnRenderItemText(e);
        }

        static readonly Font CheckFont = new(Theme.Icons, 9f);

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
            TextRenderer.DrawText(e.Graphics, "" /*CheckMark*/, CheckFont, e.ImageRectangle, Theme.Ok,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Stroke;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color SeparatorDark => Theme.Stroke;
        public override Color SeparatorLight => Theme.Surface;
        public override Color CheckBackground => Theme.Surface;
        public override Color CheckSelectedBackground => Theme.Hover;
        public override Color CheckPressedBackground => Theme.Hover;
    }
}
