using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ScreenTranslator;

/// <summary>
/// 语音指令可调用的本地工具：
/// open_app（App Paths + 开始菜单统一候选评分 + 别名表 + 进程校验）、
/// screenshot_translate（回调主程序的截图翻译流程）、
/// insert_text / edit_text（光标处写入/改写文字：剪贴板 + Ctrl+V 主链路，行号锚点走本地文件 IO）、
/// save_memory / save_skill / run_skill（记忆与技能库，方案 §10）、
/// find_file / open_found（文件搜索，§11.2）、browser_control / screen_click（§11.1/11.3）。
/// </summary>
public sealed class AppToolbox
{
    private readonly Func<Task>? _onScreenshotTranslate;
    private readonly AiService? _ai;
    private readonly MemoryService? _mem;
    private BrowserService? _browserSvc;   // 受控浏览器（首次用到才建，Chromium 按需下载）

    public AppToolbox(Func<Task>? onScreenshotTranslate = null, AiService? ai = null, MemoryService? memory = null)
    {
        _onScreenshotTranslate = onScreenshotTranslate;
        _ai = ai;
        _mem = memory;
    }

    // 技能沉淀：记录当前任务实际执行过的工具调用（save_skill 存的就是它）
    private readonly List<(string name, string args)> _recentActions = new();
    private bool _recording = true;   // 技能回放期间关闭记录（回放动作不污染「最近操作」）

    /// <summary>每条用户指令进入执行阶段前调用：清空上一条的操作记录。</summary>
    public void BeginTask() { _recentActions.Clear(); }

    /// <summary>执行一个工具调用。返回 (成功, 面向用户的简短说明, 静默)。</summary>
    public async Task<(bool ok, string detail, bool silent)> ExecuteAsync(string name, string argsJson, CancellationToken ct = default)
    {
        // 任务型工具记入「最近操作」（save_skill 沉淀用；meta 工具与回放不记）
        // open_found 依赖会话内的搜索结果状态，回放不可复现，不记录
        int recIdx = -1;
        if (_recording && name is "open_app" or "insert_text" or "edit_text" or "run_command" or
            "web_search" or "open_url" or "window_control" or "media_control" or "read_clipboard" or
            "find_file" or "browser_control" or "screen_click" or "press_keys" or "mouse_action" or
            "system_control" or "rename_selected")
        {
            _recentActions.Add((name, argsJson));
            recIdx = _recentActions.Count - 1;
        }
        var r = await ExecuteCoreAsync(name, argsJson, ct);
        if (recIdx >= 0 && !r.ok) _recentActions.RemoveAt(recIdx);   // 失败的步骤不沉淀进技能
        return r;
    }

