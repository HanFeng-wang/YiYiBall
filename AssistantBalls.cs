using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ScreenTranslator;

/// <summary>
/// 双悬浮球体系（混合渲染：静态底图 PNG + 实时粒子动效）：
/// - MainBallForm 主球：AI 语音悬浮球。底图 = 品牌蓝紫青渐变毛玻璃球 + 三色细环（浏览器渲染导出）；
///   呼吸光晕 / 待机声波动画 / 双击波纹 / 交互缩放全部 GDI+ 实时绘制。
///   语音模式球内动画按状态分流：倾听＝A4 冲击脉冲（电平驱动）·
///   思考蓝/回复紫/执行青＝B7 三点脉动（仅换色）。
///   状态机：待机呼吸(2.8s) / 悬停1.06 / 按下0.92弹性回弹 / 拖动1.08跟手 /
///   双击唤醒(压缩回弹+双圈波纹) / 贴边吸附露40%。
/// - AssistBallForm 辅助球：仅选中文字时出现在选区旁，单击翻译，超时自动淡出。
///   精简版样式（export.html .ab-core 同参，无 HUD 转线）：白色高光球 + 蓝紫渐变「译」字；
///   出现 = 回弹放大 + 单圈光环，消失 = 加速收缩淡出。笔刷/字体静态缓存，内存占用最小。
/// 窗体采用 UpdateLayeredWindow 逐像素 alpha 分层窗口——底图光晕等半透明像素不再有
/// TransparencyKey 品红色键污染，alpha=0 区域自动鼠标穿透。
/// </summary>
public sealed class MainBallForm : LayeredBallForm
{
    /// <summary>双击主球：打开手动对话面板（无选中词）。</summary>
    public event Action<Point>? DoubleClicked;

    /// <summary>单击主球（约 220ms 内无第二击）：进入截图翻译模式。</summary>
    public event Action<Point>? SingleClicked;

    /// <summary>长按主球 ≥500ms（位移未超拖动阈值）：进入语音对话模式。</summary>
    public event Action? LongPressed;

    /// <summary>语音模式下球心（屏幕坐标）变化（滑出动画/拖动）：白条实时跟随锚定。</summary>
    public event Action<Point>? VoiceAnchorChanged;

    /// <summary>语音模式下的实时音量（由 VoiceSession 提供 RMS）。</summary>
    public Func<float>? VoiceLevelProvider;

    private const int BallD = 56;             // 球体直径（与底图一致）
    private const int SnapPx = 48;            // 拖到边缘 48px 内自动吸附
    private const float DockShowRatio = 0.4f; // 贴边后露出 40%
    private const int RippleLife = 44;        // 波纹寿命 44 帧 ≈ 700ms
    private const int RippleGap = 8;          // 第二圈延迟 ≈ 128ms（预览 120ms）

    private float _breath;                                   // 0..1 呼吸相位（余弦 ease-in-out）
    private float _scale = 1f, _scaleTarget = 1f, _scaleVel; // 弹簧缩放（按下/悬停/拖动/回弹）
    private bool _hover, _pressed, _dragging;
    private Point _pressPoint;
    private readonly List<int> _ripples = new();             // 波纹起始帧（双击两圈）
    private bool _docked;
    private int _dockAnimTick = -1;                          // 吸附滑入动画帧，-1 未在动画中
    private Point _dockAnimFrom, _dockAnimTo;

    // 语音模式从贴边态进入：滑出边缘让白条居中，退出时（期间未被拖动）滑回原贴边位置
    private const int VoicePeekOutMargin = 136;  // 窗体离屏幕边缘的退出量：球完整可见，且常见提示白条可在球下居中
    private bool _voiceReturnDock;
    private Point _voiceDockReturnPoint;
    private Point _lastVoiceAnchor = new(int.MinValue, int.MinValue);

    private enum DockSide { Left, Right }
    private DockSide _side = DockSide.Right;

    // 单/双击判定：松手后挂起约 220ms（14 帧），期间出现双击则取消
    private const int SingleWaitFrames = 14;   // 14 × 16ms ≈ 224ms（接近系统双击阈值）
    private int _pendingSingleTick = -1;       // 挂起单击的起始帧，-1 无挂起
    private Point _pendingSinglePt;
    private bool _suppressNextSingle;          // 双击后的第二次松手不再挂起单击

    // 长按语音：按下 ≥500ms 未松开且未拖动 → 语音模式（与单击/双击互斥）
    private const int LongPressFrames = 31;    // 31 × 16ms ≈ 500ms
    private int _pressStartTick = -1;
    private bool _longPressFired;

    // 语音模式：球放大 1.35 不消失、球内动画按状态分流、状态辉光
    // 倾听（Listening/Recognizing）＝A4 冲击脉冲（电平驱动发射白圈+中心点）；
    // 思考/回复/执行等其余状态＝B7 经典三点脉动（中点白、两侧状态色）
    private bool _voiceMode;
    private VoiceState _voiceState = VoiceState.Listening;
    private float _voiceLvl;                                  // 平滑后的实时电平（0..1+）
    private readonly List<(int Born, float Power)> _pulses = new();  // 已发射冲击圈（起始帧, 电平）
    private int _lastPulseTick = int.MinValue;                // 上一圈发射帧（控制 380ms 间隔）

