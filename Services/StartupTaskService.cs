using System.Diagnostics;

namespace Reframe.Services;

/// <summary>
/// 开机自启:用 Windows 计划任务(schtasks)实现。
/// 程序是 requireAdministrator,普通 Run 注册表自启会触发 UAC;
/// 计划任务 /RL HIGHEST + /SC ONLOGON 可在登录时以最高权限静默拉起,免 UAC。
/// 所有调用静默运行,异常不外抛,以 bool 表示成败。
/// </summary>
public static class StartupTaskService
{
    private const string TaskName = "Reframe";

    /// <summary>计划任务是否已存在。</summary>
    public static bool IsEnabled()
    {
        // /Query 命中返回 0,不存在返回非 0。
        return Run($"/Query /TN \"{TaskName}\"") == 0;
    }

    /// <summary>创建/覆盖计划任务,指向当前 exe。</summary>
    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        // /F 覆盖同名任务;/TR 路径含空格须再包一层引号。
        string args = $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\" /F";
        return Run(args) == 0;
    }

    /// <summary>删除计划任务。</summary>
    public static bool Disable()
    {
        return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
    }

    /// <summary>静默运行 schtasks,返回退出码;启动失败返回 -1。</summary>
    private static int Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
