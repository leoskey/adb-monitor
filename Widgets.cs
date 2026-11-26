using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AdbMonitor;

/// <summary>配色与字体。颜色只用来表达状态：青绿正常、蓝色充电、珊瑚红低电量、琥珀需要处理、灰色断开。</summary>
static class Theme
{
    public static readonly Color Surface = Hex(0x1C1E23), Hover = Hex(0x25282E), Raised = Hex(0x2D3038), Stroke = Hex(0x303237), Track = Hex(0x333439);
    public static readonly Color Text = Hex(0xECEEF1), Muted = Hex(0x9BA1AC), Off = Hex(0x6B717C);
    public static readonly Color Ok = Hex(0x3DD6A8), Charge = Hex(0x5AA5FF), Low = Hex(0xFF6B6B), Warn = Hex(0xF5B942);

    static readonly HashSet<string> Families = new(FontFamily.Families.Select(f => f.Name));
    static string Pick(params string[] names) => names.FirstOrDefault(Families.Contains) ?? "Segoe UI";

    public static readonly string Ui = Pick("Segoe UI Variable Text", "Segoe UI");
    public static readonly string UiSemibold = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
    public static readonly string Display = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");
    public static readonly string Icons = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    public const string GlyphUsb = "", GlyphWifi = "", GlyphPhone = "",
        GlyphCheck = "", GlyphEdit = "", GlyphCopy = "";

    [DllImport("user32.dll")] static extern int GetDpiForSystem();
    static readonly int SystemDpi = GetDpiForSystem();
    static readonly Dictionary<(Font, int), Font> Scaled = [];

    /// <summary>
    /// 给指定 DPI 的窗口用的字体。GDI 按系统 DPI（启动时主屏的缩放）把磅值换算成像素，
    /// 窗口在缩放比例不同的屏幕上时不会跟着变，要自己换算字号。
    /// </summary>
    public static Font At(this Font f, int dpi) =>
        dpi == SystemDpi ? f : Scaled.TryGetValue((f, dpi), out var s) ? s : Scaled[(f, dpi)] = new Font(f.FontFamily, f.Size * dpi / SystemDpi, f.Style);

