using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScreenTranslator;

/// <summary>
/// 翻译+对话面板：WebView2 承载前端 UI，C# 负责流式 AI、TTS、历史、设置入口。
/// 参考 IME 候选面板：无边框圆角、紧贴悬浮球出现、可关闭收回。
/// </summary>
public sealed class MainWindowForm : Form
{
    private const int Corner = 14;
    private readonly AiService _ai;
    private readonly HistoryStore _history;
    private readonly TtsService _tts;
    private readonly AppConfig _cfg;
    private readonly MemoryService? _memory;

    private WebView2? _webView;
    private DropShadowWindow? _shadow;
    private List<(string role, string content)> _messages = new();
    private string _currentWord = "";
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _assistantBuf = new();
    private bool _locked;          // 截图识别中：面板只允许关闭设置入口，其余交互全部禁用
    private bool _screenshotFlow;  // 本次 RunChatAsync 属于截图首轮（结束后解锁并生成标题）
    private bool _pendingTitle;    // 划词首词过长：首轮输出完成后需要重写标题

    // 开/收动画状态
    private readonly System.Windows.Forms.Timer _animTimer = null!;
    private Rectangle _finalBounds;
    private int _animStep, _animTicks;
    private bool _animClosing;
    private Action? _animDone;

    public event Action? ClosedByUser;

    /// <summary>截图翻译锁定解除（首轮输出完成/失败）：Program 侧恢复主球显示。</summary>
    public event Action? Unlocked;

    /// <summary>面板内点「设置」时触发：由主程序统一打开设置窗口（含保存重启订阅、快捷键订阅与已开窗口去重）。</summary>
    public Action? OpenSettingsRequested;

    public MainWindowForm(AppConfig cfg, AiService ai, HistoryStore history, TtsService tts, MemoryService? memory = null)
    {
        _cfg = cfg; _ai = ai; _history = history; _tts = tts; _memory = memory;
        Text = "依依灵球 YiYi Ball";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(440, 580);
        BackColor = Color.FromArgb(247, 248, 250);       // 页面底色：加载期兜底
        DoubleBuffered = true;

        _webView = new WebView2 { Dock = DockStyle.Fill };
        _webView.DefaultBackgroundColor = Color.FromArgb(247, 248, 250); // 加载期显示页面底色，避免闪白
        Controls.Add(_webView);
        _ = InitWebViewAsync();
        UpdateRegion();

        // 开/收动画计时器（缩放 + 淡入淡出）
        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += OnAnimTick;
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

    // ---------- 圆角裁剪 ----------

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

    // ---------- 打开 / 收起动画（缩放 + 淡入淡出） ----------

    private void OnAnimTick(object? sender, EventArgs e)
    {
        _animStep++;
        float t = Math.Min(1f, _animStep / (float)_animTicks);
        float ease = _animClosing ? 1 - (1 - t) * (1 - t) : 1 - (float)Math.Pow(1 - t, 3); // easeOut
        float scale = _animClosing ? 1f - 0.07f * ease : 0.93f + 0.07f * ease;
        Opacity = _animClosing ? 1f - ease : 0.15f + 0.85f * ease;
        ApplyAnimScale(scale);
        if (t < 1f) return;

        _animTimer.Stop();
        var done = _animDone;
        _animDone = null;
        if (_animClosing)
        {
            Hide();
            Opacity = 1f;
            Size = _finalBounds.Size;
            Location = _finalBounds.Location;
        }
        else
        {
            // 打开动画结束：补上外置阴影（动画期间以 93% 尺寸跟随，结束时按最终尺寸渲染）
            _shadow ??= DropShadowWindow.Attach(this);
            _shadow.Sync(this);
        }
        done?.Invoke();
    }

    private void ApplyAnimScale(float s)
    {
        int w = Math.Max(80, (int)(_finalBounds.Width * s));
        int h = Math.Max(60, (int)(_finalBounds.Height * s));
        Size = new Size(w, h);
        Location = new Point(
            _finalBounds.X + (_finalBounds.Width - w) / 2,
            _finalBounds.Y + (_finalBounds.Height - h) / 2);
    }

    /// <summary>收起面板：缩放收缩 + 淡出后再隐藏（会话已保存）。</summary>
    private void CloseAnimated()
    {
        if (_locked) return;   // 截图识别+首轮输出期间面板不可关闭（设置按钮不受影响）
        FinishSession();
        if (!Visible) { ClosedByUser?.Invoke(); return; }
        if (_animTimer.Enabled && _animClosing) return;   // 收起动画进行中，避免重复触发来回抖动
        _finalBounds = Bounds;
        _animClosing = true;
        _shadow?.Hide();   // 收缩动画开始即隐藏阴影，避免与缩小的窗口脱节
        _animStep = 0;
        _animTicks = 7;
        var handler = ClosedByUser;
        _animDone = handler == null ? (Action?)null : () => handler();
        _animTimer.Start();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppContext.BaseDirectory, "data", "webview2"));
            await _webView!.EnsureCoreWebView2Async(env);
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
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

