using System.Runtime.InteropServices;

namespace ScreenTranslator;

/// <summary>
/// 划词捕获：鼠标手势结束后模拟 Ctrl+C 读取剪贴板获取选中文本（任意语言）。
/// 剪贴板必须在 STA 线程访问，因此用专用 STA 线程执行捕获。
/// </summary>
public sealed class SelectionService : IDisposable
{
    public event Action<string, MouseHook.POINT>? TextSelected;

    /// <summary>鼠标单击落下但没有任何选中文本（点了空白处/取消选中），用于让辅助球提前消失。</summary>
    public event Action? ClickNoSelection;

    private readonly MouseHook _hook;
    private MouseHook.POINT _downPt;
    private DateTime _downTime;
    private bool _dblClk;
    private bool _capturing;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;

    // 本应用窗口内不参与划词判定（选中面板里的文字、面板内单击等都是自家交互，不是划词手势）
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(MouseHook.POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private const uint GA_ROOTOWNER = 3;

    /// <summary>鼠标点是否落在本进程的顶层窗口（含 WebView2 子窗口链）之内。</summary>
    private static bool IsInsideOwnWindow(MouseHook.POINT pt)
    {
        try
        {
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            var root = GetAncestor(hwnd, GA_ROOTOWNER);
            if (root == IntPtr.Zero) root = hwnd;
            GetWindowThreadProcessId(root, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    public SelectionService(MouseHook hook)
    {
        _hook = hook;
        _hook.OnMouse += OnMouse;
    }

    private void OnMouse(MouseHook.MouseMsg msg, MouseHook.POINT pt)
    {
        // 本应用窗口内：不触发划词捕获、不模拟 Ctrl+C、不触发"无选区"消球
        if (IsInsideOwnWindow(pt)) return;
        switch (msg)
        {
            case MouseHook.MouseMsg.Down:
                _downPt = pt;
                _downTime = DateTime.UtcNow;
                _dblClk = false;
                break;
            case MouseHook.MouseMsg.DblClk:
                _dblClk = true;
                break;
            case MouseHook.MouseMsg.Up:
                var dist = Math.Abs(pt.X - _downPt.X) + Math.Abs(pt.Y - _downPt.Y);
                if (_dblClk || dist > 10)
                {
                    var elapsed = (DateTime.UtcNow - _downTime).TotalMilliseconds;
                    if (elapsed > 80 && elapsed < 3000)
                        ScheduleCapture(pt);
                }
                else if (!_dblClk)
                {
                    // 原地单击 = 没有产生选区（取消选中/点空白），辅助球应立即进入消失阶段
                    ClickNoSelection?.Invoke();
                }
                _dblClk = false;
                break;
        }
    }

    private void ScheduleCapture(MouseHook.POINT pt)
    {
        if (_capturing) return;
        _capturing = true;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220);
                var text = await CaptureAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim();
                    if (text.Length <= 800 && HasContent(text))
                        TextSelected?.Invoke(text, pt);
                }
            }
            finally
            {
                _capturing = false;
            }
        });
    }

    private static bool HasContent(string s)
    {
        foreach (var c in s)
            if (!char.IsWhiteSpace(c) && (char.IsLetterOrDigit(c) || c > 127))
                return true;
        return false;
    }

    /// <summary>在专用 STA 线程上执行：保存剪贴板 → Ctrl+C → 读取 → 恢复剪贴板。</summary>
    private static Task<string?> CaptureAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(CaptureOnStaThread()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static string? CaptureOnStaThread()
    {
        string? backup = null;
        bool hadClip = false;
        try
        {
            if (Clipboard.ContainsText())
            {
                backup = Clipboard.GetText();
                hadClip = true;
            }
            else
            {
                Clipboard.Clear();
            }
        }
        catch { /* 剪贴板被占用时忽略 */ }

        SendCtrlC();
        Thread.Sleep(300);

        string? result = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                result = Clipboard.GetText();
                // 未变化则视为无新选词
                if (result == backup) result = null;
            }
        }
        catch { }

        // 恢复原剪贴板
        try
        {
            if (hadClip && backup != null)
                Clipboard.SetText(backup);
            else
                Clipboard.Clear();
        }
        catch { }

        return result;
    }

    private static void SendCtrlC()
    {
        var inputs = new INPUT[4];
        // Ctrl down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_CONTROL;
        // C down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_C;
        // C up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].U.ki.wVk = VK_C;
        inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;
        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].U.ki.wVk = VK_CONTROL;
        inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, INPUT.Size);
    }

    public void Dispose() => _hook.OnMouse -= OnMouse;
}
