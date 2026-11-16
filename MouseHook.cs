using System.Runtime.InteropServices;

namespace ScreenTranslator;

/// <summary>
/// 全局低级鼠标钩子：监听左键按下/抬起/双击，用于判断划词手势。
/// </summary>
public sealed class MouseHook : IDisposable
{
    public event Action<MouseMsg, POINT>? OnMouse;

    public enum MouseMsg { Down, Up, DblClk }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x201;
    private const int WM_LBUTTONUP = 0x202;
    private const int WM_LBUTTONDBLCLK = 0x203;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private IntPtr _hook;
    private readonly LowLevelMouseProc _proc;

    public MouseHook()
    {
        _proc = HookProc;
        using var cur = System.Diagnostics.Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32() switch
            {
                WM_LBUTTONDOWN => MouseMsg.Down,
                WM_LBUTTONUP => MouseMsg.Up,
                WM_LBUTTONDBLCLK => MouseMsg.DblClk,
                _ => (MouseMsg?)null
            };
            if (msg != null)
                OnMouse?.Invoke(msg.Value, info.pt);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
