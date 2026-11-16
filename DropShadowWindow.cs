using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ScreenTranslator;

/// <summary>
/// 外挂大阴影窗：一个鼠标穿透的分层窗口垫在目标内容正下方，
/// 投影预渲染为一张 ARGB 位图（多层同心圆角矩形由外向内叠加，近似高斯模糊），
/// 仅在内容尺寸变化时重建一次——移动时仅重定位（位置未变则跳过提交）。
/// 即"现代软件的大外置阴影"效果，替代 1px 描边方案。
/// 支持两种用法：窗口投影（整个窗体 Bounds）与悬浮球投影（球体矩形，窗体其余区域透明穿透）。
/// </summary>
public sealed class DropShadowWindow : Form
{
    // 默认参数（普通窗口）；悬浮球通过 Attach 传入更小的圆形阴影参数
    private readonly int _expand;    // 阴影向外扩散半径（≈blur）
    private readonly int _dropY;     // 光源在上方时的整体下坠量
    private readonly int _corner;    // 与窗体圆角一致
    private readonly int _maxAlpha;  // 贴边处的叠加浓度
    private readonly bool _circular; // 球形阴影：每层圆角取短边一半，投影为正圆

    private Bitmap? _bmp;
    private Size _bmpForSize = Size.Empty;
    private Form? _owner;
    private Point _lastPos = new(int.MinValue, int.MinValue); // 位置未变则不重复提交 ULW

