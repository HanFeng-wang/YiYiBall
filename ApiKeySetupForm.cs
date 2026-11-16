using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScreenTranslator;

/// <summary>
/// 首次使用引导窗：ApiKey 为空时弹出，WebView2 承载 wwwroot/first-run.html。
/// 只需输入一个 API Key 即可开始使用，其余服务配置已随安装包内置。
/// 消息桥（与 first-run.html 约定）：
///   JS → C#  { type: "openExternal", url }  用系统默认浏览器打开平台 API-KEY 页
///   JS → C#  { type: "save", apiKey, baseUrl }  保存并关闭引导窗
///   JS → C#  { type: "quit" }  「暂不使用」退出应用
/// </summary>
public sealed class ApiKeySetupForm : Form
{
    private const int Corner = 14;

    /// <summary>用户输入的 API Key（DialogResult.OK 时有效）。</summary>
    public string ApiKey => _apiKey;

    /// <summary>用户确认后的服务地址（DialogResult.OK 时有效）。</summary>
    public string BaseUrl => _baseUrl;

    private string _apiKey = "";
    private string _baseUrl = "";
    private readonly string? _initialBaseUrl;
    private WebView2? _webView;
    private DropShadowWindow? _shadow;
    private bool _revealed;    // 淡入揭示标记：窗口以全透明创建，内容就绪并完成尺寸校准后才显示
    private bool _sizeDone;    // 高度校准完成标记（宽度固定 490 不调，只自适应高度）
    private System.Windows.Forms.Timer? _vpDebounce;   // viewport 防抖：WebView2 初始化/改尺寸过程中会上报失真值，安静后取最后一条
    private double _vpW, _vpH, _vpInnerH;   // 最近上报的 CSS 视口宽 / 内容自然高 / 视口高

