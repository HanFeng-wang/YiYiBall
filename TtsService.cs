using System.Net.Http;
using System.Speech.Synthesis;
using NAudio.Wave;
using System.Diagnostics;

namespace ScreenTranslator;

/// <summary>
/// 本地 TTS 发音（Windows SAPI 兜底；cfg.TtsCosyVoiceUrl 非空时优先走本地推理服务，
/// 服务未就绪/失败时静默回退 SAPI，不弹错）。本地服务返回 WAV 字节，用 NAudio 播放。
/// 语音会话使用 SpeakSync（分句逐条同步播，工作线程调用、句间可取消）；
/// 聊天面板自动发音沿用 Speak（异步，不阻塞 UI）。
/// </summary>
public sealed class TtsService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private SpeechSynthesizer? _synth;
    private readonly object _lock = new();
    private readonly AppConfig _cfg;

    public TtsService(AppConfig cfg) => _cfg = cfg;

    /// <summary>发音，返回是否成功（无可用的语音引擎时返回 false）。</summary>
    public bool Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            lock (_lock)
            {
                _synth ??= new SpeechSynthesizer();
                var s = text.Trim();
                // 根据内容选择中/英文语音
                bool isCjk = s.Any(c => c >= 0x4E00 && c <= 0x9FFF);
                var voice = _synth.GetInstalledVoices()
                    .FirstOrDefault(v => v.VoiceInfo.Culture.Name.StartsWith(isCjk ? "zh" : "en"));
                if (voice == null) return false;
                _synth.SelectVoice(voice.VoiceInfo.Name);
                _synth.SpeakAsyncCancelAll();
                _synth.SpeakAsync(s.Length > 120 ? s[..120] : s);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 同步播报一句（语音会话专用）：阻塞到念完或取消。ct 取消时立即停止并返回 false。
    /// 必须在工作线程调用。CosyVoiceUrl 非空时优先走本地推理服务（未就绪则静默回退 SAPI）。
    /// </summary>
    public bool SpeakSync(string text, CancellationToken ct = default)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.Length > 120) s = s[..120];           // 口播强约束：超长截断兜底

        // ① 本地推理服务（ZipVoice/CosyVoice 音色克隆）
        // ② SAPI 兜底：仅在未配置本地服务时启用——机械音是「音色奇怪」投诉的元凶，
        //    配置了克隆音色后失败宁可不出声（等自愈），绝不播 SAPI
        if (!string.IsNullOrWhiteSpace(_cfg.TtsCosyVoiceUrl))
            return TrySpeakHttp(s, ct);   // 服务播报成功返回 true（此前误丢返回值，恒 false 导致会话误判播报失败）
        return SpeakSapi(s, ct);
    }

    /// <summary>SAPI 播报（仅未配置本地推理服务时使用）。</summary>
    private bool SpeakSapi(string s, CancellationToken ct)
    {
        lock (_lock)
        {
            try
            {
                _synth ??= new SpeechSynthesizer();
                bool isCjk = s.Any(c => c >= 0x4E00 && c <= 0x9FFF);
                var voice = _synth.GetInstalledVoices()
                    .FirstOrDefault(v => v.VoiceInfo.Culture.Name.StartsWith(isCjk ? "zh" : "en"));
                if (voice == null) return false;
                _synth.SelectVoice(voice.VoiceInfo.Name);

                // 异步播报 + 等完成事件：ct 取消时 SpeakAsyncCancelAll 能实时切断当前句
                using var done = new ManualResetEventSlim(false);
                void OnDone(object? s, SpeakCompletedEventArgs e) => done.Set();
                _synth.SpeakCompleted += OnDone;
                try
                {
                    _synth.SpeakAsyncCancelAll();
                    _synth.SpeakAsync(s);
                    while (!done.Wait(100))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            _synth.SpeakAsyncCancelAll();
                            return false;
                        }
                    }
                    return true;
                }
                finally { _synth.SpeakCompleted -= OnDone; }
            }
            catch (OperationCanceledException) { return false; }
            catch { return false; }
        }
    }

    /// <summary>停止当前播报（打断时调用，工作线程安全）。</summary>
    public void Cancel()
    {
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
        try { _httpOut?.Stop(); } catch { }              // 本地推理播报的实时切断
    }

    /// <summary>
    /// 调本地 TTS 推理服务：POST {url}/tts {"text":...} → WAV 字节 → NAudio 同步播完。
    /// 连接失败时自动拉起/等待服务（tools\tts\server.py，模型加载约 30-60s）后重试一次。
    /// 仍失败返回 false（调用方不再回 SAPI，留待下次自愈），不向外抛异常。
    /// </summary>
    private bool TrySpeakHttp(string s, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var url = _cfg.TtsCosyVoiceUrl.TrimEnd('/') + "/tts";
                using var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { text = s }),
                    System.Text.Encoding.UTF8, "application/json");
                using var resp = Http.PostAsync(url, content, ct).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return false;
                var wav = resp.Content.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
                if (wav.Length < 100) return false;

                // edge-tts 返回 mp3（MediaFoundation 解码播放），ZipVoice 返回 wav
                var ms = new MemoryStream(wav);
                var isMp3 = (resp.Content.Headers.ContentType?.MediaType ?? "").Contains("mpeg");
                WaveStream reader = isMp3 ? new StreamMediaFoundationReader(ms) : new WaveFileReader(ms);
                var wo = new WaveOutEvent();
                _httpOut = wo;
                wo.Init(reader);
                wo.Play();
                try
                {
                    while (wo.PlaybackState == PlaybackState.Playing)
                    {
                        if (ct.IsCancellationRequested) { wo.Stop(); return false; }
                        Thread.Sleep(50);
                    }
                    return true;
                }
                finally
                {
                    _httpOut = null;
                    wo.Dispose();
                    reader.Dispose();
                    ms.Dispose();
                }
            }
            catch (OperationCanceledException) { return false; }
            catch
            {
                // 服务未运行 / 崩溃（ZipVoice 偶发 native 崩溃）→ 拉起后重试一次
                if (attempt == 0 && EnsureServer(ct)) continue;
                return false;
            }
        }
        return false;
    }

    /// <summary>探测本地 TTS 服务，无响应则拉起打包版 tts_server.exe（无则回退 python server.py）并等待就绪。</summary>
    private bool EnsureServer(CancellationToken ct)
    {
        try
        {
            if (ProbeServer()) return true;

            // 打包版优先：安装包内置 {app}\tools\tts\tts_server\tts_server.exe（自包含，无需 Python）
            var exe = FindUpward(Path.Combine(AppContext.BaseDirectory),
                Path.Combine("tools", "tts", "tts_server", "tts_server.exe"));
            ProcessStartInfo psi;
            if (exe != null)
            {
                // 经 cmd 转发并把 stdout/stderr 追加到日志：与原 start_tts.cmd 守护方式一致，便于查死因。
                // 注意 /s /c + 整体再包一层引号：cmd /c 的引号剥离规则在命令行出现 4 个引号时
                // 会剥掉首尾引号把命令撕碎（表现为「文件名、目录名或卷标语法不正确」且服务静默没启动）
                var ttsLog = Path.Combine(AppContext.BaseDirectory, "data", "tts_server.log");
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/s /c \"\"{exe}\" 9777 >> \"{ttsLog}\" 2>&1\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
            }
            else
            {
                // 开发机回退：源码目录里的 server.py（需本机 Python）
                var script = FindUpward(Path.Combine(AppContext.BaseDirectory),
                    Path.Combine("tools", "tts", "server.py"));
                if (script == null) return false;
                var ttsLog = Path.Combine(AppContext.BaseDirectory, "data", "tts_server.log");
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/s /c \"python -u \"{script}\" >> \"{ttsLog}\" 2>&1\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
            }
            if (Process.Start(psi) == null) return false;

            // 等模型加载 + 端口就绪（decoder 124MB，冷启动约 30-60s）
            for (int i = 0; i < 45; i++)
            {
                if (ct.IsCancellationRequested) return false;
                Thread.Sleep(2000);
                if (ProbeServer()) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool ProbeServer()
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var r = c.GetAsync("http://127.0.0.1:9777/health").GetAwaiter().GetResult();
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string? FindUpward(string startDir, string relative)
    {
        var dir = startDir;
        for (int i = 0; i < 6; i++)
        {
            var cand = Path.Combine(dir, relative);
            if (File.Exists(cand)) return cand;
            var parent = Directory.GetParent(dir);
            if (parent == null) return null;
            dir = parent.FullName;
        }
        return null;
    }

    private WaveOutEvent? _httpOut;

    public void Stop() => Cancel();

    public void Dispose()
    {
        lock (_lock) { _synth?.Dispose(); _synth = null; }
    }
}
