using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ScreenTranslator;

/// <summary>
/// OpenAI 兼容 API 流式对话客户端（SSE）。
/// </summary>
public sealed class AiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AppConfig _cfg;

    public AiService(AppConfig cfg) => _cfg = cfg;

    /// <summary>后续对话讲解者提示词（所有追问统一注入，不存入会话消息列表）。</summary>
    public static string ExplainerPrompt =>
        """
        你是语言学习讲解助手。会话中已有用户选中文本的原文和译文，你负责围绕它们答疑：词义用法、搭配、语域、近义词辨析、语法结构、例句、文化背景。

        规则：
        1. 默认不复述译文，仅当用户明确要求"再翻译/换个译法"时才翻译。
        2. 用户只发一个词时，给一段自然用法讲解，不列词典卡片（问发音/音标除外）。
        3. 引用原文只摘相关片段，不复读整段。
        4. 用中文回答，紧凑 Markdown，直接讲要点，无客套话。
        5. 超出该文本范围的问题正常答，但优先落回当前语境。
        """;

    /// <summary>截图首轮翻译提示词（qwen3.8-omni-flash 整理+翻译 OCR 文本，注入为 system）。</summary>
    public static string ScreenshotFirstPrompt =>
        """
        你是屏幕 OCR 文本整理与翻译助手。输入可能含换行错位、乱码、界面碎片（按钮、菜单、水印）。

        规则：
        1. 剔除乱码和界面碎片，修复换行，保留段落、列表、标题结构。不复述处理过程。
        2. 外文译为中文；原文为中文则原样输出，不改写、不总结、不润色，仅修明显 OCR 错字。混排时中文保留、外文译中文。
        3. 无有效内容时只输出：未识别到有效文本
        4. 内容明显被截断时，末尾另起一行加：ⓘ 内容可能不完整，可重新截图或补充上下文

        只输出紧凑 Markdown 结果，不解释，不加客套话。
        """;

    /// <summary>会话标题生成提示词（截图翻译首轮输出后，为会话起一个可在历史中找到的标题）。</summary>
    public static string TitlePrompt =>
        """
        你是会话标题生成器。根据首轮对话的用户问题和助手回答，生成一个标题。

        规则：
        1. 概括对话核心主题，用名词性短语，不照抄原句，不编造对话中没有的信息。
        2. 中文不超过 12 字，英文不超过 6 个词，跟随对话主语言。
        3. 只输出标题一行，不加引号、句号、书名号，不加"标题："前缀。
        4. 不用"关于……的讨论""……的问题"这类空泛说法。
        5. 无实质内容（纯问候、乱码、空白）时输出：新对话
        """;

    /// <summary>流式对话。delta 回调在工作线程上触发。model 为空时使用 ChatModel。</summary>
    public async Task StreamChatAsync(IReadOnlyList<(string role, string content)> messages,
        Action<string> onDelta, string? model = null, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            onDelta("**尚未配置 API Key**\n\n请设置环境变量 `QIANWEN_API_KEY`，或编辑程序目录下的 `config.json` 填入 ApiKey 后重启。");
            return;
        }

        var actualModel = string.IsNullOrWhiteSpace(model) ? _cfg.ChatModel : model;
        var isMt = actualModel.Contains("mt", StringComparison.OrdinalIgnoreCase);
        var bodyMessages = SanitizeMessagesForModel(messages, actualModel);

        // qwen-mt 系列必须带 translation_options 指定目标语言，否则行为未定义
        // （实测不带时输入单词会返回无关的聊天内容）。app 内机翻固定译为中文。
        object body = isMt
            ? new
            {
                model = actualModel,
                stream = true,
                translation_options = new { source_lang = "auto", target_lang = "zh" },
                messages = bodyMessages.Select(m => new { role = m.role, content = m.content })
            }
            : new
            {
                model = actualModel,
                stream = true,
                messages = bodyMessages.Select(m => new { role = m.role, content = m.content })
            };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // 带上服务端返回的原始错误信息，方便定位（如角色限制、配额、鉴权问题）
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（端点 {_cfg.BaseUrl}/chat/completions）");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("delta");
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var piece = c.GetString();
                    if (!string.IsNullOrEmpty(piece)) onDelta(piece);
                }
            }
            catch
            {
                // 跳过无法解析的心跳/注释行
            }
        }
    }

    /// <summary>
    /// 截图文字识别（多模态 OCR，非流式）：图片字节 → base64 data URL 发给 OcrModel，
    /// 返回 choices[0].message.content。mime 默认 image/png，可传 image/jpeg 以减小上传体积。
    /// </summary>
    public async Task<string> OcrAsync(byte[] imageBytes, string mime = "image/png", CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("尚未配置 API Key，请先在设置中填写。");

        var dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(imageBytes);
        var body = new
        {
            model = _cfg.OcrModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Read all the text in this image." },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = dataUrl }
                        }
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（模型 {_cfg.OcrModel}）");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>
    /// 屏幕定位 OCR（方案 §11.3，screen_click 用）：自定义 prompt 的多模态调用，
    /// 要求模型返回目标文字的包围盒坐标（qwen-vl grounding 约定：0~1000 归一化 [[x1,y1,x2,y2]]）。
    /// 解析失败返回空串，由调用方走「找不到目标」口播兜底。
    /// </summary>
    public async Task<string> GroundAsync(byte[] imageBytes, string prompt, CancellationToken ct = default)
    {
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
        var body = new
        {
            model = _cfg.GroundModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResolveApiKey());

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（模型 {_cfg.GroundModel}）");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    /// <summary>
    /// 会话标题生成（截图翻译首轮完成后调用）：非流式，TitleModel + TitlePrompt，
    /// 入参为首轮用户问题与助手回答。失败时抛异常，由调用方兜底保留原会话词。
    /// </summary>
    public async Task<string> GenerateTitleAsync(string userText, string assistantText, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("尚未配置 API Key，请先在设置中填写。");

        var body = new
        {
            model = _cfg.TitleModel,
            messages = new object[]
            {
                new { role = "system", content = TitlePrompt },
                new
                {
                    role = "user",
                    content = $"用户问题：\n{Truncate(userText, 800)}\n\n助手回答：\n{Truncate(assistantText, 800)}"
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（模型 {_cfg.TitleModel}）");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>文件重命名起名提示词（rename_selected 用，非流式小调用）。</summary>
    public static string FileNamePrompt =>
        """
        你是文件命名助手。根据原文件名和内容摘要，起一个简短、能概括内容的新文件名。
        规则：
        1. 中文文件用中文命名，外文文件用对应语言；不超过 12 个字。
        2. 只输出文件名本身：不含扩展名、引号、书名号，结尾不带标点，不要任何解释。
        3. 新名字应比原名更表意；原名已很贴切时可微调，不必强改。
        4. 内容摘要为空时，基于原文件名去噪后起名。
        5. 名字里不能出现 \ / : * ? " < > | 和换行。
        """;

    /// <summary>
    /// 文件重命名起名（rename_selected）：原文件名 + 内容摘要 → 简短新文件名（不含扩展名）。
    /// 走 TitleModel 小模型，非流式。失败抛异常由调用方兜底。
    /// </summary>
    public async Task<string> GenerateFileNameAsync(string originName, string brief, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("尚未配置 API Key，请先在设置中填写。");

        var body = new
        {
            model = _cfg.TitleModel,
            messages = new object[]
            {
                new { role = "system", content = FileNamePrompt },
                new
                {
                    role = "user",
                    content = $"原文件名：{Truncate(originName, 100)}\n\n内容摘要：\n{Truncate(brief, 1200)}"
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（模型 {_cfg.TitleModel}）");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    // ==================== 语音对话链路（编排各阶段模型） ====================

    /// <summary>语音识别（整段 wav → 文本）：paraformer 系列走 DashScope 原生 WebSocket 协议；
    /// 其余模型走 OpenAI 兼容接口，音频以 data URL 内联（裸调验证：data 前缀必须带，裸 base64 报 400）。</summary>
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct = default)
    {
        // DashScope 原生 WS 协议模型：paraformer-realtime-v2、fun-asr-flash-8k-realtime 等
        if (_cfg.AsrModel.StartsWith("paraformer", StringComparison.OrdinalIgnoreCase) ||
            _cfg.AsrModel.StartsWith("fun-asr", StringComparison.OrdinalIgnoreCase))
            return await TranscribeParaformerAsync(wav, ct);

        var body = new
        {
            model = _cfg.AsrModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_audio", input_audio = new
                        {
                            data = "data:audio/wav;base64," + Convert.ToBase64String(wav),
                            format = "wav"
                        } }
                    }
                }
            }
        };
        var msg = await PostMessageAsync(body, _cfg.AsrModel, ct);
        return msg.GetProperty("content").GetString() ?? "";
    }

    // ==================== Paraformer 实时识别（DashScope 原生 WebSocket 协议） ====================

    /// <summary>
    /// paraformer-realtime-v2 等 DashScope 原生模型不走 OpenAI 兼容接口，协议为：
    /// run-task(streaming=duplex) → task-started → 二进制 PCM 分片 → finish-task →
    /// result-generated（sentence.text 为当前句累积文本，sentence_end=true 时该句定稿）→ task-finished。
    /// 裸调验证（2026-11-15）：wss://{BaseUrl 域名}/api-ws/v1/inference 经代理可用；
    /// 注意 streaming 必须写 "duplex"——写 "out" 时音频被服务端静默丢弃（task-finished 但零句子）。
    /// </summary>
    private async Task<string> TranscribeParaformerAsync(byte[] wav, CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("尚未配置 API Key，无法使用语音识别。");

        // BaseUrl 形如 https://host/compatible-mode/v1 → 推导 DashScope 原生 WS 端点
        var host = new Uri(_cfg.BaseUrl).Host;
        var wsUrl = $"wss://{host}/api-ws/v1/inference";

        // 解析 wav：真实采样率 + data 块裸 PCM（录音为 16k/16bit/mono，仍按头解析以防变化）
        var (pcm, sampleRate) = ExtractWavPcm(wav);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        var taskId = Guid.NewGuid().ToString();
        async Task SendTextAsync(string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, ct);
        }

        // language_hints 仅 paraformer-realtime-v2 支持，fun-asr 系列不能传（官方文档明确）
        var isParaformer = _cfg.AsrModel.StartsWith("paraformer", StringComparison.OrdinalIgnoreCase);
        var parameters = isParaformer
            ? (object)new { format = "pcm", sample_rate = sampleRate, language_hints = new[] { "zh", "en" } }
            : new { format = "pcm", sample_rate = sampleRate };

        await SendTextAsync(JsonSerializer.Serialize(new
        {
            header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
            payload = new
            {
                model = _cfg.AsrModel,
                task_group = "audio",
                task = "asr",
                function = "recognition",
                input = new { },
                parameters
            }
        }));

        var buf = new byte[64 * 1024];
        async Task<(string msg, bool closed)> RecvAsync(CancellationToken token)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (res.MessageType == WebSocketMessageType.Close) return ("", true);
                ms.Write(buf, 0, res.Count);
                if (res.EndOfMessage) return (Encoding.UTF8.GetString(ms.ToArray()), false);
            }
        }
        static string Ev(string msg)
        {
            try
            {
                using var d = JsonDocument.Parse(msg);
                return d.RootElement.GetProperty("header").GetProperty("event").GetString() ?? "";
            }
            catch { return ""; }
        }
        static string ErrBrief(string msg)
        {
            try
            {
                using var d = JsonDocument.Parse(msg);
                var h = d.RootElement.GetProperty("header");
                return h.TryGetProperty("error_message", out var m) ? (m.GetString() ?? msg) : msg;
            }
            catch { return msg; }
        }

        // 等 task-started（10s 超时），期间可能直接 task-failed
        try
        {
            while (true)
            {
                var (msg, closed) = await RecvAsync(ct);
                if (closed) throw new Exception("语音识别连接被服务端关闭（未收到 task-started）。");
                var ev = Ev(msg);
                if (ev == "task-started") break;
                if (ev == "task-failed") throw new Exception("语音识别任务启动失败：" + ErrBrief(msg));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Exception("语音识别连接超时（10 秒内未收到 task-started）。");
        }

        // 发送音频分片 + finish-task（与收结果并发；ClientWebSocket 允许一收一发并行）
        var finishJson = JsonSerializer.Serialize(new
        {
            header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
            payload = new { input = new { } }
        });
        var sendTask = Task.Run(async () =>
        {
            const int chunk = 3200;   // 100ms @16k/16bit
            for (int off = 0; off < pcm.Length; off += chunk)
            {
                var len = Math.Min(chunk, pcm.Length - off);
                await ws.SendAsync(new ArraySegment<byte>(pcm, off, len), WebSocketMessageType.Binary, true, ct);
            }
            await SendTextAsync(finishJson);
        }, ct);

        var final = new StringBuilder();   // 已定稿句子
        var current = new StringBuilder(); // 当前句累积中间文本（finish 后可能仍有尾句未定稿）
        try
        {
            while (true)
            {
                var (msg, closed) = await RecvAsync(ct);
                if (closed) break;
                var ev = Ev(msg);
                if (ev == "task-finished") break;
                if (ev == "task-failed") throw new Exception("语音识别失败：" + ErrBrief(msg));
                if (ev != "result-generated") continue;
                using var d = JsonDocument.Parse(msg);
                var s = d.RootElement.GetProperty("payload").GetProperty("output").GetProperty("sentence");
                var text = s.GetProperty("text").GetString() ?? "";
                bool ended = s.TryGetProperty("sentence_end", out var endEl) && endEl.GetBoolean();
                if (ended) { final.Append(text); current.Clear(); }   // 该句定稿，累积
                else current.Clear().Append(text);
            }
        }
        finally
        {
            try { await sendTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        }

        var result = (final.Length > 0 ? final : current).ToString();
        if (result.Length == 0) throw new Exception("语音识别未返回文本。");
        return result;
    }

    /// <summary>解析 wav 头：提取 data 块裸 PCM 与真实采样率（非 RIFF 时按 16k 裸 PCM 兜底）。</summary>
    private static (byte[] pcm, int sampleRate) ExtractWavPcm(byte[] wav)
    {
        if (wav.Length > 44 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF")
        {
            int dataOff = 44, dataLen = wav.Length - 44;
            for (int i = 12; i < wav.Length - 8; i++)
            {
                if (Encoding.ASCII.GetString(wav, i, 4) == "data")
                {
                    dataOff = i + 8;
                    dataLen = BitConverter.ToInt32(wav, i + 4);
                    if (dataOff + dataLen > wav.Length) dataLen = wav.Length - dataOff;
                    break;
                }
            }
            var pcm = new byte[dataLen];
            Buffer.BlockCopy(wav, dataOff, pcm, 0, dataLen);
            return (pcm, BitConverter.ToInt32(wav, 24));
        }
        return (wav, 16000);
    }

    /// <summary>意图路由（纯分类不播报）：输出 "chat"（对话）或 "task"（操作），携带 history。</summary>
    public async Task<string> ClassifyIntentAsync(
        IReadOnlyList<(string role, string content)> history, string text, CancellationToken ct = default)
    {
        const string sys = """
            你是意图分类器。判断用户这句话是「闲聊/提问」还是「要让电脑执行操作」。
            输出 task 的场景（只要涉及让电脑做事就算，无论说法多口语化）：
            - 打开/关闭应用、执行终端命令、定时关机、定闹钟
            - 截图翻译、识别屏幕文字
            - 在光标处输入/生成/粘贴文字；对选中文字或某一行做润色/改写/翻译/替换
            - 网页搜索、打开网址、按名字找文件、点击屏幕上的按钮、让我操作浏览器网页
            - 控制窗口大小、切换窗口、调音量、静音、读剪贴板并处理其中内容
            - 记住某个信息、把刚才的操作存成技能、执行已存的技能
            输出 chat 的场景：闲聊、问候、知识问答、征求意见等不需要动电脑的。
            拿不准是不是要执行操作时，按 task 处理。只输出 chat 或 task 两个词之一，不输出其他内容。
            """;
        var msgs = new List<(string, string)> { ("system", sys) };
        msgs.AddRange(Tail(history, 6));
        msgs.Add(("user", text));

        var body = new
        {
            model = _cfg.IntentModel,
            messages = msgs.Select(m => new { role = m.Item1, content = m.Item2 })
        };
        var msg = await PostMessageAsync(body, _cfg.IntentModel, ct);
        var raw = (msg.GetProperty("content").GetString() ?? "").Trim().ToLowerInvariant();
        return raw.Contains("task") ? "task" : "chat";   // 容错：只认两个词
    }

    /// <summary>操作场景结构化规划（deepseek）：输出 JSON {"action":"open_app","args":{"name":"微信"}} 等。</summary>
    public async Task<string> PlanTaskAsync(
        IReadOnlyList<(string role, string content)> history, string text, CancellationToken ct = default)
    {
        const string sys = """
            你是电脑操作任务规划器。根据用户指令输出一个 JSON 对象，格式二十二选一：
            打开应用 {"action":"open_app","args":{"name":"应用名"}}；
            截图翻译 {"action":"screenshot_translate","args":{}}；
            在光标处输入或生成文字 {"action":"insert_text","args":{"text":"用户原话里说好的内容（没说则为空串）","instruction":"要生成什么内容的描述（用户直接给了内容则为空串）"}}；
            修改文字（润色/改写/翻译/替换） {"action":"edit_text","args":{"instruction":"改写要求","file":"用户提到的文件路径（没有则为空串）","line":行号数字（没提行号则为 0）}}；
            执行终端命令 {"action":"run_command","args":{"command":"完整的命令"}}；
            网页搜索 {"action":"web_search","args":{"query":"搜索词"}}；
            打开网址 {"action":"open_url","args":{"url":"网址"}}；
            窗口操作 {"action":"window_control","args":{"action":"close|minimize|maximize|switch|minimize_all","target":"窗口标题关键词（minimize_all 时为空串）"}}；
            媒体控制（播放/暂停会自动适配视频或音乐场景；set_volume/set_brightness 把音量/屏幕亮度调到指定数值） {"action":"media_control","args":{"action":"play|pause|next|previous|stop|volume_up|volume_down|mute|set_volume|set_brightness","value":数值(仅 set_volume/set_brightness 需要，0-100 的整数)}}；
            系统设置开关（WiFi/蓝牙/飞行模式/省电模式；飞行模式=同时开关 WiFi 和蓝牙） {"action":"system_control","args":{"action":"wifi_on|wifi_off|bluetooth_on|bluetooth_off|airplane_on|airplane_off|saver_on|saver_off"}}；
            模拟按键（按回车确认、F5 刷新、F11 全屏播放/退出全屏、F2 重命名、Alt+F4 关闭最前面的窗口等键盘快捷键操作；「按住某键多少秒」传 duration 秒，说了按住但没说多久传 duration=3；「连按某键几次」传 count 次，说了连按但没说次数传 count=5（可选 interval 间隔毫秒，默认 150）；完全没提按住/连按就只单按一次，不要传 duration/count；按住/连按期间用户说话会自动打断；语音识别的近音词直接联想成正确键名，如「空白键/空百键」=空格 Space，不反问用户） {"action":"press_keys","args":{"keys":"键名用+连接，如 Enter、F5、F11、Alt+F4、Ctrl+Shift+Esc","duration":按住秒数,"count":连按次数}}；
            重命名资源管理器里选中的文件/文件夹（用户说重命名选中的文件、给它起个名，直接调用不反问） {"action":"rename_selected","args":{"instruction":"命名要求（如改成英文、加日期，没有则为空串）"}}；
            鼠标操作（单击/双击/右键；「按住左/右键几秒」传 action=hold+button+duration，说了按住但没说多久传 duration=3，说话即打断自动松开；「连点几次」传 count，说了连点但没说次数传 count=5，「持续点击几秒」传 duration，间隔拟人随机浮动默认 150ms；完全没提连点/按住就只点一下，不要传 duration/count） {"action":"mouse_action","args":{"action":"click|double_click|right_click|hold","position":"current|center（没说位置则为 current）","button":"left|right（hold 用）","duration":按住或持续秒数,"count":连点次数}}；
            读剪贴板 {"action":"read_clipboard","args":{"instruction":"对剪贴板内容的要求（如翻译成英文、润色，没有要求则为空串）"}}；
            记住信息（用户说记住/记一下） {"action":"save_memory","args":{"content":"要长期记住的内容（一句简洁的陈述）"}}；
            存成技能（用户说把这个存成技能并给了名字） {"action":"save_skill","args":{"name":"技能名"}}；
            执行技能（用户说执行某个已存技能） {"action":"run_skill","args":{"name":"技能名"}}；
            按文件名找文件 {"action":"find_file","args":{"query":"文件名关键词（含扩展名）","folder":"限定搜索的文件夹路径（用户说当前文件夹/这里时填资源管理器路径，没有则为空串）"}}；
            打开刚才搜索结果里的某一条 {"action":"open_found","args":{"index":第几条(从1开始)}}；
            在浏览器里完成任务（打开网页操作/在网站内搜索/填表单） {"action":"browser_control","args":{"url":"起始网址（没说则为空串）","instruction":"要在浏览器里完成的任务描述"}}；
            屏幕单步点击/输入（点确认按钮、点输入框并输入等简单操作） {"action":"screen_click","args":{"target":"目标按钮或输入框上显示的文字","value":"要在输入框里输入的文字（纯点击则为空串）"}}；
            看屏幕回答问题（用户问屏幕/当前网页是什么、让看一眼屏幕时） {"action":"look_screen","args":{"question":"想了解屏幕上的什么"}}；
            指令信息不足、无法确定具体操作时（比如没说清要打开哪个应用），输出 {"action":"clarify","args":{"question":"一句简短的反问，帮用户把指令补充完整"}}。
            只输出 JSON，不要解释。
            """;
        var body = new
        {
            model = _cfg.PlanModel,
            messages = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = text }
            }
        };
        var msg = await PostMessageAsync(body, _cfg.PlanModel, ct);
        return msg.GetProperty("content").GetString() ?? "";
    }

    /// <summary>
    /// 执行轮（omni + Function Calling）：给定 history 与用户指令，返回模型决定调用的工具列表
    /// （裸调验证：finish_reason=tool_calls 正常）。无工具调用时返回的 content 是模型直答文本。
    /// envContext：环境上下文（如资源管理器当前路径），作为 system 注入帮助模型理解「这里/当前文件夹」。
    /// </summary>
    public async Task<(string content, List<(string name, string args)> toolCalls)> OmniToolRoundAsync(
        IReadOnlyList<(string role, string content)> history, string text,
        string? envContext = null, CancellationToken ct = default)
    {
        var tools = new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "open_app",
                    description = "打开电脑上的应用程序",
                    parameters = new
                    {
                        type = "object",
                        properties = new { name = new { type = "string", description = "应用名，如 微信、记事本" } },
                        required = new[] { "name" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "screenshot_translate",
                    description = "截取屏幕区域并翻译其中的文字",
                    parameters = new { type = "object", properties = new { } }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "insert_text",
                    description = "在当前光标处输入文字，或按要求生成一段内容并贴到光标处",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            text = new { type = "string", description = "用户明确说好的原话内容（没有则为空）" },
                            instruction = new { type = "string", description = "要生成什么内容的描述（用户直接给了内容则为空）" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "edit_text",
                    description = "修改文字：改写当前选中的文字或光标所在行（润色、翻译、精简、改代码等）；用户说了第几行时可指定 file 和 line 直接改文件",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            instruction = new { type = "string", description = "改写要求，如 润色、翻译成英文、精简" },
                            file = new { type = "string", description = "目标文件路径（用户没提则为空）" },
                            line = new { type = "integer", description = "第几行（用户没提行号则为 0，走当前选区/光标行）" }
                        },
                        required = new[] { "instruction" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "run_command",
                    description = "在后台静默执行一条终端命令（PowerShell），如定时关机、计划任务、ping 等",
                    parameters = new
                    {
                        type = "object",
                        properties = new { command = new { type = "string", description = "完整的命令" } },
                        required = new[] { "command" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "web_search",
                    description = "用默认浏览器搜索关键词",
                    parameters = new
                    {
                        type = "object",
                        properties = new { query = new { type = "string", description = "搜索词" } },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "open_url",
                    description = "用默认浏览器打开一个网址",
                    parameters = new
                    {
                        type = "object",
                        properties = new { url = new { type = "string", description = "网址" } },
                        required = new[] { "url" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "window_control",
                    description = "窗口操作：关闭、最小化、最大化、切换到某窗口，或最小化所有窗口",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "close|minimize|maximize|switch|minimize_all" },
                            target = new { type = "string", description = "窗口标题关键词（minimize_all 时为空）" }
                        },
                        required = new[] { "action" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "media_control",
                    description = "媒体控制：播放/暂停（自动识别视频或音乐场景选择键位）、切歌、停止、调大/调小音量、静音、把音量/屏幕亮度调到指定数值（set_volume/set_brightness + value）",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "play|pause|next|previous|stop|volume_up|volume_down|mute|set_volume|set_brightness" },
                            value = new { type = "integer", description = "0~100 的整数，仅 set_volume（音量）/set_brightness（亮度）时需要，如用户说「音量调到 30」" }
                        },
                        required = new[] { "action" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "system_control",
                    description = "电脑系统设置开关：开/关 WiFi、开/关蓝牙、开/关飞行模式（同时关/开 WiFi 和蓝牙）、开/关省电模式（切换节能/平衡电源计划）",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "wifi_on|wifi_off|bluetooth_on|bluetooth_off|airplane_on|airplane_off|saver_on|saver_off" }
                        },
                        required = new[] { "action" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "press_keys",
                    description = "模拟键盘按键/快捷键并真实按下：确认回车 Enter、刷新 F5、关闭最前面的窗口 Alt+F4、显示桌面 Win+D、全屏/退出全屏 F11（视频全屏播放、网页全屏）、重命名 F2、任务管理器 Ctrl+Shift+Esc、复制 Ctrl+C 等。" +
                        "按住：用户说「按住右方向键 30 秒」「长按空格」传 duration（秒）；说了按住但没说多久（如「按住一会儿」）传 duration=3；没提按住不要传 duration。连按：「连按几次」传 count；说了连按但没说次数（如「连按几下」）传 count=5；没提连按不要传 count。按住/连按期间用户开口说话会自动打断退出。" +
                        "语音识别常把键名听成近音词（如「空白键/空百键/斯白键」=空格 Space，「回车/确认键」=Enter，F 键可能被写成「f11/付11/F 11」），联想成最可能的键，直接传标准键名调用，不要反问用户。一次只按一个主键",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            keys = new { type = "string", description = "键名用 + 连接，如 Enter、F5、F11、Alt+F4、Ctrl+C、Win+D。支持字母、数字、F1-F12、Enter/Esc/Tab/Space/Backspace/Delete/Home/End/PageUp/PageDown/方向键（up/down/left/right）/PrintScreen；中文键名（空格、回车、上/下/左/右）会自动归一" },
                            duration = new { type = "number", description = "按住主键的秒数（如 30）。说了按住但没说多久传 3；没提按住不要传这个字段" },
                            count = new { type = "number", description = "连续按键次数（如 10）。说了连按但没说次数传 5；没提连按不要传这个字段" },
                            interval = new { type = "number", description = "连续按键的基准间隔毫秒（没说默认 150，会随机浮动）" }
                        },
                        required = new[] { "keys" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "rename_selected",
                    description = "直接重命名用户在资源管理器里选中的文件/文件夹：读取内容后自动起一个简短贴切的新名字。用户说「重命名选中的文件/给它起个名/这个文档叫什么名」时直接调用，不反问",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            instruction = new { type = "string", description = "用户的命名要求（如「改成英文」「加上日期」，没有则为空）" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "mouse_action",
                    description = "鼠标操作：单击、双击、右键、按住指定秒数、连续点击。按住：用户说「按住左键 5 秒」传 action=hold+button+duration，「按住一会儿」没说多久传 duration=3，说话即打断自动松开；连点：用户说「连点 10 次」传 count，「随便点几下」没说次数传 count=5，「持续点击 5 秒」传 duration（间隔拟人随机浮动默认 150ms，可传 interval）；用户没提连点/按住时不要传 duration/count，只点一下",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "click|double_click|right_click|hold（按住）" },
                            position = new { type = "string", description = "current（鼠标当前位置，默认）|center（屏幕正中心）" },
                            button = new { type = "string", description = "hold 用：left（默认）|right" },
                            duration = new { type = "number", description = "秒：hold=按住秒数（说了按住但没说多久传 3）；连点没传 count 时=持续点击秒数" },
                            count = new { type = "number", description = "连点次数（说了连点但没说次数传 5）；没提连点不要传这个字段" },
                            interval = new { type = "number", description = "连点基准间隔毫秒（没说默认 150，会随机浮动）" }
                        },
                        required = new[] { "action" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "read_clipboard",
                    description = "读取剪贴板文字内容，可直接口播或按要求（翻译/润色等）处理后口播或贴回光标处",
                    parameters = new
                    {
                        type = "object",
                        properties = new { instruction = new { type = "string", description = "对剪贴板内容的要求（没有要求则为空）" } }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "save_memory",
                    description = "把用户的偏好、习惯或事实存入长期记忆（用户说「记住/记一下」时使用）",
                    parameters = new
                    {
                        type = "object",
                        properties = new { content = new { type = "string", description = "要长期记住的内容，一句简洁的陈述" } },
                        required = new[] { "content" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "save_skill",
                    description = "把刚执行过的操作序列存成一个可回放的技能（用户说「把这个存成技能，叫 XX」时使用）",
                    parameters = new
                    {
                        type = "object",
                        properties = new { name = new { type = "string", description = "技能名" } },
                        required = new[] { "name" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "run_skill",
                    description = "按序回放一个已保存的技能（用户说「执行 XX」且 XX 是技能名时使用）",
                    parameters = new
                    {
                        type = "object",
                        properties = new { name = new { type = "string", description = "技能名" } },
                        required = new[] { "name" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "find_file",
                    description = "按文件名关键词搜索本机文件（优先 Everything 全盘秒搜，未装则搜桌面/文档/下载）。用户说「这个文件夹里的…」时把环境上下文里的资源管理器路径填进 folder",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "文件名关键词，建议含扩展名" },
                            folder = new { type = "string", description = "限定搜索的文件夹路径（用户说当前文件夹/这里且环境上下文给了路径时填写，否则为空）" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "open_found",
                    description = "打开刚才 find_file 搜索结果里的某一条（用户说「打开第一个/第二个」时使用）",
                    parameters = new
                    {
                        type = "object",
                        properties = new { index = new { type = "integer", description = "第几条（从 1 开始）" } },
                        required = new[] { "index" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "browser_control",
                    description = "在受控浏览器窗口里完成一个网页任务（页面操作、站内搜索、填表单）。不用于普通的「打开网址」（那是 open_url）",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            url = new { type = "string", description = "起始网址（用户没说则为空）" },
                            instruction = new { type = "string", description = "要在浏览器里完成的任务描述" }
                        },
                        required = new[] { "instruction" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "screen_click",
                    description = "在屏幕上定位某段文字并单击它；或在定位到的输入框里输入文字。也用于应用界面/登录界面上的按钮（如微信登录界面的「进入微信」、各种「登录/确认/同意」按钮）。只适合单步简单操作（点确认、点输入框并输入等）",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            target = new { type = "string", description = "目标按钮或输入框上显示的文字" },
                            value = new { type = "string", description = "要在输入框里输入的文字（纯点击则为空）" }
                        },
                        required = new[] { "target" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "look_screen",
                    description = "截取当前屏幕并回答关于屏幕内容的问题（如「这个网页是干什么的」「屏幕上是什么」）。只用于看屏幕回答问题，翻译屏幕文字用 screenshot_translate",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            question = new { type = "string", description = "想了解屏幕上的什么，一句话" }
                        },
                        required = new[] { "question" }
                    }
                }
            }
        };

        var msgs = new List<(string, string)>();
        // 组合指令提示：一句话多个步骤时让模型按顺序给出全部 tool_calls（执行器依序执行）。
        // 高频坑纠正：同一应用重复 open_app——打开后的界面操作应该用 screen_click
        msgs.Add(("system",
            "用户的一句话可能包含多个连续操作步骤（如「双击一下再按空格」「打开微信并登录」）。" +
            "需要多个工具调用时，在 tool_calls 里按执行顺序全部给出，不要只给第一个。" +
            "同一个应用最多调用一次 open_app；应用打开后的界面内操作（登录、点按钮、输入）" +
            "不要再用 open_app，改用 screen_click（如微信登录界面点「进入微信」）、press_keys 等完成。"));
        if (!string.IsNullOrWhiteSpace(envContext)) msgs.Add(("system", envContext));
        msgs.AddRange(Tail(history, 8));
        msgs.Add(("user", text));

        var body = new
        {
            model = _cfg.ChatModel,
            messages = msgs.Select(m => new { role = m.Item1, content = m.Item2 }),
            tools
        };
        var msg = await PostMessageAsync(body, _cfg.ChatModel, ct);

        var calls = new List<(string, string)>();
        if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in tc.EnumerateArray())
            {
                try
                {
                    var fn = c.GetProperty("function");
                    calls.Add((fn.GetProperty("name").GetString() ?? "",
                               fn.GetProperty("arguments").GetString() ?? "{}"));
                }
                catch { }
            }
        }
        var content = msg.TryGetProperty("content", out var cc) && cc.ValueKind == JsonValueKind.String
            ? cc.GetString() ?? "" : "";
        return (content, calls);
    }

    /// <summary>短文案生成（规划口播 / 结果口播）：非流式，由 system prompt 强约束 1~2 句。</summary>
    public async Task<string> QuickTextAsync(string system, string user, CancellationToken ct = default)
    {
        var body = new
        {
            model = _cfg.ChatModel,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };
        var msg = await PostMessageAsync(body, _cfg.ChatModel, ct);
        return msg.GetProperty("content").GetString() ?? "";
    }

    /// <summary>
    /// 文本向量化（/embeddings 接口，方案 §10.2）：qwen3.7-text-embedding-flash。
    /// 入参一组文本，返回等长 float[] 列表（次序与输入一致）。
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("尚未配置 API Key，请先在设置中填写。");

        var body = new
        {
            model = _cfg.EmbeddingModel,
            input = inputs
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/embeddings");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（模型 {_cfg.EmbeddingModel}）");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // data[] 按 index 排序保证与输入次序一致
        var items = doc.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0)
            .Select(d => d.GetProperty("embedding").EnumerateArray()
                .Select(v => (float)v.GetDouble()).ToArray())
            .ToList();
        return items;
    }

    /// <summary>非流式 chat/completions 公共请求：返回 choices[0].message 节点。</summary>
    private async Task<JsonElement> PostMessageAsync(object body, string modelTag, CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("尚未配置 API Key，请先在设置中填写。");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            _cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new Exception(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}：{ExtractErrorBrief(errBody)}（模型 {modelTag}）");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").Clone();
    }

    /// <summary>取 history 末尾 n 条（语音上下文只发最近几轮）。</summary>
    private static IEnumerable<(string role, string content)> Tail(
        IReadOnlyList<(string role, string content)> history, int n)
    {
        for (int i = Math.Max(0, history.Count - n); i < history.Count; i++)
            yield return history[i];
    }


    /// <summary>从服务端错误响应中提取简短错误描述（error.message 或截 300 字符）。</summary>
    private static string ExtractErrorBrief(string errBody)
    {
        try
        {
            using var edoc = JsonDocument.Parse(errBody);
            var msg = edoc.RootElement.GetProperty("error").GetProperty("message").GetString();
            if (!string.IsNullOrEmpty(msg)) return msg;
        }
        catch { }
        return errBody.Length > 300 ? errBody[..300] : errBody;
    }

    /// <summary>
    /// qwen-mt 系列翻译模型只接受 user/assistant 角色。
    /// 发送到 MT 模型前，把 system 指令合并进第一条 user 消息。
    /// </summary>
    private IReadOnlyList<(string role, string content)> SanitizeMessagesForModel(
        IReadOnlyList<(string role, string content)> messages, string actualModel)
    {
        if (!actualModel.Contains("mt", StringComparison.OrdinalIgnoreCase))
            return messages;

        var result = new List<(string, string)>();
        string? systemPrompt = null;
        foreach (var m in messages)
        {
            if (m.role == "system") { systemPrompt = m.content; continue; }
            if (systemPrompt != null && m.role == "user" && result.Count == 0)
            {
                result.Add(("user", $"{systemPrompt}\n\n---\n\n处理以下内容：\n\n{m.content}"));
                systemPrompt = null;
            }
            else
            {
                result.Add(m);
            }
        }
        if (systemPrompt != null && result.Count == 0)
            result.Add(("user", systemPrompt));
        return result;
    }

    /// <summary>API Key 来源：进程环境变量 → 用户注册表环境变量 → config.json。</summary>
    private string? ResolveApiKey()
    {
        var env = Environment.GetEnvironmentVariable("QIANWEN_API_KEY");
        if (string.IsNullOrWhiteSpace(env))
            env = Environment.GetEnvironmentVariable("QIANWEN_API_KEY", EnvironmentVariableTarget.User);
        return !string.IsNullOrWhiteSpace(env) ? env
             : (string.IsNullOrWhiteSpace(_cfg.ApiKey) ? null : _cfg.ApiKey);
    }
}
