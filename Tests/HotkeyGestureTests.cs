using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Hotkey gesture pure parsing: text → (modifier bitmask, virtual key). Covers default gestures, case
/// tolerance, various invalid inputs, and the round-trip with Format.
/// </summary>
public class HotkeyGestureTests
{
    // user32 RegisterHotKey fsModifiers (same values as the HotkeyGesture constants).
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // ---- All default gestures parse ----

    [Theory(DisplayName = "Default gestures all parse")]
    [InlineData("Ctrl+Alt+B")]
    [InlineData("Ctrl+Alt+F")]
    [InlineData("Win+Alt+1")]
    [InlineData("Win+Alt+2")]
    [InlineData("Win+Alt+3")]
    public void DefaultGestures_Parse(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    [Fact(DisplayName = "Ctrl+Alt+B → MOD_CONTROL|MOD_ALT + 'B'")]
    public void CtrlAltB_Decoded()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+B", out uint mods, out uint vk));
        Assert.Equal(MOD_CONTROL | MOD_ALT, mods);
        Assert.Equal((uint)'B', vk);
    }

    [Fact(DisplayName = "Win+Alt+1 → MOD_WIN|MOD_ALT + '1'")]
    public void WinAlt1_Decoded()
    {
        Assert.True(HotkeyGesture.TryParse("Win+Alt+1", out uint mods, out uint vk));
        Assert.Equal(MOD_WIN | MOD_ALT, mods);
        Assert.Equal((uint)'1', vk);
    }