    /// <summary>是否处于待机态（决定呼吸动画是否生效，与预览 :not(.idle) 一致）。</summary>
    private bool Idle => !_hover && !_pressed && !_dragging && _ripples.Count == 0 && !_voiceMode;

    /// <summary>进入语音模式：球原地放大 1.35 倍不消失、脉冲一次；
    /// 贴边态则整个球滑出屏幕边缘（白条可居中挂在球下），退出时滑回原贴边位置。</summary>
    public void EnterVoiceMode()
    {
        _voiceMode = true;
        _voiceState = VoiceState.Listening;
        _ripples.Add(Tick);                    // 进入瞬间脉冲反馈

        if (_docked)
        {
            // 贴边进入：滑出边缘（球完整可见 + 白条居中余量），记住贴边位置以便回位。
            // 回位条件：语音期间球未被再次拖动（拖动会清掉 _voiceReturnDock）。
            _voiceReturnDock = true;
            _voiceDockReturnPoint = Location;
            _docked = false;
            var wa = Screen.FromControl(this).WorkingArea;
            int x = _side == DockSide.Left
                ? wa.Left + VoicePeekOutMargin
                : wa.Right - Width - VoicePeekOutMargin;
            _dockAnimFrom = Location;
            _dockAnimTo = new Point(x, Top);
            _dockAnimTick = 0;
        }
        else
        {
            _voiceReturnDock = false;
        }
        PeekTarget = 0f;
        Alpha = 1f;
        _scaleTarget = 1.35f;
    }

    /// <summary>退出语音模式：球缩回原尺寸、恢复待机声波；
    /// 若从贴边进入且期间未被拖动，滑回原贴边位置并恢复 40% 露出。</summary>
    public void ExitVoiceMode()
    {
        _voiceMode = false;
        _pulses.Clear();
        _voiceLvl = 0f;
        _scaleTarget = _hover ? 1.06f : 1f;
        if (_voiceReturnDock)
        {
            _voiceReturnDock = false;
            BeginDock(_side, _voiceDockReturnPoint);
        }
    }

    /// <summary>语音会话状态切换（驱动辉光颜色）。</summary>
    public void SetVoiceState(VoiceState s) => _voiceState = s;