    public ApiKeySetupForm(string? initialBaseUrl = null)
    {
        _initialBaseUrl = initialBaseUrl;
        Text = "依依灵球 YiYi Ball";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        ClientSize = new Size(490, 630);   // 兜底尺寸；实际由 JS 上报的内容尺寸自适应校正
        BackColor = Color.FromArgb(244, 246, 251);   // 与 first-run.html 底色一致，加载期兜底
        Opacity = 0;   // 防闪屏：等内容渲染 + 尺寸校准完成后整体淡入，避免"空窗→内容蹦出→再跳一下"

        _webView = new WebView2 { Dock = DockStyle.Fill };
        _webView.DefaultBackgroundColor = Color.FromArgb(244, 246, 251);
        Controls.Add(_webView);
        _ = InitWebViewAsync();
        UpdateRegion();

        Shown += (_, _) =>
        {
            _shadow ??= DropShadowWindow.Attach(this);   // 阴影等揭示时再 Sync 显示，避免先于内容出现
        };
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppContext.BaseDirectory, "data", "webview2"));
            await _webView!.EnsureCoreWebView2Async(env);
            // 禁用浏览器快捷键（Ctrl+滚轮缩放等），防止误缩放破坏尺寸校准
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot", "first-run.html");
            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            _webView.CoreWebView2.Navigate(new Uri(root).AbsoluteUri);
            // 兜底揭示：若 1.5s 内 JS 尺寸上报未到达（异常情况），也照常淡入，避免永远透明
            var fallback = new System.Windows.Forms.Timer { Interval = 1500 };
            fallback.Tick += (_, _) => { fallback.Stop(); fallback.Dispose(); _ = RevealAsync(); };
            fallback.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "WebView2 运行时初始化失败：" + ex.Message +
                "\n\n请安装 Microsoft Edge WebView2 Runtime（Windows 10/11 一般已内置）。",
                "依依灵球 YiYi Ball", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    // ---------- JS → C# ----------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    // 预填当前配置的服务地址（一般即内置直连地址）
                    _webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "config",
                        baseUrl = _initialBaseUrl ?? ""
                    }));
                    break;

                case "openExternal":
                    var url = root.GetProperty("url").GetString() ?? "";
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = uri.AbsoluteUri,
                            UseShellExecute = true
                        });
                    }
                    break;

                case "save":
                    _apiKey = root.GetProperty("apiKey").GetString()?.Trim() ?? "";
                    _baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString()?.Trim() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(_apiKey)) return;   // HTML 已拦截，这里双保险
                    DialogResult = DialogResult.OK;
                    Close();
                    break;

                case "viewport":
                    // JS 上报 CSS 视口与内容自然高度。WebView2 初始化/改尺寸过程中会上报失真值，
                    // 所以防抖 200ms 取最后一条，且只采信"横纵比例一致"的稳定值；宽度固定不动，只调高度。
                    _vpW = root.GetProperty("w").GetDouble();
                    _vpH = root.GetProperty("contentH").GetDouble();
                    _vpInnerH = root.TryGetProperty("h", out var ih) ? ih.GetDouble() : 0;
                    _vpDebounce ??= new System.Windows.Forms.Timer { Interval = 200 };
                    _vpDebounce.Tick -= OnViewportDebounced;
                    _vpDebounce.Tick += OnViewportDebounced;
                    _vpDebounce.Start();
                    break;

                case "quit":
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
            }
        }
        catch
        {
            // 忽略非法消息
        }
    }

    // ---------- 窗口尺寸自适应（宽度固定 490，只自适应高度） ----------

    private void OnViewportDebounced(object? sender, EventArgs e)
    {
        _vpDebounce!.Stop();
        if (_webView == null || !IsHandleCreated || _sizeDone || _vpW <= 0) return;
        double scaleW = _vpW / _webView.ClientSize.Width;
        double scaleH = _vpInnerH > 0 ? _vpInnerH / ClientSize.Height : scaleW;
        Log($"viewport cssW={_vpW:F1} cssH={_vpInnerH:F1} contentH={_vpH:F1} client={_webView.ClientSize.Width}x{ClientSize.Height} scaleW={scaleW:F4} scaleH={scaleH:F4}");

        // 只采信横纵比例一致的稳定上报（WebView2 初始化期会有失真值，如 cssW=1068 之类）
        if (scaleW < 0.3 || scaleW > 1.5 || Math.Abs(scaleW - scaleH) > 0.03 * scaleW) return;

        // 宽度固定 490（保持窄版外观），只按实测内容高度把高度调到刚好填满
        _sizeDone = true;
        ResizeTo(ClientSize.Width, (int)Math.Round(_vpH / scaleW));
        Log($"final clientSize={ClientSize.Width}x{ClientSize.Height}");
        _ = RevealAsync();
    }

    /// <summary>临时调试日志：记录尺寸校准过程，定位异常后可移除。</summary>
    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "data", "firstrun-size.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
        }
        catch { }
    }

    private void ResizeTo(int w, int h)
    {
        if (w == ClientSize.Width && h == ClientSize.Height) return;
        ClientSize = new Size(w, h);
        // 尺寸变了，重新居中；已揭示状态下同步外置阴影（未揭示时由 RevealAsync 首次 Sync）
        var screen = Screen.FromControl(this).WorkingArea;
        Location = new Point(screen.Left + (screen.Width - Width) / 2,
                             screen.Top + (screen.Height - Height) / 2);
        if (_revealed) _shadow?.Sync(this);
    }

    /// <summary>整体淡入：内容与尺寸都就绪后才让窗口出现，消除启动闪屏。</summary>
    private async Task RevealAsync()
    {
        if (_revealed || IsDisposed || Disposing) return;
        _revealed = true;
        _shadow?.Sync(this);   // 阴影与窗口同帧出现
        while (Opacity < 1 && !IsDisposed)
        {
            Opacity = Math.Min(1, Opacity + 0.18);   // 约 6 帧淡入 ≈ 100ms
            await Task.Delay(16);
        }
        Opacity = 1;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /*TOOLWINDOW*/;
            return cp;
        }
    }

    private void UpdateRegion()
    {
        Region = new Region(RoundedPath(ClientRectangle, Corner));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private static GraphicsPath RoundedPath(Rectangle r, int corner)
    {
        var p = new GraphicsPath();
        int d = corner * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
