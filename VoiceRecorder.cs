using NAudio.Wave;

namespace ScreenTranslator;

/// <summary>
/// 麦克风采集 + 音量 + VAD 切句（NAudio WaveInEvent，16k/16bit/mono，20ms 帧）。
/// - VolumeChanged：每 20ms 一次 RMS（0..1 量级），驱动球内声纹与打断检测（永不停，伪全双工）。
/// - FrameReady：每 20ms 一次 RMS+PCM 原始帧（语音唤醒 KWS 共用同一条流）。
/// - UtteranceReady：一段完整语句的 WAV 字节（RMS 过阈 3 帧 = 开口；静音 800ms = 句尾；
///   最短 400ms 防半句，最长 15s 强制切句）。SegmentationEnabled=false 时只报音量不切句
///   （播报期间关闭切句，但音量仍持续输出供回声守卫）。
/// - 设备：使用 cfg.MicDevice 指定输入设备序号；打不开时自动回退设备 0。
///   启动时把设备清单与所选设备写入 data\ball.log，便于排查"说话听不到"。
/// 回调都在 WaveIn 后台线程触发，消费方自行调度到 UI 线程。
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly MemoryStream _segBuffer = new();
    private readonly object _lock = new();

    private int _speechFrames;        // 连续过阈帧数（判定开口）
    private int _silenceFrames;       // 开口后连续静音帧数（判定句尾）
    private bool _inSpeech;
    private long _speechBytes;        // 开口以来的累计字节
    private int _zeroFrames;          // 连续零电平帧（设备无输入诊断）
    private bool _zeroWarned;
    private float _noiseFloor = -1f;  // 环境底噪（listening 态最小值保持 + 极慢回升），-1 未初始化
    private float _peakRms;           // 当前语句内峰值电平（切句日志用）

    public event Action<float>? VolumeChanged;
    public event Action<float, byte[]>? FrameReady;
    public event Action<byte[]>? UtteranceReady;
    public event Action<string>? Error;

    /// <summary>是否做 VAD 切句（语音会话 listening 态为 true）。</summary>
    public volatile bool SegmentationEnabled;

    /// <summary>静音判定时长（ms），默认 800。</summary>
    public int SilenceMs { get; set; } = 800;

    /// <summary>最短有效语句（ms），更短视为误触丢弃。</summary>
    public int MinUtteranceMs { get; set; } = 400;

    /// <summary>单句最长（ms），到达强制切句。</summary>
    public int MaxUtteranceMs { get; set; } = 15000;

    private readonly AppConfig _cfg;
    public VoiceRecorder(AppConfig cfg) => _cfg = cfg;

    /// <summary>
    /// 当前 RMS 判定阈值：随灵敏度配置变化（0.5 灵敏度时约 0.033），
    /// 并随环境底噪自适应——安静环境/低增益麦克风自动降低（下限 0.008），
    /// 嘈杂环境不超过固定阈值。解决"说话声音小过不了阈、完全不切句"的问题。
    /// </summary>
    public float Threshold
    {
        get
        {
            float fixedTh = (float)(0.05 * (1 - _cfg.MicSensitivity) + 0.008);
            return _noiseFloor < 0 ? fixedTh : Math.Clamp(_noiseFloor * 5f, 0.008f, fixedTh);
        }
    }

    /// <summary>枚举系统输入设备名（日志/诊断用）。</summary>
    public static string[] ListDevices()
    {
        try
        {
            int n = WaveIn.DeviceCount;
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = WaveIn.GetCapabilities(i).ProductName;
            return names;
        }
        catch { return Array.Empty<string>(); }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_waveIn != null) return;
            ResetSegment();
            _zeroWarned = false;
            _zeroFrames = 0;

            var devices = ListDevices();
            var deviceNames = devices.Select((d, i) => "[" + i + "]" + d.TrimEnd((char)0));
            Log("麦克风设备清单：" + (devices.Length == 0 ? "（无）" : string.Join(" | ", deviceNames)));

            int dev = Math.Clamp(_cfg.MicDevice, 0, Math.Max(0, devices.Length - 1));
            if (dev != _cfg.MicDevice)
                Log($"配置的 MicDevice={_cfg.MicDevice} 超出范围，回退为 {dev}");

            var wi = new WaveInEvent
            {
                DeviceNumber = dev,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 4
            };
            wi.DataAvailable += OnData;
            wi.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Error?.Invoke(e.Exception.Message);
            };
            try
            {
                wi.StartRecording();
            }
            catch (Exception ex)
            {
                // 没有任何录音设备的机器（精简系统/无麦克风）不能崩：静默跳过，程序照常运行
                if (dev != 0)
                {
                    Log($"设备 {dev} 打开失败（{ex.Message}），回退设备 0");
                    try { wi.Dispose(); } catch { }
                    wi = new WaveInEvent
                    {
                        DeviceNumber = 0,
                        WaveFormat = new WaveFormat(16000, 16, 1),
                        BufferMilliseconds = 20,
                        NumberOfBuffers = 4
                    };
                    wi.DataAvailable += OnData;
                    wi.RecordingStopped += (_, e) =>
                    {
                        if (e.Exception != null) Error?.Invoke(e.Exception.Message);
                    };
                    try
                    {
                        wi.StartRecording();
                    }
                    catch (Exception ex2)
                    {
                        Log($"设备 0 也打不开（{ex2.Message}），本次跳过录音（可能没有麦克风），程序继续运行");
                        try { wi.Dispose(); } catch { }
                        return;
                    }
                }
                else
                {
                    Log($"设备 {dev} 打不开（{ex.Message}），本次跳过录音（可能没有麦克风），程序继续运行");
                    try { wi.Dispose(); } catch { }
                    return;
                }
            }
            Log($"录音已启动：设备 {dev}「{(dev < devices.Length ? devices[dev].TrimEnd((char)0) : "?")}」，16k/16bit/mono，阈值 {Threshold:0.000}");
            _waveIn = wi;
        }
    }

    internal static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data"));
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "data", "ball.log"),
                $"[{DateTime.Now:HH:mm:ss}] [mic] {msg}\n");
        }
        catch { }
    }

    public void Stop()
    {
        WaveInEvent? wi;
        lock (_lock)
        {
            wi = _waveIn;
            _waveIn = null;
        }
        if (wi != null)
        {
            try { wi.StopRecording(); } catch { }
            wi.Dispose();
        }
        lock (_lock) ResetSegment();
    }

    private void ResetSegment()
    {
        _segBuffer.SetLength(0);
        _speechFrames = _silenceFrames = 0;
        _inSpeech = false;
        _speechBytes = 0;
        _peakRms = 0;
    }

    private void OnData(object? s, WaveInEventArgs e)
    {
        float rms = ComputeRms(e.Buffer, e.BytesRecorded);
        VolumeChanged?.Invoke(rms);
        FrameReady?.Invoke(rms, e.Buffer);
        if (!SegmentationEnabled) return;

        // 设备无输入诊断：持续 ~10s 严格零电平提示一次（真实麦克风底噪不会是严格 0）
        if (rms <= 0.0001f)
        {
            if (!_zeroWarned && ++_zeroFrames >= 500)
            {
                _zeroWarned = true;
                Error?.Invoke("当前麦克风持续没有声音输入，请检查是否选对了输入设备（可改 config.json 的 MicDevice）");
            }
        }
        else _zeroFrames = 0;

        // 环境底噪跟踪（仅 listening 态；TTS 播报期 SegmentationEnabled=false，不会污染底噪）：
        // 出现新低立即采用，否则极慢地向当前电平回升（语音突发不会明显抬高底噪）
        if (_noiseFloor < 0 || rms < _noiseFloor) _noiseFloor = rms;
        else _noiseFloor += (rms - _noiseFloor) * 0.0005f;

        lock (_lock)
        {
            float th = Threshold;
            _peakRms = Math.Max(_peakRms, rms);
            bool loud = rms > th;

            if (!_inSpeech)
            {
                _speechFrames = loud ? _speechFrames + 1 : 0;
                if (_speechFrames >= 3)                     // ~60ms 过阈 = 开口
                {
                    _inSpeech = true;
                    _silenceFrames = 0;
                    _speechBytes = (uint)_segBuffer.Length;
                    _segBuffer.SetLength(0);                // 丢弃开口前的静音缓冲
                    _segBuffer.Write(e.Buffer, 0, e.BytesRecorded);
                }
                // 未开口：环形保留最近 ~200ms 静音（开口时拼在句头，避免吞首字）
                else
                {
                    _segBuffer.Write(e.Buffer, 0, e.BytesRecorded);
                    long cap = 16000 * 2 / 5;               // 200ms
                    if (_segBuffer.Length > cap)
                        TrimHead(_segBuffer, (int)(_segBuffer.Length - cap));
                }
                return;
            }

            // 已开口：累计整段
            _segBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            _speechBytes += e.BytesRecorded;
            int speechMs = (int)(_speechBytes * 1000 / 32000);   // 16k×16bit×mono = 32KB/s

            if (speechMs >= MaxUtteranceMs) { Emit(); return; }

            _silenceFrames = loud ? 0 : _silenceFrames + 1;
            if (_silenceFrames >= SilenceMs / 20)       // 静音 800ms = 句尾
            {
                if (speechMs >= MinUtteranceMs) Emit();
                else
                {
                    Log($"弃短句：{speechMs} ms，峰值 {_peakRms:0.000}，阈值 {Threshold:0.000}（可能为噪声/半字）");
                    ResetSegment();
                }
            }
        }
    }

    /// <summary>切句完成：导出 WAV 字节并重置（在锁内调用）。</summary>
    private void Emit()
    {
        Log($"切句：{(int)(_segBuffer.Length * 1000 / 32000)} ms，峰值 {_peakRms:0.000}，阈值 {Threshold:0.000}");
        var wav = BuildWav(_segBuffer.ToArray());
        ResetSegment();
        Task.Run(() => UtteranceReady?.Invoke(wav));    // 脱离锁再回调
    }

    private static float ComputeRms(byte[] buf, int n)
    {
        long sum = 0;
        int samples = n / 2;
        for (int i = 0; i + 1 < n; i += 2)
        {
            short v = (short)(buf[i] | (buf[i + 1] << 8));
            sum += (long)v * v;
        }
        if (samples == 0) return 0;
        return (float)Math.Sqrt((double)sum / samples / 32768 / 32768);   // 0..1
    }

    /// <summary>丢弃 MemoryStream 头部 n 字节。</summary>
    private static void TrimHead(MemoryStream ms, int n)
    {
        var rest = ms.ToArray().AsSpan(n).ToArray();
        ms.SetLength(0);
        ms.Write(rest, 0, rest.Length);
    }

    /// <summary>裸 PCM → WAV 文件字节（16k/16bit/mono）。</summary>
    internal static byte[] BuildWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)1);              // mono
            w.Write(16000);                 // 采样率
            w.Write(32000);                 // 字节率
            w.Write((short)2);              // 块对齐
            w.Write((short)16);             // 位深
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