    protected override void OnAnimTick()
    {
        Tick++;

        // 语音模式：锁定 1.35 缩放（悬停/按下不改变目标）
        if (_voiceMode) _scaleTarget = 1.35f;

        // 长按判定：按下 ≥500ms 未松开、未拖动 → 语音模式（与单击/双击互斥）
        if (_pressStartTick >= 0 && _pressed && !_dragging && !_longPressFired &&
            Tick - _pressStartTick >= LongPressFrames)
        {
            _pressStartTick = -1;
            if (!_voiceMode)
            {
                _longPressFired = true;
                LongPressed?.Invoke();
            }
        }

        // 待机呼吸：2.8s 周期（175 帧 × 16ms），余弦即 ease-in-out
        _breath = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * (Tick % 175) / 175f));

        // 弹簧缩放：释放时自然过冲回弹（0.92 → 1.03 → 1 的弹性感）
        _scaleVel += (_scaleTarget - _scale) * 0.24f;
        _scaleVel *= 0.66f;
        _scale += _scaleVel;

        // 波纹寿命清理
        _ripples.RemoveAll(t => Tick - t > RippleLife);

        // 贴边露出比例平滑过渡（球在画布内滑动，窗体不动）
        Peek += (PeekTarget - Peek) * 0.22f;

        // 吸附滑入动画：300ms easeOutCubic 移动窗体到边缘
        if (_dockAnimTick >= 0)
        {
            _dockAnimTick++;
            float t = Math.Min(1f, _dockAnimTick / 18f);
            float ease = 1f - (float)Math.Pow(1 - t, 3);
            Location = new Point(
                _dockAnimFrom.X + (int)((_dockAnimTo.X - _dockAnimFrom.X) * ease),
                _dockAnimFrom.Y + (int)((_dockAnimTo.Y - _dockAnimFrom.Y) * ease));
            if (t >= 1f) _dockAnimTick = -1;
        }

        // 挂起单击超时（约 220ms 内无第二击）→ 触发单击（双击时 OnMouseDoubleClick 已取消）
        if (_pendingSingleTick >= 0 && Tick - _pendingSingleTick >= SingleWaitFrames)
        {
            _pendingSingleTick = -1;
            SingleClicked?.Invoke(_pendingSinglePt);
        }

        // 语音模式：球心（屏幕坐标）变化时通知白条跟随（滑出动画/拖动中保持白条居中球下）
        if (_voiceMode)
        {
            var c = BallCenter();
            var anchor = new Point(Location.X + (int)c.X, Location.Y + (int)c.Y);
            if (anchor != _lastVoiceAnchor)
            {
                _lastVoiceAnchor = anchor;
                VoiceAnchorChanged?.Invoke(anchor);
            }
        }
    }

    /// <summary>当前球心（窗体客户区坐标，与 Compose 保持一致）。</summary>
    private PointF BallCenter()
    {
        float r = BallD / 2f;
        float dockCx = _side == DockSide.Left
            ? -(r - DockShowRatio * BallD)
            : Canvas + (r - DockShowRatio * BallD);
        float cx = Canvas / 2f + (dockCx - Canvas / 2f) * Peek;
        return new PointF(cx, Canvas / 2f);
    }

    /// <summary>主球阴影：跟随球心（贴边滑动/拖动/吸附动画时自动跟随），直径固定为球体直径。</summary>
    protected override void UpdateShadow()
    {
        var c = BallCenter();
        SyncShadow(new Rectangle(
            Location.X + (int)c.X - BallD / 2,
            Location.Y + (int)c.Y - BallD / 2, BallD, BallD));
    }

    protected override void Compose(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float cy = Canvas / 2f;
        float r = BallD / 2f;

        // 球心：居中 ↔ 贴边偏移（贴边时窗体留在屏幕内，球体在画布层滑出，只露 40%）
        float dockCx = _side == DockSide.Left
            ? -(r - DockShowRatio * BallD)
            : Canvas + (r - DockShowRatio * BallD);
        float cx = Canvas / 2f + (dockCx - Canvas / 2f) * Peek;

        bool idle = Idle;
        float scale = idle ? 1f + 0.03f * _breath : _scale;   // 待机呼吸 scale 1→1.03→1

        // 状态辉光：语音模式按状态着色（在听绿 / 播报紫 / 其余蓝），待机保持品牌蓝呼吸
        if (_voiceMode)
        {
            var (cr, cg, cb) = _voiceState == VoiceState.Listening ? (34, 197, 94)
                   : _voiceState == VoiceState.Speaking ? (138, 92, 255)
                   : (74, 124, 255);
            DrawGlow(g, cx, cy, r * scale, 0.35f, cr, cg, cb);
        }
        else if (idle) DrawGlow(g, cx, cy, r * scale, 0.20f + 0.25f * _breath, 74, 124, 255);

        // 双击唤醒波纹：两圈由内向外扩散（scale 1→1.8，透明度 0.5→0，青色 2.5px）
        foreach (int start in _ripples)
        {
            float t = (Tick - start) / (float)RippleLife;
            if (t < 0f || t > 1f) continue;
            float rad = r * scale * (1f + 0.8f * t);
            using var p = new Pen(Color.FromArgb((int)(128 * (1 - t)), 56, 214, 255), 2.5f);
            g.DrawEllipse(p, cx - rad, cy - rad, rad * 2, rad * 2);
        }

        // 静态底图（球体+阴影）+ 实时三色环（7s/圈旋转）
        // 球内动画按状态分流：倾听＝A4 冲击脉冲 · 思考/回复/执行＝B7 三点脉动 · 待机＝声波棒
        DrawBase(g, cx, cy, scale);
        DrawRing(g, cx, cy, scale);
        if (!_voiceMode) { DrawWave(g, cx, cy, scale); }
        else if (_voiceState == VoiceState.Listening || _voiceState == VoiceState.Recognizing)
        {
            DrawVoicePulse(g, cx, cy, r, scale);
        }
        else
        {
            DrawStateDots(g, cx, cy, r, scale);
        }
    }

    // 三色环色标：conic blue → purple → cyan → blue（预览 .ring conic-gradient）
    private static readonly (float p, int R, int G, int B)[] RingStops =
    {
        (0f,    74, 124, 255),
        (1f/3f, 138, 92, 255),
        (2f/3f, 56, 214, 255),
        (1f,    74, 124, 255),
    };

    private static Color RingColor(float f, float opacity)
    {
        for (int i = 1; i < RingStops.Length; i++)
        {
            if (f <= RingStops[i].p)
            {
                var a = RingStops[i - 1]; var b = RingStops[i];
                float t = (f - a.p) / (b.p - a.p);
                return Color.FromArgb((int)(255 * opacity),
                    (int)(a.R + (b.R - a.R) * t),
                    (int)(a.G + (b.G - a.G) * t),
                    (int)(a.B + (b.B - a.B) * t));
            }
        }
        return Color.FromArgb((int)(255 * opacity), 74, 124, 255);
    }

    /// <summary>品牌三色细环：60 段圆弧近似 conic 渐变，7s/圈匀速旋转（预览 spin 7s）。</summary>
    private void DrawRing(Graphics g, float cx, float cy, float scale)
    {
        const float radius = 32f;   // inset -4px：球半径 28 + 4
        const float width = 2.2f, opacity = 0.85f;
        const int seg = 60;
        float rot = TickMs / 7000f * 360f;
        var rc = new RectangleF(cx - radius * scale, cy - radius * scale, radius * 2 * scale, radius * 2 * scale);
        for (int i = 0; i < seg; i++)
        {
            float f = i / (float)seg;
            using var p = new Pen(RingColor(f, opacity), width * scale);
            g.DrawArc(p, rc, rot + f * 360f, 360f / seg + 2.5f);   // +2.5° 重叠消除段间缝隙（抗锯齿端点会吃掉约 1°）
        }
    }

    /// <summary>品牌蓝柔光晕：5 圈同心描边近似 blur 光晕（叠加在底图之下），颜色随状态。</summary>
    private static void DrawGlow(Graphics g, float cx, float cy, float r, float alpha, int cr, int cg, int cb)
    {
        for (int i = 0; i < 5; i++)
        {
            float rad = r + 1.5f + i * 3f;
            int alphaI = (int)(255 * alpha * (1f - i / 5f));
            using var p = new Pen(Color.FromArgb(Math.Min(255, alphaI), cr, cg, cb), 3f);
            g.DrawEllipse(p, cx - rad, cy - rad, rad * 2, rad * 2);
        }
    }

    /// <summary>
    /// 语音倾听动画（挑选页 A4 冲击脉冲）：电平超过阈值时每 380ms 发射一圈白色冲击环，
    /// 单圈寿命 1000ms，圈径随发射时电平放大、线宽由粗到细、透明度渐隐；中心常驻白色圆点
    /// 随实时电平胀缩。参数与预览 pulseFx(interval=380, speed=1000, width=0.095, center) 一致。
    /// </summary>
    private void DrawVoicePulse(Graphics g, float cx, float cy, float r, float scale)
    {
        // 采集本帧电平：原始 RMS ×7 标定到 0..1（与预览网页一致），轻平滑去抖
        float raw = VoiceLevelProvider?.Invoke() ?? 0f;
        float lvl = Math.Min(1f, raw * 7f);
        _voiceLvl += (lvl - _voiceLvl) * 0.35f;

        // 发射：电平 > 0.2 时每 380ms（≈24 帧）一圈
        const int PulseInterval = 24;
        const int PulseLife = 62;                    // 单圈寿命 ≈ 1000ms
        if (_voiceLvl > 0.2f && Tick - _lastPulseTick >= PulseInterval)
        {
            _pulses.Add((Tick, _voiceLvl));
            _lastPulseTick = Tick;
        }
        _pulses.RemoveAll(p => Tick - p.Born > PulseLife);

        foreach (var p in _pulses)
        {
            float k = (Tick - p.Born) / (float)PulseLife;
            float rad = r * scale * (0.5f + k * 0.75f) * (0.6f + p.Power * 0.8f);
            // 小球视觉补偿：线宽系数 0.095→0.14 且尾段保底 2px、透明度 0.5→0.78，
            // 否则 38px 半径的球上白环只有 1~3px，叠在亮色渐变上几乎不可见
            using var pen = new Pen(Color.FromArgb((int)(200 * (1 - k)), 255, 255, 255),
                                   r * scale * 0.14f * (1 - k) + 2f);
            g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
        }

        // 中心白点：随电平胀缩（0.12r 静默 → 说话 ~0.28r → 爆音上限 ~0.57r），
        // 静默兜底微呼吸（预览 showLvl 静默值 0.08±0.08）；系数比预览加大补偿小球观感
        float idleLvl = 0.08f + 0.06f * (0.5f - 0.5f * (float)Math.Cos(TickMs * 0.0012));
        float eff = Math.Max(_voiceLvl, idleLvl);
        float core = r * scale * (0.12f + eff * 0.45f);
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, cx - core, cy - core, core * 2, core * 2);
    }

    /// <summary>
    /// 思考/回复/执行状态动画（挑选页 B7 经典三点）：三颗圆点正弦上下浮动 + 半径脉动，
    /// 中点纯白、两侧点着状态色（思考蓝 #6E9BFF / 回复紫 #B28CFF / 执行青 #43DCFF）。
    /// 程序球（r≈38px）比预览卡（r≈100px）小，浮动/点径按观感补偿放大，
    /// 两侧点加细白描边保证在品牌渐变球上清晰可辨。
    /// </summary>
    private void DrawStateDots(Graphics g, float cx, float cy, float r, float scale)
    {
        var tint = _voiceState == VoiceState.Executing ? Color.FromArgb(67, 220, 255)     // 执行青
                 : _voiceState == VoiceState.Speaking ? Color.FromArgb(178, 140, 255)    // 回复紫
                 : Color.FromArgb(110, 155, 255);                                        // 思考蓝
        float tMs = TickMs;
        using var white = new SolidBrush(Color.White);
        using var side = new SolidBrush(tint);
        using var edge = new Pen(Color.White, 1.6f);
        for (int k = 0; k < 3; k++)
        {
            float ph = (float)Math.Sin(tMs * 0.005 - k * 0.9);
            float y = cy + ph * r * 0.28f * scale;
            float x = cx + (k - 1) * r * 0.42f * scale;
            float rr = r * scale * (0.14f + 0.06f * (ph + 1f) / 2f);
            if (k == 1)
            {
                g.FillEllipse(white, x - rr, y - rr, rr * 2, rr * 2);
            }
            else
            {
                g.FillEllipse(side, x - rr, y - rr, rr * 2, rr * 2);
                g.DrawEllipse(edge, x - rr, y - rr, rr * 2, rr * 2);
            }
        }
    }

    // 5 根声波棒基础高度（预览 .wave i:nth-child 的 14/24/34/24/14 px）
    private static readonly float[] WaveBaseH = { 14f, 24f, 34f, 24f, 14f };

    // 相位延迟（秒）：0 / .12 / .24 / .36 / .48，周期 1.1s
    private static readonly float[] WaveDelay = { 0f, 0.12f, 0.24f, 0.36f, 0.48f };

    /// <summary>
    /// 待机声波：5 根白色圆头短棒律动（预览 .wave 同参）。
    /// scaleY 0.75→1.12→0.75，逐根延迟 0.12s 形成流动感。
    /// </summary>
    private void DrawWave(Graphics g, float cx, float cy, float scale)
    {
        float tSec = TickMs / 1000f;
        float barW = 4f * scale;
        float gap = 4f * scale;
        float total = WaveBaseH.Length * barW + (WaveBaseH.Length - 1) * gap;

        using var brush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));

        for (int i = 0; i < WaveBaseH.Length; i++)
        {
            float ph = (tSec + WaveDelay[i]) / 1.1f;
            // 0.75 → 1.12 → 0.75：余弦 ease-in-out
            float sy = 0.75f + 0.37f * (0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * ph));
            float h = WaveBaseH[i] * scale * sy;
            float x = cx - total / 2f + i * (barW + gap);
            float rad = barW / 2f;
            // 圆头：上下各一个半圆 + 中间矩形
            g.FillEllipse(brush, x, cy - h / 2f, barW, barW);
            g.FillEllipse(brush, x, cy + h / 2f - barW, barW, barW);
            g.FillRectangle(brush, x, cy - h / 2f + rad, barW, Math.Max(0f, h - barW));
        }
    }

    // ---------- 交互（与预览 pointer 事件一一对应） ----------

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        _scaleTarget = _voiceMode ? 1.35f : 1.06f;  // 悬停 scale 1.06（语音模式保持 1.35）
        if (_docked && _dockAnimTick < 0)
            PeekTarget = 0f;                        // 贴边态悬停：球滑回完整可见
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _scaleTarget = _voiceMode ? 1.35f : 1f;
        if (_docked && _dockAnimTick < 0 && !_dragging)
            PeekTarget = 1f;                        // 离开：滑回贴边只露 40%
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressPoint = PointToScreen(e.Location);
        _dragging = false;
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            _pressStartTick = Tick;                 // 长按计时起点（语音模式判定）
            _longPressFired = false;
            _scaleTarget = 0.92f;                   // 按下 0.92（120ms 内由弹簧到位）
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left) return;
        var now = PointToScreen(e.Location);
        if (!_dragging && Math.Abs(now.X - _pressPoint.X) + Math.Abs(now.Y - _pressPoint.Y) > 6)
        {
            _dragging = true;
            _pressed = false;
            _voiceReturnDock = false;               // 拖动即视为位置二次改变：退出语音不再回原贴边位
            _scaleTarget = 1.08f;                   // 拖动放大 1.08、跟手
            Alpha = 0.95f;
            if (_docked)
            {
                _docked = false;
                PeekTarget = 0f;
                Alpha = 1f;
            }
        }
        if (_dragging)
        {
            Left = now.X - Canvas / 2;
            Top = now.Y - Canvas / 2;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool wasDragging = _dragging;
        if (_dragging)
        {
            _dragging = false;
            TryDock();                              // 松手 300ms 吸附最近边缘
        }
        if (e.Button == MouseButtons.Left && !wasDragging &&
            !_longPressFired && !_voiceMode)        // 长按/语音模式不触发单击
        {
            if (_suppressNextSingle)
                _suppressNextSingle = false;        // 双击后的第二次松手：不挂起单击
            else
            {
                // 挂起单击，等约 220ms 排除双击后再触发
                _pendingSingleTick = Tick;
                _pendingSinglePt = new Point(Left + Canvas / 2, Top + Canvas / 2);
            }
        }
        _longPressFired = false;
        _pressStartTick = -1;
        _pressed = false;
        _scaleTarget = _voiceMode ? 1.35f : _hover ? 1.06f : 1f;   // 弹簧回弹
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !_dragging && !_voiceMode)
        {
            _pendingSingleTick = -1;                // 双击成立：取消挂起的单击
            _suppressNextSingle = true;             // 并忽略随后到来的第二次松手
            // 双击唤醒：轻微下压由弹簧弹起 + 双圈波纹（间隔 ≈120ms）
            _ripples.Add(Tick);
            _ripples.Add(Tick + RippleGap);
            _scaleVel = -0.12f;
            DoubleClicked?.Invoke(new Point(Left + Canvas / 2, Top + Canvas / 2));
        }
    }

    /// <summary>松手后判断是否吸附：拖到边缘 SnapPx 范围内即滑入贴边。</summary>
    private void TryDock()
    {
        if (_dockAnimTick >= 0) return;
        var wa = Screen.FromControl(this).WorkingArea;
        int targetY = Math.Clamp(Top, wa.Top + 4, wa.Bottom - Height - 4);
        if (Left <= wa.Left + SnapPx)
            BeginDock(DockSide.Left, new Point(wa.Left, targetY));
        else if (Right >= wa.Right - SnapPx)
            BeginDock(DockSide.Right, new Point(wa.Right - Width, targetY));
    }

    private void BeginDock(DockSide side, Point to)
    {
        _side = side;
        _docked = true;
        _dockAnimFrom = Location;
        _dockAnimTo = to;
        _dockAnimTick = 0;
        PeekTarget = 1f;                            // 窗体滑到边缘的同时球体滑出只露 40%
        Alpha = 0.9f;                               // 规格贴边透明度 90%
    }

    /// <summary>常驻显示：屏幕右侧中部。</summary>
    public void ShowResident()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 24, wa.Top + wa.Height / 3);
        _docked = false;
        Peek = PeekTarget = 0f;
        Alpha = 1f;
        Show();
    }
}

