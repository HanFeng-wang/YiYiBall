namespace ScreenTranslator;

/// <summary>
/// 全局快捷键组合解析（设置 &gt; 快捷键）：如 "Ctrl+Alt+S"、"F9"、"Ctrl+Shift+Space"。
/// 安全约束：无修饰键时仅允许 F1-F12——单字母/数字等键会劫持全局输入，拒绝注册。
/// </summary>
public static class HotkeyMap
{
    // RegisterHotKey 的修饰键标志（winuser.h）
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    private static readonly Dictionary<string, ushort> NamedVk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };

    /// <summary>解析组合串为修饰键标志 + 虚拟键码；无法识别或违反安全约束时返回 false。</summary>
    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (var p in parts)
        {
            if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("control", StringComparison.OrdinalIgnoreCase)) { mods |= ModControl; continue; }
            if (p.Equals("shift", StringComparison.OrdinalIgnoreCase)) { mods |= ModShift; continue; }
            if (p.Equals("alt", StringComparison.OrdinalIgnoreCase)) { mods |= ModAlt; continue; }
            if (p.Equals("win", StringComparison.OrdinalIgnoreCase) || p.Equals("windows", StringComparison.OrdinalIgnoreCase)) { mods |= ModWin; continue; }

            if (vk != 0) return false;   // 主键只能有一个
            vk = p.Length switch
            {
                1 when char.IsAsciiLetter(p[0]) => (ushort)(0x41 + char.ToUpperInvariant(p[0]) - 'A'),
                1 when char.IsAsciiDigit(p[0]) => (ushort)(0x30 + (p[0] - '0')),
                _ => NamedVk.TryGetValue(p, out var v) ? v : (ushort)0,
            };
            if (vk == 0) return false;
        }

        // F1-F12 可单独作为快捷键；其余主键必须带修饰键
        bool isFKey = vk is >= 0x70 and <= 0x7B;
        return isFKey || mods != 0;
    }
}
