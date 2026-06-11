namespace Reframe.Core;

/// <summary>
/// Pure parsing of hotkey gestures (no Win32 / no WinUI dependency, unit-testable).
/// Text like "Ctrl+Alt+F" / "Win+Alt+1" → (modifier bitmask, virtual-key code).
///
/// <para>Modifier recognition (case-tolerant, several aliases):</para>
/// <list type="bullet">
/// <item>Ctrl / Control → MOD_CONTROL (0x0002)</item>
/// <item>Alt → MOD_ALT (0x0001)</item>
/// <item>Shift → MOD_SHIFT (0x0004)</item>
/// <item>Win / Windows / Meta / Super → MOD_WIN (0x0008)</item>
/// </list>
///
/// <para>Main key: a single letter A-Z, a single digit 0-9, or a function key F1-F24. Anything else is invalid.</para>
///
/// <para>Validation: exactly one main key is required, plus at least one modifier (a modifier-less global
/// hotkey is easy to trigger by accident, and a lone Win key is mostly taken by the system). The parsed mods
/// do not include MOD_NOREPEAT — HotkeyService adds it uniformly at registration time, keeping the pure logic
/// stable and testable.</para>
/// </summary>
public static class HotkeyGesture
{
    // Matches user32 RegisterHotKey's fsModifiers.
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>
    /// Parse the gesture text. On success returns true and fills mods/vk (mods excludes NOREPEAT).
    /// On failure (empty string, no main key, multiple main keys, unknown token, no modifier) returns false.
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

            // Reaching here = a main-key token. Only one allowed.
            if (haveKey) return false;
            if (!TryParseKey(token, out vk)) return false;
            haveKey = true;
        }

        // Must have a main key + at least one modifier.
        if (!haveKey || mods == 0) return false;
        return true;
    }

    /// <summary>
    /// Format (mods, vk) back into canonical text (modifiers in a fixed order Ctrl+Alt+Shift+Win, for easy
    /// display/comparison). When vk can't be recognized, output the modifiers + a hex code as a fallback.
    /// NOREPEAT in mods is ignored.
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

        // Single letter A-Z: VK matches the uppercase ASCII.
        if (token.Length == 1)
        {
            char c = token[0];
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; } // VK_0..VK_9 == '0'..'9'
            return false;
        }

        // Function keys F1..F24: VK_F1 = 0x70.
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
