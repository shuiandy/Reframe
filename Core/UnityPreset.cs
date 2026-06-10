using Microsoft.Win32;

namespace Reframe.Core;

/// <summary>
/// Unity 启动分辨率预设的注册表写入(见 DESIGN 研究结论 / 原神拉伸根治)。
///
/// Unity(如原神 Unity 2017.4)把渲染缓冲分辨率钉死在
/// <c>HKCU\Software\miHoYo\原神</c> 下的三个 DWORD:
/// <list type="bullet">
///   <item><c>Screenmanager Resolution Width_h&lt;hash&gt;</c></item>
///   <item><c>Screenmanager Resolution Height_h&lt;hash&gt;</c></item>
///   <item><c>Screenmanager Is Fullscreen mode_h&lt;hash&gt;</c></item>
/// </list>
/// hash 后缀因游戏而异,所以按**前缀**匹配值名(可能命中多个,全部写)。
/// 游戏启动时读注册表渲染,外部 resize 只会整张缩放(拉伸),且游戏退出时会把当前值写回——
/// 所以只在游戏**未运行**时写才有意义。本类只写 Unity 标准键、不创建不存在的值,安全。
/// </summary>
public static class UnityPreset
{
    /// <summary>这些值名前缀对应的目标 DWORD。</summary>
    public const string WidthPrefix = "Screenmanager Resolution Width";
    public const string HeightPrefix = "Screenmanager Resolution Height";
    public const string FullscreenPrefix = "Screenmanager Is Fullscreen mode";

    /// <summary>
    /// 把目标分辨率写入注册表(纯 I/O)。返回是否实际写入了至少一个值。
    /// 键或全部三类值都不存在 → 不创建、返回 false(用户没装该游戏或路径写错,别污染注册表)。
    /// </summary>
    /// <param name="registryPath">HKCU 下的相对路径,如 <c>Software\miHoYo\原神</c>。</param>
    public static bool Write(string registryPath, int width, int height, bool windowed)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return false;

        // 不创建键:writable 打开一个已存在的键;不存在则返回 null。
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
        if (key is null) return false;

        var names = key.GetValueNames();
        int fullscreenValue = windowed ? 0 : 1;

        bool wrote = false;
        foreach (var name in names)
        {
            if (Matches(name, WidthPrefix))
            {
                key.SetValue(name, width, RegistryValueKind.DWord);
                wrote = true;
            }
            else if (Matches(name, HeightPrefix))
            {
                key.SetValue(name, height, RegistryValueKind.DWord);
                wrote = true;
            }
            else if (Matches(name, FullscreenPrefix))
            {
                key.SetValue(name, fullscreenValue, RegistryValueKind.DWord);
                wrote = true;
            }
        }

        return wrote;
    }

    /// <summary>
    /// 读注册表当前值,判断是否已等于目标(全部命中的宽/高/全屏值都一致)。
    /// 返回 true 表示无需再写;键不存在或缺值则返回 false(交给 Write 决定能不能写)。
    /// 用于 tick 纠正时的廉价比较,避免重复写。
    /// </summary>
    public static bool AlreadyMatches(string registryPath, int width, int height, bool windowed)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return false;

        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false);
        if (key is null) return false;

        int fullscreenValue = windowed ? 0 : 1;
        bool sawWidth = false, sawHeight = false, sawFullscreen = false;

        foreach (var name in key.GetValueNames())
        {
            if (Matches(name, WidthPrefix))
            {
                sawWidth = true;
                if (ReadDword(key, name) != width) return false;
            }
            else if (Matches(name, HeightPrefix))
            {
                sawHeight = true;
                if (ReadDword(key, name) != height) return false;
            }
            else if (Matches(name, FullscreenPrefix))
            {
                sawFullscreen = true;
                if (ReadDword(key, name) != fullscreenValue) return false;
            }
        }

        // 必须三类都见到且都一致,才算"已匹配"。缺任意一类 → 让 Write 去补。
        return sawWidth && sawHeight && sawFullscreen;
    }

    /// <summary>值名是否以给定前缀开头(hash 后缀未知,前缀匹配是关键)。纯函数,单测靶点。</summary>
    public static bool Matches(string valueName, string prefix)
        => valueName.StartsWith(prefix, StringComparison.Ordinal);

    private static int? ReadDword(RegistryKey key, string name)
    {
        var v = key.GetValue(name);
        return v is int i ? i : (int?)null;
    }
}