/// <summary>
/// 辅助悬浮球：只在划词选中时出现于选区旁，单击立即翻译；
/// 15 秒无操作自动淡出——没有选中文字时绝不会出现。
/// 极简渲染：渐变球体 + 白色"译"字，纯 GDI+，不加载底图；
/// 出现动画 = easeOutBack 弹性放大 + 单圈扩散光环；消失 = 加速收缩 + 线性淡出。
/// </summary>
public sealed class AssistBallForm : LayeredBallForm
{
    public event Action<Point>? Clicked;

    private const int BallD = 44;                   // 球体直径
    private const int LifeMs = 15000;               // 无操作存活时长
    private const int AppearFrames = 16;            // 出现 ≈256ms
    private const int FadeFrames = 10;              // 消失 ≈160ms

    private static readonly Font TextFont = new("Microsoft YaHei UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly StringFormat TextFmt = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };
    // 精简版样式（export.html .ab-core 同参）：白色高光球 + 蓝紫渐变「译」字，全部只创建一次
    private static readonly PathGradientBrush BallFill = CreateBallFill();
    private static readonly LinearGradientBrush TextBrush = new(
        new RectangleF(-BallD / 2f, -BallD / 2f, BallD, BallD),
        Color.FromArgb(74, 124, 255), Color.FromArgb(138, 92, 255), 45f);   // 135° 蓝紫渐变字
    private static readonly SolidBrush ShadowBrush = new(Color.FromArgb(36, 30, 50, 120)); // 0 4px 12px rgba(30,50,120,.14)
    private static readonly Pen HighlightPen = new(Color.FromArgb(220, 255, 255, 255), 1.6f);

    /// <summary>白色高光球：radial at 34% 28% #fff → #f4f7ff(45%) → #e2e8f6，两 stop 近似。</summary>
    private static PathGradientBrush CreateBallFill()
    {
        using var path = new GraphicsPath();
        path.AddEllipse(-BallD / 2f, -BallD / 2f, BallD, BallD);
        var br = new PathGradientBrush(path)
        {
            CenterColor = Color.White,
            CenterPoint = new PointF(-BallD / 2f + BallD * 0.34f, -BallD / 2f + BallD * 0.28f),
            FocusScales = new PointF(0.6f, 0.6f),   // 中段过渡近似 45% 处 #f4f7ff
        };
        br.SurroundColors = new[] { Color.FromArgb(226, 232, 246) };
        return br;
    }

    private int _idleMs;
    private float _appear;                          // 0..1 出现/消失进度
    private int _fadeDir;                           // 1 出现 / 0 静止 / -1 消失

    protected override void OnAnimTick()
    {
        Tick++;
        if (_fadeDir > 0)
        {
            _appear = Math.Min(1f, _appear + 1f / AppearFrames);
            Alpha = Math.Min(1f, _appear * 1.8f);   // 透明度先到位，弹性缩放随后
            if (_appear >= 1f) { _fadeDir = 0; Alpha = 1f; }
        }
        else if (_fadeDir < 0)
        {
            _appear -= 1f / FadeFrames;
            if (_appear <= 0f)
            {
                _appear = 0; _fadeDir = 0; Alpha = 0f;
                Hide(); _anim.Stop();               // 隐藏后停表，零 CPU 占用
            }
            else Alpha = _appear;
        }
        else if (Visible)
        {
            _idleMs += 16;
            if (_idleMs >= LifeMs) BeginFade();
        }
    }

    protected override void Compose(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float t = Math.Clamp(_appear, 0f, 1f);

        // 出现：easeOutBack 弹性放大（有过冲回弹）；消失：加速收缩
        float scale = _fadeDir < 0
            ? 1f - 0.45f * t * t
            : 0.5f + 0.5f * EaseOutBack(t);

        // 出现时的一圈扩散光环（单枚椭圆，最便宜的存在感）
        if (_fadeDir > 0 && t < 0.75f)
        {
            float rt = t / 0.75f;
            float rad = BallD / 2f * (1f + 0.9f * rt);
            using var p = new Pen(Color.FromArgb((int)(110 * (1 - rt)), 74, 124, 255), 2f);
            g.DrawEllipse(p, Canvas / 2f - rad, Canvas / 2f - rad, rad * 2, rad * 2);
        }

        g.TranslateTransform(Canvas / 2f, Canvas / 2f);
        g.ScaleTransform(scale, scale);

        var rc = new RectangleF(-BallD / 2f, -BallD / 2f, BallD, BallD);

        g.FillEllipse(ShadowBrush, rc.X, rc.Y + 3.5f, rc.Width, rc.Height);  // 柔和投影（0 4px 12px）
        g.FillEllipse(BallFill, rc);                                         // 白色高光球体
        g.DrawArc(HighlightPen, rc.X + 1.5f, rc.Y + 1.5f, rc.Width - 3f, rc.Height - 3f,
            200f, 110f);                                                     // 顶部内侧高光（inset 0 1px 1px white）
        g.DrawString("译", TextFont, TextBrush,
            new RectangleF(-BallD / 2f, -BallD / 2f - 0.5f, BallD, BallD), TextFmt);

        g.ResetTransform();
    }

    /// <summary>easeOutBack：先 overshoot 再回落的弹性缓动。</summary>
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    /// <summary>在选区附近出现（弹性放大淡入），重置存活计时。</summary>
    public void ShowNear(Point screenPt)
    {
        var wa = Screen.FromPoint(screenPt).WorkingArea;
        // 球中心贴在选区右下（旧窗体 58px 中心 29 → 新画布 112px 中心 56，偏移差 27px）
        int x = Math.Min(screenPt.X + 10 - (Canvas / 2 - 29), wa.Right - Width - 4);
        int y = Math.Min(screenPt.Y + 14 - (Canvas / 2 - 29), wa.Bottom - Height - 4);
        x = Math.Max(x, wa.Left + 4);
        y = Math.Max(y, wa.Top + 4);
        Location = new Point(x, y);
        _idleMs = 0;
        _appear = 0f;
        _fadeDir = 1;
        Alpha = 0f;
        Show();
        if (!_anim.Enabled) _anim.Start();
    }

    /// <summary>辅助球阴影：跟随球体；淡出期间直接隐藏（收缩中的球不再投影）。</summary>
    protected override void UpdateShadow()
    {
        if (_fadeDir < 0) { HideShadow(); return; }
        SyncShadow(new Rectangle(
            Location.X + Canvas / 2 - BallD / 2,
            Location.Y + Canvas / 2 - BallD / 2, BallD, BallD));
    }

    /// <summary>开始淡出（点击使用后或超时）。</summary>
    public void BeginFade()
    {
        _idleMs = 0;
        _fadeDir = -1;
        if (!_anim.Enabled) _anim.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            Clicked?.Invoke(new Point(Left + Canvas / 2, Top + Canvas / 2));
            BeginFade();
        }
    }
}