    static Color Hex(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));

    public static Color ToneColor(Tone t) => t switch
    {
        Tone.Ok => Ok, Tone.Charging => Charge, Tone.Low => Low, Tone.Warn => Warn, _ => Off,
    };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Win11：深色、圆角、细描边（DWM 同时提供阴影）。旧系统上调用失败会被忽略。</summary>
    public static void StyleWindow(IntPtr hwnd)
    {
        int dark = 1, corner = 2, border = Stroke.R | Stroke.G << 8 | Stroke.B << 16;
        DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
        DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref corner, 4);
        DwmSetWindowAttribute(hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref border, 4);
    }

    /// <summary>托盘图标：和设备图标同款的电量圆环，运行时画出来，不带 .ico 资源。</summary>
    public static Icon RingIcon(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = size / 5.5f;
            var arc = new RectangleF(w / 2 + .5f, w / 2 + .5f, size - w - 1, size - w - 1);
            using var track = new Pen(Off, w);
            using var pen = new Pen(Ok, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(track, arc);
            g.DrawArc(pen, arc, -90, 270);
        }
        return Icon.FromHandle(bmp.GetHicon()); // ponytail: HICON 不释放，整个进程只创建一次
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// 单台设备的图标：一圈圆环表示电量，中间是百分比；左上角 USB、右上角 Wi-Fi（只画在用的连接），
/// 右下角充电标记，下方是短名称。悬停和选中时整格换底色。
/// </summary>
sealed class DeviceIcon : Control
{
    public const int CellW = 56, CellH = 68;
    static readonly Font NumF = new(Theme.UiSemibold, 9.75f), NameF = new(Theme.Ui, 8f),
        GlyphF = new(Theme.Icons, 7f), BigGlyphF = new(Theme.Icons, 11f);
    Font Num => NumF.At(DeviceDpi); Font NameFont => NameF.At(DeviceDpi); Font Glyph => GlyphF.At(DeviceDpi); Font BigGlyph => BigGlyphF.At(DeviceDpi);
    const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    public Device? Device;
    public bool Error, Selected;
    bool _hover;

    public DeviceIcon() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Surface);

        var bg = Selected ? Theme.Raised : _hover ? Theme.Hover : Theme.Surface;
        if (bg != Theme.Surface)
        {
            using var cell = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 10 * s);
            using var b = new SolidBrush(bg);
            g.FillPath(b, cell);
        }

        // 占位图标（Device 为 null）：没有设备是虚线圆环加手机，出错是红色整环加「!」
        var d = Device;
        bool empty = d == null && !Error;
        var tone = d?.Tone ?? Tone.Off;
        bool alert = d == null ? Error : tone == Tone.Warn;
        var color = d == null ? Theme.Low : Theme.ToneColor(tone);

        var box = new RectangleF(8 * s, 5.5f * s, 40 * s, 40 * s);
        var arc = RectangleF.Inflate(box, -3 * s, -3 * s);
        using (var track = new Pen(Theme.Track, 3 * s))
        {
            if (empty) track.DashPattern = [1f, 1.54f];
            g.DrawEllipse(track, arc);
        }
        float sweep = alert ? 360 : d?.Level is int lv ? 360f * Math.Clamp(lv, 0, 100) / 100 : 0;
        if (sweep > 0)
            using (var pen = new Pen(color, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, -90, sweep);

        var center = Rectangle.Round(box);
        if (empty) TextRenderer.DrawText(g, Theme.GlyphPhone, BigGlyph, center, Theme.Muted, Center);
        else TextRenderer.DrawText(g, alert ? "!" : d!.Level?.ToString() ?? "--", Num, center,
            alert || tone == Tone.Low ? color : tone == Tone.Off ? Theme.Muted : Theme.Text, Center);

        if (d is { State: not null })
        {
            if (d.Usb) TextRenderer.DrawText(g, Theme.GlyphUsb, Glyph, Rectangle.Round(new RectangleF(2 * s, 1.5f * s, 11 * s, 11 * s)), Theme.Muted, Center);
            if (d.Wifi) TextRenderer.DrawText(g, Theme.GlyphWifi, Glyph, Rectangle.Round(new RectangleF(43 * s, 1.5f * s, 11 * s, 11 * s)), Theme.Muted, Center);
        }
        if (d is { Charging: true })
        {
            var badge = new RectangleF(37 * s, 34.5f * s, 14 * s, 14 * s);
            using (var cut = new SolidBrush(bg)) g.FillEllipse(cut, RectangleF.Inflate(badge, 2 * s, 2 * s));
            using (var b = new SolidBrush(Theme.Charge)) g.FillEllipse(b, badge);
            // 实心闪电（24 格坐标缩到 9px）：图标字体里的闪电是空心的，这么小看不清
            float k = 9 * s / 24, ox = badge.X + 2.5f * s, oy = badge.Y + 2.5f * s;
            PointF[] bolt = [new(13, 2), new(3, 14), new(12, 14), new(11, 22), new(21, 10), new(12, 10)];
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPolygon(fill, Array.ConvertAll(bolt, p => new PointF(ox + p.X * k, oy + p.Y * k)));
        }

        TextRenderer.DrawText(g, d?.ShortName ?? (Error ? "出错" : "无设备"), NameFont,
            Rectangle.Round(new RectangleF(2 * s, 48.5f * s, 52 * s, 14 * s)), Theme.Muted, Center | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>卡片要显示的内容。Serial 为 null 表示占位卡片（没有设备 / 出错），此时不能改名、复制。</summary>
sealed record CardContent(string Title, string Big, string Sub, Tone Tone, int? Bar,
    List<(string Label, string Value)> Rows, string? Serial = null, string Alias = "", string Placeholder = "", bool CanRemove = false);

/// <summary>
/// 设备详情卡片：标题（可就地改名）、大号电量与状态、电量条、「标签 / 值」、序列号（可复制）、移除按钮。
/// 平时不抢焦点（WS_EX_NOACTIVATE），只有改名时主动激活以便输入；开关由 MainForm 控制。
/// </summary>
sealed class DetailCard : Form
{
    static readonly Font TitleF = new(Theme.UiSemibold, 11f), BigF = new(Theme.Display, 24f), MidF = new(Theme.Display, 16f),
        TextF = new(Theme.Ui, 9.75f), IconF = new(Theme.Icons, 10.5f);
    Font TitleFont => TitleF.At(Dpi); Font BigFont => BigF.At(Dpi); Font MidFont => MidF.At(Dpi);
    Font TextFont => TextF.At(Dpi); Font IconFont => IconF.At(Dpi);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top;
    const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    CardContent? _c;
    readonly TextBox _box = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Hover, ForeColor = Theme.Text, Font = new Font(Theme.Ui, 10.5f), Visible = false };
    readonly System.Windows.Forms.Timer _copied = new() { Interval = 1200 };
    Rectangle _editBtn, _copyBtn, _removeBtn, _hot;
    bool _renaming;
    int _dpi;

    /// <summary>改名确认时触发，参数是去掉首尾空白的新名称（空字符串表示恢复默认）。</summary>
    public event Action<string>? Renamed;
    public event Action? RemoveClicked;

    public string Title => _c?.Title ?? "";

    public DetailCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Controls.Add(_box);
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Enter) Commit(); else EndRename();
        };
        _copied.Tick += (_, _) => { _copied.Stop(); Invalidate(); };
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/ | 0x08000000 /*NOACTIVATE*/; return cp; }
    }
    protected override bool ShowWithoutActivation => true;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleWindow(Handle); }
    protected override void OnVisibleChanged(EventArgs e) { if (!Visible) EndRename(); base.OnVisibleChanged(e); }

    // 卡片始终贴着小窗显示，尺寸和字号跟小窗所在屏幕的 DPI 走，由 SetContent 在定位之前重排；
    // 不用卡片自己的 DPI：它要等挪到那块屏幕上才变，而且隐藏时不可靠
    int Dpi => Owner?.DeviceDpi ?? DeviceDpi;
    int Px(int v) => (Owner ?? (Control)this).LogicalToDeviceUnits(v);
    protected override void OnDpiChanged(DpiChangedEventArgs e) { } // 不让 WinForms 按卡片自己的 DPI 再缩放一次

    public void SetContent(CardContent c)
    {
        if (_c is { } o && c with { Rows = o.Rows } == o && c.Rows.SequenceEqual(o.Rows) && _dpi == Dpi) return;
        if (_c?.Serial != c.Serial || _dpi != Dpi) EndRename();
        _c = c;
        _dpi = Dpi;
        ClientSize = Arrange(null);
        Invalidate();
    }

    // ---- 就地改名：标题换成输入框，回车或点对勾保存，Esc 或关闭卡片取消 ----

    public void BeginRename()
    {
        if (!Visible || _c is not { Serial: not null } c) return;
        _renaming = true;
        _box.Text = c.Alias.Length > 0 ? c.Alias : c.Placeholder; // 没有别名时预填默认名称并全选
        int h = _box.PreferredHeight;
        _box.SetBounds(Px(26), Px(16) + (Px(32) - h) / 2, Px(180), h);
        _box.Visible = true;
        Activate(); // NOACTIVATE 窗口点击不会获得焦点，输入前必须主动激活
        _box.Focus();
        _box.SelectAll();
        Invalidate();
    }

    void EndRename()
    {
        if (!_renaming) return;
        _renaming = false;
        _box.Visible = false;
        Invalidate();
    }

    void Commit()
    {
        var name = _box.Text.Trim();
        if (name == _c?.Placeholder) name = ""; // 没改动默认名称，不当作别名保存
        EndRename();
        Renamed?.Invoke(name);
    }

    // ---- 鼠标：三个按钮（改名/保存、复制、移除）用绘制时记下的矩形做命中测试 ----

    Rectangle Hit(Point p) => _editBtn.Contains(p) ? _editBtn : _copyBtn.Contains(p) ? _copyBtn : _removeBtn.Contains(p) ? _removeBtn : Rectangle.Empty;

    void SetHot(Rectangle r) { if (r != _hot) { _hot = r; Invalidate(); } }
    protected override void OnMouseMove(MouseEventArgs e) { SetHot(Hit(e.Location)); base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { SetHot(Rectangle.Empty); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var h = Hit(e.Location);
        if (e.Button != MouseButtons.Left || h.IsEmpty || _c is not { } c) return;
        if (h == _editBtn) { if (_renaming) Commit(); else BeginRename(); }
        else if (h == _copyBtn)
        {
            try { Clipboard.SetText(c.Serial!); } catch (ExternalException) { return; } // 剪贴板被别的程序占用
            _copied.Stop();
            _copied.Start();
            Invalidate();
        }
        else RemoveClicked?.Invoke();
    }

    // ---- 布局与绘制 ----

    static float Descent(Font f) => f.GetHeight() * f.FontFamily.GetCellDescent(f.Style) / f.FontFamily.GetLineSpacing(f.Style);

    void IconButton(Graphics g, Rectangle r, string glyph, Color color)
    {
        if (r == _hot)
        {
            using var path = Theme.RoundRect(r, Px(8));
            using var b = new SolidBrush(Theme.Hover);
            g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, glyph, IconFont, r, color, Line | TextFormatFlags.HorizontalCenter);
    }

    /// <summary>同一段代码负责测量和绘制，保证尺寸与画面一致（g 为 null 时只测量并记下按钮位置）。</summary>
    Size Arrange(Graphics? g)
    {
        _editBtn = _copyBtn = _removeBtn = Rectangle.Empty;
        if (_c is not { } c) return Size.Empty;
        int pad = Px(16), gap = Px(12), rowH = Px(32), x = pad, right = Px(272) - pad, y = pad;
        Size M(string t, Font f) => TextRenderer.MeasureText(t, f, Size.Empty, Flags);

        // 标题行：名称 + 改名按钮；改名时是输入框 + 保存按钮
        if (c.Serial != null) _editBtn = new Rectangle(right - rowH, y, rowH, rowH);
        if (g != null)
        {
            if (_renaming)
            {
                var field = new Rectangle(x, y, _editBtn.Left - Px(8) - x, rowH - 1);
                using (var path = Theme.RoundRect(field, Px(8)))
                using (var b = new SolidBrush(Theme.Hover))
                using (var pen = new Pen(Theme.Charge))
                {
                    g.FillPath(b, path);
                    g.DrawPath(pen, path);
                }
                IconButton(g, _editBtn, Theme.GlyphCheck, Theme.Ok);
            }
            else
            {
                int titleRight = c.Serial != null ? _editBtn.Left - Px(8) : right;
                TextRenderer.DrawText(g, c.Title, TitleFont, new Rectangle(x, y, titleRight - x, rowH), Theme.Text, Line);
                if (c.Serial != null) IconButton(g, _editBtn, Theme.GlyphEdit, Theme.Muted);
            }
        }
        y += rowH + gap;

        if (c.Big.Length > 0)
        {
            // 大号电量（或「未授权」等状态）+ 基线对齐的说明 + 电量条
            int h = Px(40);
            if (g != null)
            {
                var bigFont = c.Tone == Tone.Warn ? MidFont : BigFont;
                Size bs = M(c.Big, bigFont), ss = M(c.Sub, TextFont);
                int by = y + (h - bs.Height) / 2, sx = x + bs.Width + Px(10);
                int sy = by + bs.Height - ss.Height - (int)Math.Round(Descent(bigFont) - Descent(TextFont));
                TextRenderer.DrawText(g, c.Big, bigFont, new Point(x, by), c.Tone == Tone.Off ? Theme.Muted : Theme.ToneColor(c.Tone), Flags);
                TextRenderer.DrawText(g, c.Sub, TextFont, new Rectangle(sx, sy, right - sx, ss.Height), Theme.Muted,
                    Flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            y += h;
            if (c.Bar is int lv)
            {
                y += Px(10);
                var track = new RectangleF(x, y, right - x, Px(6));
                if (g != null)
                {
                    using (var p = Theme.RoundRect(track, track.Height / 2))
                    using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, p);
                    if (lv > 0)
                    {
                        float w = Math.Max(track.Height, track.Width * Math.Min(lv, 100) / 100f);
                        using var p = Theme.RoundRect(new RectangleF(track.X, track.Y, w, track.Height), track.Height / 2);
                        using var b = new SolidBrush(Theme.ToneColor(c.Tone));
                        g.FillPath(b, p);
                    }
                }
                y += (int)track.Height;
            }
            y += gap;
        }
        else
        {
            // 占位卡片：一段说明文字
            const TextFormatFlags wrap = Flags | TextFormatFlags.WordBreak;
            var ss = TextRenderer.MeasureText(c.Sub, TextFont, new Size(right - x, 0), wrap);
            if (g != null) TextRenderer.DrawText(g, c.Sub, TextFont, new Rectangle(x, y, right - x, ss.Height), Theme.Muted, wrap);
            y += ss.Height + gap;
        }

        if (c.Rows.Count > 0 || c.Serial != null)
        {
            if (g != null) using (var b = new SolidBrush(Theme.Stroke)) g.FillRectangle(b, x, y, right - x, 1);
            y += Px(5);
            void Row(string label, string value, int valueRight)
            {
                if (g == null) return;
                int lw = M(label, TextFont).Width + Px(16);
                TextRenderer.DrawText(g, label, TextFont, new Rectangle(x, y, lw, rowH), Theme.Muted, Line);
                TextRenderer.DrawText(g, value, TextFont, new Rectangle(x + lw, y, valueRight - x - lw, rowH), Theme.Text, Line | TextFormatFlags.Right);
            }
            foreach (var (label, value) in c.Rows) { Row(label, value, right); y += rowH; }
            if (c.Serial != null)
            {
                int cb = Px(28);
                _copyBtn = new Rectangle(right + Px(6) - cb, y + (rowH - cb) / 2, cb, cb);
                Row("序列号", c.Serial, _copyBtn.Left - Px(4));
                if (g != null) IconButton(g, _copyBtn, _copied.Enabled ? Theme.GlyphCheck : Theme.GlyphCopy, _copied.Enabled ? Theme.Ok : Theme.Muted);
                y += rowH;
            }
            y += gap;
        }

        if (c.CanRemove)
        {
            _removeBtn = new Rectangle(x, y, right - x, Px(36));
            if (g != null)
            {
                using var path = Theme.RoundRect(new RectangleF(x, y, right - x - 1, Px(36) - 1), Px(10));
                if (_removeBtn == _hot) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
                using var pen = new Pen(Theme.Stroke);
                g.DrawPath(pen, path);
                TextRenderer.DrawText(g, "移除设备", TextFont, _removeBtn, Theme.Low, Line | TextFormatFlags.HorizontalCenter);
            }
            y += Px(36) + gap;
        }
        return new Size(Px(272), y - gap + pad);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Theme.Surface);
        Arrange(e.Graphics);
    }
}