    private DropShadowWindow(int expand, int dropY, int corner, int maxAlpha, bool circular)
    {
        _expand = expand; _dropY = dropY; _corner = corner; _maxAlpha = maxAlpha; _circular = circular;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        // 不设 Enabled=false：禁用窗体无法 Show（"未启用的窗体不能显示为模式对话框"）。
        // 鼠标穿透与免激活已由 CreateParams 里的 TRANSPARENT/NOACTIVATE 样式保证。
        // TOPMOST 不在这里写死：Attach 时按 owner 是否置顶决定（阴影必须与主人窗同层，
        // 否则"置顶阴影 + 非置顶主窗"的 Owner 组合会在激活时搅动置顶带 z 序，把主球挤下去）。
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /*LAYERED*/ | 0x00000020 /*TRANSPARENT 鼠标穿透*/
                        | 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/;
            return cp;
        }
    }

    /// <summary>挂到 owner 身后。trackForm=true（普通窗口）：自动跟随其移动与显隐；
    /// trackForm=false（悬浮球）：由调用方每帧调用 Sync 按球体位置驱动。
    /// 悬浮球传 circular:true + 更小的 expand/maxAlpha 得到贴合球体的小圆影。</summary>
    public static DropShadowWindow Attach(Form owner, bool trackForm = true,
        int expand = 8, int dropY = 0, int corner = 14, int maxAlpha = 35, bool circular = false)
    {
        var s = new DropShadowWindow(expand, dropY, corner, maxAlpha, circular);
        s._owner = owner;
        // 阴影与 owner 同层：置顶窗口（悬浮球等）阴影保持置顶；
        // 非置顶窗口（设置/引导）阴影也非置顶，避免置顶带 z 序被搅动。
        s.TopMost = owner.TopMost;
        if (trackForm)
        {
            // 非置顶窗口：把阴影设为主窗口的 Win32 Owner（GWL_HWNDPARENT）。
            // 系统保证"被拥有窗口"永远压在 Owner 上方、激活时整组一起提升，
            // 否则主窗重新激活后阴影仍埋在别的窗口下面（光晕被盖住 = 阴影"消失"）。
            // 置顶窗口（悬浮球等）本来就恒在最上层，无需此处理。
            if (!owner.TopMost)
            {
                var shadowHandle = s.Handle;   // 强制先创建阴影句柄，才能登记为 Owner
                SetWindowLongPtr(owner.Handle, -8 /*GWL_HWNDPARENT*/, shadowHandle);
            }
            owner.LocationChanged += (_, _) => s.FollowOwner();
            owner.VisibleChanged += (_, _) =>
            {
                if (!owner.Visible) s.Hide();
                else s.Sync(owner);
            };
        }
        owner.HandleDestroyed += (_, _) => s.Dispose();
        return s;
    }

    /// <summary>窗口投影：按 owner 整个窗体 Bounds 同步（普通窗口用）。</summary>
    public void Sync(Form owner) => Sync(owner, new Rectangle(owner.Location, owner.Size));

    /// <summary>按内容区域（屏幕坐标）同步阴影。窗口传 Bounds；悬浮球传球体矩形。</summary>
    public void Sync(Form owner, Rectangle content)
    {
        _owner = owner;
        if (content.Size != _bmpForSize) Render(content.Size);
        var pos = new Point(content.Left - _expand, content.Top - _expand + _dropY);
        if (!Visible)
        {
            Show();
            Reposition(pos);   // 关键：Show 之后必须提交 ULW 位图，否则分层窗口完全透明（首次不显示的根因）
            PinBelow(owner);   // 首次显示：钉到 owner 正下方
        }
        else
        {
            Reposition(pos);   // 后续：z 序由 WndProc 自动纠正，不必每次 SetWindowPos
        }
    }

    private void FollowOwner()
    {
        if (_owner == null || !Visible || _bmp == null) return;
        Reposition(new Point(_owner.Left - _expand, _owner.Top - _expand + _dropY));
    }

    /// <summary>隐藏并清除位置缓存：下次在同一位置重新显示时仍会重新提交 ULW 位图。</summary>
    public new void Hide()
    {
        base.Hide();
        _lastPos = new(int.MinValue, int.MinValue);
    }

    private void Reposition(Point pos)
    {
        if (_bmp == null || !Visible || pos == _lastPos) return;
        _lastPos = pos;
        bool ok = Layered.Update(Handle, _bmp, pos, 255);
        if (!ok) Log($"ULW FAILED size={_bmp.Width}x{_bmp.Height}");
    }

    /// <summary>渲染投影位图：由外向内叠加多层半透明圆角矩形，线性权重 → 平滑二次衰减。</summary>
    private void Render(Size contentSize)
    {
        _bmp?.Dispose();
        int w = contentSize.Width + _expand * 2;
        int h = contentSize.Height + _expand * 2 + _dropY;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int steps = 15;
        float wsum = steps * (steps + 1) / 2f;
        var core = new RectangleF(_expand, _expand + _dropY, contentSize.Width, contentSize.Height);
        for (int i = steps; i >= 1; i--)
        {
            float expand = _expand * i / steps;
            int a = (int)(_maxAlpha * i / wsum);   // 内层权重高：边缘实、外围快速衰减
            using var br = new SolidBrush(Color.FromArgb(Math.Min(255, a), 0, 0, 0));
            var rc = new RectangleF(core.X - expand, core.Y - expand,
                core.Width + expand * 2, core.Height + expand * 2);
            float corner = _circular ? Math.Min(rc.Width, rc.Height) / 2f : _corner + expand;
            using var path = RoundedPath(rc, corner);
            g.FillPath(br, path);
        }
        _bmp = bmp;
        _bmpForSize = contentSize;
        // 位图已重建：清空位置缓存，强制下次 Reposition 重新提交 ULW。
        // 否则动画最后一帧 FollowOwner 已把最终位置写入缓存，随后的 Sync 渲染出新位图
        // 却因"位置未变"被跳过提交——屏幕上残留旧尺寸位图（阴影比面板小一圈）。
        _lastPos = new(int.MinValue, int.MinValue);
    }

    /// <summary>把阴影窗口钉在 owner 的 z 序正下方（两者都 TopMost 时保持紧贴）。</summary>
    private void PinBelow(Form owner)
    {
        SetWindowPos(Handle, owner.Handle, 0, 0, 0, 0,
            0x0002 /*NOMOVE*/ | 0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/);
    }

    /// <summary>
    /// z 序自愈：其它 TopMost 窗口开关（如关闭设置窗）会触发系统重排，
    /// 可能把本阴影抬到 owner 之上形成半透明黑罩。监测到"紧邻的上一窗口不是 owner"即自动归位。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x0047 /*WM_WINDOWPOSCHANGED*/ && _owner != null && Visible && IsHandleCreated)
        {
            var prev = GetWindow(Handle, 3 /*GW_HWNDPREV*/);
            if (prev != _owner.Handle && prev != Handle) PinBelow(_owner);
        }
    }

    private static GraphicsPath RoundedPath(RectangleF r, float corner)
    {
        var p = new GraphicsPath();
        float d = corner * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bmp?.Dispose(); _bmp = null; }
        base.Dispose(disposing);
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "shadow.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>跨位数安全的 SetWindowLongPtr（32 位进程退回 SetWindowLong）。</summary>
    private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, value);
        else SetWindowLong32(hWnd, nIndex, value.ToInt32());
    }
}
