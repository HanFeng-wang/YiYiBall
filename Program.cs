namespace ScreenTranslator;

internal static class Program
{
    /// <summary>单实例锁：同时只允许运行一个依依灵球（RestartApp 重启前会主动释放）。</summary>
    private static Mutex? _instanceMutex;

    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);
        Application.ThreadException += (_, e) => WriteCrashLog(e.Exception);
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    static void WriteCrashLog(object? ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    static void Run(string[] args)
    {
        // 远程调试端口：浏览器访问 http://127.0.0.1:9223/json 可查看 WebView2 页面（排查 UI 问题用）
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--remote-debugging-port=9223");

        // Playwright 随包 Chromium：安装包内置 {app}\browsers\chromium-*（免首次下载），存在才生效
        var bundledBrowsers = Path.Combine(AppContext.BaseDirectory, "browsers");
        if (Directory.Exists(bundledBrowsers))
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", bundledBrowsers);

        ApplicationConfiguration.Initialize();

        // 预览模式：--first-run 直接弹出首启引导窗查看真机效果（不写配置，关窗即退出）
        if (args.Contains("--first-run"))
        {
            var previewCfg = AppConfig.Load();
            using var preview = new ApiKeySetupForm(previewCfg.BaseUrl);
            preview.ShowDialog();
            return;
        }

        var cfg = AppConfig.Load();

        // 单实例：已有一个依依灵球在运行时，新启动的直接提示并退出
        // （--first-run 预览模式在上面已 return，不受此限制）
        _instanceMutex = new Mutex(true, @"Local\YiYiBall-SingleInstance-8E5C4F2A", out var createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            MessageBox.Show("依依灵球已在运行中（屏幕悬浮球与托盘图标仍可用）。",
                "依依灵球 YiYi Ball", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 首次使用引导：未填 API Key 时弹出引导窗（服务地址已预填内置直连地址，只需确认 + 填 Key）
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            using var setup = new ApiKeySetupForm(cfg.BaseUrl);
            if (setup.ShowDialog() != DialogResult.OK) return;   // 用户选择退出
            cfg.ApiKey = setup.ApiKey;
            if (!string.IsNullOrWhiteSpace(setup.BaseUrl)) cfg.BaseUrl = setup.BaseUrl;
            cfg.Save();
        }

        cfg.ApplyAutoStart();   // 开机自启：启动即按当前配置同步注册表（不等设置页保存）
        var ai = new AiService(cfg);
        var history = new HistoryStore(cfg.HistoryLimit);
        history.Load();
        var tts = new TtsService(cfg);

        // 记忆与知识库（方案 §10）：SQLite memory.db；启动即触发一次知识库增量扫描（后台）
        MemoryService? memory = null;
        try { memory = new MemoryService(cfg, ai); }
        catch (Exception ex) { VoiceRecorder.Log("记忆库初始化失败：" + ex.Message); }
        if (memory != null)
        {
            _ = Task.Run(async () =>
            {
                try { await memory.ScanKbAsync(); }
                catch { /* 知识库扫描失败不阻塞启动 */ }
            });
        }

        // 不可见消息窗体：维持消息循环，并用于跨线程调度
        using var ui = new MessageLoopForm();
        _ = ui.Handle; // 强制创建窗口句柄，否则 BeginInvoke 会抛异常（discard，避免占用 _ 标识符）

        using var hook = new MouseHook();
        var selection = new SelectionService(hook, cfg.CaptureSelectedText);
        MainBallForm? ball = null;
        AssistBallForm? assist = null;
        MainWindowForm? main = null;
        string pendingText = "";

        // 自定义全局快捷键动作表（提前创建：CreateMain → ShowSettings → ApplyHotkeys 闭包引用，需在调用点前明确赋值）
        var hotkeyActions = new Dictionary<int, Action>();

        // 托盘（提前创建：CreateMain → ShowSettings → RestartApp 闭包引用 tray）
        // 图标用程序自带图标（悬浮球），读取失败退回系统盾牌
        System.Drawing.Icon trayIcon;
        try { trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Shield; }
        catch { trayIcon = SystemIcons.Shield; }
        var tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "依依灵球 YiYi Ball",
            Visible = true
        };

        MainWindowForm CreateMain()
        {
            var f = new MainWindowForm(cfg, ai, history, tts, memory);
            f.Unlocked += () => ball?.Show();   // 截图锁定解除后恢复主球
            f.OpenSettingsRequested = ShowSettings;   // 对话面板打开设置：复用托盘同一入口（含保存重启订阅 + 已开窗口去重）
            return f;
        }

        // 主球双击 → 干净打开对话面板（不残留上次选中词）
        void OnMainDoubleClicked(Point pt)
        {
            main ??= CreateMain();
            main.OpenManual(pt);
        }

        // 主球单击（约 220ms 无第二击）→ 进入截图翻译
        void OnMainSingleClicked(Point pt) => ui.BeginInvoke(BeginScreenshot);

        PromptSounds.Preload();   // 预加载语音模式提示音（首次播放零延迟）

        // ---------- 语音对话：长按主球 ≥500ms 进入（方案 §2.1/§2.3） ----------
        VoiceRecorder? recorder = null;     // 麦克风采集跨会话复用（唤醒将来共用同一条流）
        WakeWordService? wake = null;
        VoiceSession? voice = null;
        bool voiceActive = false;
        var caption = new VoicePillWebViewForm();
        using var keyObserver = new GlobalKeyObserver();

        // 全局 Esc：语音会话期间直接短路退出
        keyObserver.EscapePressed += () => ui.BeginInvoke(() =>
        {
            if (voiceActive) voice?.RequestExit();
        });

        void StartVoiceSession()
        {
            if (!cfg.VoiceEnabled || voiceActive) return;
            voiceActive = true;
            recorder ??= new VoiceRecorder(cfg);

            // screenshot_translate 工具：退出语音会话后进入截图翻译
            // ai 注入：insert_text/edit_text 的生成与改写走同一 AiService
            // memory 注入：save_memory / save_skill / run_skill（方案 §10）
            var tools = new AppToolbox(() =>
            {
                ui.BeginInvoke(() => { voice?.RequestExit(); BeginScreenshot(); });
                return Task.CompletedTask;
            }, ai, memory);

            var s = new VoiceSession(cfg, ai, tts, recorder, tools, ui, memory);
            voice = s;
            s.StateChanged += st => { ball?.SetVoiceState(st); caption.SetState(st); };
            s.TurnAdded += (who, text) => caption.AddTurn(who, text);
            s.Exited += EndVoiceSession;

            ball!.VoiceLevelProvider = () => s.Level;
            ball.EnterVoiceMode();
            PromptSounds.PlayIn();              // 进入提示音（伴随球脉冲）
            caption.ClearTurns();
            caption.AnchorTo(new Point(ball.Left + ball.Width / 2, ball.Top + ball.Height / 2));
            caption.Show();
            recorder.Start();
            s.Start();
            wake?.Stop();                       // 会话中暂停唤醒监听（互斥，防误触发）
        }

        void EndVoiceSession()
        {
            if (!voiceActive) return;
            voiceActive = false;
            PromptSounds.PlayOut();             // 告别音（再见/静默退出/Esc 统一在此触发）
            voice?.RequestExit();               // 幂等（会话内部已退出时为空操作）
            ball!.VoiceLevelProvider = null;
            ball?.ExitVoiceMode();
            caption.Hide();
            if (cfg.WakeWordEnabled)
            {
                // 麦克风保持运行（唤醒共用同一条流），恢复唤醒监听
                (wake ??= new WakeWordService(cfg)).Start();
            }
            else
            {
                recorder?.Stop();
            }
        }

        // ---------- 唤醒待机：程序启动即开麦（唤醒与语音会话共用同一条流） ----------
        bool screenshotTipShown = false;   // 每次启动只提示一次（设置里可永久关闭）
        if (cfg.WakeWordEnabled)
        {
            recorder ??= new VoiceRecorder(cfg);
            recorder.FrameReady += (rms, pcm) => wake?.Feed(rms, pcm);
            wake ??= new WakeWordService(cfg);
            wake.OnWakeDetected += () => ui.BeginInvoke(() => { if (!voiceActive) StartVoiceSession(); });
            recorder.Start();
            wake.Start();
        }

        // ---------- 截图翻译：主球单击 → 隐藏自家 UI → 框选 → OCR → 翻译面板 ----------

        void BeginScreenshot()
        {
            // 自家 UI 全部隐藏，避免入镜
            ball?.HideFully();
            assist?.HideFully();
            main?.HideImmediate();

            bool captured = false;
            var form = new ScreenCaptureForm(cfg.ShowScreenshotTip && !screenshotTipShown);
            screenshotTipShown = true;

            form.TipDisabled += () =>
            {
                cfg.ShowScreenshotTip = false;
                cfg.Save();
            };
            form.Captured += (bmp, selRect) =>
            {
                captured = true;                // 截图成功：主球等解锁后由 Unlocked 恢复，避免入镜
                form.Close();
                _ = ScreenshotFlowAsync(bmp, selRect);   // 异步流程自行处理异常与资源释放
            };
            // 取消（Esc/右键/误触）→ 恢复主球；成功截图 → 保持隐藏（面板立即打开）
            form.FormClosed += (_, _) => { if (!captured) ball?.Show(); };

            form.Show();
        }

        async Task ScreenshotFlowAsync(Bitmap bmp, Rectangle selRect)
        {
            try
            {
                var anchor = new Point(selRect.X + selRect.Width / 2,
                                       selRect.Y + selRect.Height / 2);

                // 先强制打开面板并锁定：用户立刻看到"正在识别"，而不是对着屏幕干等
                main ??= CreateMain();
                main.OpenScreenshotPending(anchor);

                // 过大截图先缩放再转 JPEG：上传体积与 OCR 耗时都近似随像素数增长
                var (bytes, mime) = PrepareForOcr(bmp);
                string ocr = "";
                string? error = null;
                try { ocr = (await ai.OcrAsync(bytes, mime)).Trim(); }
                catch (Exception ex) { error = "截图识别失败：" + ex.Message; }

                main.CompleteScreenshot(ocr, error);
            }
            finally
            {
                bmp.Dispose();
            }
        }

        /// <summary>宽超过 1400px 的截图等比缩小，并编码为 JPEG（GDI+ 默认质量）以大幅压缩上传体积。</summary>
        static (byte[] bytes, string mime) PrepareForOcr(Bitmap bmp)
        {
            const int MaxWidth = 1400;
            var src = bmp;
            bool scaled = false;
            if (bmp.Width > MaxWidth)
            {
                int h = Math.Max(1, bmp.Height * MaxWidth / bmp.Width);
                src = new Bitmap(bmp, new Size(MaxWidth, h));
                scaled = true;
            }
            try
            {
                using var ms = new MemoryStream();
                src.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                return (ms.ToArray(), "image/jpeg");
            }
            finally
            {
                if (scaled) src.Dispose();
            }
        }

        // 辅助球单击 → 用选中的词打开翻译会话
        void OnAssistClicked(Point pt)
        {
            main ??= CreateMain();
            main.OpenForNewWord(pendingText, pt);
        }

        // 划词 → 辅助球出现在选区旁（记录选中文字；主球不动）
        selection.TextSelected += (text, pt) =>
        {
            var screenPt = new Point(pt.X, pt.Y);
            ui.BeginInvoke(() =>
            {
                pendingText = text;
                assist ??= new AssistBallForm();
                assist.Clicked -= OnAssistClicked;
                assist.Clicked += OnAssistClicked;
                assist.ShowNear(screenPt);
            });
        };

        // 原地单击（无选区）= 没有选中文字 → 辅助球立即进入消失阶段，不再等满 15 秒
        selection.ClickNoSelection += () =>
            ui.BeginInvoke(() => assist?.BeginFade());

        // 常驻主球：启动即显示在屏幕右侧中部
        ui.BeginInvoke(() =>
        {
            ball ??= new MainBallForm();
            ball.DoubleClicked -= OnMainDoubleClicked;
            ball.DoubleClicked += OnMainDoubleClicked;
            ball.SingleClicked -= OnMainSingleClicked;
            ball.SingleClicked += OnMainSingleClicked;
            ball.LongPressed -= () => ui.BeginInvoke(StartVoiceSession);   // 长按 ≥500ms → 语音对话
            ball.LongPressed += () => ui.BeginInvoke(StartVoiceSession);
            ball.VoiceAnchorChanged -= caption.AnchorTo;   // 语音中球心变化（滑出/拖动）→ 白条实时居中跟随
            ball.VoiceAnchorChanged += caption.AnchorTo;
            ball.ShowResident();
        });

        // 语音唤醒（占位：sherpa-onnx 模型就绪后接入，方案 §3.2）
        if (cfg.WakeWordEnabled)
            ui.BeginInvoke(() => (wake ??= new WakeWordService(cfg)).Start());

        // 托盘（tray 已提前创建于 CreateMain 之前，此处仅建菜单）
        var menu = new ContextMenuStrip();

        // ---------- 自定义全局快捷键（设置 > 快捷键；默认留空不启用） ----------
        void ToggleVoice()
        {
            if (voiceActive) EndVoiceSession();
            else StartVoiceSession();
        }
        void BindHotkey(int id, string actionId, Action run)
        {
            if (!cfg.Hotkeys.TryGetValue(actionId, out var combo) || string.IsNullOrWhiteSpace(combo)) return;
            if (!HotkeyMap.TryParse(combo, out var mods, out var vk)) return;
            if (ui.TryRegisterHotkey(id, mods, vk)) hotkeyActions[id] = run;
        }
        void ApplyHotkeys()
        {
            ui.UnregisterAllHotkeys();
            hotkeyActions.Clear();
            BindHotkey(1, "screenshot", () => ui.BeginInvoke(BeginScreenshot));
            BindHotkey(2, "voice", () => ui.BeginInvoke(ToggleVoice));
            BindHotkey(3, "chat", () => ui.BeginInvoke(() =>
            {
                main ??= CreateMain();
                main.OpenManual(Cursor.Position);
            }));
        }
        void OnHotkeyPressed(int id)
        {
            if (hotkeyActions.TryGetValue(id, out var run)) run();
        }
        ui.HotkeyPressed += OnHotkeyPressed;
        ApplyHotkeys();

        // 托盘点击 → 直接打开设置（已打开则激活）
        void ShowSettings()
        {
            var existing = Application.OpenForms.OfType<SettingsWindowForm>().FirstOrDefault();
            if (existing != null) { existing.Activate(); return; }
            var settings = new SettingsWindowForm(cfg, memory);
            settings.RestartRequested += () => ui.BeginInvoke(RestartApp);   // 保存并重启
            settings.HotkeysChanged += ApplyHotkeys;   // 快捷键保存后立即重新注册生效
            settings.Show();
        }

        // 保存设置后原地重启：先启动新实例再退出（配置文件已落盘，新进程直接读到最新值）
        void RestartApp()
        {
            // 先释放单实例锁再拉起新进程，否则新实例会被自己挡住
            try { _instanceMutex?.Dispose(); } catch { }
            _instanceMutex = null;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    UseShellExecute = true
                });
            }
            catch { }
            tray.Visible = false;   // 干净移除托盘图标，避免残留
            Application.Exit();
        }

        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("打开对话窗口", null, (_, _) =>
        {
            main ??= CreateMain();
            main.OpenManual(Cursor.Position);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            tray.Visible = false;
            Application.Exit();
        });
        tray.ContextMenuStrip = menu;
        // 只响应左键打开设置；右键交给托盘菜单（否则 Click 左右键都触发，右键菜单弹出时设置窗口同时被拉起，点退出会卡一下）
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        tray.DoubleClick += (_, _) => ShowSettings();

        Application.Run(ui);

        GC.KeepAlive(selection);
    }

    /// <summary>
    /// 不可见消息窗体，维持 WinForms 消息循环（钩子回调依赖它）；
    /// 同时承载全局快捷键：RegisterHotKey 注册、WM_HOTKEY 分发。
    /// </summary>
    private sealed class MessageLoopForm : Form
    {
        /// <summary>全局快捷键触发（参数 = 注册时用的 id）。</summary>
        public event Action<int>? HotkeyPressed;

        public MessageLoopForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
        }

        private const int WmHotkey = 0x0312;
        private readonly List<int> _registered = new();

        /// <summary>注册一个全局快捷键；组合被其他程序占用等情况下返回 false。</summary>
        public bool TryRegisterHotkey(int id, uint mods, uint vk)
        {
            if (!IsHandleCreated) _ = Handle;
            if (!HotkeyNative.RegisterHotKey(Handle, id, mods, vk)) return false;
            _registered.Add(id);
            return true;
        }

        /// <summary>注销当前所有已注册的全局快捷键（设置变更后重新注册前调用）。</summary>
        public void UnregisterAllHotkeys()
        {
            foreach (var id in _registered)
                HotkeyNative.UnregisterHotKey(Handle, id);
            _registered.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>全局快捷键 RegisterHotKey P/Invoke（user32）。</summary>
    internal static class HotkeyNative
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(System.IntPtr hWnd, int id, uint mods, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(System.IntPtr hWnd, int id);
    }
}