    [Fact(DisplayName = "Parsed result excludes NOREPEAT (added at registration)")]
    public void Parse_DoesNotIncludeNoRepeat()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+B", out uint mods, out _));
        Assert.Equal(0u, mods & 0x4000); // MOD_NOREPEAT
    }

    // ---- Case tolerance + modifier aliases ----

    [Theory(DisplayName = "Case tolerance: ctrl/CTRL/Ctrl are equivalent")]
    [InlineData("ctrl+alt+f")]
    [InlineData("CTRL+ALT+F")]
    [InlineData("Ctrl+Alt+f")]
    [InlineData("  ctrl + alt + F ")]
    public void CaseAndSpace_Tolerant(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out uint vk));
        Assert.Equal(MOD_CONTROL | MOD_ALT, mods);
        Assert.Equal((uint)'F', vk);
    }

    [Theory(DisplayName = "Control is an alias for Ctrl")]
    [InlineData("Control+Alt+B")]
    [InlineData("control+alt+b")]
    public void Control_Alias(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out _));
        Assert.Equal(MOD_CONTROL | MOD_ALT, mods);
    }

    [Theory(DisplayName = "Aliases for Win: Windows/Meta/Super")]
    [InlineData("Windows+Alt+1")]
    [InlineData("Meta+Alt+1")]
    [InlineData("Super+Alt+1")]
    public void Win_Aliases(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out _));
        Assert.Equal(MOD_WIN | MOD_ALT, mods);
    }

    [Fact(DisplayName = "Shift single modifier + letter")]
    public void Shift_Letter()
    {
        Assert.True(HotkeyGesture.TryParse("Shift+Q", out uint mods, out uint vk));
        Assert.Equal(MOD_SHIFT, mods);
        Assert.Equal((uint)'Q', vk);
    }

    [Fact(DisplayName = "All four modifiers + main key")]
    public void AllFourModifiers()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+Win+K", out uint mods, out uint vk));
        Assert.Equal(MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN, mods);
        Assert.Equal((uint)'K', vk);
    }

    [Fact(DisplayName = "Modifier order doesn't matter")]
    public void ModifierOrder_Independent()
    {
        Assert.True(HotkeyGesture.TryParse("Alt+Ctrl+B", out uint m1, out uint vk1));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+B", out uint m2, out uint vk2));
        Assert.Equal(m2, m1);
        Assert.Equal(vk2, vk1);
    }

    // ---- Function keys ----

    [Theory(DisplayName = "Function keys F1..F12 → VK_F1(0x70)..")]
    [InlineData("Ctrl+F1", 0x70)]
    [InlineData("Ctrl+F2", 0x71)]
    [InlineData("Ctrl+F12", 0x7B)]
    [InlineData("Ctrl+f5", 0x74)]
    public void FunctionKeys(string gesture, int expectedVk)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out uint vk));
        Assert.Equal((uint)expectedVk, vk);
    }

    [Fact(DisplayName = "F24 still valid, F25 out of range invalid")]
    public void FunctionKey_Bounds()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+F24", out _, out uint vk));
        Assert.Equal((uint)(0x70 + 23), vk);
        Assert.False(HotkeyGesture.TryParse("Ctrl+F25", out _, out _));
        Assert.False(HotkeyGesture.TryParse("Ctrl+F0", out _, out _));
    }

    // ---- Invalid input ----

    [Theory(DisplayName = "Empty / whitespace → invalid")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespace_Invalid(string? gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    [Theory(DisplayName = "Bare main key without a modifier → invalid (easy to trigger by accident)")]
    [InlineData("B")]
    [InlineData("F1")]
    [InlineData("1")]
    public void NoModifier_Invalid(string gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    [Fact(DisplayName = "Modifiers only, no main key → invalid")]
    public void ModifiersOnly_Invalid()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+Alt", out _, out _));
        Assert.False(HotkeyGesture.TryParse("Win", out _, out _));
    }

    [Fact(DisplayName = "Two main keys → invalid")]
    public void TwoMainKeys_Invalid()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+A+B", out _, out _));
        Assert.False(HotkeyGesture.TryParse("Ctrl+1+2", out _, out _));
    }

    [Theory(DisplayName = "Unknown token → invalid")]
    [InlineData("Ctrl+Alt+Foobar")]
    [InlineData("Ctrl+Alt+#")]
    [InlineData("Ctrl+Alt+F99")]
    [InlineData("Hyper+B")]
    public void UnknownToken_Invalid(string gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    // ---- Full coverage of digit / letter keys ----

    [Theory(DisplayName = "Digits 0-9 VK matches ASCII")]
    [InlineData("Win+Alt+0", '0')]
    [InlineData("Win+Alt+5", '5')]
    [InlineData("Win+Alt+9", '9')]
    public void Digits(string gesture, char expected)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out uint vk));
        Assert.Equal((uint)expected, vk);
    }

    [Theory(DisplayName = "Letter A/Z boundary VK matches uppercase ASCII")]
    [InlineData("Ctrl+A", 'A')]
    [InlineData("Ctrl+Z", 'Z')]
    [InlineData("Ctrl+a", 'A')]
    public void Letters(string gesture, char expected)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out uint vk));
        Assert.Equal((uint)expected, vk);
    }

    // ---- Format round-trip ----

    [Theory(DisplayName = "Format(TryParse(g)) canonical round-trip is consistent")]
    [InlineData("Ctrl+Alt+B")]
    [InlineData("Ctrl+Alt+F")]
    [InlineData("Win+Alt+1")]
    [InlineData("Ctrl+Alt+Shift+Win+K")]
    [InlineData("Ctrl+F5")]
    public void Format_RoundTrip(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out uint vk));
        string formatted = HotkeyGesture.Format(mods, vk);
        // Re-parsing formatted should give the same (mods, vk)
        Assert.True(HotkeyGesture.TryParse(formatted, out uint mods2, out uint vk2));
        Assert.Equal(mods, mods2);
        Assert.Equal(vk, vk2);
    }

    [Fact(DisplayName = "Format uses a fixed modifier order Ctrl+Alt+Shift+Win")]
    public void Format_FixedOrder()
    {
        HotkeyGesture.TryParse("Win+Shift+Alt+Ctrl+B", out uint mods, out uint vk);
        Assert.Equal("Ctrl+Alt+Shift+Win+B", HotkeyGesture.Format(mods, vk));
    }
}
