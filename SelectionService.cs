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
    private int _capturing;
    private readonly bool _enabled;

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
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

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

    public SelectionService(MouseHook hook, bool enabled = true)
    {
        _hook = hook;
        _enabled = enabled;
        _hook.OnMouse += OnMouse;
    }

    private void OnMouse(MouseHook.MouseMsg msg, MouseHook.POINT pt)
    {
        // 本应用窗口内：不触发划词捕获、不模拟 Ctrl+C、不触发"无选区"消球
        if (!_enabled || IsInsideOwnWindow(pt)) return;
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
                        ScheduleCapture(pt, GetClipboardSequenceNumber());
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

    private void ScheduleCapture(MouseHook.POINT pt, uint clipboardSeqAtSelection)
    {
        if (Interlocked.Exchange(ref _capturing, 1) != 0) return;
        Task.Run(async () =>
        {
            try
            {
                // 给目标窗口留下处理鼠标选区的时间，也给用户自己按 Ctrl+C 的机会。
                await Task.Delay(260);

                // 用户已经复制过，或应用已自行更新剪贴板：此时强行注入 Ctrl+C 只会制造竞态。
                if (GetClipboardSequenceNumber() != clipboardSeqAtSelection) return;
                if (HasRecentCommandModifierActivity()) return;
                if (!await WaitForPhysicalModifiersReleasedAsync(700)) return;
                if (GetClipboardSequenceNumber() != clipboardSeqAtSelection) return;

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
                Interlocked.Exchange(ref _capturing, 0);
            }
        });
    }

    /// <summary>物理修饰键仍被按住时绝不注入 Ctrl+C，避免覆盖真实 Ctrl+C/Ctrl+V 的按键状态。</summary>
    private static async Task<bool> WaitForPhysicalModifiersReleasedAsync(int timeoutMs)
    {
        int waited = 0;
        while (HasPhysicalModifierDown())
        {
            if (waited >= timeoutMs) return false;
            await Task.Delay(25);
            waited += 25;
        }
        return true;
    }

    private static bool HasPhysicalModifierDown() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    /// <summary>刚按过 Ctrl/Alt/Win 时不接管，优先让用户自己的复制/粘贴快捷键正常完成。</summary>
    private static bool HasRecentCommandModifierActivity()
    {
        foreach (int vk in new[] { VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN })
        {
            short state = GetAsyncKeyState(vk);
            if ((state & 0x8000) != 0 || (state & 0x0001) != 0) return true;
        }
        return false;
    }

    private static bool HasContent(string s)
    {
        foreach (var c in s)
            if (!char.IsWhiteSpace(c) && (char.IsLetterOrDigit(c) || c > 127))
                return true;
        return false;
    }

    /// <summary>在专用 STA 线程上执行：Ctrl+C → 读取 → 仅在剪贴板仍归本次捕获时恢复。</summary>
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
        bool hadText = false;
        try
        {
            if (Clipboard.ContainsText())
            {
                backup = Clipboard.GetText();
                hadClip = true;
                hadText = true;
            }
            else
            {
                hadClip = Clipboard.GetDataObject() != null;
            }
        }
        catch { /* 剪贴板被占用时忽略 */ }

        uint seqBefore = GetClipboardSequenceNumber();
        if (!SendCtrlC()) return null;

        uint seqAfter = seqBefore;
        for (int i = 0; i < 28; i++)        // 最多等约 700ms，兼容 Chromium/Electron 与 Office
        {
            Thread.Sleep(25);
            seqAfter = GetClipboardSequenceNumber();
            if (seqAfter != seqBefore) break;
        }
        if (seqAfter == seqBefore) return null;   // 无选区：不改动用户现有剪贴板

        // 用户已再次操作剪贴板（例如紧接着 Ctrl+C/Ctrl+V），不要在此时恢复旧值。
        if (HasPhysicalModifierDown() || GetClipboardSequenceNumber() != seqAfter) return null;

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

        // 仅在剪贴板没有发生后续变化时恢复；避免把用户刚复制的新内容覆盖掉。
        if (GetClipboardSequenceNumber() != seqAfter) return result;
        try
        {
            if (hadText && backup != null)
                Clipboard.SetText(backup);
            else if (!hadClip)
                Clipboard.Clear();
        }
        catch { }

        return result;
    }

    private static bool SendCtrlC()
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
        uint sent = SendInput((uint)inputs.Length, inputs, INPUT.Size);
        if (sent == inputs.Length) return true;

        // SendInput 被系统部分拒绝时清理可能已经落下的按键，不让 Ctrl 残留。
        var releases = new INPUT[2];
        releases[0].type = INPUT_KEYBOARD;
        releases[0].U.ki.wVk = VK_C;
        releases[0].U.ki.dwFlags = KEYEVENTF_KEYUP;
        releases[1].type = INPUT_KEYBOARD;
        releases[1].U.ki.wVk = VK_CONTROL;
        releases[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)releases.Length, releases, INPUT.Size);
        return false;
    }

    public void Dispose() => _hook.OnMouse -= OnMouse;
}
