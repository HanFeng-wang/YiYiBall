using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenTranslator;

/// <summary>语音会话状态（球内声纹颜色 / 球下状态小字随之切换）。</summary>
public enum VoiceState
{
    Listening,    // 在听（绿辉光，声纹随音量起伏）
    Recognizing,  // 识别中
    Routing,      // 意图路由中（纯分类，不播报）
    Thinking,     // 对话生成中
    Planning,     // 操作规划中
    Executing,    // 工具执行中
    Speaking,     // 播报中（紫辉光，可打断）
}

/// <summary>
/// 语音会话编排器（方案 §2.3 状态机 + §2.4 伪全双工）：
/// listening → ASR → 意图路由 → 对话直答 / 规划→口播→执行→结果口播 → 播报 → 回 listening。
/// 伪全双工：麦克风/VAD 全程不停——
/// - speaking 中开口：回声基线守卫（播报起始 ~500ms 建基线，超基线 ×1.8 且 +0.10 持续 ~250ms）
///   → 停 TTS 回 listening；完整回复已入 history，未播句子留在待播队列（说"继续"续播）。
/// - thinking/planning/executing 中开口：CancellationToken 取消该阶段、丢弃中间结果 → listening。
/// - 退出：Esc（外部调用 RequestExit）、"再见"类词（播完告别语）、listening 静默 6s。
/// 所有回调已调度到 uiSync 控件线程。
/// </summary>
public sealed class VoiceSession : IDisposable
{
    // 明确告别/叫停类：直接退出语音会话
    private static readonly Regex ExitWord = new("再见|拜拜|退下|闭嘴|住口|别说了|别播了|结束吧|退出语音|关闭语音|结束对话|停止对话", RegexOptions.Compiled);
    // 模糊指令（关闭/退出/结束/停止）：后面没带对象（纯指令或只带语气词/客套词）才算关闭语音助手；
    // 像「关闭微信」带了对象，不是让助手退出，走正常指令链路
    private static readonly Regex BareStopWord = new("^(那|你|给我|请|麻烦你)*(关闭|关掉|退出|结束|停止|停)(一下|吧|呀|啊|了)*$", RegexOptions.Compiled);
    private static readonly Regex ContinueWord = new("^继续|接着说|接着播|继续说", RegexOptions.Compiled);
    // 操作类指令关键词：命中直接走操作分支，不经 LLM 分类（防模糊口语被误判为 chat）
    // 「生成/输入/打字/润色/改写/帮我写/第N行」→ text 工具；「关机/定时/闹钟/音量/搜/窗口」→ run_command 与 §9 工具；
    // 「记住/记一下/存成技能/执行」→ 记忆与技能库（方案 §10.6）
    private static readonly Regex TaskWord = new("打开|启动|开启|运行|帮我开|卸载|安装|截图|截屏|翻译一下|翻译这个|翻译屏幕|润色|改写|生成|输入文字|打字|帮我写|第.{1,4}行|光标|粘贴|替换成|查找|搜索|关机|重启|定时|闹钟|命令行|终端|音量|静音|搜一下|帮我搜|最小化|切到|置顶|最顶层|放.{0,6}(前面|顶层)|记住|记一下|存成技能|执行|帮我找|找一下|点一下|播放|暂停|下一首|上一首|切歌|放首歌|放个音乐|开关|飞行模式|省电模式|无线网|蓝牙|全屏|空格|空白键|回车键|重命名|改名|按住|发消息|发给|发微信|给.{1,12}发|联系人|聊天记录|进入.{0,8}(游戏|原神|星穹铁道)|(?i:wifi)|(?i:f\\s*\\d{1,2})", RegexOptions.Compiled);
    // 「打开/启动」类指令：即使模型误选截图翻译也纠正为 open_app（见 ExecuteTaskAsync 守卫）
    private static readonly Regex OpenAppWord = new("打开|启动|开启|运行|帮我开", RegexOptions.Compiled);
    // 应用语义操作：即使句子里有「打开」，也不要把发送消息/搜索播放误纠正成单纯 open_app
    private static readonly Regex AppActionIntent = new("发消息|发给|发微信|给.{1,12}发|联系人|聊天记录|置顶|最顶层|放.{0,6}(前面|顶层)|搜索.{0,20}(打开|播放)|搜.{0,20}(打开|播放)|抖音|优酷|爱奇艺|腾讯视频|QQ音乐|网易云|哔哩哔哩|小红书|原神|星穹铁道", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlayIntent = new("播放|放一下|放出来|放给我|听一下|听听|开始听|开始看|看视频|听歌", RegexOptions.Compiled);
    private static readonly Regex OpenResultIntent = new("第一条|第一个|首个|第一个结果|第一条结果|点开|打开.{0,8}结果|播放", RegexOptions.Compiled);
    // 只有用户明确要求关闭/退出时，才允许关闭窗口或发送 Alt+F4，防止执行其他任务时误关应用。
    private static readonly Regex CloseWindowIntent = new("关闭|关掉|关了|关一下|关窗口|关应用|退出|结束|终止|杀掉|(?i:alt\\s*\\+?\\s*f4)", RegexOptions.Compiled);
    // 澄清等待中用户改主意：取消待执行指令
    private static readonly Regex CancelWord = new("算了|不用了|不要了|取消", RegexOptions.Compiled);

    private static readonly string ChatSys = """
        你是桌面语音助手「依依」。用中文口语回答，一定要简短：最多两句话，直接说结果。不要思考过程，不要 Markdown，不要表情符号，不要换行。
        """;
    private static readonly string PlanSpeechSys = """
        把输入里的电脑操作计划转成一句口语提示，一句话以内，像"我想想，我先帮你打开微信"。不要列步骤细节，不要 Markdown。只输出这一句话。
        """;
    private static readonly string ResultOkSys = """
        用一句简短中文口语确认操作已完成，像"好的，我已经打开微信了"。不要 Markdown，不要表情。只输出这一句话。
        """;
    private static readonly string ResultFailSys = """
        电脑操作失败了。用一两句简短中文口语说明原因并请用户帮忙手动完成，像"因为×××，我没有完成这个操作，请您帮我来完成"。不要 Markdown，不要表情。只输出这段话。
        """;

    private readonly AppConfig _cfg;
    private readonly AiService _ai;
    private readonly TtsService _tts;
    private readonly VoiceRecorder _rec;
    private readonly AppToolbox _tools;
    private readonly MemoryService? _mem;      // 记忆与知识库（方案 §10），未启用时为 null
    private readonly Control _uiSync;

    private readonly List<(string role, string content)> _history = new();
    private CancellationTokenSource _sessionCts = new();   // 整个会话
    private CancellationTokenSource? _phaseCts;            // 当前模型阶段（可被打断取消）
    private readonly object _phaseLock = new();

    // ---- 声纹/打断检测（WaveIn 线程高频读写，volatile/简单类型） ----
    private volatile VoiceState _state = VoiceState.Listening;
    private volatile float _level;                         // 最新 RMS（球体每帧读取）
    private volatile bool _active;

    // 回声基线守卫
    private long _speakStartTicks;
    private double _echoSum;
    private int _echoN;
    private float _echoBaseline = -1f;
    private int _overFrames;                               // 超阈连续帧

    // 静默超时
    private long _lastVoiceTick;

    // 待播队列（打断后剩余句子，说"继续"续播）
    // 注意：播报循环在工作线程、Start() 清空在会话线程，所有访问必须持锁（否则偶发 IndexOutOfRange）
    private readonly List<string> _pending = new();
    private readonly object _pendingLock = new();

    // 澄清等待：反问后暂存的待执行指令（用户下一句语音作为补充回答拼接执行；会话重开时清空）
    private string? _pendingTask;

    public VoiceState State => _state;
    public float Level => _level;

    /// <summary>状态变化（UI 线程）。</summary>
    public event Action<VoiceState>? StateChanged;
    /// <summary>球下小字追加一轮（UI 线程）：who = you / ai / sys。</summary>
    public event Action<string, string>? TurnAdded;
    /// <summary>会话退出（UI 线程，用户 Esc / 告别 / 静默超时）。</summary>
    public event Action? Exited;

    public VoiceSession(AppConfig cfg, AiService ai, TtsService tts, VoiceRecorder rec,
        AppToolbox tools, Control uiSync, MemoryService? memory = null)
    {
        _cfg = cfg;
        _ai = ai;
        _tts = tts;
        _rec = rec;
        _tools = tools;
        _uiSync = uiSync;
        _mem = memory;
    }

    private void Post(Action a) => _uiSync.BeginInvoke(a);

    private void SetState(VoiceState s)
    {
        _state = s;
        Post(() => StateChanged?.Invoke(s));
    }

    private void AddTurn(string who, string text) => Post(() => TurnAdded?.Invoke(who, text));

    // ---------------- 生命周期 ----------------

    public void Start()
    {
        _active = true;
        _sessionCts = new CancellationTokenSource();
        _history.Clear();
        _pendingTask = null;
        lock (_pendingLock) _pending.Clear();
        _rec.VolumeChanged += OnVolume;
        _rec.UtteranceReady += OnUtterance;
        _rec.Error += OnRecError;
        _rec.SegmentationEnabled = true;
        _lastVoiceTick = Environment.TickCount64;
        SetState(VoiceState.Listening);
        AddTurn("sys", "（语音模式：说「再见」退出，播报中开口可打断）");
    }

    public void RequestExit() => ExitInternal(farewell: false);

    private void ExitInternal(bool farewell)
    {
        if (!_active) return;
        _active = false;

        void RunFarewell()
        {
            if (farewell)
            {
                _tts.SpeakSync("好的，下次再叫我哦，再见。");
            }
            CleanupAndNotify();
        }
        if (farewell) Task.Run(RunFarewell);
        else CleanupAndNotify();
    }

    private void CleanupAndNotify()
    {
        try
        {
            lock (_phaseLock) { _phaseCts?.Cancel(); _phaseCts?.Dispose(); _phaseCts = null; }
            _sessionCts.Cancel();
            _tts.Cancel();
            _rec.SegmentationEnabled = false;
            _rec.VolumeChanged -= OnVolume;
            _rec.UtteranceReady -= OnUtterance;
            _rec.Error -= OnRecError;
            Post(() => Exited?.Invoke());
        }
        catch { }
    }

    // ---------------- 麦克风帧处理（WaveIn 线程，~20ms 一次） ----------------

    private void OnVolume(float rms)
    {
        _level = rms;
        if (!_active) return;

        switch (_state)
        {
            case VoiceState.Listening:
            {
                if (rms > _rec.Threshold) _lastVoiceTick = Environment.TickCount64;
                else if (Environment.TickCount64 - _lastVoiceTick > 6000)
                {
                    _lastVoiceTick = Environment.TickCount64;
                    Task.Run(() => ExitInternal(farewell: false));   // 静默 6s 直接退出
                }
                break;
            }
            case VoiceState.Speaking:
            {
                // 回声基线：播报起始 ~500ms 平均电平
                long elapsed = Environment.TickCount64 - _speakStartTicks;
                if (elapsed < 500)
                {
                    _echoSum += rms; _echoN++;
                }
                else
                {
                    if (_echoBaseline < 0 && _echoN > 0)
                        _echoBaseline = Math.Max(0.005f, (float)(_echoSum / _echoN));

                    // 显著超基线（×1.8 且 +0.10）持续 ~250ms（12 帧）才判打断
                    bool over = rms > _echoBaseline * 1.8f + 0.10f;
                    _overFrames = over ? _overFrames + 1 : 0;
                    if (_overFrames >= 12) { _overFrames = 0; Task.Run(() => Interrupt("speaking")); }
                }
                break;
            }
            case VoiceState.Thinking:
            case VoiceState.Planning:
            case VoiceState.Executing:
            {
                // 思考阶段打断：开口（过阈 ~120ms）即取消该阶段
                bool loud = rms > _rec.Threshold;
                _overFrames = loud ? _overFrames + 1 : 0;
                if (_overFrames >= 6) { _overFrames = 0; Task.Run(() => Interrupt("phase")); }
                break;
            }
        }
    }

    private void OnRecError(string msg) => AddTurn("sys", "（麦克风错误：" + msg + "）");

    /// <summary>打断：speaking = 停 TTS 留待播队列；思考阶段 = 取消模型调用。切回 listening。</summary>
    private void Interrupt(string kind)
    {
        if (!_active) return;
        if (kind == "speaking" && _state == VoiceState.Speaking)
        {
            _tts.Cancel();
            lock (_phaseLock) { _phaseCts?.Cancel(); }
            int remain;
            lock (_pendingLock) remain = _pending.Count;   // 主播报循环维护（含未播句）
            AddTurn("sys", remain > 0 ? $"（已打断播报，说「继续」接着听剩余 {remain} 句）"
                                      : "（已打断播报）");
            ToListening();
        }
        else if (kind == "phase")
        {
            lock (_phaseLock) { _phaseCts?.Cancel(); }
            AddTurn("sys", "（已打断，请再说）");
            ToListening();
        }
    }

    private void ToListening()
    {
        _rec.SegmentationEnabled = true;
        _lastVoiceTick = Environment.TickCount64;
        SetState(VoiceState.Listening);
    }

    // ---------------- 语句处理主链路（单管线顺序执行） ----------------

    private async void OnUtterance(byte[] wav)
    {
        if (!_active || _state != VoiceState.Listening) return;
        var sessionCt = _sessionCts.Token;
        try
        {
            SetState(VoiceState.Recognizing);
            _rec.SegmentationEnabled = false;
            VoiceRecorder.Log($"收到语句：{wav.Length / 1024} KB，送 ASR（{_cfg.AsrModel}）");

            string text = (await WithPhase(_ai.TranscribeAsync(wav, sessionCt))).Trim();
            if (text.Length == 0)
            {
                // 空识别不再静默：明确告知用户（此前静默回听，看起来像"说话不识别"）
                VoiceRecorder.Log("ASR 返回空文本，回听");
                AddTurn("sys", "（没听清，请再说说）");
                ToListening();
                return;
            }
            VoiceRecorder.Log("ASR：" + text);
            AddTurn("you", text);

            // 退出词：明确告别类直接退出；「关闭/退出」类须是纯指令（没带对象）才算关语音助手
            if (ExitWord.IsMatch(text) || BareStopWord.IsMatch(text.Trim()))
            { ExitInternal(farewell: true); return; }

            // "继续"：待播队列续播（不进 LLM）
            bool hasPending;
            lock (_pendingLock) hasPending = _pending.Count > 0;
            if (hasPending && ContinueWord.IsMatch(text))
            {
                await SpeakRemainingAsync(sessionCt);
                ToListening();
                return;
            }

            // 澄清衔接：上一轮依依反问了，这句是补充回答——拼接原指令直接进操作分支
            bool fromClarify = false;
            if (_pendingTask != null)
            {
                if (CancelWord.IsMatch(text))
                {
                    _pendingTask = null;
                    AddTurn("sys", "（好的，已取消）");
                    ToListening();
                    return;
                }
                text = _pendingTask + "，" + text;
                _pendingTask = null;
                fromClarify = true;
            }

            // ① 意图路由：操作类关键词强规则优先（LLM 对模糊口语分类不稳，如「启动一个程序」被误判）
            // 澄清回答拼出的指令必然是操作类，跳过分类直接进 task
            SetState(VoiceState.Routing);
            bool ruleHit = TaskWord.IsMatch(text) || fromClarify;
            string intent = ruleHit ? "task"
                : await WithPhase(_ai.ClassifyIntentAsync(_history, text, sessionCt));
            VoiceRecorder.Log($"[路由]「{text}」→ {intent}{(ruleHit ? "（强规则）" : "（LLM 分类）")}");
            var ct = PhaseCt();   // 路由完成后新建阶段 token

            if (intent == "chat")
            {
                // ② 对话分支：omni 直答（强约束 1~2 句）
                SetState(VoiceState.Thinking);
                string sys = ChatSys + "\n\n最近对话：\n" + HistoryDigest();

                // 检索注入（方案 §10.1：只在聊天/问答分支触发，工具指令跳过；状态仍归 Thinking 不加节点）
                // 长期记忆 + 知识库命中片段以「参考资料」注入；检索失败不阻塞对话
                if (_mem != null)
                {
                    try
                    {
                        var (memRef, kbRef) = await RetrieveContextAsync(text, ct);
                        if (memRef.Length > 0 || kbRef.Length > 0)
                        {
                            sys += "\n\n参考资料（回答时可参考，不确定就别硬用）：\n" + memRef + kbRef;
                        }
                    }
                    catch { /* 向量化服务异常时按无记忆对话 */ }
                }

                string reply = (await WithPhase(_ai.QuickTextAsync(sys, text, ct))).Trim();
                if (reply.Length == 0) reply = "我在呢，你再说一遍好吗？";
                _history.Add(("user", text));
                _history.Add(("assistant", reply));
                TrimHistory();

                // 自动提取记忆（方案 §10.4 双通道之二）：后台静默判断本轮有无值得长期记的偏好/事实
                if (_mem != null) AutoExtractMemory(text, reply);

                await SpeakTextAsync(reply, sessionCt);
                if (!_active) return;
                ToListening();
                return;
            }

            // ③ 操作分支：deepseek 结构化规划
            SetState(VoiceState.Planning);
            string planJson = (await WithPhase(_ai.PlanTaskAsync(_history, text, sessionCt))).Trim();
            ct = PhaseCt();

            // LLM 判断指令不清晰 → 反问澄清（暂存原指令，用户下一句作为补充回答）
            var pm = Regex.Match(planJson, @"\{.*\}", RegexOptions.Singleline);
            if (pm.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(pm.Value);
                    if (doc.RootElement.TryGetProperty("action", out var pa) &&
                        pa.GetString() == "clarify" &&
                        doc.RootElement.TryGetProperty("args", out var pargs) &&
                        pargs.TryGetProperty("question", out var pq))
                    {
                        string question = (pq.GetString() ?? "").Trim();
                        if (question.Length > 0)
                        {
                            await AskClarifyAsync(text, question, sessionCt);
                            if (!_active) return;
                            ToListening();
                            return;
                        }
                    }
                }
                catch { }   // JSON 解析异常按原流程继续
            }

            // 规划口播（先播报再执行，给用户打断/纠正窗口）
            string planSpeak = "";
            try
            {
                planSpeak = (await WithPhase(_ai.QuickTextAsync(PlanSpeechSys,
                    $"用户指令：{text}\n计划：{planJson}", ct))).Trim();
            }
            catch { }
            if (planSpeak.Length > 0)
            {
                await SpeakTextAsync(planSpeak, sessionCt);
                if (!_active) return;
            }

            // ④ 执行：omni Function Calling 优先，deepseek JSON 兜底
            SetState(VoiceState.Executing);
            var (ok, detail, toolName, clarify, silent) = await ExecuteTaskAsync(text, sessionCt);

            // 规则兜底判断不清晰（如「帮我打开」缺应用名）→ 反问澄清，与 LLM 澄清同路径
            if (clarify != null)
            {
                await AskClarifyAsync(text, clarify, sessionCt);
                if (!_active) return;
                ToListening();
                return;
            }

            // ⑤ 结果处理（Agent 静默原则）：
            //    silent=true → 成功不打扰，只球下小字留痕；@SPEAK: 前缀 → 直接口播工具结果内容；
            //    其余走 LLM 话术（成功确认 / 失败原因）
            _history.Add(("user", text));
            if (silent)
            {
                _history.Add(("assistant", detail));
                TrimHistory();
                AddTurn("sys", detail);
                if (!_active) return;
                ToListening();
                return;
            }

            ct = PhaseCt();
            string resultSpeak;
            if (ok && detail.StartsWith("@SPEAK:"))
            {
                resultSpeak = detail[7..];
            }
            else
            {
                try
                {
                    resultSpeak = (await WithPhase(_ai.QuickTextAsync(
                        ok ? ResultOkSys : ResultFailSys,
                        $"操作：{toolName}\n结果：{detail}", ct))).Trim();
                }
                catch
                {
                    resultSpeak = ok ? "好的，办好了。" : "没完成这个操作，麻烦你手动来一下。";
                }
            }
            _history.Add(("assistant", resultSpeak));
            TrimHistory();

            await SpeakTextAsync(resultSpeak, sessionCt);
            if (!_active) return;
            ToListening();
        }
        catch (OperationCanceledException)
        {
            // 阶段被打断/会话退出：中间结果已丢弃，listening 由 Interrupt/Exit 负责
        }
        catch (Exception ex)
        {
            if (_active)
            {
                VoiceRecorder.Log("语句处理异常：" + ex.Message);
                AddTurn("sys", "（出错了：" + ex.Message + "）");
                ToListening();
            }
        }
    }

