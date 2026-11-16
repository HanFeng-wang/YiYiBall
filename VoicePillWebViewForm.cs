using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Drawing.Drawing2D;

namespace ScreenTranslator;

/// <summary>
/// 球下白条窗体（WebView2 版）：一条简短的白色圆角状态药丸，只显示"在干什么"。
/// 窗体即白条（白色本体 + 圆角 Region 裁剪，白底由 WebView2 不透明白色绘制），阴影与主面板/设置窗同源（DropShadowWindow 外挂投影）；
/// 宽度按文字实时测量（220~456px @96dpi），高度 44。
/// 窗体 WS_EX_TRANSPARENT 点击穿透、不抢焦点；显隐用 Opacity 淡入淡出（与主面板开收动画同策略）。
/// </summary>
public sealed class VoicePillWebViewForm : Form
{
    // 白条几何（96dpi 基准，随 DPI 缩放）：与预览页 voice-pill 同参
    private const int PillH = 44;
    private const int PadX = 18;           // 左右内边距
    private const int DotD = 8, Gap = 9;   // 状态圆点直径 / 圆点与文字间距
    private const float FontPx = 13f, SpacingPx = 0.5f;
    private const int MinW = 220, MaxW = 456;
    // 白条中心 = 球心下方 84px：语音球视觉半径 28×1.35≈38 + 上下净距 24 + 半高 22，
    // 球与白条之间留出明显空隙（物理 px，球位图本身不随 DPI 缩放）
    private const int PillCenterOffsetY = 84;

    private readonly WebView2 _web = new();
    private bool _ready;                       // WebView2 初始化完成，可收发消息
    private DropShadowWindow? _shadow;         // 外挂大阴影（首次显示时创建）
    private Point _center = new(-1, -1);       // 白条中心（屏幕坐标），AnchorTo 时记录

    private string _stateText = "在听…";
    private Color _stateColor = Color.FromArgb(22, 163, 74);
    private bool _statePulse;
    private string? _flashText;                // 短暂浮现的文字（你说的话 / 系统提示）
    private Color _flashColor = Color.FromArgb(41, 46, 54);
    private long _flashUntilTick;
    private readonly System.Windows.Forms.Timer _flashTimer;

    // 显隐淡入淡出（10 帧动画，与主面板开收动画同节奏）
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private bool _fadingOut;
    private int _fadeStep;

    private static readonly Color ColListen = Color.FromArgb(22, 163, 74);   // 在听=绿
    private static readonly Color ColBusy = Color.FromArgb(31, 111, 235);    // 识别/执行=蓝
    private static readonly Color ColSpeak = Color.FromArgb(109, 76, 230);   // 思考/播报=紫
    private static readonly Color ColYou = Color.FromArgb(41, 46, 54);       // 你说的话=深墨
    private static readonly Color ColSys = Color.FromArgb(120, 127, 142);    // 系统提示=灰

    public VoicePillWebViewForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;               // WebView2 加载完成前的兜底白底
        Size = new Size(MinW, PillH);
        UpdateRegion();

        // 白条本体由 WebView2 不透明白底直接绘制。不能用透明背景指望"透出窗体白底"：
        // 在 Region 圆角 + 分层窗体（Opacity 动画）组合下，WebView2 透明会打穿整个窗口，
        // 露出垫在正后方的阴影层位图——白条整体变成灰色蒙罩（实测踩坑）。
        _web.DefaultBackgroundColor = Color.White;
        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _flashTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _flashTimer.Tick += (_, _) =>
        {
            if (_flashText != null && Environment.TickCount64 >= _flashUntilTick)
            {
                _flashText = null;
                UpdateDisplay();
            }
        };

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _fadeTimer.Tick += OnFadeTick;