    private async Task<(bool ok, string detail, bool silent)> ExecuteCoreAsync(string name, string argsJson, CancellationToken ct = default)
    {
        try
        {
            switch (name)
            {
                case "open_app":
                    using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson))
                    {
                        var appName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (appName.Length == 0) return (false, "没有说明要打开哪个应用", false);
                        var r = await OpenAppAsync(appName);
                        return (r.Item1, r.Item2, false);
                    }
                case "screenshot_translate":
                    if (_onScreenshotTranslate == null) return (false, "截图翻译功能不可用", false);
                    await _onScreenshotTranslate();
                    return (true, "已进入截图翻译", false);
                case "insert_text":
                    {
                        var r = await InsertTextAsync(argsJson, ct);
                        return (r.Item1, r.Item2, false);
                    }
                case "edit_text":
                    {
                        var r = await EditTextAsync(argsJson, ct);
                        return (r.Item1, r.Item2, false);
                    }
                case "run_command":
                    return RunCommandAsync(argsJson);
                case "web_search":
                    return WebSearch(argsJson);
                case "open_url":
                    return OpenUrl(argsJson);
                case "window_control":
                    return WindowControl(argsJson);
                case "media_control":
                    return MediaControl(argsJson);
                case "press_keys":
                    return await PressKeysAsync(argsJson, ct);
                case "mouse_action":
                    return await MouseActionAsync(argsJson, ct);
                case "read_clipboard":
                    return await ReadClipboardAsync(argsJson, ct);
                case "save_memory":
                    return await SaveMemoryAsync(argsJson, ct);
                case "save_skill":
                    return await SaveSkillAsync(argsJson, ct);
                case "run_skill":
                    return await RunSkillAsync(argsJson, ct);
                case "find_file":
                    return FindFile(argsJson);
                case "open_found":
                    return OpenFound(argsJson);
                case "browser_control":
                    {
                        if (_ai == null) return (false, "浏览器自动化功能不可用", false);
                        _browserSvc ??= new BrowserService();
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                        string url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string inst = doc.RootElement.TryGetProperty("instruction", out var ins) ? ins.GetString() ?? "" : "";
                        if (inst.Length == 0) return (false, "没有说要在浏览器里做什么", false);
                        string result = await _browserSvc.RunTaskAsync(url, inst, _ai, ct);
                        return (true, result, true);
                    }
                case "screen_click":
                    return await ScreenClickAsync(argsJson, ct);
                case "system_control":
                    return await SystemControlAsync(argsJson);
                case "rename_selected":
                    return await RenameSelectedAsync(argsJson, ct);
                case "look_screen":
                    return await LookScreenAsync(argsJson, ct);
                default:
                    return (false, $"未知的操作：{name}", false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message, false);
        }
    }

    // ---------------- 文件搜索与屏幕点击（§11.2 find_file / §11.3 screen_click） ----------------

    // 最近一次 find_file 的结果（open_found 用）
    private readonly List<string> _lastFindResults = new();

    /// <summary>
    /// 文件搜索：优先 Everything 的 es.exe（毫秒级全盘），未安装则降级递归搜桌面/文档/下载。
    /// 结果小字列前几条，说「打开第一个」→ open_found 资源管理器定位。
    /// </summary>
    private (bool, string, bool) FindFile(string argsJson)
    {
        string query = GetArg(argsJson, "query").Trim();
        if (query.Length == 0) return (false, "没有说要找什么文件", false);
        // folder：用户说「这个文件夹/这里」时，模型从环境上下文里带来的资源管理器当前路径
        string folder = GetArg(argsJson, "folder").Trim().Trim('"');

        List<string> results = new();
        bool usedEs = false;
        string? esPath = LocateEs();
        if (esPath != null)
        {
            try
            {
                var psi = new ProcessStartInfo(esPath)
                {
                    // Everything 语法：空格隔开为 AND 匹配（含全路径），带路径词即限定在该文件夹下
                    Arguments = "-n 12 " + (folder.Length > 0 ? "\"" + folder + "\" " : "") + query,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode == 0)
                {
                    foreach (var line in output.Split('\n'))
                    {
                        var f = line.Trim().Trim('"');
                        if (f.Length > 3 && File.Exists(f)) results.Add(f);
                    }
                    usedEs = true;
                }
            }
            catch { usedEs = false; }
        }

        if (!usedEs)
        {
            // 降级：常用目录递归搜（文件名包含关键词），顺手提示装 Everything；
            // 用户指定了 folder（资源管理器当前路径）则优先搜那里
            var roots = new List<string>();
            if (folder.Length > 0 && Directory.Exists(folder)) roots.Add(folder);
            roots.AddRange(new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
            });
            foreach (var root in roots)
            {
                if (results.Count >= 12 || !Directory.Exists(root)) continue;
                IEnumerable<string> found;
                try
                {
                    // IgnoreInaccessible：无权限的子目录/文件直接跳过（dotnet/runtime 为此场景
                    // 内置的能力），不会再因 AppData 等受保护目录抛异常导致整个搜索失败
                    var opts = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        MatchCasing = MatchCasing.CaseInsensitive,
                    };
                    found = Directory.EnumerateFiles(root, "*", opts)
                        .Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase));
                    var list = found.ToList();   // 立即物化，异常（若有）留在 try 内
                    found = list;
                }
                catch { continue; }
                foreach (var f in found)
                {
                    results.Add(f);
                    if (results.Count >= 12) break;
                }
            }
        }

        _lastFindResults.Clear();
        _lastFindResults.AddRange(results);
        if (results.Count == 0)
        {
            return (false, usedEs
                ? $"全盘没找到名字带「{query}」的文件"
                : $"桌面、文档、下载里都没找到「{query}」。装个 Everything（免费）我就能秒搜全盘了", false);
        }

        var listSb = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
            listSb.AppendLine($"{i + 1}. {results[i]}");
        return (true, $"找到 {results.Count} 个：\n{listSb}（说「打开第几个」即可定位）", true);
    }

    /// <summary>定位 es.exe：PATH / 常见安装位置 / Everything 安装目录注册表。</summary>
    private static string? LocateEs()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "es.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        try
        {
            var install = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Everything", "InstallDir", null) as string;
            if (install != null)
            {
                var p = Path.Combine(install, "es.exe");
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;   // PATH 上的 es.exe 由 Process 直接解析（找不到就走降级）
    }

    /// <summary>打开最近一次搜索结果里的某条：资源管理器定位（/select），不直接执行文件。</summary>
    private (bool, string, bool) OpenFound(string argsJson)
    {
        if (_lastFindResults.Count == 0)
            return (false, "刚才没有搜索过文件，先跟我说要找什么", false);
        int idx;
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson))
        {
            idx = doc.RootElement.TryGetProperty("index", out var i) && i.TryGetInt32(out int v) ? v : 1;
        }
        if (idx < 1 || idx > _lastFindResults.Count)
            return (false, $"刚才一共找到 {_lastFindResults.Count} 个，说「打开第 1 到 {_lastFindResults.Count} 个」都行", false);
        string path = _lastFindResults[idx - 1];
        try
        {
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
            return (true, "已在资源管理器定位 " + path, true);
        }
        catch (Exception ex) { return (false, "打开失败：" + ex.Message, false); }
    }

    // 鼠标注入常量（screen_click / mouse_action）
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002,
        MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010,
        MOUSEEVENTF_ABSOLUTE = 0x8000;

    /// <summary>SendInput 移动鼠标到屏幕绝对坐标并单击（0~65535 归一化映射虚拟屏）。</summary>
    private static void ClickAt(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        int nx = (x - vs.Left) * 65535 / Math.Max(1, vs.Width - 1);
        int ny = (y - vs.Top) * 65535 / Math.Max(1, vs.Height - 1);
        var mi = new MouseInput { dx = nx, dy = ny, dwFlags = (int)(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE) };
        var move = new INPUT { type = 0u };   // INPUT_MOUSE
        move.u.mi = mi;
        var down = new INPUT { type = 0u };
        down.u.mi = new MouseInput { dwFlags = (int)MOUSEEVENTF_LEFTDOWN };
        var up = new INPUT { type = 0u };
        up.u.mi = new MouseInput { dwFlags = (int)MOUSEEVENTF_LEFTUP };
        SendInput(3, new[] { move, down, up }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
    }

    /// <summary>SendInput 移动鼠标到屏幕绝对坐标，执行指定次数的左键或右键点击（mouse_action 用）。</summary>
    private static void MouseClickAt(int x, int y, bool right, int clicks)
    {
        var vs = SystemInformation.VirtualScreen;
        int nx = (x - vs.Left) * 65535 / Math.Max(1, vs.Width - 1);
        int ny = (y - vs.Top) * 65535 / Math.Max(1, vs.Height - 1);
        var seq = new List<INPUT>
        {
            new() { type = 0u, u = new InputUnion { mi = new MouseInput { dx = nx, dy = ny, dwFlags = (int)(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE) } } }
        };
        uint down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        for (int i = 0; i < clicks; i++)
        {
            seq.Add(new INPUT { type = 0u, u = new InputUnion { mi = new MouseInput { dwFlags = (int)down } } });
            seq.Add(new INPUT { type = 0u, u = new InputUnion { mi = new MouseInput { dwFlags = (int)up } } });
        }
        SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
    }

    /// <summary>在鼠标当前位置注入按下/松开（不移动），连续 clicks 次（mouse hold / 连点用）。</summary>
    private static void MouseButtons(bool right, int clicks)
    {
        uint down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        var seq = new List<INPUT>();
        for (int i = 0; i < clicks; i++)
        {
            seq.Add(new INPUT { type = 0u, u = new InputUnion { mi = new MouseInput { dwFlags = (int)down } } });
            seq.Add(new INPUT { type = 0u, u = new InputUnion { mi = new MouseInput { dwFlags = (int)up } } });
        }
        SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>拟人间隔：基准间隔 × 0.5~1.5 随机浮动（连点/连按节奏像真人，不机械）。</summary>
    private static TimeSpan HumanJitter(double baseMs) =>
        TimeSpan.FromMilliseconds(baseMs * (0.5 + Random.Shared.NextDouble()));

    /// <summary>
    /// 鼠标操作（mouse_action 工具）：单击/双击/右键/按住/连点。
    /// 按住：{"action":"hold","button":"left","duration":5}（秒）；说话即打断，立刻松开；
    ///       没说按多久默认 3 秒。
    /// 连点：{"action":"click","count":10,"interval":150}（次数 + 基准间隔毫秒，间隔随机浮动）；
    ///       只传 duration 不传 count 则持续点击这么长时间。都传以 count 为准。
    /// 默认值：说了「连点/随便点几下」但没说次数 → 5 次；没提连点/时长 → 只点一下；
    ///         间隔没说默认 150ms。
    /// 位置默认当前光标处，position=center 为屏幕正中心。
    /// </summary>
    private async Task<(bool, string, bool)> MouseActionAsync(string argsJson, CancellationToken ct)
    {
        string action = GetArg(argsJson, "action").Trim().ToLowerInvariant();
        string position = GetArg(argsJson, "position").Trim().ToLowerInvariant();
        if (action.Length == 0) return (false, "没有说明要做什么鼠标操作", false);

        string durationRaw = GetArg(argsJson, "duration").Trim();
        double duration = 0;
        double.TryParse(durationRaw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out duration);
        string countRaw = GetArg(argsJson, "count").Trim();
        int.TryParse(countRaw, out int count);
        double.TryParse(GetArg(argsJson, "interval"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double interval);
        if (duration > 600) duration = 600;      // 兜底：最长 10 分钟
        if (count > 1000) count = 1000;
        // 说了「连点/随便点几下」但没说次数 → 默认 5 次；完全没提连点/时长 → 只点一下
        if (countRaw.Length > 0 && count <= 0) count = 5;
        if (interval <= 0) interval = 150;       // 默认基准间隔 150ms（会随机浮动）
        if (interval > 5000) interval = 5000;

        // 按住：按下 → 等待（说话打断 = ct 取消）→ 松开
        if (action is "hold" or "hold_click" or "mouse_hold")
        {
            bool rightHold = GetArg(argsJson, "button").Trim().StartsWith("right", StringComparison.OrdinalIgnoreCase);
            if (duration <= 0) duration = 3;     // 「按住一会儿」没说多久：默认 3 秒
            string btnName = rightHold ? "右键" : "左键";
            SendMouseButton(rightHold, down: true);
            try
            {
                while (duration > 0)
                {
                    double step = Math.Min(80, duration * 1000);
                    await Task.Delay((int)step, ct);
                    duration -= step / 1000;
                }
            }
            catch (OperationCanceledException)
            {
                SendMouseButton(rightHold, down: false);   // 说话打断也要保证松开
                throw;
            }
            SendMouseButton(rightHold, down: false);
            return (true, $"{btnName}已按住并松开", false);
        }

        var (right, clicks, desc) = action switch
        {
            "click" or "single_click" or "left_click" => (false, 1, "单击"),
            "double_click" or "dblclick" => (false, 2, "双击"),
            "right_click" => (true, 1, "右键单击"),
            _ => (false, 0, ""),
        };
        if (clicks == 0) return (false, $"不认识的鼠标操作：{action}", false);

        int x, y;
        string where;
        if (position == "center")
        {
            var b = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
            x = b.Left + b.Width / 2;
            y = b.Top + b.Height / 2;
            where = "屏幕正中心";
        }
        else
        {
            var p = Cursor.Position;
            x = p.X; y = p.Y;
            where = "当前位置";
        }

        // 连点：count 次（或持续 duration 时间），间隔拟人随机浮动，说话可打断
        if (count > 1 || (count <= 0 && duration > 0))
        {
            int done = 0;
            try
            {
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        MouseButtons(right, 1);
                        done++;
                        if (i < count - 1) await Task.Delay(HumanJitter(interval), ct);
                    }
                }
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < duration)
                    {
                        MouseButtons(right, 1);
                        done++;
                        await Task.Delay(HumanJitter(interval), ct);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }   // 说话打断：点出去的都是完整按下-松开对，无需清理
            return (true, $"已在{where}连续{desc} {done} 次（间隔约 {interval:0} 毫秒随机浮动）", false);
        }

        MouseClickAt(x, y, right, clicks);
        return (true, $"已在{where}{desc}", true);
    }

    /// <summary>注入一次鼠标按下或松开事件（按住 hold 用，不动光标）。</summary>
    private static void SendMouseButton(bool right, bool down)
    {
        uint flag = right ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP)
                          : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
        var seq = new INPUT[] { new() { type = 0u, u = new InputUnion { mi = new MouseInput { dwFlags = (int)flag } } } };
        SendInput(1u, seq, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 屏幕单步点击/输入（方案 §11.3，用户确认：只用现有 OCR 模型，不上真视觉模型）：
    /// 全屏截图 → 多模态模型 grounding（0~1000 归一化包围盒）→ SendInput 点击；
    /// 带 value 时点击聚焦输入框后复用 insert_text 粘贴。
    /// 定位不到目标不盲目点击，口播请用户说具体点。
    /// 坐标持久化：位置固定的按钮（如微信登录的「进入微信」），视觉定位成功一次后
    /// 记住屏幕绝对坐标（data\click_points.json），之后直接点击不再走视觉；
    /// 记忆按「目标文字 + 前台应用」隔离——确认/取消/关闭这类通用按钮每个软件位置都
    /// 不一样，跨应用复用会点错；虚拟屏布局变化或换了应用都视为失效重新视觉定位。
    /// </summary>
    private async Task<(bool, string, bool)> ScreenClickAsync(string argsJson, CancellationToken ct)
    {
        if (_ai == null) return (false, "屏幕点击功能不可用", false);
        string target = GetArg(argsJson, "target").Trim();
        string value = GetArg(argsJson, "value").Trim();
        if (target.Length == 0) return (false, "没有说要点屏幕上的什么", false);

        // 点击后的公共尾巴：纯点击，或等焦点后粘贴 value（实际 ClickAt 在调用方完成）
        async Task<(bool, string, bool)> Clicked(bool remembered)
        {
            if (value.Length > 0)
            {
                await Task.Delay(200, ct);   // 等输入框获得焦点
                var (ok, detail) = await InsertTextAsync(
                    JsonSerializer.Serialize(new { text = value }), ct);
                if (!ok) return (ok, $"已点击「{target}」，但输入文字失败：{detail}", false);
                return (true, $"已在「{target}」里输入 {Truncate(value, 40)}", true);
            }
            return (true, $"已点击「{target}」{(remembered ? "（记住的位置）" : "")}", true);
        }

        // ① 记住的按钮：同一个应用里、虚拟屏布局没变就直接点（不截图、不调视觉模型，快且稳）。
        //    key 带「@前台应用」：「进入微信」只在微信里生效；「确认」在 A 软件记住的位置
        //    不会被拿到 B 软件里用。前台应用拿不到就不复用，老老实实走视觉定位
        var vs = SystemInformation.VirtualScreen;
        string screenSig = $"{vs.Left},{vs.Top},{vs.Width},{vs.Height}";
        string fgApp = ForegroundApp();
        if (fgApp.Length > 0 &&
            LoadClickPoints().TryGetValue(NormKey(target) + "@" + fgApp, out var cp) &&
            cp.Screen == screenSig)
        {
            ClickAt(cp.X, cp.Y);
            return await Clicked(remembered: true);
        }

        // ② 视觉定位：grounding（qwen-vl 约定 0~1000 归一化 [[x1,y1,x2,y2]]）
        byte[] png = CaptureVirtualScreen();
        string prompt = "在截图中找到显示为「" + target + "」的按钮、输入框或文字，" +
            "输出它所在区域的包围盒坐标，格式 [[x1,y1,x2,y2]]，四个数都是 0 到 1000 的归一化值。" +
            "只输出坐标；找不到就只输出：未找到";
        string resp = await _ai.GroundAsync(png, prompt, ct);
        var nums = Regex.Matches(resp, @"\d+(?:\.\d+)?")
            .Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).Take(4).ToList();
        if (resp.Contains("未找到") || nums.Count < 4)
            return (false, $"屏幕上没找到「{target}」，你要不描述得更具体点？", false);

        // 归一化坐标 → 屏幕绝对坐标（中心点）
        int cx = vs.Left + (int)((nums[0] + nums[2]) / 2 / 1000.0 * vs.Width);
        int cy = vs.Top + (int)((nums[1] + nums[3]) / 2 / 1000.0 * vs.Height);

        ClickAt(cx, cy);
        SaveClickPoint(target, cx, cy);   // 记住这次的位置（按前台应用隔离），下次同应用直接点
        return await Clicked(remembered: false);
    }

    // ---------------- screen_click 坐标持久化（data\click_points.json） ----------------

    private sealed record ClickPoint(
        [property: System.Text.Json.Serialization.JsonPropertyName("x")] int X,
        [property: System.Text.Json.Serialization.JsonPropertyName("y")] int Y,
        [property: System.Text.Json.Serialization.JsonPropertyName("screen")] string Screen,
        [property: System.Text.Json.Serialization.JsonPropertyName("app")] string App = "");

    private static string ClickPointsPath => Path.Combine(AppContext.BaseDirectory, "data", "click_points.json");
    private static readonly object _clickPointsLock = new();
    private static Dictionary<string, ClickPoint>? _clickPoints;

    /// <summary>key 归一化：小写去空格（「进入微信」「进入 微信」视为同一目标）。</summary>
    private static string NormKey(string target) => target.Trim().ToLowerInvariant().Replace(" ", "");

    /// <summary>
    /// 前台窗口所属进程名（小写）。前台是自己（球抢了焦点）沿 Z 序找下一个可见外部窗口；
    /// 拿不到返回空串。screen_click 坐标记忆的隔离键：同一按钮名在不同软件里位置不同。
    /// </summary>
    private static string ForegroundApp()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            uint pid = WindowPid(hwnd);
            if (pid == (uint)Environment.ProcessId)
            {
                for (hwnd = GetWindow(hwnd, GW_HWNDNEXT); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
                {
                    if (!IsWindowVisible(hwnd)) continue;
                    uint p = WindowPid(hwnd);
                    if (p != 0 && p != (uint)Environment.ProcessId) { pid = p; break; }
                }
            }
            if (pid == 0 || pid == (uint)Environment.ProcessId) return "";
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static Dictionary<string, ClickPoint> LoadClickPoints()
    {
        lock (_clickPointsLock)
        {
            if (_clickPoints != null) return _clickPoints;
            try
            {
                _clickPoints = File.Exists(ClickPointsPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, ClickPoint>>(File.ReadAllText(ClickPointsPath)) ?? new()
                    : new();
            }
            catch { _clickPoints = new(); }
            return _clickPoints;
        }
    }

    private static void SaveClickPoint(string target, int x, int y)
    {
        // 按「目标文字 + 前台应用」隔离保存：同一个「确认/关闭」在不同软件里的位置
        // 差别很大，跨应用复用会点错；前台应用拿不到就不记，宁走视觉也不存盲坐标
        string app = ForegroundApp();
        if (app.Length == 0) return;
        lock (_clickPointsLock)
        {
            try
            {
                var vs = SystemInformation.VirtualScreen;
                LoadClickPoints()[NormKey(target) + "@" + app] =
                    new ClickPoint(x, y, $"{vs.Left},{vs.Top},{vs.Width},{vs.Height}", app);
                Directory.CreateDirectory(Path.GetDirectoryName(ClickPointsPath)!);
                File.WriteAllText(ClickPointsPath,
                    JsonSerializer.Serialize(_clickPoints, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 持久化失败不影响本次点击 */ }
        }
    }

    /// <summary>截取整个虚拟屏为 PNG（缩到 1600 宽控制上传体积），screen_click / look_screen 共用。</summary>
    private static byte[] CaptureVirtualScreen() => Task.Run(() =>
    {
        return InSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            using var full = new Bitmap(vs.Width, vs.Height);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, new Size(vs.Width, vs.Height));
            double scale = Math.Min(1.0, 1600.0 / vs.Width);
            using var bmp = new Bitmap(Math.Max(1, (int)(vs.Width * scale)), Math.Max(1, (int)(vs.Height * scale)));
            using (var g2 = Graphics.FromImage(bmp))
                g2.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        });
    }).GetAwaiter().GetResult();

    /// <summary>
    /// 看屏幕回答（look_screen）：截全屏 → 视觉模型按用户问题作答，结果口播。
    /// 场景：「这个网站是干什么的」「帮我看看屏幕上这是什么」——区别于
    /// screenshot_translate（只做 OCR 整理翻译）。日志实证：此前模型面对这类
    /// 问题只能选 screenshot_translate，把讲解场景带进了翻译流程。
    /// </summary>
    private async Task<(bool, string, bool)> LookScreenAsync(string argsJson, CancellationToken ct)
    {
        if (_ai == null) return (false, "看屏幕功能不可用", false);
        string question = GetArg(argsJson, "question").Trim();
        if (question.Length == 0) return (false, "没有说要了解屏幕上的什么", false);
        byte[] png = CaptureVirtualScreen();
        string prompt = "这是一张电脑屏幕截图。根据截图内容回答下面的问题，用简短中文口语，一到三句话直接说答案，" +
            "不要 Markdown，不要罗列长清单，不要描述截图以外的内容。\n问题：" + question;
        string resp = (await _ai.GroundAsync(png, prompt, ct)).Trim();
        if (resp.Length == 0) return (false, "没看清屏幕上的内容", false);
        return (true, Truncate(resp, 200), false);
    }

    // ---------------- 记忆与技能库（§10：save_memory / save_skill / run_skill） ----------------

    /// <summary>指令式记忆写入（「记住 / 记一下 XXX」）：向量化入库，小字确认不口播。</summary>
    private async Task<(bool, string, bool)> SaveMemoryAsync(string argsJson, CancellationToken ct)
    {
        if (_mem == null) return (false, "记忆功能未启用", false);
        string content = GetArg(argsJson, "content").Trim();
        if (content.Length == 0) return (false, "没有说要记住什么", false);
        await _mem.AddMemoryAsync(content, ct);
        return (true, "已记住：" + Truncate(content, 80), true);
    }

    /// <summary>技能沉淀（「把这个存成技能，叫晨会」）：把最近一次任务实际执行的工具序列存库。</summary>
    private async Task<(bool, string, bool)> SaveSkillAsync(string argsJson, CancellationToken ct)
    {
        if (_mem == null) return (false, "记忆功能未启用", false);
        string name = GetArg(argsJson, "name").Trim();
        if (name.Length == 0) return (false, "没有说技能叫什么名字", false);
        if (_recentActions.Count == 0)
            return (false, "刚才没有执行过什么操作，没什么可存的。你可以先说一遍要做的事，再让我存成技能", false);
        var actions = new List<object>();
        foreach (var (n, a) in _recentActions)
        {
            object args;
            try { using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(a) ? "{}" : a); args = JsonSerializer.Deserialize<JsonElement>(d.RootElement.GetRawText()); }
            catch { args = new { }; }
            actions.Add(new { action = n, args });
        }
        await _mem.SaveSkillAsync(name, JsonSerializer.Serialize(actions), ct);
        return (true, $"已存成技能「{name}」（共 {_recentActions.Count} 步），以后说「执行{name}」就行", true);
    }

    /// <summary>技能回放：按序执行存下的工具序列，Agent 静默；某步失败即停并口播原因。</summary>
    private async Task<(bool, string, bool)> RunSkillAsync(string argsJson, CancellationToken ct)
    {
        if (_mem == null) return (false, "记忆功能未启用", false);
        string name = GetArg(argsJson, "name").Trim();
        if (name.Length == 0) return (false, "没有说要执行哪个技能", false);
        var (actionsJson, allNames) = _mem.GetSkill(name);
        if (actionsJson == null)
            return (false, allNames.Count == 0
                ? "还没有存过任何技能。想存的话，先跟我说要做的事，然后说「把这个存成技能，叫某某」"
                : $"没有叫「{name}」的技能，存过的有：{Truncate(string.Join("、", allNames), 80)}", false);

        List<(string action, string args)>? steps;
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            steps = doc.RootElement.EnumerateArray()
                .Select(e => (
                    e.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                    e.TryGetProperty("args", out var g) ? g.GetRawText() : "{}"))
                .Where(s => s.Item1.Length > 0)
                .ToList();
        }
        catch { return (false, $"技能「{name}」的数据损坏了，麻烦在设置里删掉重存", false); }

        // 回放不记入「最近操作」（避免回放动作被再次沉淀）
        bool prevRecording = _recording;
        _recording = false;
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, detail, _) = await ExecuteAsync(steps[i].action, steps[i].args, ct);
                if (!ok)
                    return (false, $"技能「{name}」执行到第 {i + 1} 步（{steps[i].action}）失败：{Truncate(detail, 100)}", false);
            }
            return (true, $"已执行技能「{name}」（共 {steps.Count} 步）", true);
        }
        finally { _recording = prevRecording; }
    }

    // ---------------- run_command（终端命令执行，§5） ----------------

    // 危险命令词表：命中不静默透传——先口播询问，同一命令 2 分钟内再说一遍视为确认
    private static readonly Regex DangerousCmd = new(
        @"format\s+[a-z]:|del\s+[^&|;]*[/]s|rd\s+[/]s|rmdir\s+[/]s|remove-item\s+[^&|;]*-recurse|rm\s+-[a-z]*r[a-z]*f|diskpart|cipher\s+/w|vssadmin\s+delete",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string? _pendingDangerous;
    private DateTime _pendingDangerousAt;

    /// <summary>唯一保护线：命令涉及程序自身安装目录的删除/格式化类操作，一律拒绝。</summary>
    private static string? GuardSelfDirectory(string command)
    {
        string selfDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        bool touchesSelf = command.IndexOf(selfDir, StringComparison.OrdinalIgnoreCase) >= 0
            || command.IndexOf("YiYiBall", StringComparison.OrdinalIgnoreCase) >= 0
            || command.IndexOf("ScreenTranslator", StringComparison.OrdinalIgnoreCase) >= 0;   // 兼容旧 exe 名
        if (touchesSelf && DangerousCmd.IsMatch(command))
            return "这条命令要删除或改动我自己的程序目录，我不能执行";
        return null;
    }

    /// <summary>
    /// 终端命令执行：powershell 后台跑，工作目录/命令内容不限（用户明确要求）。
    /// Agent 静默：成功只返回小字留痕文本（silent=true），失败才口播 stderr 要点。
    /// </summary>
    private (bool, string, bool) RunCommandAsync(string argsJson)
    {
        string command = GetArg(argsJson, "command").Trim();
        if (command.Length == 0) return (false, "没有说要执行什么命令", false);

        var guard = GuardSelfDirectory(command);
        if (guard != null) return (false, guard, false);

        // 关机/重启直通，不走 PowerShell 包裹：日志实证 PowerShell 会往 stderr 写 CLIXML
        // 进度流，ExitCode 判定误报「命令执行失败」。shutdown.exe 普通用户权限即可调用，
        // 立即返回不等系统真正关机。同时归一化 Stop-Computer / Restart-Computer 写法。
        var sm = Regex.Match(command.Trim(), @"^(?:shutdown(?:\.exe)?|Stop-Computer|Restart-Computer)\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
        if (sm.Success)
        {
            string rest = sm.Groups["rest"].Value;
            string head = command.Trim();
            bool restart = head.StartsWith("Restart-Computer", StringComparison.OrdinalIgnoreCase)
                           || Regex.IsMatch(rest, @"[/-]r\b", RegexOptions.IgnoreCase);
            bool abort = Regex.IsMatch(rest, @"[/-]a\b", RegexOptions.IgnoreCase);
            var tm = Regex.Match(rest, @"[/-]t\s+(\d+)", RegexOptions.IgnoreCase);
            string delay = tm.Success ? tm.Groups[1].Value : "5";
            string swArgs = abort ? "/a" : (restart ? "/r" : "/s") + " /t " + delay;
            Process.Start(new ProcessStartInfo("shutdown", swArgs) { UseShellExecute = false, CreateNoWindow = true });
            string say = abort ? "已取消关机计划" : (restart ? $"已执行重启（{delay} 秒后生效）" : $"已执行关机（{delay} 秒后生效）");
            return (true, say, false);
        }

        // 高危命令：先问再执行。用户下一轮再说同一命令视为确认（澄清链路会带着原指令重规划）
        if (DangerousCmd.IsMatch(command))
        {
            if (_pendingDangerous == command && (DateTime.UtcNow - _pendingDangerousAt).TotalMinutes < 2)
            {
                _pendingDangerous = null;
            }
            else
            {
                _pendingDangerous = command;
                _pendingDangerousAt = DateTime.UtcNow;
                return (false, $"这条命令会删除或格式化数据（{Truncate(command, 60)}），确定要我执行吗？确定的话再跟我说一遍", false);
            }
        }

        // -EncodedCommand 免引号转义；命令前强制 UTF-8 输出（PowerShell 中文输出乱码规避）
        string wrapped = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            bool done = p.WaitForExit(30_000);
            if (!done) { try { p.Kill(true); } catch { } }
            string output = ((so.IsCompleted ? so.Result : "") + (se.IsCompleted ? se.Result : "")).Trim();
            if (!done)
                return (true, $"已执行（30 秒超时已截断）：{Truncate(command, 60)}", true);
            if (p.ExitCode != 0)
                return (false, "命令执行失败：" + Truncate(output, 200), false);
            string note = output.Length > 0 ? " · " + Truncate(output, 200) : "";
            return (true, $"已执行：{Truncate(command, 60)}{note}", true);
        }
        catch (Exception ex)
        {
            return (false, "命令执行失败：" + ex.Message, false);
        }
    }

    // ---------------- §9 四工具（web_search / open_url / window_control / media_control / read_clipboard） ----------------

    /// <summary>网页搜索：默认浏览器打开搜索页，零自动化成本。</summary>
    private static (bool, string, bool) WebSearch(string argsJson)
    {
        string q = GetArg(argsJson, "query").Trim();
        if (q.Length == 0) return (false, "没有说要搜什么", false);
        try
        {
            Process.Start(new ProcessStartInfo("https://www.baidu.com/s?wd=" + Uri.EscapeDataString(q)) { UseShellExecute = true });
            return (true, $"已用默认浏览器搜索「{Truncate(q, 40)}」", true);
        }
        catch (Exception ex) { return (false, ex.Message, false); }
    }

    /// <summary>打开网址：shell 协议交给默认浏览器。</summary>
    private static (bool, string, bool) OpenUrl(string argsJson)
    {
        string url = GetArg(argsJson, "url").Trim();
        if (url.Length == 0) return (false, "没有说要打开哪个网址", false);
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return (true, $"已在默认浏览器打开 {Truncate(url, 60)}", true);
        }
        catch (Exception ex) { return (false, ex.Message, false); }
    }

    // 窗口枚举 / 操作
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint WM_CLOSE = 0x10;
    private const int SW_MINIMIZE = 6, SW_MAXIMIZE = 3, SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;

    private static string GetWindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        return sb.ToString();
    }

    private static bool IsOwnToolWindow(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return true;   // 不动自己的窗口
        return (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;
    }

    private static IntPtr FindWindowByTitle(string keyword)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && !IsOwnToolWindow(h) &&
                GetWindowTitle(h).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>窗口操作：关闭/最小化/最大化/切换（EnumWindows + 原生 API，比模拟 Alt+Tab 稳）。</summary>
    private static (bool, string, bool) WindowControl(string argsJson)
    {
        string action = GetArg(argsJson, "action").Trim().ToLowerInvariant();
        string target = GetArg(argsJson, "target").Trim();

        if (action == "minimize_all")
        {
            int n = 0;
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h) && !IsOwnToolWindow(h) && GetWindowTitle(h).Length > 0)
                {
                    ShowWindowAsync(h, SW_MINIMIZE);
                    n++;
                }
                return true;
            }, IntPtr.Zero);
            return (true, $"已最小化 {n} 个窗口", true);
        }

        if (target.Length == 0) return (false, "没有说要操作哪个窗口", false);
        IntPtr found = FindWindowByTitle(target);
        if (found == IntPtr.Zero) return (false, $"没有找到标题带「{target}」的窗口", false);
        string title = Truncate(GetWindowTitle(found), 40);

        switch (action)
        {
            case "close":
                PostMessage(found, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return (true, $"已关闭窗口「{title}」", true);
            case "minimize":
                ShowWindowAsync(found, SW_MINIMIZE);
                return (true, $"已最小化「{title}」", true);
            case "maximize":
                ShowWindowAsync(found, SW_MAXIMIZE);
                return (true, $"已最大化「{title}」", true);
            case "switch":
            case "activate":
            case "":
                ShowWindowAsync(found, SW_RESTORE);
                SetForegroundWindow(found);
                return (true, $"已切到「{title}」", true);
            default:
                return (false, $"不认识的窗口操作：{action}", false);
        }
    }

    /// <summary>
    /// 媒体控制：模拟系统媒体键（走 SMTC，网易云/QQ 音乐/Groove 等全局响应，无需焦点）。
    /// play/pause 都是同一个切换键（系统媒体键本身无独立播放/暂停）。
    /// </summary>
    private static (bool, string, bool) MediaControl(string argsJson)
    {
        string action = GetArg(argsJson, "action").Trim().ToLowerInvariant();
        switch (action)
        {
            case "play":
            case "pause":
            case "play_pause":
                // 视频场景（前台是浏览器或本地播放器）媒体键经常不生效或作用错对象，
                // 改按空格键切换当前页面的视频；音乐/其他场景仍发系统媒体键（全局生效）。
                // 直接按前台窗口判断，不打断用户问场景。
                if (IsVideoForeground())
                {
                    SendKey(VK_SPACE);
                    return (true, "已切换播放/暂停（视频）", true);
                }
                SendKey(VK_MEDIA_PLAY_PAUSE);
                return (true, "已切换播放/暂停", true);
            case "next":
                SendKey(VK_MEDIA_NEXT_TRACK);
                return (true, "已切到下一首", true);
            case "previous":
            case "prev":
                SendKey(VK_MEDIA_PREV_TRACK);
                return (true, "已切到上一首", true);
            case "stop":
                SendKey(VK_MEDIA_STOP);
                return (true, "已停止播放", true);
            case "volume_up":
                for (int i = 0; i < 5; i++) SendKey(VK_VOLUME_UP);
                return (true, "已调大音量", true);
            case "volume_down":
                for (int i = 0; i < 5; i++) SendKey(VK_VOLUME_DOWN);
                return (true, "已调小音量", true);
            case "mute":
                SendKey(VK_VOLUME_MUTE);
                return (true, "已切换静音", true);
            case "set_volume":
            {
                int v = GetArgInt(argsJson, "value");
                if (v <= 0 || v > 100) return (false, "音量数值要给 0 到 100 之间的具体数字", false);
                return SetVolume(v);
            }
            case "set_brightness":
            {
                int v = GetArgInt(argsJson, "value");
                if (v <= 0 || v > 100) return (false, "亮度数值要给 0 到 100 之间的具体数字", false);
                return SetBrightness(v);
            }
            default:
                return (false, $"不认识的媒体操作：{action}", false);
        }
    }

    /// <summary>把系统主音量设为指定百分比（Core Audio，NAudio 封装，全局生效不受媒体键步进限制）。</summary>
    private static (bool, string, bool) SetVolume(int value)
    {
        try
        {
            var dev = new NAudio.CoreAudioApi.MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            dev.AudioEndpointVolume.MasterVolumeLevelScalar = value / 100f;
            return (true, $"已把音量调到 {value}", true);
        }
        catch (Exception ex)
        {
            return (false, $"调整音量失败：{ex.Message}", false);
        }
    }

    /// <summary>
    /// 把屏幕亮度设为指定百分比（WMI WmiSetBrightness，笔记本/带亮度控制的显示器生效；
    /// 台式机外接显示器通常不支持，返回可口播的失败说明）。借 PowerShell 调 WMI，避免新增依赖。
    /// </summary>
    private static (bool, string, bool) SetBrightness(int value)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                $"-NoProfile -Command \"(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightnessMethods | Select-Object -First 1).WmiSetBrightness(1,{value})\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "未能启动亮度调节", false);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            if (p.ExitCode == 0) return (true, $"已把亮度调到 {value}", true);
            return (false, err.Contains("Not Supported", StringComparison.OrdinalIgnoreCase) || err.Length == 0
                ? "这台设备不支持亮度调节（台式机或外接显示器一般不行）"
                : $"调整亮度失败：{Truncate(err, 120)}", false);
        }
        catch
        {
            return (false, "调整亮度失败", false);
        }
    }

    // ---------------- 系统设置开关（system_control：WiFi / 蓝牙 / 飞行模式 / 省电模式） ----------------

    /// <summary>
    /// 系统设置开关（system_control 工具）：
    /// WiFi/蓝牙走 WinRT Radio API（等效系统快捷开关，免管理员）；
    /// 飞行模式系统未开放直接 API，用「同时关/开 WiFi+蓝牙」近似实现；
    /// 省电模式 = 电源计划切到「节能」（开）并记住原计划，「关」时恢复（没记住则回「平衡」）。
    /// </summary>
    private static async Task<(bool, string, bool)> SystemControlAsync(string argsJson)
    {
        string action = GetArg(argsJson, "action").Trim().ToLowerInvariant();
        switch (action)
        {
            case "wifi_on": return await SetRadioAsync(Windows.Devices.Radios.RadioKind.WiFi, on: true, "WiFi");
            case "wifi_off": return await SetRadioAsync(Windows.Devices.Radios.RadioKind.WiFi, on: false, "WiFi");
            case "bluetooth_on": return await SetRadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth, on: true, "蓝牙");
            case "bluetooth_off": return await SetRadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth, on: false, "蓝牙");
            case "airplane_on":
            {
                var r1 = await SetRadioAsync(Windows.Devices.Radios.RadioKind.WiFi, on: false, "WiFi");
                var r2 = await SetRadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth, on: false, "蓝牙");
                bool any = r1.Item1 || r2.Item1;
                return (any, $"已开飞行模式（{(r1.Item1 ? "WiFi 已关" : "WiFi 没关掉")}，{(r2.Item1 ? "蓝牙已关" : "蓝牙没关掉")}）", any);
            }
            case "airplane_off":
            {
                var r1 = await SetRadioAsync(Windows.Devices.Radios.RadioKind.WiFi, on: true, "WiFi");
                var r2 = await SetRadioAsync(Windows.Devices.Radios.RadioKind.Bluetooth, on: true, "蓝牙");
                bool any = r1.Item1 || r2.Item1;
                return (any, $"已关飞行模式（{(r1.Item1 ? "WiFi 已开" : "WiFi 没打开")}，{(r2.Item1 ? "蓝牙已开" : "蓝牙没打开")}）", any);
            }
            case "saver_on": return SetPowerSaver(on: true);
            case "saver_off": return SetPowerSaver(on: false);
            default:
                return (false, $"不认识的系统设置操作：{action}", false);
        }
    }

    /// <summary>开关一个无线电台（WinRT Radio：GetRadiosAsync → SetStateAsync，免管理员）。</summary>
    private static async Task<(bool, string, bool)> SetRadioAsync(
        Windows.Devices.Radios.RadioKind kind, bool on, string desc)
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == kind);
            if (radio == null) return (false, $"这台电脑上没找到{desc}设备", false);
            var st = await radio.SetStateAsync(
                on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
            if (st == Windows.Devices.Radios.RadioAccessStatus.Allowed)
                return (true, $"{desc}已{(on ? "打开" : "关闭")}", true);
            return (false, $"{desc}开关没成功（系统返回：{st}）", false);
        }
        catch (Exception ex)
        {
            return (false, $"{desc}开关失败：{ex.Message}", false);
        }
    }

    // Windows 内置电源计划 GUID（隐藏的计划也能直接 setactive）：节能 / 平衡
    private const string SchemeSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static string PrevSchemePath => Path.Combine(AppContext.BaseDirectory, "data", "power_prev_scheme.txt");

    /// <summary>
    /// 省电模式：开 = 记住当前电源计划（data\power_prev_scheme.txt）并切「节能」；
    /// 关 = 恢复记住的计划，没记住则回「平衡」。
    /// </summary>
    private static (bool, string, bool) SetPowerSaver(bool on)
    {
        if (on)
        {
            string? prev = GetActivePowerScheme();
            if (prev != null && !prev.Equals(SchemeSaver, StringComparison.OrdinalIgnoreCase))
                TryWriteFile(PrevSchemePath, prev);
            return RunPowerCfg("/setactive " + SchemeSaver)
                ? (true, "已开启省电模式", true)
                : (false, "切换节能电源计划失败了", false);
        }
        string back;
        try { back = File.Exists(PrevSchemePath) ? File.ReadAllText(PrevSchemePath).Trim() : ""; }
        catch { back = ""; }
        if (back.Length == 0) back = SchemeBalanced;
        return RunPowerCfg("/setactive " + back)
            ? (true, "已退出省电模式", true)
            : (false, "恢复电源计划失败了", false);
    }

    /// <summary>当前活动电源计划 GUID（powercfg /getactivescheme 输出里取第一个 GUID，中文输出不影响匹配）。</summary>
    private static string? GetActivePowerScheme()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = Regex.Match(output,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    /// <summary>跑一条 powercfg 命令，退出码 0 即成功。</summary>
    private static bool RunPowerCfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", args)
            { UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(8000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>尽力写一个小文本文件（自动建目录，失败静默）。</summary>
    private static void TryWriteFile(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch { }
    }

    /// <summary>视频类宿主进程名单（浏览器 + 本地播放器 + 国内视频客户端）：判定命中时播放/暂停按空格键。</summary>
    private static readonly HashSet<string> VideoHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // 浏览器（网页视频按空格切换）
        "chrome", "msedge", "firefox", "brave", "opera", "360se", "360chrome",
        "qqbrowser", "sogouexplorer", "quark", "quarkpc", "huaweibrowser",
        "baidunetdisk",
        // 本地播放器（多数也响应空格）
        "potplayer", "potplayermini", "potplayermini64", "vlc", "mpv",
        "wmplayer", "mpc-hc", "mpc-hc64", "filmlab", "cloudmusic",
        // 国内视频客户端
        "bilibili", "douyin", "qqlive", "qyclient", "iqiyi", "youku",
        "doubao", "microsoft.mediaplayer",
    };

    /// <summary>前台窗口（或本应用前台时 Z 序里的下一个外部窗口）是否属于视频类宿主。</summary>
    private static bool IsVideoForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            uint pid = WindowPid(hwnd);
            // 前台是自己（如刚点过球说话、球抢了焦点）：沿 Z 序往下找第一个可见的外部窗口，
            // 否则永远判不到视频场景
            if (pid == (uint)Environment.ProcessId)
            {
                for (hwnd = GetWindow(hwnd, GW_HWNDNEXT); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
                {
                    if (!IsWindowVisible(hwnd)) continue;
                    uint p = WindowPid(hwnd);
                    if (p != 0 && p != (uint)Environment.ProcessId) { pid = p; break; }
                }
            }
            if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
            using var proc = Process.GetProcessById((int)pid);
            return VideoHostProcesses.Contains(proc.ProcessName);
        }
        catch { return false; }
    }

    private static uint WindowPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>
    /// 读剪贴板：无要求 → 直接口播内容；带要求（翻译/润色…）→ LLM 处理后按语义
    /// 贴回光标（说「贴/粘贴」）或口播结果。共享 text 工具的剪贴板礼仪与改写链路。
    /// </summary>
    private async Task<(bool, string, bool)> ReadClipboardAsync(string argsJson, CancellationToken ct)
    {
        string text = GetClipboardText();
        if (text.Trim().Length == 0) return (false, "剪贴板里没有文字内容", false);

        string instruction = GetArg(argsJson, "instruction").Trim();
        if (instruction.Length == 0)
            return (true, "@SPEAK:剪贴板里的内容是：" + Truncate(text, 200), false);

        if (_ai == null) return (false, "文字处理功能不可用", false);
        string rewritten = (await _ai.QuickTextAsync(RewriteSys,
            $"要求：{instruction}\n原文：\n{text}", ct)).Trim();
        rewritten = StripCodeFence(rewritten);
        if (rewritten.Length == 0) return (false, "没有出结果，请再说一次", false);

        if (Regex.IsMatch(instruction, "贴|粘贴|写回|替换"))
        {
            var r = await PasteToCursorAsync(rewritten);
            return (r.Item1, r.Item2, false);
        }
        return (true, "@SPEAK:" + Truncate(rewritten, 300), false);
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    // ---------------- text 工具（insert_text / edit_text） ----------------

    // 改写专用 prompt：输入 = 用户要求 + 原文，输出只有替换文本（参照 aider/cline 的输出纪律）
    private const string RewriteSys = """
        你是文本改写器。输入包含用户的改写要求和一段原文。按用户要求改写原文，只输出改写后的文本本身：不要解释，不要前后缀，不要用 Markdown 代码块包裹。
        """;
    // 生成专用 prompt：纯写入场景，模型只收用户那句话，不读任何上下文
    private const string GenerateSys = """
        你是文字生成助手。按用户要求直接生成内容，只输出文本本身：不要解释，不要前后缀，不要用 Markdown 代码块包裹。
        """;

    /// <summary>从工具参数 JSON 里取字符串字段（取不到返回空串）。</summary>
    private static string GetArg(string argsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>从工具参数 JSON 里取整数字段（取不到返回 0）。</summary>
    private static int GetArgInt(string argsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            if (doc.RootElement.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// insert_text：把文字贴到光标处。文字来源两种——用户原话里说的（text），
    /// 或按指令生成的（instruction，走 LLM，不读任何上下文）。
    /// </summary>
    private async Task<(bool, string)> InsertTextAsync(string argsJson, CancellationToken ct)
    {
        string text = GetArg(argsJson, "text").Trim();
        string instruction = GetArg(argsJson, "instruction").Trim();
        if (text.Length == 0 && instruction.Length == 0)
            return (false, "没有说要输入什么内容");

        if (text.Length == 0)
        {
            if (_ai == null) return (false, "文字生成功能不可用");
            text = (await _ai.QuickTextAsync(GenerateSys, instruction, ct)).Trim();
            text = StripCodeFence(text);
            if (text.Length == 0) return (false, "内容没有生成出来，请再说一次");
        }
        return await PasteToCursorAsync(text);
    }

    /// <summary>
    /// edit_text：改写型工具。锚点二选一：
    /// ① 行号锚点（file+line 可确定）：本地 IO 读第 N 行（零 token）→ LLM 改写 →
    ///    以「原行内容」做 SEARCH、模型输出做 REPLACE 写回（行号只用作取数坐标，防漂移写错位置）；
    /// ② 选区锚点（默认）：备份剪贴板 → Ctrl+C 读选区（无选区则 Home+Shift+End 选中当前行）→
    ///    LLM 改写 → Ctrl+V 原位覆盖 → 恢复剪贴板。
    /// </summary>
    private async Task<(bool, string)> EditTextAsync(string argsJson, CancellationToken ct)
    {
        string instruction = GetArg(argsJson, "instruction").Trim();
        if (instruction.Length == 0) return (false, "没有说明要怎么改");
        if (_ai == null) return (false, "文字改写功能不可用");

        string file = GetArg(argsJson, "file").Trim();
        int line = GetArgInt(argsJson, "line");
        // 行号给了但文件没说：尝试从前台窗口标题里解析文件路径
        if (line > 0 && file.Length == 0) file = TryGetFileFromWindowTitle() ?? "";

        if (line > 0)
            return EditFileLine(file, line, instruction, ct);
        return await EditSelectionAsync(instruction, ct);
    }

    /// <summary>行号锚点：本地读文件第 N 行 → 改写 → 内容匹配写回。读改写全程不过剪贴板。</summary>
    private (bool, string) EditFileLine(string file, int line, string instruction, CancellationToken ct)
    {
        if (file.Length == 0 || !File.Exists(file))
            return (false, "没有找到那个文件，告诉我文件在哪里我再帮你改");
        var lines = File.ReadAllLines(file);
        if (line < 1 || line > lines.Length)
            return (false, $"这个文件只有 {lines.Length} 行，没有第 {line} 行");
        string original = lines[line - 1];
        if (original.Trim().Length == 0)
            return (false, $"第 {line} 行是空行，没法改");

        string rewritten = _ai!.QuickTextAsync(RewriteSys,
            $"要求：{instruction}\n原文：\n{original}", ct).GetAwaiter().GetResult().Trim();
        rewritten = StripCodeFence(rewritten);
        if (rewritten.Length == 0) return (false, "改写没有出结果，请再说一次");

        // SEARCH/REPLACE 写回：按内容匹配而不是行号，避免期间文件被改导致写错位置
        string all = File.ReadAllText(file);
        int idx = all.IndexOf(original, StringComparison.Ordinal);
        if (idx < 0)
            return (false, "文件内容好像刚被改过，为了安全我没有写回，请你手动改一下");
        File.WriteAllText(file, all.Substring(0, idx) + rewritten + all.Substring(idx + original.Length));
        return (true, $"已经把第 {line} 行改好并保存了");
    }

    /// <summary>选区锚点：剪贴板读选区 → 改写 → Ctrl+V 原位覆盖。全程备份恢复剪贴板。</summary>
    private async Task<(bool, string)> EditSelectionAsync(string instruction, CancellationToken ct)
    {
        if (IsTerminalForeground())
            return (false, "终端窗口里我暂时改不了文字，你手动粘贴一下好吗");

        var backup = BackupClipboard();
        try
        {
            // 清空剪贴板再复制：没选区时 Ctrl+C 不会误读到用户之前复制的内容
            InSta(() => { try { Clipboard.Clear(); } catch { } });
            uint seq = GetClipboardSequenceNumber();
            SendKey(VK_C, ctrl: true);
            // 目标应用（Word/Chrome/微信）从收到 Ctrl+C 到剪贴板更新有 100~300ms 延迟，
            // 固定短等待会读到空（表现为"识别不了选中的文字"）——按剪贴板序号变化轮询等复制完成
            string original = WaitClipboardText(seq);

            if (original.Length == 0)
            {
                // 没有现成选区：选中当前行（Home → Shift+End）再复制
                SendKey(VK_HOME);
                SendKey(VK_END, shift: true);
                SendKey(VK_C, ctrl: true);
                original = WaitClipboardText(seq);
            }
            if (original.Trim().Length == 0)
                return (false, "没有读到要修改的文字，你先选中它再叫我好吗");

            string rewritten = (await _ai!.QuickTextAsync(RewriteSys,
                $"要求：{instruction}\n原文：\n{original}", ct)).Trim();
            rewritten = StripCodeFence(rewritten);
            if (rewritten.Length == 0) return (false, "改写没有出结果，请再说一次");

            // 选区还在前台窗口焦点处，Ctrl+V 即原位替换
            if (!SetClipboardText(rewritten)) return (false, "剪贴板被别的程序占用了，没改成");
            await Task.Delay(80, ct);
            SendKey(VK_V, ctrl: true);
            await Task.Delay(120, ct);
            return (true, "已经把改好的文字贴过去了");
        }
        finally
        {
            await Task.Delay(60, CancellationToken.None);
            RestoreClipboard(backup);
        }
    }

    /// <summary>把一段文字贴到当前光标处（备份剪贴板 → 写入 → Ctrl+V → 恢复）。</summary>
    private static async Task<(bool, string)> PasteToCursorAsync(string text)
    {
        if (IsTerminalForeground())
            return (false, "终端窗口里我暂时打不了字，你手动粘贴一下好吗");

        var backup = BackupClipboard();
        if (!SetClipboardText(text)) return (false, "剪贴板被别的程序占用了，没能写入");
        await Task.Delay(80);
        SendKey(VK_V, ctrl: true);
        await Task.Delay(120);
        RestoreClipboard(backup);
        return (true, "已经把内容贴到光标处了");
    }

    /// <summary>剥掉模型偶尔加的 Markdown 代码块围栏（输出纪律的兜底）。</summary>
    private static string StripCodeFence(string s)
    {
        var m = Regex.Match(s.Trim(), @"^```[^\n]*\n([\s\S]*?)\n?```$");
        return m.Success ? m.Groups[1].Value : s;
    }

    // ---- 剪贴板礼仪：备份 / 恢复（含文本、图片、文件列表等常见格式） ----

    private sealed record ClipItem(string Format, object Data);

    private static T InSta<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return func();
        T result = default!;
        var t = new Thread(() => result = func());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static void InSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { action(); return; }
        var t = new Thread(new ThreadStart(action));
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    /// <summary>备份当前剪贴板常见格式（文本/图片/文件列表），失败返回 null（不阻塞主流程）。</summary>
    private static List<ClipItem>? BackupClipboard()
    {
        return InSta(() =>
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return null;
                var items = new List<ClipItem>();
                foreach (var fmt in data.GetFormats())
                {
                    try
                    {
                        var val = data.GetData(fmt);
                        if (val is string || val is Image || val is string[] || val is MemoryStream)
                            items.Add(new ClipItem(fmt, val));
                    }
                    catch { }
                }
                return items;
            }
            catch { return null; }
        });
    }

    /// <summary>恢复剪贴板备份（动作完成后调用，用户之前复制的内容不丢）。</summary>
    private static void RestoreClipboard(List<ClipItem>? backup)
    {
        if (backup == null || backup.Count == 0) return;
        InSta(() =>
        {
            try
            {
                var data = new DataObject();
                foreach (var it in backup)
                {
                    try { data.SetData(it.Format, it.Data); } catch { }
                }
                Clipboard.SetDataObject(data, true, 5, 100);
            }
            catch { }
        });
    }

    private static string GetClipboardText() => InSta(() =>
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { Thread.Sleep(60); }
        }
        return "";
    });

    /// <summary>等待目标应用完成复制：剪贴板序号变化且出现文本才算，最多约 1s，超时返回空串。</summary>
    private static string WaitClipboardText(uint seqBefore)
    {
        for (int i = 0; i < 10; i++)
        {
            if (GetClipboardSequenceNumber() != seqBefore)
            {
                string t = GetClipboardText();
                if (t.Length > 0) return t;
            }
            Thread.Sleep(100);
        }
        return "";
    }

    private static bool SetClipboardText(string text) => InSta(() =>
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(60); }
        }
        return false;
    });

    // ---- 按键注入（SendInput，比 SendKeys 对前台窗口更可靠） ----

    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_END = 0x23, VK_HOME = 0x24,
        VK_C = 0x43, VK_V = 0x56,
        VK_ALT = 0x12, VK_LWIN = 0x5B,
        VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1,
        VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3,
        VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF,
        VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int maxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeyboardInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx, dy, mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }

    /// <summary>向当前前台窗口发一次按键（可选 Ctrl/Shift/Alt/Win 修饰），随后短暂等待让目标应用消化。</summary>
    private static void SendKey(ushort vk, bool ctrl = false, bool shift = false, bool alt = false, bool win = false)
    {
        var seq = BuildKeySeq(vk, ctrl, shift, alt, win, out var _, out var _);
        SendKeyInputs(seq);
        Thread.Sleep(30);
    }

    /// <summary>拼一次按键的完整注入序列：修饰键按下 → 主键按下 → 主键松开 → 修饰键松开。</summary>
    private static List<(ushort vk, bool down)> BuildKeySeq(
        ushort vk, bool ctrl, bool shift, bool alt, bool win,
        out List<(ushort vk, bool down)> downSeq, out List<(ushort vk, bool down)> upSeq)
    {
        var down = new List<(ushort, bool)>();
        if (win) down.Add((VK_LWIN, true));
        if (alt) down.Add((VK_ALT, true));
        if (ctrl) down.Add((VK_CONTROL, true));
        if (shift) down.Add((VK_SHIFT, true));
        down.Add((vk, true));

        var up = new List<(ushort, bool)> { (vk, false) };
        if (shift) up.Add((VK_SHIFT, false));
        if (ctrl) up.Add((VK_CONTROL, false));
        if (alt) up.Add((VK_ALT, false));
        if (win) up.Add((VK_LWIN, false));

        downSeq = down;
        upSeq = up;
        var all = new List<(ushort, bool)>(down);
        all.AddRange(up);
        return all;
    }

    /// <summary>把一组 (vk, down/up) 事件一次性注入前台窗口。</summary>
    private static void SendKeyInputs(List<(ushort vk, bool down)> seq)
    {
        var inputs = new INPUT[seq.Count];
        for (int i = 0; i < seq.Count; i++)
        {
            inputs[i].type = 1;   // INPUT_KEYBOARD
            inputs[i].u.ki.wVk = seq[i].vk;
            if (!seq[i].down) inputs[i].u.ki.dwFlags = 2;   // KEYEVENTF_KEYUP
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // press_keys：键名 → VK 码表（其余字母/数字动态解析）
    private static readonly Dictionary<string, ushort> NamedVk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
        ["capslock"] = 0x14, ["numlock"] = 0x90,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };

    /// <summary>
    /// 按键名近音别名（语音识别容错）：ASR 常把键名听成近音词——「空格」经常被
    /// 识别成「空白键/空百键/斯白键」，按最可能的键归一，不反问用户。
    /// </summary>
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.Ordinal)
    {
        ["空白键"] = "space", ["空百键"] = "space", ["空格键"] = "space", ["空格"] = "space", ["空白"] = "space",
        ["回车键"] = "enter", ["回车"] = "enter", ["确认键"] = "enter", ["换行"] = "enter",
        ["退出键"] = "esc", ["取消键"] = "esc",
        ["退格键"] = "backspace", ["退格"] = "backspace",
        ["删除键"] = "delete", ["删除"] = "delete",
        ["制表键"] = "tab", ["制表符"] = "tab",
        ["上"] = "up", ["下"] = "down", ["左"] = "left", ["右"] = "right",
        ["上箭头"] = "up", ["下箭头"] = "down", ["左箭头"] = "left", ["右箭头"] = "right",
        ["截屏键"] = "printscreen", ["截屏"] = "printscreen",
        ["大写锁定"] = "capslock", ["大写键"] = "capslock",
        ["小写锁定"] = "numlock",
        ["翻上"] = "pageup", ["翻下"] = "pagedown", ["上一页"] = "pageup", ["下一页"] = "pagedown",
    };

    /// <summary>
    /// 模拟按键/快捷键（press_keys 工具）：解析 "Alt+F4"、"F5"、"Enter"、"Ctrl+Shift+Esc"、"Win+D"
    /// 这类描述并真实注入到前台窗口。修饰键在前、+ 分隔，主键只有一个。
    /// 传进来的键名先做归一化（去尾部「键」、查近音别名）再查 VK 码，认不出才报错。
    /// duration（秒，可选）：按住主键这么久再松开（如方向右键按住 30 秒）；说了按住但没说多久默认 3 秒；
    ///           完全没提按住 = 只单按一次。
    /// count（次，可选）+ interval（毫秒，可选）：连续按键 count 次、基准间隔 interval（间隔随机浮动）；
    ///           说了「连按几下」没说次数默认 5 次。按住/连按期间用户开口说话即打断退出。
    /// </summary>
    private async Task<(bool, string, bool)> PressKeysAsync(string argsJson, CancellationToken ct = default)
    {
        string keys = GetArg(argsJson, "keys").Trim();
        if (keys.Length == 0) return (false, "没有说明要按哪个键", false);

        var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (false, "按键描述无法识别", false);

        bool ctrl = false, shift = false, alt = false, win = false;
        ushort main = 0;
        string mainName = "";
        foreach (var raw in parts)
        {
            string p = raw;
            // 归一化：去掉中文尾部「键」（如 空格键→空格），再查近音别名（空白键→space）
            if (p.EndsWith("键", StringComparison.Ordinal) && p.Length > 1) p = p[..^1];
            if (KeyAliases.TryGetValue(p, out var aliased)) p = aliased;

            ushort vk;
            if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("control", StringComparison.OrdinalIgnoreCase))
            { ctrl = true; continue; }
            if (p.Equals("shift", StringComparison.OrdinalIgnoreCase)) { shift = true; continue; }
            if (p.Equals("alt", StringComparison.OrdinalIgnoreCase)) { alt = true; continue; }
            if (p.Equals("win", StringComparison.OrdinalIgnoreCase) || p.Equals("windows", StringComparison.OrdinalIgnoreCase))
            { win = true; continue; }

            vk = p.Length switch
            {
                1 when char.IsAsciiLetter(p[0]) => (ushort)(0x41 + char.ToUpperInvariant(p[0]) - 'A'),
                1 when char.IsAsciiDigit(p[0]) => (ushort)(0x30 + (p[0] - '0')),
                _ => NamedVk.TryGetValue(p, out var v) ? v : (ushort)0,
            };
            if (vk == 0) return (false, $"不认识的键：{p}", false);
            if (main != 0) return (false, $"一次只能按一个主键，「{keys}」无法识别", false);
            main = vk;
            mainName = p;
        }
        if (main == 0) return (false, "只给了修饰键，没说要按什么键", false);

        // 按住时长（秒）：用户说「按住 XX 多少秒」时模型会传 duration；说了按住但没说多久默认 3 秒；
        // 完全没提按住就单按一次
        string durationRaw = GetArg(argsJson, "duration").Trim();
        double duration = 0;
        double.TryParse(durationRaw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out duration);
        if (duration > 600) duration = 600;   // 兜底：最长 10 分钟，防止模型传离谱值
        if (durationRaw.Length > 0 && duration <= 0) duration = 3;   // 「按住一会儿」没说多久：默认 3 秒
        if (duration > 0)
            return await HoldKeyAsync(main, mainName, ctrl, shift, alt, win, duration, ct);

        // 连续按键：count 次 + 基准间隔毫秒（间隔拟人随机浮动），说话可打断
        string countRaw = GetArg(argsJson, "count").Trim();
        int.TryParse(countRaw, out int repeatCount);
        if (countRaw.Length > 0 && repeatCount <= 0) repeatCount = 5;   // 「连按几下」没说次数：默认 5 次
        double.TryParse(GetArg(argsJson, "interval"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double interval);
        if (repeatCount > 200) repeatCount = 200;
        if (interval <= 0) interval = 150;
        if (interval > 5000) interval = 5000;
        if (repeatCount > 1)
        {
            string combo = DescribeCombo(mainName, ctrl, shift, alt, win);
            try
            {
                for (int i = 0; i < repeatCount; i++)
                {
                    SendKey(main, ctrl, shift, alt, win);
                    if (i < repeatCount - 1) await Task.Delay(HumanJitter(interval), ct);
                }
            }
            catch (OperationCanceledException) { throw; }   // 说话打断：每次都是完整按下-松开对
            return (true, $"已连续按下 {combo} {repeatCount} 次（间隔约 {interval:0} 毫秒随机浮动）", false);
        }

        SendKey(main, ctrl, shift, alt, win);
        // 组合描述统一成标准形式回给用户（如 ctrl+c → Ctrl+C）
        return (true, $"已按下 {DescribeCombo(mainName, ctrl, shift, alt, win)}", true);
    }

    /// <summary>按键的口语友好显示（回给人听）：方向键/常用键用中文名，其余用原名大写。</summary>
    private static readonly Dictionary<string, string> KeyDisplay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = "上方向键", ["down"] = "下方向键", ["left"] = "左方向键", ["right"] = "右方向键",
        ["space"] = "空格", ["enter"] = "回车", ["esc"] = "Esc", ["tab"] = "Tab",
        ["backspace"] = "退格", ["delete"] = "Delete", ["home"] = "Home", ["end"] = "End",
        ["pageup"] = "PageUp", ["pagedown"] = "PageDown", ["printscreen"] = "截屏键",
    };

    /// <summary>组合键的标准形式描述（如 ctrl+c → Ctrl+C、right → 右方向键）。</summary>
    private static string DescribeCombo(string mainName, bool ctrl, bool shift, bool alt, bool win)
    {
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (shift) sb.Append("Shift+");
        if (alt) sb.Append("Alt+");
        if (win) sb.Append("Win+");
        sb.Append(mainName.Length == 1 ? mainName.ToUpperInvariant()
            : KeyDisplay.TryGetValue(mainName, out var d) ? d : mainName);
        return sb.ToString();
    }

    /// <summary>
    /// 按住主键指定秒数再松开（duration > 0 时走这里）：先注入按下事件，随后轮询等待，
    /// 到时自然松开。用户开口说话 → 会话侧 Interrupt("phase") 取消 ct → 这里立刻松键退出，
    /// 不需要用户做别的电脑操作来打断。
    /// </summary>
    private async Task<(bool, string, bool)> HoldKeyAsync(
        ushort main, string mainName, bool ctrl, bool shift, bool alt, bool win,
        double duration, CancellationToken ct)
    {
        BuildKeySeq(main, ctrl, shift, alt, win, out var downSeq, out var upSeq);
        SendKeyInputs(downSeq);
        string combo = DescribeCombo(mainName, ctrl, shift, alt, win);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalSeconds < duration)
                await Task.Delay(80, ct);   // 说话打断 = ct 取消，Task.Delay 直接抛出
        }
        catch (OperationCanceledException)
        {
            SendKeyInputs(upSeq);   // 说话打断/会话退出都要保证松键，不能让键一直卡住
            throw;
        }
        SendKeyInputs(upSeq);
        return (true, $"{combo}已按住 {duration:0.#} 秒并松开", false);
    }

    /// <summary>前台窗口是否终端类（Windows Terminal / conhost）——粘贴语义不同，text 工具明确不支持。</summary>
    private static bool IsTerminalForeground()
    {
        var sb = new StringBuilder(64);
        GetClassName(GetForegroundWindow(), sb, 64);
        string cls = sb.ToString();
        return cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS";
    }

    /// <summary>从前台窗口标题解析文件路径（如编辑器标题「D:\x\a.cs - Visual Studio」），取不到返回 null。</summary>
    private static string? TryGetFileFromWindowTitle()
    {
        var sb = new StringBuilder(512);
        GetWindowText(GetForegroundWindow(), sb, 512);
        var m = Regex.Match(sb.ToString(), @"[A-Za-z]:\\[^\\/:*?""<>|\r\n]+\.[A-Za-z0-9]{1,6}");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// 取前台资源管理器窗口当前打开的文件夹路径（Shell COM，问题反馈：语音问
    /// 「这个文件夹」时助手不知道资源管理器里开的是哪）。非资源管理器或解析失败返回 null。
    /// </summary>
    public static string? TryGetForegroundExplorerPath()
    {
        var sb = new StringBuilder(64);
        GetClassName(GetForegroundWindow(), sb, 64);
        if (sb.ToString() is not ("CabinetWClass" or "ExploreWClass")) return null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            IntPtr fg = GetForegroundWindow();
            string? first = null;
            foreach (var w in shell.Windows())
            {
                try
                {
                    string path = (string)(w.Document.Folder.Self.Path ?? "");
                    if (path.Length == 0) continue;
                    if ((IntPtr)(long)w.HWND == fg) return path;    // 优先前台那个
                    first ??= path;
                }
                catch { }   // IE 窗口等无 Folder，跳过
            }
            return first;
        }
        catch { return null; }
    }

    /// <summary>
    /// 取资源管理器当前选中的文件/文件夹（Shell COM，rename_selected 用）：优先前台窗口，
    /// 否则第一个有选中项的窗口。多选时 count>1。path 为 null 时 err 说明原因。
    /// 必须在 STA 线程调用（InSta 包裹），复用 try/catch 跳过 IE 等无 Document 的窗口。
    /// </summary>
    private static (string? path, int count, string err) GetExplorerSelection()
    {
        return InSta(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return ((string?)null, 0, "系统不支持资源管理器自动操作");
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return ((string?)null, 0, "没有打开的资源管理器窗口");
                IntPtr fg = GetForegroundWindow();
                (string path, int count)? first = null;
                foreach (var w in shell.Windows())
                {
                    try
                    {
                        dynamic doc = w.Document;
                        dynamic sel = doc.SelectedItems();
                        int n = (int)sel.Count;
                        if (n == 0) continue;
                        string p = (string)sel.Item(0).Path;
                        if ((IntPtr)(long)w.HWND == fg) return ((string?)p, n, "");
                        first ??= (p, n);
                    }
                    catch { }   // IE 窗口等无 SelectedItems，跳过
                }
                if (first != null) return ((string?)first.Value.path, first.Value.count, "");
                return ((string?)null, 0, "没有找到有选中文件的资源管理器窗口，先打开文件夹并选中一个文件");
            }
            catch (Exception ex) { return ((string?)null, 0, "读取选中状态失败：" + ex.Message); }
        });
    }

    /// <summary>
    /// rename_selected 的内容摘要：文件抽正文（复用知识库的文本提取，txt/md/docx/pdf），
    /// 文件夹列内部条目。摘要压成一行并截断，提取失败返回空串（起名退化为按原文件名去噪）。
    /// </summary>
    private static string ReadFileBrief(string path, bool isDir)
    {
        try
        {
            if (isDir)
            {
                var names = Directory.EnumerateFileSystemEntries(path)
                    .Take(15).Select(Path.GetFileName).ToList();
                return names.Count > 0 ? "文件夹内条目：" + string.Join("、", names) : "空文件夹";
            }
            string text = MemoryService.ExtractText(path);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 1500 ? text[..1500] : text;
        }
        catch { return ""; }
    }

    /// <summary>
    /// 重命名资源管理器选中的文件/文件夹（rename_selected）：Shell COM 取选中项 →
    /// 读内容摘要（用户有命名要求时一并给模型）→ 小模型起简短名 → 清洗非法字符 →
    /// File/Directory.Move。同名冲突追加「 2」「 3」递增。
    /// </summary>
    private async Task<(bool, string, bool)> RenameSelectedAsync(string argsJson, CancellationToken ct)
    {
        if (_ai == null) return (false, "重命名功能不可用", false);
        string instruction = GetArg(argsJson, "instruction").Trim();

        var (path, count, err) = GetExplorerSelection();
        if (path == null) return (false, err, false);
        if (count > 1) return (false, "你选中了多个文件，先只选中一个再让我重命名", false);
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path))
            return (false, "选中的不是本地文件（比如「此电脑」「回收站」这类位置），重命名不了", false);

        string originName = Path.GetFileName(path);
        string ext = isDir ? "" : Path.GetExtension(path);

        string brief = ReadFileBrief(path, isDir);
        if (instruction.Length > 0) brief = $"命名要求：{instruction}\n\n{brief}";
        string raw = await _ai.GenerateFileNameAsync(originName, brief, ct);
        raw = StripCodeFence(raw).Trim().Trim('"', '「', '」', '《', '》', '\'', ' ');
        // 清洗：去掉文件名非法字符和换行，结尾的点/空格去掉（Windows 不允许）
        var invalid = Path.GetInvalidFileNameChars();
        var sbName = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sbName.Append(Array.IndexOf(invalid, ch) >= 0 || ch == '\n' || ch == '\r' ? "" : ch);
        raw = sbName.ToString().Trim().TrimEnd('.');
        if (raw.Length == 0)
            return (false, "想不出合适的名字，你描述一下这个文件是干什么的，我再帮你命名", false);

        string dir = Path.GetDirectoryName(path)!;
        string target = Path.Combine(dir, raw + ext);
        for (int i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(dir, $"{raw} {i}{ext}");

        if (isDir) Directory.Move(path, target);
        else File.Move(path, target);
        return (true, $"已把「{originName}」重命名为「{Path.GetFileName(target)}」", false);
    }

    // 泛称应用 → 系统 shell 目标：Windows 会按用户设置的默认程序分派
    // （打开浏览器 = 默认浏览器打开网址，打开邮件 = 默认邮件客户端）
    private static readonly (string Word, string Target)[] GenericApps =
    {
        ("浏览器", "https://www.baidu.com"),
        ("邮件", "mailto:"),
        ("邮箱", "mailto:"),
    };

    // 内置中英别名表：常见中文名 ↔ 规范英文名（命中别名按「完全相等」级计分）。
    // En 侧要与实际候选名（exe 名去掉 .exe / .lnk 显示名去掉空格）对得上：
    //   chrome.exe→"chrome"、msedge→"msedge"、Visual Studio Code.lnk→"visualstudiocode"
    private static readonly (string Cn, string En)[] Aliases =
    {
        // 国外浏览器
        ("谷歌浏览器", "chrome"), ("谷歌", "chrome"),
        ("火狐浏览器", "firefox"), ("火狐", "firefox"),
        ("欧朋浏览器", "opera"), ("欧朋", "opera"),
        ("微软浏览器", "msedge"), ("edge浏览器", "msedge"),
        // 国外常用软件（Adobe 系 / 开发 / 通讯）
        ("ps", "photoshop"), ("pr", "premiere"), ("ae", "aftereffects"),
        ("vscode", "visualstudiocode"), ("电报", "telegram"),
        // 国内常用（微信 4.0 的进程名是 Weixin.exe，wechat/weixin 两个别名都留）
        ("微信", "wechat"), ("微信", "weixin"), ("企业微信", "wecom"), ("腾讯会议", "wemeet"),
        ("钉钉", "dingtalk"), ("飞书", "feishu"), ("飞书", "lark"),
        ("百度网盘", "baidunetdisk"), ("网易云音乐", "cloudmusic"),
        ("迅雷", "thunder"),
        // 系统工具
        ("计算器", "calculator"), ("记事本", "notepad"), ("画图", "paint"),
        ("截图工具", "snippingtool"), ("任务管理器", "taskmgr"),
        ("文件资源管理器", "explorer"), ("控制面板", "control"),
        ("命令提示符", "cmd"), ("命令行", "cmd"), ("终端", "terminal"),
        ("注册表", "regedit"), ("远程桌面", "mstsc"), ("写字板", "wordpad"),
        ("ppt", "powerpnt"),
    };

    /// <summary>
    /// 候选名打分（替代旧「包含即命中、取第一个」）：完全相等最高，别名次之，前缀再次，
    /// 包含按候选名长度惩罚（越短越准，「微信」优于「微信开发者工具」）。≤0 表示不命中。
    /// </summary>
    private static int ScoreName(string candidate, string name, string lower, bool hasCjk)
    {
        var c = candidate.ToLowerInvariant().Replace(" ", "").Replace(".exe", "");
        var n = lower.Replace(" ", "").Replace(".exe", "");
        if (c.Length == 0 || n.Length == 0) return 0;
        if (c == n) return 100;
        foreach (var (cn, en) in Aliases)
            if ((n == cn && c == en) || (n == en && c == cn)) return 95;
        if (hasCjk)
        {
            // 中文目标：双向包含都算命中，短名优先
            if (c.Contains(n) && n.Length > 1) return 60 - Math.Min(30, c.Length - n.Length);
            if (n.Contains(c) && c.Length > 1) return 55 - Math.Min(30, n.Length - c.Length);
        }
        else
        {
            if (c.StartsWith(n)) return 80 - Math.Min(20, c.Length - n.Length);
            if (c.Contains(n)) return 60 - Math.Min(30, c.Length - n.Length);
        }
        return 0;
    }

    /// <summary>
    /// 按名字打开应用：①泛称走系统默认程序 ②App Paths + 开始菜单 .lnk 统一收集候选、评分取最高
    /// ③启动后短暂轮询进程列表校验（暴露「打开了但其实没打开」的静默失败）。
    /// </summary>
    private static async Task<(bool, string)> OpenAppAsync(string appName)
    {
        string name = appName.Trim();
        string lower = name.ToLowerInvariant();
        string norm = lower.Replace(" ", "");
        bool hasCjk = name.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ⓪ 泛称（或加「默认/系统/电脑」前缀）→ 系统默认程序；
        //    「谷歌浏览器」这类带具体名字的不算泛称，仍按名字搜索
        foreach (var (word, target) in GenericApps)
        {
            if (norm == word || norm == "默认" + word || norm == "系统" + word || norm == "电脑" + word)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return (true, $"已打开{name}（系统默认）");
                }
                catch (Exception ex) { return (false, ex.Message); }
            }
        }

        // ① 统一收集候选（App Paths 的 exe + 开始菜单 .lnk + UWP/MSIX），全部打分取最高，
        //    避免「微信」误开「微信开发者工具」
        //    元组末位 IsShell：.lnk/UWP 经 shell 启动，目标进程名不可靠，校验放宽为「出现任意新进程」
        var candidates = new List<(int Score, string Path, bool IsShell, string Display, string ProcName)>();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                int score = ScoreName(sub, name, lower, hasCjk);
                if (score <= 0) continue;
                var exe = key.OpenSubKey(sub)?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(exe)) continue;
                exe = Environment.ExpandEnvironmentVariables(exe.Replace("\"", ""));
                candidates.Add((score, exe, false, sub, Path.GetFileNameWithoutExtension(exe)));
            }
        }
        foreach (var lnk in GetStartMenuLinks())
        {
            var display = Path.GetFileNameWithoutExtension(lnk);
            int score = ScoreName(display, name, lower, hasCjk);
            if (score <= 0) continue;
            candidates.Add((score, lnk, true, display, display));
        }
        foreach (var (uwpName, aumid) in GetUwpApps())
        {
            int score = ScoreName(uwpName, name, lower, hasCjk);
            if (score <= 0) continue;
            candidates.Add((score, "shell:appsFolder\\" + aumid, true, uwpName, uwpName));
        }
        var best = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Display.Length)   // 同分取短名（更准）
            .FirstOrDefault();
        if (best.Score <= 0) return (false, $"没有找到「{name}」，你可以从开始菜单启动");

        // ①.5 目标已在运行就不再重复启动：组合指令的第二步常把「登录/界面操作」误规划成
        //     再次 open_app（如「打开微信并登录」）；App Paths 候选按进程名查，.lnk/UWP 的
        //     显示名与进程名常不一致（微信.lnk→Weixin.exe）查不到就照常走 shell 启动
        //     （单实例应用自己会唤起已有窗口，多开无意义；多实例应用避免多开窗口）
        try
        {
            if (Process.GetProcessesByName(best.ProcName).Length > 0 ||
                Process.GetProcessesByName(best.Display).Length > 0)
                return (true, $"{best.Display} 已经在运行");
        }
        catch { }

        // ② 启动：.lnk/UWP 一律走 shell（等效双击，系统自动处理工作目录/参数）；
        //    App Paths 命中的 exe 带上所在目录作工作目录（部分应用依赖相对路径资源）
        var psi = new ProcessStartInfo(best.Path) { UseShellExecute = true };
        var dir = Path.GetDirectoryName(best.Path);
        if (!best.IsShell && Directory.Exists(dir)) psi.WorkingDirectory = dir!;

        var before = GetProcessIds();
        try { Process.Start(psi); }
        catch (Exception ex) { return (false, ex.Message); }

        // ③ 执行后校验：目标进程已在运行、或 3 秒内出现同名新进程才算成功
        string proc = best.ProcName.ToLowerInvariant();
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(200);
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.ProcessName.ToLowerInvariant() == proc) return (true, $"已打开 {best.Display}"); }
                catch { }
                finally { p.Dispose(); }
            }
            // .lnk/UWP 的目标进程名可能与显示名不同：有任何新进程也视为启动成功
            if (best.IsShell && GetProcessIds().Any(id => !before.Contains(id)))
                return (true, $"已打开 {best.Display}");
        }
        return (false, $"「{best.Display}」好像没打开成功，你可以从开始菜单里找找它");
    }

    /// <summary>当前全部进程 Id 快照（校验用）。</summary>
    private static HashSet<int> GetProcessIds()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.Id); } catch { }
            p.Dispose();
        }
        return set;
    }

    private static string[]? _startMenuLinks;

    /// <summary>开始菜单全部 .lnk（当前用户 + 所有用户），进程内缓存。</summary>
    private static string[] GetStartMenuLinks()
    {
        if (_startMenuLinks != null) return _startMenuLinks;
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        _startMenuLinks = dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", opts))
            .ToArray();
        return _startMenuLinks;
    }

    private static List<(string Name, string Aumid)>? _uwpApps;

    /// <summary>
    /// UWP/MSIX 应用索引：枚举 shell:AppsFolder（条目 Path 即 AUMID，可用 shell:appsFolder\&lt;AUMID&gt; 启动），
    /// 覆盖商店版/系统内置应用。Shell COM 不可用时返回空表，不影响桌面应用链路。进程内缓存。
    /// </summary>
    private static List<(string Name, string Aumid)> GetUwpApps()
    {
        if (_uwpApps != null) return _uwpApps;
        var list = new List<(string, string)>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string appName = item.Name;
                string aumid = item.Path;
                if (!string.IsNullOrEmpty(appName) && !string.IsNullOrEmpty(aumid))
                    list.Add((appName, aumid));
            }
        }
        catch { /* COM 枚举失败：UWP 索引缺失，走其余候选 */ }
        _uwpApps = list;
        return _uwpApps;
    }
}