    /// <summary>
    /// 执行操作：omni 工具轮 → 无调用则解析 deepseek 规划 JSON。
    /// 返回 clarify 非空表示指令不清晰（规则兜底判定），由调用方反问澄清而不是直接失败。
    /// silent 透传 AppToolbox 的静默标记（成功小字留痕不口播）。
    /// </summary>
    private async Task<(bool ok, string detail, string tool, string? clarify, bool silent)> ExecuteTaskAsync(string text, CancellationToken sessionCt)
    {
        string toolName = "";
        string argsJson = "{}";
        List<(string name, string args)> calls = new();   // omni 一轮的全部工具调用（组合指令可能多个）

        try
        {
            var ct = PhaseCt();
            // 环境上下文：前台是资源管理器时把当前路径告诉模型（「这个文件夹里…」的指向）
            string? env = AppToolbox.TryGetForegroundExplorerPath() is { Length: > 0 } folder
                ? $"（环境提示：用户当前文件资源管理器打开的文件夹是 {folder}。用户说「这个文件夹/这里/当前目录」时指它，find_file 的 folder 参数填它）"
                : null;
            (_, calls) = await WithPhase(_ai.OmniToolRoundAsync(_history, text, env, ct));
            if (calls.Count > 0)
            {
                toolName = calls[0].name;
                argsJson = calls[0].args;
            }
            else
            {
                // 兜底：deepseek 规划 JSON
                VoiceRecorder.Log($"[工具轮]「{text}」omni 无调用，转 deepseek 规划");
                var planJson = (await WithPhase(_ai.PlanTaskAsync(_history, text, PhaseCt()))).Trim();
                var m = Regex.Match(planJson, @"\{.*\}", RegexOptions.Singleline);
                if (m.Success)
                {
                    using var doc = JsonDocument.Parse(m.Value);
                    toolName = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                    argsJson = doc.RootElement.TryGetProperty("args", out var g)
                        ? g.GetRawText() : "{}";
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { VoiceRecorder.Log($"[工具轮]「{text}」异常：{ex.Message}"); /* 失败 → toolName 仍空，走反问澄清 */ }

        // 守卫：明确「打开/启动」类指令但模型选错工具（如模糊指令被误路由到截图翻译）→ 纠正为 open_app
        if (toolName == "screenshot_translate" && OpenAppWord.IsMatch(text) && !AppActionIntent.IsMatch(text))
        {
            toolName = "open_app";
            argsJson = System.Text.Json.JsonSerializer.Serialize(
                new { name = ExtractAppName(text) });
        }

        // 规则兜底：「打开应用」但提不出应用名（如「帮我打开」「启动一个程序」）→ 反问澄清
        if (toolName == "open_app" && ExtractArgsName(argsJson).Length == 0)
            return (false, "", "open_app", "你想打开哪个应用呢？跟我说一下应用的名字。", false);

        // 应用语义操作：缺关键参数先问清，绝不空发消息或对未知搜索结果乱点
        if (toolName == "app_action")
        {
            string action = ExtractArgsString(argsJson, "action").ToLowerInvariant();
            string app = ExtractArgsString(argsJson, "app");
            string query = ExtractArgsString(argsJson, "query");
            string contact = ExtractArgsString(argsJson, "contact");
            string message = ExtractArgsString(argsJson, "message");
            string instruction = ExtractArgsString(argsJson, "instruction");

            // 硬规则：只说搜索就只搜索；明确说播放才播放，明确说第一条/点开才打开结果
            if ((action is "search_play" or "search_and_play" or "play_result") && !PlayIntent.IsMatch(text))
            {
                action = OpenResultIntent.IsMatch(text) ? "search_open" : "search";
                argsJson = System.Text.Json.JsonSerializer.Serialize(new { action, app, query });
            }
            else if ((action is "search_open" or "search_and_open" or "open_result") && !OpenResultIntent.IsMatch(text))
            {
                action = "search";
                argsJson = System.Text.Json.JsonSerializer.Serialize(new { action, app, query });
            }
            else if (action == "search" && PlayIntent.IsMatch(text) && query.Length > 0)
            {
                action = "search_play";
                argsJson = System.Text.Json.JsonSerializer.Serialize(new { action, app, query });
            }
            else if (action == "search" && OpenResultIntent.IsMatch(text) && query.Length > 0)
            {
                action = "search_open";
                argsJson = System.Text.Json.JsonSerializer.Serialize(new { action, app, query });
            }

            if ((action is "search" or "search_open" or "search_and_open" or "open_result"
                or "search_play" or "search_and_play" or "play_result") && query.Length == 0)
                return (false, "", "app_action", "你想搜索什么内容呢？", false);
            if (action is "send_message" or "send")
            {
                if (contact.Length == 0)
                    return (false, "", "app_action", "要发给谁呢？跟我说一下联系人或群名。", false);
                if (message.Length == 0 && instruction.Length == 0)
                    return (false, "", "app_action", "要发什么内容呢？你可以直接说正文，也可以让我代你组织。", false);
            }
            if ((action is "find_contact" or "open_chat" or "search_contact") && contact.Length == 0)
                return (false, "", "app_action", "你想找哪位联系人或群聊呢？", false);
            if (app.Length == 0 && action is "" or "open" or "activate" or "bring_to_front" or "topmost" or "focus" or "login" or "search")
                return (false, "", "app_action", "你想操作哪个应用呢？", false);
        }

        // 防止模型在“发消息/搜索”等任务后顺手关掉目标应用：非明确关闭指令一律拦截。
        if (!CloseWindowIntent.IsMatch(text))
        {
            if (toolName == "window_control" &&
                ExtractArgsString(argsJson, "action").Equals("close", StringComparison.OrdinalIgnoreCase))
                return (false, "", toolName, "这条指令不是要关闭窗口，我已阻止误关闭。需要关闭时请明确说“关闭”或“关掉”。", false);
            if (toolName == "press_keys" &&
                Regex.IsMatch(ExtractArgsString(argsJson, "keys"), @"(?i)alt\s*\+?\s*f4"))
                return (false, "", toolName, "这条指令不是要关闭窗口，我已阻止 Alt+F4 误关闭。需要关闭时请明确说“关闭”或“关掉”。", false);
            if (toolName == "run_command" &&
                Regex.IsMatch(ExtractArgsString(argsJson, "command"), @"(?i)\b(taskkill(?:\.exe)?|stop-process|kill)\b"))
                return (false, "", toolName, "这条指令不是要关闭程序，我已阻止结束进程。需要关闭时请明确说“关闭”或“结束程序”。", false);
        }

        if (toolName.Length == 0)
            return (false, "", "未识别",
                "我没太听懂这个操作，可以换个说法吗？比如打开某个应用、在光标处写段文字、搜个文件。", false);

        // 新任务开始前清空上一条指令的操作记录（save_skill/save_memory/run_skill 是 meta 工具，
        // 不能清——「存成技能」依赖上一条任务刚执行的序列）
        if (toolName is not ("save_memory" or "save_skill" or "run_skill"))
            _tools.BeginTask();

        // 工具执行也挂在阶段打断源上：按住按键/连点这类长操作，用户一说话
        // Interrupt("phase") 就取消它，工具内部松键/松鼠标后静默退出
        var execCt = PhaseCt();
        var (ok, detail, silent) = await _tools.ExecuteAsync(toolName, argsJson, execCt);
        VoiceRecorder.Log($"[执行] {toolName} {argsJson} → {(ok ? "成功" : "失败")}：{detail}");

        // 组合指令：omni 一轮可能返回多个工具调用（如「双击再按空格」），首调用后顺序补执行剩余项
        if (ok && calls.Count > 1)
            (ok, detail, silent) = await ExecuteRestAsync(calls.Skip(1).ToList(), detail, execCt);

        // 复合指令续轮：模型一轮没给全时，把执行结果作为备注再问一轮，
        // 直到模型不再调用工具（全部完成）或失败，最多 3 轮。
        // 续轮不再 BeginTask（保留前面的操作记录，save_skill 沉淀完整序列），也不走守卫与澄清。
        for (int round = 2; ok && round <= 3; round++)
        {
            string ask = $"（系统备注：上一步已执行 {toolName}，结果：{detail}。用户的完整指令是「{text}」。" +
                "如果还有未完成的步骤，给出剩余步骤的全部工具调用；如果已经全部完成，不要再调用任何工具。）";
            List<(string name, string args)> next;
            try
            {
                (_, next) = await WithPhase(_ai.OmniToolRoundAsync(_history, ask, null, PhaseCt()));
            }
            catch (OperationCanceledException) { throw; }
            catch { break; }
            if (next.Count == 0) break;   // 模型认为全部完成
            toolName = next[0].name;
            argsJson = next[0].args;
            if (toolName is "save_memory" or "save_skill" or "run_skill" or "clarify" or "screenshot_translate")
                break;   // 续轮不做 meta/整屏交互类操作，避免意外行为
            (ok, detail, silent) = await _tools.ExecuteAsync(toolName, argsJson, PhaseCt());
            VoiceRecorder.Log($"[执行·续{round}] {toolName} {argsJson} → {(ok ? "成功" : "失败")}：{detail}");
            // 续轮同样可能一次给出多个剩余步骤，顺序补执行
            if (ok && next.Count > 1)
                (ok, detail, silent) = await ExecuteRestAsync(next.Skip(1).ToList(), detail, PhaseCt());
        }

        return (ok, detail, toolName, null, silent);
    }

    /// <summary>顺序执行同一轮里剩下的工具调用（组合指令如「双击再按空格」）。失败即停，detail 用「；」拼接。</summary>
    private async Task<(bool ok, string detail, bool silent)> ExecuteRestAsync(
        List<(string name, string args)> calls, string detail, CancellationToken ct)
    {
        bool ok = true; bool silent = false;
        foreach (var (name, args) in calls)
        {
            // 与续轮同一口径：不做 meta/整屏交互类操作，避免意外行为
            if (name is "save_memory" or "save_skill" or "run_skill" or "clarify" or "screenshot_translate")
                break;
            var r = await _tools.ExecuteAsync(name, args, ct);
            VoiceRecorder.Log($"[执行·组合] {name} {args} → {(r.ok ? "成功" : "失败")}：{r.detail}");
            detail = r.ok ? $"{detail}；{r.detail}" : r.detail;
            if (!r.ok) { ok = false; break; }
            silent = r.silent;
        }
        return (ok, detail, silent);
    }

    /// <summary>从工具参数 JSON 里取 name 字段（取不到或为空返回空串）。</summary>
    private static string ExtractArgsName(string argsJson) => ExtractArgsString(argsJson, "name");

    private static string ExtractArgsString(string argsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>反问澄清：播报问题并暂存原指令，用户下一句语音作为补充回答（说「算了」可取消）。</summary>
    private async Task AskClarifyAsync(string originalText, string question, CancellationToken ct)
    {
        _pendingTask = originalText;
        _history.Add(("user", originalText));
        _history.Add(("assistant", question));
        TrimHistory();
        AddTurn("sys", "（等待补充说明，说「算了」可取消）");
        await SpeakTextAsync(question, ct);
    }

    /// <summary>从口语指令提取应用名：去掉动词与虚词后的剩余部分（如「帮我启动一下微信」→「微信」）。</summary>
    private static string ExtractAppName(string text)
    {
        var name = OpenAppWord.Replace(text, "");
        // 去掉常见的修饰词，剩下的作为应用名
        name = Regex.Replace(name, "^(一下|一个|个|帮我|请|麻烦)+", "").Trim();
        name = Regex.Replace(name, "(一下|一下下|程序|应用|软件)+$", "").Trim();
        return name;
    }

    // ---------------- 播报（分句 + 待播队列） ----------------

    /// <summary>分句播报一段文案；打断时未播句子留在 _pending。</summary>
    private async Task SpeakTextAsync(string fullText, CancellationToken ct)
    {
        var sentences = SplitSentences(fullText);
        lock (_pendingLock)
        {
            _pending.Clear();
            _pending.AddRange(sentences);
        }

        SetState(VoiceState.Speaking);
        _speakStartTicks = Environment.TickCount64;
        _echoSum = 0; _echoN = 0; _echoBaseline = -1f; _overFrames = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? s;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) break;
                s = _pending[0];
            }
            bool done = await Task.Run(() => _tts.SpeakSync(s, ct));
            if (!done || ct.IsCancellationRequested) return;   // 打断：剩余句留在 _pending
            lock (_pendingLock)
            {
                if (_pending.Count > 0 && _pending[0] == s) _pending.RemoveAt(0);
            }
        }
    }

    /// <summary>"继续"：把 _pending 剩余句接着播（不进 LLM）。</summary>
    private async Task SpeakRemainingAsync(CancellationToken ct)
    {
        lock (_pendingLock) { if (_pending.Count == 0) return; }
        SetState(VoiceState.Speaking);
        _speakStartTicks = Environment.TickCount64;
        _echoSum = 0; _echoN = 0; _echoBaseline = -1f; _overFrames = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? s;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) break;
                s = _pending[0];
            }
            if (!await Task.Run(() => _tts.SpeakSync(s, ct))) return;
            lock (_pendingLock)
            {
                if (_pending.Count > 0 && _pending[0] == s) _pending.RemoveAt(0);
            }
        }
    }

    /// <summary>按 。！？；!?; 与换行切句，过短碎片并入下一句，超长在逗号处二次切。</summary>
    internal static List<string> SplitSentences(string text)
    {
        var raw = Regex.Split(text.Trim(), @"(?<=[。！？；!?;\n])");
        var list = new List<string>();
        foreach (var piece in raw)
        {
            var s = piece.Trim();
            if (s.Length == 0) continue;
            if (list.Count > 0 && list[^1].Length + s.Length <= 8)
                list[^1] += s;                              // 碎片并入前句
            else if (s.Length <= 50) list.Add(s);
            else
            {
                // 超长句在逗号处二次切分
                var parts = Regex.Split(s, @"(?<=[，,])");
                var buf = "";
                foreach (var p in parts)
                {
                    if (buf.Length + p.Length > 50 && buf.Length > 0) { list.Add(buf.Trim()); buf = p; }
                    else buf += p;
                }
                if (buf.Trim().Length > 0) list.Add(buf.Trim());
            }
        }
        return list;
    }

    // ---------------- 辅助 ----------------

    /// <summary>新建当前阶段 CTS（随会话取消）；返回其 token。</summary>
    private CancellationToken PhaseCt()
    {
        lock (_phaseLock)
        {
            _phaseCts?.Dispose();
            _phaseCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
            return _phaseCts.Token;
        }
    }

    /// <summary>在"阶段"内执行任务：完成后短暂锁定（~800ms 保护窗）再允许下一次打断判定。</summary>
    private async Task<T> WithPhase<T>(Task<T> task)
    {
        try
        {
            var r = await task;
            // 阶段收尾保护窗：结果已到手，忽略此刻的开口判定（避免"差一步全作废"）
            await Task.Delay(400);
            return r;
        }
        finally
        {
            _overFrames = 0;
        }
    }

    /// <summary>history 最近几轮的紧凑摘要（发给对话分支的 system）。</summary>
    private string HistoryDigest()
    {
        var tail = _history.Skip(Math.Max(0, _history.Count - 6))
            .Select(h => $"{(h.role == "user" ? "用户" : "依依")}：{h.content}");
        return string.Join("\n", tail);
    }

    // ---------------- 记忆与知识库（方案 §10） ----------------

    private static readonly string MemExtractSys = """
        判断下面这轮对话里有没有值得长期记住的用户偏好、习惯或事实（比如「我用 Chrome」「我叫小张」「每天九点开会」）。
        有则输出一句简洁的第三人称陈述（如「用户常用 Chrome 浏览器」）；没有则只输出：无
        不要输出解释，不要照抄整个对话。
        """;

    /// <summary>聊天分支检索：长期记忆 + 知识库各取 top-k，拼成「参考资料」文本段。</summary>
    private async Task<(string memRef, string kbRef)> RetrieveContextAsync(string question, CancellationToken ct)
    {
        string memRef = "", kbRef = "";

        var memHits = await _mem!.SearchMemoriesAsync(question, topK: 3, ct: ct);
        if (memHits.Count > 0)
            memRef = "长期记忆：\n" + string.Join("\n", memHits.Select(h => "- " + h.content)) + "\n";

        var kbHits = await _mem!.SearchKbAsync(question, topK: 3, ct: ct);
        if (kbHits.Count > 0)
        {
            kbRef = "知识库片段：\n" + string.Join("\n\n", kbHits.Select(h =>
                $"【{Path.GetFileName(h.source)}】{TruncateText(h.content, 300)}")) + "\n";
        }
        return (memRef, kbRef);
    }

    /// <summary>
    /// 自动提取记忆（每轮聊天后，方案 §10.4）：flash 小模型静默判断有无值得长期记的事实，
    /// 有才写入（一次一句话成本）。后台执行不阻塞播报，失败零动作。
    /// </summary>
    private void AutoExtractMemory(string userText, string replyText)
    {
        var ai = _ai;
        var mem = _mem;
        _ = Task.Run(async () =>
        {
            try
            {
                var fact = (await ai.QuickTextAsync(MemExtractSys,
                    $"用户：{TruncateText(userText, 400)}\n依依：{TruncateText(replyText, 400)}")).Trim();
                if (fact.Length == 0 || fact.Contains("无") && fact.Length <= 4) return;
                await mem!.AddMemoryAsync(fact);
            }
            catch { /* 记忆提取失败不影响对话 */ }
        });
    }

    private static string TruncateText(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private void TrimHistory()
    {
        while (_history.Count > 20) _history.RemoveAt(0);
    }

    public void Dispose()
    {
        RequestExit();
        _sessionCts.Dispose();
    }
}