/// <summary>
/// 分层悬浮球窗体基类：UpdateLayeredWindow 逐像素 alpha（无色键污染），
/// 静态底图由浏览器从 ball-preview.html 同款 CSS 渲染导出（assets/balls/*_base.png，160px 2x），
/// 动效（光晕/波纹/HUD 弧/声波）由子类 Compose 实时绘制。alpha=0 区域自动鼠标穿透。
/// </summary>
public abstract class LayeredBallForm : Form
{
    protected const int Canvas = 112;               // 画布/窗体边长（容纳 1.8x 波纹 + 光晕）

    protected readonly System.Windows.Forms.Timer _anim;
    protected int Tick;                             // 动画帧（16ms/帧）
    protected float Peek, PeekTarget;               // 0=球居中 1=贴边只露 40%
    protected float Alpha = 1f;                     // 整体不透明度（经 ULW SourceConstantAlpha）

    private static Bitmap? _mainBase, _assistBase;
    private readonly Bitmap _present = new(Canvas, Canvas, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

    protected LayeredBallForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(Canvas, Canvas);
        _anim = new System.Windows.Forms.Timer { Interval = 16 };
        _anim.Tick += (_, _) =>
        {
            OnAnimTick();
            if (Visible) { Present(); UpdateShadow(); }
        };
    }

