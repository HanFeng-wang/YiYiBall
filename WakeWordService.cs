using SherpaOnnx;

namespace ScreenTranslator;

/// <summary>
/// 语音唤醒服务（sherpa-onnx 官方 C# API 内嵌 KWS，方案 §3.2）：
/// 唤醒词「小依小依」等（词表 = tools\kws\keywords.txt，声母+带调韵母格式），
/// 与 ASR 共用 VoiceRecorder 的麦克风流（FrameReady 20ms 帧分发）。
/// 历史教训：早期走 Python 守护进程 + UDP 中转，但 sherpa-onnx native 偶发静默崩溃
/// （进程直接消失、无任何报错），唤醒随之静默失效——故改为进程内直跑，整层中转消失。
/// 模型在后台线程加载（约数秒），就绪前 Feed 静默丢弃帧；命中后播提示音并触发
/// OnWakeDetected（免手势进入语音模式）。
/// </summary>
public sealed class WakeWordService : IDisposable
{
    /// <summary>检出唤醒词（免手势进入语音模式）。工作线程触发，订阅方自行调度到 UI。</summary>
    public event Action? OnWakeDetected;

    private const int SampleRate = 16000;
    private const float KeywordsThreshold = 0.18f;   // 0.25 实测召回偏低（喊 2-3 次才中 1 次）
    private const float KeywordsScore = 1.5f;

    private readonly AppConfig _cfg;
    private volatile bool _running;
    private long _lastWakeTick;

    private KeywordSpotter? _kws;
    private OnlineStream? _stream;
    private volatile bool _ready;
    private Task? _initTask;
    private readonly object _lock = new();   // Feed（WaveIn 线程）与 Start/Stop（UI 线程）并发保护

    public WakeWordService(AppConfig cfg) => _cfg = cfg;

    public bool IsListening { get; private set; }

    /// <summary>模型是否就绪（向上各层的 models\ 下有 kws 模型目录）。</summary>
    public static bool ModelReady()
    {
        // 运行目录通常在 bin\...\，模型在项目根 models\*kws*zipformer*\：向上逐层查 models 子目录
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            try
            {
                var models = Path.Combine(dir, "models");
                if (Directory.Exists(models) &&
                    Directory.GetDirectories(models, "*kws*zipformer*", SearchOption.TopDirectoryOnly).Length > 0)
                    return true;
            }
            catch { }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return false;
    }

    public void Start()
    {
        if (!_cfg.WakeWordEnabled || IsListening) return;
        if (!ModelReady())
        {
            TryLog("唤醒模型未就绪（models 下无 *kws*zipformer*），本次不启用唤醒");
            return;
        }
        _running = true;
        IsListening = true;
        if (_ready)
        {
            lock (_lock) { _stream ??= _kws!.CreateStream(); }   // 复用已加载模型，重建识别流
        }
        else
        {
            _initTask ??= Task.Run(InitKws);                     // 首次：后台加载模型（就绪前 Feed 丢帧）
        }
        TryLog("唤醒监听启动（内嵌 KWS）");
    }

    /// <summary>后台加载 KWS 模型并创建识别流。</summary>
    private void InitKws()
    {
        try
        {
            var mdir = FindModelDir();
            var keywords = FindUpward(Path.Combine(AppContext.BaseDirectory),
                Path.Combine("tools", "kws", "keywords.txt"))
                ?? throw new FileNotFoundException("找不到 tools\\kws\\keywords.txt");

            var cfg = new KeywordSpotterConfig();
            cfg.FeatConfig.SampleRate = SampleRate;
            cfg.FeatConfig.FeatureDim = 80;
            cfg.ModelConfig.Transducer.Encoder = Pick(mdir, "encoder-*.onnx");
            cfg.ModelConfig.Transducer.Decoder = Pick(mdir, "decoder-*.onnx");
            cfg.ModelConfig.Transducer.Joiner = Pick(mdir, "joiner-*.onnx");
            cfg.ModelConfig.Tokens = Pick(mdir, "tokens.txt");
            cfg.ModelConfig.Provider = "cpu";
            cfg.ModelConfig.NumThreads = 1;
            cfg.KeywordsFile = keywords;
            cfg.KeywordsScore = KeywordsScore;
            cfg.KeywordsThreshold = KeywordsThreshold;

            var kws = new KeywordSpotter(cfg);
            var stream = kws.CreateStream();
            lock (_lock) { _kws = kws; _stream = stream; }
            _ready = true;
            TryLog($"内嵌 KWS 就绪（词表：{Path.GetFileName(keywords)}，阈值 {KeywordsThreshold}）");
        }
        catch (Exception ex)
        {
            TryLog("内嵌 KWS 初始化失败：" + ex.Message);
        }
    }

    public void Stop()
    {
        if (!IsListening) return;
        IsListening = false;
        _running = false;
        lock (_lock)
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;          // Start 时重建（模型保留，免二次加载）
        }
        TryLog("唤醒监听暂停（语音会话中）");
    }

    /// <summary>喂入一帧 20ms PCM（VoiceRecorder.FrameReady 分发，WaveIn 线程调用）。</summary>
    public void Feed(float rms, byte[] pcm20ms)
    {
        if (!_running || !_ready) return;
        if (rms <= 0.0001f) return;                     // 静音帧不上送

        // 16bit LE PCM → float
        int n = pcm20ms.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = BitConverter.ToInt16(pcm20ms, i * 2) / 32768f;

        lock (_lock)
        {
            if (_stream == null || _kws == null) return;
            try
            {
                _stream.AcceptWaveform(SampleRate, samples);
                while (_kws.IsReady(_stream))
                {
                    _kws.Decode(_stream);
                    var result = _kws.GetResult(_stream);
                    if (result.Keyword == string.Empty) continue;

                    // 命中：立即重置流（防连触），防抖 3s
                    _kws.Reset(_stream);
                    if (Environment.TickCount64 - _lastWakeTick < 3000) break;
                    _lastWakeTick = Environment.TickCount64;
                    TryLog("命中唤醒词：" + result.Keyword);
                    // 命中提示音在工作线程立即播：唤醒路径此前"没有音效"即因音效排在
                    // UI 侧会话启动逻辑之后，时序上不可靠；独立于 UI 立即反馈
                    PromptSounds.PlayIn();
                    OnWakeDetected?.Invoke();
                    break;
                }
            }
            catch (ObjectDisposedException) { }         // Stop 与 Feed 并发，正常
            catch (Exception ex)
            {
                TryLog("KWS 解码异常：" + ex.Message);
            }
        }
    }

    /// <summary>向上逐层查 models\ 下的 KWS 模型目录。</summary>
    private static string FindModelDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            try
            {
                var models = Path.Combine(dir, "models");
                if (Directory.Exists(models))
                {
                    var hit = Directory.GetDirectories(models, "*kws*zipformer*", SearchOption.TopDirectoryOnly)
                        .OrderBy(d => d).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            catch { }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        throw new DirectoryNotFoundException("models 下找不到 *kws*zipformer* 模型目录");
    }

    private static string Pick(string mdir, string pattern)
    {
        var cands = Directory.GetFiles(mdir, pattern);
        if (cands.Length == 0) throw new FileNotFoundException($"{mdir} 下找不到 {pattern}");
        // 优先 int8 量化版（体积小、加载快）
        return cands.FirstOrDefault(c => c.Contains("int8")) ?? cands[0];
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

    private static void TryLog(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data"));
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "ball.log"),
                $"[{DateTime.Now:HH:mm:ss}] [wakeword] {msg}\n");
        }
        catch { }
    }

    public void Dispose() => Stop();
}
