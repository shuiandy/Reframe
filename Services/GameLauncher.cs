using System.Diagnostics;
using Reframe.Core;

namespace Reframe.Services;

/// <summary>
/// 迷你游戏库:从配置一键启动游戏。
///
/// 时序闭环(先预设、后启动):若 Profile 启用了启动分辨率预设(<see cref="UnityResolutionPreset"/>),
/// 且目标进程当前**没在跑**,先调 <see cref="UnityPreset.Write"/> 把 Screenmanager 注册表写成目标分辨率,
/// 再启动游戏——这样游戏启动时读到的就是目标分辨率(原神等渲染分辨率钉死在注册表的 Unity 游戏)。
/// 进程已在跑就不写预设(游戏退出时会把当前值写回,中途写无意义),也不重复启动。
///
/// 启动目标:<see cref="Profile.LaunchCommand"/> 非空优先用它,否则用 <see cref="Profile.ExePath"/>;两者皆空 → 失败。
/// 用 ShellExecute 启动:本地 exe 设工作目录为其所在目录;URI / 启动器命令(如 hoyoplay://)交给 shell,不设工作目录。
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// 启动 Profile 对应的游戏。成功返回 true;失败返回 false 并通过 <paramref name="error"/> 给出人话原因
    /// (没配启动方式 / 文件不存在 / 已在运行 / 启动异常)。须在 UI 线程调用(调用方据 error 弹提示)。
    /// </summary>
    public static bool Launch(Profile p, out string? error)
    {
        error = null;

        // 启动目标:LaunchCommand 非空优先,否则 ExePath;都空 → 没配启动方式。
        string? target = FirstNonEmpty(p.LaunchCommand, p.ExePath);
        if (target is null)
        {
            error = "没有配置启动方式:请填写“启动命令”或“可执行文件”。";
            return false;
        }

        // 已在运行(仅进程匹配可靠判定)→ 不重复启动。
        if (p.MatchKind == MatchKind.Process && IsProcessRunning(p.MatchValue))
        {
            error = $"“{NameOf(p)}”已在运行。";
            return false;
        }

        // 安全白名单(本进程为 requireAdministrator,绝不对任意串 ShellExecute):
        //   放行① 现存的 .exe 文件路径;放行② 白名单协议前缀(steam:// 等);其余一律拒。
        bool isExe = IsExistingExe(target);
        bool isAllowedUri = !isExe && IsAllowedProtocol(target);

        if (!isExe && !isAllowedUri)
        {
            // 既不是现存 exe、也不是白名单协议:可能是写错的路径,或不被允许的命令。
            error = LooksLikeUri(target)
                ? "不支持的启动命令:仅允许 steam:// hoyoplay:// http(s):// 等已知启动器协议。"
                : $"找不到文件:{target}";
            return false;
        }

        bool isUri = isAllowedUri;

        // 时序闭环:先写分辨率预设(进程未在跑时才写),后启动。
        // 注:能走到这里说明上面"已在运行"判定未命中(进程不在跑),故此处直接写。
        var preset = p.ResolutionPreset;
        if (preset is { Enabled: true } && !string.IsNullOrWhiteSpace(preset.RegistryPath))
        {
            try { UnityPreset.Write(preset.RegistryPath, preset.Width, preset.Height, preset.Windowed); }
            catch { /* 预设写入失败不阻断启动:大不了游戏按自身记忆分辨率渲染 */ }
        }

        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            // 本地 exe 设工作目录为其所在目录(部分游戏依赖 CWD 找资源);URI 不设。
            if (!isUri)
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = "启动失败:" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 允许的启动协议前缀白名单(admin 进程下只放行已知游戏启动器/网页协议)。
    /// 命中按 URI 交给 shell;命中之外的协议(file:// shell: cmd 管道等)一律拒。
    /// </summary>
    private static readonly string[] AllowedSchemes =
    {
        "steam://",        // Steam
        "hoyoplay://",     // 米哈游 HoYoPlay
        "com.epicgames.launcher://", // Epic
        "uplay://", "ubisoft://",    // Ubisoft Connect
        "origin://", "ea://", "link2ea://", // EA / Origin
        "battlenet://", "blizzard://",      // Battle.net
        "goggalaxy://",    // GOG Galaxy
        "http://", "https://", // 网页启动页
    };

    /// <summary>target 是否为一个现存的 .exe 文件(放行①)。</summary>
    private static bool IsExistingExe(string target)
    {
        try
        {
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target);
        }
        catch { return false; } // 非法路径字符等
    }

    /// <summary>target 是否以白名单协议前缀打头(放行②,大小写不敏感)。</summary>
    private static bool IsAllowedProtocol(string target)
    {
        foreach (var s in AllowedSchemes)
            if (target.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>仅用于报错措辞:看起来像个协议 URI(含 "://"),用来区分"路径写错"与"协议不被允许"。</summary>
    private static bool LooksLikeUri(string target)
        => target.Contains("://", StringComparison.Ordinal);

    /// <summary>按进程名(忽略 .exe、大小写)判断是否有该进程在运行。与 Watcher 的判定一致。</summary>
    private static bool IsProcessRunning(string matchValue)
    {
        if (string.IsNullOrWhiteSpace(matchValue)) return false;
        string name = matchValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? matchValue[..^4]
            : matchValue;
        try
        {
            var procs = Process.GetProcessesByName(name);
            try { return procs.Length > 0; }
            finally { foreach (var pr in procs) pr.Dispose(); }
        }
        catch { return false; }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    private static string NameOf(Profile p)
        => string.IsNullOrWhiteSpace(p.Name) ? "该配置" : p.Name;
}
