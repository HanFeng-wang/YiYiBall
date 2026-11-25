using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScreenTranslator;

/// <summary>
/// WebView2 设置窗口：微信输入法式布局（左侧白卡导航 + 右侧分组卡片），
/// 无边框圆角 + 大外置柔和阴影（DropShadowWindow），标题/品牌区可拖动。
/// 协议（camelCase）：ready → config；save → saved；close；openDataDir；drag。
/// </summary>
public sealed class SettingsWindowForm : Form
{
    private const int Corner = 14;
    private readonly AppConfig _cfg;
    private readonly MemoryService? _memory;
    private readonly WebView2 _webView = new();
    private DropShadowWindow? _shadow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsWindowForm(AppConfig cfg, MemoryService? memory = null)
    {
        _cfg = cfg;
        _memory = memory;
        Text = "依依灵球 设置";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = false; // 不常驻置顶（用户要求）；被遮挡时从托盘重新打开即可
        ClientSize = new Size(1000, 830);
        BackColor = Color.FromArgb(244, 246, 251);   // 页面底色：加载期兜底
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) =>
        {
            _shadow ??= DropShadowWindow.Attach(this);
            _shadow.Sync(this);
        };

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.FromArgb(244, 246, 251); // 加载期显示页面底色，避免闪白
        Controls.Add(_webView);
        _ = InitWebViewAsync();
        UpdateRegion();
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

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int corner)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = corner * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppContext.BaseDirectory, "data", "webview2"));
            await _webView.EnsureCoreWebView2Async(env);
            // 应用型窗口：禁用浏览器快捷键（Ctrl+滚轮/Ctrl+加减号缩放、Ctrl+F 查找等），
            // 防止误缩放把布局压窄（说明文字会被挤成竖条）
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot", "settings.html");
            _webView.CoreWebView2.Navigate(new Uri(root).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "WebView2 运行时初始化失败：" + ex.Message +
                "\n\n请安装 Microsoft Edge WebView2 Runtime（Windows 10/11 一般已内置）。",
                "依依灵球 YiYi Ball", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Post(object msg)
    {
        try { _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg, JsonOpts)); }
        catch { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    Post(ConfigPayload("config"));
                    break;
                case "save":
                    ApplyAndSave(root);
                    Post(ConfigPayload("saved"));   // 回传保存后的最新值，UI 立即同步（避免界面残留旧勾选）
                    ScheduleRestart();
                    break;
                case "close":
                    Close();
                    break;
                case "openDataDir":
                    var dir = Path.Combine(AppContext.BaseDirectory, "data");
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                    break;
                case "openExternal":
                    // 用系统默认浏览器打开外链（WebView2 内 target=_blank 会弹内嵌窗，不走默认浏览器）
                    var extUrl = root.GetProperty("url").GetString() ?? "";
                    if (Uri.TryCreate(extUrl, UriKind.Absolute, out var extUri)
                        && (extUri.Scheme == Uri.UriSchemeHttp || extUri.Scheme == Uri.UriSchemeHttps))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = extUri.AbsoluteUri,
                            UseShellExecute = true
                        });
                    }
                    break;
                case "listMemories":
                    Task.Run(() =>
                    {
                        var items = _memory?.ListMemories()
                            .Select(m => new { id = m.id, content = m.content, updatedAt = m.updatedAt })
                            .ToArray() ?? Array.Empty<object>();
                        Post(new { type = "memories", items });
                    });
                    break;
                case "updateMemory":
                    long mid = root.GetProperty("id").GetInt64();
                    string mcontent = root.GetProperty("content").GetString() ?? "";
                    if (_memory != null && mcontent.Trim().Length > 0)
                        _ = Task.Run(async () =>
                        {
                            try { await _memory.UpdateMemoryAsync(mid, mcontent); Post(new { type = "memoriesSaved" }); }
                            catch (Exception ex) { Post(new { type = "error", message = "记忆保存失败：" + ex.Message }); }
                        });
                    break;
                case "deleteMemory":
                    if (_memory != null)
                    {
                        _memory.DeleteMemory(root.GetProperty("id").GetInt64());
                        Task.Run(() => Post(new { type = "memoriesSaved" }));
                    }
                    break;
                case "listSkills":
                    Task.Run(() =>
                    {
                        var items = _memory?.ListSkills()
                            .Select(s => new { id = s.id, name = s.name, actions = s.actions })
                            .ToArray() ?? Array.Empty<object>();
                        Post(new { type = "skills", items });
                    });
                    break;
                case "deleteSkill":
                    if (_memory != null)
                    {
                        _memory.DeleteSkill(root.GetProperty("id").GetInt64());
                        Task.Run(() => Post(new { type = "skillsSaved" }));
                    }
                    break;
                case "scanKb":
                    if (_memory != null)
                    {
                        // 扫描用前端当前填写的文件夹（不必先点保存）；不落盘
                        if (root.TryGetProperty("folders", out var fo) && fo.ValueKind == JsonValueKind.Array)
                        {
                            var folders = fo.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString() ?? "")
                                .Where(s => s.Trim().Length > 0)
                                .Select(s => s.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (folders.Count > 0) _cfg.KbFolders = folders;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int n = await _memory.ScanKbAsync();
                                Post(new { type = "kbScanDone", updated = n });
                            }
                            catch (Exception ex)
                            {
                                Post(new { type = "kbScanDone", updated = -1, message = ex.Message });
                            }
                        });
                    }
                    break;
                case "drag":
                    BeginDrag();
                    break;
            }
        }
        catch
        {
            // 忽略非法消息
        }
    }

    private object ConfigPayload(string type) => new
    {
        type,
        baseUrl = _cfg.BaseUrl,
        // 隐私：已保存的 API Key 不回显到界面，输入框保持空白（留空保存 = 保持现有 Key 不变）
        apiKey = "",
        translationModel = _cfg.TranslationModel,
        chatModel = _cfg.ChatModel,
        ocrModel = _cfg.OcrModel,
        titleModel = _cfg.TitleModel,
        autoSpeak = _cfg.AutoSpeak,
        captureSelectedText = _cfg.CaptureSelectedText,
        showScreenshotTip = _cfg.ShowScreenshotTip,
        autoStart = _cfg.AutoStart,
        hotkeys = _cfg.Hotkeys,
        historyLimit = _cfg.HistoryLimit,
        // 语音对话（设置保存后需重启生效）
        voiceEnabled = _cfg.VoiceEnabled,
        wakeWordEnabled = _cfg.WakeWordEnabled,
        micSensitivity = _cfg.MicSensitivity,
        micDevice = _cfg.MicDevice,
        asrModel = _cfg.AsrModel,
        ttsCosyVoiceUrl = _cfg.TtsCosyVoiceUrl,
        micDevices = ListMics(),
        // 模型页集中管理：意图路由 / 操作规划 / 屏幕定位
        intentModel = _cfg.IntentModel,
        planModel = _cfg.PlanModel,
        groundModel = _cfg.GroundModel,
        // 对话行为
        autoSpeakReply = _cfg.AutoSpeakReply,
        contextTurns = _cfg.ContextTurns,
        systemPrompt = _cfg.SystemPrompt,
        // 记忆与知识库（方案 §10）
        embeddingModel = _cfg.EmbeddingModel,
        kbFolders = _cfg.KbFolders
    };

    /// <summary>枚举系统输入设备（与 VoiceRecorder 的 WaveIn 序号一致），供下拉框选择录音设备。</summary>
    private static string[] ListMics()
    {
        try
        {
            int n = NAudio.Wave.WaveInEvent.DeviceCount;
            var list = new string[n];
            for (int i = 0; i < n; i++)
                list[i] = NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName;
            return list;
        }
        catch { return Array.Empty<string>(); }
    }

    private void ApplyAndSave(JsonElement root)
    {
        static string S(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        _cfg.BaseUrl = S(root, "baseUrl").Trim();
        // 隐私：界面不回显已保存的 Key，输入框留空 = 保持现有值；只有用户输入了新值才覆盖
        var apiKey = S(root, "apiKey").Trim();
        if (apiKey.Length > 0) _cfg.ApiKey = apiKey;
        _cfg.TranslationModel = S(root, "translationModel").Trim();
        _cfg.ChatModel = S(root, "chatModel").Trim();
        var ocrModel = S(root, "ocrModel").Trim();
        if (ocrModel.Length > 0) _cfg.OcrModel = ocrModel;   // 留空时保留现有值，避免清空导致 OCR 不可用
        var titleModel = S(root, "titleModel").Trim();
        if (titleModel.Length > 0) _cfg.TitleModel = titleModel;   // 留空时保留现有值，避免清空导致标题生成失败
        if (root.TryGetProperty("autoSpeak", out var sp))
            _cfg.AutoSpeak = sp.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("captureSelectedText", out var cst))
            _cfg.CaptureSelectedText = cst.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("showScreenshotTip", out var st))
            _cfg.ShowScreenshotTip = st.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("autoStart", out var au) &&
            (au.ValueKind == JsonValueKind.True || au.ValueKind == JsonValueKind.False))
            _cfg.AutoStart = au.ValueKind == JsonValueKind.True;
        _cfg.ApplyAutoStart();   // 开机自启：按最新勾选同步 HKCU Run 注册表项
        // 自定义全局快捷键（设置 > 快捷键）：整体覆盖式保存，空串 = 未启用
        bool hotkeysTouched = false;
        if (root.TryGetProperty("hotkeys", out var hks) && hks.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string>();
            foreach (var p in hks.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    dict[p.Name] = p.Value.GetString() ?? "";
            _cfg.Hotkeys = dict;
            hotkeysTouched = true;
        }
        if (root.TryGetProperty("historyLimit", out var hl) && hl.TryGetInt32(out var n))
            _cfg.HistoryLimit = Math.Clamp(n, 50, 5000);
        // 语音对话
        if (root.TryGetProperty("voiceEnabled", out var ve))
            _cfg.VoiceEnabled = ve.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("wakeWordEnabled", out var we))
            _cfg.WakeWordEnabled = we.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("micSensitivity", out var ms) && ms.TryGetDouble(out var sen))
            _cfg.MicSensitivity = Math.Clamp(sen, 0, 1);
        if (root.TryGetProperty("micDevice", out var md) && md.TryGetInt32(out var dev))
            _cfg.MicDevice = Math.Max(0, dev);
        var asrModel = S(root, "asrModel").Trim();
        if (asrModel.Length > 0) _cfg.AsrModel = asrModel;   // 留空时保留现有值
        _cfg.TtsCosyVoiceUrl = S(root, "ttsCosyVoiceUrl").Trim();   // 留空 = 系统语音兜底，允许清空
        // 模型页：意图路由 / 操作规划 / 屏幕定位（留空时保留现有值）
        var intentModel = S(root, "intentModel").Trim();
        if (intentModel.Length > 0) _cfg.IntentModel = intentModel;
        var planModel = S(root, "planModel").Trim();
        if (planModel.Length > 0) _cfg.PlanModel = planModel;
        var groundModel = S(root, "groundModel").Trim();
        if (groundModel.Length > 0) _cfg.GroundModel = groundModel;
        // 对话行为
        if (root.TryGetProperty("autoSpeakReply", out var ar) &&
            (ar.ValueKind == JsonValueKind.True || ar.ValueKind == JsonValueKind.False))
            _cfg.AutoSpeakReply = ar.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("contextTurns", out var ctv) && ctv.TryGetInt32(out var ct))
            _cfg.ContextTurns = Math.Clamp(ct, 1, 50);
        _cfg.SystemPrompt = S(root, "systemPrompt");   // 留空 = 内置默认人设，允许清空
        // 记忆与知识库（方案 §10）：embedding 模型留空保留现有值；文件夹一行一个
        var emb = S(root, "embeddingModel").Trim();
        if (emb.Length > 0) _cfg.EmbeddingModel = emb;
        if (root.TryGetProperty("kbFolders", out var kf) && kf.ValueKind == JsonValueKind.Array)
        {
            _cfg.KbFolders = kf.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(s => s.Trim().Length > 0)
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        _cfg.Save();
        if (hotkeysTouched) HotkeysChanged?.Invoke();   // 快捷键保存后立即重新注册生效（无需重启）
    }

    /// <summary>保存后延迟片刻（让提示吐司可见）再请求重启。</summary>
    public event Action? RestartRequested;

    /// <summary>快捷键设置保存后触发（主程序重新注册全局热键）。</summary>
    public event Action? HotkeysChanged;

    private void ScheduleRestart()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            RestartRequested?.Invoke();
        };
        timer.Start();
    }

    // ---------- 无边框拖动：模拟标题栏拖拽 ----------

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void BeginDrag()
    {
        ReleaseCapture();
        _ = SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
    }
}