    protected int TickMs => Tick * 16;

    // ---------- 外挂阴影（DropShadowWindow 按球体区域投影） ----------
    private DropShadowWindow? _shadow;

    /// <summary>把阴影同步到给定球体矩形（屏幕坐标）。首次调用时惰性创建阴影窗。
    /// 球体专用：小扩散圆形阴影（普通窗口的矩形大阴影对球体来说过大）。</summary>
    protected void SyncShadow(Rectangle ballScreen)
    {
        _shadow ??= DropShadowWindow.Attach(this, trackForm: false,
            expand: 5, dropY: 2, maxAlpha: 55, circular: true);
        _shadow.Sync(this, ballScreen);
    }

    /// <summary>隐藏外挂阴影（辅助球淡出时调用）。</summary>
    protected void HideShadow() => _shadow?.Hide();

    /// <summary>完全隐藏（窗体 + 外挂阴影）：进入截图前调用，避免球体入镜。
    /// 再次 Show 后动画帧的 Present/UpdateShadow 会自动恢复画面与阴影。</summary>
    public void HideFully()
    {
        HideShadow();
        Hide();
    }

    /// <summary>每帧在 Present 之后调用：子类按当前球心/状态同步阴影位置（位置未变时零开销）。</summary>
    protected virtual void UpdateShadow() { }

