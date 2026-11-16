using System.Drawing.Drawing2D;

namespace ScreenTranslator;

/// <summary>
/// 截图遮罩：全屏暗幕 + 拖拽框选，Esc/右键取消，松手截取选区。
/// 关键：在窗体显示之前先抓整屏底图（否则遮罩自身会入镜），选区从底图裁剪，
/// 屏幕上呈现"选区内恢复原亮度"的常见截图交互。
/// 首次使用时在顶部显示操作引导胶囊（含"不再提醒"），4 秒后自动淡出。
/// </summary>
public sealed class ScreenCaptureForm : Form
{
    /// <summary>完成框选：参数为选区位图（调用方负责 Dispose）与选区屏幕坐标矩形。</summary>
    public event Action<Bitmap, Rectangle>? Captured;

    /// <summary>用户点击"不再提醒"。</summary>
    public event Action? TipDisabled;

    private const int BrandR = 74, BrandG = 124, BrandB = 255;   // 品牌蓝 #4A7CFF
    private const int MinSel = 12;                                // 选区最小边长，小于视为误触取消

    private readonly Bitmap _bg;          // 显示前抓取的整屏底图
    private Rectangle _sel;               // 当前选区（客户区坐标）
    private bool _dragging;
    private readonly bool _showTip;
    private Rectangle _tipBtnRect;        // "不再提醒"按钮命中区（客户区坐标）
    private int _tipAlpha = 235;          // 提示胶囊透明度（4 秒后开始淡出）
    private readonly System.Windows.Forms.Timer _tipTimer;

    public ScreenCaptureForm(bool showTip)
    {
        _showTip = showTip;

        // 全屏虚拟屏无边框遮罩
        var vs = SystemInformation.VirtualScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(vs.Left, vs.Top);
        Size = vs.Size;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;

        // 在显示之前抓整屏底图（此时遮罩还未出现，不会入镜）
        _bg = new Bitmap(vs.Width, vs.Height);
        using (var g = Graphics.FromImage(_bg))
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size);

        _tipTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _tipTimer.Tick += (_, _) =>
        {
            // 前 4 秒保持，之后约 0.5 秒淡出
            if (_tipAlpha > 0 && (ElapsedMs(_shownAt) > 4000 || _dragging))
                _tipAlpha = Math.Max(0, _tipAlpha - 24);
            if (_tipAlpha == 0) _tipTimer.Stop();
            if (_showTip) Invalidate();
        };
    }

    private long _shownAt;
    private static long ElapsedMs(long from) => Environment.TickCount64 - from;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /*TOOLWINDOW*/;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shownAt = Environment.TickCount64;
        if (_showTip) _tipTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // 底图（原屏幕画面）+ 全屏暗幕
        g.DrawImage(_bg, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(88, 8, 10, 18)))
            g.FillRectangle(dim, ClientRectangle);

        // 选区内重画原亮度底图 + 品牌蓝边框
        if (_sel.Width > 0 && _sel.Height > 0)
        {
            g.DrawImage(_bg, _sel, _sel, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.FromArgb(BrandR, BrandG, BrandB), 1.6f);
            g.DrawRectangle(pen, _sel.X, _sel.Y, _sel.Width - 1, _sel.Height - 1);

            // 尺寸提示：右下角外侧（放不下则放内侧）
            string sizeText = $"{_sel.Width} × {_sel.Height}";
            using var f = new Font("Microsoft YaHei UI", 9f);
            var ts = g.MeasureString(sizeText, f);
            int bx = _sel.Right - (int)ts.Width - 10;
            int by = _sel.Bottom + 6;
            if (by + ts.Height > ClientSize.Height) by = _sel.Bottom - (int)ts.Height - 6;
            bx = Math.Clamp(bx, 4, ClientSize.Width - (int)ts.Width - 4);
            using var bgBrush = new SolidBrush(Color.FromArgb(200, 20, 24, 40));
            using var txBrush = new SolidBrush(Color.White);
            g.FillRectangle(bgBrush, bx - 6, by - 3, ts.Width + 12, ts.Height + 4);
            g.DrawString(sizeText, f, txBrush, bx, by);
        }

        // 首次引导提示：顶部居中胶囊 + "不再提醒"按钮
        if (_showTip)
            DrawTip(g);
    }

    private void DrawTip(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        const string text = "拖拽框选要翻译的区域，Esc 取消";
        using var f = new Font("Microsoft YaHei UI", 10f);
        using var bf = new Font("Microsoft YaHei UI", 8.5f);
        var ts = g.MeasureString(text, f);
        var bs = g.MeasureString("不再提醒", bf);

        int pad = 16;
        int w = (int)(ts.Width + bs.Width) + pad * 3 + 10;
        int h = (int)Math.Max(ts.Height, bs.Height) + 16;
        int x = (ClientSize.Width - w) / 2;
        int y = 28;

        using var path = RoundedRect(new Rectangle(x, y, w, h), h / 2);
        using var bgBrush = new SolidBrush(Color.FromArgb(_tipAlpha, 24, 28, 48));
        g.FillPath(bgBrush, path);
        using var border = new Pen(Color.FromArgb(_tipAlpha, BrandR, BrandG, BrandB), 1f);
        g.DrawPath(border, path);

        using var txBrush = new SolidBrush(Color.FromArgb(_tipAlpha, Color.White));
        g.DrawString(text, f, txBrush, x + pad, y + (h - ts.Height) / 2);

        // "不再提醒"：胶囊内的次级按钮（命中区稍大便于点击）
        int btnX = x + w - pad - (int)bs.Width - 4;
        int btnY = y + (h - (int)bs.Height) / 2;
        using var dimBrush = new SolidBrush(Color.FromArgb(_tipAlpha, 165, 175, 205));
        g.DrawString("不再提醒", bf, dimBrush, btnX, btnY);
        _tipBtnRect = new Rectangle(btnX - 6, y + 2, (int)bs.Width + 12, h - 4);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- 交互 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            // 先命中"不再提醒"
            if (_tipBtnRect.Contains(e.Location))
            {
                _tipAlpha = 0;
                _tipTimer.Stop();
                TipDisabled?.Invoke();
                Invalidate();
                return;
            }
            _dragging = true;
            _sel = new Rectangle(e.Location, Size.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _sel = Normalize(_sel.X, _sel.Y, e.X, e.Y);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        if (_sel.Width < MinSel || _sel.Height < MinSel)
        {
            Cancel();   // 几乎没拖动 = 误触，取消
            return;
        }
        Finish();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Cancel();
    }

    private void Cancel()
    {
        Close();
    }

    private void Finish()
    {
        _tipTimer.Stop();
        // 选区从底图裁剪（底图覆盖虚拟屏，坐标直接对应）
        var bmp = _bg.Clone(_sel, _bg.PixelFormat);
        var screenRect = new Rectangle(Location.X + _sel.X, Location.Y + _sel.Y, _sel.Width, _sel.Height);
        Captured?.Invoke(bmp, screenRect);   // Program 侧负责 Close + 后续流程
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tipTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bg.Dispose();
        base.Dispose(disposing);
    }

    private static Rectangle Normalize(int x1, int y1, int x2, int y2) =>
        Rectangle.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}