        _ = Handle;   // 启动即预创建窗口句柄与 WebView2 环境，进入语音模式零等待
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TRANSPARENT 点击穿透（白条不接鼠标）+ NOACTIVATE 不抢焦点 + TOOLWINDOW 不进 Alt-Tab
            // 注：Opacity 淡入淡出由 WinForms 自动补 LAYERED（SetLayeredWindowAttributes，
            //     与主面板动画同机制；不涉及 UpdateLayeredWindow，与 WebView2 子窗口兼容）
            cp.ExStyle |= 0x00000020 /*TRANSPARENT*/ | 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _flashTimer.Start();
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppContext.BaseDirectory, "data", "webview2"));
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot", "voice-pill.html");
            _web.CoreWebView2.Navigate(new Uri(root).AbsoluteUri);
            _ready = true;
            UpdateDisplay();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 白条 WebView2 初始化失败：{ex}\n\n"); } catch { }
        }
    }

    /// <summary>锚定到球正下方（屏幕坐标球心）：白条中心 = 球心下方 84px，宽度自适应居中。</summary>
    public void AnchorTo(Point ballCenterScreen)
    {
        _center = ballCenterScreen;
        ApplyLayout();
    }

    /// <summary>设置当前状态文字（白条常显内容）。</summary>
    public void SetState(VoiceState s)
    {
        (_stateText, _stateColor, _statePulse) = s switch
        {
            VoiceState.Listening  => ("在听…",   ColListen, false),
            VoiceState.Recognizing => ("识别中…", ColBusy,  false),
            VoiceState.Routing    => ("思考中…", ColSpeak, true),
            VoiceState.Thinking   => ("思考中…", ColSpeak, true),
            VoiceState.Planning   => ("思考中…", ColSpeak, true),
            VoiceState.Executing  => ("执行中…", ColBusy,  false),
            VoiceState.Speaking   => ("播报中…", ColSpeak, false),
            _                     => ("在听…",   ColListen, false),
        };
        UpdateDisplay();
    }

    /// <summary>短暂浮现一轮（you=你说的话 / sys=提示），~2.5s 后回到状态文字。</summary>
    public void AddTurn(string who, string text)
    {
        if (who == "ai") return;   // 播报本身有状态提示，不重复
        _flashText = text.Length > 30 ? text[..30] + "…" : text;
        _flashColor = who == "you" ? ColYou : ColSys;
        _flashUntilTick = Environment.TickCount64 + 2500;
        if (!_flashTimer.Enabled) _flashTimer.Start();
        UpdateDisplay();
    }

    public void ClearTurns()
    {
        _flashText = null;
        UpdateDisplay();
    }

    public new void Show()
    {
        _fadingOut = false;
        ApplyLayout();
        Opacity = 0;
        base.Show();
        GetShadow().Sync(this);    // 阴影随内容一起出现
        _fadeStep = 0;
        _fadeTimer.Start();
        Push();
    }

    public new void Hide()
    {
        _flashText = null;
        if (Visible)
        {
            _fadingOut = true;
            _fadeStep = 0;
            _fadeTimer.Start();
        }
        _shadow?.Hide();           // 阴影先撤（与主面板收起动画同策略）
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        const int Ticks = 10;
        _fadeStep++;
        float t = Math.Min(1f, _fadeStep / (float)Ticks);
        float ease = 1 - (1 - t) * (1 - t);   // easeOut
        if (_fadingOut)
        {
            Opacity = 1f - ease;
            if (t >= 1f) { _fadeTimer.Stop(); Opacity = 1f; base.Hide(); }
        }
        else
        {
            Opacity = 0.15f + 0.85f * ease;
            if (t >= 1f) _fadeTimer.Stop();
        }
    }

    /// <summary>文字或状态变化：重排版（宽度自适应）+ 推送内容到 HTML。</summary>
    private void UpdateDisplay()
    {
        ApplyLayout();
        Push();
    }

    /// <summary>按当前文字测量宽度 → 调整尺寸与位置（保持锚点居中）→ 同步外挂阴影。</summary>
    private void ApplyLayout()
    {
        var size = MeasurePill(CurrentText());
        if (Size != size) Size = size;   // 触发 OnResize → 圆角 Region 更新
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Left = Math.Clamp(_center.X - size.Width / 2, 8, wa.Right - size.Width - 8);
        Top = Math.Clamp(_center.Y + PillCenterOffsetY - size.Height / 2, 8, wa.Bottom - size.Height - 8);
        if (Visible) GetShadow().Sync(this);
    }

    private string CurrentText() => _flashText ?? _stateText;

    /// <summary>按 HTML 同参排版测量白条尺寸（13px 微软雅黑 + 0.5px 字距 + 圆点/内边距）。</summary>
    private Size MeasurePill(string text)
    {
        float s = DpiScale;
        using var font = new Font("Microsoft YaHei UI", FontPx * s, GraphicsUnit.Pixel);
        int textW = TextRenderer.MeasureText(text, font).Width
                  + (int)Math.Ceiling(SpacingPx * s * text.Length);   // HTML letter-spacing 同参
        int w = Math.Clamp(PadX * 2 + DotD + Gap + textW, (int)(MinW * s), (int)(MaxW * s));
        return new Size(w, (int)Math.Round(PillH * s));
    }

    private float DpiScale => DeviceDpi / 96f;

    /// <summary>外挂大阴影：与主面板/设置窗同参（expand 8 / maxAlpha 35），圆角与药丸一致（全圆端）。</summary>
    private DropShadowWindow GetShadow()
    {
        if (_shadow == null)
        {
            _shadow = DropShadowWindow.Attach(this, trackForm: true,
                expand: (int)Math.Round(8 * DpiScale), dropY: 0,
                corner: Height / 2, maxAlpha: 35, circular: false);
        }
        return _shadow;
    }

    /// <summary>药丸圆角裁剪：圆角 = 高度一半（全圆端）。</summary>
    private void UpdateRegion()
    {
        int d = Height;
        var p = new GraphicsPath();
        p.AddArc(0, 0, d, d, 180, 90);
        p.AddArc(Width - d, 0, d, d, 270, 90);
        p.AddArc(Width - d, Height - d, d, d, 0, 90);
        p.AddArc(0, Height - d, d, d, 90, 90);
        p.CloseFigure();
        Region?.Dispose();
        Region = new Region(p);
        p.Dispose();
    }

    /// <summary>把当前显示内容推给 HTML（白条文字 / 圆点颜色 / 脉冲；显隐由窗体 Opacity 承担）。</summary>
    private void Push()
    {
        if (!_ready) return;
        bool flashing = _flashText != null;
        var c = flashing ? _flashColor : _stateColor;
        var msg = new
        {
            text = flashing ? _flashText : _stateText,
            color = $"rgb({c.R},{c.G},{c.B})",
            pulse = !flashing && _statePulse,
        };
        try { _web.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(msg)); }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _flashTimer.Dispose(); _fadeTimer.Dispose(); _web.Dispose(); }
        base.Dispose(disposing);
    }
}
