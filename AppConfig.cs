using System.Text.Json;
using Microsoft.Win32;

namespace ScreenTranslator;

public sealed class AppConfig
{
    // 默认直连云端 OpenAI 兼容接口（随安装包内置，无需 LiteLLM 等本地服务）；ApiKey 留空 = 首启引导填写
    public string BaseUrl { get; set; } = "https://maas.qianwenaiapi.com/compatible-mode/v1";
    public string ApiKey { get; set; } = "";
    public string TranslationModel { get; set; } = "qwen-mt-lite";
    public string ChatModel { get; set; } = "qwen3.8-omni-flash";
    public string OcrModel { get; set; } = "qwen-vl-ocr";   // 截图文字识别模型（多模态 OCR）
    public string TitleModel { get; set; } = "tongyi-xiaomi-analysis-flash"; // 会话标题生成模型（截图翻译专用）
    public bool AutoSpeak { get; set; } = true;
    public bool CaptureSelectedText { get; set; } = true;   // 划词后自动模拟 Ctrl+C 取词；可关闭以避免与复制快捷键冲突
    public bool ShowScreenshotTip { get; set; } = true;     // 截图时是否显示操作引导提示
    public bool AutoStart { get; set; } = true;             // 开机自动启动（HKCU Run 注册表项）
    public int HistoryLimit { get; set; } = 500;

    // ---------- 开机自启（注册表 HKCU\...\Run，无需管理员权限） ----------
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "YiYiBall";

    /// <summary>按当前 AutoStart 配置写入/删除开机自启注册表项。</summary>
    public void ApplyAutoStart()
    {
        try
        {
            // CreateSubKey：Run 键理论上始终存在，但个别精简系统可能缺失——不存在则创建，避免自启静默失效
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (AutoStart)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // 注册表写入失败不影响设置保存
        }
    }

    // ---------- 对话行为 ----------
    public bool AutoSpeakReply { get; set; } = false;       // 对话回复完成后自动朗读（截图翻译长文不读）
    public int ContextTurns { get; set; } = 10;             // 上下文携带轮数（1..50，一组问答算一轮）
    public string SystemPrompt { get; set; } = "";          // 自定义系统人设（空 = 内置讲解者提示词）

    // ---------- 自定义全局快捷键（设置 > 快捷键） ----------
    // 键 = 动作 id（screenshot / voice / chat），值 = 组合串（如 "Ctrl+Alt+S"）；空串或缺省 = 未启用
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    // ---------- 语音对话（长按主球进入） ----------
    public bool VoiceEnabled { get; set; } = true;          // 长按主球语音对话总开关
    public bool WakeWordEnabled { get; set; } = false;      // 语音唤醒（sherpa-onnx KWS，模型就绪后开启）
    public double MicSensitivity { get; set; } = 0.5;       // 0..1，越大越灵敏（映射 VAD 阈值）
    public int MicDevice { get; set; } = 0;                 // 麦克风设备序号（WaveIn DeviceNumber，见 data\ball.log 日志里的设备清单）
    public string AsrModel { get; set; } = "fun-asr-flash-8k-realtime";   // 整段 wav 语音识别（DashScope 原生 WS 协议）
    public string IntentModel { get; set; } = "tongyi-intent-detect-v3";  // 意图路由（纯分类）
    public string PlanModel { get; set; } = "deepseek-v4.1-flash";        // 操作场景结构化规划
    // 打包版随安装包内置 TTS 服务（{app}\tools\tts\tts_server\tts_server.exe，程序自动拉起）：
    // 默认指向本地服务（edge 在线晓伊音色优先，ZipVoice 本地克隆兜底）；留空 = SAPI 兜底
    public string TtsCosyVoiceUrl { get; set; } = "http://127.0.0.1:9777";

    // ---------- 记忆与知识库（方案 §10） ----------
    public string EmbeddingModel { get; set; } = "qwen3.7-text-embedding-flash"; // 向量化模型（云端 embeddings 接口）
    public List<string> KbFolders { get; set; } = new();    // 知识库收录文件夹（txt/md/pdf/docx，自动增量扫描）

    // ---------- 屏幕定位（方案 §11.3） ----------
    // 实测 qwen-vl-ocr 不返回坐标（2026-11-14），grounding 用可输出 0~1000 归一化包围盒的视觉模型
    public string GroundModel { get; set; } = "qwen3-vl-plus";

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppConfig();
    }

    public void Save()
    {
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
