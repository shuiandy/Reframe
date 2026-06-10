namespace Reframe.Core;

/// <summary>
/// 热键手势的纯解析(无 Win32 / 无 WinUI 依赖,可单测)。
/// 文本如 "Ctrl+Alt+F" / "Win+Alt+1" → (修饰符位掩码, 虚拟键码)。
///
/// <para>修饰符识别(大小写宽容、可多种别名):</para>
/// <list type="bullet">
/// <item>Ctrl / Control → MOD_CONTROL(0x0002)</item>
/// <item>Alt → MOD_ALT(0x0001)</item>
/// <item>Shift → MOD_SHIFT(0x0004)</item>
/// <item>Win / Windows / Meta / Super → MOD_WIN(0x0008)</item>
/// </list>
///
/// <para>主键:单字母 A-Z、单数字 0-9、功能键 F1-F24。其余无效。</para>
///
/// <para>校验:必须恰好一个主键;至少一个修饰符(全局热键无修饰符易误触,且 Win 单键多被系统占用)。
/// 解析得到的 mods 不含 MOD_NOREPEAT —— 由 HotkeyService 注册时统一附加,使纯逻辑稳定可测。</para>
/// </summary>
public static class HotkeyGesture
{
    // 与 user32 RegisterHotKey 的 fsModifiers 一致。
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>
    /// 解析手势文本。成功返回 true 并填 mods/vk(mods 不含 NOREPEAT)。
    /// 失败(空串、无主键、多主键、未知 token、无修饰符)返回 false。
    /// </summary>
    public static bool TryParse(string? gesture, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        bool haveKey = false;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = raw.ToUpperInvariant();
            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    mods |= MOD_CONTROL;
                    continue;
                case "ALT":
                    mods |= MOD_ALT;
                    continue;
                case "SHIFT":
                    mods |= MOD_SHIFT;
                    continue;
                case "WIN":
                case "WINDOWS":
                case "META":
                case "SUPER":
                    mods |= MOD_WIN;
                    continue;
            }

            // 走到这里 = 主键 token。只允许一个。
            if (haveKey) return false;
            if (!TryParseKey(token, out vk)) return false;
            haveKey = true;
        }

        // 必须有主键 + 至少一个修饰符。
        if (!haveKey || mods == 0) return false;
        return true;
    }

    /// <summary>
    /// 把 (mods, vk) 反向格式化为规范文本(修饰符固定顺序 Ctrl+Alt+Shift+Win,便于显示/比较)。
    /// vk 无法识别时仅输出修饰符 + 十六进制码兜底。mods 中的 NOREPEAT 被忽略。
    /// </summary>
    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>(4);
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        if (token.Length == 0) return false;

        // 单字母 A-Z:VK 与 ASCII 大写一致。
        if (token.Length == 1)
        {
            char c = token[0];
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; } // VK_0..VK_9 == '0'..'9'
            return false;
        }

        // 功能键 F1..F24:VK_F1 = 0x70。
        if (token[0] == 'F' && int.TryParse(token.AsSpan(1), out int n) && n >= 1 && n <= 24)
        {
            vk = (uint)(0x70 + (n - 1));
            return true;
        }

        return false;
    }

    private static string KeyName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x70 + 23) return "F" + (vk - 0x70 + 1);
        return "0x" + vk.ToString("X2");
    }
}