    // ---------- C# → JS ----------

    // 统一 camelCase：HistoryItem.Msg 的 Role/Content 会序列化为 role/content，与前端约定一致
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void Post(object msg)
    {
        var json = JsonSerializer.Serialize(msg, JsonOpts);
        var brief = json.Length > 200 ? json[..200] + "..." : json;
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "webmsg.log"),
            $"[C2JS] {brief}\n"); } catch { }
        try
        {
            _webView?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "webmsg.log"),
                $"[C2JS-FAIL] {ex.Message}\n"); } catch { }
        }
    }

    // ---------- JS → C# ----------

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "webmsg.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] {e.WebMessageAsJson}\n"); } catch { }
        try
        {
            // 兼容两种消息：对象 JSON 或被引号包裹的字符串化 JSON（旧格式）
            JsonDocument? inner = null;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    inner = JsonDocument.Parse(root.GetString() ?? "{}");
                    root = inner.RootElement;
                }
                var type = root.GetProperty("type").GetString();
                switch (type)
                {
                    case "ready":
                        Post(new { type = "config", autoSpeak = _cfg.AutoSpeak });
                        break;
                    case "speak":
                        var spoken = root.GetProperty("text").GetString() ?? "";
                        if (!_tts.Speak(spoken))
                            Post(new { type = "error", message = "本机没有可用的语音引擎，无法发音。" });
                        break;
                    case "ask":
                        await HandleAskAsync(root.GetProperty("text").GetString() ?? "");
                        break;
                    case "close":
                        CloseAnimated();
                        break;
                    case "openSettings":
                        OpenSettingsRequested?.Invoke();   // 统一走主程序入口；直接 new 会缺 RestartRequested 订阅，保存后不会重启
                        break;
                    case "requestHistory":
                        SendHistory(root.TryGetProperty("keyword", out var kwEl) ? kwEl.GetString() : null);
                        break;
                    case "deleteHistory":
                        var delWord = root.GetProperty("word").GetString() ?? "";
                        _history.Remove(delWord);
                        SendHistory(root.TryGetProperty("keyword", out var kwEl2) ? kwEl2.GetString() : null);
                        break;
                    case "openHistoryItem":
                        var word = root.GetProperty("word").GetString() ?? "";
                        LoadHistoryItem(word);
                        break;
                    case "jsError":
                        var jsErr = root.GetProperty("message").GetString() ?? "";
                        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "webmsg.log"),
                            $"[JS-ERROR] {jsErr}\n"); } catch { }
                        break;
                }
            }
            finally
            {
                inner?.Dispose();
            }
        }
        catch
        {
            // 忽略非法消息
        }
    }

    private void SendHistory(string? keyword = null)
    {
        var kw = keyword?.Trim() ?? "";
        var items = _history.GetRecent()
            .Where(i => kw.Length == 0 || i.Word.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .Select(i => new
            {
                word = i.Word,
                time = i.Time.ToString("MM-dd HH:mm"),
                count = i.Messages.Count(m => m.Role == "user")
            });
        Post(new { type = "historyList", items });
    }

    private void LoadHistoryItem(string word)
    {
        var item = _history.FindByWord(word);
        if (item == null) return;
        _currentWord = item.Word;
        // 过滤旧历史中残留的 system 消息（system 改为调用时注入，不入会话）
        _messages = item.Messages.Where(m => m.Role != "system")
            .Select(m => (m.Role, m.Content)).ToList();
        _pendingTitle = false;
        Post(new { type = "loadSession", word = item.Word,
            messages = item.Messages.Where(m => m.Role != "system") });
    }

    // ---------- 会话与流式 ----------

    public void OpenForNewWord(string text, Point nearPt)
    {
        // 未划词直接点球 = 手动打开，不查历史也不自动翻译
        if (string.IsNullOrWhiteSpace(text))
        {
            OpenManual(nearPt);
            return;
        }
        // 同词复用已有会话（对话式 AI 独立会话管理）
        var existing = _history.FindByWord(text);
        _currentWord = text;
        ShowNear(nearPt);
        _pendingTitle = false;
        if (existing != null && existing.Messages.Count(m => m.Role != "system") > 0)
        {
            _messages = existing.Messages.Where(m => m.Role != "system")
                .Select(m => (m.Role, m.Content)).ToList();
            Post(new { type = "loadSession", word = text,
                messages = existing.Messages.Where(m => m.Role != "system") });
            if (_cfg.AutoSpeak) _tts.Speak(text);
            return;
        }
        // system 提示词不再入列：RunChatAsync 调用时按入口注入
        _messages = new List<(string, string)> { ("user", text) };
        _pendingTitle = NeedsAutoTitle(text);   // 首词过长（中文>12字/外文>30字符）：首轮输出后重写标题
        Post(new { type = "newWord", word = text });
        if (_cfg.AutoSpeak) _tts.Speak(text);
        _ = RunChatAsync(_cfg.TranslationModel);
    }

    /// <summary>手动打开（主球双击/托盘）：干净空面板，不残留上一次的选中词。</summary>
    public void OpenManual(Point nearPt)
    {
        _cts?.Cancel();               // 停掉可能还在流式输出的旧会话
        _locked = false;
        _screenshotFlow = false;
        _pendingTitle = false;
        _currentWord = "";
        _messages = new List<(string, string)>();
        ShowNear(nearPt);
        Post(new { type = "newWord", word = "" });
        Post(new { type = "unlock" });   // 兜底：清除可能残留的前端锁定态
    }

    /// <summary>
    /// 截图流程第一步：截图成功后立即强制打开面板并锁定，展示"正在识别屏幕文字"占位，
    /// 由 Program 在后台跑 OCR，完成后调 CompleteScreenshot。
    /// </summary>
    public void OpenScreenshotPending(Point nearPt)
    {
        _cts?.Cancel();
        _screenshotFlow = false;
        _pendingTitle = false;
        _currentWord = "";
        _messages = new List<(string, string)>();
        _locked = true;
        ShowNear(nearPt);
        Post(new { type = "newWord", word = "" });
        Post(new { type = "screenshotPending" });
        Post(new { type = "state", state = "thinking" });
    }

    /// <summary>
    /// 截图流程第二步：OCR 完成。结果作为会话首条消息，由 ChatModel + 截图首轮提示词（6.1）整理翻译，
    /// 首轮输出完成后解锁面板并生成会话标题。
    /// ocrText 为空或 error 非空时，以 error 消息类型提示并立即解锁（复用前端现有样式）。
    /// 不做 AutoSpeak：截图内容可能是长段落，不适合自动朗读。
    /// </summary>
    public void CompleteScreenshot(string ocrText, string? error)
    {
        // OCR 失败 / 未识别到文字：提示后解锁，否则面板会永远关不掉
        if (error != null || string.IsNullOrWhiteSpace(ocrText))
        {
            Post(new { type = "error", message =
                error ?? "未识别到文字，请重新截图或改用划词翻译。" });
            Post(new { type = "streamEnd" });
            Post(new { type = "state", state = "idle" });
            UnlockScreenshot();
            return;
        }
        string content = ocrText.Trim();
        _currentWord = content.Length > 60 ? content[..60] : content;   // 会话词：先截断，标题生成后替换
        _messages = new List<(string, string)> { ("user", content) };
        _screenshotFlow = true;
        _pendingTitle = false;
        Post(new { type = "newWord", word = _currentWord });
        Post(new { type = "userMsg", text = content });
        _ = RunChatAsync(_cfg.ChatModel, AiService.ScreenshotFirstPrompt);
    }

    /// <summary>解除锁定：恢复前端交互（设置以外的按钮在识别期间被禁用），并恢复主球。</summary>
    private void UnlockScreenshot()
    {
        _locked = false;
        Post(new { type = "unlock" });
        Unlocked?.Invoke();
    }

    /// <summary>
    /// 截图翻译收尾：用标题生成模型为首轮会话起名，替换掉默认的 OCR 截断词，
    /// 历史里能按标题直接找到这条记录。失败时保留原截断词，不影响会话。
    /// </summary>
    private async Task RunTitleGenerationAsync()
    {
        try
        {
            if (_messages.Count < 2) return;
            var oldWord = _currentWord;
            var title = (await _ai.GenerateTitleAsync(_messages[0].content, _messages[1].content))
                .Trim().Trim('"', '“', '”', '『', '』');
            if (title.Length == 0 || title == "新对话") return;
            if (title.Length > 40) title = title[..40];
            _currentWord = title;
            _history.Remove(oldWord);      // 历史以 word 为主键：删掉 OCR 截断词条目，改用标题保存
            SaveCurrentSession();
            Post(new { type = "title", text = title });
        }
        catch
        {
            // 标题生成失败静默兜底：会话继续用 OCR 截断词作为标题
        }
    }

    /// <summary>立即收起（无动画、先存会话）：进入截图前调用，避免面板入镜。</summary>
    public void HideImmediate()
    {
        _cts?.Cancel();
        _locked = false;           // 面板被强制收起：旧流程的锁定随之作废
        if (Visible) FinishSession();
        _shadow?.Hide();
        _animTimer.Stop();
        Hide();
        Opacity = 1f;
        Size = _finalBounds.Size;
        Location = _finalBounds.Location;
    }

    /// <summary>面板出现在悬浮球正下方，屏幕边界内 clamp。</summary>
    private void ShowNear(Point ballCenter)
    {
        var wa = Screen.FromPoint(ballCenter).WorkingArea;
        int x = ballCenter.X - Width / 2;
        int y = ballCenter.Y + 40;
        x = Math.Clamp(x, wa.Left + 8, wa.Right - Width - 8);
        y = Math.Clamp(y, wa.Top + 8, wa.Bottom - Height - 8);
        Location = new Point(x, y);
        _finalBounds = new Rectangle(x, y, Width, Height);

        // 弹出动画：从 93% 缩放 + 半透明，弹性放大到正常
        _animClosing = false;
        _animStep = 0;
        _animTicks = 9;
        ApplyAnimScale(0.93f);
        Opacity = 0.15;
        Show();
        Activate();
        _animTimer.Start();
    }

    private async Task HandleAskAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        if (string.IsNullOrEmpty(_currentWord)) _currentWord = question; // 手动打开时以首个提问作为会话词
        _messages.Add(("user", question));
        Post(new { type = "userMsg", text = question });
        // 系统人设：用户自定义优先，留空回退内置讲解者提示词
        var sp = _cfg.SystemPrompt;
        _ = RunChatAsync(_cfg.ChatModel, string.IsNullOrWhiteSpace(sp) ? AiService.ExplainerPrompt : sp);
        await Task.CompletedTask;
    }

    private async Task RunChatAsync(string? model = null, string? systemPrompt = null)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _assistantBuf.Clear();
        Post(new { type = "state", state = "thinking" });
        Post(new { type = "streamStart" });
        try
        {
            // system 提示词不入会话列表，调用时按入口前置（首轮翻译无 system / 截图首轮 6.1 / 追问统一讲解者 6.2）
            // 上下文按设置轮数截断：一组问答算一轮（user+assistant 两条），只携带最近 N 轮
            var hist = _cfg.ContextTurns > 0
                ? _messages.TakeLast(_cfg.ContextTurns * 2).ToList()
                : _messages;
            var msgs = systemPrompt == null
                ? hist
                : new List<(string role, string content)> { ("system", systemPrompt) }.Concat(hist).ToList();
            await _ai.StreamChatAsync(msgs,
                delta =>
                {
                    _assistantBuf.Append(delta);
                    var snapshot = _assistantBuf.ToString();
                    if (InvokeRequired)
                        BeginInvoke(() => Post(new { type = "chunk", delta = snapshot }));
                    else
                        Post(new { type = "chunk", delta = snapshot });
                }, model, ct);
            _messages.Add(("assistant", _assistantBuf.ToString()));
            Post(new { type = "streamEnd" });
            Post(new { type = "state", state = "idle" });
            // 对话回复自动朗读（截图翻译流程除外，避免长文朗读）
            if (_cfg.AutoSpeakReply && !_screenshotFlow)
                _tts.Speak(_assistantBuf.ToString());
            SaveCurrentSession();
            EndScreenshotRound();
            if (_pendingTitle) { _pendingTitle = false; _ = RunTitleGenerationAsync(); }
        }
        catch (OperationCanceledException)
        {
            _screenshotFlow = false;   // 被新流程/收起取消：截图流程终止，交给新入口接管
            _pendingTitle = false;
        }
        catch (Exception ex)
        {
            Post(new { type = "error", message = ex.Message });
            Post(new { type = "streamEnd" });
            Post(new { type = "state", state = "idle" });
            // 出错也要保存：用户提问不能丢，下次打开同词可继续
            SaveCurrentSession();
            EndScreenshotRound();
            _pendingTitle = false;   // 失败轮不生成标题，只消费标志避免追问时误触发
        }
    }

    /// <summary>划词首词过长判定：含中文按中文字数（>12 字），纯外文按字符数（>30）。</summary>
    private static bool NeedsAutoTitle(string word)
    {
        bool hasCjk = word.Any(c => char.IsBetween(c, '\u4e00', '\u9fff'));
        return word.Length > (hasCjk ? 12 : 30);
    }

    /// <summary>截图首轮输出结束（成功或失败）：解锁面板并启动标题生成。</summary>
    private void EndScreenshotRound()
    {
        if (!_screenshotFlow) return;
        _screenshotFlow = false;
        UnlockScreenshot();
        _ = RunTitleGenerationAsync();
    }

    private void SaveCurrentSession()
    {
        if (_messages.Count > 1 && !string.IsNullOrEmpty(_currentWord))
            _history.SaveSession(_currentWord, _messages);
    }

    /// <summary>失焦自动收起：面板失去激活后短暂延迟，若焦点未回到面板则自动隐藏（会话已保存，不怕关）。</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _ = AutoHideAsync();
    }

    private async Task AutoHideAsync()
    {
        await Task.Delay(350);
        if (InvokeRequired) { BeginInvoke(() => _ = AutoHideAsync()); return; }
        if (!Visible || ActiveForm == this) return;
        if (IsForegroundWithinOurTree()) return;   // WebView2 右键菜单等自家体系弹窗打开时不收起
        // 设置窗口等自家对话框打开时不收起
        if (Application.OpenForms.Cast<Form>().Any(f => f != this && f.GetType() == typeof(SettingsWindowForm))) return;
        CloseAnimated();
    }

    // 前台窗口若属于本进程（或归属链挂在我们窗体上），说明焦点仍在自家体系内
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private const uint GA_ROOTOWNER = 3;

    private static bool IsForegroundWithinOurTree()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return true;
        var root = GetAncestor(fg, GA_ROOTOWNER);   // WebView2 菜单可能由浏览器进程创建，但归属我们的窗口
        if (root == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out uint rootPid);
        return rootPid == (uint)Environment.ProcessId;
    }

    private void FinishSession()
    {
        SaveCurrentSession();
        _tts.Stop();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseAnimated();
        }
        base.OnFormClosing(e);
    }
}
