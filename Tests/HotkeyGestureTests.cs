using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// 热键手势纯解析:文本 → (修饰符位掩码, 虚拟键)。覆盖默认手势、大小写宽容、
/// 各类无效输入、以及与 Format 的往返。
/// </summary>
public class HotkeyGestureTests
{
    // user32 RegisterHotKey fsModifiers(与 HotkeyGesture 常量同值)。
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // ---- 默认手势全部可解析 ----

    [Theory(DisplayName = "默认手势均可解析")]
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

    [Fact(DisplayName = "解析结果不含 NOREPEAT(由注册时附加)")]
    public void Parse_DoesNotIncludeNoRepeat()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+B", out uint mods, out _));
        Assert.Equal(0u, mods & 0x4000); // MOD_NOREPEAT
    }

    // ---- 大小写宽容 + 修饰符别名 ----

    [Theory(DisplayName = "大小写宽容:ctrl/CTRL/Ctrl 等价")]
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

    [Theory(DisplayName = "Control 是 Ctrl 的别名")]
    [InlineData("Control+Alt+B")]
    [InlineData("control+alt+b")]
    public void Control_Alias(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out _));
        Assert.Equal(MOD_CONTROL | MOD_ALT, mods);
    }

    [Theory(DisplayName = "Win 的别名:Windows/Meta/Super")]
    [InlineData("Windows+Alt+1")]
    [InlineData("Meta+Alt+1")]
    [InlineData("Super+Alt+1")]
    public void Win_Aliases(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out _));
        Assert.Equal(MOD_WIN | MOD_ALT, mods);
    }

    [Fact(DisplayName = "Shift 单修饰符 + 字母")]
    public void Shift_Letter()
    {
        Assert.True(HotkeyGesture.TryParse("Shift+Q", out uint mods, out uint vk));
        Assert.Equal(MOD_SHIFT, mods);
        Assert.Equal((uint)'Q', vk);
    }

    [Fact(DisplayName = "全部四个修饰符 + 主键")]
    public void AllFourModifiers()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+Win+K", out uint mods, out uint vk));
        Assert.Equal(MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN, mods);
        Assert.Equal((uint)'K', vk);
    }

    [Fact(DisplayName = "修饰符顺序无关")]
    public void ModifierOrder_Independent()
    {
        Assert.True(HotkeyGesture.TryParse("Alt+Ctrl+B", out uint m1, out uint vk1));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+B", out uint m2, out uint vk2));
        Assert.Equal(m2, m1);
        Assert.Equal(vk2, vk1);
    }

    // ---- 功能键 ----

    [Theory(DisplayName = "功能键 F1..F12 → VK_F1(0x70)..")]
    [InlineData("Ctrl+F1", 0x70)]
    [InlineData("Ctrl+F2", 0x71)]
    [InlineData("Ctrl+F12", 0x7B)]
    [InlineData("Ctrl+f5", 0x74)]
    public void FunctionKeys(string gesture, int expectedVk)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out uint vk));
        Assert.Equal((uint)expectedVk, vk);
    }

    [Fact(DisplayName = "F24 仍合法,F25 越界无效")]
    public void FunctionKey_Bounds()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+F24", out _, out uint vk));
        Assert.Equal((uint)(0x70 + 23), vk);
        Assert.False(HotkeyGesture.TryParse("Ctrl+F25", out _, out _));
        Assert.False(HotkeyGesture.TryParse("Ctrl+F0", out _, out _));
    }

    // ---- 无效输入 ----

    [Theory(DisplayName = "空 / 空白 → 无效")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespace_Invalid(string? gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    [Theory(DisplayName = "无修饰符的裸主键 → 无效(易误触)")]
    [InlineData("B")]
    [InlineData("F1")]
    [InlineData("1")]
    public void NoModifier_Invalid(string gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    [Fact(DisplayName = "只有修饰符没有主键 → 无效")]
    public void ModifiersOnly_Invalid()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+Alt", out _, out _));
        Assert.False(HotkeyGesture.TryParse("Win", out _, out _));
    }

    [Fact(DisplayName = "两个主键 → 无效")]
    public void TwoMainKeys_Invalid()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+A+B", out _, out _));
        Assert.False(HotkeyGesture.TryParse("Ctrl+1+2", out _, out _));
    }

    [Theory(DisplayName = "未知 token → 无效")]
    [InlineData("Ctrl+Alt+Foobar")]
    [InlineData("Ctrl+Alt+#")]
    [InlineData("Ctrl+Alt+F99")]
    [InlineData("Hyper+B")]
    public void UnknownToken_Invalid(string gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    // ---- 数字键 / 字母键 全覆盖 ----

    [Theory(DisplayName = "数字 0-9 VK 与 ASCII 一致")]
    [InlineData("Win+Alt+0", '0')]
    [InlineData("Win+Alt+5", '5')]
    [InlineData("Win+Alt+9", '9')]
    public void Digits(string gesture, char expected)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out uint vk));
        Assert.Equal((uint)expected, vk);
    }

    [Theory(DisplayName = "字母 A/Z 边界 VK 与大写 ASCII 一致")]
    [InlineData("Ctrl+A", 'A')]
    [InlineData("Ctrl+Z", 'Z')]
    [InlineData("Ctrl+a", 'A')]
    public void Letters(string gesture, char expected)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out _, out uint vk));
        Assert.Equal((uint)expected, vk);
    }

    // ---- Format 往返 ----

    [Theory(DisplayName = "Format(TryParse(g)) 规范化往返一致")]
    [InlineData("Ctrl+Alt+B")]
    [InlineData("Ctrl+Alt+F")]
    [InlineData("Win+Alt+1")]
    [InlineData("Ctrl+Alt+Shift+Win+K")]
    [InlineData("Ctrl+F5")]
    public void Format_RoundTrip(string gesture)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out uint mods, out uint vk));
        string formatted = HotkeyGesture.Format(mods, vk);
        // 再解析 formatted 应得到同样的 (mods, vk)
        Assert.True(HotkeyGesture.TryParse(formatted, out uint mods2, out uint vk2));
        Assert.Equal(mods, mods2);
        Assert.Equal(vk, vk2);
    }

    [Fact(DisplayName = "Format 固定修饰符顺序 Ctrl+Alt+Shift+Win")]
    public void Format_FixedOrder()
    {
        HotkeyGesture.TryParse("Win+Shift+Alt+Ctrl+B", out uint mods, out uint vk);
        Assert.Equal("Ctrl+Alt+Shift+Win+B", HotkeyGesture.Format(mods, vk));
    }
}