    /// <summary>每帧推进动画状态（16ms 一次）。</summary>
    protected abstract void OnAnimTick();

    /// <summary>每帧合成画面（透明画布，底图 + 实时粒子）。</summary>
    protected abstract void Compose(Graphics g);

    /// <summary>加载静态底图（启动后首次使用时加载一次，常驻内存仅 2 张小图）。</summary>
    protected static Bitmap GetBase(bool main)
    {
        if (main ? _mainBase == null : _assistBase == null)
        {
            var name = main ? "main_base.png" : "assist_base.png";
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "balls", name);
            using var fs = File.OpenRead(path);
            using var tmp = new Bitmap(fs);
            var bmp = new Bitmap(tmp);              // 像素拷贝，释放文件句柄
            if (main) _mainBase = bmp; else _assistBase = bmp;
        }
        return main ? _mainBase! : _assistBase!;
    }

    /// <summary>底图按 2x 源尺寸缩放绘制：160px 图对应 80 逻辑像素，球心对齐 (cx,cy)。</summary>
    protected void DrawBase(Graphics g, float cx, float cy, float scale)
    {
        float size = 80f * scale;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(GetBase(this is MainBallForm), cx - size / 2, cy - size / 2, size, size);
    }

    /// <summary>合成并提交到分层窗口。</summary>
    private void Present()
    {
        using var g = Graphics.FromImage(_present);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        try { Compose(g); }
        catch (Exception ex)
        {
            Log("compose: " + ex.Message);
            return;
        }

        try
        {
            bool ok = Layered.Update(Handle, _present, Location, (int)(255 * Math.Clamp(Alpha, 0f, 1f)));
            if (!ok) Log("ulw returned false, err=" + Marshal.GetLastWin32Error());
            else if (Tick < 2 || Tick % 300 == 0) Log($"tick={Tick} present ok alpha={(int)(255 * Alpha)}");
        }
        catch (Exception ex)
        {
            Log("ulw: " + ex.Message);
        }
    }

    private static void Log(string msg)
    {
        try
        {
            if (_loggedLines >= 30) return;   // 只记录前 30 条，避免刷盘
            _loggedLines++;
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "ball.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private static int _loggedLines;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /*LAYERED*/ | 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _anim.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _present.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>UpdateLayeredWindow P/Invoke：32bpp ARGB 位图 → 逐像素 alpha 分层窗口。</summary>
internal static class Layered
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    private const uint ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    /// <summary>把 ARGB 位图提交为窗体内容（就地预乘 alpha，ULW 要求预乘格式）。返回 false = 调用失败。</summary>
    public static unsafe bool Update(IntPtr hwnd, Bitmap bmp, Point location, int alpha)
    {
        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)sizeof(BITMAPINFOHEADER),
                        biWidth = w, biHeight = -h,      // 负高 = 自上而下
                        biPlanes = 1, biBitCount = 32, biCompression = 0
                    }
                };
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr dib = CreateDIBSection(screenDc, ref bmi, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
                try
                {
                    // 预乘 alpha + 拷贝到 DIB
                    byte* src = (byte*)data.Scan0, dst = (byte*)bits;
                    int n = w * h;
                    for (int i = 0; i < n; i++)
                    {
                        byte a = src[3];
                        dst[0] = (byte)(src[0] * a >> 8);
                        dst[1] = (byte)(src[1] * a >> 8);
                        dst[2] = (byte)(src[2] * a >> 8);
                        dst[3] = a;
                        src += 4; dst += 4;
                    }
                    SelectObject(memDc, dib);

                    var dstPos = new POINT(location.X, location.Y);
                    var size = new SIZE(w, h);
                    var srcPos = new POINT(0, 0);
                    var blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        SourceConstantAlpha = (byte)Math.Clamp(alpha, 0, 255),
                        AlphaFormat = AC_SRC_ALPHA
                    };
                    return UpdateLayeredWindow(hwnd, screenDc, ref dstPos, ref size, memDc, ref srcPos, 0, ref blend, ULW_ALPHA);
                }
                finally { DeleteObject(dib); DeleteDC(memDc); }
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }
        finally { bmp.UnlockBits(data); }
    }
}
