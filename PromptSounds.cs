using System.Media;

namespace ScreenTranslator;

/// <summary>
/// 语音模式提示音（「落地 · 果冻弹」）：进入音（唤醒/长按进入时）与告别音（会话退出时）。
/// 预置 wav 由 tools/sfx/render_drip.py 渲染（与预览页 Web Audio 合成同参），SoundPlayer 播放。
/// </summary>
public static class PromptSounds
{
    private static SoundPlayer? _in;
    private static SoundPlayer? _out;
    private static long _lastPlayInTick;                // 唤醒命中时工作线程已播一次，UI 侧会话启动还会调一次，防双重播放

    public static void Preload()
    {
        try { (_in ??= Create("voice_in.wav")).LoadAsync(); } catch { }
        try { (_out ??= Create("voice_out.wav")).LoadAsync(); } catch { }
    }

    /// <summary>进入语音模式提示音（唤醒命中 / 长按进入）。1.5s 内重复调用只播一次。</summary>
    public static void PlayIn()
    {
        var now = Environment.TickCount64;
        if (now - _lastPlayInTick < 1500) return;
        _lastPlayInTick = now;
        Play(_in ??= Create("voice_in.wav"));
    }

    /// <summary>退出语音模式告别音（再见 / 静默超时 / Esc）。</summary>
    public static void PlayOut() => Play(_out ??= Create("voice_out.wav"));

    private static SoundPlayer Create(string name) =>
        new(Path.Combine(AppContext.BaseDirectory, "assets", "sounds", name));

    private static void Play(SoundPlayer p)
    {
        try { p.Play(); } catch { }   // 音效失败不影响主流程
    }
}
