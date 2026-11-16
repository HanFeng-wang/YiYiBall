using System.Runtime.InteropServices;

namespace ScreenTranslator;

/// <summary>全局按键观察：捕捉 Esc（语音模式退出）。</summary>
public sealed class GlobalKeyObserver : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_ESCAPE = 0x1B;

    private readonly System.Windows.Forms.Timer _timer;
    private bool _wasDown;

    public event Action? EscapePressed;

    public GlobalKeyObserver()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) =>
        {
            bool down = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
            if (down && !_wasDown) EscapePressed?.Invoke();
            _wasDown = down;
        };
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
